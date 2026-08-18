using System.Text.RegularExpressions;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Partners.Contracts;

namespace GiftCardPlatform.Modules.Partners.Domain;

/// <summary>
/// A registered e-pin reseller such as an external marketplace that sells gift
/// cards from its own checkout and never touches the portal.
///
/// Unlike a POS client, a partner is tenant-owned: it is anchored to a root
/// organization whose prepaid corporate credit funds every card it mints. That
/// anchoring is what lets the existing ledger, RLS, reporting, and
/// reconciliation machinery apply to partner minting unchanged, and it is the
/// reason a compromised partner credential can never produce more value than
/// the reseller has already paid for.
/// </summary>
internal sealed partial class Partner
{
    public const int CodeMaxLength = 32;
    public const int DisplayNameMaxLength = 120;

    public Guid Id { get; private init; }

    /// <summary>
    /// The funding tenant root. Also the RLS isolation key, so it is fixed for
    /// the life of the partner: repointing it would silently move every future
    /// mint onto another company's money.
    /// </summary>
    public Guid RootOrganizationId { get; private init; }

    public string Code { get; private init; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public PartnerStatus Status { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private init; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public bool IsUsable => Status == PartnerStatus.Active;

    private Partner()
    {
    }

    public static Partner Register(
        Guid id,
        Guid rootOrganizationId,
        string? code,
        string? displayName,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ValidationFailedException(
                "partner.id.required",
                "A partner identifier is required.");
        }

        if (rootOrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "partner.root_organization.required",
                "A funding root organization is required.");
        }

        return new Partner
        {
            Id = id,
            RootOrganizationId = rootOrganizationId,
            Code = NormalizeCode(code),
            DisplayName = NormalizeDisplayName(displayName),
            Status = PartnerStatus.Active,
            RegisteredAtUtc = now,
        };
    }

    /// <summary>
    /// Kill switch. Stops the partner's clients from exchanging credentials at
    /// all. Deliberately does not touch e-pins already sold: those belong to the
    /// reseller's buyers, and voiding them indiscriminately would punish
    /// customers for their supplier's compromise.
    /// </summary>
    public void Disable(DateTimeOffset now)
    {
        if (Status == PartnerStatus.Disabled)
        {
            return;
        }

        Status = PartnerStatus.Disabled;
        DisabledAtUtc = now;
    }

    public void Reactivate()
    {
        if (Status == PartnerStatus.Active)
        {
            return;
        }

        Status = PartnerStatus.Active;
        DisabledAtUtc = null;
    }

    public void Rename(string? displayName) => DisplayName = NormalizeDisplayName(displayName);

    public static string NormalizeCode(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length == 0 ||
            normalized.Length > CodeMaxLength ||
            !CodePattern().IsMatch(normalized))
        {
            throw new ValidationFailedException(
                "partner.code.invalid",
                "A partner code must be 1-32 characters of A-Z, 0-9, or hyphen.");
        }

        return normalized;
    }

    private static string NormalizeDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > DisplayNameMaxLength)
        {
            throw new ValidationFailedException(
                "partner.display_name.invalid",
                $"A partner display name of at most {DisplayNameMaxLength} characters is required.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9-]+$")]
    private static partial Regex CodePattern();
}
