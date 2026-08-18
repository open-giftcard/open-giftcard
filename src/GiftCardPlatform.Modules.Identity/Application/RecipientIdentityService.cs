using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class RecipientIdentityService(
    IdentityDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    ITransactionCoordinator transactionCoordinator,
    TimeProvider timeProvider) : IRecipientIdentityService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<RecipientIdentityResult> ResolveAsync(
        ResolveRecipientIdentityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var contact = Normalize(request.ContactType, request.Contact);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Users
            .SingleOrDefaultAsync(
                user =>
                    (contact.NormalizedEmail != null &&
                     user.NormalizedEmail == contact.NormalizedEmail) ||
                    (contact.NormalizedPhoneNumber != null &&
                     user.NormalizedPhoneNumber == contact.NormalizedPhoneNumber),
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.IsActive)
            {
                throw new ConflictException(
                    "recipient_identity.disabled",
                    "The recipient identity is disabled.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecipientIdentityResult(ToResult(existing), WasCreated: false);
        }

        var password = CredentialPolicy.ValidatePassword(request.Password);
        var now = timeProvider.GetUtcNow();
        var user = contact.NormalizedEmail is not null
            ? User.Create(contact.Email!, contact.NormalizedEmail, now)
            : User.CreateWithPhone(
                contact.PhoneNumber!,
                contact.NormalizedPhoneNumber!,
                now);
        user.SetPasswordHash(passwordHasher.HashPassword(user, password));
        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FindSqlState(exception) is UniqueViolation or SerializationFailure)
        {
            throw new ConflictException(
                "recipient_identity.concurrent_conflict",
                "The recipient identity changed concurrently. Retry the claim safely.");
        }

        return new RecipientIdentityResult(ToResult(user), WasCreated: true);
    }

    private static CredentialPolicy.NormalizedLoginIdentifier Normalize(
        IdentityContactType contactType,
        string? contact) =>
        contactType switch
        {
            IdentityContactType.Email => FromEmail(contact),
            IdentityContactType.Phone => FromPhone(contact),
            _ => throw new ValidationFailedException(
                "recipient_identity.contact_type.invalid",
                "Recipient contact type must be Email or Phone."),
        };

    private static CredentialPolicy.NormalizedLoginIdentifier FromEmail(string? value)
    {
        var (email, normalized) = CredentialPolicy.NormalizeEmail(value);
        return new CredentialPolicy.NormalizedLoginIdentifier(
            email,
            normalized,
            PhoneNumber: null,
            NormalizedPhoneNumber: null);
    }

    private static CredentialPolicy.NormalizedLoginIdentifier FromPhone(string? value)
    {
        var (phone, normalized) = CredentialPolicy.NormalizePhone(value);
        return new CredentialPolicy.NormalizedLoginIdentifier(
            Email: null,
            NormalizedEmail: null,
            PhoneNumber: phone,
            NormalizedPhoneNumber: normalized);
    }

    private static UserResult ToResult(User user) =>
        new(
            user.Id,
            user.Email,
            user.PhoneNumber,
            user.Status.ToString(),
            user.CreatedAtUtc,
            user.DisabledAtUtc);

    private static string? FindSqlState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return null;
    }
}
