namespace GiftCardPlatform.Modules.Organizations.Contracts;

/// <summary>
/// A requested slice of a list. Offset paging: adequate for administrative
/// listings, and the bound is what matters — an unbounded list endpoint becomes
/// a denial-of-service vector once a customer has tens of thousands of members.
/// </summary>
public sealed record PageRequest(int Limit, int Offset)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static PageRequest Default { get; } = new(DefaultLimit, 0);
}

/// <summary>
/// One page of results. <c>HasMore</c> is derived by reading one row beyond the
/// page, which avoids a COUNT over the whole table on every request.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Limit, int Offset, bool HasMore);

/// <summary>Request to create a root customer organization.</summary>
public sealed record CreateRootOrganizationRequest(string? Name, string? Code);

/// <summary>Public view of an organization. Never an EF Core entity.</summary>
public sealed record OrganizationResult(
    Guid Id,
    string Name,
    string Code,
    string Status,
    int Depth,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Bounded platform-customer discovery request. Search is a literal
/// case-insensitive name/code fragment; status is an optional organization
/// status name.
/// </summary>
public sealed record OrganizationListRequest(
    string? Search,
    string? Status,
    PageRequest Page);

/// <summary>
/// One organization the authenticated user may select. Discovery is based on
/// the user's own active membership and does not grant authority by itself.
/// </summary>
public sealed record UserOrganizationResult(
    Guid MembershipId,
    Guid TenantRootOrganizationId,
    OrganizationResult Organization,
    DateTimeOffset MembershipCreatedAtUtc);

/// <summary>
/// The verified organization context selected through authentication plus the
/// membership's effective permissions against that exact organization.
/// </summary>
public sealed record SelectedOrganizationContextResult(
    Guid MembershipId,
    Guid TenantRootOrganizationId,
    OrganizationResult Organization,
    IReadOnlyList<string> EffectivePermissions);

/// <summary>
/// The Organizations module's public contract. Callers reach the module only
/// through this interface (ADR-004, ADR-011).
/// </summary>
public interface IOrganizationService
{
    /// <summary>
    /// Creates a root customer organization and its audit record in one atomic
    /// transaction. Requires the <c>platform.organizations.create</c> permission.
    /// </summary>
    Task<OrganizationResult> CreateRootOrganizationAsync(
        CreateRootOrganizationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a single organization. Requires the
    /// <c>platform.organizations.view</c> permission.
    /// </summary>
    Task<OrganizationResult> GetOrganizationAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only discovery surface for independent frontend clients. Every query
/// still runs under PostgreSQL RLS and trusted execution context.
/// </summary>
public interface IOrganizationDiscoveryQuery
{
    /// <summary>
    /// Lists root customer organizations for a platform operator with
    /// <c>platform.organizations.view</c>.
    /// </summary>
    Task<PagedResult<OrganizationResult>> ListPlatformOrganizationsAsync(
        OrganizationListRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists only the authenticated user's own active organization memberships.
    /// This is an identity-context operation and must be called without a
    /// selected organization header.
    /// </summary>
    Task<PagedResult<UserOrganizationResult>> ListCurrentUserOrganizationsAsync(
        PageRequest page,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the already verified selected organization and the active
    /// membership's effective permissions against that exact target.
    /// </summary>
    Task<SelectedOrganizationContextResult> GetSelectedOrganizationContextAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow financial eligibility boundary for modules that must verify a
/// corporate-value recipient without reading Organizations persistence.
/// </summary>
public interface IOrganizationFinancialEligibilityQuery
{
    /// <summary>
    /// Returns true only for an active root customer organization visible to the
    /// trusted execution context.
    /// </summary>
    Task<bool> IsActiveRootAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns true when the funding root and operational issuing organization
    /// are both active and the issuer belongs to that root customer hierarchy.
    /// </summary>
    Task<bool> IsActiveIssuingOrganizationAsync(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        CancellationToken cancellationToken);
}
