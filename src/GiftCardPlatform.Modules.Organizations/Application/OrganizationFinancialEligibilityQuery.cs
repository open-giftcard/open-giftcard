using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Organizations.Application;

internal sealed class OrganizationFinancialEligibilityQuery(
    OrganizationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator) : IOrganizationFinancialEligibilityQuery
{
    public async Task<bool> IsActiveRootAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var eligible = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(
                organization =>
                    organization.Id == organizationId &&
                    organization.ParentOrganizationId == null &&
                    organization.Status == OrganizationStatus.Active,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return eligible;
    }

    public async Task<bool> IsActiveIssuingOrganizationAsync(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var rootIsActive = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(
                organization =>
                    organization.Id == fundingOrganizationId &&
                    organization.RootOrganizationId == fundingOrganizationId &&
                    organization.ParentOrganizationId == null &&
                    organization.Status == OrganizationStatus.Active,
                cancellationToken)
            .ConfigureAwait(false);
        var issuerIsActive = rootIsActive &&
            await dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(
                    organization =>
                        organization.Id == issuingOrganizationId &&
                        organization.RootOrganizationId == fundingOrganizationId &&
                        organization.Status == OrganizationStatus.Active,
                    cancellationToken)
                .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return issuerIsActive;
    }
}
