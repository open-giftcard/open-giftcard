using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class AuthenticationService(
    IdentityDbContext dbContext,
    IPasswordHasher<User> passwordHasher,
    ITokenGenerator tokenGenerator,
    UserSessionTokenIssuer sessionTokenIssuer,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IAuthenticationService, IRecipientClaimSessionIssuer
{
    private const string InvalidCredentialsCode = "auth.invalid_credentials";
    private const string InvalidCredentialsMessage = "The email, phone number, or password is invalid.";
    private const string InvalidRefreshCode = "auth.invalid_refresh_token";
    private const string InvalidRefreshMessage = "The refresh token is invalid or expired.";

    private readonly User dummyUser = CreateDummyUser();
    private readonly string dummyPasswordHash = CreateDummyPasswordHash(passwordHasher);

    public async Task<TokenPairResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Refuse impossible credential shapes before invoking the deliberately
        // expensive password hasher.
        if (request.Password is null ||
            request.Password.EnumerateRunes().Take(CredentialPolicy.MaximumPasswordLength + 1).Count() >
            CredentialPolicy.MaximumPasswordLength)
        {
            await VerifyDummyPasswordAsync("invalid-password-shape").ConfigureAwait(false);
            throw InvalidCredentials();
        }

        CredentialPolicy.NormalizedLoginIdentifier identifier;
        try
        {
            identifier = CredentialPolicy.NormalizeIdentifier(request.Identifier);
        }
        catch (ValidationFailedException)
        {
            await VerifyDummyPasswordAsync(request.Password).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        var user = await dbContext.Users
            .SingleOrDefaultAsync(
                x =>
                    (identifier.NormalizedEmail != null &&
                     x.NormalizedEmail == identifier.NormalizedEmail) ||
                    (identifier.NormalizedPhoneNumber != null &&
                     x.NormalizedPhoneNumber == identifier.NormalizedPhoneNumber),
                cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            await VerifyDummyPasswordAsync(request.Password).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            request.Password ?? string.Empty);

        if (verification == PasswordVerificationResult.Failed || !user.IsActive)
        {
            throw InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(passwordHasher.HashPassword(user, request.Password!));
        }

        return await sessionTokenIssuer
            .IssueAsync(user, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TokenPairResult> IssueAsync(
        Guid userId,
        string? password,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty ||
            password is null ||
            password.EnumerateRunes()
                .Take(CredentialPolicy.MaximumPasswordLength + 1)
                .Count() > CredentialPolicy.MaximumPasswordLength)
        {
            await VerifyDummyPasswordAsync(password).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        var user = await dbContext.Users
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            await VerifyDummyPasswordAsync(password).ConfigureAwait(false);
            throw InvalidCredentials();
        }

        var verification = passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            password);
        if (verification == PasswordVerificationResult.Failed || !user.IsActive)
        {
            throw InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.SetPasswordHash(passwordHasher.HashPassword(user, password));
        }

        return await sessionTokenIssuer
            .IssueAsync(user, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TokenPairResult> RefreshAsync(
        RefreshSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suppliedToken = RequireRefreshToken(request.RefreshToken);
        var tokenHash = tokenGenerator.HashRefreshToken(suppliedToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var currentToken = await dbContext.RefreshTokens
            .FromSqlInterpolated(
                $"select * from identity.refresh_tokens where token_hash = {tokenHash} for update")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (currentToken is null)
        {
            throw InvalidRefresh();
        }

        var session = await dbContext.Sessions
            .SingleAsync(x => x.Id == currentToken.SessionId, cancellationToken)
            .ConfigureAwait(false);

        if (currentToken.ConsumedAtUtc is not null || currentToken.RevokedAtUtc is not null)
        {
            await RevokeForReuseAsync(session, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw InvalidRefresh();
        }

        var user = await dbContext.Users
            .SingleAsync(x => x.Id == session.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (currentToken.ExpiresAtUtc <= now ||
            session.IsRevoked ||
            session.ExpiresAtUtc <= now ||
            !user.IsActive ||
            currentToken.TokenFamilyId != session.TokenFamilyId)
        {
            throw InvalidRefresh();
        }

        var generatedRefresh = tokenGenerator.CreateRefreshToken();
        var replacement = RefreshToken.Create(
            session.Id,
            session.TokenFamilyId,
            generatedRefresh.Hash,
            now,
            session.ExpiresAtUtc);

        currentToken.Consume(now, replacement.Id);
        dbContext.RefreshTokens.Add(replacement);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var access = tokenGenerator.CreateAccessToken(user, session, now);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TokenPairResult(
            access.Token,
            access.ExpiresAtUtc,
            generatedRefresh.Plaintext,
            replacement.ExpiresAtUtc);
    }

    public async Task RevokeAsync(
        RevokeSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var suppliedToken = RequireRefreshToken(request.RefreshToken);
        var tokenHash = tokenGenerator.HashRefreshToken(suppliedToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var currentToken = await dbContext.RefreshTokens
            .FromSqlInterpolated(
                $"select * from identity.refresh_tokens where token_hash = {tokenHash} for update")
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Revocation is deliberately idempotent and does not disclose whether a
        // caller-supplied token exists.
        if (currentToken is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var session = await dbContext.Sessions
            .SingleAsync(x => x.Id == currentToken.SessionId, cancellationToken)
            .ConfigureAwait(false);

        if (!session.IsRevoked)
        {
            session.Revoke(now, "user_revoked");
            await RevokeActiveTokensAsync(session.Id, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await auditRecorder.RecordAsync(
                new AuditEntry(
                    session.UserId,
                    AuditActorType.IdentityUser,
                    OrganizationScopeId: null,
                    AuditOperations.SessionRevoked,
                    nameof(UserSession),
                    session.Id.ToString(),
                    AuditOutcome.Success,
                    executionContext.CorrelationId),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeForReuseAsync(
        UserSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        session.Revoke(now, "refresh_token_reuse");
        await RevokeActiveTokensAsync(session.Id, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditRecorder.RecordAsync(
            new AuditEntry(
                session.UserId,
                AuditActorType.IdentityUser,
                OrganizationScopeId: null,
                AuditOperations.RefreshTokenReuseDetected,
                nameof(UserSession),
                session.Id.ToString(),
                AuditOutcome.Failure,
                executionContext.CorrelationId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeActiveTokensAsync(
        Guid sessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeTokens = await dbContext.RefreshTokens
            .Where(x => x.SessionId == sessionId && x.RevokedAtUtc == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in activeTokens)
        {
            token.Revoke(now);
        }
    }

    private Task VerifyDummyPasswordAsync(string? suppliedPassword)
    {
        _ = passwordHasher.VerifyHashedPassword(
            dummyUser,
            dummyPasswordHash,
            suppliedPassword ?? string.Empty);
        return Task.CompletedTask;
    }

    private static string RequireRefreshToken(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 512)
        {
            throw InvalidRefresh();
        }

        return refreshToken;
    }

    private static UnauthorizedException InvalidCredentials() =>
        new(InvalidCredentialsCode, InvalidCredentialsMessage);

    private static UnauthorizedException InvalidRefresh() =>
        new(InvalidRefreshCode, InvalidRefreshMessage);

    private static User CreateDummyUser() =>
        User.Create("invalid@example.invalid", "INVALID@EXAMPLE.INVALID", DateTimeOffset.UnixEpoch);

    private static string CreateDummyPasswordHash(IPasswordHasher<User> hasher)
    {
        var user = CreateDummyUser();
        return hasher.HashPassword(user, "not-a-real-password-value");
    }
}
