using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Payments.Domain;

internal enum PosTerminalStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// One till belonging to a registered POS client. Its store reference is
/// recorded with every payment for reconciliation and disputes (ADR-018); a
/// first-class store entity is deferred until a requirement needs one.
/// </summary>
internal sealed class PosTerminal
{
    public const int StoreReferenceMaxLength = 64;

    public Guid Id { get; private init; }

    public Guid PosClientId { get; private init; }

    public string Code { get; private init; } = string.Empty;

    public string StoreReference { get; private init; } = string.Empty;

    public PosTerminalStatus Status { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private init; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public static PosTerminal Register(
        Guid id,
        Guid posClientId,
        string? code,
        string? storeReference,
        DateTimeOffset now)
    {
        if (id == Guid.Empty || posClientId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "pos.terminal.scope.required",
                "Terminal and POS client identifiers are required.");
        }

        var normalizedStore = storeReference?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalizedStore.Length == 0 || normalizedStore.Length > StoreReferenceMaxLength)
        {
            throw new ValidationFailedException(
                "pos.terminal.store_reference.invalid",
                $"A store reference of at most {StoreReferenceMaxLength} characters is required.");
        }

        return new PosTerminal
        {
            Id = id,
            PosClientId = posClientId,
            Code = PosClient.NormalizeCode(code),
            StoreReference = normalizedStore,
            Status = PosTerminalStatus.Active,
            RegisteredAtUtc = now,
        };
    }

    public void Disable(DateTimeOffset now)
    {
        if (Status == PosTerminalStatus.Disabled)
        {
            return;
        }

        Status = PosTerminalStatus.Disabled;
        DisabledAtUtc = now;
    }

    public bool IsUsable => Status == PosTerminalStatus.Active;
}
