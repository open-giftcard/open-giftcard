using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Authorization.Application;

internal sealed class InitialOrganizationAdministratorService(
    AuthorizationDbContext dbContext,
    IIdentityUserQuery identityUserQuery,
    IInitialAdministratorMembershipProvisioner membershipProvisioner,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IInitialOrganizationAdministratorService
{
    internal const string AdministratorRoleName = "Company Administrator";

    public async Task<InitialOrganizationAdministratorResult> AssignAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existingBootstrap = await dbContext.OrganizationAdministratorBootstraps
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingBootstrap is not null)
        {
            if (existingBootstrap.UserId != userId)
            {
                throw new ConflictException(
                    "organization.initial_administrator.exists",
                    "The organization already has an initial administrator.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(existingBootstrap);
        }

        var user = await identityUserQuery.FindAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || !string.Equals(user.Status, "Active", StringComparison.Ordinal))
        {
            throw new NotFoundException(
                "user.active_not_found",
                "An active user was not found.");
        }

        var membership = await membershipProvisioner
            .EnsureActiveRootMembershipAsync(organizationId, userId, cancellationToken)
            .ConfigureAwait(false);

        await PermissionCatalogueSynchronizer
            .EnsureAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        var reservedNameExists = await dbContext.Roles
            .AnyAsync(
                x => x.OrganizationId == organizationId && x.Name == AdministratorRoleName,
                cancellationToken)
            .ConfigureAwait(false);
        if (reservedNameExists)
        {
            throw new ConflictException(
                "organization.initial_administrator.role_conflict",
                "The reserved Company Administrator role name is already in use.");
        }

        var now = timeProvider.GetUtcNow();
        var role = Role.CreateSystem(
            organizationId,
            AdministratorRoleName,
            executionContext.UserId!.Value,
            now);
        foreach (var permission in OrganizationPermissions.All)
        {
            role.Grant(permission);
        }

        var roleAssignment = MembershipRoleAssignment.Create(
            organizationId,
            membership.Id,
            role.Id,
            RoleScopeType.Subtree,
            organizationId,
            selectedOrganizationIds: null,
            executionContext.UserId.Value,
            now);
        var bootstrap = OrganizationAdministratorBootstrap.Create(
            organizationId,
            userId,
            membership.Id,
            role.Id,
            roleAssignment.Id,
            now);

        dbContext.Roles.Add(role);
        dbContext.MembershipRoleAssignments.Add(roleAssignment);
        dbContext.OrganizationAdministratorBootstraps.Add(bootstrap);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId.Value,
                AuditActorType.PlatformOperator,
                organizationId,
                AuditOperations.InitialOrganizationAdministratorAssigned,
                nameof(OrganizationAdministratorBootstrap),
                organizationId.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["user_id"] = userId.ToString(),
                    ["membership_id"] = membership.Id.ToString(),
                    ["role_id"] = role.Id.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(bootstrap);
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

    private static InitialOrganizationAdministratorResult ToResult(
        OrganizationAdministratorBootstrap bootstrap) =>
        new(
            bootstrap.OrganizationId,
            bootstrap.UserId,
            bootstrap.MembershipId,
            bootstrap.RoleId,
            bootstrap.RoleAssignmentId,
            bootstrap.AssignedAtUtc);
}
