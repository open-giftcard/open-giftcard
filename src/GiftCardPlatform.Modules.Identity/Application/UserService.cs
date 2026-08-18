using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class UserService(
    IdentityDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IUserService
{
    private const string UniqueViolation = "23505";

    public async Task<UserResult> CreateAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission(PlatformPermissions.UsersCreate);

        var (email, normalizedEmail) = CredentialPolicy.NormalizeEmail(request.Email);
        var password = CredentialPolicy.ValidatePassword(request.Password);
        var now = timeProvider.GetUtcNow();
        var user = User.Create(email, normalizedEmail, now);
        user.SetPasswordHash(passwordHasher.HashPassword(user, password));

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            throw new ConflictException("user.email.duplicate", "A user with this email already exists.");
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                OrganizationScopeId: null,
                AuditOperations.UserCreated,
                nameof(User),
                user.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string> { ["email"] = user.Email! }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(user);
    }

    public async Task<UserResult> DisableAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequirePlatformPermission(PlatformPermissions.UsersDisable);

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var user = await dbContext.Users
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("user.not_found", "User not found.");

        var now = timeProvider.GetUtcNow();
        user.Disable(now);

        var activeSessions = await dbContext.Sessions
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var session in activeSessions)
        {
            session.Revoke(now, "user_disabled");
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.UserId!.Value,
                AuditActorType.PlatformOperator,
                OrganizationScopeId: null,
                AuditOperations.UserDisabled,
                nameof(User),
                user.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["revoked_sessions"] = activeSessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(user);
    }

    private void RequirePlatformPermission(string permission)
    {
        if (!executionContext.IsAuthenticated ||
            executionContext.UserId is null ||
            !executionContext.HasPlatformPermission(permission))
        {
            throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
        }
    }

    private static UserResult ToResult(User user) =>
        new(
            user.Id,
            user.Email,
            user.PhoneNumber,
            user.Status.ToString(),
            user.CreatedAtUtc,
            user.DisabledAtUtc);
}
