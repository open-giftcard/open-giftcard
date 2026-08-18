namespace GiftCardPlatform.Modules.Identity.Contracts;

public sealed record CreateUserRequest(string? Email, string? Password);

public sealed record UserResult(
    Guid Id,
    string? Email,
    string? PhoneNumber,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DisabledAtUtc);

public sealed record LoginRequest(string? Identifier, string? Password);

public enum IdentityContactType
{
    Email = 1,
    Phone = 2,
}

public sealed record ResolveRecipientIdentityRequest(
    IdentityContactType ContactType,
    string? Contact,
    string? Password);

public sealed record RecipientContactResult(
    IdentityContactType ContactType,
    string Contact,
    string MaskedContact);

public sealed record RecipientIdentityResult(
    UserResult User,
    bool WasCreated);

public sealed record RefreshSessionRequest(string? RefreshToken);

public sealed record RevokeSessionRequest(string? RefreshToken);

public sealed record TokenPairResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public interface IUserService
{
    Task<UserResult> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken);

    Task<UserResult> DisableAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IAuthenticationService
{
    Task<TokenPairResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<TokenPairResult> RefreshAsync(
        RefreshSessionRequest request,
        CancellationToken cancellationToken);

    Task RevokeAsync(RevokeSessionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Issues a session only after an already verified gift-card claim created the
/// exact recipient identity and the submitted password is re-verified.
/// Existing identities must continue through normal login.
/// </summary>
public interface IRecipientClaimSessionIssuer
{
    Task<TokenPairResult> IssueAsync(
        Guid userId,
        string? password,
        CancellationToken cancellationToken);
}

public interface IIdentityBootstrapService
{
    Task<UserResult> CreateInitialPlatformUserAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken);
}

public interface IIdentityUserQuery
{
    Task<UserResult?> FindAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record OrganizationStaffIdentityResult(Guid UserId, string Email);

/// <summary>
/// Permission-protected staff-identity composition for organization membership
/// administration. It never returns passwords, sessions, normalized
/// credentials, or recipient phone identifiers.
/// </summary>
public interface IOrganizationStaffDirectory
{
    /// <summary>
    /// Resolves one active email identity after verifying
    /// <c>organization.memberships.create</c> for the target organization.
    /// </summary>
    Task<OrganizationStaffIdentityResult> ResolveForMembershipCreationAsync(
        Guid organizationId,
        string? email,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns nullable staff emails only after the existing organization or
    /// platform membership-view permission succeeds.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string?>> GetVisibleEmailsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow accountless-claim boundary. It associates an existing active
/// identity or creates the minimum global identity for the verified invitation
/// contact. It never creates an organization membership.
/// </summary>
public interface IRecipientIdentityService
{
    Task<RecipientIdentityResult> ResolveAsync(
        ResolveRecipientIdentityRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Identity-owned normalization and masking boundary for verified recipient
/// contacts. Callers may persist the normalized contact only on a protected,
/// contact-bound invitation and must expose <see cref="RecipientContactResult.MaskedContact"/>
/// outside Identity and notification delivery.
/// </summary>
public interface IRecipientContactService
{
    RecipientContactResult NormalizeAndMask(
        IdentityContactType contactType,
        string? contact);
}

/// <summary>Configuration shared by token issuance and bearer validation.</summary>
public sealed class IdentityTokenOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Issuer { get; set; } = "GiftCardPlatform";

    public string Audience { get; set; } = "GiftCardPlatform.Api";

    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}
