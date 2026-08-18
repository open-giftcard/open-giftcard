namespace GiftCardPlatform.Modules.Authorization.Domain;

/// <summary>
/// A named permission granted to an organization role. Tenant-scoped: it carries
/// the owning organization so the RLS policy has a key on the row itself rather
/// than through a join (ADR-005).
/// </summary>
internal sealed class RolePermission
{
    private RolePermission()
    {
        // Rehydration by EF Core.
        Permission = null!;
    }

    private RolePermission(Guid id, Guid roleId, Guid organizationId, string permission)
    {
        Id = id;
        RoleId = roleId;
        OrganizationId = organizationId;
        Permission = permission;
    }

    public Guid Id { get; private set; }

    public Guid RoleId { get; private set; }

    /// <summary>Denormalized from the role. Tenant key for RLS isolation.</summary>
    public Guid OrganizationId { get; private set; }

    public string Permission { get; private set; }

    internal static RolePermission Create(Role role, string permission) =>
        new(Guid.CreateVersion7(), role.Id, role.OrganizationId, permission);
}
