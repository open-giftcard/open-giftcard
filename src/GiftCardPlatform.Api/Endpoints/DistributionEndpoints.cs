using GiftCardPlatform.Modules.Distribution.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record DistributeGiftCardApiRequest(
    RecipientContactType ContactType,
    string? RecipientContact,
    string? BusinessReference,
    string? IdempotencyKey);

public sealed record DistributionInvitationApiResponse(
    Guid Id,
    Guid FundingOrganizationId,
    Guid IssuingOrganizationId,
    Guid GiftCardId,
    DistributionInvitationKind Kind,
    RecipientContactType? ContactType,
    string? MaskedRecipientContact,
    string State,
    DateTimeOffset ClaimExpiresAtUtc,
    int FailedClaimAttempts,
    string BusinessReference,
    string IdempotencyKey,
    Guid DistributedByUserId,
    Guid? DistributedByMembershipId,
    Guid? DistributedByPartnerClientId,
    DateTimeOffset DistributedAtUtc,
    Guid? ClaimedByUserId,
    DateTimeOffset? ClaimedAtUtc);

public sealed record ClaimGiftCardApiRequest(
    string? ClaimToken,
    string? Pin,
    RecipientContactType? ContactType,
    string? RecipientContact,
    string? Password,
    string? IdempotencyKey);

public sealed record GiftCardClaimApiResponse(
    Guid InvitationId,
    Guid OwnerUserId,
    bool IdentityWasCreated,
    string MaskedLoginIdentifier,
    TokenPairApiResponse? Session,
    GiftCardApiResponse GiftCard,
    DateTimeOffset ClaimedAtUtc);

public sealed record BulkGiftCardBatchItemApiRequest(
    string? ItemReference,
    decimal Amount,
    string? Currency,
    DateTimeOffset? ValidFromUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool? IsTransferable,
    bool? IsDivisible,
    RecipientContactType ContactType,
    string? RecipientContact);

public sealed record CreateBulkGiftCardBatchApiRequest(
    string? BatchReference,
    string? IdempotencyKey,
    IReadOnlyList<BulkGiftCardBatchItemApiRequest>? Items);

internal static class DistributionEndpoints
{
    public const string ClaimRateLimitPolicy = "gift-card-claim";

    public static IEndpointRouteBuilder MapDistributionEndpoints(
        this IEndpointRouteBuilder app,
        IHostEnvironment environment)
    {
        var distributions = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/gift-cards/" +
                "{giftCardId:guid}/distributions")
            .WithTags("Distribution")
            .RequireAuthorization();

