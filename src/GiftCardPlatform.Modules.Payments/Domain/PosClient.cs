using System.Text.RegularExpressions;
using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Payments.Domain;

internal enum PosClientStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// A registered point-of-sale integration (ADR-043). Platform-scoped, not
/// tenant-scoped: the stores are the platform operator's own, so no customer organization owns a
/// till. The client is an API caller and is never issued database credentials.
/// </summary>
internal sealed partial class PosClient
{
    public const int CodeMaxLength = 32;
    public const int DisplayNameMaxLength = 120;

    public Guid Id { get; private init; }

    public string Code { get; private init; } = string.Empty;

    public string DisplayName { get; private init; } = string.Empty;

    /// <summary>SHA-256 hex of a 256-bit secret. The secret itself is never stored.</summary>
    public string SecretHash { get; private set; } = string.Empty;

    public PosClientStatus Status { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private init; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public static PosClient Register(
        Guid id,
        string? code,
        string? displayName,
        string secretHash,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ValidationFailedException(
                "pos.client.id.required",
                "A POS client identifier is required.");
        }

        var normalizedCode = NormalizeCode(code);
        var normalizedName = displayName?.Trim() ?? string.Empty;
        if (normalizedName.Length == 0 || normalizedName.Length > DisplayNameMaxLength)
        {
            throw new ValidationFailedException(
                "pos.client.display_name.invalid",
                $"A POS client display name of at most {DisplayNameMaxLength} characters is required.");
        }

        if (secretHash is null || secretHash.Length != PosCredentialCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "pos.client.secret.invalid",
                "A POS client secret hash is required.");
        }

        return new PosClient
        {
            Id = id,
            Code = normalizedCode,
            DisplayName = normalizedName,
            SecretHash = secretHash,
            Status = PosClientStatus.Active,
            RegisteredAtUtc = now,
        };
    }

    public void Disable(DateTimeOffset now)
    {
        if (Status == PosClientStatus.Disabled)
        {
            return;
        }

        Status = PosClientStatus.Disabled;
        DisabledAtUtc = now;
    }

    public bool IsUsable => Status == PosClientStatus.Active;

    public static string NormalizeCode(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Length > CodeMaxLength ||
            !CodePattern().IsMatch(normalized))
        {
            throw new ValidationFailedException(
                "pos.client.code.invalid",
                "A POS code must be 1-32 characters of A-Z, 0-9, or hyphen.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9-]+$")]
    private static partial Regex CodePattern();
}
