using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Authorization.Domain;
using GiftCardPlatform.Modules.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Authorization.Application;

/// <summary>
/// Management of organization roles and their assignments (ADR-006).
///
/// Authorization is enforced here, below the controller layer. The owning
/// organization always comes from the trusted execution context, never the
/// request body, and PostgreSQL RLS enforces the same boundary underneath.
/// </summary>
internal sealed class RoleService(
    AuthorizationDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOrganizationPermissionAuthorizer permissionAuthorizer,
    TimeProvider timeProvider) : IRoleService
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    public async Task<RoleResult> CreateRoleAsync(
        Guid organizationId,
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.RoleCreate,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var role = Role.Create(
            organizationId,
            request.Name,
            executionContext.UserId!.Value,
            timeProvider.GetUtcNow());

        dbContext.Roles.Add(role);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new ConflictException("role.duplicate", "A role with this name already exists.");
        }

        await RecordAsync(
            AuditOperations.RoleCreated,
            nameof(Role),
            role.Id,
            role.OrganizationId,
            new Dictionary<string, string> { ["name"] = role.Name },
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RoleResult(role.Id, role.OrganizationId, role.Name, [], role.CreatedAtUtc);
    }

    public async Task<IReadOnlyList<RoleResult>> ListRolesAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.RoleView,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var roles = await dbContext.Roles
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .Select(x => new RoleResult(
                x.Id,
                x.OrganizationId,
                x.Name,
                x.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList(),
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return roles;
    }

    public async Task<IReadOnlyList<RoleAssignmentResult>> ListRoleAssignmentsAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.RoleView,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var assignments = await dbContext.MembershipRoleAssignments
            .AsNoTracking()
            .Where(assignment => assignment.OrganizationId == organizationId)
            .OrderBy(assignment => assignment.CreatedAtUtc)
            .ThenBy(assignment => assignment.Id)
            .Select(assignment => new RoleAssignmentResult(
                assignment.Id,
                assignment.OrganizationId,
                assignment.MembershipId,
                assignment.RoleId,
                assignment.ScopeType.ToString(),
                assignment.AnchorOrganizationId,
                assignment.SelectedOrganizations
                    .OrderBy(scope => scope.GrantedOrganizationId)
                    .Select(scope => scope.GrantedOrganizationId)
                    .ToList(),
                assignment.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return assignments;
    }

    public async Task<RoleResult> GrantPermissionsAsync(
        Guid organizationId,
        Guid roleId,
        GrantPermissionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = (request.Permissions ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (requested.Count == 0)
        {
            throw new ValidationFailedException(
                "role.permissions.required",
                "At least one permission is required.");
        }

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.RoleManagePermissions,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var definedPermissions = await dbContext.PermissionDefinitions
            .AsNoTracking()
            .Where(x => requested.Contains(x.Name))
            .Select(x => x.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (definedPermissions.Count != requested.Count)
        {
            throw new ValidationFailedException(
                "role.permissions.unknown",
                "One or more permissions are not defined.");
        }

        // A caller must not grant a platform permission to a customer role
        // (DOMAIN_RULES §4.12), and must not grant a permission they do not hold
        // themselves (§4.7).
        foreach (var permission in requested)
        {
            if (PlatformPermissions.IsKnown(permission))
            {
                throw new ValidationFailedException(
                    "role.permissions.platform_not_grantable",
                    "Platform permissions cannot be granted to an organization role.");
            }

            await permissionAuthorizer.RequirePermissionAsync(
                organizationId,
                permission,
                cancellationToken).ConfigureAwait(false);
        }

        var role = await dbContext.Roles
            .Include(x => x.Permissions)
            .SingleOrDefaultAsync(x => x.Id == roleId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("role.not_found", "Role not found.");

        foreach (var permission in requested)
        {
            role.Grant(permission);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: ForeignKeyViolation })
        {
            // The catalogue is the authority on what names exist.
            throw new ValidationFailedException(
                "role.permissions.unknown",
                "One or more permissions are not defined.");
        }

        await RecordAsync(
            AuditOperations.RolePermissionsGranted,
            nameof(Role),
            role.Id,
            role.OrganizationId,
            new Dictionary<string, string> { ["permissions"] = string.Join(',', requested) },
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RoleResult(
            role.Id,
            role.OrganizationId,
            role.Name,
            [.. role.Permissions.Select(x => x.Permission).OrderBy(x => x, StringComparer.Ordinal)],
            role.CreatedAtUtc);
    }

    public async Task<RoleAssignmentResult> AssignRoleAsync(
        Guid organizationId,
        AssignRoleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.RoleAssign,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var anchor = request.AnchorOrganizationId ?? organizationId;

        // The role must exist and belong to the caller's organization. RLS
        // already hides other tenants' roles, so this doubles as the
        // cross-organization assignment check (DOMAIN_RULES §4.4).
        var role = await dbContext.Roles
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.RoleId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("role.not_found", "Role not found.");

        if (role.OrganizationId != organizationId)
        {
            throw new ForbiddenException(
                "role.assignment.cross_organization",
                "A role cannot be assigned outside its owning organization.");
        }

        var assignment = MembershipRoleAssignment.Create(
            organizationId,
            request.MembershipId,
            request.RoleId,
            ToDomainScope(request.Scope),
            anchor,
            request.SelectedOrganizationIds,
            executionContext.UserId!.Value,
            timeProvider.GetUtcNow());

        dbContext.MembershipRoleAssignments.Add(assignment);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new ConflictException(
                "role.assignment.duplicate",
                "The role is already assigned to this membership at this anchor.");
        }

        await RecordAsync(
            AuditOperations.RoleAssigned,
            nameof(MembershipRoleAssignment),
            assignment.Id,
            assignment.OrganizationId,
            new Dictionary<string, string>
            {
                ["membership_id"] = assignment.MembershipId.ToString(),
                ["role_id"] = assignment.RoleId.ToString(),
                ["scope"] = assignment.ScopeType.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToResult(assignment);
    }

    private async Task RecordAsync(
        string operation,
        string entityType,
        Guid entityId,
        Guid organizationScopeId,
        Dictionary<string, string> metadata,
        CancellationToken cancellationToken) =>
        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: executionContext.UserId!.Value,
                ActorType: AuditActorType.OrganizationMember,
                ActorMembershipId: executionContext.ActiveMembershipId,
                OrganizationScopeId: organizationScopeId,
                Operation: operation,
                EntityType: entityType,
                EntityId: entityId.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: metadata),
            cancellationToken).ConfigureAwait(false);

    private static RoleScopeType ToDomainScope(RoleScope scope) => scope switch
    {
        RoleScope.Organization => RoleScopeType.Organization,
        RoleScope.Subtree => RoleScopeType.Subtree,
        RoleScope.SelectedOrganizations => RoleScopeType.SelectedOrganizations,
        _ => throw new ValidationFailedException("role_assignment.scope_type.invalid", "Unknown scope type."),
    };

    private static RoleAssignmentResult ToResult(MembershipRoleAssignment assignment) => new(
        assignment.Id,
        assignment.OrganizationId,
        assignment.MembershipId,
        assignment.RoleId,
        assignment.ScopeType.ToString(),
        assignment.AnchorOrganizationId,
        [.. assignment.SelectedOrganizations.Select(x => x.GrantedOrganizationId)],
        assignment.CreatedAtUtc);
}
