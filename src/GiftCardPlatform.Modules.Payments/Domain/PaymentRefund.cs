using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Payments.Domain;

internal sealed class PaymentRefund
{
    public const int IdempotencyKeyMaxLength = 128;
    public const int IdempotencyKeyMinLength = 8;
    public const int PosTransactionReferenceMaxLength = 120;
    public const int ReasonMaxLength = 256;

    private PaymentRefund()
    {
        GiftCardPublicReference = null!;
        PosTransactionReference = null!;
        IdempotencyKey = null!;
        Reason = null!;
        Currency = null!;
    }

    public Guid Id { get; private set; }
    public Guid PaymentProvisionId { get; private set; }
    public Guid RedemptionLedgerTransactionId { get; private set; }
    public Guid RefundLedgerTransactionId { get; private set; }
    public Guid FundingOrganizationId { get; private set; }
    public Guid GiftCardId { get; private set; }
    public string GiftCardPublicReference { get; private set; }
    public Guid PosClientId { get; private set; }
    public Guid PosTerminalId { get; private set; }
    public string StoreReference { get; private set; } = null!;
    public string? PosTransactionReference { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string Reason { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; }
    public DateTimeOffset RefundedAtUtc { get; private set; }

    public static PaymentRefund Create(
        Guid id,
        PaymentProvision provision,
        Guid refundLedgerTransactionId,
        Guid posTerminalId,
        string storeReference,
        string? posTransactionReference,
        string? idempotencyKey,
        string? reason,
        decimal amount,
        DateTimeOffset refundedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(provision);
        if (id == Guid.Empty || refundLedgerTransactionId == Guid.Empty ||
            posTerminalId == Guid.Empty || provision.RedemptionLedgerTransactionId is null)
        {
            throw new ValidationFailedException(
                "payment.refund.scope.required",
                "Refund, provision, ledger, and terminal identifiers are required.");
        }

        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedReason = NormalizeRequired(
            reason, ReasonMaxLength, "payment.refund.reason.invalid");
        var normalizedReference = NormalizeOptional(
            posTransactionReference, PosTransactionReferenceMaxLength,
            "payment.refund.pos_transaction_reference.invalid");
        var normalizedStore = NormalizeRequired(
            storeReference, PosTerminal.StoreReferenceMaxLength,
            "payment.refund.store_reference.invalid");
        EnsureAmount(amount);

        return new PaymentRefund
        {
            Id = id,
            PaymentProvisionId = provision.Id,
            RedemptionLedgerTransactionId = provision.RedemptionLedgerTransactionId.Value,
            RefundLedgerTransactionId = refundLedgerTransactionId,
            FundingOrganizationId = provision.FundingOrganizationId,
            GiftCardId = provision.GiftCardId,
            GiftCardPublicReference = provision.GiftCardPublicReference,
            PosClientId = provision.PosClientId,
            PosTerminalId = posTerminalId,
            StoreReference = normalizedStore,
            PosTransactionReference = normalizedReference,
            IdempotencyKey = normalizedKey,
            Reason = normalizedReason,
            Amount = amount,
            Currency = provision.Currency,
            RefundedAtUtc = TruncateToPostgresPrecision(refundedAtUtc),
        };
    }

    public bool Matches(decimal amount, string? posTransactionReference, string? reason) =>
        Amount == amount &&
        PosTransactionReference == NormalizeOptional(
            posTransactionReference, PosTransactionReferenceMaxLength,
            "payment.refund.pos_transaction_reference.invalid") &&
        Reason == NormalizeRequired(reason, ReasonMaxLength, "payment.refund.reason.invalid");

    public static string NormalizeIdempotencyKey(string? value) =>
        NormalizeRequired(
            value, IdempotencyKeyMaxLength, "payment.refund.idempotency_key.invalid",
            IdempotencyKeyMinLength);

    public static void ValidateIntent(
        decimal amount,
        string? posTransactionReference,
        string? reason)
    {
        EnsureAmount(amount);
        _ = NormalizeOptional(
            posTransactionReference,
            PosTransactionReferenceMaxLength,
            "payment.refund.pos_transaction_reference.invalid");
        _ = NormalizeRequired(reason, ReasonMaxLength, "payment.refund.reason.invalid");
    }

    private static void EnsureAmount(decimal amount)
    {
        if (amount <= 0 || amount > PaymentProvision.MaximumAmount ||
            decimal.Round(amount, PaymentProvision.AmountScale) != amount)
        {
            throw new ValidationFailedException(
                "payment.refund.amount.invalid",
                "Refund amount must be positive and have no more than four decimal places.");
        }
    }

    private static string NormalizeRequired(
        string? value,
        int maximum,
        string code,
        int minimum = 1)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimum || normalized.Length > maximum)
        {
            throw new ValidationFailedException(code, "The supplied value is invalid.");
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maximum, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }
        if (normalized.Length > maximum)
        {
            throw new ValidationFailedException(code, "The supplied value is invalid.");
        }
        return normalized;
    }

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
