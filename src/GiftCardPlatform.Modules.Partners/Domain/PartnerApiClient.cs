using System.Text.RegularExpressions;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.Modules.Partners.Contracts;

namespace GiftCardPlatform.Modules.Partners.Domain;

/// <summary>
/// One machine credential belonging to a <see cref="Partner"/>. A reseller
/// normally holds several: one per environment, plus overlapping pairs during a
/// rotation, so a key can be retired without an integration outage.
///
/// Only the SHA-256 digest of the secret is stored, so the platform cannot
/// reproduce a lost secret and a database disclosure does not yield a working
/// minting credential.
/// </summary>
internal sealed partial class PartnerApiClient
{
    public const int CodeMaxLength = 40;
    public const int DisplayNameMaxLength = 120;

    public Guid Id { get; private init; }

    public Guid PartnerId { get; private init; }

    /// <summary>
    /// Denormalized from the owning partner so the RLS policy can isolate on
    /// this table without a join, and so the credential exchange resolves the
    /// funding tenant without a second query.
    /// </summary>
    public Guid RootOrganizationId { get; private init; }

    /// <summary>Globally unique, so the credential exchange needs only the code.</summary>
    public string Code { get; private init; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>SHA-256 hex of a 256-bit secret. The secret itself is never stored.</summary>
    public string SecretHash { get; private set; } = string.Empty;

    /// <summary>
    /// What this credential may do. Stored on the row rather than derived from
    /// the partner, so one reseller can hold a minting key and a read-only key
    /// whose compromise costs different amounts.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; private set; } = [];

    public PartnerApiClientStatus Status { get; private set; }

    public DateTimeOffset RegisteredAtUtc { get; private init; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public bool IsUsable => Status == PartnerApiClientStatus.Active;

    private PartnerApiClient()
    {
    }

    public static PartnerApiClient Register(
        Guid id,
        Guid partnerId,
        Guid rootOrganizationId,
        string? code,
        string? displayName,
        IReadOnlyCollection<string>? scopes,
        string secretHash,
        DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ValidationFailedException(
                "partner.api_client.id.required",
                "A partner API client identifier is required.");
        }

        if (partnerId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "partner.api_client.partner.required",
                "An owning partner is required.");
        }

        if (rootOrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "partner.api_client.root_organization.required",
                "A funding root organization is required.");
        }

        if (secretHash is null || secretHash.Length != PartnerCredentialCodec.HashHexLength)
        {
            throw new ValidationFailedException(
                "partner.api_client.secret.invalid",
                "A partner API client secret hash is required.");
        }

        return new PartnerApiClient
        {
            Id = id,
            PartnerId = partnerId,
            RootOrganizationId = rootOrganizationId,
            Code = NormalizeCode(code),
            DisplayName = NormalizeDisplayName(displayName),
            Scopes = NormalizeScopes(scopes),
            SecretHash = secretHash,
            Status = PartnerApiClientStatus.Active,
            RegisteredAtUtc = now,
        };
    }

    /// <summary>
    /// Kill switch for a single credential, which is the usual response to a
    /// suspected leak: the reseller keeps trading on its other keys while the
    /// compromised one dies within one access-token lifetime.
    /// </summary>
    public void Disable(DateTimeOffset now)
    {
        if (Status == PartnerApiClientStatus.Disabled)
        {
            return;
        }

        Status = PartnerApiClientStatus.Disabled;
        DisabledAtUtc = now;
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
                "partner.api_client.code.invalid",
                "A partner API client code must be 1-40 characters of A-Z, 0-9, or hyphen.");
        }

        return normalized;
    }

    /// <summary>
    /// A credential with no scope could authenticate but do nothing, which reads
    /// as a broken integration rather than a deliberate one, so it is refused.
    /// Unknown names are refused too: silently dropping one would leave an
    /// operator believing they granted authority that was never stored.
    /// </summary>
    private static List<string> NormalizeScopes(IReadOnlyCollection<string>? scopes)
    {
        var normalized = (scopes ?? [])
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0 || normalized.Any(scope => !PartnerScopes.IsKnown(scope)))
        {
            throw new ValidationFailedException(
                "partner.api_client.scopes.invalid",
                "At least one known partner scope is required.");
        }

        return normalized;
    }

    private static string NormalizeDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > DisplayNameMaxLength)
        {
            throw new ValidationFailedException(
                "partner.api_client.display_name.invalid",
                $"A partner API client display name of at most {DisplayNameMaxLength} characters is required.");
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Z0-9-]+$")]
    private static partial Regex CodePattern();
}
