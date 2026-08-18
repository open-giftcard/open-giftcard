using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Identity.Domain;
using GiftCardPlatform.Modules.Identity.Infrastructure;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Identity.Application;

internal sealed class UserSessionTokenIssuer(
    IdentityDbContext dbContext,
    ITokenGenerator tokenGenerator,
    IOptions<IdentityTokenOptions> tokenOptions,
    ITransactionCoordinator transactionCoordinator,
    TimeProvider timeProvider)
{
    private readonly IdentityTokenOptions options = tokenOptions.Value;

    public async Task<TokenPairResult> IssueAsync(
        User user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = timeProvider.GetUtcNow();
        var sessionExpiresAtUtc = now.AddDays(options.RefreshTokenDays);
        var session = UserSession.Create(user.Id, now, sessionExpiresAtUtc);
        var generatedRefresh = tokenGenerator.CreateRefreshToken();
        var refreshToken = RefreshToken.Create(
            session.Id,
            session.TokenFamilyId,
            generatedRefresh.Hash,
            now,
            sessionExpiresAtUtc);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        dbContext.Sessions.Add(session);
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var access = tokenGenerator.CreateAccessToken(user, session, now);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new TokenPairResult(
            access.Token,
            access.ExpiresAtUtc,
            generatedRefresh.Plaintext,
            refreshToken.ExpiresAtUtc);
    }
}
