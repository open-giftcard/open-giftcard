using System.Text.RegularExpressions;
using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Organizations.Domain;

/// <summary>
/// Normalization and validation for organization codes. Codes are normalized
/// consistently so uniqueness is enforced on a single canonical form, backed by
/// a unique index in PostgreSQL.
/// </summary>
internal static partial class OrganizationCode
{
    public const int MinLength = 2;
    public const int MaxLength = 32;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern { get; }

    /// <summary>Trims surrounding whitespace and upper-cases invariantly.</summary>
    public static string Normalize(string? raw) => raw?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>Normalizes then validates, throwing a mapped validation error.</summary>
    public static string NormalizeAndValidate(string? raw)
    {
        var normalized = Normalize(raw);

        if (normalized.Length == 0)
        {
            throw new ValidationFailedException("organization.code.required", "Organization code is required.");
        }

        if (normalized.Length is < MinLength or > MaxLength)
        {
            throw new ValidationFailedException(
                "organization.code.invalid_length",
                $"Organization code must be between {MinLength} and {MaxLength} characters.");
        }

        if (!CodePattern.IsMatch(normalized))
        {
            throw new ValidationFailedException(
                "organization.code.invalid_format",
                "Organization code may contain only letters, digits, hyphens, and underscores, and must start with a letter or digit.");
        }

        return normalized;
    }
}
