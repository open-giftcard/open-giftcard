using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Distribution.Application;

/// <summary>
/// Development-only lookup of the activation link.
///
/// It reads the queued outbox message rather than an in-process copy. That
/// matters for more than tidiness: an in-memory capture is not rolled back with
/// the transaction, so a bulk batch that failed part-way used to leave a live
/// link visible for a card that was never actually distributed. Reading the same
/// durable row the dispatcher will send makes what Development shows and what a
/// recipient receives the same thing by construction.
///
/// The notification identifier is the invitation identifier, so no join is
/// needed.
/// </summary>
internal sealed class DevelopmentClaimDeliveryQuery(
    DistributionDbContext dbContext,
    IDevelopmentNotificationQuery notifications,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator) : IDevelopmentClaimDeliveryQuery
{
    public async Task<DevelopmentClaimDeliveryResult?> FindAsync(
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty || invitationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "distribution.scope.required",
                "Organization and invitation identifiers are required.");
        }

        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.GiftCardsDistribute,
                cancellationToken)
            .ConfigureAwait(false);

        InvitationSnapshot? invitation;
        await using (var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            invitation = await dbContext.Invitations
                .AsNoTracking()
                .Where(item =>
                    item.Id == invitationId &&
                    item.IssuingOrganizationId == organizationId)
                .Select(item => new InvitationSnapshot(
                    item.ContactType!.Value,
                    item.MaskedRecipientContact!,
                    item.ClaimExpiresAtUtc))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (invitation is null)
        {
            return null;
        }

        var queued = await notifications
            .FindAsync(invitationId, cancellationToken)
            .ConfigureAwait(false);
        if (queued is null)
        {
            // Already delivered or dead-lettered: the body is destroyed at that
            // point, so there is deliberately nothing left to show.
            return null;
        }

        var claimUrl = ExtractUrl(queued.Body);
        return claimUrl is null
            ? null
            : new DevelopmentClaimDeliveryResult(
                invitationId,
                invitation.ContactType,
                invitation.MaskedRecipientContact,
                claimUrl,
                invitation.ClaimExpiresAtUtc,
                queued.CapturedAtUtc);
    }

    private static string? ExtractUrl(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record InvitationSnapshot(
        RecipientContactType ContactType,
        string MaskedRecipientContact,
        DateTimeOffset ClaimExpiresAtUtc);
}
