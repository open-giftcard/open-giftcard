using GiftCardPlatform.BuildingBlocks.Errors;

namespace GiftCardPlatform.Modules.Identity.Domain;

internal enum UserStatus
{
    Active = 1,
    Disabled = 2,
}

internal sealed class User
{
    private User()
    {
    }

    private User(
        Guid id,
        string? email,
        string? normalizedEmail,
        string? phoneNumber,
        string? normalizedPhoneNumber,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Email = email;
        NormalizedEmail = normalizedEmail;
        PhoneNumber = phoneNumber;
        NormalizedPhoneNumber = normalizedPhoneNumber;
        PasswordHash = string.Empty;
        Status = UserStatus.Active;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string? Email { get; private set; }

    public string? NormalizedEmail { get; private set; }

    public string? PhoneNumber { get; private set; }

    public string? NormalizedPhoneNumber { get; private set; }

    public string PasswordHash { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DisabledAtUtc { get; private set; }

    public bool IsActive => Status == UserStatus.Active;

    public static User Create(
        string email,
        string normalizedEmail,
        DateTimeOffset createdAtUtc) =>
        new(
            Guid.CreateVersion7(),
            email,
            normalizedEmail,
            phoneNumber: null,
            normalizedPhoneNumber: null,
            createdAtUtc: createdAtUtc.ToUniversalTime());

    public static User CreateWithPhone(
        string phoneNumber,
        string normalizedPhoneNumber,
        DateTimeOffset createdAtUtc) =>
        new(
            Guid.CreateVersion7(),
            email: null,
            normalizedEmail: null,
            phoneNumber: phoneNumber,
            normalizedPhoneNumber: normalizedPhoneNumber,
            createdAtUtc: createdAtUtc.ToUniversalTime());

    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
    }

    public void Disable(DateTimeOffset disabledAtUtc)
    {
        if (Status == UserStatus.Disabled)
        {
            throw new ConflictException("user.already_disabled", "The user is already disabled.");
        }

        Status = UserStatus.Disabled;
        DisabledAtUtc = disabledAtUtc.ToUniversalTime();
    }
}
