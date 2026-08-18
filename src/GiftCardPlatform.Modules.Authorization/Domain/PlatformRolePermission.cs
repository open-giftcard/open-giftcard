namespace GiftCardPlatform.Modules.Authorization.Domain;

internal sealed class PlatformRolePermission
{
    private PlatformRolePermission()
    {
        Permission = string.Empty;
    }

    private PlatformRolePermission(Guid id, Guid roleId, string permission)
    {
        Id = id;
        RoleId = roleId;
        Permission = permission;
    }

    public Guid Id { get; private set; }

    public Guid RoleId { get; private set; }

    public string Permission { get; private set; }

    internal static PlatformRolePermission Create(PlatformRole role, string permission) =>
        new(Guid.CreateVersion7(), role.Id, permission);
}
