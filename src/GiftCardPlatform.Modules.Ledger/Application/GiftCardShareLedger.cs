using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Domain;
using GiftCardPlatform.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Ledger.Application;

internal sealed class GiftCardShareLedger(
    LedgerDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardShareLedger
{
    public GiftCardShareTransferPlan PrepareTransfer()
    {
        EnsureAuthenticatedShareActor();
        return new GiftCardShareTransferPlan(Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    public async Task<GiftCardLockedBalanceResult> GetLockedBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        EnsureShareScope();
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException("ledger.gift_card.required", "A gift card is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireValueLockAsync(giftCardId, cancellationToken).ConfigureAwait(false);
        var account = await dbContext.Accounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.Type == LedgerAccountType.GiftCardValue && item.GiftCardId == giftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card.account.missing",
                "The gift-card value account is not available.");
        var balance = await GetAccountBalanceAsync(account.Id, cancellationToken).ConfigureAwait(false);
        if (balance < 0)
        {
            throw new ConflictException("ledger.gift_card.balance.invalid", "Gift-card balance is invalid.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardLockedBalanceResult(giftCardId, account.Id, account.Currency, balance);
    }

    public async Task<GiftCardShareTransferResult> RecordTransferAsync(
        RecordGiftCardShareTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAuthenticatedShareActor();
        if (request.ShareId != executionContext.ShareId || request.ShareId == Guid.Empty ||
            request.FundingOrganizationId == Guid.Empty || request.SourceGiftCardId == Guid.Empty ||
            request.ChildGiftCardId == Guid.Empty || request.Plan.LedgerTransactionId == Guid.Empty ||
            request.Plan.ChildLedgerAccountId == Guid.Empty)
        {
            throw new ForbiddenException(
                "ledger.gift_card_share.scope.invalid",
                "The protected share transfer scope is invalid.");
        }

        var money = Money.Create(request.Amount, request.Currency);
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireValueLockAsync(request.SourceGiftCardId, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Transactions
            .IgnoreQueryFilters()
            .Include(item => item.Entries)
            .SingleOrDefaultAsync(
                item => item.Id == request.Plan.LedgerTransactionId ||
                    (item.OperationType == LedgerTransaction.GiftCardShareTransferOperation &&
                     item.IdempotencyKey == request.IdempotencyKey.Trim()),
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesGiftCardShareTransferIntent(
                    request.FundingOrganizationId,
                    request.SourceGiftCardId,
                    request.ChildGiftCardId,
                    money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "ledger.idempotency_key.reused",
                    "The idempotency key was already used for different financial intent.");
            }

            var childEntry = existing.Entries.Single(entry => entry.Direction == LedgerEntryDirection.Credit);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardShareTransferResult(existing.Id, childEntry.AccountId, existing.PostedAtUtc);
        }

        var sourceAccount = await dbContext.Accounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.Type == LedgerAccountType.GiftCardValue &&
                    item.OrganizationId == request.FundingOrganizationId &&
                    item.GiftCardId == request.SourceGiftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card.account.missing",
                "The source gift-card value account is not available.");
        var balance = await GetAccountBalanceAsync(sourceAccount.Id, cancellationToken).ConfigureAwait(false);
        if (balance < money.Amount)
        {
            throw new ConflictException(
                "sharing.balance.insufficient",
                "The source gift card does not have enough posted value.");
        }

        var now = timeProvider.GetUtcNow();
        var childAccount = LedgerAccount.CreateGiftCardValue(
            request.Plan.ChildLedgerAccountId,
            request.FundingOrganizationId,
            request.ChildGiftCardId,
            money.Currency,
            now);
        dbContext.Accounts.Add(childAccount);
        var ledgerTransaction = LedgerTransaction.CreateGiftCardShareTransfer(
            request.Plan.LedgerTransactionId,
            request.FundingOrganizationId,
            request.SourceGiftCardId,
            request.ChildGiftCardId,
            sourceAccount,
            childAccount,
            money,
            request.BusinessReference,
            request.IdempotencyKey,
            executionContext.UserId!.Value,
            now);
        dbContext.Transactions.Add(ledgerTransaction);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardShareTransferResult(
            ledgerTransaction.Id,
            childAccount.Id,
            ledgerTransaction.PostedAtUtc);
    }

    private async Task<decimal> GetAccountBalanceAsync(Guid accountId, CancellationToken cancellationToken) =>
        await dbContext.Entries
            .IgnoreQueryFilters()
            .Where(entry => entry.AccountId == accountId)
            .SumAsync(
                entry => (decimal?)(entry.Direction == LedgerEntryDirection.Credit
                    ? entry.Amount
                    : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;

    private Task<int> AcquireValueLockAsync(Guid giftCardId, CancellationToken cancellationToken)
    {
        var lockKey = $"gift-card-value|{giftCardId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private void EnsureShareScope()
    {
        if ((!executionContext.IsAuthenticated || executionContext.UserId is null) &&
            executionContext.ShareId is null)
        {
            throw new ForbiddenException(
                "ledger.gift_card_share.scope.required",
                "An authenticated actor or exact share candidate is required.");
        }
    }

    private void EnsureAuthenticatedShareActor()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "ledger.gift_card_share.actor.required",
                "An authenticated financial actor is required for sharing.");
        }
    }
}
