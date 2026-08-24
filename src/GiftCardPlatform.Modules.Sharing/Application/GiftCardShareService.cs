using System.Data;
using System.Globalization;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Notifications.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Sharing.Contracts;
using GiftCardPlatform.Modules.Sharing.Domain;
using GiftCardPlatform.Modules.Sharing.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Sharing.Application;

internal sealed record PreparedDirectGiftCardShare(
    CreatedDirectGiftCardShareResult Result,
    DirectGiftCardShareNotification? Notification);

internal sealed class GiftCardShareService(
    SharingDbContext dbContext,
    IGiftCardSharingWriter giftCardSharingWriter,
    IGiftCardShareLedger ledger,
    IRecipientContactService recipientContactService,
    IRecipientIdentityService recipientIdentityService,
    IRecipientClaimSessionIssuer recipientClaimSessionIssuer,
    IDirectGiftCardShareNotifier directNotifier,
    INotificationChannelAvailability notificationChannels,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    ISessionContextWriter sessionContextWriter,
    MutableExecutionContext executionContext,
    IPaymentReservationQuery paymentReservations,
    IOptions<SharingOptions> options,
    TimeProvider timeProvider) :
    IProtectedGiftCardShareService,
    IDirectGiftCardShareService,
    IShareReservationQuery,
    IShareExpirationProcessor,
    IShareLifecycleWriter
{
    private readonly SharingOptions settings = options.Value;

    public Task<CreatedGiftCardShareResult> CreateAsync(
        Guid sourceGiftCardId,
        CreateGiftCardShareRequest request,
        CancellationToken cancellationToken) =>
        TranslateConcurrencyAsync(
            () => CreateCoreAsync(sourceGiftCardId, request, cancellationToken));

    private async Task<CreatedGiftCardShareResult> CreateCoreAsync(
        Guid sourceGiftCardId,
        CreateGiftCardShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var senderUserId = RequireCardholder();
        var idempotencyKey = GiftCardShare.NormalizeIdempotencyKey(
            request.IdempotencyKey,
            "sharing.create.idempotency_key");

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var source = await giftCardSharingWriter
            .GetOwnedSourceAsync(sourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var lockedBalance = await ledger
            .GetLockedBalanceAsync(sourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(source.Currency, lockedBalance.Currency, StringComparison.Ordinal))
        {
            throw new ConflictException(
                "sharing.balance.currency_mismatch",
                "The card and Ledger currencies do not match.");
        }

        var existing = await dbContext.Shares
            .SingleOrDefaultAsync(
                share => share.SenderUserId == senderUserId &&
                    share.CreateIdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesCreate(sourceGiftCardId, request.Amount))
            {
                throw new ConflictException(
                    "sharing.idempotency_key.reused",
                    "The idempotency key was already used for different share intent.");
            }

            throw new ConflictException(
                "sharing.credentials.already_issued",
                "The share already exists and its one-time credentials cannot be returned again.");
        }

        var activeReserved = await GetActiveReservedAmountCoreAsync(
            sourceGiftCardId,
            cancellationToken).ConfigureAwait(false);
        // Value already promised to a till is not available to share (ADR-033).
        var activeProvisioned = await paymentReservations
            .GetActiveProvisionedAmountAsync(sourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        if (request.Amount > lockedBalance.Amount - activeReserved - activeProvisioned)
        {
            throw new ConflictException(
                "sharing.balance.insufficient",
                "The source gift card does not have enough available value.");
        }

        var now = timeProvider.GetUtcNow();
        var shareId = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var pin = SharePinCodec.Create();
        var share = GiftCardShare.Create(
            shareId,
            sourceGiftCardId,
            source.FundingOrganizationId,
            senderUserId,
            request.Amount,
            source.Currency,
            token.SecretHash,
            pin.PersistedHash,
            idempotencyKey,
            now,
            now.AddHours(settings.ClaimTokenLifetimeHours));
        dbContext.Shares.Add(share);
        dbContext.Events.Add(GiftCardShareEvent.Create(
            share,
            GiftCardShareEventType.Created,
            senderUserId,
            now));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecordAuditAsync(
            share,
            AuditOperations.GiftCardShareCreated,
            senderUserId,
            AuditActorType.IdentityUser,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CreatedGiftCardShareResult(
            ToResult(share),
            BuildClaimUrl(token.RawToken),
            pin.RawPin);
    }

    public async Task<CreatedDirectGiftCardShareResult> CreateDirectAsync(
        Guid sourceGiftCardId,
        CreateDirectGiftCardShareRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await TranslateConcurrencyAsync(
            () => PrepareDirectAsync(sourceGiftCardId, request, cancellationToken))
            .ConfigureAwait(false);
        // Delivery is queued inside PrepareDirectAsync's transaction and drained
        // by the dispatcher, so there is no post-commit send to make here.
        return prepared.Result;
    }

    private async Task<PreparedDirectGiftCardShare> PrepareDirectAsync(
        Guid sourceGiftCardId,
        CreateDirectGiftCardShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var senderUserId = RequireCardholder();
        var idempotencyKey = GiftCardShare.NormalizeIdempotencyKey(
            request.IdempotencyKey,
            "sharing.create.idempotency_key");
        var contactType = MapContactType(request.ContactType);
        var contact = recipientContactService.NormalizeAndMask(
            contactType,
            request.RecipientContact);
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var source = await giftCardSharingWriter
            .GetOwnedSourceAsync(sourceGiftCardId, cancellationToken).ConfigureAwait(false);
        var lockedBalance = await ledger
            .GetLockedBalanceAsync(sourceGiftCardId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(source.Currency, lockedBalance.Currency, StringComparison.Ordinal))
        {
            throw new ConflictException(
                "sharing.balance.currency_mismatch",
                "The card and Ledger currencies do not match.");
        }

        var existing = await dbContext.Shares.SingleOrDefaultAsync(
            share => share.SenderUserId == senderUserId &&
                share.CreateIdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesDirectCreate(
                    sourceGiftCardId,
                    request.Amount,
                    request.ContactType,
                    contact.Contact))
            {
                throw new ConflictException(
                    "sharing.idempotency_key.reused",
                    "The idempotency key was already used for different share intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PreparedDirectGiftCardShare(
                new CreatedDirectGiftCardShareResult(
                    ToResult(existing),
                    existing.MaskedRecipientContact!,
                    DeliveryDispatchedThisRequest: false),
                Notification: null);
        }

        notificationChannels.RequireAvailable(
            request.ContactType == GiftCardShareContactType.Email
                ? NotificationChannel.Email
                : NotificationChannel.Sms);

        var activeReserved = await GetActiveReservedAmountCoreAsync(
            sourceGiftCardId,
            cancellationToken).ConfigureAwait(false);
        // Value already promised to a till is not available to share (ADR-033).
        var activeProvisioned = await paymentReservations
            .GetActiveProvisionedAmountAsync(sourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        if (request.Amount > lockedBalance.Amount - activeReserved - activeProvisioned)
        {
            throw new ConflictException(
                "sharing.balance.insufficient",
                "The source gift card does not have enough available value.");
        }

        var now = timeProvider.GetUtcNow();
        var shareId = Guid.CreateVersion7();
        var token = ShareTokenCodec.Create(shareId);
        var share = GiftCardShare.CreateDirect(
            shareId,
            sourceGiftCardId,
            source.FundingOrganizationId,
            senderUserId,
            request.Amount,
            source.Currency,
            token.SecretHash,
            request.ContactType,
            contact.Contact,
            contact.MaskedContact,
            idempotencyKey,
            now,
            now.AddHours(settings.ClaimTokenLifetimeHours));
        dbContext.Shares.Add(share);
        dbContext.Events.Add(GiftCardShareEvent.Create(
            share,
            GiftCardShareEventType.Created,
            senderUserId,
            now));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecordAuditAsync(
            share,
            AuditOperations.GiftCardShareCreated,
            senderUserId,
            AuditActorType.IdentityUser,
            cancellationToken).ConfigureAwait(false);
        // Queue the invitation inside this transaction, so it is durable exactly
        // when the share is. Sending after commit instead, as this used to, means
        // a crash in the gap loses the claim link and the reserved value sits
        // there with nobody able to claim it.
        await directNotifier
            .SendAsync(
                new DirectGiftCardShareNotification(
                    share.Id,
                    senderUserId,
                    request.ContactType,
                    contact.Contact,
                    contact.MaskedContact,
                    token.RawToken,
                    share.ExpiresAtUtc),
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PreparedDirectGiftCardShare(
            new CreatedDirectGiftCardShareResult(
                ToResult(share),
                contact.MaskedContact,
                DeliveryDispatchedThisRequest: true),
            Notification: null);
    }

    public async Task<GiftCardSharePage> GetMineAsync(
        GiftCardSharePageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = RequireCardholder();
        if (request.Limit is < 1 or > GiftCardSharePageRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "sharing.page.limit.invalid",
                $"Limit must be between 1 and {GiftCardSharePageRequest.MaxLimit}.");
        }

        if ((request.Kind is not null && !Enum.IsDefined(request.Kind.Value)) ||
            (request.State is not null && !Enum.IsDefined(request.State.Value)) ||
            (request.Direction is not null && !Enum.IsDefined(request.Direction.Value)))
        {
            throw new ValidationFailedException(
                "sharing.page.filter.invalid",
                "Share kind, state, and direction filters must be valid values.");
        }

        var filterFingerprint = GetFilterFingerprint(request);
        var cursor = DecodeCursor(request.Cursor, filterFingerprint);
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var query = dbContext.Shares
            .AsNoTracking()
            .Where(share => share.SenderUserId == userId || share.ClaimedByUserId == userId);
        if (request.Kind is not null)
        {
            query = query.Where(share => share.Kind == request.Kind.Value);
        }

        if (request.State is not null)
        {
            query = query.Where(share => share.State == request.State.Value);
        }

        if (request.Direction == GiftCardShareDirection.Sent)
        {
            query = query.Where(share => share.SenderUserId == userId);
        }
        else if (request.Direction == GiftCardShareDirection.Received)
        {
            query = query.Where(share => share.ClaimedByUserId == userId);
        }

        if (cursor is not null)
        {
            query = query.Where(share =>
                share.CreatedAtUtc < cursor.Value.OccurredAtUtc ||
                (share.CreatedAtUtc == cursor.Value.OccurredAtUtc && share.Id.CompareTo(cursor.Value.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(share => share.CreatedAtUtc)
            .ThenByDescending(share => share.Id)
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = rows.Count > request.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var cardIds = rows
            .SelectMany(share => share.ChildGiftCardId is null
                ? new[] { share.SourceGiftCardId }
                : new[] { share.SourceGiftCardId, share.ChildGiftCardId.Value })
            .Distinct()
            .ToArray();
        var publicReferences = await giftCardSharingWriter
            .GetVisiblePublicReferencesAsync(cardIds, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var next = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].CreatedAtUtc, rows[^1].Id, filterFingerprint)
            : null;
        return new GiftCardSharePage(
            rows.Select(share => ToResult(share, publicReferences)).ToList(),
            request.Limit,
            next);
    }

    public Task<GiftCardShareResult> CancelAsync(
        Guid shareId,
        string? idempotencyKey,
        CancellationToken cancellationToken) =>
        TranslateConcurrencyAsync(
            () => CancelCoreAsync(shareId, idempotencyKey, cancellationToken));

    private async Task<GiftCardShareResult> CancelCoreAsync(
        Guid shareId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var senderUserId = RequireCardholder();
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var hint = await dbContext.Shares
            .AsNoTracking()
            .SingleOrDefaultAsync(
                share => share.Id == shareId && share.SenderUserId == senderUserId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ShareNotFound();
        _ = await giftCardSharingWriter
            .GetOwnedSourceAsync(hint.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        _ = await ledger.GetLockedBalanceAsync(hint.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        await AcquireShareLockAsync(shareId, cancellationToken).ConfigureAwait(false);
        var share = await dbContext.Shares
            .SingleOrDefaultAsync(item => item.Id == shareId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ShareNotFound();
        var wasCancelled = share.State == GiftCardShareState.Cancelled;
        share.Cancel(senderUserId, idempotencyKey, timeProvider.GetUtcNow());
        if (!wasCancelled)
        {
            dbContext.Events.Add(GiftCardShareEvent.Create(
                share,
                GiftCardShareEventType.Cancelled,
                senderUserId,
                share.ClosedAtUtc!.Value));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordAuditAsync(
                share,
                AuditOperations.GiftCardShareCancelled,
                senderUserId,
                AuditActorType.IdentityUser,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(share);
    }

    public Task<ClaimedGiftCardShareResult> ClaimAsync(
        ClaimGiftCardShareRequest request,
        CancellationToken cancellationToken) =>
        TranslateConcurrencyAsync(() => ClaimCoreAsync(request, cancellationToken));

    private async Task<ClaimedGiftCardShareResult> ClaimCoreAsync(
        ClaimGiftCardShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var recipientUserId = RequireCardholder();
        if (!ShareTokenCodec.TryParse(request.ClaimToken, out var shareId, out var secret))
        {
            throw InvalidClaim();
        }

        executionContext.SetShareCandidate(shareId);
        AppException? delayedFailure = null;
        ClaimedGiftCardShareResult? result = null;
        await using (var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var hint = await dbContext.Shares
                .AsNoTracking()
                .SingleOrDefaultAsync(share => share.Id == shareId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw InvalidClaim();
            _ = await giftCardSharingWriter
                .GetClaimSourceAsync(hint.SourceGiftCardId, cancellationToken)
                .ConfigureAwait(false);
            _ = await ledger.GetLockedBalanceAsync(hint.SourceGiftCardId, cancellationToken)
                .ConfigureAwait(false);
            await AcquireShareLockAsync(shareId, cancellationToken).ConfigureAwait(false);
            var share = await dbContext.Shares
                .SingleOrDefaultAsync(item => item.Id == shareId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw InvalidClaim();

            if (!share.VerifySecret(secret))
            {
                throw InvalidClaim();
            }

            if (!share.VerifyPin(request.Pin))
            {
                var now = timeProvider.GetUtcNow();
                if (share.RecordFailedPinAttempt(settings.MaximumFailedPinAttempts, now))
                {
                    var type = share.State switch
                    {
                        GiftCardShareState.Locked => GiftCardShareEventType.Locked,
                        GiftCardShareState.Expired => GiftCardShareEventType.Expired,
                        _ => GiftCardShareEventType.PinFailed,
                    };
                    dbContext.Events.Add(GiftCardShareEvent.Create(share, type, recipientUserId, now));
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (share.State is GiftCardShareState.Locked or GiftCardShareState.Expired)
                    {
                        await RecordAuditAsync(
                            share,
                            share.State == GiftCardShareState.Locked
                                ? AuditOperations.GiftCardShareLocked
                                : AuditOperations.GiftCardShareExpired,
                            recipientUserId,
                            AuditActorType.IdentityUser,
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                delayedFailure = InvalidClaim();
            }
            else if (share.State == GiftCardShareState.Claimed)
            {
                if (!share.MatchesCompletedClaim(recipientUserId, request.IdempotencyKey) ||
                    share.ChildGiftCardId is null)
                {
                    throw new ConflictException(
                        "sharing.claim.already_completed",
                        "The share was already claimed.");
                }

                var child = await giftCardSharingWriter
                    .GetChildAsync(share.ChildGiftCardId.Value, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = new ClaimedGiftCardShareResult(ToResult(share), child);
            }
            else
            {
                var now = timeProvider.GetUtcNow();
                try
                {
                    share.EnsureClaimable(now);
                }
                catch (ConflictException exception)
                {
                    if (share.State == GiftCardShareState.Expired)
                    {
                        dbContext.Events.Add(GiftCardShareEvent.Create(
                            share,
                            GiftCardShareEventType.Expired,
                            recipientUserId,
                            now));
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await RecordAuditAsync(
                            share,
                            AuditOperations.GiftCardShareExpired,
                            recipientUserId,
                            AuditActorType.IdentityUser,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    delayedFailure = exception;
                }

                if (delayedFailure is null)
                {
                    var plan = ledger.PrepareTransfer();
                    var childGiftCardId = Guid.CreateVersion7();
                    share.BeginClaim(
                        recipientUserId,
                        childGiftCardId,
                        plan.LedgerTransactionId,
                        request.IdempotencyKey,
                        now);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    var transfer = await ledger.RecordTransferAsync(
                        new RecordGiftCardShareTransferRequest(
                            share.Id,
                            share.FundingOrganizationId,
                            share.SourceGiftCardId,
                            childGiftCardId,
                            share.Amount,
                            share.Currency,
                            $"SHARE-{share.Id:N}",
                            $"share:{share.Id:N}",
                            plan),
                        cancellationToken).ConfigureAwait(false);
                    var child = await giftCardSharingWriter.CreateChildAsync(
                        new CreateSharedGiftCardChildRequest(
                            share.Id,
                            share.SourceGiftCardId,
                            childGiftCardId,
                            recipientUserId,
                            share.Amount,
                            transfer.ChildLedgerAccountId,
                            transfer.TransactionId,
                            transfer.PostedAtUtc),
                        cancellationToken).ConfigureAwait(false);
                    share.CompleteClaim(transfer.PostedAtUtc);
                    dbContext.Events.Add(GiftCardShareEvent.Create(
                        share,
                        GiftCardShareEventType.Claimed,
                        recipientUserId,
                        transfer.PostedAtUtc));
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await RecordAuditAsync(
                        share,
                        AuditOperations.GiftCardShareClaimed,
                        recipientUserId,
                        AuditActorType.IdentityUser,
                        cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    result = new ClaimedGiftCardShareResult(ToResult(share), child);
                }
            }
        }

        if (delayedFailure is not null)
        {
            throw delayedFailure;
        }

        return result!;
    }

    public Task<ClaimedDirectGiftCardShareResult> ClaimDirectAsync(
        ClaimDirectGiftCardShareRequest request,
        CancellationToken cancellationToken) =>
        TranslateConcurrencyAsync(() => ClaimDirectCoreAsync(request, cancellationToken));

    private async Task<ClaimedDirectGiftCardShareResult> ClaimDirectCoreAsync(
        ClaimDirectGiftCardShareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShareTokenCodec.TryParse(request.ClaimToken, out var shareId, out var secret))
        {
            throw InvalidClaim();
        }

        executionContext.SetAnonymousShareCandidate(shareId);
        AppException? delayedFailure = null;
        ClaimedDirectGiftCardShareResult? result = null;
        await using (var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var hint = await dbContext.Shares.AsNoTracking()
                .SingleOrDefaultAsync(share => share.Id == shareId, cancellationToken)
                .ConfigureAwait(false) ?? throw InvalidClaim();
            if (hint.Kind != GiftCardShareKind.DirectInvitation || !hint.VerifySecret(secret) ||
                hint.RecipientContactType is null || hint.RecipientContact is null ||
                hint.MaskedRecipientContact is null)
            {
                throw InvalidClaim();
            }

            if (hint.State is not GiftCardShareState.Pending and not GiftCardShareState.Claimed)
            {
                throw new ConflictException(
                    "sharing.claim.unavailable",
                    "The share is unavailable.");
            }

            _ = await giftCardSharingWriter
                .GetClaimSourceAsync(hint.SourceGiftCardId, cancellationToken).ConfigureAwait(false);
            _ = await ledger.GetLockedBalanceAsync(hint.SourceGiftCardId, cancellationToken)
                .ConfigureAwait(false);
            await AcquireShareLockAsync(shareId, cancellationToken).ConfigureAwait(false);
            var share = await dbContext.Shares
                .SingleOrDefaultAsync(item => item.Id == shareId, cancellationToken)
                .ConfigureAwait(false) ?? throw InvalidClaim();

            if (share.Kind != GiftCardShareKind.DirectInvitation || !share.VerifySecret(secret) ||
                share.RecipientContactType is null || share.RecipientContact is null ||
                share.MaskedRecipientContact is null)
            {
                throw InvalidClaim();
            }

            if (share.State == GiftCardShareState.Claimed)
            {
                if (!share.MatchesCompletedDirectClaim(request.IdempotencyKey) ||
                    share.ClaimedByUserId is null || share.ChildGiftCardId is null ||
                    share.IdentityWasCreatedOnClaim is null)
                {
                    throw new ConflictException(
                        "sharing.claim.already_completed",
                        "The share was already claimed.");
                }

                executionContext.SetShareIdentity(share.ClaimedByUserId.Value, share.Id);
                await sessionContextWriter.WriteAsync(
                    transaction.Transaction.Connection!,
                    transaction.Transaction,
                    executionContext,
                    cancellationToken).ConfigureAwait(false);
                var child = await giftCardSharingWriter
                    .GetChildAsync(share.ChildGiftCardId.Value, cancellationToken).ConfigureAwait(false);
                var session = share.IdentityWasCreatedOnClaim.Value
                    ? await recipientClaimSessionIssuer.IssueAsync(
                        share.ClaimedByUserId.Value,
                        request.Password,
                        cancellationToken).ConfigureAwait(false)
                    : null;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = ToDirectClaimResult(share, child, session);
            }
            else
            {
                var now = timeProvider.GetUtcNow();
                try
                {
                    share.EnsureClaimable(now);
                }
                catch (ConflictException exception)
                {
                    if (share.State == GiftCardShareState.Expired)
                    {
                        dbContext.Events.Add(GiftCardShareEvent.Create(
                            share,
                            GiftCardShareEventType.Expired,
                            SystemActorIds.ShareExpiration,
                            now));
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await RecordAuditAsync(
                            share,
                            AuditOperations.GiftCardShareExpired,
                            SystemActorIds.ShareExpiration,
                            AuditActorType.System,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    delayedFailure = exception;
                }

                if (delayedFailure is null)
                {
                    var identity = await recipientIdentityService.ResolveAsync(
                        new ResolveRecipientIdentityRequest(
                            MapContactType(share.RecipientContactType.Value),
                            share.RecipientContact,
                            request.Password),
                        cancellationToken).ConfigureAwait(false);
                    executionContext.SetShareIdentity(identity.User.Id, share.Id);
                    await sessionContextWriter.WriteAsync(
                        transaction.Transaction.Connection!,
                        transaction.Transaction,
                        executionContext,
                        cancellationToken).ConfigureAwait(false);

                    var plan = ledger.PrepareTransfer();
                    var childGiftCardId = Guid.CreateVersion7();
                    share.BeginClaim(
                        identity.User.Id,
                        childGiftCardId,
                        plan.LedgerTransactionId,
                        request.IdempotencyKey,
                        now);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    var transfer = await ledger.RecordTransferAsync(
                        new RecordGiftCardShareTransferRequest(
                            share.Id,
                            share.FundingOrganizationId,
                            share.SourceGiftCardId,
                            childGiftCardId,
                            share.Amount,
                            share.Currency,
                            $"SHARE-{share.Id:N}",
                            $"share:{share.Id:N}",
                            plan),
                        cancellationToken).ConfigureAwait(false);
                    var child = await giftCardSharingWriter.CreateChildAsync(
                        new CreateSharedGiftCardChildRequest(
                            share.Id,
                            share.SourceGiftCardId,
                            childGiftCardId,
                            identity.User.Id,
                            share.Amount,
                            transfer.ChildLedgerAccountId,
                            transfer.TransactionId,
                            transfer.PostedAtUtc),
                        cancellationToken).ConfigureAwait(false);
                    share.CompleteClaim(transfer.PostedAtUtc, identity.WasCreated);
                    dbContext.Events.Add(GiftCardShareEvent.Create(
                        share,
                        GiftCardShareEventType.Claimed,
                        identity.User.Id,
                        transfer.PostedAtUtc));
                    var session = identity.WasCreated
                        ? await recipientClaimSessionIssuer.IssueAsync(
                            identity.User.Id,
                            request.Password,
                            cancellationToken).ConfigureAwait(false)
                        : null;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await RecordAuditAsync(
                        share,
                        AuditOperations.GiftCardShareClaimed,
                        identity.User.Id,
                        AuditActorType.IdentityUser,
                        cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    result = ToDirectClaimResult(share, child, session);
                }
            }
        }

        if (delayedFailure is not null)
        {
            throw delayedFailure;
        }

        return result!;
    }

    public async Task<decimal> GetActiveReservedAmountAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var result = await GetActiveReservedAmountCoreAsync(giftCardId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<int> ProcessDueAsync(int maximumItems, CancellationToken cancellationToken)
    {
        if (!executionContext.IsSystem || maximumItems is < 1 or > 100)
        {
            throw new ForbiddenException("sharing.expiration.system.required", "A valid system expiration scope is required.");
        }

        List<Guid> due;
        await using (var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            due = await dbContext.Shares.AsNoTracking()
                .Where(share => share.State == GiftCardShareState.Pending && share.ExpiresAtUtc <= now)
                .OrderBy(share => share.ExpiresAtUtc).ThenBy(share => share.Id)
                .Select(share => share.Id).Take(maximumItems).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var expired = 0;
        foreach (var shareId in due)
        {
            executionContext.SetSystemShareCandidate(shareId);
            if (await ExpireOneAsync(shareId, cancellationToken).ConfigureAwait(false))
            {
                expired++;
            }
        }

        return expired;
    }

    public async Task CloseForSourceLifecycleAsync(
        CloseSharesForSourceLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!executionContext.IsAuthenticated || executionContext.UserId is null ||
            request.SourceGiftCardId == Guid.Empty || !Enum.IsDefined(request.Closure))
        {
            throw new ForbiddenException(
                "sharing.source_lifecycle.actor.required",
                "An authorized gift-card lifecycle actor is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        _ = await ledger.GetLockedBalanceAsync(request.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var shares = await dbContext.Shares
            .Where(share => share.SourceGiftCardId == request.SourceGiftCardId &&
                share.State == GiftCardShareState.Pending)
            .OrderBy(share => share.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        foreach (var share in shares)
        {
            await AcquireShareLockAsync(share.Id, cancellationToken).ConfigureAwait(false);
            if (!share.CloseForSourceLifecycle(request.Closure, now))
            {
                continue;
            }

            var eventType = request.Closure == ShareSourceLifecycleClosure.Cancelled
                ? GiftCardShareEventType.Cancelled
                : GiftCardShareEventType.Expired;
            dbContext.Events.Add(GiftCardShareEvent.Create(
                share,
                eventType,
                executionContext.UserId.Value,
                now));
            await RecordAuditAsync(
                share,
                request.Closure == ShareSourceLifecycleClosure.Cancelled
                    ? AuditOperations.GiftCardShareCancelled
                    : AuditOperations.GiftCardShareExpired,
                executionContext.UserId.Value,
                executionContext.IsSystem
                    ? AuditActorType.System
                    : executionContext.IsPlatformOperator
                        ? AuditActorType.PlatformOperator
                        : executionContext.ActiveMembershipId is not null
                            ? AuditActorType.OrganizationMember
                            : AuditActorType.IdentityUser,
                cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExpireOneAsync(Guid shareId, CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var hint = await dbContext.Shares.AsNoTracking()
            .SingleOrDefaultAsync(share => share.Id == shareId, cancellationToken).ConfigureAwait(false);
        if (hint is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _ = await giftCardSharingWriter.GetClaimSourceAsync(hint.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        _ = await ledger.GetLockedBalanceAsync(hint.SourceGiftCardId, cancellationToken)
            .ConfigureAwait(false);
        await AcquireShareLockAsync(shareId, cancellationToken).ConfigureAwait(false);
        var share = await dbContext.Shares.SingleAsync(item => item.Id == shareId, cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (!share.Expire(now))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        dbContext.Events.Add(GiftCardShareEvent.Create(
            share,
            GiftCardShareEventType.Expired,
            executionContext.UserId!.Value,
            now));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecordAuditAsync(
            share,
            AuditOperations.GiftCardShareExpired,
            executionContext.UserId.Value,
            AuditActorType.System,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<decimal> GetActiveReservedAmountCoreAsync(
        Guid giftCardId,
        CancellationToken cancellationToken) =>
        // Deliberately unfiltered. This is a sum, so a caller the filter does not
        // recognise would get a smaller reserved figure rather than an error, and
        // a till would then be free to reserve value a share already holds. RLS
        // remains the authoritative barrier and admits only the card the caller
        // is entitled to, whether that is its owner or one verified credential.
        await dbContext.Shares
            .IgnoreQueryFilters()
            .Where(share => share.SourceGiftCardId == giftCardId &&
                (share.State == GiftCardShareState.Pending || share.State == GiftCardShareState.Claiming))
            .SumAsync(share => (decimal?)share.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

    private async Task RecordAuditAsync(
        GiftCardShare share,
        string operation,
        Guid actorUserId,
        AuditActorType actorType,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string>
        {
            ["sourceGiftCardId"] = share.SourceGiftCardId.ToString(),
            ["amount"] = share.Amount.ToString(CultureInfo.InvariantCulture),
            ["currency"] = share.Currency,
            ["kind"] = share.Kind.ToString(),
            ["state"] = share.State.ToString(),
        };
        if (share.RecipientContactType is not null && share.MaskedRecipientContact is not null)
        {
            metadata["recipientContactType"] = share.RecipientContactType.Value.ToString();
            metadata["maskedRecipient"] = share.MaskedRecipientContact;
        }
        if (share.ChildGiftCardId is not null)
        {
            metadata["childGiftCardId"] = share.ChildGiftCardId.Value.ToString();
        }

        if (share.LedgerTransactionId is not null)
        {
            metadata["ledgerTransactionId"] = share.LedgerTransactionId.Value.ToString();
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                actorUserId,
                actorType,
                share.FundingOrganizationId,
                operation,
                nameof(GiftCardShare),
                share.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                metadata),
            cancellationToken).ConfigureAwait(false);
    }

    private string BuildClaimUrl(string token)
    {
        var separator = settings.ClaimBaseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{settings.ClaimBaseUrl.TrimEnd('?', '&')}{separator}token={Uri.EscapeDataString(token)}";
    }

    private Task<int> AcquireShareLockAsync(Guid shareId, CancellationToken cancellationToken)
    {
        var lockKey = $"gift-card-share|{shareId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private Guid RequireCardholder()
    {
        if (!executionContext.IsAuthenticated || executionContext.IsPlatformOperator || executionContext.UserId is null)
        {
            throw new ForbiddenException("sharing.cardholder.required", "An authenticated cardholder is required.");
        }

        return executionContext.UserId.Value;
    }

    private static GiftCardShareResult ToResult(
        GiftCardShare share,
        IReadOnlyDictionary<Guid, string>? publicReferences = null) =>
        new(
            share.Id,
            share.Kind,
            share.SourceGiftCardId,
            share.FundingOrganizationId,
            share.SenderUserId,
            share.ClaimedByUserId,
            share.ChildGiftCardId,
            publicReferences?.GetValueOrDefault(share.SourceGiftCardId),
            share.ChildGiftCardId is null
                ? null
                : publicReferences?.GetValueOrDefault(share.ChildGiftCardId.Value),
            share.LedgerTransactionId,
            share.Amount,
            share.Currency,
            share.State,
            share.FailedPinAttempts,
            share.RecipientContactType,
            share.MaskedRecipientContact,
            share.IdentityWasCreatedOnClaim,
            share.ExpiresAtUtc,
            share.CreatedAtUtc,
            share.ClaimedAtUtc,
            share.ClosedAtUtc);

    private static ClaimedDirectGiftCardShareResult ToDirectClaimResult(
        GiftCardShare share,
        GiftCardResult child,
        TokenPairResult? session) =>
        new(
            ToResult(share),
            share.ClaimedByUserId!.Value,
            share.IdentityWasCreatedOnClaim!.Value,
            share.MaskedRecipientContact!,
            session is null
                ? null
                : new DirectGiftCardShareClaimSessionResult(
                    session.AccessToken,
                    session.AccessTokenExpiresAtUtc,
                    session.RefreshToken,
                    session.RefreshTokenExpiresAtUtc),
            child);

    private static IdentityContactType MapContactType(GiftCardShareContactType contactType) =>
        contactType switch
        {
            GiftCardShareContactType.Email => IdentityContactType.Email,
            GiftCardShareContactType.Phone => IdentityContactType.Phone,
            _ => throw new ValidationFailedException(
                "sharing.direct.contact_type.invalid",
                "Recipient contact type must be Email or Phone."),
        };

    private static string EncodeCursor(
        DateTimeOffset occurredAtUtc,
        Guid id,
        string? filterFingerprint) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
            filterFingerprint is null
                ? $"{occurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}"
                : $"{occurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}|{filterFingerprint}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (DateTimeOffset OccurredAtUtc, Guid Id)? DecodeCursor(
        string? value,
        string? filterFingerprint)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('|');
            if ((parts.Length != 2 && parts.Length != 3) ||
                (parts.Length == 2 && filterFingerprint is not null) ||
                (parts.Length == 3 && !string.Equals(parts[2], filterFingerprint, StringComparison.Ordinal)) ||
                !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id) || ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                throw new FormatException();
            }

            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException)
        {
            throw new ValidationFailedException("sharing.page.cursor.invalid", "The page cursor is invalid.");
        }
    }

    private static string? GetFilterFingerprint(GiftCardSharePageRequest request) =>
        request.Kind is null && request.State is null && request.Direction is null
            ? null
            : $"{request.Kind?.ToString() ?? "*"},{request.State?.ToString() ?? "*"}," +
              $"{request.Direction?.ToString() ?? "*"}";

    private static NotFoundException ShareNotFound() =>
        new("sharing.not_found", "Share not found.");

    private static UnauthorizedException InvalidClaim() =>
        new("sharing.claim.invalid", "The share claim is invalid or unavailable.");

    private static async Task<T> TranslateConcurrencyAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "sharing.concurrent_conflict",
                "A concurrent sharing or financial operation conflicted. " +
                "Retry safely with the same idempotency key.");
        }
    }

    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException ||
                current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation or
                        PostgresErrorCodes.SerializationFailure or
                        PostgresErrorCodes.DeadlockDetected,
                })
            {
                return true;
            }
        }

        return false;
    }
}
