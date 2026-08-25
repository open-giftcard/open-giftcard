using System.ComponentModel.DataAnnotations;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

public sealed record AllocateCorporateCreditApiRequest
{
    public Guid OrganizationId { get; init; }

    public decimal Amount { get; init; }

    /// <example>TRY</example>
    public string? Currency { get; init; }

    /// <example>CONTRACT-2026-0042</example>
    public string? BusinessReference { get; init; }

    /// <example>allocation-contract-2026-0042-v1</example>
    [Required]
    public string? IdempotencyKey { get; init; }
}

public sealed record CorporateCreditAllocationApiResponse(
    Guid Id,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    string IdempotencyKey,
    DateTimeOffset AllocatedAtUtc);

public sealed record ReverseCorporateCreditApiRequest
{
    public string? Reason { get; init; }

    [Required]
    public string? IdempotencyKey { get; init; }
}

public sealed record CorporateCreditReversalApiResponse(
    Guid Id,
    Guid AllocationId,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string Reason,
    string IdempotencyKey,
    DateTimeOffset ReversedAtUtc);

public sealed record CorporateCreditBalanceApiResponse(string Currency, decimal Amount);

public sealed record CorporateCreditAllocationHistoryApiResponse(
    Guid Id,
    Guid OrganizationId,
    Guid LedgerTransactionId,
    decimal Amount,
    string Currency,
    string BusinessReference,
    Guid AllocatedByUserId,
    DateTimeOffset AllocatedAtUtc,
    CorporateCreditReversalSummaryApiResponse? Reversal);

public sealed record CorporateCreditReversalSummaryApiResponse(
    Guid Id,
    Guid LedgerTransactionId,
    string Reason,
    Guid ReversedByUserId,
    DateTimeOffset ReversedAtUtc);

public sealed record CorporateCreditHistoryPageApiResponse(
    IReadOnlyList<CorporateCreditAllocationHistoryApiResponse> Items,
    int Limit,
    string? NextCursor);

internal static class CorporateCreditEndpoints
{
    public static IEndpointRouteBuilder MapCorporateCreditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                $"{ApiRoutes.V1}/corporate-credits/allocations",
                AllocateAsync)
            .WithName("AllocateCorporateCredit")
            .WithTags("Corporate Credits")
            .WithSummary("Allocates corporate credit through the immutable ledger.")
            .WithDescription(
                "Requires platform.corporate_credits.allocate. Identical retries return the original allocation.")
            .Produces<CorporateCreditAllocationApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireAuthorization();

        app.MapPost(
                $"{ApiRoutes.V1}/corporate-credits/allocations/{{allocationId:guid}}/reversal",
                ReverseAsync)
            .WithName("ReverseCorporateCredit")
            .WithTags("Corporate Credits")
            .WithSummary("Reverses one corporate-credit allocation through a compensating ledger transaction.")
            .WithDescription(
                "Requires platform.corporate_credits.reverse. The original allocation remains immutable.")
            .Produces<CorporateCreditReversalApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireAuthorization();

        var organizationGroup = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/corporate-credits")
            .WithTags("Corporate Credits")
            .RequireAuthorization();

        organizationGroup.MapGet("/balances", GetBalancesAsync)
            .WithName("GetCorporateCreditBalances")
            .WithSummary("Returns ledger-derived corporate-credit balances.")
            .WithDescription(
                "Requires platform.corporate_credits.view or organization.corporate_credits.view.")
            .Produces<IReadOnlyList<CorporateCreditBalanceApiResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        organizationGroup.MapGet("/allocations", GetAllocationHistoryAsync)
            .WithName("GetCorporateCreditAllocationHistory")
            .WithSummary("Returns immutable corporate-credit allocation history.")
            .WithDescription(
                "Requires platform.corporate_credits.view or organization.corporate_credits.view. " +
                "Uses a stable opaque cursor with a limit from 1 to 200.")
            .Produces<CorporateCreditHistoryPageApiResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> AllocateAsync(
        [FromBody] AllocateCorporateCreditApiRequest request,
        ICorporateCreditAllocationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.AllocateAsync(
            new AllocateCorporateCreditRequest(
                request.OrganizationId,
                request.Amount,
                request.Currency,
                request.BusinessReference,
                request.IdempotencyKey),
            cancellationToken);

        return Results.Ok(new CorporateCreditAllocationApiResponse(
            result.Id,
            result.OrganizationId,
            result.LedgerTransactionId,
            result.Amount,
            result.Currency,
            result.BusinessReference,
            result.IdempotencyKey,
            result.AllocatedAtUtc));
    }

    private static async Task<IResult> GetBalancesAsync(
        Guid organizationId,
        ICorporateCreditQueryService service,
        CancellationToken cancellationToken)
    {
        var balances = await service
            .GetBalancesAsync(organizationId, cancellationToken);

        return Results.Ok(
            balances.Select(balance =>
                new CorporateCreditBalanceApiResponse(balance.Currency, balance.Amount)));
    }

    private static async Task<IResult> ReverseAsync(
        Guid allocationId,
        [FromBody] ReverseCorporateCreditApiRequest request,
        ICorporateCreditReversalService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReverseAsync(
            new ReverseCorporateCreditRequest(
                allocationId,
                request.Reason,
                request.IdempotencyKey),
            cancellationToken);

        return Results.Ok(new CorporateCreditReversalApiResponse(
            result.Id,
            result.AllocationId,
            result.OrganizationId,
            result.LedgerTransactionId,
            result.Amount,
            result.Currency,
            result.Reason,
            result.IdempotencyKey,
            result.ReversedAtUtc));
    }

    private static async Task<IResult> GetAllocationHistoryAsync(
        Guid organizationId,
        ICorporateCreditQueryService service,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        var page = await service.GetAllocationHistoryAsync(
            organizationId,
            new CorporateCreditHistoryRequest(
                limit ?? CorporateCreditHistoryRequest.DefaultLimit,
                cursor),
            cancellationToken);

        return Results.Ok(new CorporateCreditHistoryPageApiResponse(
            [.. page.Items.Select(item => new CorporateCreditAllocationHistoryApiResponse(
                item.Id,
                item.OrganizationId,
                item.LedgerTransactionId,
                item.Amount,
                item.Currency,
                item.BusinessReference,
                item.AllocatedByUserId,
                item.AllocatedAtUtc,
                item.Reversal is null
                    ? null
                    : new CorporateCreditReversalSummaryApiResponse(
                        item.Reversal.Id,
                        item.Reversal.LedgerTransactionId,
                        item.Reversal.Reason,
                        item.Reversal.ReversedByUserId,
                        item.Reversal.ReversedAtUtc)))],
            page.Limit,
            page.NextCursor));
    }
}
