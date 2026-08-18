using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed class DistributionLifecycleWriter(
    DistributionDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IDistributionLifecycleWriter
{
    private const string SerializationFailure = "40001";

    public async Task CloseForCardLifecycleAsync(
        CloseDistributionForLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InvitationId == Guid.Empty || request.GiftCardId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.lifecycle_closure.scope.required",
                "Invitation and gift-card identifiers are required.");
        }

        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException(
                "distribution.lifecycle_closure.actor.required",
                "An authenticated lifecycle actor is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var lockKey = $"distribution-invitation|{request.InvitationId:D}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken).ConfigureAwait(false);

        var invitation = await dbContext.Invitations
            .SingleOrDefaultAsync(
                item =>
                    item.Id == request.InvitationId &&
                    item.GiftCardId == request.GiftCardId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "distribution.lifecycle_closure.invitation.invalid",
                "The card's distribution invitation is unavailable.");

        if (invitation.CloseForCardLifecycle(request.Closure))
        {
            dbContext.Events.Add(
                DistributionEvent.CardLifecycleClosed(
                    invitation,
                    request.Closure,
                    executionContext.UserId.Value,
                    executionContext.ActiveMembershipId,
                    timeProvider.GetUtcNow()));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (FindSqlState(exception) == SerializationFailure)
        {
            throw new ConflictException(
                "distribution.concurrent_conflict",
                "The distribution changed concurrently. Retry the lifecycle command safely.");
        }
    }

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
