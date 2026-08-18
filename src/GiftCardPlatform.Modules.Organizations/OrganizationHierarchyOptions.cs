using GiftCardPlatform.Modules.Organizations.Domain;

namespace GiftCardPlatform.Modules.Organizations;

/// <summary>
/// Configurable organization-hierarchy limits (ADR-010). Read from the
/// <c>Organizations:Hierarchy</c> configuration section when one is supplied, and
/// validated at registration by <c>AddOrganizationsModule</c>.
/// </summary>
public sealed class OrganizationHierarchyOptions
{
    public const string SectionName = "Organizations:Hierarchy";

    /// <summary>
    /// Maximum number of customer-hierarchy levels. The platform scope is
    /// not counted (ADR-021).
    ///
    /// Valid range is 1 to <see cref="OrganizationHierarchy.DefaultMaxDepth"/>,
    /// the ceiling enforced by the <c>ck_organizations_max_depth</c> check
    /// constraint. Raising it beyond that requires a migration widening the
    /// constraint, so configuration alone cannot exceed what the database
    /// accepts.
    /// </summary>
    public int MaxDepth { get; set; } = OrganizationHierarchy.DefaultMaxDepth;
}
