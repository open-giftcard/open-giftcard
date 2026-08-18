using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Organizations.Domain;

internal enum OrganizationStatus
{
    Active = 1,
    Suspended = 2,
    Disabled = 3,
}

/// <summary>
/// A customer organization. The platform operator is not represented here: the platform is
/// a distinct scope, not a row in the customer hierarchy (ADR-021).
///
/// Root organizations are platform-created; subsidiaries are created by the
/// owning customer organization (IMPL-003). Reparenting, updates, and deletion
/// remain out of scope.
/// </summary>
internal sealed class Organization
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 200;

    private Organization()
    {
        // Rehydration by EF Core.
        Name = null!;
        Code = null!;
        HierarchyPath = null!;
    }

    private Organization(
        Guid id,
        string name,
        string code,
        OrganizationStatus status,
        Guid? parentOrganizationId,
        Guid rootOrganizationId,
        string hierarchyPath,
        int depth,
        DateTimeOffset createdAtUtc,
        Guid createdByUserId)
    {
        Id = id;
        Name = name;
        Code = code;
        Status = status;
        ParentOrganizationId = parentOrganizationId;
        RootOrganizationId = rootOrganizationId;
        HierarchyPath = hierarchyPath;
        Depth = depth;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    /// <summary>Normalized, unique organization code.</summary>
    public string Code { get; private set; }

    public OrganizationStatus Status { get; private set; }

    /// <summary>Null for a root customer organization.</summary>
    public Guid? ParentOrganizationId { get; private set; }

    /// <summary>
    /// The root of this organization's hierarchy — the owning customer. A root
    /// organization is its own root. This is the tenant namespace within which
    /// subsidiary codes are unique, so one customer's codes can never collide
    /// with, or disclose the existence of, another customer's (ADR-024).
    /// </summary>
    public Guid RootOrganizationId { get; private set; }

    /// <summary>PostgreSQL <c>ltree</c> materialized path.</summary>
    public string HierarchyPath { get; private set; }

    public int Depth { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>
    /// Creates a root customer organization: no parent, depth zero, and a
    /// hierarchy path consisting of its own label.
    /// </summary>
    public static Organization CreateRoot(
        string? name,
        string? code,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc)
    {
        var normalizedName = NormalizeAndValidateName(name);
        var normalizedCode = OrganizationCode.NormalizeAndValidate(code);

        if (createdByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "organization.created_by.required",
                "A creating user is required.");
        }

        var id = Guid.CreateVersion7();

        return new Organization(
            id,
            normalizedName,
            normalizedCode,
            OrganizationStatus.Active,
            parentOrganizationId: null,
            // A root organization is its own tenant root.
            rootOrganizationId: id,
            hierarchyPath: OrganizationHierarchy.CreateRootPath(id),
            depth: OrganizationHierarchy.RootDepth,
            createdAtUtc: createdAtUtc.ToUniversalTime(),
            createdByUserId: createdByUserId);
    }

    /// <summary>
    /// Creates a subsidiary beneath <paramref name="parent"/>: depth one below the
    /// parent, and a hierarchy path extending the parent's materialized path
    /// (ADR-010).
    ///
    /// <paramref name="maxDepth"/> is the configured number of allowed levels.
    /// Depth is zero-based, so a new organization is rejected once its depth
    /// would reach that count. A cycle cannot arise here because the new
    /// organization is always a fresh leaf; reparenting is out of scope.
    /// </summary>
    public static Organization CreateSubsidiary(
        Organization parent,
        string? name,
        string? code,
        Guid createdByUserId,
        DateTimeOffset createdAtUtc,
        int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (parent.Status != OrganizationStatus.Active)
        {
            throw new ValidationFailedException(
                "organization.parent.not_active",
                "A subsidiary can only be created under an active organization.");
        }

        var normalizedName = NormalizeAndValidateName(name);
        var normalizedCode = OrganizationCode.NormalizeAndValidate(code);

        if (createdByUserId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "organization.created_by.required",
                "A creating user is required.");
        }

        var depth = parent.Depth + 1;

        if (depth >= maxDepth)
        {
            throw new ValidationFailedException(
                "organization.hierarchy.max_depth_exceeded",
                $"The organization hierarchy is limited to {maxDepth} levels.");
        }

        var id = Guid.CreateVersion7();

        return new Organization(
            id,
            normalizedName,
            normalizedCode,
            OrganizationStatus.Active,
            parentOrganizationId: parent.Id,
            // Inherited, so an entire customer subtree shares one code namespace.
            rootOrganizationId: parent.RootOrganizationId,
            hierarchyPath: OrganizationHierarchy.CreateChildPath(parent.HierarchyPath, id),
            depth: depth,
            createdAtUtc: createdAtUtc.ToUniversalTime(),
            createdByUserId: createdByUserId);
    }

    private static string NormalizeAndValidateName(string? name)
    {
        var normalized = name?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new ValidationFailedException("organization.name.required", "Organization name is required.");
        }

        if (normalized.Length is < NameMinLength or > NameMaxLength)
        {
            throw new ValidationFailedException(
                "organization.name.invalid_length",
                $"Organization name must be between {NameMinLength} and {NameMaxLength} characters.");
        }

        return normalized;
    }
}
