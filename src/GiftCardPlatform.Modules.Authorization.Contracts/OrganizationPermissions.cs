namespace GiftCardPlatform.Modules.Authorization.Contracts;

/// <summary>
/// Named permissions held by an organization-scoped (customer) caller and
/// evaluated against the active organization membership. Distinct from
/// <see cref="PlatformPermissions"/>, which belong to platform operators
/// (ADR-006, ADR-021).
///
/// These definitions seed the global permission catalogue. Effective grants are
/// resolved from organization roles and scoped membership assignments.
/// </summary>
public static class OrganizationPermissions
{
    public const string MembershipsCreate = "organization.memberships.create";
    public const string MembershipsView = "organization.memberships.view";
    public const string MembershipsDisable = "organization.memberships.disable";
    public const string CorporateCreditsView = "organization.corporate_credits.view";
    public const string GiftCardsIssue = "organization.gift_cards.issue";
    public const string GiftCardsView = "organization.gift_cards.view";

    public const string GiftCardsDistribute = "organization.gift_cards.distribute";
    public const string GiftCardsManageLifecycle =
        "organization.gift_cards.lifecycle.manage";
    public const string AuditView = "organization.audit.view";

    /// <summary>Reads the caller's own organization and its subsidiaries.</summary>
    public const string View = "organization.view";

    /// <summary>Creates a subsidiary beneath the caller's own organization.</summary>
    public const string CreateSubsidiary = "organization.create_subsidiary";

    // Role management (ADR-006). Names taken from PROJECT_DEFINITION §12.
    public const string RoleView = "role.view";
    public const string RoleCreate = "role.create";
    public const string RoleAssign = "role.assign";
    public const string RoleManagePermissions = "role.manage_permissions";

    public static IReadOnlyCollection<string> All { get; } =
    [
        MembershipsCreate,
        MembershipsView,
        MembershipsDisable,
        CorporateCreditsView,
        GiftCardsIssue,
        GiftCardsView,
        GiftCardsDistribute,
        GiftCardsManageLifecycle,
        AuditView,
        View,
        CreateSubsidiary,
        RoleView,
        RoleCreate,
        RoleAssign,
        RoleManagePermissions,
    ];

    public static bool IsKnown(string permission) => All.Contains(permission, StringComparer.Ordinal);
}
