namespace GiftCardPlatform.Modules.Organizations.Domain;

/// <summary>
/// Hierarchy path helpers for the accepted PostgreSQL <c>ltree</c>
/// representation (ADR-010).
///
/// An ltree label may contain only letters, digits, and underscores, so a UUID's
/// hyphens cannot be used directly. Labels are therefore a fixed prefix plus the
/// UUID in "N" format, which is deterministic and reversible.
/// </summary>
internal static class OrganizationHierarchy
{
    public const string LabelPrefix = "org_";

    /// <summary>Root organizations sit at depth zero.</summary>
    public const int RootDepth = 0;

    /// <summary>
    /// Accepted maximum customer hierarchy depth in levels (ADR-010). The depth
    /// column is zero-based, so this many levels means a deepest stored depth of
    /// <c>DefaultMaxDepth - 1</c>, which is what
    /// <c>ck_organizations_max_depth</c> enforces in the database. Raising the
    /// configured limit beyond this requires a migration to widen that
    /// constraint.
    /// </summary>
    public const int DefaultMaxDepth = 5;

    /// <summary>Builds the ltree label for a single organization.</summary>
    public static string CreateLabel(Guid organizationId) =>
        LabelPrefix + organizationId.ToString("N");

    /// <summary>Builds the full ltree path for a root organization, which is just its own label.</summary>
    public static string CreateRootPath(Guid organizationId) => CreateLabel(organizationId);

    /// <summary>
    /// Builds the ltree path of a child by appending its own label to the
    /// parent's materialized path.
    /// </summary>
    public static string CreateChildPath(string parentPath, Guid childOrganizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        return parentPath + "." + CreateLabel(childOrganizationId);
    }
}
