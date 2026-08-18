using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.Api.Endpoints;

internal static class ReportingEndpoints
{
    public static IEndpointRouteBuilder MapReportingEndpoints(
        this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/reports")
            .WithTags("Financial Reporting")
            .RequireAuthorization();

        reports.MapGet("/financial-summary", GetFinancialSummaryAsync)
            .WithName("GetOrganizationFinancialSummary")
            .WithSummary("Returns rebuildable per-currency Phase 2 totals.")
            .WithDescription(
                "Requires both corporate-credit and gift-card view authority. " +
                "Every amount is derived from authoritative domain and Ledger records.")
            .Produces<OrganizationFinancialSummary>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        reports.MapGet("/financial-history", GetFinancialHistoryAsync)
            .WithName("GetOrganizationFinancialHistory")
            .WithSummary("Returns a stable cross-operation financial timeline.")
            .WithDescription(
                "Supports exact category, operation, and currency filters; " +
                "literal case-insensitive business/public reference search; " +
                "and an inclusive UTC start with an exclusive UTC end.")
            .Produces<FinancialHistoryPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        reports.MapGet("/card-register", GetCardRegisterAsync)
            .WithName("GetOrganizationCardRegister")
            .WithSummary("Lists every gift card the organization has funded.")
            .WithDescription(
                "Distinct from gift-card inventory, which shows only cards still in " +
                "organization ownership and therefore loses sight of a card once it " +
                "reaches its recipient. Reports the funded amount rather than the " +
                "remaining balance for a card an identity already owns, and masks the " +
                "recipient contact (ADR-052).")
            .Produces<OrganizationCardRegisterPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        reports.MapGet("/reconciliation", ReconcileAsync)
            .WithName("ReconcileOrganizationFinancials")
            .WithSummary("Compares authoritative financial and sharing records with Ledger postings.")
            .WithDescription(
                "Read-only: findings cover corporate credit, card funding/lifecycle, active " +
                "share reservations, transfers, and child lineage. The operation never mutates " +
                "or repairs history.")
            .Produces<OrganizationReconciliationResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var paymentReports = app.MapGroup($"{ApiRoutes.V1}/platform/reports/payments")
            .WithTags("POS Payment Reporting")
            .RequireAuthorization();
        paymentReports.MapGet("/", GetPaymentsAsync)
            .WithName("GetPosPaymentReport")
            .WithSummary("Searches cross-tenant store, terminal, receipt, payment, and refund activity.")
            .WithDescription(
                "Requires platform.payments.view. Totals are derived from authoritative " +
                "payment provisions and immutable refunds; fully refunded payments are reversals.")
            .Produces<PaymentReportPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        paymentReports.MapGet("/{paymentProvisionId:guid}", GetPaymentAsync)
            .WithName("GetPosPaymentReceiptReport")
            .WithSummary("Returns one payment receipt with its immutable refund lines.")
            .WithDescription("Requires platform.payments.view.")
            .Produces<PaymentReceiptReport>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var owned = app.MapGroup($"{ApiRoutes.V1}/me/gift-cards")
            .WithTags("My Gift Cards")
            .RequireAuthorization();
        owned.MapGet("/", GetMyGiftCardsAsync)
            .WithName("GetMyGiftCards")
            .WithSummary("Lists the signed-in recipient's currently owned cards and balances.")
            .Produces<OwnedGiftCardPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        owned.MapGet("/{giftCardId:guid}", GetMyGiftCardAsync)
            .WithName("GetMyGiftCard")
            .WithSummary("Returns one owned card with its Ledger-derived balance.")
            .Produces<OwnedGiftCardDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        owned.MapGet("/{giftCardId:guid}/history", GetMyGiftCardHistoryAsync)
            .WithName("GetMyGiftCardFinancialHistory")
            .WithSummary("Returns an owned card's transaction and lifecycle history.")
            .Produces<FinancialHistoryPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet(
                $"{ApiRoutes.V1}/organizations/{{organizationId:guid}}/audit-records",
                GetAuditRecordsAsync)
            .WithTags("Audit Investigation")
            .WithName("GetOrganizationAuditRecords")
            .WithSummary("Returns permission-protected tenant audit history.")
            .WithDescription(
                "Requires organization.audit.view or platform.audit.view. " +
                "Supports exact operation, outcome, and correlation filters.")
            .RequireAuthorization()
            .Produces<AuditInvestigationPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> GetFinancialSummaryAsync(
        Guid organizationId,
        IFinancialReportingQuery query,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await query
                .GetOrganizationSummaryAsync(organizationId, cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetCardRegisterAsync(
        Guid organizationId,
        IOrganizationCardRegisterQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null,
        string? lifecycleState = null,
        string? ownershipState = null,
        string? currency = null,
        string? reference = null) =>
        Results.Ok(
            await query
                .GetRegisterAsync(
                    organizationId,
                    new OrganizationCardRegisterRequest(
                        limit ?? OrganizationCardRegisterRequest.DefaultLimit,
                        cursor,
                        lifecycleState,
                        ownershipState,
                        currency,
                        reference),
                    cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetFinancialHistoryAsync(
        Guid organizationId,
        IFinancialReportingQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null,
        string? category = null,
        string? operation = null,
        string? currency = null,
        string? reference = null,
        DateTimeOffset? occurredFromUtc = null,
        DateTimeOffset? occurredBeforeUtc = null) =>
        Results.Ok(
            await query
                .GetOrganizationHistoryAsync(
                    organizationId,
                    new OrganizationFinancialHistoryRequest(
                        limit ?? ReportingPageRequest.DefaultLimit,
                        cursor,
                        category,
                        operation,
                        currency,
                        reference,
                        occurredFromUtc,
                        occurredBeforeUtc),
                    cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> ReconcileAsync(
        Guid organizationId,
        IFinancialReportingQuery query,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await query
                .ReconcileOrganizationAsync(organizationId, cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetPaymentsAsync(
        IPaymentReportingQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null,
        Guid? posClientId = null,
        Guid? posTerminalId = null,
        Guid? fundingOrganizationId = null,
        string? storeReference = null,
        string? state = null,
        string? currency = null,
        string? reference = null,
        DateTimeOffset? occurredFromUtc = null,
        DateTimeOffset? occurredBeforeUtc = null) =>
        Results.Ok(
            await query.GetPaymentsAsync(
                new PaymentReportRequest(
                    limit ?? ReportingPageRequest.DefaultLimit,
                    cursor,
                    posClientId,
                    posTerminalId,
                    fundingOrganizationId,
                    storeReference,
                    state,
                    currency,
                    reference,
                    occurredFromUtc,
                    occurredBeforeUtc),
                cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetPaymentAsync(
        Guid paymentProvisionId,
        IPaymentReportingQuery query,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await query.GetPaymentAsync(paymentProvisionId, cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetMyGiftCardsAsync(
        IFinancialReportingQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null) =>
        Results.Ok(
            await query
                .GetMyGiftCardsAsync(
                    Page(limit, cursor),
                    cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetMyGiftCardAsync(
        Guid giftCardId,
        IFinancialReportingQuery query,
        CancellationToken cancellationToken) =>
        Results.Ok(
            await query
                .GetMyGiftCardAsync(giftCardId, cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetMyGiftCardHistoryAsync(
        Guid giftCardId,
        IFinancialReportingQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null) =>
        Results.Ok(
            await query
                .GetMyGiftCardHistoryAsync(
                    giftCardId,
                    Page(limit, cursor),
                    cancellationToken)
                .ConfigureAwait(false));

    private static async Task<IResult> GetAuditRecordsAsync(
        Guid organizationId,
        IAuditInvestigationQuery query,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null,
        string? operation = null,
        AuditOutcome? outcome = null,
        Guid? correlationId = null) =>
        Results.Ok(
            await query
                .GetAsync(
                    organizationId,
                    new AuditInvestigationRequest(
                        limit ?? AuditInvestigationRequest.DefaultLimit,
                        cursor,
                        operation,
                        outcome,
                        correlationId),
                    cancellationToken)
                .ConfigureAwait(false));

    private static ReportingPageRequest Page(int? limit, string? cursor) =>
        new(limit ?? ReportingPageRequest.DefaultLimit, cursor);
}
