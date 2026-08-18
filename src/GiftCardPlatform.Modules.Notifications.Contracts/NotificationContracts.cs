namespace GiftCardPlatform.Modules.Notifications.Contracts;

public enum NotificationChannel
{
    Email = 1,
    Sms = 2,
}

/// <summary>
/// What a message is for. The dispatcher never interprets this; it exists so
/// operators can tell one stuck queue from another without reading payloads.
/// </summary>
public enum NotificationKind
{
    GiftCardClaimInvitation = 1,
    GiftCardShareInvitation = 2,
}

/// <summary>
/// A message to enqueue, with the credential-bearing part kept separate from the
/// part that is safe to keep.
///
/// <paramref name="Body"/> carries the raw activation link. It is protected
/// before it reaches a column and destroyed the moment delivery succeeds or the
/// message is dead-lettered, so a credential never lingers at rest for longer
/// than the delivery it exists for.
///
/// Exactly one owner is required. A company distributing a card sets
/// <paramref name="OrganizationId"/>; a cardholder sharing their own value sets
/// <paramref name="OwnerUserId"/>, because a share is between two people and has
/// no organization behind it. The owner is what the row-level policy checks, so
/// a message with neither cannot be written at all.
/// </summary>
public sealed record NotificationRequest(
    Guid Id,
    NotificationKind Kind,
    NotificationChannel Channel,
    string Recipient,
    string MaskedRecipient,
    string Subject,
    string Body,
    Guid? OrganizationId,
    Guid? OwnerUserId,
    string IdempotencyKey,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Enqueues a message inside the caller's business transaction, exactly as
/// <c>IAuditRecorder.RecordAsync</c> does.
///
/// This is the whole point of the outbox: the message becomes durable if and
/// only if the business change it describes commits. Sending after commit, as
/// the previous in-process notifier did, loses the activation link entirely if
/// the process dies in the gap, and the recipient can then never claim.
/// </summary>
public interface INotificationOutbox
{
    Task EnqueueAsync(NotificationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One delivery attempt's outcome. <paramref name="Retryable"/> separates a
/// transient provider failure from a permanent rejection: a malformed address
/// will never succeed, so retrying it only delays the dead-letter that tells an
/// operator to look.
/// </summary>
public sealed record NotificationDeliveryResult(
    bool Delivered,
    bool Retryable,
    string? FailureCode);

/// <summary>
/// A provider adapter. Implementations must treat <c>message.Id</c> as the
/// provider-side idempotency key where the provider supports one, so a retry
/// after an ambiguous failure cannot send the same activation link twice.
/// </summary>
public interface INotificationChannelSender
{
    NotificationChannel Channel { get; }

    Task<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken);
}

/// <summary>The decrypted message handed to a provider adapter.</summary>
public sealed record NotificationMessage(
    Guid Id,
    NotificationKind Kind,
    NotificationChannel Channel,
    string Recipient,
    string Subject,
    string Body,
    int AttemptNumber);

public sealed record NotificationDispatchBatchResult(
    int Attempted,
    int Delivered,
    int Retrying,
    int DeadLettered);

public interface INotificationDispatcher
{
    Task<NotificationDispatchBatchResult> DispatchDueAsync(
        int maximumItems,
        CancellationToken cancellationToken);
}

/// <summary>
/// Protects the credential-bearing columns at rest.
///
/// The outbox has to hold a raw activation link between commit and delivery,
/// which is the one place this system stores a reusable credential rather than
/// a hash of one. It is therefore encrypted, and the implementation lives in the
/// host so the module takes no dependency on a key provider, exactly as the
/// audit checkpoint signer does.
/// </summary>
public interface INotificationPayloadProtector
{
    string Protect(string plaintext);

    /// <summary>
    /// Returns null when the value cannot be unprotected, which happens if keys
    /// were rotated away or lost. The dispatcher dead-letters rather than
    /// throwing, so one undecryptable row cannot stall the queue behind it.
    /// </summary>
    string? TryUnprotect(string protectedValue);
}

/// <summary>
/// Development-only inspection of what would have been sent. Returns the masked
/// recipient and the activation link, never the recipient's real address.
/// </summary>
public sealed record DevelopmentNotificationResult(
    Guid Id,
    NotificationKind Kind,
    NotificationChannel Channel,
    string MaskedRecipient,
    string Body,
    DateTimeOffset CapturedAtUtc);

public interface IDevelopmentNotificationQuery
{
    Task<DevelopmentNotificationResult?> FindAsync(
        Guid notificationId,
        CancellationToken cancellationToken);
}

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Turns the dispatcher loop on. Off in tests that drive the dispatcher
    /// directly, so a background sweep cannot race an assertion.
    /// </summary>
    public bool DispatchEnabled { get; set; } = true;

    public int DispatchPollIntervalSeconds { get; set; } = 10;

    public int DispatchBatchSize { get; set; } = 20;

    /// <summary>
    /// Attempts before a message is dead-lettered. Bounded because an address
    /// that has failed this many times needs a person, not more retries.
    /// </summary>
    public int MaximumAttempts { get; set; } = 8;

    public int BaseRetryDelaySeconds { get; set; } = 30;

    public int MaximumRetryDelaySeconds { get; set; } = 3600;
}
