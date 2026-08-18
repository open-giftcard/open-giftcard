namespace GiftCardPlatform.Modules.Organizations.Contracts;

public sealed record ActiveMembershipResolution(
    Guid MembershipId,
    Guid TenantRootOrganizationId);

/// <summary>
/// Authentication-facing lookup for the active organization membership.
/// The caller's requested organization is already present in the execution
/// context so PostgreSQL RLS remains authoritative during the lookup.
/// </summary>
public interface IActiveMembershipResolver
{
    /// <summary>
    /// Returns the active membership and its verified tenant root for
    /// <paramref name="userId"/> in <paramref name="organizationId"/>, or
    /// <see langword="null"/> when no active membership exists. The database
    /// uniqueness constraint guarantees at most one result.
    /// </summary>
    Task<ActiveMembershipResolution?> ResolveActiveMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken);
}
