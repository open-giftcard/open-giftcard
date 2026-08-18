using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Authorization.Domain;

internal sealed class PlatformRoleAssignment
{
    private PlatformRoleAssignment()
    {
    }

    private PlatformRoleAssignment(
        Guid id,
        Guid userId,
        Guid roleId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        RoleId = roleId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PlatformRoleAssignment Create(
        Guid userId,
        Guid roleId,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty || roleId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "platform_role_assignment.invalid",
                "A user and platform role are required.");
        }

        return new PlatformRoleAssignment(
            Guid.CreateVersion7(),
            userId,
            roleId,
            createdAtUtc.ToUniversalTime());
    }
}
