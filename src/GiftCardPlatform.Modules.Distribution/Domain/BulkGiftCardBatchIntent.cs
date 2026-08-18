using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal sealed record BulkGiftCardBatchItemIntent(
    int Position,
    string ItemReference,
    IssueGiftCardRequest Issuance,
    RecipientContactType ContactType,
    string RecipientContact,
    string MaskedRecipientContact,
    string DistributionIdempotencyKey)
{
    public DistributeGiftCardRequest ToDistributionRequest(Guid giftCardId) =>
        new(
            giftCardId,
            ContactType,
            RecipientContact,
            ItemReference,
            DistributionIdempotencyKey);
}

internal sealed record BulkGiftCardBatchIntent(
    string BatchReference,
    string IdempotencyKey,
    string IntentHash,
    IReadOnlyList<BulkGiftCardBatchItemIntent> Items)
{
    public const int MaximumItems = 100;
    public const int MaximumAsyncItems = 2_000;
    public const int ItemReferenceMaxLength = 120;
    public const int BatchReferenceMaxLength = 120;
    public const int IntentHashLength = 64;

    private static readonly Guid ValidationGiftCardId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static BulkGiftCardBatchIntent Create(
        CreateBulkGiftCardBatchRequest request,
        IGiftCardIssuanceRequestValidator issuanceValidator) =>
        Create(request, issuanceValidator, MaximumItems);

    public static BulkGiftCardBatchIntent CreateAsync(
        CreateBulkGiftCardBatchRequest request,
        IGiftCardIssuanceRequestValidator issuanceValidator) =>
        Create(request, issuanceValidator, MaximumAsyncItems);

    public static BulkGiftCardBatchIntent CreateRetry(
        BulkGiftCardBatch source,
        IReadOnlyList<BulkGiftCardBatchItemIntent> items)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > MaximumAsyncItems)
        {
            throw new ConflictException(
                "bulk.retry.no_failed_items",
                "The batch has no failed items to retry.");
        }

        var suffix = "/retry";
        var prefixLength = Math.Min(
            source.BatchReference.Length,
            BatchReferenceMaxLength - suffix.Length);
        var batchReference = source.BatchReference[..prefixLength] + suffix;
        var idempotencyKey = $"bulk-retry:{source.Id:N}";
        return new BulkGiftCardBatchIntent(
            batchReference,
            idempotencyKey,
            ComputeIntentHash(batchReference, items),
            items);
    }

    private static BulkGiftCardBatchIntent Create(
        CreateBulkGiftCardBatchRequest request,
        IGiftCardIssuanceRequestValidator issuanceValidator,
        int maximumItems)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issuanceValidator);

        var batchReference = NormalizeRequired(
            request.BatchReference,
            BatchReferenceMaxLength,
            minimumLength: 1,
            "bulk.batch_reference");
        var idempotencyKey = NormalizeRequired(
            request.IdempotencyKey,
            DistributionIntent.IdempotencyKeyMaxLength,
            DistributionIntent.IdempotencyKeyMinLength,
            "bulk.idempotency_key");
        var requestedItems = request.Items
            ?? throw new ValidationFailedException(
                "bulk.items.required",
                "At least one batch item is required.");
        if (requestedItems.Count < 1 || requestedItems.Count > maximumItems)
        {
            throw new ValidationFailedException(
                "bulk.items.invalid_count",
                $"A batch must contain between 1 and {maximumItems} items.");
        }

        var itemReferences = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<BulkGiftCardBatchItemIntent>(requestedItems.Count);
        for (var index = 0; index < requestedItems.Count; index++)
        {
            var requested = requestedItems[index]
                ?? throw ItemValidationFailure(
                    index,
                    null,
                    "bulk.item.required");
            string itemReference;
            try
            {
                itemReference = NormalizeRequired(
                    requested.ItemReference,
                    ItemReferenceMaxLength,
                    minimumLength: 1,
                    "bulk.item_reference");
            }
            catch (ValidationFailedException exception)
            {
                throw ItemValidationFailure(
                    index,
                    requested.ItemReference,
                    exception.Code);
            }

            if (!itemReferences.Add(itemReference))
            {
                throw ItemValidationFailure(
                    index,
                    itemReference,
                    "bulk.item_reference.duplicate");
            }

            try
            {
                var issuanceIdempotencyKey = DeriveChildIdempotencyKey(
                    idempotencyKey,
                    itemReference,
                    "issue");
                var issuance = issuanceValidator.ValidateAndNormalize(
                    new IssueGiftCardRequest(
                        requested.Amount,
                        requested.Currency,
                        requested.ValidFromUtc,
                        requested.ExpiresAtUtc,
                        requested.IsTransferable,
                        requested.IsDivisible,
                        itemReference,
                        issuanceIdempotencyKey));
                var distributionIdempotencyKey = DeriveChildIdempotencyKey(
                    idempotencyKey,
                    itemReference,
                    "distribute");
                var distribution = DistributionIntent.Create(
                    new DistributeGiftCardRequest(
                        ValidationGiftCardId,
                        requested.ContactType,
                        requested.RecipientContact,
                        itemReference,
                        distributionIdempotencyKey));

                items.Add(new BulkGiftCardBatchItemIntent(
                    index + 1,
                    itemReference,
                    issuance,
                    distribution.ContactType,
                    distribution.RecipientContact,
                    distribution.MaskedRecipientContact,
                    distributionIdempotencyKey));
            }
            catch (ValidationFailedException exception)
            {
                throw ItemValidationFailure(
                    index,
                    itemReference,
                    exception.Code);
            }
        }

        return new BulkGiftCardBatchIntent(
            batchReference,
            idempotencyKey,
            ComputeIntentHash(batchReference, items),
            items);
    }

    private static string ComputeIntentHash(
        string batchReference,
        IReadOnlyList<BulkGiftCardBatchItemIntent> items)
    {
        var canonical = new StringBuilder();
        Append(canonical, batchReference);
        Append(canonical, items.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var item in items)
        {
            Append(canonical, item.Position.ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.ItemReference);
            Append(
                canonical,
                item.Issuance.Amount.ToString("G29", CultureInfo.InvariantCulture));
            Append(canonical, item.Issuance.Currency ?? string.Empty);
            Append(
                canonical,
                item.Issuance.ValidFromUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
            Append(
                canonical,
                item.Issuance.ExpiresAtUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
            Append(
                canonical,
                (item.Issuance.IsTransferable ?? false) ? "1" : "0");
            Append(
                canonical,
                (item.Issuance.IsDivisible ?? false) ? "1" : "0");
            Append(
                canonical,
                ((int)item.ContactType).ToString(CultureInfo.InvariantCulture));
            Append(canonical, item.RecipientContact);
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string DeriveChildIdempotencyKey(
        string batchIdempotencyKey,
        string itemReference,
        string operation)
    {
        var canonical = new StringBuilder();
        Append(canonical, batchIdempotencyKey);
        Append(canonical, itemReference);
        Append(canonical, operation);
        return "bulk:" +
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Append(StringBuilder builder, string value) =>
        builder
            .Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('|');

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

    private static ValidationFailedException ItemValidationFailure(
        int index,
        string? itemReference,
        string causeCode) =>
        new(
            "bulk.item.invalid",
            $"Batch item at index {index} is invalid.",
            new Dictionary<string, object?>
            {
                ["itemIndex"] = index,
                ["itemReference"] = SafeReference(itemReference),
                ["causeCode"] = causeCode,
            });

    private static string? SafeReference(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized)
            ? null
            : normalized[..Math.Min(normalized.Length, ItemReferenceMaxLength)];
    }
}
