using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;

namespace GiftCardPlatform.Modules.Distribution.Domain;

internal enum BulkGiftCardBatchState
{
    Processing = 1,
    Completed = 2,
    Pending = 3,
}

internal enum BulkGiftCardBatchItemState
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
}

internal sealed class BulkGiftCardBatch
{
    private readonly List<BulkGiftCardBatchItem> items = [];

    private BulkGiftCardBatch()
    {
        BatchReference = null!;
        IdempotencyKey = null!;
        IntentHash = null!;
    }

    private BulkGiftCardBatch(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchIntent intent,
        Guid createdByUserId,
        Guid createdByMembershipId,
        DateTimeOffset createdAtUtc,
        BulkGiftCardBatchState initialState,
        Guid? retryOfBatchId)
    {
        Id = id;
        FundingOrganizationId = fundingOrganizationId;
        IssuingOrganizationId = issuingOrganizationId;
        BatchReference = intent.BatchReference;
        IdempotencyKey = intent.IdempotencyKey;
        IntentHash = intent.IntentHash;
        State = initialState;
        TotalItems = intent.Items.Count;
        CreatedByUserId = createdByUserId;
        CreatedByMembershipId = createdByMembershipId;
        CreatedAtUtc = Truncate(createdAtUtc);
        RetryOfBatchId = retryOfBatchId;
        items.AddRange(intent.Items.Select(item => BulkGiftCardBatchItem.CreatePending(
            Guid.CreateVersion7(),
            id,
            fundingOrganizationId,
            issuingOrganizationId,
            item)));
    }

