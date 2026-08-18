using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Application;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.GiftCards.Application;
using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.UnitTests;

public sealed class BulkGiftCardBatchTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 18, 0, 0, TimeSpan.Zero);

    private static readonly IGiftCardIssuanceRequestValidator Validator =
        new GiftCardIssuanceRequestValidator(new FixedTimeProvider(Now));

    [Fact]
    public void Intent_normalizes_items_and_derives_stable_child_keys()
    {
        var request = Request(
            Item(
                "  ROW-001  ",
                " try ",
                RecipientContactType.Email,
                " Recipient@Example.com "),
            Item(
                "ROW-002",
                "TRY",
                RecipientContactType.Phone,
                "+90 (555) 123-4567"));

        var intent = BulkGiftCardBatchIntent.Create(request, Validator);
        var equivalent = BulkGiftCardBatchIntent.Create(
            Request(
                Item(
                    "ROW-001",
                    "TRY",
                    RecipientContactType.Email,
                    "recipient@example.com"),
                Item(
                    "ROW-002",
                    "try",
                    RecipientContactType.Phone,
                    "+905551234567")),
            Validator);

        Assert.Equal("PRESENTATION-BATCH", intent.BatchReference);
        Assert.Equal(equivalent.IntentHash, intent.IntentHash);
        Assert.Collection(
            intent.Items,
            first =>
            {
                Assert.Equal(1, first.Position);
                Assert.Equal("ROW-001", first.ItemReference);
                Assert.Equal("TRY", first.Issuance.Currency);
                Assert.Equal("recipient@example.com", first.RecipientContact);
                Assert.Equal("r***@example.com", first.MaskedRecipientContact);
                Assert.False(first.Issuance.IsTransferable);
                Assert.False(first.Issuance.IsDivisible);
            },
            second =>
            {
                Assert.Equal(2, second.Position);
                Assert.Equal("+905551234567", second.RecipientContact);
            });
        Assert.NotEqual(
            intent.Items[0].Issuance.IdempotencyKey,
            intent.Items[0].DistributionIdempotencyKey);
        Assert.Equal(
            intent.Items[0].Issuance.IdempotencyKey,
            equivalent.Items[0].Issuance.IdempotencyKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Batch_size_must_be_between_one_and_one_hundred(int count)
    {
        var items = Enumerable
            .Range(1, count)
            .Select(index => Item(
                $"ROW-{index:000}",
                "TRY",
                RecipientContactType.Email,
                $"recipient-{index}@example.com"))
            .ToArray();

        var exception = Assert.Throws<ValidationFailedException>(() =>
            BulkGiftCardBatchIntent.Create(
                new CreateBulkGiftCardBatchRequest(
                    "PRESENTATION-BATCH",
                    "bulk-request-001",
                    items),
                Validator));

        Assert.Equal("bulk.items.invalid_count", exception.Code);
    }

    [Fact]
    public void Duplicate_item_reference_has_stable_item_context()
    {
        var exception = Assert.Throws<ValidationFailedException>(() =>
            BulkGiftCardBatchIntent.Create(
                Request(
                    Item(
                        "ROW-001",
                        "TRY",
                        RecipientContactType.Email,
                        "first@example.com"),
                    Item(
                        "ROW-001",
                        "TRY",
                        RecipientContactType.Email,
                        "second@example.com")),
                Validator));

        Assert.Equal("bulk.item.invalid", exception.Code);
        Assert.Equal(1, exception.Extensions!["itemIndex"]);
        Assert.Equal("ROW-001", exception.Extensions["itemReference"]);
        Assert.Equal(
            "bulk.item_reference.duplicate",
            exception.Extensions["causeCode"]);
    }

    [Fact]
    public void Invalid_item_does_not_echo_recipient_contact()
    {
        const string privateContact = "not-an-email-private-value";
        var exception = Assert.Throws<ValidationFailedException>(() =>
            BulkGiftCardBatchIntent.Create(
                Request(
                    Item(
                        "ROW-001",
                        "TRY",
                        RecipientContactType.Email,
                        "valid@example.com"),
                    Item(
                        "ROW-002",
                        "TRY",
                        RecipientContactType.Email,
                        privateContact)),
                Validator));

        Assert.Equal(1, exception.Extensions!["itemIndex"]);
        Assert.Equal("ROW-002", exception.Extensions["itemReference"]);
        Assert.Equal("distribution.email.invalid", exception.Extensions["causeCode"]);
        Assert.DoesNotContain(privateContact, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_batch_maps_ordered_results_and_currency_totals()
    {
        var intent = BulkGiftCardBatchIntent.Create(
            Request(
                Item(
                    "ROW-001",
                    "TRY",
                    RecipientContactType.Email,
                    "first@example.com"),
                Item(
                    "ROW-002",
                    "USD",
                    RecipientContactType.Phone,
                    "+905551234567")),
            Validator);
        var batch = BulkGiftCardBatch.CreateSynchronous(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            intent,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Now);
        foreach (var item in batch.Items)
        {
            var card = Card(batch, item);
            var invitation = Invitation(batch, item, card.Id);
            item.SetSuccessSources(card, invitation);
            batch.RecordSucceeded(item, Now.AddMinutes(1));
        }

        var result = BulkGiftCardBatchMapping.ToResult(batch);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.TotalItems);
        Assert.Equal(2, result.SucceededItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(
            ["ROW-001", "ROW-002"],
            result.Items.Select(item => item.ItemReference).ToArray());
        Assert.Equal(
            25m,
            Assert.Single(
                result.CurrencyTotals,
                total => total.Currency == "TRY").Amount);
        Assert.Equal(
            25m,
            Assert.Single(
                result.CurrencyTotals,
                total => total.Currency == "USD").Amount);
    }

    [Fact]
    public void Async_intent_accepts_fifteen_hundred_rows_and_persists_pending_intent()
    {
        var items = Enumerable.Range(1, 1_500)
            .Select(index => Item(
                $"ROW-{index:0000}",
                "TRY",
                RecipientContactType.Email,
                $"recipient-{index}@example.com"))
            .ToArray();
        var intent = BulkGiftCardBatchIntent.CreateAsync(
            new CreateBulkGiftCardBatchRequest(
                "PAYROLL-1500",
                "bulk-request-1500",
                items),
            Validator);
        var batch = BulkGiftCardBatch.CreatePending(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            intent,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Now);

        Assert.Equal(BulkGiftCardBatchState.Pending, batch.State);
        Assert.Equal(1_500, batch.TotalItems);
        Assert.All(batch.Items, item =>
        {
            Assert.Equal(BulkGiftCardBatchItemState.Pending, item.State);
            Assert.NotEmpty(item.RecipientContact);
            Assert.NotEmpty(item.IssuanceIdempotencyKey);
            Assert.NotEmpty(item.DistributionIdempotencyKey);
        });
    }

    [Fact]
    public void Mixed_outcomes_complete_with_exact_counts_and_settled_rows_are_immutable()
    {
        var intent = BulkGiftCardBatchIntent.CreateAsync(
            Request(
                Item("ROW-001", "TRY", RecipientContactType.Email, "first@example.com"),
                Item("ROW-002", "TRY", RecipientContactType.Email, "second@example.com")),
            Validator);
        var batch = BulkGiftCardBatch.CreatePending(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            intent,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Now);
        var succeededItem = batch.Items[0];
        var card = Card(batch, succeededItem);
        succeededItem.SetSuccessSources(
            card,
            Invitation(batch, succeededItem, card.Id));
        batch.RecordSucceeded(succeededItem, Now.AddSeconds(1));
        var failedItem = batch.Items[1];
        batch.RecordFailed(
            failedItem,
            "ledger.insufficient_balance",
            "Corporate credit is insufficient.",
            Now.AddSeconds(2));

        Assert.Equal(BulkGiftCardBatchState.Completed, batch.State);
        Assert.Equal(1, batch.SucceededItems);
        Assert.Equal(1, batch.FailedItems);
        Assert.Equal("ledger.insufficient_balance", failedItem.FailureCode);
        Assert.Throws<ConflictException>(() => batch.RecordFailed(
            failedItem,
            "changed",
            "Cannot revise a settled outcome.",
            Now.AddSeconds(3)));
    }

    [Fact]
    public void Retry_intent_contains_only_failed_rows_and_preserves_child_keys()
    {
        var sourceIntent = BulkGiftCardBatchIntent.CreateAsync(
            Request(
                Item("ROW-001", "TRY", RecipientContactType.Email, "first@example.com"),
                Item("ROW-002", "TRY", RecipientContactType.Email, "second@example.com")),
            Validator);
        var source = BulkGiftCardBatch.CreatePending(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            sourceIntent,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Now);
        var succeededItem = source.Items[0];
        var card = Card(source, succeededItem);
        succeededItem.SetSuccessSources(
            card,
            Invitation(source, succeededItem, card.Id));
        source.RecordSucceeded(succeededItem, Now.AddSeconds(1));
        source.RecordFailed(
            source.Items[1],
            "ledger.insufficient_balance",
            "Corporate credit is insufficient.",
            Now.AddSeconds(2));

        var originalFailed = source.Items[1];
        var retryIntent = BulkGiftCardBatchIntent.CreateRetry(
            source,
            [originalFailed.ToIntent()]);

        var retryItem = Assert.Single(retryIntent.Items);
        Assert.Equal(originalFailed.Position, retryItem.Position);
        Assert.Equal(
            originalFailed.IssuanceIdempotencyKey,
            retryItem.Issuance.IdempotencyKey);
        Assert.Equal(
            originalFailed.DistributionIdempotencyKey,
            retryItem.DistributionIdempotencyKey);
    }

    private static CreateBulkGiftCardBatchRequest Request(
        params BulkGiftCardBatchItemRequest[] items) =>
        new(
            " PRESENTATION-BATCH ",
            "bulk-request-001",
            items);

    private static BulkGiftCardBatchItemRequest Item(
        string reference,
        string currency,
        RecipientContactType contactType,
        string contact) =>
        new(
            reference,
            25m,
            currency,
            Now,
            Now.AddYears(1),
            null,
            null,
            contactType,
            contact);

    private static GiftCardResult Card(
        BulkGiftCardBatch batch,
        BulkGiftCardBatchItem item)
    {
        var id = Guid.CreateVersion7();
        return new GiftCardResult(
            id,
            "GC-TEST-" + item.Position,
            batch.FundingOrganizationId,
            batch.IssuingOrganizationId,
            batch.IssuingOrganizationId,
            null,
            "AwaitingClaim",
            "AwaitingClaim",
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            item.Amount,
            item.Currency,
            item.ValidFromUtc,
            item.ExpiresAtUtc,
            false,
            false,
            null,
            id,
            0,
            Guid.CreateVersion7(),
            Now,
            null,
            item.ItemReference,
            item.IssuanceIdempotencyKey,
            batch.CreatedByUserId,
            batch.CreatedByMembershipId,
            null,
            Now);
    }

    private static DistributionInvitationResult Invitation(
        BulkGiftCardBatch batch,
        BulkGiftCardBatchItem item,
        Guid cardId) =>
        new(
            Guid.CreateVersion7(),
            batch.FundingOrganizationId,
            batch.IssuingOrganizationId,
            cardId,
            DistributionInvitationKind.Directed,
            item.ContactType,
            item.MaskedRecipientContact,
            "Pending",
            Now.AddDays(1),
            0,
            item.ItemReference,
            item.DistributionIdempotencyKey,
            batch.CreatedByUserId,
            batch.CreatedByMembershipId,
            null,
            Now,
            null,
            null);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
