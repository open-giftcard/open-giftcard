namespace GiftCardPlatform.Modules.Authorization.Domain;

/// <summary>
/// A global permission definition — the catalogue of names a role may be granted
/// (ADR-005 "global" tenancy category: no <c>organization_id</c>, no RLS).
///
/// Seeded from the constants in the Contracts assembly so the catalogue has one
/// source of truth. Its purpose is referential: a grant of an unknown permission
/// is rejected by a foreign key rather than silently stored as a string that
/// nothing will ever match.
/// </summary>
internal sealed class PermissionDefinition
{
    private PermissionDefinition()
    {
        // Rehydration by EF Core.
        Name = null!;
    }

    private PermissionDefinition(string name, bool isPlatformPermission)
    {
        Name = name;
        IsPlatformPermission = isPlatformPermission;
    }

    /// <summary>The permission name, and the primary key: names are the identity.</summary>
    public string Name { get; private set; }

    /// <summary>
    /// Distinguishes platform permissions from customer-organization ones
    /// (DOMAIN_RULES §4.12). Only organization permissions may be granted to an
    /// organization role.
    /// </summary>
    public bool IsPlatformPermission { get; private set; }

    public static PermissionDefinition Create(string name, bool isPlatformPermission) =>
        new(name, isPlatformPermission);
}