    public Guid Id { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid IssuingOrganizationId { get; private set; }

    public string BatchReference { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string IntentHash { get; private set; }

    public BulkGiftCardBatchState State { get; private set; }

    public int TotalItems { get; private set; }

    public int SucceededItems { get; private set; }

    public int FailedItems { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid CreatedByMembershipId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public Guid? RetryOfBatchId { get; private set; }

    public IReadOnlyList<BulkGiftCardBatchItem> Items => items;

    public static BulkGiftCardBatch CreateSynchronous(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchIntent intent,
        Guid createdByUserId,
        Guid createdByMembershipId,
        DateTimeOffset createdAtUtc) =>
        Create(
            id,
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            createdByUserId,
            createdByMembershipId,
            createdAtUtc,
            BulkGiftCardBatchState.Processing,
            retryOfBatchId: null);

    public static BulkGiftCardBatch CreatePending(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchIntent intent,
        Guid createdByUserId,
        Guid createdByMembershipId,
        DateTimeOffset createdAtUtc,
        Guid? retryOfBatchId = null) =>
        Create(
            id,
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            createdByUserId,
            createdByMembershipId,
            createdAtUtc,
            BulkGiftCardBatchState.Pending,
            retryOfBatchId);

    private static BulkGiftCardBatch Create(
        Guid id,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchIntent intent,
        Guid createdByUserId,
        Guid createdByMembershipId,
        DateTimeOffset createdAtUtc,
        BulkGiftCardBatchState initialState,
        Guid? retryOfBatchId)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (id == Guid.Empty ||
            fundingOrganizationId == Guid.Empty ||
            issuingOrganizationId == Guid.Empty ||
            createdByUserId == Guid.Empty ||
            createdByMembershipId == Guid.Empty ||
            retryOfBatchId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.scope.required",
                "Batch, organization, actor, and membership identifiers are required.");
        }

        return new BulkGiftCardBatch(
            id,
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            createdByUserId,
            createdByMembershipId,
            createdAtUtc.ToUniversalTime(),
            initialState,
            retryOfBatchId);
    }

    public bool Matches(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchIntent intent) =>
        FundingOrganizationId == fundingOrganizationId &&
        IssuingOrganizationId == issuingOrganizationId &&
        BatchReference == intent.BatchReference &&
        IntentHash == intent.IntentHash;

    public void StartProcessing()
    {
        if (State == BulkGiftCardBatchState.Pending)
        {
            State = BulkGiftCardBatchState.Processing;
        }
        else if (State != BulkGiftCardBatchState.Processing)
        {
            throw new ConflictException(
                "bulk.batch.not_processable",
                "The batch is not available for processing.");
        }
    }

    public void RecordSucceeded(BulkGiftCardBatchItem item, DateTimeOffset occurredAtUtc)
    {
        EnsureOwnedPendingItem(item);
        item.MarkSucceeded(occurredAtUtc);
        SucceededItems++;
        CompleteIfSettled(occurredAtUtc);
    }

    public void RecordFailed(
        BulkGiftCardBatchItem item,
        string failureCode,
        string failureMessage,
        DateTimeOffset occurredAtUtc)
    {
        EnsureOwnedPendingItem(item);
        item.MarkFailed(failureCode, failureMessage, occurredAtUtc);
        FailedItems++;
        CompleteIfSettled(occurredAtUtc);
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        if (State == BulkGiftCardBatchState.Completed)
        {
            return;
        }

        if (State != BulkGiftCardBatchState.Processing ||
            SucceededItems + FailedItems != TotalItems)
        {
            throw new ConflictException(
                "bulk.batch.incomplete",
                "The batch cannot complete until every item has an outcome.");
        }

        var completed = Truncate(completedAtUtc);
        if (completed < CreatedAtUtc)
        {
            throw new ValidationFailedException(
                "bulk.completed_at.invalid",
                "Batch completion cannot precede batch creation.");
        }

        State = BulkGiftCardBatchState.Completed;
        CompletedAtUtc = completed;
    }

    private void CompleteIfSettled(DateTimeOffset occurredAtUtc)
    {
        if (SucceededItems + FailedItems == TotalItems)
        {
            Complete(occurredAtUtc);
        }
    }

    private void EnsureOwnedPendingItem(BulkGiftCardBatchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (State is not (BulkGiftCardBatchState.Pending or BulkGiftCardBatchState.Processing) ||
            item.BatchId != Id ||
            item.State != BulkGiftCardBatchItemState.Pending)
        {
            throw new ConflictException(
                "bulk.item.result.invalid",
                "The batch item is not pending in this batch.");
        }

        StartProcessing();
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}

internal sealed class BulkGiftCardBatchItem
{
    private BulkGiftCardBatchItem()
    {
        ItemReference = null!;
        Currency = null!;
        RecipientContact = null!;
        MaskedRecipientContact = null!;
        IssuanceIdempotencyKey = null!;
        DistributionIdempotencyKey = null!;
    }

    private BulkGiftCardBatchItem(
        Guid id,
        Guid batchId,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchItemIntent intent)
    {
        Id = id;
        BatchId = batchId;
        FundingOrganizationId = fundingOrganizationId;
        IssuingOrganizationId = issuingOrganizationId;
        Position = intent.Position;
        ItemReference = intent.ItemReference;
        State = BulkGiftCardBatchItemState.Pending;
        Amount = intent.Issuance.Amount;
        Currency = intent.Issuance.Currency!;
        ValidFromUtc = intent.Issuance.ValidFromUtc!.Value;
        ExpiresAtUtc = intent.Issuance.ExpiresAtUtc!.Value;
        IsTransferable = intent.Issuance.IsTransferable!.Value;
        IsDivisible = intent.Issuance.IsDivisible!.Value;
        ContactType = intent.ContactType;
        RecipientContact = intent.RecipientContact;
        MaskedRecipientContact = intent.MaskedRecipientContact;
        IssuanceIdempotencyKey = intent.Issuance.IdempotencyKey!;
        DistributionIdempotencyKey = intent.DistributionIdempotencyKey;
    }

    public Guid Id { get; private set; }

    public Guid BatchId { get; private set; }

    public Guid FundingOrganizationId { get; private set; }

    public Guid IssuingOrganizationId { get; private set; }

    public int Position { get; private set; }

    public string ItemReference { get; private set; }

    public BulkGiftCardBatchItemState State { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset ValidFromUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public bool IsTransferable { get; private set; }

    public bool IsDivisible { get; private set; }

    public RecipientContactType ContactType { get; private set; }

    internal string RecipientContact { get; private set; }

    public string MaskedRecipientContact { get; private set; }

    public string IssuanceIdempotencyKey { get; private set; }

    public string DistributionIdempotencyKey { get; private set; }

    public Guid? GiftCardId { get; private set; }

    public string? GiftCardPublicReference { get; private set; }

    public Guid? InvitationId { get; private set; }

    public string? GiftCardState { get; private set; }

    public string? InvitationState { get; private set; }

    public DateTimeOffset? DistributedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureMessage { get; private set; }

    public DateTimeOffset? SettledAtUtc { get; private set; }

    public static BulkGiftCardBatchItem CreatePending(
        Guid id,
        Guid batchId,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        BulkGiftCardBatchItemIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (id == Guid.Empty ||
            batchId == Guid.Empty ||
            fundingOrganizationId == Guid.Empty ||
            issuingOrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.item.scope.required",
                "Batch item identifiers are required.");
        }

        return new BulkGiftCardBatchItem(
            id,
            batchId,
            fundingOrganizationId,
            issuingOrganizationId,
            intent);
    }

    public BulkGiftCardBatchItemIntent ToIntent() =>
        new(
            Position,
            ItemReference,
            ToIssuanceRequest(),
            ContactType,
            RecipientContact,
            MaskedRecipientContact,
            DistributionIdempotencyKey);

    public IssueGiftCardRequest ToIssuanceRequest() =>
        new(
            Amount,
            Currency,
            ValidFromUtc,
            ExpiresAtUtc,
            IsTransferable,
            IsDivisible,
            ItemReference,
            IssuanceIdempotencyKey);

    public DistributeGiftCardRequest ToDistributionRequest(Guid giftCardId) =>
        new(
            giftCardId,
            ContactType,
            RecipientContact,
            ItemReference,
            DistributionIdempotencyKey);

    public void SetSuccessSources(
        GiftCardResult giftCard,
        DistributionInvitationResult invitation)
    {
        ArgumentNullException.ThrowIfNull(giftCard);
        ArgumentNullException.ThrowIfNull(invitation);
        if (State != BulkGiftCardBatchItemState.Pending ||
            giftCard.Id != invitation.GiftCardId ||
            giftCard.FundingOrganizationId != FundingOrganizationId ||
            invitation.FundingOrganizationId != FundingOrganizationId ||
            giftCard.IssuingOrganizationId != IssuingOrganizationId ||
            invitation.IssuingOrganizationId != IssuingOrganizationId ||
            !string.Equals(invitation.State, "Pending", StringComparison.Ordinal))
        {
            throw new ConflictException(
                "bulk.item.result.invalid",
                "The batch item did not produce the expected card and invitation result.");
        }

        GiftCardId = giftCard.Id;
        GiftCardPublicReference = giftCard.PublicReference;
        InvitationId = invitation.Id;
        GiftCardState = "AwaitingClaim";
        InvitationState = invitation.State;
        DistributedAtUtc = invitation.DistributedAtUtc;
    }

    internal void MarkSucceeded(DateTimeOffset settledAtUtc)
    {
        if (State != BulkGiftCardBatchItemState.Pending ||
            GiftCardId is null ||
            InvitationId is null ||
            GiftCardPublicReference is null ||
            DistributedAtUtc is null)
        {
            throw new ConflictException(
                "bulk.item.result.invalid",
                "A successful batch item requires card and invitation results.");
        }

        State = BulkGiftCardBatchItemState.Succeeded;
        SettledAtUtc = Truncate(settledAtUtc);
    }

    internal void MarkFailed(
        string failureCode,
        string failureMessage,
        DateTimeOffset settledAtUtc)
    {
        if (State != BulkGiftCardBatchItemState.Pending ||
            string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Length > 160 ||
            string.IsNullOrWhiteSpace(failureMessage) ||
            failureMessage.Length > 500)
        {
            throw new ConflictException(
                "bulk.item.failure.invalid",
                "A failed batch item requires a safe failure code and message.");
        }

        FailureCode = failureCode;
        FailureMessage = failureMessage;
        State = BulkGiftCardBatchItemState.Failed;
        SettledAtUtc = Truncate(settledAtUtc);
    }

    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }
}
