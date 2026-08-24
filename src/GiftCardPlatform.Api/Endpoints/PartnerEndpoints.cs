using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

internal static class PartnerEndpoints
{
    /// <summary>
    /// Throttles the anonymous credential exchange. Unlike the POS token
    /// endpoint, which has no limit, this one is reachable by anyone who learns
    /// a client code, and a partner secret is a minting credential.
    /// </summary>
    public const string AuthRateLimitPolicy = "partner-auth";

    public static IEndpointRouteBuilder MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost($"{ApiRoutes.V1}/partners", RegisterPartnerAsync)
            .WithTags("Partners")
            .WithName("RegisterPartner")
            .WithSummary("Registers an e-pin reseller against a funding root organization.")
            .WithDescription(
                "The organization must be an active root. Its prepaid corporate credit funds " +
                "every card the partner mints, which is the ceiling on what a compromised " +
                "credential can ever produce.")
            .RequireAuthorization()
            .Produces<PartnerResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/partners", GetPartnersAsync)
            .WithTags("Partners")
            .WithName("GetPartners")
            .WithSummary("Lists registered e-pin resellers.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PartnerResult>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost($"{ApiRoutes.V1}/partners/{{partnerId:guid}}/clients", RegisterClientAsync)
            .WithTags("Partners")
            .WithName("RegisterPartnerApiClient")
            .WithSummary("Registers an API client belonging to a partner.")
            .WithDescription(
                "Returns the client secret once. Only its hash is stored, so the secret cannot " +
                "be recovered afterwards and a lost one is replaced by registering another " +
                "client and disabling the old one.")
            .RequireAuthorization()
            .Produces<RegisteredPartnerApiClientResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/partners/{{partnerId:guid}}/clients", GetClientsAsync)
            .WithTags("Partners")
            .WithName("GetPartnerApiClients")
            .WithSummary("Lists a partner's API clients. Secrets are never returned.")
            .RequireAuthorization()
            .Produces<IReadOnlyList<PartnerApiClientResult>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost(
                $"{ApiRoutes.V1}/partners/{{partnerId:guid}}/clients/{{clientId:guid}}/disable",
                DisableClientAsync)
            .WithTags("Partners")
            .WithName("DisablePartnerApiClient")
            .WithSummary("Disables one API client. Takes effect on the next request.")
            .WithDescription(
                "The usual response to a suspected leak: the reseller keeps trading on its " +
                "other keys. E-pins already sold stay claimable, because they belong to the " +
                "reseller's buyers; voiding those is a separate clawback action.")
            .RequireAuthorization()
            .Produces<PartnerApiClientResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost($"{ApiRoutes.V1}/partners/{{partnerId:guid}}/disable", DisablePartnerAsync)
            .WithTags("Partners")
            .WithName("DisablePartner")
            .WithSummary("Disables a whole reseller. Takes effect on the next request.")
            .RequireAuthorization()
            .Produces<PartnerResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost($"{ApiRoutes.V1}/partners/auth/token", AuthenticateAsync)
            .WithTags("Partners")
            .WithName("IssuePartnerAccessToken")
            .WithSummary("Exchanges partner API client credentials for a short-lived access token.")
            .WithDescription(
                "Unknown clients, disabled clients, disabled partners, and wrong secrets are " +
                "refused identically and in constant time. The token identifies the client " +
                "only; the funding organization is resolved server-side on every request, so " +
                "disabling a client or partner takes effect immediately rather than at expiry.")
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimitPolicy)
            .Produces<PartnerAccessTokenResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost($"{ApiRoutes.V1}/partners/gift-cards/mint", MintGiftCardAsync)
            .WithTags("Partners")
            .WithName("MintPartnerGiftCard")
            .WithSummary("Mints a gift card against the reseller's prepaid float.")
            .WithDescription(
                "The funding and issuing organization come from the authenticated partner " +
                "principal and cannot be supplied by the caller. The response returns the " +
                "buyer claim URL and PIN and must never be cached or logged.")
            .RequireAuthorization()
            .Produces<MintedPartnerEpinApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    private static async Task<IResult> RegisterPartnerAsync(
        [FromBody] RegisterPartnerRequest request,
        IPartnerRegistrationService service,
        CancellationToken cancellationToken)
    {
        var created = await service.RegisterAsync(request, cancellationToken);
        return Results.Created($"{ApiRoutes.V1}/partners/{created.Id}", created);
    }

    private static async Task<IResult> GetPartnersAsync(
        IPartnerRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetPartnersAsync(cancellationToken));

    private static async Task<IResult> RegisterClientAsync(
        Guid partnerId,
        [FromBody] RegisterPartnerApiClientRequest request,
        IPartnerRegistrationService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // The one-time secret must not be cached by any intermediary.
        response.Headers.CacheControl = "no-store";
        var created = await service.RegisterClientAsync(partnerId, request, cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/partners/{partnerId}/clients/{created.Client.Id}",
            created);
    }

    private static async Task<IResult> GetClientsAsync(
        Guid partnerId,
        IPartnerRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetClientsAsync(partnerId, cancellationToken));

    private static async Task<IResult> DisableClientAsync(
        Guid partnerId,
        Guid clientId,
        IPartnerRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.DisableClientAsync(partnerId, clientId, cancellationToken));

    private static async Task<IResult> DisablePartnerAsync(
        Guid partnerId,
        IPartnerRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.DisablePartnerAsync(partnerId, cancellationToken));

    private static async Task<IResult> AuthenticateAsync(
        [FromBody] PartnerAccessTokenRequest request,
        IPartnerAuthenticationService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.AuthenticateAsync(request, cancellationToken));
    }

    private static async Task<IResult> MintGiftCardAsync(
        [FromBody] IssueGiftCardApiRequest request,
        IPartnerEpinService service,
        IPartnerMintQuota quota,
        IExecutionContext executionContext,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        var partnerClientId = executionContext.PartnerClientId;
        if (partnerClientId is null)
        {
            return Results.Forbid();
        }

        var lease = await quota
            .TryAcquireAsync(partnerClientId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!lease.Acquired)
        {
            response.Headers.RetryAfter = lease.RetryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "partner.mint.rate_limit_exceeded",
                });
        }

        var result = await service.MintAsync(
            new MintPartnerEpinRequest(
                new IssueGiftCardRequest(
                    request.Amount,
                    request.Currency,
                    request.ValidFromUtc,
                    request.ExpiresAtUtc,
                    request.IsTransferable,
                    request.IsDivisible,
                    request.BusinessReference,
                    request.IdempotencyKey)),
            cancellationToken);
        return Results.Ok(new MintedPartnerEpinApiResponse(
            GiftCardEndpoints.ToResponse(result.GiftCard),
            result.InvitationId,
            result.ClaimUrl,
            result.Pin,
            result.ClaimExpiresAtUtc));
    }
}

public sealed record MintedPartnerEpinApiResponse(
    GiftCardApiResponse GiftCard,
    Guid InvitationId,
    string ClaimUrl,
    string Pin,
    DateTimeOffset ClaimExpiresAtUtc);