        distributions.MapPost("/", DistributeAsync)
            .WithName("DistributeGiftCard")
            .WithSummary("Sends one inventory card to an email or phone recipient.")
            .WithDescription(
                "Creates a single-use invitation and changes ownership to AwaitingClaim. " +
                "No ledger value moves during this ownership-only operation.")
            .Produces<DistributionInvitationApiResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var batches = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/" +
                "gift-card-batches")
            .WithTags("Distribution")
            .RequireAuthorization();
        batches.MapPost("/", CreateBulkBatchAsync)
            .WithName("CreateBulkGiftCardBatch")
            .WithSummary("Issues and distributes 1–100 cards atomically.")
            .WithDescription(
                "The synchronous operation requires both issue and distribute permissions. " +
                "All card, Ledger, invitation, audit, and batch rows commit together or all roll back.")
            .Produces<BulkGiftCardBatchResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        batches.MapGet("/{batchId:guid}", GetBulkBatchAsync)
            .WithName("GetBulkGiftCardBatch")
            .WithSummary("Returns one durable bulk issuance/distribution result.")
            .Produces<BulkGiftCardBatchResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var asyncBatches = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/" +
                "gift-cards/bulk-batches")
            .WithTags("Distribution")
            .RequireAuthorization();
        asyncBatches.MapPost("/async", AcceptBulkBatchAsync)
            .WithName("AcceptAsyncBulkGiftCardBatch")
            .WithSummary("Durably accepts up to 2,000 gift-card rows for background processing.")
            .WithDescription(
                "Requires both issue and distribute permissions. Every normalized row is " +
                "persisted before the Pending response; corporate credit is consumed per row.")
            .Produces<BulkGiftCardBatchSummary>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);
        asyncBatches.MapGet("/{batchId:guid}", GetAsyncBulkBatchAsync)
            .WithName("GetAsyncBulkGiftCardBatch")
            .WithSummary("Returns paginated per-row outcomes for an asynchronous batch.")
            .Produces<BulkGiftCardBatchPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        asyncBatches.MapPost("/{batchId:guid}/retry", RetryBulkBatchAsync)
            .WithName("RetryAsyncBulkGiftCardBatch")
            .WithSummary("Creates an idempotent child batch containing only failed rows.")
            .Produces<BulkGiftCardBatchSummary>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost($"{ApiRoutes.V1}/gift-card-claims", ClaimAsync)
            .WithTags("Distribution")
            .WithName("ClaimGiftCard")
            .WithSummary("Claims and activates a delivered gift card.")
            .WithDescription(
                "The invitation selects email or phone as the recipient login identifier. " +
                "A password is required only when the verified contact has no existing identity.")
            .AllowAnonymous()
            .RequireRateLimiting(ClaimRateLimitPolicy)
            .Produces<GiftCardClaimApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        if (environment.IsDevelopment())
        {
            app.MapGet(
                    $"{ApiRoutes.V1}/development/organizations/{{organizationId:guid}}/" +
                    "claim-deliveries/{invitationId:guid}",
                    GetDevelopmentDeliveryAsync)
                .WithTags("Development")
                .WithName("GetDevelopmentClaimDelivery")
                .WithSummary("Returns the Development claim delivery captured for the demo.")
                .RequireAuthorization()
                .Produces<DevelopmentClaimDeliveryResult>()
                .ProducesProblem(StatusCodes.Status401Unauthorized)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status404NotFound);
        }

        return app;
    }

    private static async Task<IResult> CreateBulkBatchAsync(
        Guid organizationId,
        [FromBody] CreateBulkGiftCardBatchApiRequest request,
        IBulkGiftCardBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            organizationId,
            new CreateBulkGiftCardBatchRequest(
                request.BatchReference,
                request.IdempotencyKey,
                request.Items?
                    .Select(item => new BulkGiftCardBatchItemRequest(
                        item.ItemReference,
                        item.Amount,
                        item.Currency,
                        item.ValidFromUtc,
                        item.ExpiresAtUtc,
                        item.IsTransferable,
                        item.IsDivisible,
                        item.ContactType,
                        item.RecipientContact))
                    .ToArray()),
            cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/organizations/{organizationId}/" +
            $"gift-card-batches/{result.Id}",
            result);
    }

    private static async Task<IResult> GetBulkBatchAsync(
        Guid organizationId,
        Guid batchId,
        IBulkGiftCardBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await service
                .GetAsync(organizationId, batchId, cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> AcceptBulkBatchAsync(
        Guid organizationId,
        [FromBody] CreateBulkGiftCardBatchApiRequest request,
        IBulkGiftCardBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.AcceptAsync(
            organizationId,
            ToContract(request),
            cancellationToken).ConfigureAwait(false);
        return Results.Accepted(
            $"{ApiRoutes.V1}/organizations/{organizationId}/gift-cards/" +
            $"bulk-batches/{result.Id}",
            result);
    }

    private static async Task<IResult> GetAsyncBulkBatchAsync(
        Guid organizationId,
        Guid batchId,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        IBulkGiftCardBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await service.GetPageAsync(
                organizationId,
                batchId,
                new BulkGiftCardBatchPageRequest(
                    limit ?? BulkGiftCardBatchPageRequest.DefaultLimit,
                    cursor),
                cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> RetryBulkBatchAsync(
        Guid organizationId,
        Guid batchId,
        IBulkGiftCardBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RetryAsync(
            organizationId,
            batchId,
            cancellationToken).ConfigureAwait(false);
        return Results.Accepted(
            $"{ApiRoutes.V1}/organizations/{organizationId}/gift-cards/" +
            $"bulk-batches/{result.Id}",
            result);
    }

    private static CreateBulkGiftCardBatchRequest ToContract(
        CreateBulkGiftCardBatchApiRequest request) =>
        new(
            request.BatchReference,
            request.IdempotencyKey,
            request.Items?
                .Select(item => new BulkGiftCardBatchItemRequest(
                    item.ItemReference,
                    item.Amount,
                    item.Currency,
                    item.ValidFromUtc,
                    item.ExpiresAtUtc,
                    item.IsTransferable,
                    item.IsDivisible,
                    item.ContactType,
                    item.RecipientContact))
                .ToArray());

    private static async Task<IResult> DistributeAsync(
        Guid organizationId,
        Guid giftCardId,
        [FromBody] DistributeGiftCardApiRequest request,
        IGiftCardDistributionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.DistributeAsync(
            organizationId,
            new DistributeGiftCardRequest(
                giftCardId,
                request.ContactType,
                request.RecipientContact,
                request.BusinessReference,
                request.IdempotencyKey),
            cancellationToken);
        var response = ToResponse(result);
        return Results.Created(
            $"{ApiRoutes.V1}/organizations/{organizationId}/gift-cards/" +
            $"{giftCardId}/distributions/{response.Id}",
            response);
    }

    private static async Task<IResult> ClaimAsync(
        [FromBody] ClaimGiftCardApiRequest request,
        HttpContext context,
        IGiftCardClaimService service,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var result = await service.ClaimAsync(
            new ClaimGiftCardRequest(
                request.ClaimToken,
                request.Pin,
                request.ContactType,
                request.RecipientContact,
                request.Password,
                request.IdempotencyKey),
            cancellationToken);
        return Results.Ok(new GiftCardClaimApiResponse(
            result.InvitationId,
            result.OwnerUserId,
            result.IdentityWasCreated,
            result.MaskedLoginIdentifier,
            result.Session is null
                ? null
                : new TokenPairApiResponse(
                    result.Session.AccessToken,
                    result.Session.AccessTokenExpiresAtUtc,
                    result.Session.RefreshToken,
                    result.Session.RefreshTokenExpiresAtUtc),
            GiftCardEndpoints.ToResponse(result.GiftCard),
            result.ClaimedAtUtc));
    }

    private static async Task<IResult> GetDevelopmentDeliveryAsync(
        Guid organizationId,
        Guid invitationId,
        IDevelopmentClaimDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        var result = await query
            .FindAsync(organizationId, invitationId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static DistributionInvitationApiResponse ToResponse(
        DistributionInvitationResult invitation) =>
        new(
            invitation.Id,
            invitation.FundingOrganizationId,
            invitation.IssuingOrganizationId,
            invitation.GiftCardId,
            invitation.Kind,
            invitation.ContactType,
            invitation.MaskedRecipientContact,
            invitation.State,
            invitation.ClaimExpiresAtUtc,
            invitation.FailedClaimAttempts,
            invitation.BusinessReference,
            invitation.IdempotencyKey,
            invitation.DistributedByUserId,
            invitation.DistributedByMembershipId,
            invitation.DistributedByPartnerClientId,
            invitation.DistributedAtUtc,
            invitation.ClaimedByUserId,
            invitation.ClaimedAtUtc);
}
