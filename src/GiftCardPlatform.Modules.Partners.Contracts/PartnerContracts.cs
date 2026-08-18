namespace GiftCardPlatform.Modules.Partners.Contracts;

/// <summary>
/// Lifecycle of an e-pin reseller and of one of its API clients. Disabling
/// either is the kill switch: it stops new minting immediately, but it does not
/// invalidate e-pins already sold to the reseller's buyers. Voiding those is a
/// separate, deliberate clawback action scoped to unclaimed cards.
/// </summary>
public enum PartnerStatus
{
    Active = 1,
    Disabled = 2,
}

public enum PartnerApiClientStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// A registered e-pin reseller. <paramref name="RootOrganizationId"/> is the
/// funding tenant: minting debits that organization's prepaid corporate credit,
/// which is the hard ceiling on how much a compromised key can ever produce.
/// </summary>
public sealed record PartnerResult(
    Guid Id,
    Guid RootOrganizationId,
    string Code,
    string DisplayName,
    PartnerStatus Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? DisabledAtUtc);

/// <summary>
/// An API client belonging to a partner. Never carries the secret; see
/// <see cref="RegisteredPartnerApiClientResult"/> for the one-time disclosure.
/// </summary>
public sealed record PartnerApiClientResult(
    Guid Id,
    Guid PartnerId,
    string Code,
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    PartnerApiClientStatus Status,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? DisabledAtUtc);

/// <summary>
/// Returned exactly once, at registration, under <c>Cache-Control: no-store</c>.
/// The platform stores only a SHA-256 digest of <paramref name="Secret"/> and
/// cannot reproduce it; a lost secret is replaced by rotating the client.
/// </summary>
public sealed record RegisteredPartnerApiClientResult(
    PartnerApiClientResult Client,
    string Secret);

/// <summary>
/// What a partner API client is allowed to do (ADR-053).
///
/// A partner has no organization membership, so it cannot use the tenant
/// permission model, and it is not a platform operator either. Authority is
/// therefore explicit on the client row, which also lets one reseller hold a
/// minting key and a separate read-only key without the two sharing a blast
/// radius.
///
/// These are deliberately not in the Authorization permission catalogue: that
/// table is foreign-keyed to grants made to human memberships and platform
/// roles, and a machine credential is neither.
/// </summary>
public static class PartnerScopes
{
    /// <summary>Mint gift cards against the partner's own prepaid float.</summary>
    public const string GiftCardsMint = "partner.gift_cards.mint";

    public static IReadOnlyCollection<string> All { get; } = [GiftCardsMint];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}

public sealed record RegisterPartnerRequest(
    Guid RootOrganizationId,
    string? Code,
    string? DisplayName);

public sealed record RegisterPartnerApiClientRequest(
    string? Code,
    string? DisplayName,
    IReadOnlyCollection<string>? Scopes = null);

