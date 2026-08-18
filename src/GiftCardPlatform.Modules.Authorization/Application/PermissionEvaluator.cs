using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Authorization.Application;

/// <summary>
/// Resolves a membership's effective permissions against a target organization
/// (ADR-006).
///
/// Effective permissions are the union over every assignment whose scope covers
/// the target. Parent-organization ownership alone grants nothing: only an
/// assignment that actually reaches the target does, which is what keeps
/// hierarchy from silently implying authority (DOMAIN_RULES §4.8).
/// </summary>
internal sealed class PermissionEvaluator(
    AuthorizationDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IOrganizationHierarchyQuery hierarchyQuery) : IPermissionEvaluator
{
    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid membershipId,
        Guid targetOrganizationId,
        CancellationToken cancellationToken)
    {
        // Nested inside any transaction already in progress (ADR-026), so the
        // RLS session context is established and the hierarchy lookup below
        // joins the same unit of work rather than opening a second one.
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var assignments = await dbContext.MembershipRoleAssignments
            .AsNoTracking()
            .Where(x => x.MembershipId == membershipId)
            .Select(x => new AssignmentProjection(
                x.ScopeType,
                x.AnchorOrganizationId,
                x.SelectedOrganizations.Select(s => s.GrantedOrganizationId).ToList(),
                dbContext.RolePermissions
                    .Where(p => p.RoleId == x.RoleId)
                    .Select(p => p.Permission)
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var effective = new HashSet<string>(StringComparer.Ordinal);

        // Scope checks run inside the same transaction: the hierarchy lookup
        // joins it as a nested scope (ADR-026), so the whole evaluation sees one
        // consistent snapshot rather than reopening a transaction per assignment.
        foreach (var assignment in assignments)
        {
            if (assignment.Permissions.Count == 0)
            {
                continue;
            }

            if (await CoversAsync(assignment, targetOrganizationId, cancellationToken).ConfigureAwait(false))
            {
                effective.UnionWith(assignment.Permissions);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return effective;
    }

    public async Task<bool> HasPermissionAsync(
        Guid membershipId,
        Guid targetOrganizationId,
        string permission,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var effective = await GetEffectivePermissionsAsync(membershipId, targetOrganizationId, cancellationToken)
            .ConfigureAwait(false);

        return effective.Contains(permission);
    }

    private async Task<bool> CoversAsync(
        AssignmentProjection assignment,
        Guid targetOrganizationId,
        CancellationToken cancellationToken) => assignment.ScopeType switch
        {
            RoleScopeType.Organization =>
                assignment.AnchorOrganizationId == targetOrganizationId,

            // Delegated to the Organizations module rather than read from its tables
            // directly (ADR-004): containment is evaluated against the ltree path.
            RoleScopeType.Subtree =>
                await hierarchyQuery
                    .IsSelfOrDescendantAsync(assignment.AnchorOrganizationId, targetOrganizationId, cancellationToken)
                    .ConfigureAwait(false),

            RoleScopeType.SelectedOrganizations =>
                assignment.SelectedOrganizationIds.Contains(targetOrganizationId),

            _ => false,
        };

    private sealed record AssignmentProjection(
        RoleScopeType ScopeType,
        Guid AnchorOrganizationId,
        IReadOnlyList<Guid> SelectedOrganizationIds,
        IReadOnlyList<string> Permissions);
}
