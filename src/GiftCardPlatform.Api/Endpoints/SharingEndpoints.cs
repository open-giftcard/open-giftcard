using System.ComponentModel.DataAnnotations;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record CreateGiftCardShareApiRequest(decimal Amount, [property: Required] string? IdempotencyKey);

public sealed record CancelGiftCardShareApiRequest([property: Required] string? IdempotencyKey);

public sealed record ClaimGiftCardShareApiRequest(
    string? ClaimToken,
    string? Pin,
    [property: Required] string? IdempotencyKey);

public sealed record CreateDirectGiftCardShareApiRequest(
    decimal Amount,
    GiftCardShareContactType ContactType,
    string? RecipientContact,
    [property: Required] string? IdempotencyKey);

public sealed record ClaimDirectGiftCardShareApiRequest(
    string? ClaimToken,
    string? Password,
    [property: Required] string? IdempotencyKey);

internal static class SharingEndpoints
{
    public static IEndpointRouteBuilder MapSharingEndpoints(
        this IEndpointRouteBuilder app,
        IHostEnvironment environment)
    {
        app.MapPost(
                $"{ApiRoutes.V1}/me/gift-cards/{{giftCardId:guid}}/shares",
                CreateAsync)
            .WithTags("Sharing")
            .WithName("CreateGiftCardShare")
            .WithSummary("Reserves part of an owned card and creates a protected share link.")
            .WithDescription(
                "Returns the raw claim URL and six-digit PIN once. The Ledger transfer " +
                "does not post until a recipient claims the share.")
            .RequireAuthorization()
            .Produces<CreatedGiftCardShareResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/me/shares", GetMineAsync)
            .WithTags("Sharing")
            .WithName("GetMyGiftCardShares")
            .WithSummary("Lists shares sent or received by the signed-in cardholder.")
            .RequireAuthorization()
            .Produces<GiftCardSharePage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapPost($"{ApiRoutes.V1}/me/shares/{{shareId:guid}}/cancel", CancelAsync)
            .WithTags("Sharing")
            .WithName("CancelGiftCardShare")
            .WithSummary("Cancels an unclaimed share and releases its reservation.")
            .RequireAuthorization()
            .Produces<GiftCardShareResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost($"{ApiRoutes.V1}/share-claims", ClaimAsync)
            .WithTags("Sharing")
            .WithName("ClaimGiftCardShare")
            .WithSummary("Claims a PIN-protected share into the signed-in recipient account.")
            .WithDescription(
                "The caller must already be authenticated. Claim atomically creates a " +
                "separate child card and posts the balanced source-to-child Ledger transfer.")
            .RequireAuthorization()
            .Produces<ClaimedGiftCardShareResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost(
                $"{ApiRoutes.V1}/me/gift-cards/{{giftCardId:guid}}/share-invitations",
                CreateDirectAsync)
            .WithTags("Sharing")
            .WithName("CreateDirectGiftCardShare")
            .WithSummary("Reserves card value and sends a contact-bound share invitation.")
            .WithDescription(
                "Commits the reservation before notification delivery. The response and " +
                "audit expose only the masked recipient contact.")
            .RequireAuthorization()
            .Produces<CreatedDirectGiftCardShareResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost($"{ApiRoutes.V1}/share-invitation-claims", ClaimDirectAsync)
            .WithTags("Sharing")
            .WithName("ClaimDirectGiftCardShare")
            .WithSummary("Claims a verified email or phone share invitation.")
            .WithDescription(
                "Creates the minimum recipient identity only when the verified contact is new. " +
                "Existing identities continue through normal login.")
            .AllowAnonymous()
            .RequireRateLimiting(DistributionEndpoints.ClaimRateLimitPolicy)
            .Produces<ClaimedDirectGiftCardShareResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        if (environment.IsDevelopment())
        {
            app.MapGet(
                    $"{ApiRoutes.V1}/me/shares/{{shareId:guid}}/development-delivery",
                    GetDevelopmentDirectDeliveryAsync)
                .WithTags("Development")
                .WithName("GetDevelopmentDirectGiftCardShareDelivery")
                .WithSummary("Returns the captured Development direct-share delivery.")
                .RequireAuthorization()
                .Produces<DevelopmentDirectGiftCardShareDeliveryResult>()
                .Produces(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden);
        }

        return app;
    }

    private static async Task<IResult> CreateAsync(
        Guid giftCardId,
        [FromBody] CreateGiftCardShareApiRequest request,
        IProtectedGiftCardShareService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var created = await service.CreateAsync(
            giftCardId,
            new CreateGiftCardShareRequest(request.Amount, request.IdempotencyKey),
            cancellationToken);
        return Results.Created($"{ApiRoutes.V1}/me/shares/{created.Share.Id}", created);
    }

    private static async Task<IResult> GetMineAsync(
        int? limit,
        string? cursor,
        GiftCardShareKind? kind,
        GiftCardShareState? state,
        GiftCardShareDirection? direction,
        IProtectedGiftCardShareService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetMineAsync(
            new GiftCardSharePageRequest(
                limit ?? GiftCardSharePageRequest.DefaultLimit,
                cursor,
                kind,
                state,
                direction),
            cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid shareId,
        [FromBody] CancelGiftCardShareApiRequest request,
        IProtectedGiftCardShareService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.CancelAsync(
            shareId,
            request.IdempotencyKey,
            cancellationToken));

    private static async Task<IResult> ClaimAsync(
        [FromBody] ClaimGiftCardShareApiRequest request,
        IProtectedGiftCardShareService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.ClaimAsync(
            new ClaimGiftCardShareRequest(request.ClaimToken, request.Pin, request.IdempotencyKey),
            cancellationToken));
    }

    private static async Task<IResult> CreateDirectAsync(
        Guid giftCardId,
        [FromBody] CreateDirectGiftCardShareApiRequest request,
        IDirectGiftCardShareService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var created = await service.CreateDirectAsync(
            giftCardId,
            new CreateDirectGiftCardShareRequest(
                request.Amount,
                request.ContactType,
                request.RecipientContact,
                request.IdempotencyKey),
            cancellationToken);
        return Results.Created($"{ApiRoutes.V1}/me/shares/{created.Share.Id}", created);
    }

    private static async Task<IResult> ClaimDirectAsync(
        [FromBody] ClaimDirectGiftCardShareApiRequest request,
        IDirectGiftCardShareService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.ClaimDirectAsync(
            new ClaimDirectGiftCardShareRequest(
                request.ClaimToken,
                request.Password,
                request.IdempotencyKey),
            cancellationToken));
    }

    private static async Task<IResult> GetDevelopmentDirectDeliveryAsync(
        Guid shareId,
        IDevelopmentDirectGiftCardShareDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        var delivery = await query.FindAsync(shareId, cancellationToken);
        return delivery is null ? Results.NotFound() : Results.Ok(delivery);
    }
}