/// <summary>
/// Registers partners and their API clients. Every method requires
/// <c>platform.partners.manage</c>; deciding who may mint against a prepaid
/// float is the platform operator's commercial call, not a customer self-service action.
/// </summary>
public interface IPartnerRegistrationService
{
    Task<PartnerResult> RegisterAsync(RegisterPartnerRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<PartnerResult>> GetPartnersAsync(CancellationToken cancellationToken);

    Task<RegisteredPartnerApiClientResult> RegisterClientAsync(
        Guid partnerId,
        RegisterPartnerApiClientRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PartnerApiClientResult>> GetClientsAsync(
        Guid partnerId,
        CancellationToken cancellationToken);

    /// <summary>Kill switch for one credential. Already-sold e-pins are unaffected.</summary>
    Task<PartnerApiClientResult> DisableClientAsync(Guid partnerId, Guid clientId, CancellationToken cancellationToken);

    /// <summary>Kill switch for a whole reseller. Already-sold e-pins are unaffected.</summary>
    Task<PartnerResult> DisablePartnerAsync(Guid partnerId, CancellationToken cancellationToken);
}

public sealed record PartnerAccessTokenRequest(string? ClientCode, string? ClientSecret);

public sealed record PartnerAccessTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid PartnerId,
    Guid PartnerClientId);

public interface IPartnerAuthenticationService
{
    Task<PartnerAccessTokenResult> AuthenticateAsync(
        PartnerAccessTokenRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The verified partner principal behind a presented access token, resolved
/// from the database on every request rather than trusted from token claims.
/// </summary>
public sealed record PartnerPrincipal(
    Guid PartnerClientId,
    Guid PartnerId,
    Guid RootOrganizationId,
    IReadOnlyCollection<string> Scopes);

/// <summary>
/// Resolves a partner access token's client id back to a live principal.
///
/// This runs per request and deliberately re-reads the database rather than
/// trusting the organization in the token. It is what makes the kill switch
/// immediate: disabling a key or a partner stops the very next request, instead
/// of leaving already-minted tokens usable until they expire. It also keeps the
/// funding tenant authoritative server state, per the tenant-isolation rule that
/// scope never comes from the caller.
///
/// Returns null when the client or its partner is unknown or disabled.
/// </summary>
public interface IPartnerPrincipalResolver
{
    Task<PartnerPrincipal?> ResolveAsync(Guid partnerClientId, CancellationToken cancellationToken);
}

/// <summary>
/// Claim names on a partner access token. The token carries identity only: the
/// funding organization is resolved server-side by
/// <see cref="IPartnerPrincipalResolver"/>, never read from here.
/// </summary>
public static class PartnerTokenClaims
{
    public const string Principal = "partner_principal";
    public const string PrincipalValue = "partner";
    public const string ClientId = "partner_client_id";
}

public sealed class PartnersOptions
{
    public const string SectionName = "Partners";

    /// <summary>
    /// Lifetime of the access token minted by the credential exchange. Kept
    /// short deliberately: revoking a key takes effect within one token
    /// lifetime, which is what makes the kill switch meaningful without
    /// maintaining a token blocklist.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 5;

    /// <summary>
    /// Failed credential exchanges tolerated per API client inside
    /// <see cref="CredentialFailureWindowSeconds"/> before that client is
    /// refused for the rest of the window, regardless of whether the secret is
    /// eventually correct.
    ///
    /// Keyed on the resolved client rather than on anything the caller sends, so
    /// it isolates one reseller's brute-force noise from every other reseller,
    /// which an IP-partitioned limiter cannot do when partners share an egress
    /// address.
    /// </summary>
    public int CredentialFailureLimit { get; set; } = 5;

    /// <summary>
    /// Length of the failure window. Short on purpose: anyone who learns a
    /// client code can spend its budget, so recovery must be automatic rather
    /// than needing an operator.
    /// </summary>
    public int CredentialFailureWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Base URL the buyer's claim link is built on, pointing at the cardholder
    /// app. Separate from the sharing claim URLs because the e-pin flow is its
    /// own page group with its own activation purpose.
    /// </summary>
    public string ClaimBaseUrl { get; set; } = "http://localhost:5180/epin";

    /// <summary>Lifetime of an unclaimed e-pin product.</summary>
    public int OrphanClaimLifetimeDays { get; set; } = 365;

    /// <summary>
    /// Maximum face value of a single e-pin, expressed in the requested
    /// currency's major unit. Partner minting is one card per order, so this is
    /// also the per-order amount cap. The prepaid float remains the aggregate
    /// financial ceiling.
    /// </summary>
    public decimal MaximumEpinAmount { get; set; } = 1_000m;

    /// <summary>
    /// Base64-encoded 256-bit key used to deterministically reconstitute claim
    /// material for an idempotent mint retry without storing raw PINs or tokens.
    /// Keep this in a secret manager, separate from JWT signing material.
    /// </summary>
    public string EpinDeliveryKey { get; set; } = string.Empty;
}
