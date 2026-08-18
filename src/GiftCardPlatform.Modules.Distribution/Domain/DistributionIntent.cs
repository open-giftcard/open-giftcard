using System.Net.Mail;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal sealed record DistributionIntent(
    Guid GiftCardId,
    RecipientContactType ContactType,
    string RecipientContact,
    string MaskedRecipientContact,
    string BusinessReference,
    string IdempotencyKey)
{
    public const int ContactMaxLength = 320;
    public const int BusinessReferenceMaxLength = 120;
    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 128;

    public static DistributionIntent Create(DistributeGiftCardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GiftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.gift_card.required",
                "A gift card identifier is required.");
        }

        var contact = NormalizeContact(request.ContactType, request.RecipientContact);
        return new DistributionIntent(
            request.GiftCardId,
            request.ContactType,
            contact,
            Mask(request.ContactType, contact),
            NormalizeRequired(
                request.BusinessReference,
                BusinessReferenceMaxLength,
                minimumLength: 1,
                "distribution.business_reference"),
            NormalizeRequired(
                request.IdempotencyKey,
                IdempotencyKeyMaxLength,
                IdempotencyKeyMinLength,
                "distribution.idempotency_key"));
    }

    private static string NormalizeContact(RecipientContactType contactType, string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return contactType switch
        {
            RecipientContactType.Email => NormalizeEmail(candidate),
            RecipientContactType.Phone => NormalizePhone(candidate),
            _ => throw new ValidationFailedException(
                "distribution.contact_type.invalid",
                "Recipient contact type must be Email or Phone."),
        };
    }

    private static string NormalizeEmail(string candidate)
    {
        if (candidate.Length == 0 || candidate.Length > ContactMaxLength)
        {
            throw InvalidEmail();
        }

        try
        {
            var address = new MailAddress(candidate);
            if (!string.Equals(address.Address, candidate, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidEmail();
            }

            return address.Address.ToLowerInvariant();
        }
        catch (FormatException)
        {
            throw InvalidEmail();
        }
    }

    private static string NormalizePhone(string candidate)
    {
        var normalized = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            if (character is ' ' or '-' or '(' or ')')
            {
                continue;
            }

            normalized.Append(character);
        }

        var result = normalized.ToString();
        if (result.Length is < 9 or > 16 ||
            result[0] != '+' ||
            result[1] is < '1' or > '9' ||
            result.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
        {
            throw new ValidationFailedException(
                "distribution.phone.invalid",
                "Phone numbers must use E.164 form, for example +905551234567.");
        }

        return result;
    }

    private static string Mask(RecipientContactType type, string contact)
    {
        if (type == RecipientContactType.Email)
        {
            var at = contact.IndexOf('@', StringComparison.Ordinal);
            var local = contact[..at];
            var visible = local.Length == 1 ? local : local[..1];
            return $"{visible}***{contact[at..]}";
        }

        return $"{contact[..Math.Min(3, contact.Length - 4)]}***{contact[^4..]}";
    }

    private static string NormalizeRequired(
        string? value,
        int maximumLength,
        int minimumLength,
        string errorPrefix)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < minimumLength || normalized.Length > maximumLength)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.invalid_length",
                $"Value must be between {minimumLength} and {maximumLength} characters.");
        }

        return normalized;
    }

    private static ValidationFailedException InvalidEmail() =>
        new(
            "distribution.email.invalid",
            "A valid recipient email address is required.");
}
