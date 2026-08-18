using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Ledger.Contracts;

namespace GiftCardPlatform.Modules.Ledger.Domain;

internal enum LedgerEntryDirection
{
    Debit = 1,
    Credit = 2,
}

internal sealed class LedgerEntry
{
    private LedgerEntry()
    {
        Currency = null!;
    }

    internal LedgerEntry(
        Guid transactionId,
        Guid organizationId,
        Guid accountId,
        LedgerEntryDirection direction,
        Money money)
    {
        Id = Guid.CreateVersion7();
        TransactionId = transactionId;
        OrganizationId = organizationId;
        AccountId = accountId;
        Direction = direction;
        Amount = money.Amount;
        Currency = money.Currency;
    }

    public Guid Id { get; private set; }

    public Guid TransactionId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid AccountId { get; private set; }

    public LedgerEntryDirection Direction { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }
}

internal sealed class LedgerTransaction
{
    public const int OperationTypeMaxLength = 80;
    public const int BusinessReferenceMaxLength = 120;
    public const int IdempotencyKeyMinLength = 8;
    public const int IdempotencyKeyMaxLength = 128;
    public const string CorporateCreditOperation = "corporate_credit.allocation";
    public const string CorporateCreditReversalOperation = "corporate_credit.reversal";
    public const string GiftCardIssuanceOperation = "gift_card.issuance";
    public const string GiftCardCancellationReturnOperation =
        "gift_card.cancellation_return";
    public const string GiftCardExpirationReturnOperation =
        "gift_card.expiration_return";
    public const string GiftCardShareTransferOperation = "gift_card.share_transfer";
    public const string GiftCardRedemptionOperation = "gift_card.redemption";
    public const string GiftCardRefundOperation = "gift_card.refund";

    private readonly List<LedgerEntry> _entries = [];

    private LedgerTransaction()
    {
        OperationType = null!;
        BusinessReference = null!;
        IdempotencyKey = null!;
        IntentHash = null!;
    }

