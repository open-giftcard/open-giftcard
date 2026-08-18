using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.Modules.Reporting.Application;

/// <summary>
/// Normalized, bounded register filters.
///
/// States and currency are exact matches against a closed set, so an
/// unrecognised value is refused rather than quietly returning everything: a
/// typo that silently widens a filter is worse than one that fails.
///
/// The reference filter is a literal substring match with PostgreSQL wildcards
/// escaped, so a caller cannot supply a pattern of their own.
/// </summary>
internal sealed record OrganizationCardRegisterFilters(
    string? LifecycleState,
    string? OwnershipState,
    string? Currency,
    string? ReferencePattern)
{
    private const int MaximumReferenceLength = 100;

    /// <summary>
    /// Mirrors the Gift Cards lifecycle and ownership enumerations. A state
    /// added by the backend without being added here is refused as unknown,
    /// which surfaces the drift instead of hiding it.
    /// </summary>
    private static readonly string[] LifecycleStates =
        ["Active", "AwaitingClaim", "Suspended", "Cancelled", "Expired"];

    private static readonly string[] OwnershipStates =
        ["OrganizationInventory", "AwaitingClaim", "IdentityOwned"];

    public string Fingerprint
    {
        get
        {
            var normalized = string.Join(
                "|",
                Component(LifecycleState),
                Component(OwnershipState),
                Component(Currency),
                Component(ReferencePattern));
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        }
    }

    public static OrganizationCardRegisterFilters Create(
        OrganizationCardRegisterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = NormalizeText(request.Reference);

        return new OrganizationCardRegisterFilters(
            MatchExactly(
                request.LifecycleState,
                LifecycleStates,
                "reporting.card_register.lifecycle_state.invalid",
                "Lifecycle state"),
            MatchExactly(
                request.OwnershipState,
                OwnershipStates,
                "reporting.card_register.ownership_state.invalid",
                "Ownership state"),
            NormalizeCurrency(request.Currency),
            reference is null ? null : $"%{EscapeLike(reference)}%");
    }

    private static string? MatchExactly(
        string? value,
        string[] allowed,
        string errorCode,
        string fieldName)
    {
        var normalized = NormalizeText(value);
        if (normalized is null)
        {
            return null;
        }

        var match = Array.Find(
            allowed,
            candidate => string.Equals(
                candidate,
                normalized,
                StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new ValidationFailedException(
                errorCode,
                $"{fieldName} '{normalized}' is not recognised.");
        }

        return match;
    }

    private static string? NormalizeCurrency(string? value)
    {
        var normalized = NormalizeText(value);
        if (normalized is null)
        {
            return null;
        }

        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetter))
        {
            throw new ValidationFailedException(
                "reporting.card_register.currency.invalid",
                "Currency must be a three-letter code.");
        }

        return normalized.ToUpperInvariant();
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaximumReferenceLength)
        {
            throw new ValidationFailedException(
                "reporting.card_register.filter.too_long",
                $"A filter must be at most {MaximumReferenceLength} characters.");
        }

        return normalized;
    }

    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string Component(string? value) => value ?? string.Empty;
}
