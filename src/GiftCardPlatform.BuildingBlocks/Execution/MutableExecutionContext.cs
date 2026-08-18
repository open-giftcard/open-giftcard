namespace GiftCardPlatform.BuildingBlocks.Execution;

/// <summary>
/// Scoped, settable implementation of <see cref="IExecutionContext"/>.
/// Registered as a scoped service rather than an <c>AsyncLocal</c> (ADR-020):
/// one instance per request, background job, or test scope.
/// </summary>
public sealed class MutableExecutionContext : IExecutionContext
{
    private HashSet<string> _platformPermissions = new(StringComparer.Ordinal);
    private HashSet<string> _partnerScopes = new(StringComparer.Ordinal);

    public bool IsAuthenticated { get; private set; }

    public Guid? UserId { get; private set; }

    public bool IsPlatformOperator { get; private set; }

    public bool IsSystem { get; private set; }

    public IReadOnlyCollection<string> PlatformPermissions => _platformPermissions;

    public Guid? ActiveMembershipId { get; private set; }

    public Guid? ActiveOrganizationId { get; private set; }

    public Guid? TenantRootOrganizationId { get; private set; }

    public Guid? ClaimInvitationId { get; private set; }

    public Guid? ShareId { get; private set; }

    public Guid? PaymentTokenId { get; private set; }

    public string? PaymentCodeHash { get; private set; }

    public bool IsPosClient { get; private set; }

    public Guid? PosClientId { get; private set; }

    public Guid? PosTerminalId { get; private set; }

    public bool IsPartnerClient { get; private set; }

    public Guid? PartnerClientId { get; private set; }

    public Guid? PartnerId { get; private set; }

    public IReadOnlyCollection<string> PartnerScopes => _partnerScopes;

    public Guid CorrelationId { get; private set; } = Guid.CreateVersion7();

