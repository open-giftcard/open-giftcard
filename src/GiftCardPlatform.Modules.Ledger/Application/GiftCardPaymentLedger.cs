using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Domain;
using GiftCardPlatform.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Ledger.Application;

internal sealed class GiftCardPaymentLedger(
    LedgerDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IGiftCardPaymentLedger
{
    public async Task<GiftCardLockedBalanceResult> GetLockedBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        EnsurePaymentScope();
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card.required",
                "A gift card is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // The same lock key sharing uses. Taking it here is what makes a share
        // and a payment provision serialise on one card instead of both reading
        // the same balance and each reserving it.
        await AcquireValueLockAsync(giftCardId, cancellationToken).ConfigureAwait(false);

        var account = await dbContext.Accounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.Type == LedgerAccountType.GiftCardValue &&
                    item.GiftCardId == giftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card.account.missing",
                "The gift-card value account is not available.");

        var balance = await dbContext.Entries
            .IgnoreQueryFilters()
            .Where(entry => entry.AccountId == account.Id)
            .SumAsync(
                entry => (decimal?)(entry.Direction == LedgerEntryDirection.Credit
                    ? entry.Amount
                    : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        if (balance < 0)
        {
            throw new ConflictException(
                "ledger.gift_card.balance.invalid",
                "Gift-card balance is invalid.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardLockedBalanceResult(giftCardId, account.Id, account.Currency, balance);
    }

    public async Task<GiftCardLockedBalanceResult> GetBalanceAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        EnsurePaymentScope();
        if (giftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card.required",
                "A gift card is required.");
        }

        // Deliberately no value lock and no serializable isolation. This answers
        // a question; it never decides what to reserve or post. Locking here
        // would put a repeatable read in the path of every share and payment on
        // the card.
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var account = await dbContext.Accounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.Type == LedgerAccountType.GiftCardValue &&
                    item.GiftCardId == giftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card.account.missing",
                "The gift-card value account is not available.");

        var balance = await dbContext.Entries
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entry => entry.AccountId == account.Id)
            .SumAsync(
                entry => (decimal?)(entry.Direction == LedgerEntryDirection.Credit
                    ? entry.Amount
                    : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        if (balance < 0)
        {
            throw new ConflictException(
                "ledger.gift_card.balance.invalid",
                "Gift-card balance is invalid.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardLockedBalanceResult(giftCardId, account.Id, account.Currency, balance);
    }

    public async Task<GiftCardRedemptionResult> RecordRedemptionAsync(
        RecordGiftCardRedemptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRedemptionScope(request.PaymentTokenId);
        var money = Money.Create(request.Amount, request.Currency);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireValueLockAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);

        var idempotencyKey = $"payment-token:{request.PaymentTokenId:N}";
        var existing = await dbContext.Transactions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.OperationType == LedgerTransaction.GiftCardRedemptionOperation &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesGiftCardRedemptionIntent(
                    request.FundingOrganizationId,
                    request.GiftCardId,
                    request.PaymentTokenId,
                    request.ProvisionId,
                    money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "payment.confirmation.credential_conflict",
                    "The payment credential was already used for a different redemption.");
            }

            await SetLedgerTransactionCandidateAsync(existing.Id, cancellationToken)
                .ConfigureAwait(false);
            var settlementEntry = await dbContext.Entries
                .IgnoreQueryFilters()
                .SingleAsync(
                    entry => entry.Direction == LedgerEntryDirection.Credit,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardRedemptionResult(
                existing.Id,
                settlementEntry.AccountId,
                existing.PostedAtUtc);
        }

        var giftCardAccount = await dbContext.Accounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                account => account.Type == LedgerAccountType.GiftCardValue &&
                    account.OrganizationId == request.FundingOrganizationId &&
                    account.GiftCardId == request.GiftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "payment.confirmation.unavailable",
                "The payment provision cannot be confirmed.");
        var balance = await GetAccountBalanceAsync(giftCardAccount.Id, cancellationToken)
            .ConfigureAwait(false);
        if (balance < money.Amount)
        {
            throw new ConflictException(
                "payment.confirmation.unavailable",
                "The payment provision cannot be confirmed.");
        }

        var settlementLockKey = $"redemption-settlement|{money.Currency}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({settlementLockKey}, 0))",
            cancellationToken).ConfigureAwait(false);
        var settlementAccount = await dbContext.Accounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                account => account.Type == LedgerAccountType.PlatformRedemptionSettlement &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);
        if (settlementAccount is null)
        {
            settlementAccount = LedgerAccount.CreatePlatformRedemptionSettlement(
                money.Currency,
                timeProvider.GetUtcNow());
            dbContext.Accounts.Add(settlementAccount);
        }

        var redemption = LedgerTransaction.CreateGiftCardRedemption(
            request.FundingOrganizationId,
            request.GiftCardId,
            request.PaymentTokenId,
            request.ProvisionId,
            giftCardAccount,
            settlementAccount,
            money,
            request.BusinessReference,
            executionContext.PosClientId!.Value,
            timeProvider.GetUtcNow());
        await SetLedgerTransactionCandidateAsync(redemption.Id, cancellationToken)
            .ConfigureAwait(false);
        dbContext.Transactions.Add(redemption);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardRedemptionResult(
            redemption.Id,
            settlementAccount.Id,
            redemption.PostedAtUtc);
    }

    public async Task<GiftCardRefundLedgerResult> RecordRefundAsync(
        RecordGiftCardRefundRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureRedemptionScope(request.PaymentTokenId);
        if (request.RefundId == Guid.Empty || request.OriginalRedemptionTransactionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "payment.refund.scope.required",
                "Refund and original redemption identifiers are required.");
        }
        var money = Money.Create(request.Amount, request.Currency);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireValueLockAsync(request.GiftCardId, cancellationToken).ConfigureAwait(false);
        await SetRefundCandidateAsync(request.RefundId, cancellationToken).ConfigureAwait(false);
        var idempotencyKey = $"payment-refund:{request.RefundId:N}";
        var existing = await dbContext.Transactions.IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.OperationType == LedgerTransaction.GiftCardRefundOperation &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesGiftCardRefundIntent(
                    request.FundingOrganizationId, request.GiftCardId, request.ProvisionId,
                    request.RefundId, request.OriginalRedemptionTransactionId, money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "payment.refund.already_completed",
                    "The refund identifier was already used with different intent.");
            }
            await SetLedgerTransactionCandidateAsync(existing.Id, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardRefundLedgerResult(existing.Id, existing.PostedAtUtc);
        }

        var originalExists = await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(
            item => item.Id == request.OriginalRedemptionTransactionId &&
                item.OperationType == LedgerTransaction.GiftCardRedemptionOperation &&
                item.OrganizationId == request.FundingOrganizationId,
            cancellationToken).ConfigureAwait(false);
        if (!originalExists)
        {
            throw new ConflictException(
                "payment.refund.redemption_unavailable",
                "The original redemption is not available for refund.");
        }

        var giftCardAccount = await dbContext.Accounts.IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                account => account.Type == LedgerAccountType.GiftCardValue &&
                    account.OrganizationId == request.FundingOrganizationId &&
                    account.GiftCardId == request.GiftCardId,
                cancellationToken).ConfigureAwait(false);
        var settlementAccount = await dbContext.Accounts.IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                account => account.Type == LedgerAccountType.PlatformRedemptionSettlement &&
                    account.Currency == money.Currency,
                cancellationToken).ConfigureAwait(false);
        // Payments serializes and database-enforces cumulative refunds against
        // the immutable confirmed amount. Re-reading the global settlement
        // balance here would require broad POS visibility over other clients'
        // entries, violating the exact-candidate RLS boundary. The per-sale cap
        // proves this inverse posting cannot overdraw its originating value.
        if (giftCardAccount is null || settlementAccount is null)
        {
            throw new ConflictException(
                "payment.refund.unavailable",
                "The payment cannot be refunded.");
        }

        var refund = LedgerTransaction.CreateGiftCardRefund(
            request.FundingOrganizationId, request.GiftCardId, request.ProvisionId,
            request.RefundId, request.OriginalRedemptionTransactionId,
            settlementAccount, giftCardAccount, money, request.BusinessReference,
            executionContext.PosClientId!.Value, timeProvider.GetUtcNow());
        await SetLedgerTransactionCandidateAsync(refund.Id, cancellationToken).ConfigureAwait(false);
        dbContext.Transactions.Add(refund);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GiftCardRefundLedgerResult(refund.Id, refund.PostedAtUtc);
    }

    private async Task<decimal> GetAccountBalanceAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
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

    private Task<int> SetLedgerTransactionCandidateAsync(
        Guid transactionId,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select set_config('app.ledger_transaction_id', {transactionId.ToString()}, true)",
            cancellationToken);

    private Task<int> SetRefundCandidateAsync(Guid refundId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select set_config('app.payment_refund_id', {refundId.ToString()}, true)",
            cancellationToken);

    private void EnsurePaymentScope()
    {
        // Either the card owner reading their own available value, or a till
        // acting through one verified payment credential. A POS principal alone
        // is never enough.
        var hasOwner = executionContext.IsAuthenticated && executionContext.UserId is not null;
        var hasCredential = executionContext.PaymentTokenId is not null;
        if (!hasOwner && !hasCredential)
        {
            throw new ForbiddenException(
                "ledger.gift_card_payment.scope.required",
                "An authenticated owner or verified payment credential is required.");
        }
    }

    private void EnsureRedemptionScope(Guid paymentTokenId)
    {
        if (!executionContext.IsPosClient ||
            executionContext.PosClientId is null ||
            executionContext.PosTerminalId is null ||
            executionContext.PaymentTokenId != paymentTokenId ||
            paymentTokenId == Guid.Empty)
        {
            throw new ForbiddenException(
                "ledger.gift_card_redemption.scope.required",
                "An authenticated POS client and verified payment credential are required.");
        }
    }
}
