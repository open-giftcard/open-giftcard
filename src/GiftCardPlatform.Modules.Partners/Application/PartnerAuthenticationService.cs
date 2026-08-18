using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Partners.Contracts;
using GiftCardPlatform.Modules.Partners.Domain;
using GiftCardPlatform.Modules.Partners.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace GiftCardPlatform.Modules.Partners.Application;

internal sealed class PartnerAuthenticationService(
    PartnersDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    TimeProvider timeProvider,
    IOptions<PartnersOptions> partnerOptions,
    IOptions<PartnerTokenSigningOptions> signingOptions,
    IPartnerCredentialThrottle throttle)
    : IPartnerAuthenticationService, IPartnerPrincipalResolver
{
    private readonly PartnersOptions settings = partnerOptions.Value;
    private readonly PartnerTokenSigningOptions signing = signingOptions.Value;

    public async Task<PartnerAccessTokenResult> AuthenticateAsync(
        PartnerAccessTokenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalization must not leak which field was wrong, so malformed input
        // is refused with the same error as a wrong secret.
        string clientCode;
        try
        {
            clientCode = PartnerApiClient.NormalizeCode(request.ClientCode);
        }
        catch (ValidationFailedException)
        {
            throw InvalidCredentials();
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnableCredentialLookupAsync(transaction, cancellationToken).ConfigureAwait(false);

        // The query filters are skipped deliberately. They express the ordinary
        // tenant rule, which by definition cannot hold before a caller is
        // authenticated. RLS remains the authoritative barrier and still applies:
        // this read succeeds only because EnableCredentialLookupAsync opened the
        // read-only escape above, and it stays refused for writes.
        var client = await dbContext.ApiClients
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Code == clientCode, cancellationToken)
            .ConfigureAwait(false);
        var partner = client is null
            ? null
            : await dbContext.Partners
                .AsNoTracking()
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(item => item.Id == client.PartnerId, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The secret is verified even when the client is unknown, so an attacker
        // cannot distinguish "no such client" from "wrong secret" by response
        // timing. Unknown, disabled partner, disabled client, and wrong secret
        // all return the same refusal.
        var secretMatches = PartnerCredentialCodec.Matches(
            client?.SecretHash ?? new string('0', PartnerCredentialCodec.HashHexLength),
            request.ClientSecret);

        // Throttled clients are refused even when the secret is right: that is
        // what makes guessing expensive. The refusal is the same one every other
        // failure produces, so the response never reveals that a code is real,
        // let alone that it is currently under attack.
        var throttled = client is not null && throttle.IsThrottled(client.Id);
        if (client is null || partner is null || !secretMatches || throttled ||
            !client.IsUsable || !partner.IsUsable)
        {
            // Only resolved clients are counted, so an unknown code cannot grow
            // the table, and a disabled one cannot be used to burn a live
            // client's budget.
            if (client is not null && !secretMatches)
            {
                throttle.RecordFailure(client.Id);
            }

            throw InvalidCredentials();
        }

        throttle.RecordSuccess(client.Id);

        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signing.SigningKey));

        // Identity only. The funding organization is deliberately absent: it is
        // resolved server-side on every request, so a token cannot outlive a
        // partner's tenant anchoring or survive the kill switch.
        var token = new JwtSecurityToken(
            issuer: signing.Issuer,
            audience: signing.Audience,
            claims:
            [
                new Claim(PartnerTokenClaims.Principal, PartnerTokenClaims.PrincipalValue),
                new Claim(PartnerTokenClaims.ClientId, client.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7(now).ToString()),
            ],
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new PartnerAccessTokenResult(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            partner.Id,
            client.Id);
    }

    public async Task<PartnerPrincipal?> ResolveAsync(
        Guid partnerClientId,
        CancellationToken cancellationToken)
    {
        if (partnerClientId == Guid.Empty)
        {
            return null;
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await EnableCredentialLookupAsync(transaction, cancellationToken).ConfigureAwait(false);

        // Same reasoning as the exchange: the principal is being established, so
        // there is no tenant context yet for a query filter to match against.
        var client = await dbContext.ApiClients
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(item => item.Id == partnerClientId, cancellationToken)
            .ConfigureAwait(false);
        var partner = client is null
            ? null
            : await dbContext.Partners
                .AsNoTracking()
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(item => item.Id == client.PartnerId, cancellationToken)
                .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (client is null || partner is null || !client.IsUsable || !partner.IsUsable)
        {
            return null;
        }

        return new PartnerPrincipal(
            client.Id,
            partner.Id,
            client.RootOrganizationId,
            client.Scopes);
    }

    /// <summary>
    /// Opens the read-only RLS escape for this transaction only.
    ///
    /// Resolving a credential happens before any caller is authenticated, so
    /// there is no tenant context for the ordinary policy to match. The escape
    /// appears in the policy's <c>using</c> clause and deliberately not in
    /// <c>with check</c>, so this path can read a partner and a key and can
    /// never create or modify either. Same device as
    /// <c>app.is_initial_admin_bootstrap</c>, and equally transaction-local.
    /// </summary>
    private static async Task EnableCredentialLookupAsync(
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('app.is_partner_credential_lookup', 'true', true)",
            transaction.Transaction.Connection,
            transaction.Transaction);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UnauthorizedException InvalidCredentials() =>
        new("partner.credentials.invalid", "The partner credentials are not valid.");
}

/// <summary>
/// The JWT signing material shared with user authentication. Partners binds the
/// same configuration section rather than depending on the Identity module, so
/// no module boundary is crossed to sign a machine token (ADR-004), exactly as
/// the POS token options already do.
///
/// The issuer and audience defaults must match the ones user authentication
/// falls back to. Neither is set in configuration today, so leaving these empty
/// mints a token the API then rejects as having no audience.
/// </summary>
internal sealed class PartnerTokenSigningOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; set; } = "GiftCardPlatform";

    public string Audience { get; set; } = "GiftCardPlatform.Api";

    public string SigningKey { get; set; } = string.Empty;
}
