using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Domain;
using GiftCardPlatform.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Ledger.Application;

internal sealed class LedgerWriter(
    LedgerDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : ILedgerWriter
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<LedgerTransactionResult> RecordCorporateCreditAsync(
        RecordCorporateCreditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!executionContext.IsPlatformOperator || executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "ledger.platform_operator.required",
                "A platform operator is required for corporate-credit funding.");
        }

        var money = Money.Create(request.Amount, request.Currency);
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.OperationType == LedgerTransaction.CorporateCreditOperation &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!existing.MatchesCorporateCreditIntent(
                    request.OrganizationId,
                    money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "ledger.idempotency_key.reused",
                    "The idempotency key was already used for different financial intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LedgerTransactionResult(existing.Id, existing.PostedAtUtc);
        }

        var now = timeProvider.GetUtcNow();
        var platformAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.Type == LedgerAccountType.PlatformFunding &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);
        if (platformAccount is null)
        {
            platformAccount = LedgerAccount.CreatePlatformFunding(money.Currency, now);
            dbContext.Accounts.Add(platformAccount);
        }

        var organizationAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.Type == LedgerAccountType.OrganizationCorporateCredit &&
                    account.OrganizationId == request.OrganizationId &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);
        if (organizationAccount is null)
        {
            organizationAccount = LedgerAccount.CreateOrganizationCorporateCredit(
                request.OrganizationId,
                money.Currency,
                now);
            dbContext.Accounts.Add(organizationAccount);
        }

        var ledgerTransaction = LedgerTransaction.CreateCorporateCredit(
            request.OrganizationId,
            platformAccount,
            organizationAccount,
            money,
            request.BusinessReference,
            request.IdempotencyKey,
            executionContext.UserId.Value,
            now);
        dbContext.Transactions.Add(ledgerTransaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            var sqlState = FindPostgresSqlState(exception);
            throw new ConflictException(
                sqlState == UniqueViolation
                    ? "ledger.idempotency_or_account.conflict"
                    : "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return new LedgerTransactionResult(ledgerTransaction.Id, ledgerTransaction.PostedAtUtc);
    }

    public async Task<LedgerTransactionResult> RecordCorporateCreditReversalAsync(
        RecordCorporateCreditReversalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!executionContext.IsPlatformOperator || executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "ledger.platform_operator.required",
                "A platform operator is required for corporate-credit reversal.");
        }

        var money = Money.Create(request.Amount, request.Currency);
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.OperationType == LedgerTransaction.CorporateCreditReversalOperation &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!existing.MatchesCorporateCreditReversalIntent(
                    request.OrganizationId,
                    request.OriginalTransactionId,
                    money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "ledger.idempotency_key.reused",
                    "The idempotency key was already used for different reversal intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new LedgerTransactionResult(existing.Id, existing.PostedAtUtc);
        }

        var original = await dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == request.OriginalTransactionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (original is null ||
            original.OrganizationId != request.OrganizationId ||
            original.OperationType != LedgerTransaction.CorporateCreditOperation)
        {
            throw new ConflictException(
                "ledger.reversal.original.invalid",
                "The original ledger transaction is not eligible for corporate-credit reversal.");
        }

        var accountLockKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"corporate-credit|{request.OrganizationId:D}|{money.Currency}");
        await dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $"select pg_advisory_xact_lock(hashtextextended({accountLockKey}, 0))",
                cancellationToken)
            .ConfigureAwait(false);

        var organizationAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.OrganizationId == request.OrganizationId &&
                    account.Type == LedgerAccountType.OrganizationCorporateCredit &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);
        var platformAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.Type == LedgerAccountType.PlatformFunding &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);

        if (organizationAccount is null || platformAccount is null)
        {
            throw new ConflictException(
                "ledger.reversal.accounts.missing",
                "The original financial accounts are not available for reversal.");
        }

        var available = await dbContext.Entries
            .Where(entry => entry.AccountId == organizationAccount.Id)
            .SumAsync(
                entry => (decimal?)(
                    entry.Direction == LedgerEntryDirection.Credit
                        ? entry.Amount
                        : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        if (available < money.Amount)
        {
            throw new ConflictException(
                "corporate_credit.balance.insufficient",
                "The organization no longer has enough available corporate credit to reverse this allocation.");
        }

        var now = timeProvider.GetUtcNow();
        var ledgerTransaction = LedgerTransaction.CreateCorporateCreditReversal(
            request.OrganizationId,
            request.OriginalTransactionId,
            organizationAccount,
            platformAccount,
            money,
            request.BusinessReference,
            request.IdempotencyKey,
            executionContext.UserId.Value,
            now);
        dbContext.Transactions.Add(ledgerTransaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return new LedgerTransactionResult(ledgerTransaction.Id, ledgerTransaction.PostedAtUtc);
    }

    public async Task<GiftCardFundingResult> RecordGiftCardIssuanceAsync(
        RecordGiftCardIssuanceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var verifiedOrganizationMember =
            executionContext.IsAuthenticated &&
            !executionContext.IsPlatformOperator &&
            executionContext.UserId is not null &&
            executionContext.ActiveMembershipId is not null &&
            executionContext.TenantRootOrganizationId == request.FundingOrganizationId;
        var acceptedBulkProcessor =
            executionContext.IsSystem &&
            executionContext.UserId == SystemActorIds.BulkGiftCardBatch;

        // The third and only other non-member minter (ADR-053). An e-pin
        // reseller has no membership to verify, so the funding tenant on the
        // principal is what is checked instead, and it is server state resolved
        // from the client row on every request rather than anything the caller
        // sent. The scope is deliberately not checked here: authority to mint is
        // decided in the issuance service, and this gate exists to prove the
        // caller may spend *this* tenant's float.
        var verifiedPartnerClient =
            executionContext.IsAuthenticated &&
            !executionContext.IsPlatformOperator &&
            !executionContext.IsSystem &&
            executionContext.IsPartnerClient &&
            executionContext.PartnerClientId is not null &&
            executionContext.TenantRootOrganizationId == request.FundingOrganizationId;

        if (!verifiedOrganizationMember && !acceptedBulkProcessor && !verifiedPartnerClient)
        {
            throw new ForbiddenException(
                "ledger.organization_member.required",
                "A verified organization membership in the funding tenant is required.");
        }

        // A partner is not an Identity user, so it has no user id to attribute
        // to. The column has no foreign key and already carries non-user actors
        // for system jobs; recording the client id keeps every mint traceable to
        // the exact credential that produced it.
        var actorUserId = executionContext.UserId ?? executionContext.PartnerClientId!.Value;

        if (request.GiftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card.required",
                "A gift card identifier is required.");
        }

        var money = Money.Create(request.Amount, request.Currency);
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.OperationType == LedgerTransaction.GiftCardIssuanceOperation &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.MatchesGiftCardIssuanceIntent(
                    request.FundingOrganizationId,
                    request.GiftCardId,
                    money,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "ledger.idempotency_key.reused",
                    "The idempotency key was already used for different gift-card funding intent.");
            }

            var existingAccountId = await dbContext.Accounts
                .Where(account =>
                    account.Type == LedgerAccountType.GiftCardValue &&
                    account.GiftCardId == request.GiftCardId)
                .Select(account => account.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardFundingResult(
                existing.Id,
                existingAccountId,
                existing.PostedAtUtc);
        }

        var accountLockKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"corporate-credit|{request.FundingOrganizationId:D}|{money.Currency}");
        await dbContext.Database
            .ExecuteSqlInterpolatedAsync(
                $"select pg_advisory_xact_lock(hashtextextended({accountLockKey}, 0))",
                cancellationToken)
            .ConfigureAwait(false);

        var organizationAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.OrganizationId == request.FundingOrganizationId &&
                    account.Type == LedgerAccountType.OrganizationCorporateCredit &&
                    account.Currency == money.Currency,
                cancellationToken)
            .ConfigureAwait(false);
        if (organizationAccount is null)
        {
            throw new ConflictException(
                "corporate_credit.balance.insufficient",
                "The funding organization has no available corporate credit in this currency.");
        }

        var available = await dbContext.Entries
            .Where(entry => entry.AccountId == organizationAccount.Id)
            .SumAsync(
                entry => (decimal?)(
                    entry.Direction == LedgerEntryDirection.Credit
                        ? entry.Amount
                        : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        if (available < money.Amount)
        {
            throw new ConflictException(
                "corporate_credit.balance.insufficient",
                "The funding organization does not have enough available corporate credit.");
        }

        var now = timeProvider.GetUtcNow();
        var giftCardAccount = LedgerAccount.CreateGiftCardValue(
            request.FundingOrganizationId,
            request.GiftCardId,
            money.Currency,
            now);
        dbContext.Accounts.Add(giftCardAccount);

        var ledgerTransaction = LedgerTransaction.CreateGiftCardIssuance(
            request.FundingOrganizationId,
            request.GiftCardId,
            organizationAccount,
            giftCardAccount,
            money,
            request.BusinessReference,
            idempotencyKey,
            actorUserId,
            now);
        dbContext.Transactions.Add(ledgerTransaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return new GiftCardFundingResult(
            ledgerTransaction.Id,
            giftCardAccount.Id,
            ledgerTransaction.PostedAtUtc);
    }

    public async Task<GiftCardValueReturnResult> RecordGiftCardValueReturnAsync(
        RecordGiftCardValueReturnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!executionContext.IsAuthenticated ||
            executionContext.UserId is null ||
            (!executionContext.IsPlatformOperator &&
             (executionContext.ActiveMembershipId is null ||
              executionContext.TenantRootOrganizationId !=
                  request.FundingOrganizationId)))
        {
            throw new ForbiddenException(
                "ledger.gift_card_return.actor.required",
                "An authorized organization, platform, or system actor is required.");
        }

        if (request.FundingOrganizationId == Guid.Empty ||
            request.GiftCardId == Guid.Empty ||
            request.IssuanceLedgerTransactionId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.gift_card_return.scope.required",
                "Funding organization, gift card, and issuance identifiers are required.");
        }

        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var accountLockKey = $"gift-card-value|{request.GiftCardId:D}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({accountLockKey}, 0))",
            cancellationToken).ConfigureAwait(false);

        var giftCardAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.Type == LedgerAccountType.GiftCardValue &&
                    account.OrganizationId == request.FundingOrganizationId &&
                    account.GiftCardId == request.GiftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card_return.account.missing",
                "The gift card value account is not available.");

        var existing = await dbContext.Transactions
            .Include(item => item.Entries)
            .SingleOrDefaultAsync(
                item =>
                    (item.OperationType ==
                        LedgerTransaction.GiftCardCancellationReturnOperation ||
                     item.OperationType ==
                        LedgerTransaction.GiftCardExpirationReturnOperation) &&
                    item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var returnedEntry = existing.Entries.Single(
                entry =>
                    entry.AccountId == giftCardAccount.Id &&
                    entry.Direction == LedgerEntryDirection.Debit);
            var existingMoney = Money.Create(returnedEntry.Amount, returnedEntry.Currency);
            if (!existing.MatchesGiftCardValueReturnIntent(
                    request.FundingOrganizationId,
                    request.GiftCardId,
                    request.IssuanceLedgerTransactionId,
                    existingMoney,
                    request.Reason,
                    request.BusinessReference))
            {
                throw new ConflictException(
                    "ledger.idempotency_key.reused",
                    "The idempotency key was already used for different value-return intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardValueReturnResult(
                existing.Id,
                existingMoney.Amount,
                existingMoney.Currency,
                existing.PostedAtUtc);
        }

        var issuance = await dbContext.Transactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == request.IssuanceLedgerTransactionId &&
                    item.OrganizationId == request.FundingOrganizationId &&
                    item.OperationType == LedgerTransaction.GiftCardIssuanceOperation,
                cancellationToken)
            .ConfigureAwait(false);
        if (issuance is null)
        {
            throw new ConflictException(
                "ledger.gift_card_return.issuance.invalid",
                "The original gift-card issuance is not eligible for value return.");
        }

        var balance = await dbContext.Entries
            .Where(entry => entry.AccountId == giftCardAccount.Id)
            .SumAsync(
                entry => (decimal?)(
                    entry.Direction == LedgerEntryDirection.Credit
                        ? entry.Amount
                        : -entry.Amount),
                cancellationToken)
            .ConfigureAwait(false) ?? 0m;
        if (balance < 0m)
        {
            throw new ConflictException(
                "ledger.gift_card_return.balance.invalid",
                "The gift-card ledger account has an invalid negative balance.");
        }

        var now = timeProvider.GetUtcNow();
        if (balance == 0m)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new GiftCardValueReturnResult(
                TransactionId: null,
                Amount: 0m,
                Currency: giftCardAccount.Currency,
                ProcessedAtUtc: now);
        }

        var organizationLockKey =
            $"corporate-credit|{request.FundingOrganizationId:D}|{giftCardAccount.Currency}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({organizationLockKey}, 0))",
            cancellationToken).ConfigureAwait(false);

        var organizationAccount = await dbContext.Accounts
            .SingleOrDefaultAsync(
                account =>
                    account.Type == LedgerAccountType.OrganizationCorporateCredit &&
                    account.OrganizationId == request.FundingOrganizationId &&
                    account.Currency == giftCardAccount.Currency,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "ledger.gift_card_return.account.missing",
                "The funding organization's corporate-credit account is not available.");

        var money = Money.Create(balance, giftCardAccount.Currency);
        var ledgerTransaction = LedgerTransaction.CreateGiftCardValueReturn(
            request.FundingOrganizationId,
            request.GiftCardId,
            request.IssuanceLedgerTransactionId,
            giftCardAccount,
            organizationAccount,
            money,
            request.Reason,
            request.BusinessReference,
            idempotencyKey,
            executionContext.UserId.Value,
            now);
        dbContext.Transactions.Add(ledgerTransaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return new GiftCardValueReturnResult(
            ledgerTransaction.Id,
            money.Amount,
            money.Currency,
            ledgerTransaction.PostedAtUtc);
    }

    private static bool IsFinancialConcurrencyConflict(Exception exception)
    {
        var sqlState = FindPostgresSqlState(exception);
        return sqlState is UniqueViolation or SerializationFailure;
    }

    private static string? FindPostgresSqlState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException.SqlState;
            }
        }

        return null;
    }
}
