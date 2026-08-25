using System.ComponentModel.DataAnnotations;
namespace GiftCardPlatform.Modules.Payments.Contracts;

/// <summary>
/// One issued payment credential. <see cref="RawToken"/> is returned exactly
/// once, by the creating request, and is never persisted, logged, or audited
/// (ADR-017). Only its SHA-256 hash is stored.
/// </summary>
public sealed record IssuedPaymentTokenResult(
    Guid Id,
    Guid GiftCardId,
    string GiftCardPublicReference,
    string RawToken,
    string NumericCode,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PaymentTokenStatusResult(
    Guid Id,
    Guid GiftCardId,
    string State,
    Guid? PaymentProvisionId,
    decimal? Amount,
    string? Currency,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? SettledAtUtc,
    decimal? ConfirmedAmount);

public interface IPaymentTokenService
{
    /// <summary>
    /// Issues a short-lived payment credential for a card the authenticated
    /// caller currently owns. The card must be identity-owned, active, and
    /// within its validity window; transferability and divisibility are sharing
    /// policies and deliberately do not gate spending.
    /// </summary>
    Task<IssuedPaymentTokenResult> IssueAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the exact owner's checkout outcome without returning or
    /// accepting either payment credential presentation.
    /// </summary>
    Task<PaymentTokenStatusResult> GetStatusAsync(
        Guid giftCardId,
        Guid paymentTokenId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Active payment holds against a card, so other spending paths can subtract
/// them from available value (ADR-033). Sharing consumes this exactly as
/// Payments consumes Sharing's reservation query — a share must never be able
/// to spend value already promised to a till, and vice versa.
///
/// This is a read boundary only; it grants no authority over provisions.
/// </summary>
public interface IPaymentReservationQuery
{
    Task<decimal> GetActiveProvisionedAmountAsync(
        Guid giftCardId,
        CancellationToken cancellationToken);
}

/// <param name="Amount">The sale total the till is trying to settle.</param>
/// <param name="AllowPartialApproval">
/// Whether the till can handle being approved for less than it asked for.
///
/// Defaults to false, so a caller that has not been written to read the approved
/// amount back cannot be silently under-charged: it keeps the original refusal.
/// A till that sets this is stating it will collect the remainder by another
/// tender, which is how a gift card is normally used, since the customer rarely
/// knows the balance. This mirrors card networks, where a terminal must signal
/// that it supports partial authorization before an issuer will give one.
/// </param>
/// <param name="IdempotencyKey">
/// The till's own name for this attempt. Required, because without it a lost
/// response cannot be recovered: the credential is consumed server-side, the
/// till never learns the provision id, and it cannot cancel a hold it cannot
/// name, so the customer's value stays reserved until the window expires.
/// Retrying with the same key returns the original hold instead of being refused
/// as a replay. Reusing it with different intent is a conflict.
/// </param>
public sealed record CreatePaymentProvisionRequest(
    string? PaymentToken,
    string? PaymentCode,
    decimal Amount,
    string? PosTransactionReference,
    bool AllowPartialApproval = false,
    [property: Required] string? IdempotencyKey = null);

public sealed record ConfirmPaymentProvisionRequest(decimal Amount);

/// <summary>
/// A till asking what a presented card is worth, before deciding how to split
/// the tender.
/// </summary>
public sealed record PaymentBalanceInquiryRequest(
    string? PaymentToken,
    string? PaymentCode);

/// <param name="AvailableAmount">
/// Spendable now: posted value less every active hold, of either kind. This is
/// the number a cashier should be told, not the posted balance, because value
/// already promised to a share or another till cannot be spent here.
/// </param>
/// <param name="ExpiresAtUtc">
/// When the card stops being usable. A cashier is the last person who can tell
/// the customer this in time to matter.
/// </param>
public sealed record PaymentBalanceInquiryResult(
    string GiftCardPublicReference,
    decimal AvailableAmount,
    string Currency,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Reading a presented card's value without reserving any of it.
///
/// Deliberately separate from <see cref="IPaymentProvisionService"/>: an inquiry
/// takes nothing, holds nothing, and posts nothing. It is only reachable with a
/// live credential the cardholder has just produced, so it cannot be used to
/// sweep balances, and it does not consume that credential, so asking does not
/// cost the customer the code they are about to pay with.
/// </summary>
public interface IPaymentBalanceInquiryService
{
    Task<PaymentBalanceInquiryResult> InquireAsync(
        PaymentBalanceInquiryRequest request,
        CancellationToken cancellationToken);
}

public sealed record CreatePaymentRefundRequest(
    decimal Amount,
    [property: Required] string? IdempotencyKey,
    string? PosTransactionReference,
    string? Reason);

public sealed record PaymentRefundResult(
    Guid Id,
    Guid PaymentProvisionId,
    Guid GiftCardId,
    string GiftCardPublicReference,
    decimal Amount,
    string Currency,
    string StoreReference,
    string? PosTransactionReference,
    string Reason,
    Guid RefundLedgerTransactionId,
    DateTimeOffset RefundedAtUtc,
    decimal RemainingRefundableAmount);

/// <param name="Amount">
/// The value actually held. Compare it against <paramref name="RequestedAmount"/>:
/// when it is lower, the card did not cover the sale and the difference is still
/// owed by another tender.
/// </param>
/// <param name="RequestedAmount">The sale total the till asked to settle.</param>
/// <param name="OutstandingAmount">
/// What is still owed, stated rather than left to the caller's arithmetic
/// because getting it wrong means a customer walks out having underpaid.
/// </param>
public sealed record PaymentProvisionResult(
    Guid Id,
    Guid GiftCardId,
    string GiftCardPublicReference,
    decimal Amount,
    decimal RequestedAmount,
    decimal OutstandingAmount,
    string Currency,
    string State,
    string StoreReference,
    string? PosTransactionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? SettledAtUtc,
    decimal? ConfirmedAmount,
    Guid? RedemptionLedgerTransactionId);

public interface IPaymentProvisionService
{
    /// <summary>
    /// Consumes a payment credential exactly once and holds the requested value
    /// for the ADR-044 window. Nothing is posted to the Ledger.
    /// </summary>
    Task<PaymentProvisionResult> CreateAsync(
        CreatePaymentProvisionRequest request,
        CancellationToken cancellationToken);

    Task<PaymentProvisionResult> GetAsync(
        Guid provisionId,
        CancellationToken cancellationToken);

    /// <summary>Releases an active hold. Only the POS client that created it may cancel.</summary>
    Task<PaymentProvisionResult> CancelAsync(
        Guid provisionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirms one active hold exactly once and posts the immutable redemption
    /// transaction. The held amount is a ceiling; a smaller positive amount
    /// releases the remainder atomically (ADR-018, ADR-046).
    /// </summary>
    Task<PaymentProvisionResult> ConfirmAsync(
        Guid provisionId,
        ConfirmPaymentProvisionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends one partial refund against a confirmed redemption. Reusing the
    /// same idempotency key with the same intent returns the original result;
    /// cumulative refunds can never exceed the confirmed amount.
    /// </summary>
    Task<PaymentRefundResult> RefundAsync(
        Guid provisionId,
        CreatePaymentRefundRequest request,
        CancellationToken cancellationToken);
}

public sealed record PaymentProvisionExpirationBatchResult(int Examined, int Expired);

public interface IPaymentProvisionExpirationProcessor
{
    Task<PaymentProvisionExpirationBatchResult> ProcessDueAsync(
        int maximumItems,
        CancellationToken cancellationToken);
}

public sealed class PaymentProvisionOptions
{
    public const string SectionName = "Payments:Provisions";

    /// <summary>
    /// ADR-044 fixes the reservation window at 2 minutes against the server
    /// clock. Validated on start so an environment cannot silently widen how
    /// long an abandoned till can hold a cardholder's value.
    /// </summary>
    public int WindowSeconds { get; set; } = 120;

    public bool ExpirationEnabled { get; set; } = true;

    public int ExpirationPollIntervalSeconds { get; set; } = 15;

    public int ExpirationBatchSize { get; set; } = 50;
}

public sealed record RegisterPosClientRequest(string? Code, string? DisplayName);

/// <summary>
/// <see cref="Secret"/> is returned only by the registering request and only its
/// hash is stored, so it cannot be recovered afterwards (ADR-043).
/// </summary>
public sealed record RegisteredPosClientResult(
    Guid Id,
    string Code,
    string DisplayName,
    string Secret,
    DateTimeOffset RegisteredAtUtc);

public sealed record PosClientResult(
    Guid Id,
    string Code,
    string DisplayName,
    string Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? DisabledAtUtc);

public sealed record RegisterPosTerminalRequest(string? Code, string? StoreReference);

public sealed record PosTerminalResult(
    Guid Id,
    Guid PosClientId,
    string Code,
    string StoreReference,
    string Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? DisabledAtUtc);

public interface IPosRegistrationService
{
    Task<RegisteredPosClientResult> RegisterClientAsync(
        RegisterPosClientRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PosClientResult>> GetClientsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Permanently retires a POS client. New and already-issued device tokens
    /// are refused on their next request.
    /// </summary>
    Task<PosClientResult> DisableClientAsync(
        Guid posClientId,
        CancellationToken cancellationToken);

    Task<PosTerminalResult> RegisterTerminalAsync(
        Guid posClientId,
        RegisterPosTerminalRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PosTerminalResult>> GetTerminalsAsync(
        Guid posClientId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Permanently retires one till without disabling its sibling terminals.
    /// New and already-issued device tokens are refused on their next request.
    /// </summary>
    Task<PosTerminalResult> DisableTerminalAsync(
        Guid posClientId,
        Guid posTerminalId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Claim names carried by a POS access token. Public because the API
/// authentication adapter must recognise a device principal before it falls
/// through to the user path; the issuing logic stays inside the module.
/// </summary>
public static class PosTokenClaims
{
    /// <summary>Marks a token as a device principal rather than a user subject.</summary>
    public const string Principal = "pos_principal";

    public const string ClientId = "pos_client_id";

    public const string TerminalId = "pos_terminal_id";

    public const string PrincipalValue = "true";
}

public sealed record PosAccessTokenRequest(
    string? ClientCode,
    string? ClientSecret,
    string? TerminalCode);

public sealed record PosAccessTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid PosClientId,
    Guid PosTerminalId,
    string StoreReference);

public interface IPosAuthenticationService
{
    Task<PosAccessTokenResult> AuthenticateAsync(
        PosAccessTokenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The verified active device behind a POS access token. Token claims carry
/// identity only; active state is resolved from PostgreSQL on every request.
/// </summary>
public sealed record PosPrincipal(Guid PosClientId, Guid PosTerminalId);

/// <summary>
/// Resolves a signed POS token back to an active client and terminal. Returning
/// null makes client or terminal retirement effective on the next request
/// instead of waiting for the access token to expire.
/// </summary>
public interface IPosPrincipalResolver
{
    Task<PosPrincipal?> ResolveAsync(
        Guid posClientId,
        Guid posTerminalId,
        CancellationToken cancellationToken);
}

public sealed class PosAuthenticationOptions
{
    public const string SectionName = "Payments:Pos";

    /// <summary>
    /// POS access tokens are short-lived and have no refresh credential: a till
    /// re-authenticates with its own secret, which it holds anyway. That keeps a
    /// stolen token useful only briefly without adding a second long-lived
    /// credential to distribute and revoke.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;
}

public sealed class PaymentTokenOptions
{
    public const string SectionName = "Payments:Tokens";

    /// <summary>
    /// ADR-017 fixes this at 60 seconds against the server clock. It is exposed
    /// as configuration so the value has one source of truth, and validated on
    /// start so it cannot drift from the accepted decision.
    /// </summary>
    public int LifetimeSeconds { get; set; } = 60;
}
