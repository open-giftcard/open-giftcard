namespace GiftCardPlatform.Modules.Organizations.Contracts;

/// <summary>
/// Request to create a membership. The owning organization is never taken from
/// the request body — it is derived from the trusted execution context and the
/// route (ADR-005, ADR-020).
/// </summary>
public sealed record CreateMembershipRequest(Guid UserId);

/// <summary>Public view of an organization membership. Never an EF Core entity.</summary>
public sealed record MembershipResult(
    Guid Id,
    Guid OrganizationId,
    Guid UserId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DisabledAtUtc);

/// <summary>
/// The Organizations module's public contract for organization memberships, the
/// first tenant-owned records. Tenant isolation is enforced below this contract
/// (application authorization plus PostgreSQL RLS), so it stays valid when a
/// handler is invoked outside HTTP (ADR-004, ADR-011).
/// </summary>
public interface IMembershipService
{
    /// <summary>
    /// Creates a membership in the caller's own organization. Requires the
    /// <c>organization.memberships.create</c> permission; the target organization
    /// must equal the caller's active organization.
    /// </summary>
    Task<MembershipResult> CreateMembershipAsync(
        Guid organizationId,
        CreateMembershipRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists memberships for an organization. An organization-scoped caller may
    /// read only its own organization (<c>organization.memberships.view</c>); a
    /// platform operator may read any organization through the controlled RLS path
    /// (<c>platform.organizations.memberships.view</c>).
    /// </summary>
    Task<PagedResult<MembershipResult>> ListMembershipsAsync(
        Guid organizationId,
        PageRequest page,
        CancellationToken cancellationToken);

    /// <summary>
    /// Disables a membership in the caller's own organization. Requires the
    /// <c>organization.memberships.disable</c> permission.
    /// </summary>
    Task<MembershipResult> DisableMembershipAsync(
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken);
}

public interface IInitialAdministratorMembershipProvisioner
{
    Task<MembershipResult> EnsureActiveRootMembershipAsync(
        Guid organizationId,
        Guid userId,
        CancellationToken cancellationToken);
}
