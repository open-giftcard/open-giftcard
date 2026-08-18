namespace GiftCardPlatform.Modules.Organizations.Contracts;

/// <summary>
/// Hierarchy questions the Organizations module answers for other modules.
///
/// Authorization needs this to evaluate ADR-006 <c>Subtree</c> scope. It asks
/// through this contract rather than querying the organizations tables, because
/// a module must never read another module's persistence (ADR-004, ADR-011).
/// </summary>
public interface IOrganizationHierarchyQuery
{
    /// <summary>
    /// True when <paramref name="targetOrganizationId"/> is
    /// <paramref name="anchorOrganizationId"/> itself or any descendant of it.
    ///
    /// Evaluated against the materialized <c>ltree</c> path, so depth costs
    /// nothing. Returns false when either organization is not visible to the
    /// caller — tenant isolation still applies to this question.
    /// </summary>
    Task<bool> IsSelfOrDescendantAsync(
        Guid anchorOrganizationId,
        Guid targetOrganizationId,
        CancellationToken cancellationToken);
}
