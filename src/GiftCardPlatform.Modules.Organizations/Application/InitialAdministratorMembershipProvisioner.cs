using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Organizations.Application;

internal sealed class InitialAdministratorMembershipProvisioner(
    OrganizationsDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IInitialAdministratorMembershipProvisioner
{
    public async Task<MembershipResult> EnsureActiveRootMembershipAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnableInitialAdministratorWritePathAsync(
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var isActiveRoot = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == organizationId &&
                     x.ParentOrganizationId == null &&
                     x.Status == OrganizationStatus.Active,
                cancellationToken)
            .ConfigureAwait(false);
        if (!isActiveRoot)
        {
            throw new NotFoundException(
                "organization.root_not_found",
                "An active root organization was not found.");
        }

        var existing = await dbContext.Memberships
            .SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.UserId == userId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                throw new ConflictException(
                    "membership.disabled",
                    "The user's existing organization membership is disabled.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(existing);
        }

        var membership = OrganizationMembership.Create(
            organizationId,
            userId,
            timeProvider.GetUtcNow());
        dbContext.Memberships.Add(membership);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                organizationId,
                AuditOperations.MembershipCreated,
                nameof(OrganizationMembership),
                membership.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["user_id"] = userId.ToString() }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(membership);
    }

    private void RequirePlatformPermission()
    {
        if (!executionContext.IsAuthenticated ||
            executionContext.UserId is null ||
            !executionContext.HasPlatformPermission(
                PlatformPermissions.InitialAdministratorsAssign))
        {
            throw new ForbiddenException(
                "auth.forbidden",
                "The required permission is missing.");
        }
    }

    private static async Task EnableInitialAdministratorWritePathAsync(
        IModuleTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select set_config('app.is_initial_admin_bootstrap', 'true', true)",
            transaction.Transaction.Connection,
            transaction.Transaction);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MembershipResult ToResult(OrganizationMembership membership) =>
        new(
            membership.Id,
            membership.OrganizationId,
            membership.UserId,
            membership.Status.ToString(),
            membership.CreatedAtUtc,
            membership.DisabledAtUtc);
}
