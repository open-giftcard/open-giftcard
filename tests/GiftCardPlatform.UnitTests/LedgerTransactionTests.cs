using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Domain;

namespace GiftCardPlatform.UnitTests;

public sealed class LedgerTransactionTests
{
    [Fact]
    public void Corporate_credit_creates_one_balanced_debit_and_credit()
    {
        var organizationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var source = LedgerAccount.CreatePlatformFunding("TRY", now);
        var destination = LedgerAccount.CreateOrganizationCorporateCredit(
            organizationId,
            "TRY",
            now);

        var transaction = LedgerTransaction.CreateCorporateCredit(
            organizationId,
            source,
            destination,
            Money.Create(250m, "TRY"),
            "CONTRACT-42",
            "allocation-contract-42",
            Guid.CreateVersion7(),
            now);

        Assert.Equal(LedgerTransaction.CorporateCreditOperation, transaction.OperationType);
        Assert.Equal(2, transaction.Entries.Count);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == source.Id &&
                entry.Direction == LedgerEntryDirection.Debit &&
                entry.Amount == 250m);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == destination.Id &&
                entry.Direction == LedgerEntryDirection.Credit &&
                entry.Amount == 250m);
    }

    [Fact]
    public void Idempotency_intent_matches_only_the_same_financial_request()
    {
        var organizationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var transaction = LedgerTransaction.CreateCorporateCredit(
            organizationId,
            LedgerAccount.CreatePlatformFunding("TRY", now),
            LedgerAccount.CreateOrganizationCorporateCredit(organizationId, "TRY", now),
            Money.Create(10m, "TRY"),
            "REFERENCE-1",
            "allocation-reference-1",
            Guid.CreateVersion7(),
            now);

        Assert.True(transaction.MatchesCorporateCreditIntent(
            organizationId,
            Money.Create(10m, "TRY"),
            "REFERENCE-1"));
        Assert.True(transaction.MatchesCorporateCreditIntent(
            organizationId,
            Money.Create(10.0000m, "TRY"),
            "REFERENCE-1"));
        Assert.False(transaction.MatchesCorporateCreditIntent(
            organizationId,
            Money.Create(11m, "TRY"),
            "REFERENCE-1"));
    }

    [Fact]
    public void Organization_account_must_belong_to_the_recipient()
    {
        var organizationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<ValidationFailedException>(() =>
            LedgerTransaction.CreateCorporateCredit(
                organizationId,
                LedgerAccount.CreatePlatformFunding("TRY", now),
                LedgerAccount.CreateOrganizationCorporateCredit(Guid.CreateVersion7(), "TRY", now),
                Money.Create(10m, "TRY"),
                "REFERENCE-1",
                "allocation-reference-1",
                Guid.CreateVersion7(),
                now));

        Assert.Equal("ledger.accounts.invalid", exception.Code);
    }

    [Fact]
    public void Idempotency_key_has_a_minimum_length()
    {
        var organizationId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<ValidationFailedException>(() =>
            LedgerTransaction.CreateCorporateCredit(
                organizationId,
                LedgerAccount.CreatePlatformFunding("TRY", now),
                LedgerAccount.CreateOrganizationCorporateCredit(organizationId, "TRY", now),
                Money.Create(10m, "TRY"),
                "REFERENCE-1",
                "short",
                Guid.CreateVersion7(),
                now));

        Assert.Equal("ledger.idempotency_key.invalid_length", exception.Code);
    }

    [Fact]
    public void Posted_time_is_stable_at_postgresql_microsecond_precision()
    {
        var organizationId = Guid.CreateVersion7();
        var timeWithSubMicrosecondTicks = new DateTimeOffset(
            638_000_000_000_000_007,
            TimeSpan.Zero);

        var transaction = LedgerTransaction.CreateCorporateCredit(
            organizationId,
            LedgerAccount.CreatePlatformFunding("TRY", timeWithSubMicrosecondTicks),
            LedgerAccount.CreateOrganizationCorporateCredit(
                organizationId,
                "TRY",
                timeWithSubMicrosecondTicks),
            Money.Create(10m, "TRY"),
            "REFERENCE-1",
            "allocation-reference-1",
            Guid.CreateVersion7(),
            timeWithSubMicrosecondTicks);

        Assert.Equal(0, transaction.PostedAtUtc.Ticks % 10);
    }

    [Fact]
    public void Corporate_credit_reversal_posts_the_exact_opposite_directions()
    {
        var organizationId = Guid.CreateVersion7();
        var originalTransactionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var organizationAccount = LedgerAccount.CreateOrganizationCorporateCredit(
            organizationId,
            "TRY",
            now);
        var platformAccount = LedgerAccount.CreatePlatformFunding("TRY", now);

        var reversal = LedgerTransaction.CreateCorporateCreditReversal(
            organizationId,
            originalTransactionId,
            organizationAccount,
            platformAccount,
            Money.Create(250m, "TRY"),
            "REVERSAL-42",
            "reversal-contract-42",
            Guid.CreateVersion7(),
            now);

        Assert.Equal(LedgerTransaction.CorporateCreditReversalOperation, reversal.OperationType);
        Assert.Equal(originalTransactionId, reversal.ReversesTransactionId);
        Assert.Contains(
            reversal.Entries,
            entry =>
                entry.AccountId == organizationAccount.Id &&
                entry.Direction == LedgerEntryDirection.Debit &&
                entry.Amount == 250m);
        Assert.Contains(
            reversal.Entries,
            entry =>
                entry.AccountId == platformAccount.Id &&
                entry.Direction == LedgerEntryDirection.Credit &&
                entry.Amount == 250m);
    }

    [Fact]
    public void Reversal_intent_matches_only_the_same_original_financial_effect()
    {
        var organizationId = Guid.CreateVersion7();
        var originalTransactionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var reversal = LedgerTransaction.CreateCorporateCreditReversal(
            organizationId,
            originalTransactionId,
            LedgerAccount.CreateOrganizationCorporateCredit(organizationId, "TRY", now),
            LedgerAccount.CreatePlatformFunding("TRY", now),
            Money.Create(25m, "TRY"),
            "REVERSAL-25",
            "reversal-contract-25",
            Guid.CreateVersion7(),
            now);

        Assert.True(reversal.MatchesCorporateCreditReversalIntent(
            organizationId,
            originalTransactionId,
            Money.Create(25m, "TRY"),
            "REVERSAL-25"));
        Assert.False(reversal.MatchesCorporateCreditReversalIntent(
            organizationId,
            Guid.CreateVersion7(),
            Money.Create(25m, "TRY"),
            "REVERSAL-25"));
    }

    [Fact]
    public void Gift_card_issuance_moves_value_from_corporate_credit_into_one_card_account()
    {
        var organizationId = Guid.CreateVersion7();
        var giftCardId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var corporateAccount = LedgerAccount.CreateOrganizationCorporateCredit(
            organizationId,
            "TRY",
            now);
        var giftCardAccount = LedgerAccount.CreateGiftCardValue(
            organizationId,
            giftCardId,
            "TRY",
            now);
        var money = Money.Create(75.50m, "TRY");

        var transaction = LedgerTransaction.CreateGiftCardIssuance(
            organizationId,
            giftCardId,
            corporateAccount,
            giftCardAccount,
            money,
            "AWARD-75",
            "giftcard-ledger-award-75",
            Guid.CreateVersion7(),
            now);

        Assert.Equal(LedgerTransaction.GiftCardIssuanceOperation, transaction.OperationType);
        Assert.Equal(2, transaction.Entries.Count);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == corporateAccount.Id &&
                entry.Direction == LedgerEntryDirection.Debit &&
                entry.Amount == money.Amount);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == giftCardAccount.Id &&
                entry.Direction == LedgerEntryDirection.Credit &&
                entry.Amount == money.Amount);
        Assert.True(
            transaction.MatchesGiftCardIssuanceIntent(
                organizationId,
                giftCardId,
                money,
                "AWARD-75"));
        Assert.False(
            transaction.MatchesGiftCardIssuanceIntent(
                organizationId,
                Guid.CreateVersion7(),
                money,
                "AWARD-75"));
    }

    [Theory]
    [InlineData(
        GiftCardValueReturnReason.Cancellation,
        LedgerTransaction.GiftCardCancellationReturnOperation)]
    [InlineData(
        GiftCardValueReturnReason.Expiration,
        LedgerTransaction.GiftCardExpirationReturnOperation)]
    public void Gift_card_value_return_moves_exact_balance_back_to_corporate_credit(
        GiftCardValueReturnReason reason,
        string expectedOperation)
    {
        var organizationId = Guid.CreateVersion7();
        var giftCardId = Guid.CreateVersion7();
        var issuanceTransactionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var giftCardAccount = LedgerAccount.CreateGiftCardValue(
            organizationId,
            giftCardId,
            "TRY",
            now);
        var corporateAccount = LedgerAccount.CreateOrganizationCorporateCredit(
            organizationId,
            "TRY",
            now);
        var remainingValue = Money.Create(37.25m, "TRY");

        var transaction = LedgerTransaction.CreateGiftCardValueReturn(
            organizationId,
            giftCardId,
            issuanceTransactionId,
            giftCardAccount,
            corporateAccount,
            remainingValue,
            reason,
            "GC-RETURN-42",
            "gift-card-return-42",
            Guid.CreateVersion7(),
            now);

        Assert.Equal(expectedOperation, transaction.OperationType);
        Assert.Equal(issuanceTransactionId, transaction.ReversesTransactionId);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == giftCardAccount.Id &&
                entry.Direction == LedgerEntryDirection.Debit &&
                entry.Amount == remainingValue.Amount);
        Assert.Contains(
            transaction.Entries,
            entry =>
                entry.AccountId == corporateAccount.Id &&
                entry.Direction == LedgerEntryDirection.Credit &&
                entry.Amount == remainingValue.Amount);
        Assert.True(
            transaction.MatchesGiftCardValueReturnIntent(
                organizationId,
                giftCardId,
                issuanceTransactionId,
                remainingValue,
                reason,
                "GC-RETURN-42"));
    }

    [Fact]
    public void Redemption_debits_the_card_and_credits_platform_settlement()
    {
        var organizationId = Guid.CreateVersion7();
        var giftCardId = Guid.CreateVersion7();
        var paymentTokenId = Guid.CreateVersion7();
        var provisionId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var cardAccount = LedgerAccount.CreateGiftCardValue(
            organizationId,
            giftCardId,
            "TRY",
            now);
        var settlement = LedgerAccount.CreatePlatformRedemptionSettlement("TRY", now);
        var money = Money.Create(42.50m, "TRY");

        var redemption = LedgerTransaction.CreateGiftCardRedemption(
            organizationId,
            giftCardId,
            paymentTokenId,
            provisionId,
            cardAccount,
            settlement,
            money,
            "SALE-42",
            actorId,
            now);

        Assert.Equal(LedgerTransaction.GiftCardRedemptionOperation, redemption.OperationType);
        Assert.Equal($"payment-token:{paymentTokenId:N}", redemption.IdempotencyKey);
        Assert.Contains(
            redemption.Entries,
            entry => entry.AccountId == cardAccount.Id &&
                entry.Direction == LedgerEntryDirection.Debit &&
                entry.Amount == money.Amount);
        Assert.Contains(
            redemption.Entries,
            entry => entry.AccountId == settlement.Id &&
                entry.Direction == LedgerEntryDirection.Credit &&
                entry.Amount == money.Amount);
        Assert.True(redemption.MatchesGiftCardRedemptionIntent(
            organizationId,
            giftCardId,
            paymentTokenId,
            provisionId,
            money,
            "SALE-42"));
        Assert.False(redemption.MatchesGiftCardRedemptionIntent(
            organizationId,
            giftCardId,
            paymentTokenId,
            Guid.CreateVersion7(),
            money,
            "SALE-42"));
    }

    [Fact]
    public void Refund_debits_platform_settlement_and_credits_the_card()
    {
        var organizationId = Guid.CreateVersion7();
        var giftCardId = Guid.CreateVersion7();
        var provisionId = Guid.CreateVersion7();
        var refundId = Guid.CreateVersion7();
        var redemptionId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var cardAccount = LedgerAccount.CreateGiftCardValue(
            organizationId, giftCardId, "TRY", now);
        var settlement = LedgerAccount.CreatePlatformRedemptionSettlement("TRY", now);
        var money = Money.Create(12.50m, "TRY");

        var refund = LedgerTransaction.CreateGiftCardRefund(
            organizationId, giftCardId, provisionId, refundId, redemptionId,
            settlement, cardAccount, money, "RETURN-42", actorId, now);

        Assert.Equal(LedgerTransaction.GiftCardRefundOperation, refund.OperationType);
        Assert.Equal($"payment-refund:{refundId:N}", refund.IdempotencyKey);
        Assert.Contains(refund.Entries, entry => entry.AccountId == settlement.Id &&
            entry.Direction == LedgerEntryDirection.Debit && entry.Amount == money.Amount);
        Assert.Contains(refund.Entries, entry => entry.AccountId == cardAccount.Id &&
            entry.Direction == LedgerEntryDirection.Credit && entry.Amount == money.Amount);
        Assert.True(refund.MatchesGiftCardRefundIntent(
            organizationId, giftCardId, provisionId, refundId, redemptionId,
            money, "RETURN-42"));
        Assert.False(refund.MatchesGiftCardRefundIntent(
            organizationId, giftCardId, provisionId, Guid.CreateVersion7(), redemptionId,
            money, "RETURN-42"));
    }
}
