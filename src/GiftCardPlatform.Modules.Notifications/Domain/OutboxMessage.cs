using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Notifications.Contracts;

namespace GiftCardPlatform.Modules.Notifications.Domain;

internal enum OutboxMessageState
{
    Pending = 1,
    Delivered = 2,
    DeadLettered = 3,
}

/// <summary>
/// One queued notification.
///
/// The credential-bearing body is stored protected and is cleared the moment the
/// message reaches a terminal state, so an activation link exists at rest only
/// while it is still needed to deliver. Everything left afterwards is
/// operational evidence: what was sent, to which masked contact, when, and how
/// many attempts it took.
/// </summary>
internal sealed class OutboxMessage
{
    public const int RecipientMaxLength = 320;
    public const int SubjectMaxLength = 200;
    public const int IdempotencyKeyMaxLength = 200;
    public const int FailureCodeMaxLength = 80;

    public Guid Id { get; private init; }

    public NotificationKind Kind { get; private init; }

    public NotificationChannel Channel { get; private init; }

    /// <summary>
    /// The real destination. Needed to deliver, so it is protected at rest
    /// alongside the body rather than stored in the clear.
    /// </summary>
    public string ProtectedRecipient { get; private set; } = string.Empty;

    /// <summary>Safe to read, log, and show. Never the full contact.</summary>
    public string MaskedRecipient { get; private init; } = string.Empty;

    public string Subject { get; private init; } = string.Empty;

    /// <summary>
    /// The protected message body, containing the raw activation link. Null once
    /// the message is terminal.
    /// </summary>
    public string? ProtectedBody { get; private set; }

    public Guid? OrganizationId { get; private init; }

    /// <summary>
    /// The person this message belongs to, when no organization does. A share
    /// between two cardholders has no company behind it, and the row-level
    /// policy needs something to check.
    /// </summary>
    public Guid? OwnerUserId { get; private init; }

    public string IdempotencyKey { get; private init; } = string.Empty;

    public OutboxMessageState State { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private init; }

    /// <summary>When the dispatcher may next try. Always set while pending.</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? SettledAtUtc { get; private set; }

    /// <summary>
    /// When the underlying credential stops being useful. A message whose
    /// credential has already expired is dead-lettered rather than delivered,
    /// because sending a link that cannot be claimed only produces a support
    /// call.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; private init; }

    public string? LastFailureCode { get; private set; }

    public uint Version { get; private set; }

    public static OutboxMessage Create(
        NotificationRequest request,
        string protectedRecipient,
        string protectedBody,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Id == Guid.Empty)
        {
            throw new ValidationFailedException(
                "notification.id.required",
                "A notification identifier is required.");
        }

        if (string.IsNullOrWhiteSpace(request.MaskedRecipient) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "notification.identity.invalid",
                "A masked recipient and a bounded idempotency key are required.");
        }

        if (string.IsNullOrWhiteSpace(protectedRecipient) ||
            string.IsNullOrWhiteSpace(protectedBody))
        {
            throw new ValidationFailedException(
                "notification.payload.required",
                "A protected recipient and body are required.");
        }

        // Exactly one owner. Without one the row-level policy has nothing to
        // check and the insert would be refused anyway; failing here says why.
        if ((request.OrganizationId is null) == (request.OwnerUserId is null))
        {
            throw new ValidationFailedException(
                "notification.owner.invalid",
                "A notification must belong to exactly one organization or one user.");
        }

        var createdAt = Truncate(now);
        return new OutboxMessage
        {
            Id = request.Id,
            Kind = request.Kind,
            Channel = request.Channel,
            ProtectedRecipient = protectedRecipient,
            MaskedRecipient = request.MaskedRecipient,
            Subject = Trim(request.Subject, SubjectMaxLength),
            ProtectedBody = protectedBody,
            OrganizationId = request.OrganizationId,
            OwnerUserId = request.OwnerUserId,
            IdempotencyKey = request.IdempotencyKey,
            State = OutboxMessageState.Pending,
            AttemptCount = 0,
            CreatedAtUtc = createdAt,
            NextAttemptAtUtc = createdAt,
            ExpiresAtUtc = request.ExpiresAtUtc is null ? null : Truncate(request.ExpiresAtUtc.Value),
        };
    }

    public bool IsDue(DateTimeOffset now) =>
        State == OutboxMessageState.Pending && NextAttemptAtUtc <= Truncate(now);

    /// <summary>
    /// A credential that has already lapsed is not worth delivering. Recorded as
    /// a dead letter with its own code so it is distinguishable from a provider
    /// failure when someone asks why a recipient never got their link.
    /// </summary>
    public bool HasLapsed(DateTimeOffset now) =>
        ExpiresAtUtc is not null && ExpiresAtUtc.Value <= Truncate(now);

    public void RecordDelivered(DateTimeOffset now)
    {
        EnsurePending();
        AttemptCount++;
        State = OutboxMessageState.Delivered;
        SettledAtUtc = Truncate(now);
        Destroy();
    }

    /// <summary>
    /// Schedules the next attempt with exponential backoff, or dead-letters when
    /// the attempt bound is reached. Returns true when the message is terminal.
    /// </summary>
    public bool RecordFailure(
        string? failureCode,
        bool retryable,
        NotificationOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsurePending();

        AttemptCount++;
        LastFailureCode = failureCode is null
            ? null
            : Trim(failureCode, FailureCodeMaxLength);

        if (!retryable || AttemptCount >= options.MaximumAttempts)
        {
            DeadLetter(now);
            return true;
        }

        NextAttemptAtUtc = Truncate(now).AddSeconds(BackoffSeconds(AttemptCount, options));
        return false;
    }

    public void DeadLetter(DateTimeOffset now)
    {
        EnsurePending();
        State = OutboxMessageState.DeadLettered;
        SettledAtUtc = Truncate(now);
        Destroy();
    }

    /// <summary>
    /// Doubling delay, clamped. Deterministic rather than jittered: a single
    /// dispatcher leases rows with SKIP LOCKED, so there is no thundering herd to
    /// spread out, and a predictable schedule is easier to reason about when a
    /// provider outage is being investigated.
    /// </summary>
    internal static int BackoffSeconds(int attemptCount, NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var exponent = Math.Min(attemptCount - 1, 16);
        var scaled = (long)options.BaseRetryDelaySeconds << exponent;
        return (int)Math.Min(scaled, options.MaximumRetryDelaySeconds);
    }

    /// <summary>
    /// Drops the credential-bearing columns. Called on every terminal
    /// transition, so a delivered or abandoned activation link stops existing at
    /// rest even though the message row survives as evidence.
    /// </summary>
    private void Destroy()
    {
        ProtectedBody = null;
        ProtectedRecipient = string.Empty;
    }

    private void EnsurePending()
    {
        if (State != OutboxMessageState.Pending)
        {
            throw new ConflictException(
                "notification.not_pending",
                "The notification is no longer pending.");
        }
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcDateTime.Ticks - (value.UtcDateTime.Ticks % 10), TimeSpan.Zero);
}
