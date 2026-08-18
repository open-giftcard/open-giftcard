namespace GiftCardPlatform.BuildingBlocks.Execution;

/// <summary>
/// Trusted, server-side execution context for the current unit of work (ADR-020).
/// Populated by the API authentication adapter for HTTP requests, and explicitly
/// by background jobs and tests. Application and domain code depend on this
/// abstraction and never on <c>HttpContext</c>.
/// </summary>
public interface IExecutionContext
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    bool IsPlatformOperator { get; }

    /// <summary>
    /// True only for an internally constructed background-job context. System
    /// jobs may use the platform RLS path, but remain distinguishable from a
    /// human platform operator for authorization and audit attribution.
    /// </summary>
    bool IsSystem { get; }

    IReadOnlyCollection<string> PlatformPermissions { get; }

    /// <summary>
    /// Verified active membership for an organization-scoped caller. Permission
    /// evaluation always starts here; request-supplied permission names are never
    /// carried in the execution context.
    /// </summary>
    Guid? ActiveMembershipId { get; }

    /// <summary>
    /// The organization an organization-scoped caller is acting within. Trusted,
    /// server-side value: it drives the PostgreSQL RLS session context, so it must
    /// never be taken from a request body (ADR-005, ADR-020).
    /// </summary>
    Guid? ActiveOrganizationId { get; }

    /// <summary>
    /// Root customer organization that owns the active organization's tenant.
    /// This is the data-isolation and financial-funding boundary; it is distinct
    /// from <see cref="ActiveOrganizationId"/>, which is the operational scope
    /// selected by the caller.
    /// </summary>
    Guid? TenantRootOrganizationId { get; }

    /// <summary>
    /// Server-parsed invitation identifier for an anonymous claim attempt.
    /// It grants only the narrow RLS visibility needed to verify the
    /// high-entropy secret; it is never accepted as proof by itself.
    /// </summary>
    Guid? ClaimInvitationId { get; }

    /// <summary>
    /// Server-parsed share identifier for one protected-link operation. The
    /// high-entropy secret and PIN must still be verified; this value grants
    /// only narrow RLS visibility to the candidate share and its transfer.
    /// </summary>
    Guid? ShareId { get; }

    /// <summary>
    /// True for an authenticated point-of-sale caller (ADR-043). A POS client is
    /// deliberately neither a platform operator nor an identity user: it holds
    /// no organization or tenant scope, and being an authenticated till is not
    /// authority to charge any particular card (ADR-017).
    /// </summary>
    bool IsPosClient { get; }

    /// <summary>
    /// Server-parsed payment-credential identifier for one redemption attempt.
    /// It grants only the narrow visibility needed to resolve the credential and
    /// the card it refers to; the 256-bit secret must still be verified in
    /// constant time before value is reserved (ADR-017).
    /// </summary>
    Guid? PaymentTokenId { get; }

    /// <summary>
    /// SHA-256 of one validly shaped numeric payment-code candidate. It grants
    /// only exact token-row lookup; the code, token, card, and POS must still be
    /// verified before value can be reserved (ADR-050).
    /// </summary>
    string? PaymentCodeHash { get; }

    /// <summary>Registered POS client acting on this request.</summary>
    Guid? PosClientId { get; }

    /// <summary>
    /// Registered terminal the POS client is acting at. Recorded with every
    /// payment for reconciliation and dispute handling (ADR-018).
    /// </summary>
    Guid? PosTerminalId { get; }

    /// <summary>
    /// True for an authenticated e-pin reseller caller (ADR-053). Unlike a POS
    /// client, a partner does carry organization and tenant scope, because it
    /// mints against its own prepaid corporate credit. Being an authenticated
    /// partner is still not authority to mint: that comes from scopes on the API
    /// client row, evaluated in the application service.
    /// </summary>
    bool IsPartnerClient { get; }

    /// <summary>Registered partner API client acting on this request.</summary>
    Guid? PartnerClientId { get; }

    /// <summary>
    /// Partner that owns the acting API client. Recorded with every mint so
    /// volume and value are attributable per reseller, not merely per key.
    /// </summary>
    Guid? PartnerId { get; }

    /// <summary>
    /// What the acting partner API client is allowed to do (ADR-053). Resolved
    /// from the client row on every request, exactly as platform permissions
    /// are, so revoking a scope takes effect on the next call.
    /// </summary>
    IReadOnlyCollection<string> PartnerScopes { get; }

    /// <summary>
    /// True only for an authenticated partner client that holds the scope.
    /// Being an authenticated partner is not by itself authority to mint.
    /// </summary>
    bool HasPartnerScope(string scope);

    Guid CorrelationId { get; }

    bool HasPlatformPermission(string permission);
}