    private LedgerTransaction(
        Guid id,
        Guid organizationId,
        string operationType,
        string businessReference,
        string idempotencyKey,
        string intentHash,
        Guid? reversesTransactionId,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        OperationType = operationType;
        BusinessReference = businessReference;
        IdempotencyKey = idempotencyKey;
        IntentHash = intentHash;
        ReversesTransactionId = reversesTransactionId;
        InitiatedByUserId = initiatedByUserId;
        PostedAtUtc = TruncateToPostgresPrecision(postedAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string OperationType { get; private set; }

    public string BusinessReference { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string IntentHash { get; private set; }

    public Guid? ReversesTransactionId { get; private set; }

    public Guid InitiatedByUserId { get; private set; }

    public DateTimeOffset PostedAtUtc { get; private set; }

    public IReadOnlyCollection<LedgerEntry> Entries => _entries;

    public static LedgerTransaction CreateCorporateCredit(
        Guid organizationId,
        LedgerAccount platformFundingAccount,
        LedgerAccount organizationAccount,
        Money money,
        string? businessReference,
        string? idempotencyKey,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        if (organizationId == Guid.Empty || initiatedByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.actor_or_scope.required",
                "An organization and initiating user are required.");
        }

        ArgumentNullException.ThrowIfNull(platformFundingAccount);
        ArgumentNullException.ThrowIfNull(organizationAccount);

        if (platformFundingAccount.Type != LedgerAccountType.PlatformFunding ||
            platformFundingAccount.OrganizationId is not null ||
            organizationAccount.Type != LedgerAccountType.OrganizationCorporateCredit ||
            organizationAccount.OrganizationId != organizationId ||
            platformFundingAccount.Currency != money.Currency ||
            organizationAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the financial intent.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var normalizedKey = NormalizeRequired(
            idempotencyKey,
            IdempotencyKeyMaxLength,
            "ledger.idempotency_key");

        if (normalizedKey.Length < IdempotencyKeyMinLength)
        {
            throw new ValidationFailedException(
                "ledger.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and {IdempotencyKeyMaxLength} characters.");
        }

        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            organizationId,
            CorporateCreditOperation,
            normalizedReference,
            normalizedKey,
            ComputeIntentHash(organizationId, money, normalizedReference),
            reversesTransactionId: null,
            initiatedByUserId,
            postedAtUtc.ToUniversalTime());

        transaction._entries.Add(
            new LedgerEntry(
                id,
                organizationId,
                platformFundingAccount.Id,
                LedgerEntryDirection.Debit,
                money));
        transaction._entries.Add(
            new LedgerEntry(
                id,
                organizationId,
                organizationAccount.Id,
                LedgerEntryDirection.Credit,
                money));
        transaction.EnsureBalanced();

        return transaction;
    }

    public static LedgerTransaction CreateCorporateCreditReversal(
        Guid organizationId,
        Guid originalTransactionId,
        LedgerAccount organizationAccount,
        LedgerAccount platformFundingAccount,
        Money money,
        string? businessReference,
        string? idempotencyKey,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        if (organizationId == Guid.Empty ||
            originalTransactionId == Guid.Empty ||
            initiatedByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.reversal.scope.required",
                "An organization, original transaction, and initiating user are required.");
        }

        ArgumentNullException.ThrowIfNull(organizationAccount);
        ArgumentNullException.ThrowIfNull(platformFundingAccount);

        if (organizationAccount.Type != LedgerAccountType.OrganizationCorporateCredit ||
            organizationAccount.OrganizationId != organizationId ||
            platformFundingAccount.Type != LedgerAccountType.PlatformFunding ||
            platformFundingAccount.OrganizationId is not null ||
            organizationAccount.Currency != money.Currency ||
            platformFundingAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the reversal intent.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var normalizedKey = NormalizeRequired(
            idempotencyKey,
            IdempotencyKeyMaxLength,
            "ledger.idempotency_key");

        if (normalizedKey.Length < IdempotencyKeyMinLength)
        {
            throw new ValidationFailedException(
                "ledger.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and {IdempotencyKeyMaxLength} characters.");
        }

        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            organizationId,
            CorporateCreditReversalOperation,
            normalizedReference,
            normalizedKey,
            ComputeReversalIntentHash(
                organizationId,
                originalTransactionId,
                money,
                normalizedReference),
            originalTransactionId,
            initiatedByUserId,
            postedAtUtc.ToUniversalTime());

        transaction._entries.Add(
            new LedgerEntry(
                id,
                organizationId,
                organizationAccount.Id,
                LedgerEntryDirection.Debit,
                money));
        transaction._entries.Add(
            new LedgerEntry(
                id,
                organizationId,
                platformFundingAccount.Id,
                LedgerEntryDirection.Credit,
                money));
        transaction.EnsureBalanced();

        return transaction;
    }

    public static LedgerTransaction CreateGiftCardIssuance(
        Guid fundingOrganizationId,
        Guid giftCardId,
        LedgerAccount organizationAccount,
        LedgerAccount giftCardAccount,
        Money money,
        string? businessReference,
        string? idempotencyKey,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        if (fundingOrganizationId == Guid.Empty ||
            giftCardId == Guid.Empty ||
            initiatedByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card.scope.required",
                "A funding organization, gift card, and initiating user are required.");
        }

        ArgumentNullException.ThrowIfNull(organizationAccount);
        ArgumentNullException.ThrowIfNull(giftCardAccount);

        if (organizationAccount.Type != LedgerAccountType.OrganizationCorporateCredit ||
            organizationAccount.OrganizationId != fundingOrganizationId ||
            organizationAccount.GiftCardId is not null ||
            giftCardAccount.Type != LedgerAccountType.GiftCardValue ||
            giftCardAccount.OrganizationId != fundingOrganizationId ||
            giftCardAccount.GiftCardId != giftCardId ||
            organizationAccount.Currency != money.Currency ||
            giftCardAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the gift-card issuance intent.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var normalizedKey = NormalizeRequired(
            idempotencyKey,
            IdempotencyKeyMaxLength,
            "ledger.idempotency_key");
        if (normalizedKey.Length < IdempotencyKeyMinLength)
        {
            throw new ValidationFailedException(
                "ledger.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and {IdempotencyKeyMaxLength} characters.");
        }

        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            fundingOrganizationId,
            GiftCardIssuanceOperation,
            normalizedReference,
            normalizedKey,
            ComputeGiftCardIssuanceIntentHash(
                fundingOrganizationId,
                giftCardId,
                money,
                normalizedReference),
            reversesTransactionId: null,
            initiatedByUserId,
            postedAtUtc.ToUniversalTime());

        transaction._entries.Add(
            new LedgerEntry(
                id,
                fundingOrganizationId,
                organizationAccount.Id,
                LedgerEntryDirection.Debit,
                money));
        transaction._entries.Add(
            new LedgerEntry(
                id,
                fundingOrganizationId,
                giftCardAccount.Id,
                LedgerEntryDirection.Credit,
                money));
        transaction.EnsureBalanced();

        return transaction;
    }

