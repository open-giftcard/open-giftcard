using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.Modules.Reporting.Application;

internal sealed record FinancialHistorySearchFilters(
    string? Category,
    string? Operation,
    string? Currency,
    string? ReferencePattern,
    DateTimeOffset? OccurredFromUtc,
    DateTimeOffset? OccurredBeforeUtc)
{
    private const int MaximumCategoryLength = 64;
    private const int MaximumOperationLength = 128;
    private const int MaximumReferenceLength = 200;

    public bool IsEmpty =>
        Category is null &&
        Operation is null &&
        Currency is null &&
        ReferencePattern is null &&
        OccurredFromUtc is null &&
        OccurredBeforeUtc is null;

    public string Fingerprint
    {
        get
        {
            var normalized = string.Join(
                "|",
                Component(Category),
                Component(Operation),
                Component(Currency),
                Component(ReferencePattern),
                Component(OccurredFromUtc?.UtcDateTime.Ticks.ToString(
                    CultureInfo.InvariantCulture)),
                Component(OccurredBeforeUtc?.UtcDateTime.Ticks.ToString(
                    CultureInfo.InvariantCulture)));
            return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        }
    }

    public static FinancialHistorySearchFilters Normalize(
        OrganizationFinancialHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var category = NormalizeText(
            request.Category,
            MaximumCategoryLength,
            "reporting.history.category.invalid",
            "Category");
        var operation = NormalizeText(
            request.Operation,
            MaximumOperationLength,
            "reporting.history.operation.invalid",
            "Operation");
        var currency = NormalizeText(
            request.Currency,
            3,
            "reporting.history.currency.invalid",
            "Currency");
        if (currency is not null &&
            (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
        {
            throw new ValidationFailedException(
                "reporting.history.currency.invalid",
                "Currency must be a three-letter code.");
        }

        var reference = NormalizeText(
            request.Reference,
            MaximumReferenceLength,
            "reporting.history.reference.invalid",
            "Reference");
        var fromUtc = request.OccurredFromUtc?.ToUniversalTime();
        var beforeUtc = request.OccurredBeforeUtc?.ToUniversalTime();
        if (fromUtc is not null &&
            beforeUtc is not null &&
            fromUtc >= beforeUtc)
        {
            throw new ValidationFailedException(
                "reporting.history.occurred_range.invalid",
                "Occurred-from UTC must be earlier than occurred-before UTC.");
        }

        return new FinancialHistorySearchFilters(
            category?.ToLowerInvariant(),
            operation?.ToLowerInvariant(),
            currency?.ToUpperInvariant(),
            reference is null
                ? null
                : $"%{EscapeLike(reference.ToLowerInvariant())}%",
            fromUtc,
            beforeUtc);
    }

    private static string? NormalizeText(
        string? value,
        int maximumLength,
        string errorCode,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ValidationFailedException(
                errorCode,
                $"{fieldName} must be at most {maximumLength} characters.");
        }

        return normalized;
    }

    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string Component(string? value) =>
        value is null
            ? "-1:"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Length}:{value}");
}
