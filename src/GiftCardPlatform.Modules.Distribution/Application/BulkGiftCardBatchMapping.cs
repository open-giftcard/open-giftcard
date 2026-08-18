using System.Buffers.Binary;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal static class BulkGiftCardBatchMapping
{
    public static BulkGiftCardBatchSummary ToSummary(BulkGiftCardBatch batch) =>
        new(
            batch.Id,
            batch.FundingOrganizationId,
            batch.IssuingOrganizationId,
            batch.BatchReference,
            batch.State.ToString(),
            batch.TotalItems,
            batch.SucceededItems,
            batch.FailedItems,
            batch.CreatedAtUtc,
            batch.CompletedAtUtc,
            batch.RetryOfBatchId);

    public static BulkGiftCardBatchResult ToResult(BulkGiftCardBatch batch)
    {
        var items = batch.Items
            .OrderBy(item => item.Position)
            .Select(ToItemResult)
            .ToArray();
        var totals = batch.Items
            .GroupBy(item => item.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new BulkGiftCardCurrencyTotal(
                group.Key,
                group.Sum(item => item.Amount)))
            .ToArray();

        return new BulkGiftCardBatchResult(
            batch.Id,
            batch.FundingOrganizationId,
            batch.IssuingOrganizationId,
            batch.BatchReference,
            batch.IdempotencyKey,
            batch.State.ToString(),
            batch.TotalItems,
            batch.SucceededItems,
            batch.FailedItems,
            totals,
            batch.CreatedByUserId,
            batch.CreatedByMembershipId,
            batch.CreatedAtUtc,
            batch.CompletedAtUtc,
            batch.RetryOfBatchId,
            items);
    }

    public static BulkGiftCardBatchPage ToPage(
        BulkGiftCardBatch batch,
        IReadOnlyList<BulkGiftCardBatchItem> items,
        int limit,
        string? nextCursor) =>
        new(
            batch.Id,
            batch.FundingOrganizationId,
            batch.IssuingOrganizationId,
            batch.BatchReference,
            batch.State.ToString(),
            batch.TotalItems,
            batch.SucceededItems,
            batch.FailedItems,
            batch.CreatedByUserId,
            batch.CreatedByMembershipId,
            batch.CreatedAtUtc,
            batch.CompletedAtUtc,
            batch.RetryOfBatchId,
            limit,
            nextCursor,
            items.Select(ToItemResult).ToArray());

    public static BulkGiftCardBatchItemResult ToItemResult(
        BulkGiftCardBatchItem item) =>
        new(
            item.Position,
            item.ItemReference,
            item.State.ToString(),
            item.ContactType,
            item.MaskedRecipientContact,
            item.Amount,
            item.Currency,
            item.GiftCardId,
            item.GiftCardPublicReference,
            item.InvitationId,
            item.GiftCardState,
            item.InvitationState,
            item.DistributedAtUtc,
            item.FailureCode,
            item.FailureMessage,
            item.SettledAtUtc);
}

internal static class BulkGiftCardBatchCursorCodec
{
    private const byte Version = 1;
    private const int PayloadLength = 5;

    public static string Encode(int position)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        payload[0] = Version;
        BinaryPrimitives.WriteInt32BigEndian(payload[1..], position);
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static int? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var payload = Convert.FromBase64String(normalized);
            if (payload.Length != PayloadLength || payload[0] != Version)
            {
                throw InvalidCursor();
            }

            var position = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1));
            if (position < 1)
            {
                throw InvalidCursor();
            }

            return position;
        }
        catch (FormatException)
        {
            throw InvalidCursor();
        }
    }

    private static ValidationFailedException InvalidCursor() =>
        new(
            "bulk.cursor.invalid",
            "The bulk-batch cursor is invalid.");
}