    public static LedgerTransaction CreateGiftCardValueReturn(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid issuanceTransactionId,
        LedgerAccount giftCardAccount,
        LedgerAccount organizationAccount,
        Money money,
        GiftCardValueReturnReason reason,
        string? businessReference,
        string? idempotencyKey,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        if (fundingOrganizationId == Guid.Empty ||
            giftCardId == Guid.Empty ||
            issuanceTransactionId == Guid.Empty ||
            initiatedByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card_return.scope.required",
                "Funding organization, gift card, issuance, and actor are required.");
        }

        ArgumentNullException.ThrowIfNull(giftCardAccount);
        ArgumentNullException.ThrowIfNull(organizationAccount);
        if (!Enum.IsDefined(reason))
        {
            throw new ValidationFailedException(
                "ledger.gift_card_return.reason.invalid",
                "The gift-card value-return reason is invalid.");
        }

        if (giftCardAccount.Type != LedgerAccountType.GiftCardValue ||
            giftCardAccount.OrganizationId != fundingOrganizationId ||
            giftCardAccount.GiftCardId != giftCardId ||
            organizationAccount.Type != LedgerAccountType.OrganizationCorporateCredit ||
            organizationAccount.OrganizationId != fundingOrganizationId ||
            organizationAccount.GiftCardId is not null ||
            giftCardAccount.Currency != money.Currency ||
            organizationAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the gift-card value return.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var normalizedKey = NormalizeRequired(
            idempotencyKey,
            IdempotencyKeyMaxLength,
            "ledger.idempotency_key");
        if (normalizedKey.Length < IdempotencyKeyMinLength)
        {
            throw new ValidationFailedException(
                "ledger.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and " +
                $"{IdempotencyKeyMaxLength} characters.");
        }

        var operationType = OperationFor(reason);
        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            fundingOrganizationId,
            operationType,
            normalizedReference,
            normalizedKey,
            ComputeGiftCardValueReturnIntentHash(
                operationType,
                fundingOrganizationId,
                giftCardId,
                issuanceTransactionId,
                money,
                normalizedReference),
            issuanceTransactionId,
            initiatedByUserId,
            postedAtUtc.ToUniversalTime());

