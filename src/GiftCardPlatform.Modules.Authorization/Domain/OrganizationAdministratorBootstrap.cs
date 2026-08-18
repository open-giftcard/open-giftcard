namespace GiftCardPlatform.Modules.Authorization.Domain;

internal sealed class OrganizationAdministratorBootstrap
{
    private OrganizationAdministratorBootstrap()
    {
    }

    private OrganizationAdministratorBootstrap(
        Guid organizationId,
        Guid userId,
        Guid membershipId,
        Guid roleId,
        Guid roleAssignmentId,
        DateTimeOffset assignedAtUtc)
    {
        OrganizationId = organizationId;
        UserId = userId;
        MembershipId = membershipId;
        RoleId = roleId;
        RoleAssignmentId = roleAssignmentId;
        AssignedAtUtc = assignedAtUtc;
    }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid MembershipId { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid RoleAssignmentId { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    public static OrganizationAdministratorBootstrap Create(
        Guid organizationId,
        Guid userId,
        Guid membershipId,
        Guid roleId,
        Guid roleAssignmentId,
        DateTimeOffset assignedAtUtc) =>
        new(
            organizationId,
            userId,
            membershipId,
            roleId,
            roleAssignmentId,
            assignedAtUtc.ToUniversalTime());
}
