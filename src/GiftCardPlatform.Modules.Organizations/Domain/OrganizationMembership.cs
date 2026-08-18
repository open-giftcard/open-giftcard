using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Organizations.Domain;

internal enum OrganizationMembershipStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// The relationship between a global user and a customer organization — the first
/// tenant-owned record (ADR-005). Every membership carries the owning
/// <see cref="OrganizationId"/>, which is what the PostgreSQL RLS policy isolates.
///
/// A membership and the global user account have separate lifecycles: disabling a
/// membership prevents access to that organization without disabling the user
/// (DOMAIN_RULES §3). The user reference is a UUID without a cross-module
/// database foreign key; application provisioning verifies it through the
/// Identity contract.
/// </summary>
internal sealed class OrganizationMembership
{
    private OrganizationMembership()
    {
        // Rehydration by EF Core.
    }

    private OrganizationMembership(
        Guid id,
        Guid organizationId,
        Guid userId,
        OrganizationMembershipStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? disabledAtUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        UserId = userId;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        DisabledAtUtc = disabledAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The owning organization. Tenant key for RLS isolation.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Global Identity user reference.</summary>
    public Guid UserId { get; private set; }

    public OrganizationMembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public bool IsActive => Status == OrganizationMembershipStatus.Active;

    /// <summary>Creates an active membership for a user in an organization.</summary>
    public static OrganizationMembership Create(
        Guid organizationId,
        Guid userId,
        DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "membership.organization.required",
                "An organization is required.");
        }

        if (userId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "membership.user.required",
                "A user is required.");
        }

        return new OrganizationMembership(
            Guid.CreateVersion7(),
            organizationId,
            userId,
            OrganizationMembershipStatus.Active,
            createdAtUtc.ToUniversalTime(),
            disabledAtUtc: null);
    }

    /// <summary>
    /// Disables an active membership. Disabling an already-disabled membership is
    /// rejected so the caller does not record a misleading audit event.
    /// </summary>
    public void Disable(DateTimeOffset disabledAtUtc)
    {
        if (Status == OrganizationMembershipStatus.Disabled)
        {
            throw new ConflictException(
                "membership.already_disabled",
                "The membership is already disabled.");
        }

        Status = OrganizationMembershipStatus.Disabled;
        DisabledAtUtc = disabledAtUtc.ToUniversalTime();
    }
}
