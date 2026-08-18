using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Payments.Application;

/// <summary>
/// Deliberately a separate, dependency-light class rather than another face of
/// <see cref="PaymentProvisionService"/>.
///
/// Sharing and Payments each need to see the other's holds, and the provision
/// service already depends on Sharing's reservation query. Implementing this
/// interface there too would close the loop into a circular service graph that
/// dependency injection cannot resolve. Keeping the read here terminates the
/// chain: Payments → Sharing → this → nothing.
/// </summary>
internal sealed class PaymentReservationQuery(
    PaymentsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    TimeProvider timeProvider) : IPaymentReservationQuery
{
    public async Task<decimal> GetActiveProvisionedAmountAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        if (giftCardId == Guid.Empty)
        {
            return 0m;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Query filters are bypassed on purpose: this is an aggregate over holds
        // the caller is not entitled to see individually. It returns a single
        // total and never exposes another party's payment.
        var total = await dbContext.Provisions
            .IgnoreQueryFilters()
            .Where(provision =>
                provision.GiftCardId == giftCardId &&
                provision.State == PaymentProvisionState.Active &&
                provision.ExpiresAtUtc > now)
            .SumAsync(provision => (decimal?)provision.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }
}