    public bool HasPlatformPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return IsPlatformOperator && _platformPermissions.Contains(permission);
    }

    public bool HasPartnerScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return IsPartnerClient && _partnerScopes.Contains(scope);
    }

    public void SetCorrelationId(Guid correlationId) => CorrelationId = correlationId;

    /// <summary>
    /// Populates a signed-in identity without assigning platform authority or
    /// an organization scope. A verified organization may be selected
    /// separately after membership resolution.
    /// </summary>
    public void SetIdentityUser(Guid userId)
    {
        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Populates the context for an authenticated platform operator.</summary>
    public void SetPlatformOperator(Guid userId, IEnumerable<string> platformPermissions)
    {
        ArgumentNullException.ThrowIfNull(platformPermissions);

        IsAuthenticated = true;
        IsPlatformOperator = true;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(platformPermissions, StringComparer.Ordinal);
    }

    /// <summary>
    /// Populates a trusted background-job actor. This method is never called
    /// from request data; the host supplies a stable non-human actor ID and the
    /// exact permissions required by the job.
    /// </summary>
    public void SetSystem(Guid actorId, IEnumerable<string> platformPermissions)
    {
        ArgumentNullException.ThrowIfNull(platformPermissions);
        if (actorId == Guid.Empty)
        {
            throw new ArgumentException(
                "A stable system actor identifier is required.",
                nameof(actorId));
        }

        IsAuthenticated = true;
        IsPlatformOperator = true;
        IsSystem = true;
        UserId = actorId;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(platformPermissions, StringComparer.Ordinal);
    }

    /// <summary>
    /// Establishes only the requested tenant context needed to look up a
    /// membership behind RLS. The caller remains unauthenticated until
    /// <see cref="SetOrganizationMember"/> receives the verified active membership.
    /// </summary>
    public void SetOrganizationCandidate(Guid userId, Guid organizationId)
    {
        IsAuthenticated = false;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = null;
        ActiveOrganizationId = organizationId;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Populates a verified organization-scoped caller. Authentication has
    /// already proved that the membership is active, belongs to the user, and is
    /// owned by the active organization.
    /// </summary>
    public void SetOrganizationMember(
        Guid userId,
        Guid membershipId,
        Guid organizationId,
        Guid? tenantRootOrganizationId = null)
    {
        if (membershipId == Guid.Empty)
        {
            throw new ArgumentException("A verified membership is required.", nameof(membershipId));
        }

        var tenantRoot = tenantRootOrganizationId ?? organizationId;
        if (tenantRoot == Guid.Empty)
        {
            throw new ArgumentException(
                "A verified tenant root organization is required.",
                nameof(tenantRootOrganizationId));
        }

        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = membershipId;
        ActiveOrganizationId = organizationId;
        TenantRootOrganizationId = tenantRoot;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Establishes the narrow anonymous scope used to verify one claim
    /// invitation. The identifier is parsed from the token envelope, but the
    /// invitation secret still has to pass a constant-time hash comparison
    /// before application code may mutate anything.
    /// </summary>
    public void SetClaimCandidate(Guid invitationId)
    {
        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A claim invitation identifier is required.",
                nameof(invitationId));
        }

        IsAuthenticated = false;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = null;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = invitationId;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Promotes a successfully verified claim candidate to its resolved global
    /// identity while retaining the invitation scope until the atomic claim
    /// transaction completes.
    /// </summary>
    public void SetClaimIdentity(Guid userId, Guid invitationId)
    {
        if (userId == Guid.Empty || invitationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Claim identity and invitation identifiers are required.");
        }

        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = invitationId;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Retains the authenticated recipient while establishing the narrow RLS
    /// candidate parsed from a protected share token. It never grants tenant,
    /// membership, platform, or system authority.
    /// </summary>
    public void SetShareCandidate(Guid shareId)
    {
        if (!IsAuthenticated || IsPlatformOperator || UserId is null || shareId == Guid.Empty)
        {
            throw new ArgumentException(
                "An authenticated identity and share identifier are required.",
                nameof(shareId));
        }

        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = shareId;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Establishes the narrow anonymous scope used to verify one contact-bound
    /// share invitation. The token secret must pass constant-time verification
    /// before the scope is promoted to a recipient identity.
    /// </summary>
    public void SetAnonymousShareCandidate(Guid shareId)
    {
        if (shareId == Guid.Empty)
        {
            throw new ArgumentException("A share identifier is required.", nameof(shareId));
        }

        IsAuthenticated = false;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = null;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = shareId;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Promotes a verified direct-share candidate to its resolved recipient
    /// identity while retaining the exact share scope for the atomic transfer.
    /// </summary>
    public void SetShareIdentity(Guid userId, Guid shareId)
    {
        if (userId == Guid.Empty || shareId == Guid.Empty)
        {
            throw new ArgumentException("Share identity and share identifiers are required.");
        }

        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = userId;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = shareId;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Scopes a trusted background job to one share without losing system attribution.</summary>
    public void SetSystemShareCandidate(Guid shareId)
    {
        if (!IsSystem || !IsPlatformOperator || UserId is null || shareId == Guid.Empty)
        {
            throw new ArgumentException(
                "A trusted system context and share identifier are required.",
                nameof(shareId));
        }

        ShareId = shareId;
        ClaimInvitationId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
    }

    /// <summary>
    /// Populates an authenticated e-pin reseller caller (ADR-053).
    ///
    /// This deliberately differs from <see cref="SetPosClient"/>. A POS client is
    /// a device that owns no money, so it is given no tenant scope and RLS fails
    /// closed for it. A partner mints against its own organization's prepaid
    /// corporate credit, so it must carry that scope: setting
    /// <see cref="ActiveOrganizationId"/> is what makes every existing tenant RLS
    /// policy apply to a partner unchanged, with no policy retrofit anywhere.
    ///
    /// It still receives no user, membership, or platform authority. Authority to
    /// mint comes from scopes on the API client row, evaluated in the application
    /// service, never from possession of this context.
    /// </summary>
    public void SetPartnerClient(
        Guid partnerClientId,
        Guid partnerId,
        Guid organizationId,
        Guid tenantRootOrganizationId,
        IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        if (partnerClientId == Guid.Empty || partnerId == Guid.Empty ||
            organizationId == Guid.Empty || tenantRootOrganizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Verified partner client, partner, organization, and tenant root identifiers are required.");
        }

        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = null;
        ActiveMembershipId = null;
        ActiveOrganizationId = organizationId;
        TenantRootOrganizationId = tenantRootOrganizationId;
        ClaimInvitationId = null;
        ShareId = null;
        IsPartnerClient = true;
        PartnerClientId = partnerClientId;
        PartnerId = partnerId;
        _partnerScopes = new HashSet<string>(scopes, StringComparer.Ordinal);
        ClearPosPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Populates an authenticated point-of-sale caller (ADR-043). A POS client
    /// is a device identity, not a person: it receives no user, membership,
    /// organization, tenant, or platform authority, so tenant-scoped RLS fails
    /// closed for it. Presenting a QR credential is what selects the spending
    /// context, and that is verified separately (ADR-017).
    /// </summary>
    public void SetPosClient(Guid posClientId, Guid posTerminalId)
    {
        if (posClientId == Guid.Empty || posTerminalId == Guid.Empty)
        {
            throw new ArgumentException(
                "Verified POS client and terminal identifiers are required.");
        }

        IsAuthenticated = true;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = null;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        IsPosClient = true;
        PosClientId = posClientId;
        PosTerminalId = posTerminalId;
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Retains the authenticated POS device while establishing the narrow
    /// candidate parsed from a presented payment credential. It grants no user,
    /// organization, or tenant authority: the credential's 256-bit secret must
    /// still pass constant-time verification before value is reserved, so
    /// possession of the identifier alone is worth nothing (ADR-017).
    /// </summary>
    public void SetPaymentTokenCandidate(Guid paymentTokenId)
    {
        if (!IsPosClient || PosClientId is null || PosTerminalId is null ||
            paymentTokenId == Guid.Empty)
        {
            throw new ArgumentException(
                "An authenticated POS device and payment token identifier are required.",
                nameof(paymentTokenId));
        }

        PaymentTokenId = paymentTokenId;
        PaymentCodeHash = null;
    }

    /// <summary>
    /// Retains the authenticated POS device while granting exact hash-scoped
    /// visibility to resolve a human-entered numeric code. The raw code is
    /// never placed in execution or database session context (ADR-050).
    /// </summary>
    public void SetPaymentCodeCandidate(string paymentCodeHash)
    {
        if (!IsPosClient || PosClientId is null || PosTerminalId is null ||
            paymentCodeHash is null || paymentCodeHash.Length != 64)
        {
            throw new ArgumentException(
                "An authenticated POS device and payment code hash are required.",
                nameof(paymentCodeHash));
        }

        try
        {
            _ = Convert.FromHexString(paymentCodeHash);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The payment code hash must be hexadecimal.",
                nameof(paymentCodeHash),
                exception);
        }

        PaymentTokenId = null;
        PaymentCodeHash = paymentCodeHash;
    }

    private void ClearPaymentCandidates()
    {
        PaymentTokenId = null;
        PaymentCodeHash = null;
    }

    private void ClearPosPrincipal()
    {
        IsPosClient = false;
        PosClientId = null;
        PosTerminalId = null;
    }

    private void ClearPartnerPrincipal()
    {
        IsPartnerClient = false;
        PartnerClientId = null;
        PartnerId = null;
        _partnerScopes = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Resets the context to an unauthenticated caller.</summary>
    public void SetAnonymous()
    {
        IsAuthenticated = false;
        IsPlatformOperator = false;
        IsSystem = false;
        UserId = null;
        ActiveMembershipId = null;
        ActiveOrganizationId = null;
        TenantRootOrganizationId = null;
        ClaimInvitationId = null;
        ShareId = null;
        ClearPosPrincipal();
        ClearPartnerPrincipal();
        ClearPaymentCandidates();
        _platformPermissions = new HashSet<string>(StringComparer.Ordinal);
    }
}
