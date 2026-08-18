namespace GiftCardPlatform.Modules.Authorization.Domain;

/// <summary>
/// One organization explicitly granted by a
/// <see cref="RoleScopeType.SelectedOrganizations"/> assignment (ADR-006).
///
/// A separate relation rather than an optional column on the assignment, because
/// the scope is genuinely one-to-many.
/// </summary>
internal sealed class MembershipRoleAssignmentScope
{
    private MembershipRoleAssignmentScope()
    {
        // Rehydration by EF Core.
    }

    private MembershipRoleAssignmentScope(
        Guid id,
        Guid membershipRoleAssignmentId,
        Guid organizationId,
        Guid grantedOrganizationId)
    {
        Id = id;
        MembershipRoleAssignmentId = membershipRoleAssignmentId;
        OrganizationId = organizationId;
        GrantedOrganizationId = grantedOrganizationId;
    }

    public Guid Id { get; private set; }

    public Guid MembershipRoleAssignmentId { get; private set; }

    /// <summary>Denormalized from the assignment. Tenant key for RLS isolation.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>The organization this assignment reaches.</summary>
    public Guid GrantedOrganizationId { get; private set; }

    internal static MembershipRoleAssignmentScope Create(
        MembershipRoleAssignment assignment,
        Guid grantedOrganizationId) =>
        new(Guid.CreateVersion7(), assignment.Id, assignment.OrganizationId, grantedOrganizationId);
}
