using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Distribution.Contracts;
using GiftCardPlatform.Modules.Distribution.Domain;
using GiftCardPlatform.Modules.Distribution.Infrastructure;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Identity.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Distribution.Application;

internal sealed class GiftCardClaimService(
    DistributionDbContext dbContext,
    IRecipientIdentityService recipientIdentityService,
    IRecipientClaimSessionIssuer recipientClaimSessionIssuer,
    IIdentityUserQuery identityUserQuery,
    IGiftCardOwnershipWriter giftCardOwnershipWriter,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    ISessionContextWriter sessionContextWriter,
    MutableExecutionContext executionContext,
    IOptions<DistributionOptions> options,
    IOptions<PartnersOptions> partnerOptions,
    TimeProvider timeProvider) : IGiftCardClaimService
{
    private const string SerializationFailure = "40001";
    private readonly DistributionOptions distributionOptions = options.Value;
    private readonly byte[] epinDeliveryKey =
        Convert.FromBase64String(partnerOptions.Value.EpinDeliveryKey);

    public async Task<GiftCardClaimResult> ClaimAsync(
        ClaimGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ClaimTokenCodec.TryParse(
                request.ClaimToken,
                out var invitationId,
                out var suppliedSecret))
        {
            throw InvalidClaim();
        }

        var idempotencyKey =
            DistributionInvitation.NormalizeClaimIdempotencyKey(request.IdempotencyKey);
        var attachingUserId = IsAttachableIdentityCaller(executionContext)
            ? executionContext.UserId
            : null;
        executionContext.SetClaimCandidate(invitationId);

        AppException? delayedFailure = null;
        GiftCardClaimResult? result = null;
        await using (var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false))
        {
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            await AcquireInvitationLockAsync(invitationId, cancellationToken).ConfigureAwait(false);

            var invitation = await dbContext.Invitations
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == invitationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (invitation is null)
            {
                throw InvalidClaim();
            }

            var now = timeProvider.GetUtcNow();
            if (!invitation.VerifySecret(suppliedSecret) ||
                !invitation.VerifyPin(request.Pin, epinDeliveryKey))
            {
                if (invitation.RecordFailedClaimAttempt(
                        distributionOptions.MaximumFailedClaimAttempts,
                        now))
                {
                    dbContext.Events.Add(DistributionEvent.ClaimFailed(invitation, now));
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                delayedFailure = InvalidClaim();
            }
            else if (invitation.State == DistributionInvitationState.Claimed)
            {
                if (!invitation.MatchesCompletedClaim(idempotencyKey) ||
                    invitation.ClaimedByUserId is null ||
                    invitation.ClaimedAtUtc is null ||
                    invitation.IdentityWasCreatedOnClaim is null)
                {
                    throw new ConflictException(
                        "distribution.claim.already_completed",
                        "The invitation was already claimed.");
                }

                var card = await giftCardOwnershipWriter
                    .CompleteClaimAsync(
                        new CompleteGiftCardClaimRequest(
                            invitation.GiftCardId,
                            invitation.Id,
                            invitation.ClaimedByUserId.Value),
                        cancellationToken)
                    .ConfigureAwait(false);
                var session = invitation.IdentityWasCreatedOnClaim.Value
                    ? await recipientClaimSessionIssuer
                        .IssueAsync(
                            invitation.ClaimedByUserId.Value,
                            request.Password,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                var maskedLogin = await ResolveMaskedLoginAsync(
                        invitation,
                        invitation.ClaimedByUserId.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                result = ToResult(invitation, card, session, maskedLogin);
            }
            else
            {
                var stateBeforeClaimabilityCheck = invitation.State;
                try
                {
                    invitation.EnsureClaimableAt(now);
                }
                catch (ConflictException exception)
                {
                    if (invitation.State != stateBeforeClaimabilityCheck)
                    {
                        dbContext.Events.Add(DistributionEvent.ClaimFailed(invitation, now));
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    delayedFailure = exception;
                }

                if (delayedFailure is null)
                {
                    var (identity, maskedLogin) = await ResolveClaimIdentityAsync(
                            invitation,
                            request,
                            attachingUserId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    executionContext.SetClaimIdentity(identity.User.Id, invitation.Id);
                    await sessionContextWriter
                        .WriteAsync(
                            transaction.Transaction.Connection!,
                            transaction.Transaction,
                            executionContext,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var card = await giftCardOwnershipWriter
                        .CompleteClaimAsync(
                            new CompleteGiftCardClaimRequest(
                                invitation.GiftCardId,
                                invitation.Id,
                                identity.User.Id),
                            cancellationToken)
                        .ConfigureAwait(false);

                    invitation.CompleteClaim(
                        identity.User.Id,
                        identity.WasCreated,
                        idempotencyKey,
                        now);
                    dbContext.Events.Add(DistributionEvent.Claimed(invitation, now));
                    var session = identity.WasCreated
                        ? await recipientClaimSessionIssuer
                            .IssueAsync(
                                identity.User.Id,
                                request.Password,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : null;

                    try
                    {
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await auditRecorder.RecordAsync(
                            new AuditEntry(
                                ActorUserId: identity.User.Id,
                                ActorType: AuditActorType.IdentityUser,
                                OrganizationScopeId: invitation.FundingOrganizationId,
                                Operation: AuditOperations.GiftCardClaimed,
                                EntityType: nameof(DistributionInvitation),
                                EntityId: invitation.Id.ToString(),
                                Outcome: AuditOutcome.Success,
                                CorrelationId: executionContext.CorrelationId,
                                Metadata: new Dictionary<string, string>
                                {
                                    ["giftCardId"] = invitation.GiftCardId.ToString(),
                                    ["invitationKind"] = invitation.Kind.ToString(),
                                    ["identityCreated"] =
                                        identity.WasCreated ? "true" : "false",
                                }),
                            cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is DbUpdateConcurrencyException ||
                        FindSqlState(exception) == SerializationFailure)
                    {
                        throw new ConflictException(
                            "distribution.claim.concurrent_conflict",
                            "The claim changed concurrently. Retry safely with the same idempotency key.");
                    }

                    result = ToResult(invitation, card, session, maskedLogin);
                }
            }
        }

        if (delayedFailure is not null)
        {
            throw delayedFailure;
        }

        return result!;
    }

    private Task<int> AcquireInvitationLockAsync(
        Guid invitationId,
        CancellationToken cancellationToken)
    {
        var lockKey = $"distribution-invitation|{invitationId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private async Task<(RecipientIdentityResult Identity, string MaskedLogin)>
        ResolveClaimIdentityAsync(
            DistributionInvitation invitation,
            ClaimGiftCardRequest request,
            Guid? attachingUserId,
            CancellationToken cancellationToken)
    {
        if (invitation.Kind == DistributionInvitationKind.Directed)
        {
            var directed = await recipientIdentityService
                .ResolveAsync(
                    new ResolveRecipientIdentityRequest(
                        MapContactType(invitation.ContactType!.Value),
                        invitation.RecipientContact,
                        request.Password),
                    cancellationToken)
                .ConfigureAwait(false);
            return (directed, invitation.MaskedRecipientContact!);
        }

        if (attachingUserId is not null)
        {
            var existing = await identityUserQuery
                .FindAsync(attachingUserId.Value, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new UnauthorizedException(
                    "distribution.claim.identity.invalid",
                    "The authenticated identity is unavailable.");
            if (!string.Equals(existing.Status, "Active", StringComparison.Ordinal))
            {
                throw new ConflictException(
                    "distribution.claim.identity.disabled",
                    "The authenticated identity is disabled.");
            }

            return (new RecipientIdentityResult(existing, WasCreated: false), Mask(existing));
        }

        if (request.ContactType is null)
        {
            throw new ValidationFailedException(
                "distribution.claim.contact_type.required",
                "Email or phone is required when creating an account for an e-pin claim.");
        }

        var created = await recipientIdentityService
            .ResolveAsync(
                new ResolveRecipientIdentityRequest(
                    MapContactType(request.ContactType.Value),
                    request.RecipientContact,
                    request.Password),
                cancellationToken)
            .ConfigureAwait(false);
        if (!created.WasCreated)
        {
            throw new ConflictException(
                "distribution.claim.login_required",
                "This identity already exists. Sign in, then attach the e-pin to that account.");
        }

        return (created, Mask(created.User));
    }

    private async Task<string> ResolveMaskedLoginAsync(
        DistributionInvitation invitation,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (invitation.Kind == DistributionInvitationKind.Directed)
        {
            return invitation.MaskedRecipientContact!;
        }

        var user = await identityUserQuery
            .FindAsync(userId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConflictException(
                "distribution.claim.identity.unavailable",
                "The claimed identity is unavailable.");
        return Mask(user);
    }

    private static IdentityContactType MapContactType(RecipientContactType contactType) =>
        contactType switch
        {
            RecipientContactType.Email => IdentityContactType.Email,
            RecipientContactType.Phone => IdentityContactType.Phone,
            _ => throw new InvalidOperationException("Unsupported recipient contact type."),
        };

    private static GiftCardClaimResult ToResult(
        DistributionInvitation invitation,
        GiftCardResult card,
        TokenPairResult? session,
        string maskedLogin) =>
        new(
            invitation.Id,
            invitation.ClaimedByUserId!.Value,
            invitation.IdentityWasCreatedOnClaim!.Value,
            maskedLogin,
            session is null
                ? null
                : new GiftCardClaimSessionResult(
                    session.AccessToken,
                    session.AccessTokenExpiresAtUtc,
                    session.RefreshToken,
                    session.RefreshTokenExpiresAtUtc),
            card,
            invitation.ClaimedAtUtc!.Value);

    private static bool IsAttachableIdentityCaller(MutableExecutionContext context) =>
        context.IsAuthenticated &&
        !context.IsSystem &&
        !context.IsPosClient &&
        !context.IsPartnerClient &&
        context.UserId is not null;

    private static string Mask(UserResult user)
    {
        if (user.Email is { } email)
        {
            var at = email.IndexOf('@', StringComparison.Ordinal);
            return at > 0 ? $"{email[0]}***{email[at..]}" : "***";
        }

        if (user.PhoneNumber is { } phone && phone.Length >= 4)
        {
            return $"{phone[..Math.Min(3, phone.Length - 4)]}***{phone[^4..]}";
        }

        return "***";
    }

    private static UnauthorizedException InvalidClaim() =>
        new(
            "distribution.claim.invalid",
            "The claim invitation is invalid or unavailable.");

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
