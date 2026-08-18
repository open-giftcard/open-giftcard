using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class OrganizationStaffDirectory(
    IdentityDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOrganizationPermissionAuthorizer permissionAuthorizer)
    : IOrganizationStaffDirectory
{
    private const int MaximumDirectoryUsers = 200;

    public async Task<OrganizationStaffIdentityResult> ResolveForMembershipCreationAsync(
        Guid organizationId,
        string? email,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.MembershipsCreate,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var (_, normalizedEmail) = CredentialPolicy.NormalizeEmail(email);
        var result = await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.Status == UserStatus.Active &&
                user.NormalizedEmail == normalizedEmail &&
                user.Email != null)
            .Select(user => new OrganizationStaffIdentityResult(user.Id, user.Email!))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "membership.user_not_found",
                "No active staff account matches this email.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> GetVisibleEmailsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var distinctUserIds = userIds
            .Where(userId => userId != Guid.Empty)
            .Distinct()
            .ToArray();
        if (distinctUserIds.Length > MaximumDirectoryUsers)
        {
            throw new ValidationFailedException(
                "membership.directory.limit",
                $"At most {MaximumDirectoryUsers} staff identities may be read at once.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await RequireMembershipViewAsync(organizationId, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var results = await dbContext.Users
            .AsNoTracking()
            .Where(user => distinctUserIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Email })
            .ToDictionaryAsync(user => user.Id, user => user.Email, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task RequireMembershipViewAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (executionContext.IsPlatformOperator)
        {
            if (!executionContext.IsAuthenticated ||
                executionContext.UserId is null ||
                !executionContext.HasPlatformPermission(PlatformPermissions.MembershipsView))
            {
                throw new ForbiddenException(
                    "auth.forbidden",
                    "The required permission is missing.");
            }

            return;
        }

        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.MembershipsView,
            cancellationToken).ConfigureAwait(false);
    }
}
