using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Reporting.Contracts;

namespace GiftCardPlatform.Modules.Reporting.Application;

internal sealed record PaymentReportingSearchFilters(
    Guid? PosClientId,
    Guid? PosTerminalId,
    Guid? FundingOrganizationId,
    string? StoreReference,
    string? State,
    string? Currency,
    string? ReferencePattern,
    DateTimeOffset? OccurredFromUtc,
    DateTimeOffset? OccurredBeforeUtc)
{
    private const int MaximumReferenceLength = 200;
    private const int MaximumStoreReferenceLength = 64;

    public string Fingerprint
    {
        get
        {
            var normalized = string.Join(
                "|",
                Component(PosClientId?.ToString("N")),
                Component(PosTerminalId?.ToString("N")),
                Component(FundingOrganizationId?.ToString("N")),
                Component(StoreReference),
                Component(State),
                Component(Currency),
                Component(ReferencePattern),
                Component(OccurredFromUtc?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)),
                Component(OccurredBeforeUtc?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        }
    }

    public static PaymentReportingSearchFilters Normalize(PaymentReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureOptionalIdentifier(request.PosClientId, "pos_client_id");
        EnsureOptionalIdentifier(request.PosTerminalId, "pos_terminal_id");
        EnsureOptionalIdentifier(request.FundingOrganizationId, "funding_organization_id");

        var storeReference = NormalizeText(
            request.StoreReference,
            MaximumStoreReferenceLength,
            "reporting.payments.store_reference.invalid",
            "Store reference")?.ToUpperInvariant();
        var state = NormalizeText(
            request.State,
            16,
            "reporting.payments.state.invalid",
            "Payment state");
        state = state is null
            ? null
            : state.ToLowerInvariant() switch
            {
                "active" => "Active",
                "confirmed" => "Confirmed",
                "cancelled" => "Cancelled",
                "expired" => "Expired",
                _ => throw new ValidationFailedException(
                    "reporting.payments.state.invalid",
                    "Payment state must be Active, Confirmed, Cancelled, or Expired."),
            };
        var currency = NormalizeText(
            request.Currency,
            3,
            "reporting.payments.currency.invalid",
            "Currency")?.ToUpperInvariant();
        if (currency is not null &&
            (currency.Length != 3 || !currency.All(char.IsAsciiLetter)))
        {
            throw new ValidationFailedException(
                "reporting.payments.currency.invalid",
                "Currency must be a three-letter code.");
        }

        var reference = NormalizeText(
            request.Reference,
            MaximumReferenceLength,
            "reporting.payments.reference.invalid",
            "Reference");
        var occurredFromUtc = request.OccurredFromUtc?.ToUniversalTime();
        var occurredBeforeUtc = request.OccurredBeforeUtc?.ToUniversalTime();
        if (occurredFromUtc is not null &&
            occurredBeforeUtc is not null &&
            occurredFromUtc >= occurredBeforeUtc)
        {
            throw new ValidationFailedException(
                "reporting.payments.occurred_range.invalid",
                "Occurred-from UTC must be earlier than occurred-before UTC.");
        }

        return new PaymentReportingSearchFilters(
            request.PosClientId,
            request.PosTerminalId,
            request.FundingOrganizationId,
            storeReference,
            state,
            currency,
            reference is null ? null : $"%{EscapeLike(reference.ToLowerInvariant())}%",
            occurredFromUtc,
            occurredBeforeUtc);
    }

    private static void EnsureOptionalIdentifier(Guid? value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new ValidationFailedException(
                $"reporting.payments.{field}.invalid",
                "A supplied reporting identifier must not be empty.");
        }
    }

    private static string? NormalizeText(
        string? value,
        int maximumLength,
        string code,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ValidationFailedException(
                code,
                $"{field} must be at most {maximumLength} characters.");
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
            : string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value}");
}