        transaction._entries.Add(
            new LedgerEntry(
                id,
                fundingOrganizationId,
                giftCardAccount.Id,
                LedgerEntryDirection.Debit,
                money));
        transaction._entries.Add(
            new LedgerEntry(
                id,
                fundingOrganizationId,
                organizationAccount.Id,
                LedgerEntryDirection.Credit,
                money));
        transaction.EnsureBalanced();
        return transaction;
    }

    public static LedgerTransaction CreateGiftCardShareTransfer(
        Guid transactionId,
        Guid fundingOrganizationId,
        Guid sourceGiftCardId,
        Guid childGiftCardId,
        LedgerAccount sourceAccount,
        LedgerAccount childAccount,
        Money money,
        string? businessReference,
        string? idempotencyKey,
        Guid initiatedByUserId,
        DateTimeOffset postedAtUtc)
    {
        if (transactionId == Guid.Empty || fundingOrganizationId == Guid.Empty ||
            sourceGiftCardId == Guid.Empty || childGiftCardId == Guid.Empty ||
            initiatedByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card_share.scope.required",
                "Transfer, organization, source, child, and actor identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(sourceAccount);
        ArgumentNullException.ThrowIfNull(childAccount);
        if (sourceAccount.Type != LedgerAccountType.GiftCardValue ||
            sourceAccount.OrganizationId != fundingOrganizationId ||
            sourceAccount.GiftCardId != sourceGiftCardId ||
            childAccount.Type != LedgerAccountType.GiftCardValue ||
            childAccount.OrganizationId != fundingOrganizationId ||
            childAccount.GiftCardId != childGiftCardId ||
            sourceAccount.Currency != money.Currency || childAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the gift-card share transfer.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var normalizedKey = NormalizeRequired(
            idempotencyKey,
            IdempotencyKeyMaxLength,
            "ledger.idempotency_key");
        if (normalizedKey.Length < IdempotencyKeyMinLength)
        {
            throw new ValidationFailedException(
                "ledger.idempotency_key.invalid_length",
                $"Idempotency key must be between {IdempotencyKeyMinLength} and {IdempotencyKeyMaxLength} characters.");
        }

        var transaction = new LedgerTransaction(
            transactionId,
            fundingOrganizationId,
            GiftCardShareTransferOperation,
            normalizedReference,
            normalizedKey,
            ComputeGiftCardShareTransferIntentHash(
                fundingOrganizationId,
                sourceGiftCardId,
                childGiftCardId,
                money,
                normalizedReference),
            reversesTransactionId: null,
            initiatedByUserId,
            postedAtUtc);
        transaction._entries.Add(new LedgerEntry(
            transactionId,
            fundingOrganizationId,
            sourceAccount.Id,
            LedgerEntryDirection.Debit,
            money));
        transaction._entries.Add(new LedgerEntry(
            transactionId,
            fundingOrganizationId,
            childAccount.Id,
            LedgerEntryDirection.Credit,
            money));
        transaction.EnsureBalanced();
        return transaction;
    }

    public static LedgerTransaction CreateGiftCardRedemption(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid paymentTokenId,
        Guid provisionId,
        LedgerAccount giftCardAccount,
        LedgerAccount settlementAccount,
        Money money,
        string? businessReference,
        Guid initiatedByActorId,
        DateTimeOffset postedAtUtc)
    {
        if (fundingOrganizationId == Guid.Empty || giftCardId == Guid.Empty ||
            paymentTokenId == Guid.Empty || provisionId == Guid.Empty ||
            initiatedByActorId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card_redemption.scope.required",
                "Organization, card, credential, provision, and actor identifiers are required.");
        }

        ArgumentNullException.ThrowIfNull(giftCardAccount);
        ArgumentNullException.ThrowIfNull(settlementAccount);
        if (giftCardAccount.Type != LedgerAccountType.GiftCardValue ||
            giftCardAccount.OrganizationId != fundingOrganizationId ||
            giftCardAccount.GiftCardId != giftCardId ||
            settlementAccount.Type != LedgerAccountType.PlatformRedemptionSettlement ||
            settlementAccount.OrganizationId is not null ||
            settlementAccount.GiftCardId is not null ||
            giftCardAccount.Currency != money.Currency ||
            settlementAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the gift-card redemption.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference,
            BusinessReferenceMaxLength,
            "ledger.business_reference");
        var idempotencyKey = $"payment-token:{paymentTokenId:N}";
        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            fundingOrganizationId,
            GiftCardRedemptionOperation,
            normalizedReference,
            idempotencyKey,
            ComputeGiftCardRedemptionIntentHash(
                fundingOrganizationId,
                giftCardId,
                paymentTokenId,
                provisionId,
                money,
                normalizedReference),
            reversesTransactionId: null,
            initiatedByActorId,
            postedAtUtc);
        transaction._entries.Add(new LedgerEntry(
            id,
            fundingOrganizationId,
            giftCardAccount.Id,
            LedgerEntryDirection.Debit,
            money));
        transaction._entries.Add(new LedgerEntry(
            id,
            fundingOrganizationId,
            settlementAccount.Id,
            LedgerEntryDirection.Credit,
            money));
        transaction.EnsureBalanced();
        return transaction;
    }

    public static LedgerTransaction CreateGiftCardRefund(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid provisionId,
        Guid refundId,
        Guid originalRedemptionTransactionId,
        LedgerAccount settlementAccount,
        LedgerAccount giftCardAccount,
        Money money,
        string? businessReference,
        Guid initiatedByActorId,
        DateTimeOffset postedAtUtc)
    {
        if (fundingOrganizationId == Guid.Empty || giftCardId == Guid.Empty ||
            provisionId == Guid.Empty || refundId == Guid.Empty ||
            originalRedemptionTransactionId == Guid.Empty || initiatedByActorId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card_refund.scope.required",
                "Organization, card, provision, refund, redemption, and actor identifiers are required.");
        }
        ArgumentNullException.ThrowIfNull(settlementAccount);
        ArgumentNullException.ThrowIfNull(giftCardAccount);
        if (settlementAccount.Type != LedgerAccountType.PlatformRedemptionSettlement ||
            settlementAccount.OrganizationId is not null || settlementAccount.GiftCardId is not null ||
            giftCardAccount.Type != LedgerAccountType.GiftCardValue ||
            giftCardAccount.OrganizationId != fundingOrganizationId ||
            giftCardAccount.GiftCardId != giftCardId ||
            settlementAccount.Currency != money.Currency || giftCardAccount.Currency != money.Currency)
        {
            throw new ValidationFailedException(
                "ledger.accounts.invalid",
                "The selected ledger accounts do not match the gift-card refund.");
        }

        var normalizedReference = NormalizeRequired(
            businessReference, BusinessReferenceMaxLength, "ledger.business_reference");
        var id = Guid.CreateVersion7();
        var transaction = new LedgerTransaction(
            id,
            fundingOrganizationId,
            GiftCardRefundOperation,
            normalizedReference,
            $"payment-refund:{refundId:N}",
            ComputeGiftCardRefundIntentHash(
                fundingOrganizationId, giftCardId, provisionId, refundId,
                originalRedemptionTransactionId, money, normalizedReference),
            reversesTransactionId: null,
            initiatedByActorId,
            postedAtUtc);
        transaction._entries.Add(new LedgerEntry(
            id, fundingOrganizationId, settlementAccount.Id, LedgerEntryDirection.Debit, money));
        transaction._entries.Add(new LedgerEntry(
            id, fundingOrganizationId, giftCardAccount.Id, LedgerEntryDirection.Credit, money));
        transaction.EnsureBalanced();
        return transaction;
    }

    public bool MatchesCorporateCreditIntent(
        Guid organizationId,
        Money money,
        string? businessReference) =>
        OperationType == CorporateCreditOperation &&
        IntentHash == ComputeIntentHash(
            organizationId,
            money,
            NormalizeRequired(
                businessReference,
                BusinessReferenceMaxLength,
                "ledger.business_reference"));

    public bool MatchesCorporateCreditReversalIntent(
        Guid organizationId,
        Guid originalTransactionId,
        Money money,
        string? businessReference) =>
        OperationType == CorporateCreditReversalOperation &&
        ReversesTransactionId == originalTransactionId &&
        IntentHash == ComputeReversalIntentHash(
            organizationId,
            originalTransactionId,
            money,
            NormalizeRequired(
                businessReference,
                BusinessReferenceMaxLength,
                "ledger.business_reference"));

    public bool MatchesGiftCardIssuanceIntent(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Money money,
        string? businessReference) =>
        OperationType == GiftCardIssuanceOperation &&
        IntentHash == ComputeGiftCardIssuanceIntentHash(
            fundingOrganizationId,
            giftCardId,
            money,
            NormalizeRequired(
                businessReference,
                BusinessReferenceMaxLength,
                "ledger.business_reference"));

    public bool MatchesGiftCardValueReturnIntent(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid issuanceTransactionId,
        Money money,
        GiftCardValueReturnReason reason,
        string? businessReference)
    {
        var operationType = OperationFor(reason);
        return OperationType == operationType &&
            ReversesTransactionId == issuanceTransactionId &&
            IntentHash == ComputeGiftCardValueReturnIntentHash(
                operationType,
                fundingOrganizationId,
                giftCardId,
                issuanceTransactionId,
                money,
                NormalizeRequired(
                    businessReference,
                    BusinessReferenceMaxLength,
                    "ledger.business_reference"));
    }

    public bool MatchesGiftCardShareTransferIntent(
        Guid fundingOrganizationId,
        Guid sourceGiftCardId,
        Guid childGiftCardId,
        Money money,
        string? businessReference) =>
        OperationType == GiftCardShareTransferOperation &&
        IntentHash == ComputeGiftCardShareTransferIntentHash(
            fundingOrganizationId,
            sourceGiftCardId,
            childGiftCardId,
            money,
            NormalizeRequired(
                businessReference,
                BusinessReferenceMaxLength,
                "ledger.business_reference"));

    public bool MatchesGiftCardRedemptionIntent(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid paymentTokenId,
        Guid provisionId,
        Money money,
        string? businessReference) =>
        OperationType == GiftCardRedemptionOperation &&
        IdempotencyKey == $"payment-token:{paymentTokenId:N}" &&
        IntentHash == ComputeGiftCardRedemptionIntentHash(
            fundingOrganizationId,
            giftCardId,
            paymentTokenId,
            provisionId,
            money,
            NormalizeRequired(
                businessReference,
                BusinessReferenceMaxLength,
                "ledger.business_reference"));

    public bool MatchesGiftCardRefundIntent(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid provisionId,
        Guid refundId,
        Guid originalRedemptionTransactionId,
        Money money,
        string? businessReference) =>
        OperationType == GiftCardRefundOperation &&
        IdempotencyKey == $"payment-refund:{refundId:N}" &&
        IntentHash == ComputeGiftCardRefundIntentHash(
            fundingOrganizationId, giftCardId, provisionId, refundId,
            originalRedemptionTransactionId, money,
            NormalizeRequired(businessReference, BusinessReferenceMaxLength, "ledger.business_reference"));

    internal void EnsureBalanced()
    {
        if (_entries.Count < 2 ||
            !_entries.Any(entry => entry.Direction == LedgerEntryDirection.Debit) ||
            !_entries.Any(entry => entry.Direction == LedgerEntryDirection.Credit))
        {
            throw new ValidationFailedException(
                "ledger.transaction.unbalanced",
                "A ledger transaction requires at least one debit and one credit.");
        }

        foreach (var currencyEntries in _entries.GroupBy(entry => entry.Currency, StringComparer.Ordinal))
        {
            var debits = currencyEntries
                .Where(entry => entry.Direction == LedgerEntryDirection.Debit)
                .Sum(entry => entry.Amount);
            var credits = currencyEntries
                .Where(entry => entry.Direction == LedgerEntryDirection.Credit)
                .Sum(entry => entry.Amount);

            if (debits != credits)
            {
                throw new ValidationFailedException(
                    "ledger.transaction.unbalanced",
                    $"Ledger debits and credits must balance for {currencyEntries.Key}.");
            }
        }
    }

    private static string NormalizeRequired(string? value, int maxLength, string errorPrefix)
    {
        var normalized = value?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new ValidationFailedException($"{errorPrefix}.required", "A value is required.");
        }

        if (normalized.Length > maxLength)
        {
            throw new ValidationFailedException(
                $"{errorPrefix}.invalid_length",
                $"Value must not exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static string ComputeIntentHash(Guid organizationId, Money money, string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{CorporateCreditOperation}|{organizationId:D}|{canonicalAmount}|{money.Currency}|{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeReversalIntentHash(
        Guid organizationId,
        Guid originalTransactionId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{CorporateCreditReversalOperation}|{organizationId:D}|{originalTransactionId:D}|" +
            $"{canonicalAmount}|{money.Currency}|{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeGiftCardIssuanceIntentHash(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{GiftCardIssuanceOperation}|{fundingOrganizationId:D}|{giftCardId:D}|" +
            $"{canonicalAmount}|{money.Currency}|{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeGiftCardValueReturnIntentHash(
        string operationType,
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid issuanceTransactionId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{operationType}|{fundingOrganizationId:D}|{giftCardId:D}|" +
            $"{issuanceTransactionId:D}|{canonicalAmount}|{money.Currency}|" +
            $"{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeGiftCardShareTransferIntentHash(
        Guid fundingOrganizationId,
        Guid sourceGiftCardId,
        Guid childGiftCardId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{GiftCardShareTransferOperation}|{fundingOrganizationId:D}|{sourceGiftCardId:D}|" +
            $"{childGiftCardId:D}|{canonicalAmount}|{money.Currency}|{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeGiftCardRedemptionIntentHash(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid paymentTokenId,
        Guid provisionId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{GiftCardRedemptionOperation}|{fundingOrganizationId:D}|{giftCardId:D}|" +
            $"{paymentTokenId:D}|{provisionId:D}|{canonicalAmount}|{money.Currency}|" +
            $"{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeGiftCardRefundIntentHash(
        Guid fundingOrganizationId,
        Guid giftCardId,
        Guid provisionId,
        Guid refundId,
        Guid originalRedemptionTransactionId,
        Money money,
        string businessReference)
    {
        var canonicalAmount = money.Amount.ToString(
            "G29", System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{GiftCardRefundOperation}|{fundingOrganizationId:D}|{giftCardId:D}|" +
            $"{provisionId:D}|{refundId:D}|{originalRedemptionTransactionId:D}|" +
            $"{canonicalAmount}|{money.Currency}|{businessReference}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string OperationFor(GiftCardValueReturnReason reason) =>
        reason switch
        {
            GiftCardValueReturnReason.Cancellation =>
                GiftCardCancellationReturnOperation,
            GiftCardValueReturnReason.Expiration =>
                GiftCardExpirationReturnOperation,
            _ => throw new ValidationFailedException(
                "ledger.gift_card_return.reason.invalid",
                "The gift-card value-return reason is invalid."),
        };

    private static DateTimeOffset TruncateToPostgresPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
