using GiftCardPlatform.Modules.GiftCards.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record IssueGiftCardApiRequest
{
    public decimal Amount { get; init; }

    /// <example>TRY</example>
    public string? Currency { get; init; }

    public DateTimeOffset? ValidFromUtc { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public bool? IsTransferable { get; init; }

    public bool? IsDivisible { get; init; }

    /// <example>EMPLOYEE-AWARD-2026-0042</example>
    public string? BusinessReference { get; init; }

    /// <example>gift-card-employee-award-2026-0042-v1</example>
    public string? IdempotencyKey { get; init; }
}

public sealed record GiftCardApiResponse(
    Guid Id,
    string PublicReference,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid? OwnerOrganizationId,
    Guid? OwnerUserId,
    string OwnershipState,
    string LifecycleState,
    Guid LedgerAccountId,
    Guid IssuanceLedgerTransactionId,
    decimal FundedAmount,
    string Currency,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsTransferable,
    bool IsDivisible,
    Guid? SourceGiftCardId,
    Guid RootGiftCardId,
    int Generation,
    Guid? DistributionInvitationId,
    DateTimeOffset? DistributedAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    string BusinessReference,
    string IdempotencyKey,
    Guid IssuedByUserId,
    Guid? IssuedByMembershipId,
    Guid? IssuedByPartnerClientId,
    DateTimeOffset IssuedAtUtc);

public sealed record GiftCardInventoryPageApiResponse(
    IReadOnlyList<GiftCardApiResponse> Items,
    int Limit,
    string? NextCursor);

public sealed record GiftCardLifecycleCommandApiRequest(
    string? Reason,
    string? IdempotencyKey);

public sealed record OwnGiftCardLifecycleCommandApiRequest(string? IdempotencyKey);

public sealed record GiftCardLifecycleOperationApiResponse(
    GiftCardLifecycleEventResult Event);

public sealed record GiftCardLifecycleHistoryApiResponse(
    GiftCardApiResponse GiftCard,
    IReadOnlyList<GiftCardLifecycleEventResult> Events);

internal static class GiftCardEndpoints
{
    public static IEndpointRouteBuilder MapGiftCardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/gift-cards")
            .WithTags("Gift Cards")
            .RequireAuthorization();

        group.MapPost("/", IssueAsync)
            .WithName("IssueGiftCard")
            .WithSummary("Issues a ledger-funded card into organization inventory.")
            .WithDescription(
                "Requires organization.gift_cards.issue for the target organization. " +
                "The public reference is for display and support only; it is never a payment credential.")
            .Produces<GiftCardApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/inventory", GetInventoryAsync)
            .WithName("GetGiftCardInventory")
            .WithSummary("Returns organization-owned gift-card inventory.")
            .WithDescription(
                "Requires organization.gift_cards.view for the target organization. " +
                "Uses a stable opaque cursor with a limit from 1 to 200.")
            .Produces<GiftCardInventoryPageApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        MapOrganizationLifecycleEndpoints(group);

        var platform = app.MapGroup($"{ApiRoutes.V1}/platform/gift-cards")
            .WithTags("Gift Cards")
            .RequireAuthorization();
        MapPlatformLifecycleEndpoints(platform);

        var owned = app.MapGroup($"{ApiRoutes.V1}/me/gift-cards")
            .WithTags("My Gift Cards")
            .RequireAuthorization();
        owned.MapPost("/{giftCardId:guid}/lifecycle/suspend", SuspendOwnedAsync)
            .WithName("SuspendOwnedGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        owned.MapPost("/{giftCardId:guid}/lifecycle/reactivate", ReactivateOwnedAsync)
            .WithName("ReactivateOwnedGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        owned.MapGet("/{giftCardId:guid}/lifecycle/history", GetOwnedHistoryAsync)
            .WithName("GetOwnedGiftCardLifecycleHistory")
            .Produces<GiftCardLifecycleHistoryApiResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static void MapOrganizationLifecycleEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{giftCardId:guid}/lifecycle/suspend", SuspendForOrganizationAsync)
            .WithName("SuspendOrganizationGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/reactivate", ReactivateForOrganizationAsync)
            .WithName("ReactivateOrganizationGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/cancel", CancelForOrganizationAsync)
            .WithName("CancelOrganizationGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/expire", ExpireForOrganizationAsync)
            .WithName("ExpireOrganizationGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapGet("/{giftCardId:guid}/lifecycle/history", GetOrganizationHistoryAsync)
            .WithName("GetOrganizationGiftCardLifecycleHistory")
            .Produces<GiftCardLifecycleHistoryApiResponse>();
    }

    private static void MapPlatformLifecycleEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{giftCardId:guid}/lifecycle/suspend", SuspendForPlatformAsync)
            .WithName("SuspendPlatformGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/reactivate", ReactivateForPlatformAsync)
            .WithName("ReactivatePlatformGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/cancel", CancelForPlatformAsync)
            .WithName("CancelPlatformGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapPost("/{giftCardId:guid}/lifecycle/expire", ExpireForPlatformAsync)
            .WithName("ExpirePlatformGiftCard")
            .Produces<GiftCardLifecycleOperationApiResponse>();
        group.MapGet("/{giftCardId:guid}/lifecycle/history", GetPlatformHistoryAsync)
            .WithName("GetPlatformGiftCardLifecycleHistory")
            .Produces<GiftCardLifecycleHistoryApiResponse>();
    }

    private static async Task<IResult> IssueAsync(
        Guid organizationId,
        [FromBody] IssueGiftCardApiRequest request,
        IGiftCardIssuanceService service,
        CancellationToken cancellationToken)
    {
        var result = await service.IssueAsync(
            organizationId,
            new IssueGiftCardRequest(
                request.Amount,
                request.Currency,
                request.ValidFromUtc,
                request.ExpiresAtUtc,
                request.IsTransferable,
                request.IsDivisible,
                request.BusinessReference,
                request.IdempotencyKey),
            cancellationToken);

        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> GetInventoryAsync(
        Guid organizationId,
        IGiftCardInventoryQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        var page = await query.GetInventoryAsync(
            organizationId,
            new GiftCardInventoryRequest(
                limit ?? GiftCardInventoryRequest.DefaultLimit,
                cursor),
            cancellationToken);

        return Results.Ok(new GiftCardInventoryPageApiResponse(
            [.. page.Items.Select(ToResponse)],
            page.Limit,
            page.NextCursor));
    }

    private static Task<IResult> SuspendForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOrganizationLifecycleAsync(
            organizationId,
            giftCardId,
            GiftCardLifecycleAction.Suspend,
            request,
            service,
            cancellationToken);

    private static Task<IResult> ReactivateForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOrganizationLifecycleAsync(
            organizationId,
            giftCardId,
            GiftCardLifecycleAction.Reactivate,
            request,
            service,
            cancellationToken);

    private static Task<IResult> CancelForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOrganizationLifecycleAsync(
            organizationId,
            giftCardId,
            GiftCardLifecycleAction.Cancel,
            request,
            service,
            cancellationToken);

    private static Task<IResult> ExpireForOrganizationAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOrganizationLifecycleAsync(
            organizationId,
            giftCardId,
            GiftCardLifecycleAction.Expire,
            request,
            service,
            cancellationToken);

    private static async Task<IResult> ExecuteOrganizationLifecycleAsync(
        Guid organizationId,
        Guid giftCardId,
        GiftCardLifecycleAction action,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteForOrganizationAsync(
            organizationId,
            giftCardId,
            action,
            new AdministerGiftCardLifecycleRequest(
                request.Reason,
                request.IdempotencyKey),
            cancellationToken);
        return Results.Ok(new GiftCardLifecycleOperationApiResponse(result.Event));
    }

    private static Task<IResult> SuspendForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecutePlatformLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Suspend,
            request,
            service,
            cancellationToken);

    private static Task<IResult> ReactivateForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecutePlatformLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Reactivate,
            request,
            service,
            cancellationToken);

    private static Task<IResult> CancelForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecutePlatformLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Cancel,
            request,
            service,
            cancellationToken);

    private static Task<IResult> ExpireForPlatformAsync(
        Guid giftCardId,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecutePlatformLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Expire,
            request,
            service,
            cancellationToken);

    private static async Task<IResult> ExecutePlatformLifecycleAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        GiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteForPlatformAsync(
            giftCardId,
            action,
            new AdministerGiftCardLifecycleRequest(
                request.Reason,
                request.IdempotencyKey),
            cancellationToken);
        return Results.Ok(new GiftCardLifecycleOperationApiResponse(result.Event));
    }

    private static Task<IResult> SuspendOwnedAsync(
        Guid giftCardId,
        OwnGiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOwnedLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Suspend,
            request,
            service,
            cancellationToken);

    private static Task<IResult> ReactivateOwnedAsync(
        Guid giftCardId,
        OwnGiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken) =>
        ExecuteOwnedLifecycleAsync(
            giftCardId,
            GiftCardLifecycleAction.Reactivate,
            request,
            service,
            cancellationToken);

    private static async Task<IResult> ExecuteOwnedLifecycleAsync(
        Guid giftCardId,
        GiftCardLifecycleAction action,
        OwnGiftCardLifecycleCommandApiRequest request,
        IGiftCardLifecycleService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteForOwnerAsync(
            giftCardId,
            action,
            new OwnGiftCardLifecycleRequest(request.IdempotencyKey),
            cancellationToken);
        return Results.Ok(new GiftCardLifecycleOperationApiResponse(result.Event));
    }

    private static async Task<IResult> GetOrganizationHistoryAsync(
        Guid organizationId,
        Guid giftCardId,
        IGiftCardLifecycleHistoryQuery query,
        CancellationToken cancellationToken) =>
        ToHistoryResponse(await query.GetForOrganizationAsync(
            organizationId,
            giftCardId,
            cancellationToken));

    private static async Task<IResult> GetPlatformHistoryAsync(
        Guid giftCardId,
        IGiftCardLifecycleHistoryQuery query,
        CancellationToken cancellationToken) =>
        ToHistoryResponse(await query.GetForPlatformAsync(
            giftCardId,
            cancellationToken));

    private static async Task<IResult> GetOwnedHistoryAsync(
        Guid giftCardId,
        IGiftCardLifecycleHistoryQuery query,
        CancellationToken cancellationToken) =>
        ToHistoryResponse(await query.GetForOwnerAsync(
            giftCardId,
            cancellationToken));

    private static IResult ToHistoryResponse(GiftCardLifecycleHistoryResult result) =>
        Results.Ok(new GiftCardLifecycleHistoryApiResponse(
            ToResponse(result.GiftCard),
            result.Events));

    internal static GiftCardApiResponse ToResponse(GiftCardResult card) =>
        new(
            card.Id,
            card.PublicReference,
            card.FundingOrganizationId,
            card.IssuingOrganizationId,
            card.OwnerOrganizationId,
            card.OwnerUserId,
            card.OwnershipState,
            card.LifecycleState,
            card.LedgerAccountId,
            card.IssuanceLedgerTransactionId,
            card.FundedAmount,
            card.Currency,
            card.ValidFromUtc,
            card.ExpiresAtUtc,
            card.IsTransferable,
            card.IsDivisible,
            card.SourceGiftCardId,
            card.RootGiftCardId,
            card.Generation,
            card.DistributionInvitationId,
            card.DistributedAtUtc,
            card.ClaimedAtUtc,
            card.BusinessReference,
            card.IdempotencyKey,
            card.IssuedByUserId,
            card.IssuedByMembershipId,
            card.IssuedByPartnerClientId,
            card.IssuedAtUtc);
}
