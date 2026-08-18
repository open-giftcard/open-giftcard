namespace GiftCardPlatform.Modules.Organizations.Contracts;

/// <summary>
/// Request to create a subsidiary. The parent is never taken from the request
/// body — it is the caller's active organization, derived from the trusted
/// execution context (ADR-005, ADR-020).
/// </summary>
public sealed record CreateSubsidiaryRequest(string? Name, string? Code);

/// <summary>Public view of a subsidiary organization. Never an EF Core entity.</summary>
public sealed record SubsidiaryResult(
    Guid Id,
    Guid ParentOrganizationId,
    string Name,
    string Code,
    string Status,
    int Depth,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// The Organizations module's public contract for the customer organization
/// hierarchy. Distinct from <see cref="IOrganizationService"/>, which is the
/// platform-scoped surface: these operations are performed by an
/// organization-scoped caller acting within its own organization.
/// </summary>
public interface ISubsidiaryService
{
    /// <summary>
    /// Creates a subsidiary beneath the caller's own organization. Requires the
    /// <c>organization.create_subsidiary</c> permission; the parent must equal
    /// the caller's active organization. Enforces the configured maximum
    /// hierarchy depth (ADR-010).
    /// </summary>
    Task<SubsidiaryResult> CreateSubsidiaryAsync(
        Guid parentOrganizationId,
        CreateSubsidiaryRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists the direct subsidiaries of the caller's own organization. Requires
    /// the <c>organization.view</c> permission. Subtree traversal is deferred to
    /// ADR-006 scope evaluation.
    /// </summary>
    Task<PagedResult<SubsidiaryResult>> ListSubsidiariesAsync(
        Guid parentOrganizationId,
        PageRequest page,
        CancellationToken cancellationToken);
}
