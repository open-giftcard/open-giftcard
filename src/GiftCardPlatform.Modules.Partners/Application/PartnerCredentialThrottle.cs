using System.Collections.Concurrent;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Partners.Application;

/// <summary>
/// Per-credential failure throttle for the partner credential exchange.
///
/// The endpoint-level rate limit partitions by client IP, which is the only
/// signal a caller cannot choose for itself, but it therefore cannot isolate one
/// reseller from another: several partners behind one egress address share a
/// single budget. Partitioning the limiter on the submitted client code instead
/// would be worse than useless, because a brute-forcer would simply vary the
/// code to reset its own bucket.
///
/// This throttle closes that gap from the other side. It is keyed on the client
/// id resolved from the database, so the key is server state rather than
/// anything the request supplied, and an unknown code never creates an entry at
/// all. That also bounds memory: entries exist only for registered clients.
///
/// Only failures count. A working integration exchanges credentials once per
/// token lifetime and succeeds, so it never accumulates; a secret-guessing loop
/// accumulates immediately. Counting every attempt would instead punish a busy
/// legitimate reseller.
///
/// The window is deliberately short. Anyone who learns a client code can spend
/// its failure budget and lock the reseller out, so recovery has to be automatic
/// and quick; a persistent lock would turn a public code into a denial-of-service
/// switch against a partner's revenue.
/// </summary>
internal interface IPartnerCredentialThrottle
{
    bool IsThrottled(Guid partnerClientId);

    void RecordFailure(Guid partnerClientId);

    void RecordSuccess(Guid partnerClientId);
}

internal sealed class PartnerCredentialThrottle(
    TimeProvider timeProvider,
    IOptions<PartnersOptions> options) : IPartnerCredentialThrottle
{
    private readonly PartnersOptions settings = options.Value;
    private readonly ConcurrentDictionary<Guid, Window> windows = new();

    public bool IsThrottled(Guid partnerClientId)
    {
        if (!windows.TryGetValue(partnerClientId, out var window))
        {
            return false;
        }

        return !HasLapsed(window) && window.Failures >= settings.CredentialFailureLimit;
    }

    public void RecordFailure(Guid partnerClientId)
    {
        var now = timeProvider.GetUtcNow();
        windows.AddOrUpdate(
            partnerClientId,
            _ => new Window(now, 1),
            (_, existing) => HasLapsed(existing)
                ? new Window(now, 1)
                : existing with { Failures = existing.Failures + 1 });
    }

    /// <summary>
    /// A successful exchange clears the record, so an operator who mistypes a
    /// secret a few times and then gets it right is not left throttled.
    /// </summary>
    public void RecordSuccess(Guid partnerClientId) => windows.TryRemove(partnerClientId, out _);

    private bool HasLapsed(Window window) =>
        timeProvider.GetUtcNow() - window.StartedAtUtc >=
        TimeSpan.FromSeconds(settings.CredentialFailureWindowSeconds);

    private sealed record Window(DateTimeOffset StartedAtUtc, int Failures);
}
