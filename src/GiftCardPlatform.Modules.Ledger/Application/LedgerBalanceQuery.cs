using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Ledger.Domain;
using GiftCardPlatform.Modules.Ledger.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Ledger.Application;

internal sealed class LedgerBalanceQuery(
    LedgerDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : ILedgerBalanceQuery
{
    public async Task<IReadOnlyList<LedgerBalanceResult>> GetOrganizationCorporateCreditBalancesAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "ledger.organization.required",
                "An organization is required.");
        }

        // A matching tenant root is not by itself proof of authority. Machine
        // principals now carry tenant scope without a membership (ADR-053), so
        // this requires the membership explicitly rather than inferring it from
        // the tenant root, matching the gate in LedgerWriter.
        var platformOperator =
            executionContext.IsPlatformOperator &&
            executionContext.UserId is not null;
        var verifiedOrganizationMember =
            !executionContext.IsPlatformOperator &&
            executionContext.UserId is not null &&
            executionContext.ActiveMembershipId is not null &&
            executionContext.TenantRootOrganizationId == organizationId;
        if (!executionContext.IsAuthenticated ||
            (!platformOperator && !verifiedOrganizationMember))
        {
            throw new ForbiddenException(
                "ledger.scope.forbidden",
                "The requested financial scope is not available.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var balances = await dbContext.Accounts
            .AsNoTracking()
            .Where(account =>
                account.OrganizationId == organizationId &&
                account.Type == LedgerAccountType.OrganizationCorporateCredit)
            .OrderBy(account => account.Currency)
            .Select(account => new LedgerBalanceResult(
                account.Currency,
                dbContext.Entries
                    .Where(entry => entry.AccountId == account.Id)
                    .Sum(entry => (decimal?)(
                        entry.Direction == LedgerEntryDirection.Credit
                            ? entry.Amount
                            : -entry.Amount)) ?? 0m))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return balances;
    }
}
