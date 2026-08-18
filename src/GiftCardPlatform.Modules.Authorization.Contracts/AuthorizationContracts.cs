namespace GiftCardPlatform.Modules.Authorization.Contracts;

/// <summary>
/// How far a role assignment reaches (ADR-006). Stored on the assignment, not on
/// the role, so one role is reusable at different scopes.
/// </summary>
public enum RoleScope
{
    /// <summary>The anchor organization only.</summary>
    Organization = 1,

    /// <summary>The anchor organization and every descendant.</summary>
    Subtree = 2,

    /// <summary>One or more explicitly granted organizations.</summary>
    SelectedOrganizations = 3,
}

/// <summary>Request to create a role. The owning organization comes from context.</summary>
public sealed record CreateRoleRequest(string? Name);

/// <summary>Request to grant permissions to a role. Granting twice is a no-op.</summary>
public sealed record GrantPermissionsRequest(IReadOnlyList<string>? Permissions);

/// <summary>
/// Request to assign a role to a membership. The anchor defaults to the caller's
/// active organization when omitted.
/// </summary>
public sealed record AssignRoleRequest(
    Guid MembershipId,
    Guid RoleId,
    RoleScope Scope,
    Guid? AnchorOrganizationId = null,
    IReadOnlyList<Guid>? SelectedOrganizationIds = null);

/// <summary>Public view of a role. Never an EF Core entity.</summary>
public sealed record RoleResult(
    Guid Id,
    Guid OrganizationId,
    string Name,
    IReadOnlyList<string> Permissions,
    DateTimeOffset CreatedAtUtc);

/// <summary>Public view of a role assignment.</summary>
public sealed record RoleAssignmentResult(
    Guid Id,
    Guid OrganizationId,
    Guid MembershipId,
    Guid RoleId,
    string Scope,
    Guid AnchorOrganizationId,
    IReadOnlyList<Guid> SelectedOrganizationIds,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// The Authorization module's management surface for organization roles
/// (ADR-004, ADR-006). Tenant scope is enforced below this contract by the
/// application service and again by PostgreSQL RLS.
/// </summary>
public interface IRoleService
{
    /// <summary>Creates a role in the caller's organization. Requires <c>role.create</c>.</summary>
    Task<RoleResult> CreateRoleAsync(
        Guid organizationId,
        CreateRoleRequest request,
        CancellationToken cancellationToken);

    /// <summary>Lists the roles of the caller's organization. Requires <c>role.view</c>.</summary>
    Task<IReadOnlyList<RoleResult>> ListRolesAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the role assignments owned by the caller's organization. Requires
    /// <c>role.view</c>.
    /// </summary>
    Task<IReadOnlyList<RoleAssignmentResult>> ListRoleAssignmentsAsync(
        Guid organizationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Grants permissions to a role. Requires <c>role.manage_permissions</c>.
    /// Only organization permissions may be granted, and only names present in
    /// the seeded catalogue.
    /// </summary>
    Task<RoleResult> GrantPermissionsAsync(
        Guid organizationId,
        Guid roleId,
        GrantPermissionsRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Assigns a role to a membership within a scope. Requires <c>role.assign</c>.
    /// A role from one organization can never be assigned to a membership in
    /// another (DOMAIN_RULES §4.4).
    /// </summary>
    Task<RoleAssignmentResult> AssignRoleAsync(
        Guid organizationId,
        AssignRoleRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves what a membership may actually do (ADR-006).
///
/// Effective permissions are the union of every assigned role whose scope covers
/// the target organization. Parent-organization ownership alone never grants
/// access: only an assignment whose scope reaches the target does.
/// </summary>
public interface IPermissionEvaluator
{
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid membershipId,
        Guid targetOrganizationId,
        CancellationToken cancellationToken);

    Task<bool> HasPermissionAsync(
        Guid membershipId,
        Guid targetOrganizationId,
        string permission,
        CancellationToken cancellationToken);
}

/// <summary>
/// Application-service authorization boundary for organization-scoped
/// operations. It evaluates the verified active membership against the business
/// target and throws the standard forbidden application error when access is
/// absent.
/// </summary>
public interface IOrganizationPermissionAuthorizer
{
    Task RequirePermissionAsync(
        Guid targetOrganizationId,
        string permission,
        CancellationToken cancellationToken);
}
