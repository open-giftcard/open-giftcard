using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GiftCardPlatform.Modules.Payments.Application;

internal sealed class PaymentTokenService(
    PaymentsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IGiftCardPaymentWriter giftCards,
    IAuditRecorder auditRecorder,
    TimeProvider timeProvider,
    IOptions<PaymentTokenOptions> options) : IPaymentTokenService
{
    private readonly PaymentTokenOptions settings = options.Value;

    public async Task<IssuedPaymentTokenResult> IssueAsync(
        Guid giftCardId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = RequireOwner();

        // Serializable for consistency with every other value-adjacent
        // operation. Issuance posts nothing, but the eligibility read and the
        // token write must not straddle a lifecycle change that would make the
        // card unspendable between them.
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Gift Cards alone decides ownership and spendability.
        var card = await giftCards
            .GetOwnedSpendableAsync(giftCardId, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var tokenId = Guid.CreateVersion7(now);
        var issued = PaymentTokenCodec.Create(tokenId);
        PaymentToken? token = null;
        NumericPaymentCodeCodec.IssuedNumericCode? numeric = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            numeric = NumericPaymentCodeCodec.Create();
            token = PaymentToken.Issue(
                tokenId,
                card.Id,
                card.FundingOrganizationId,
                card.OwnerUserId,
                issued.SecretHash,
                numeric.CodeHash,
                now,
                settings.LifetimeSeconds);

            // ON CONFLICT keeps a random 12-digit collision from aborting the
            // transaction. The unique hash index is global even though RLS
            // prevents this owner from reading another owner's token.
            var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                insert into payments.payment_tokens
                    (id, gift_card_id, funding_organization_id, owner_user_id,
                     secret_hash, numeric_code_hash, issued_at_utc, expires_at_utc)
                values
                    ({token.Id}, {token.GiftCardId}, {token.FundingOrganizationId},
                     {token.OwnerUserId}, {token.SecretHash}, {token.NumericCodeHash},
                     {token.IssuedAtUtc}, {token.ExpiresAtUtc})
                on conflict (numeric_code_hash) where numeric_code_hash is not null
                do nothing
                """,
                cancellationToken).ConfigureAwait(false);
            if (inserted == 1)
            {
                break;
            }

            token = null;
            numeric = null;
        }

        if (token is null || numeric is null)
        {
            throw new ConflictException(
                "payment.token.numeric_code.unavailable",
                "A numeric payment code could not be issued. Retry safely.");
        }

        // The raw credential is never audited (ADR-017, DOMAIN_RULES §6.5).
        await auditRecorder.RecordAsync(
            new AuditEntry(
                ownerUserId,
                AuditActorType.IdentityUser,
                card.FundingOrganizationId,
                "payment.token.issued",
                nameof(PaymentToken),
                token.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["giftCardId"] = card.Id.ToString(),
                    ["giftCardPublicReference"] = card.PublicReference,
                    ["expiresAtUtc"] = token.ExpiresAtUtc.ToString("O"),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new IssuedPaymentTokenResult(
            token.Id,
            card.Id,
            card.PublicReference,
            issued.RawToken,
            numeric.RawCode,
            token.IssuedAtUtc,
            token.ExpiresAtUtc);
    }

    public async Task<PaymentTokenStatusResult> GetStatusAsync(
        Guid giftCardId,
        Guid paymentTokenId,
        CancellationToken cancellationToken)
    {
        var ownerUserId = RequireOwner();
        if (giftCardId == Guid.Empty || paymentTokenId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "payment.token.status.required",
                "Gift-card and payment-token identifiers are required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var token = await dbContext.Tokens
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == paymentTokenId &&
                    candidate.GiftCardId == giftCardId &&
                    candidate.OwnerUserId == ownerUserId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "payment.token.not_found",
                "The payment token was not found.");
        var provision = await dbContext.Provisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.PaymentTokenId == token.Id &&
                    candidate.GiftCardId == token.GiftCardId &&
                    candidate.OwnerUserId == ownerUserId,
                cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        string state;
        DateTimeOffset expiresAtUtc;
        if (provision is null)
        {
            state = token.IsPresentable(now) ? "Pending" : "Expired";
            expiresAtUtc = token.ExpiresAtUtc;
        }
        else
        {
            state = provision.State == PaymentProvisionState.Active &&
                !provision.IsHolding(now)
                    ? PaymentProvisionState.Expired.ToString()
                    : provision.State.ToString();
            expiresAtUtc = provision.ExpiresAtUtc;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PaymentTokenStatusResult(
            token.Id,
            token.GiftCardId,
            state,
            provision?.Id,
            provision?.Amount,
            provision?.Currency,
            expiresAtUtc,
            provision?.SettledAtUtc,
            provision?.ConfirmedAmount);
    }

    private Guid RequireOwner()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null ||
            executionContext.IsPlatformOperator)
        {
            throw new ForbiddenException(
                "payment.token.owner.required",
                "An authenticated card owner is required.");
        }

        return executionContext.UserId.Value;
    }
}
