using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Authorization.Domain;

/// <summary>
/// An organization-specific role (ADR-006). A role belongs to exactly one
/// organization and may never be assigned to a membership in another
/// (DOMAIN_RULES §4.3, §4.4).
///
/// A role carries no scope of its own. Scope lives on the assignment, so the
/// same role definition is reusable at different scopes.
/// </summary>
internal sealed class Role
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 100;

    private readonly List<RolePermission> _permissions = [];

    private Role()
    {
        // Rehydration by EF Core.
        Name = null!;
    }

    private Role(
        Guid id,
        Guid organizationId,
        string name,
        bool isSystem,
        DateTimeOffset createdAtUtc,
        Guid createdByUserId)
    {
        Id = id;
        OrganizationId = organizationId;
        Name = name;
        IsSystem = isSystem;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }

    /// <summary>The owning organization. Tenant key for RLS isolation.</summary>
    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public bool IsSystem { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions;

    public static Role Create(Guid organizationId, string? name, Guid createdByUserId, DateTimeOffset createdAtUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException("role.organization.required", "An organization is required.");
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new ValidationFailedException("role.created_by.required", "A creating user is required.");
        }

        var normalized = name?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new ValidationFailedException("role.name.required", "Role name is required.");
        }

        if (normalized.Length is < NameMinLength or > NameMaxLength)
        {
            throw new ValidationFailedException(
                "role.name.invalid_length",
                $"Role name must be between {NameMinLength} and {NameMaxLength} characters.");
        }

        return new Role(
            Guid.CreateVersion7(),
            organizationId,
            normalized,
            isSystem: false,
            createdAtUtc.ToUniversalTime(),
            createdByUserId);
    }

    internal static Role CreateSystem(
        Guid organizationId,
        string name,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        var role = Create(organizationId, name, createdByUserId, createdAtUtc);
        role.IsSystem = true;
        return role;
    }

    /// <summary>
    /// Grants a permission to this role. Granting the same permission twice is a
    /// no-op rather than an error, so a grant call is idempotent.
    /// </summary>
    public void Grant(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (_permissions.Exists(x => string.Equals(x.Permission, permission, StringComparison.Ordinal)))
        {
            return;
        }

        _permissions.Add(RolePermission.Create(this, permission));
    }
}
