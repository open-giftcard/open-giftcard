using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Authorization.Domain;

/// <summary>
/// How far an assignment reaches (ADR-006). Scope lives here, on the assignment,
/// not on the role, so one role definition is reusable at different scopes.
/// </summary>
internal enum RoleScopeType
{
    /// <summary>The anchor organization only.</summary>
    Organization = 1,

    /// <summary>The anchor organization and every descendant, via the hierarchy path.</summary>
    Subtree = 2,

    /// <summary>One or more explicitly granted organizations, listed separately.</summary>
    SelectedOrganizations = 3,
}

/// <summary>
/// Assigns an organization role to a membership within a scope (ADR-006).
///
/// The role and the membership must belong to the same organization: a role from
/// one organization can never be assigned to a membership in another
/// (DOMAIN_RULES §4.4).
/// </summary>
internal sealed class MembershipRoleAssignment
{
    private readonly List<MembershipRoleAssignmentScope> _selectedOrganizations = [];

    private MembershipRoleAssignment()
    {
        // Rehydration by EF Core.
    }

    private MembershipRoleAssignment(
        Guid id,
        Guid organizationId,
        Guid membershipId,
        Guid roleId,
        RoleScopeType scopeType,
        Guid anchorOrganizationId,
        DateTimeOffset createdAtUtc,
        Guid createdByUserId)
    {
        Id = id;
        OrganizationId = organizationId;
        MembershipId = membershipId;
        RoleId = roleId;
        ScopeType = scopeType;
        AnchorOrganizationId = anchorOrganizationId;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }

    /// <summary>The organization owning both the role and the membership. Tenant key for RLS.</summary>
    public Guid OrganizationId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }

    public RoleScopeType ScopeType { get; private set; }

    /// <summary>
    /// The organization the scope is anchored at. Meaningful for
    /// <see cref="RoleScopeType.Organization"/> and
    /// <see cref="RoleScopeType.Subtree"/>; for
    /// <see cref="RoleScopeType.SelectedOrganizations"/> it records where the
    /// assignment was made, and the grant list is authoritative.
    /// </summary>
    public Guid AnchorOrganizationId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public IReadOnlyCollection<MembershipRoleAssignmentScope> SelectedOrganizations => _selectedOrganizations;

    public static MembershipRoleAssignment Create(
        Guid organizationId,
        Guid membershipId,
        Guid roleId,
        RoleScopeType scopeType,
        Guid anchorOrganizationId,
        IReadOnlyCollection<Guid>? selectedOrganizationIds,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ValidationFailedException("role_assignment.membership.required", "A membership is required.");
        }

        if (roleId == Guid.Empty)
        {
            throw new ValidationFailedException("role_assignment.role.required", "A role is required.");
        }

        if (anchorOrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "role_assignment.anchor.required",
                "An anchor organization is required.");
        }

        if (!Enum.IsDefined(scopeType))
        {
            throw new ValidationFailedException("role_assignment.scope_type.invalid", "Unknown scope type.");
        }

        var assignment = new MembershipRoleAssignment(
            Guid.CreateVersion7(),
            organizationId,
            membershipId,
            roleId,
            scopeType,
            anchorOrganizationId,
            createdAtUtc.ToUniversalTime(),
            createdByUserId);

        if (scopeType == RoleScopeType.SelectedOrganizations)
        {
            // A genuine one-to-many relation, never a single optional identifier
            // (ADR-006). An empty list would grant nothing, which is a mistake
            // rather than a meaningful assignment.
            if (selectedOrganizationIds is null || selectedOrganizationIds.Count == 0)
            {
                throw new ValidationFailedException(
                    "role_assignment.selected_organizations.required",
                    "SelectedOrganizations scope requires at least one organization.");
            }

            foreach (var organization in selectedOrganizationIds.Distinct())
            {
                if (organization == Guid.Empty)
                {
                    throw new ValidationFailedException(
                        "role_assignment.selected_organizations.invalid",
                        "A selected organization identifier is invalid.");
                }

                assignment._selectedOrganizations.Add(
                    MembershipRoleAssignmentScope.Create(assignment, organization));
            }
        }
        else if (selectedOrganizationIds is { Count: > 0 })
        {
            throw new ValidationFailedException(
                "role_assignment.selected_organizations.not_applicable",
                "Selected organizations apply only to SelectedOrganizations scope.");
        }

        return assignment;
    }
}
