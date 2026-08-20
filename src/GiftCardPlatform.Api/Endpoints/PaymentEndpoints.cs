using GiftCardPlatform.Modules.Payments.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace GiftCardPlatform.Api.Endpoints;

internal static class PaymentEndpoints
{
    public const string RateLimitPolicy = "payment-redemption";

    public const string BalanceInquiryRateLimitPolicy = "pos-balance-inquiry";

    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                $"{ApiRoutes.V1}/me/gift-cards/{{giftCardId:guid}}/payment-tokens",
                IssueAsync)
            .WithTags("Payments")
            .WithName("IssuePaymentToken")
            .WithSummary("Issues a short-lived payment credential for an owned gift card.")
            .WithDescription(
                "Returns a 256-bit opaque credential once, valid for 60 seconds against " +
                "the server clock. The value it authorises is resolved by server-side " +
                "lookup; the credential itself carries no card, owner, amount, or balance.")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<IssuedPaymentTokenResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet(
                $"{ApiRoutes.V1}/me/gift-cards/{{giftCardId:guid}}/" +
                "payment-tokens/{paymentTokenId:guid}",
                GetOwnedPaymentStatusAsync)
            .WithTags("Payments")
            .WithName("GetOwnedPaymentTokenStatus")
            .WithSummary("Returns the exact card owner's checkout outcome.")
            .WithDescription(
                "Returns pending, active, confirmed, cancelled, or expired state " +
                "without returning or accepting the payment credential.")
            .RequireAuthorization()
            .Produces<PaymentTokenStatusResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost($"{ApiRoutes.V1}/pos/balance-inquiries", InquireBalanceAsync)
            .WithTags("Payments")
            .WithName("InquirePaymentBalance")
            .WithSummary("Reads what a presented card can spend, reserving nothing.")
            .WithDescription(
                "Answers the question a cashier is asked before splitting a tender. " +
                "Requires a live presented credential, so it cannot be used to sweep " +
                "balances, and deliberately does not consume it, so asking does not " +
                "cost the customer the code they are about to pay with. Returns the " +
                "amount spendable now, which excludes value held by a share or " +
                "another till. Unknown, expired, and consumed credentials are refused " +
                "identically. It is a POST because the credential travels in the body: " +
                "a query string would put a payment credential into logs and history.")
            .RequireAuthorization()
            .RequireRateLimiting(BalanceInquiryRateLimitPolicy)
            .Produces<PaymentBalanceInquiryResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost($"{ApiRoutes.V1}/pos/payment-provisions", CreateProvisionAsync)
            .WithTags("Payments")
            .WithName("CreatePaymentProvision")
            .WithSummary("Reserves card value for a sale in progress.")
            .WithDescription(
                "Consumes the presented payment credential exactly once and holds the " +
                "amount for two minutes. Nothing is posted to the Ledger; confirmation " +
                "is a separate operation. Unknown, replayed, and expired credentials " +
                "are refused identically.")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<PaymentProvisionResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet($"{ApiRoutes.V1}/pos/payment-provisions/{{provisionId:guid}}", GetProvisionAsync)
            .WithTags("Payments")
            .WithName("GetPaymentProvision")
            .WithSummary("Reads a hold created by the calling POS client.")
            .RequireAuthorization()
            .Produces<PaymentProvisionResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost(
                $"{ApiRoutes.V1}/pos/payment-provisions/{{provisionId:guid}}/cancel",
                CancelProvisionAsync)
            .WithTags("Payments")
            .WithName("CancelPaymentProvision")
            .WithSummary("Releases an active hold. Posts nothing to the Ledger.")
            .RequireAuthorization()
            .Produces<PaymentProvisionResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost(
                $"{ApiRoutes.V1}/pos/payment-provisions/{{provisionId:guid}}/confirm",
                ConfirmProvisionAsync)
            .WithTags("Payments")
            .WithName("ConfirmPaymentProvision")
            .WithSummary("Confirms one hold and posts the redemption exactly once.")
            .WithDescription(
                "Charges the explicitly stated positive amount up to the held ceiling, " +
                "releases any remainder, and atomically records the balanced Ledger " +
                "redemption. Safe retries return the original outcome.")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<PaymentProvisionResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost(
                $"{ApiRoutes.V1}/pos/payment-provisions/{{provisionId:guid}}/refunds",
                RefundProvisionAsync)
            .WithTags("Payments")
            .WithName("RefundPaymentProvision")
            .WithSummary("Appends a partial or full refund to a confirmed redemption.")
            .WithDescription(
                "Supports multiple immutable partial refunds up to the confirmed amount. " +
                "The original POS client may refund from any of its active terminals; " +
                "safe retries use an idempotency key.")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .Produces<PaymentRefundResult>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> InquireBalanceAsync(
        [FromBody] PaymentBalanceInquiryRequest request,
        IPaymentBalanceInquiryService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.InquireAsync(request, cancellationToken));
    }

    private static async Task<IResult> CreateProvisionAsync(
        [FromBody] CreatePaymentProvisionRequest request,
        IPaymentProvisionService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var created = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"{ApiRoutes.V1}/pos/payment-provisions/{created.Id}", created);
    }

    private static async Task<IResult> GetProvisionAsync(
        Guid provisionId,
        IPaymentProvisionService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAsync(provisionId, cancellationToken));

    private static async Task<IResult> CancelProvisionAsync(
        Guid provisionId,
        IPaymentProvisionService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.CancelAsync(provisionId, cancellationToken));

    private static async Task<IResult> ConfirmProvisionAsync(
        Guid provisionId,
        [FromBody] ConfirmPaymentProvisionRequest request,
        IPaymentProvisionService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.ConfirmAsync(
            provisionId,
            request,
            cancellationToken));
    }

    private static async Task<IResult> RefundProvisionAsync(
        Guid provisionId,
        [FromBody] CreatePaymentRefundRequest request,
        IPaymentProvisionService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var refund = await service.RefundAsync(provisionId, request, cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/pos/payment-provisions/{provisionId}/refunds/{refund.Id}", refund);
    }

    private static async Task<IResult> IssueAsync(
        Guid giftCardId,
        IPaymentTokenService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        // The raw credential is returned exactly once and must not be cached by
        // any intermediary (ADR-017).
        response.Headers.CacheControl = "no-store";
        var issued = await service.IssueAsync(giftCardId, cancellationToken);
        return Results.Created(
            $"{ApiRoutes.V1}/me/gift-cards/{issued.GiftCardId}/payment-tokens/{issued.Id}",
            issued);
    }

    private static async Task<IResult> GetOwnedPaymentStatusAsync(
        Guid giftCardId,
        Guid paymentTokenId,
        IPaymentTokenService service,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        return Results.Ok(
            await service.GetStatusAsync(
                giftCardId,
                paymentTokenId,
                cancellationToken));
    }
}
