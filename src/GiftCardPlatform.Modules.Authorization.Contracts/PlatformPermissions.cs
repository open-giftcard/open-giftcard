namespace GiftCardPlatform.Modules.Authorization.Contracts;

/// <summary>
/// Named platform permissions (ADR-006). Platform permissions are distinct
/// from customer-organization permissions and are never granted through an
/// organization membership.
///
/// Database-backed platform roles are owned by the Authorization module; JWT
/// authentication resolves these permissions from persisted assignments.
/// </summary>
public static class PlatformPermissions
{
    public const string OrganizationsCreate = "platform.organizations.create";
    public const string OrganizationsView = "platform.organizations.view";
    public const string UsersCreate = "platform.users.create";
    public const string UsersDisable = "platform.users.disable";
    public const string InitialAdministratorsAssign =
        "platform.organizations.initial_administrators.assign";
    public const string CorporateCreditsAllocate = "platform.corporate_credits.allocate";
    public const string CorporateCreditsView = "platform.corporate_credits.view";
    public const string CorporateCreditsReverse = "platform.corporate_credits.reverse";
    public const string GiftCardsView = "platform.gift_cards.view";
    public const string GiftCardsManageLifecycle =
        "platform.gift_cards.lifecycle.manage";
    public const string AuditView = "platform.audit.view";

    /// <summary>
    /// Registers POS clients and terminals. The platform operator owns the stores, so this is
    /// platform authority rather than a customer-organization permission
    /// (ADR-043).
    /// </summary>
    public const string PosClientsManage = "platform.pos.clients.manage";

    /// <summary>
    /// Reads cross-tenant POS payment, refund, store, terminal, and receipt
    /// reporting. Deliberately separate from device-management authority.
    /// </summary>
    public const string PaymentsView = "platform.payments.view";

    /// <summary>
    /// Allows a platform operator the controlled cross-tenant read of an
    /// organization's memberships through the RLS platform path (read-only).
    /// </summary>
    public const string MembershipsView = "platform.organizations.memberships.view";

    /// <summary>
    /// Registers e-pin reseller partners and their API clients, and disables
    /// either as the kill switch (ADR-053). Platform authority because it decides
    /// who may mint against a prepaid float, which is a commercial relationship
    /// the platform operator owns rather than something a customer organization self-administers.
    /// </summary>
    public const string PartnersManage = "platform.partners.manage";

    public static IReadOnlyCollection<string> All { get; } =
    [
        OrganizationsCreate,
        OrganizationsView,
        UsersCreate,
        UsersDisable,
        InitialAdministratorsAssign,
        CorporateCreditsAllocate,
        CorporateCreditsView,
        CorporateCreditsReverse,
        GiftCardsView,
        GiftCardsManageLifecycle,
        AuditView,
        MembershipsView,
        PosClientsManage,
        PaymentsView,
        PartnersManage,
    ];

    public static bool IsKnown(string permission) => All.Contains(permission, StringComparer.Ordinal);
}
