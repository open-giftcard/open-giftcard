using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace GiftCardPlatform.Api.Authentication;

internal static class AuthenticationRegistration
{
    /// <summary>
    /// Registers signed JWT bearer authentication in every environment.
    /// Platform authority and organization membership are both resolved from
    /// PostgreSQL after the token signature and lifetime are validated.
    /// </summary>
    public static IServiceCollection AddPlatformAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var tokenOptions = ResolveTokenOptions(configuration);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = tokenOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = tokenOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(tokenOptions.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = PopulateExecutionContextAsync,
                };
            });

        services.AddAuthorization();
        return services;
    }

    private static async Task PopulateExecutionContextAsync(TokenValidatedContext context)
    {
        // A POS token is a device principal and is checked first: it carries no
        // user subject, so it must never fall through to the user path (ADR-043).
        if (await TryPopulatePosPrincipalAsync(context).ConfigureAwait(false))
        {
            return;
        }

        // A partner token is a machine principal for the same reason: no user
        // subject, so it must not reach the user path either (ADR-053).
        if (await TryPopulatePartnerPrincipalAsync(context).ConfigureAwait(false))
        {
            return;
        }

        var rawUserId = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(rawUserId, out var userId) || userId == Guid.Empty)
        {
            context.Fail("The access token has no valid subject.");
            return;
        }

        var executionContext =
            context.HttpContext.RequestServices.GetRequiredService<MutableExecutionContext>();
        var rawOrganizationId =
            context.Request.Headers[BearerAuthentication.OrganizationIdHeader].ToString();


        if (string.IsNullOrWhiteSpace(rawOrganizationId))
        {
            var platformPermissionResolver =
                context.HttpContext.RequestServices.GetRequiredService<IPlatformPermissionResolver>();
            var platformPermissions = await platformPermissionResolver
                .GetEffectivePermissionsAsync(
                    userId,
                    context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (platformPermissions.Count > 0)
            {
                executionContext.SetPlatformOperator(userId, platformPermissions);
            }
            else
            {
                executionContext.SetIdentityUser(userId);
            }

            return;
        }

        if (!Guid.TryParse(rawOrganizationId, out var organizationId) ||
            organizationId == Guid.Empty)
        {
            context.Fail(
                $"{BearerAuthentication.OrganizationIdHeader} must be a non-empty UUID.");
            return;
        }

        executionContext.SetOrganizationCandidate(userId, organizationId);
        var resolver =
            context.HttpContext.RequestServices.GetRequiredService<IActiveMembershipResolver>();
        var membership = await resolver
            .ResolveActiveMembershipAsync(
                userId,
                organizationId,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (membership is null)
        {
            executionContext.SetAnonymous();
            context.Fail("An active organization membership is required.");
            return;
        }

        executionContext.SetOrganizationMember(
            userId,
            membership.MembershipId,
            organizationId,
            membership.TenantRootOrganizationId);
    }

    /// <summary>
    /// Populates a point-of-sale device principal when the token carries the POS
    /// marker claim. A POS token holds no user, organization, or tenant scope, so
    /// tenant RLS fails closed for it and it cannot reach cardholder or
    /// organization endpoints. Client and terminal status are re-resolved on
    /// every request, making either retirement immediate. An organization header
    /// presented alongside one is refused rather than ignored, so a till cannot
    /// appear to select a customer.
    /// </summary>
    private static async Task<bool> TryPopulatePosPrincipalAsync(
        TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal?.FindFirstValue(PosTokenClaims.Principal) is not PosTokenClaims.PrincipalValue)
        {
            return false;
        }

        if (!Guid.TryParse(
                principal.FindFirstValue(PosTokenClaims.ClientId),
                out var posClientId) ||
            !Guid.TryParse(
                principal.FindFirstValue(PosTokenClaims.TerminalId),
                out var posTerminalId) ||
            posClientId == Guid.Empty || posTerminalId == Guid.Empty)
        {
            context.Fail("The POS access token is missing a client or terminal identity.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(
                context.Request.Headers[BearerAuthentication.OrganizationIdHeader].ToString()))
        {
            context.Fail("A POS access token cannot select an organization context.");
            return true;
        }

        var resolver =
            context.HttpContext.RequestServices.GetRequiredService<IPosPrincipalResolver>();
        var pos = await resolver
            .ResolveAsync(
                posClientId,
                posTerminalId,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        var executionContext =
            context.HttpContext.RequestServices.GetRequiredService<MutableExecutionContext>();
        if (pos is null)
        {
            executionContext.SetAnonymous();
            context.Fail("An active POS client and terminal are required.");
            return true;
        }

        executionContext.SetPosClient(pos.PosClientId, pos.PosTerminalId);
        return true;
    }

    /// <summary>
    /// Populates an e-pin reseller principal when the token carries the partner
    /// marker claim (ADR-053).
    ///
    /// The token carries identity only. The partner, its status, and its funding
    /// organization are re-resolved from the database on every request, so
    /// disabling a key or a partner takes effect on the very next call rather
    /// than when the token happens to expire, and the funding tenant is always
    /// verified server state rather than anything the caller supplied.
    ///
    /// An organization header presented alongside one is refused rather than
    /// ignored, so a reseller cannot appear to select a different customer's
    /// money.
    /// </summary>
    private static async Task<bool> TryPopulatePartnerPrincipalAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal?.FindFirstValue(PartnerTokenClaims.Principal)
            is not PartnerTokenClaims.PrincipalValue)
        {
            return false;
        }

        if (!Guid.TryParse(
                principal.FindFirstValue(PartnerTokenClaims.ClientId),
                out var partnerClientId) ||
            partnerClientId == Guid.Empty)
        {
            context.Fail("The partner access token is missing a client identity.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(
                context.Request.Headers[BearerAuthentication.OrganizationIdHeader].ToString()))
        {
            context.Fail("A partner access token cannot select an organization context.");
            return true;
        }

        var resolver =
            context.HttpContext.RequestServices.GetRequiredService<IPartnerPrincipalResolver>();
        var partner = await resolver
            .ResolveAsync(partnerClientId, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        var executionContext =
            context.HttpContext.RequestServices.GetRequiredService<MutableExecutionContext>();
        if (partner is null)
        {
            executionContext.SetAnonymous();
            context.Fail("An active partner API client is required.");
            return true;
        }

        // A partner is a root organization, so the acting and tenant-root scopes
        // are the same value; there is no subsidiary for a reseller to act in.
        executionContext.SetPartnerClient(
            partner.PartnerClientId,
            partner.PartnerId,
            partner.RootOrganizationId,
            partner.RootOrganizationId,
            partner.Scopes);
        return true;
    }

    private static IdentityTokenOptions ResolveTokenOptions(IConfiguration configuration)
    {
        var options = new IdentityTokenOptions();
        configuration.GetSection(IdentityTokenOptions.SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.Issuer) ||
            string.IsNullOrWhiteSpace(options.Audience) ||
            Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                $"{IdentityTokenOptions.SectionName} must configure a non-empty issuer and audience " +
                "and a signing key of at least 32 bytes.");
        }

        if (options.AccessTokenMinutes != 15 || options.RefreshTokenDays != 30)
        {
            throw new InvalidOperationException(
                $"{IdentityTokenOptions.SectionName} access and refresh lifetimes are fixed at " +
                "15 minutes and 30 days by ADR-028.");
        }

        return options;
    }
}

internal static class BearerAuthentication
{
    public const string OrganizationIdHeader = "X-Organization-Id";
}
