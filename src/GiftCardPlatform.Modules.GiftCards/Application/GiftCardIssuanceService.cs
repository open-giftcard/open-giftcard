using System.Data;
using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.GiftCards.Domain;
using GiftCardPlatform.Modules.GiftCards.Infrastructure;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Partners.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.GiftCards.Application;

internal sealed class GiftCardIssuanceService(
    GiftCardsDbContext dbContext,
    ILedgerWriter ledgerWriter,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    IOrganizationFinancialEligibilityQuery organizationEligibility,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) :
    IGiftCardIssuanceService,
    IPartnerGiftCardIssuanceService,
    IAcceptedBulkGiftCardIssuanceService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<GiftCardResult> IssueAsync(
        Guid issuingOrganizationId,
        IssueGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOrganization(issuingOrganizationId);
        var intent = GiftCardIssuanceIntent.Create(request);
        await organizationAuthorizer
            .RequirePermissionAsync(
                issuingOrganizationId,
                OrganizationPermissions.GiftCardsIssue,
                cancellationToken)
            .ConfigureAwait(false);

        var fundingOrganizationId = executionContext.TenantRootOrganizationId
            ?? throw new ForbiddenException(
                "auth.unauthenticated",
                "A verified organization membership is required.");
        var actorUserId = executionContext.UserId!.Value;
        var actorMembershipId = executionContext.ActiveMembershipId!.Value;
        return await IssueCoreAsync(
                fundingOrganizationId,
                issuingOrganizationId,
                actorUserId,
                actorMembershipId,
                intent,
                actorUserId,
                AuditActorType.OrganizationMember,
                actorMembershipId,
                issuedByPartnerClientId: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    async Task<GiftCardResult> IPartnerGiftCardIssuanceService.MintAsync(
        IssueGiftCardRequest request,
        CancellationToken cancellationToken)
    {
        EnsurePartnerMayMint(executionContext);

        var fundingOrganizationId = executionContext.TenantRootOrganizationId!.Value;
        var partnerClientId = executionContext.PartnerClientId!.Value;
        var intent = GiftCardIssuanceIntent.Create(request);

        // A partner is always its own root organization. There is no request
        // field through which it can select another issuer or funding tenant.
        return await IssueCoreAsync(
                fundingOrganizationId,
                fundingOrganizationId,
                partnerClientId,
                issuedByMembershipId: null,
                intent,
                partnerClientId,
                AuditActorType.PartnerClient,
                auditMembershipId: null,
                issuedByPartnerClientId: partnerClientId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    async Task<GiftCardResult> IAcceptedBulkGiftCardIssuanceService.IssueAsync(
        IssueAcceptedBulkGiftCardItemRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!executionContext.IsSystem ||
            executionContext.UserId != SystemActorIds.BulkGiftCardBatch)
        {
            throw new ForbiddenException(
                "bulk.processor.system_required",
                "Only the bulk-batch system processor may issue an accepted item.");
        }

        EnsureOrganization(request.IssuingOrganizationId);
        if (request.FundingOrganizationId == Guid.Empty ||
            request.AcceptedByUserId == Guid.Empty ||
            request.AcceptedByMembershipId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "bulk.processor.scope.required",
                "Accepted batch funding and actor attribution are required.");
        }

        var intent = GiftCardIssuanceIntent.Create(request.Issuance);
        return await IssueCoreAsync(
                request.FundingOrganizationId,
                request.IssuingOrganizationId,
                request.AcceptedByUserId,
                request.AcceptedByMembershipId,
                intent,
                SystemActorIds.BulkGiftCardBatch,
                AuditActorType.System,
                auditMembershipId: null,
                issuedByPartnerClientId: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GiftCardResult> IssueCoreAsync(
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        Guid issuedByUserId,
        Guid? issuedByMembershipId,
        GiftCardIssuanceIntent intent,
        Guid auditActorId,
        AuditActorType auditActorType,
        Guid? auditMembershipId,
        Guid? issuedByPartnerClientId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.GiftCards
            .AsNoTracking()
            .SingleOrDefaultAsync(
                card =>
                    card.FundingOrganizationId == fundingOrganizationId &&
                    card.IdempotencyKey == intent.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.Matches(fundingOrganizationId, issuingOrganizationId, intent))
            {
                throw new ConflictException(
                    "gift_card.idempotency_key.reused",
                    "The idempotency key was already used for different gift-card issuance intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return GiftCardMapping.ToResult(existing);
        }

        intent.EnsureCanIssueAt(timeProvider.GetUtcNow());
        if (!await organizationEligibility
                .IsActiveIssuingOrganizationAsync(
                    fundingOrganizationId,
                    issuingOrganizationId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ValidationFailedException(
                "gift_card.issuing_organization.ineligible",
                "Gift cards can only be issued by an active organization in the funding customer hierarchy.");
        }

        var cardId = Guid.CreateVersion7();
        var funding = await ledgerWriter
            .RecordGiftCardIssuanceAsync(
                new RecordGiftCardIssuanceRequest(
                    fundingOrganizationId,
                    cardId,
                    intent.Amount,
                    intent.Currency,
                    intent.BusinessReference,
                    intent.ToLedgerIdempotencyKey(fundingOrganizationId)),
                cancellationToken)
            .ConfigureAwait(false);
        var card = GiftCard.Create(
            cardId,
            GiftCardPublicReferenceGenerator.Create(),
            fundingOrganizationId,
            issuingOrganizationId,
            intent,
            funding,
            issuedByUserId,
            issuedByMembershipId,
            issuedByPartnerClientId);
        dbContext.GiftCards.Add(card);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await auditRecorder.RecordAsync(
                new AuditEntry(
                    ActorUserId: auditActorId,
                    ActorType: auditActorType,
                    OrganizationScopeId: issuingOrganizationId,
                    Operation: AuditOperations.GiftCardIssued,
                    EntityType: nameof(GiftCard),
                    EntityId: card.Id.ToString(),
                    Outcome: AuditOutcome.Success,
                    CorrelationId: executionContext.CorrelationId,
                    Metadata: BuildAuditMetadata(
                        card,
                        fundingOrganizationId,
                        issuingOrganizationId,
                        issuedByUserId,
                        issuedByMembershipId,
                        issuedByPartnerClientId),
                    ActorMembershipId: auditMembershipId),
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return GiftCardMapping.ToResult(card);
    }

    internal static void EnsurePartnerMayMint(IExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsAuthenticated ||
            !context.IsPartnerClient ||
            context.IsPlatformOperator ||
            context.IsSystem ||
            context.PartnerId is null ||
            context.PartnerClientId is null ||
            context.TenantRootOrganizationId is null)
        {
            throw new ForbiddenException(
                "partner.principal.required",
                "An authenticated partner API client is required.");
        }

        if (!context.HasPartnerScope(PartnerScopes.GiftCardsMint))
        {
            throw new ForbiddenException(
                "partner.scope.gift_cards_mint.required",
                "The partner API client is not allowed to mint gift cards.");
        }
    }

    private static Dictionary<string, string> BuildAuditMetadata(
        GiftCard card,
        Guid fundingOrganizationId,
        Guid issuingOrganizationId,
        Guid issuedByUserId,
        Guid? issuedByMembershipId,
        Guid? issuedByPartnerClientId)
    {
        var metadata = new Dictionary<string, string>
        {
            ["fundingOrganizationId"] = fundingOrganizationId.ToString(),
            ["issuingOrganizationId"] = issuingOrganizationId.ToString(),
            ["ledgerAccountId"] = card.LedgerAccountId.ToString(),
            ["ledgerTransactionId"] = card.IssuanceLedgerTransactionId.ToString(),
            ["amount"] = card.InitialValue.ToString(CultureInfo.InvariantCulture),
            ["currency"] = card.Currency,
            ["businessReference"] = card.BusinessReference,
        };

        if (issuedByMembershipId is not null)
        {
            metadata["acceptedByUserId"] = issuedByUserId.ToString();
            metadata["acceptedByMembershipId"] = issuedByMembershipId.Value.ToString();
        }

        if (issuedByPartnerClientId is not null)
        {
            metadata["partnerClientId"] = issuedByPartnerClientId.Value.ToString();
        }

        return metadata;
    }

    private static void EnsureOrganization(Guid issuingOrganizationId)
    {
        if (issuingOrganizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "gift_card.issuing_organization.required",
                "An issuing organization is required.");
        }
    }

    private static bool IsFinancialConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: UniqueViolation or SerializationFailure,
                })
            {
                return true;
            }
        }

        return false;
    }
}
