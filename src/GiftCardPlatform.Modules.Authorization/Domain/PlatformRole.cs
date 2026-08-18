using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Authorization.Domain;

internal sealed class PlatformRole
{
    public const int NameMaxLength = 100;

    private readonly List<PlatformRolePermission> _permissions = [];

    private PlatformRole()
    {
        Name = string.Empty;
    }

    private PlatformRole(
        Guid id,
        string name,
        bool isSystem,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        IsSystem = isSystem;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public bool IsSystem { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<PlatformRolePermission> Permissions => _permissions;

    public static PlatformRole CreateSystem(string? name, DateTimeOffset createdAtUtc)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is < 2 or > NameMaxLength)
        {
            throw new ValidationFailedException(
                "platform_role.name.invalid",
                $"Platform role name must be between 2 and {NameMaxLength} characters.");
        }

        return new PlatformRole(
            Guid.CreateVersion7(),
            normalized,
            isSystem: true,
            createdAtUtc.ToUniversalTime());
    }

    public void Grant(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        if (_permissions.Exists(x =>
                string.Equals(x.Permission, permission, StringComparison.Ordinal)))
        {
            return;
        }

        _permissions.Add(PlatformRolePermission.Create(this, permission));
    }
}
