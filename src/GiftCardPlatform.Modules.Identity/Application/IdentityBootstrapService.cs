using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class IdentityBootstrapService(
    IdentityDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IIdentityBootstrapService
{
    private const string UniqueViolation = "23505";

    public async Task<UserResult> CreateInitialPlatformUserAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (email, normalizedEmail) = CredentialPolicy.NormalizeEmail(request.Email);
        var password = CredentialPolicy.ValidatePassword(request.Password);
        var now = timeProvider.GetUtcNow();
        var user = User.Create(email, normalizedEmail, now);
        user.SetPasswordHash(passwordHasher.HashPassword(user, password));

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new ConflictException(
                "bootstrap.invalid",
                "Platform bootstrap could not be completed.");
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                user.Id,
                AuditActorType.System,
                OrganizationScopeId: null,
                AuditOperations.UserCreated,
                nameof(User),
                user.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["email"] = user.Email! }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new UserResult(
            user.Id,
            user.Email,
            user.PhoneNumber,
            user.Status.ToString(),
            user.CreatedAtUtc,
            user.DisabledAtUtc);
    }
}
