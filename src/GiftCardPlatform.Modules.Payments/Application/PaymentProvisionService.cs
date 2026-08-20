using System.Data;
using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.GiftCards.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Domain;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using GiftCardPlatform.Modules.Sharing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Payments.Application;

internal sealed class PaymentProvisionService(
    PaymentsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    MutableExecutionContext executionContext,
    IGiftCardPaymentWriter giftCards,
    IGiftCardPaymentLedger ledger,
    IShareReservationQuery shareReservations,
    IAuditRecorder auditRecorder,
    TimeProvider timeProvider,
    IOptions<PaymentProvisionOptions> options) :
    IPaymentProvisionService,
    IPaymentProvisionExpirationProcessor,
    IPaymentBalanceInquiryService
{
    private readonly PaymentProvisionOptions settings = options.Value;

    public async Task<PaymentProvisionResult> CreateAsync(
        CreatePaymentProvisionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CreateCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            // A concurrent presentation can lose at PostgreSQL's serializable
            // boundary after the other request consumes the credential. It is
            // still only a replay and must not leak as an internal error.
            throw CredentialRefused();
        }
    }

    private async Task<PaymentProvisionResult> CreateCoreAsync(
        CreatePaymentProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePosDevice();

        var idempotencyKey = request.IdempotencyKey?.Trim();
        if (string.IsNullOrEmpty(idempotencyKey) ||
            idempotencyKey.Length > PaymentProvision.IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "payment.provision.idempotency_key.invalid",
                "An idempotency key is required and may be at most " +
                $"{PaymentProvision.IdempotencyKeyMaxLength} characters.");
        }

        var hasQrToken = !string.IsNullOrWhiteSpace(request.PaymentToken);
        var hasNumericCode = !string.IsNullOrWhiteSpace(request.PaymentCode);
        if (hasQrToken == hasNumericCode)
        {
            throw CredentialRefused();
        }

        Guid tokenId;
        byte[] secret = [];
        string? numericCode = null;
        if (hasQrToken)
        {
            // Parse before anything else. A malformed credential is refused
            // exactly like an unknown one, so the shape reveals nothing.
            if (!PaymentTokenCodec.TryParse(
                    request.PaymentToken,
                    out tokenId,
                    out secret))
            {
                throw CredentialRefused();
            }
        }
        else
        {
            if (!NumericPaymentCodeCodec.TryNormalize(
                    request.PaymentCode,
                    out numericCode))
            {
                throw CredentialRefused();
            }

            var codeHash = NumericPaymentCodeCodec.Hash(numericCode);
            executionContext.SetPaymentCodeCandidate(codeHash);
            await using var lookup = await transactionCoordinator
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await lookup.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            tokenId = await dbContext.Tokens
                .AsNoTracking()
                .Where(token => token.NumericCodeHash == codeHash)
                .Select(token => token.Id)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            await lookup.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (tokenId == Guid.Empty)
            {
                throw CredentialRefused();
            }
        }

        executionContext.SetPaymentTokenCandidate(tokenId);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Lock the credential row first. Two tills scanning the same code
        // serialise here, and the unique index on payment_token_id is the
        // backstop if they somehow do not.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({$"payment-token|{tokenId:D}"}, 0))",
            cancellationToken).ConfigureAwait(false);

        // Answer a retry before going anywhere near the credential. The
        // credential is single use, so a till repeating a request whose response
        // it never received would otherwise be refused as a replay: correct for
        // an attacker, useless for a cashier whose network dropped mid-sale.
        var replayed = await dbContext.Provisions.SingleOrDefaultAsync(
            provision => provision.PosClientId == executionContext.PosClientId!.Value &&
                provision.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (replayed is not null)
        {
            if (!replayed.Matches(tokenId, request.Amount, request.PosTransactionReference))
            {
                throw new ConflictException(
                    "payment.provision.idempotency_conflict",
                    "The idempotency key was already used with different payment intent.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(replayed);
        }

        var token = await dbContext.Tokens
            .SingleOrDefaultAsync(item => item.Id == tokenId, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();

        // Unknown, wrong-secret, consumed, and expired are one refusal. The
        // secret is verified even when the row is missing so timing does not
        // separate the cases either (ADR-017).
        var credentialMatches = hasQrToken
            ? PaymentTokenCodec.Matches(
                token?.SecretHash ?? new string('0', PaymentTokenCodec.HashHexLength),
                secret)
            : NumericPaymentCodeCodec.Matches(
                token?.NumericCodeHash ?? new string('0', NumericPaymentCodeCodec.HashHexLength),
                numericCode!);
        if (token is null || !credentialMatches || !token.IsPresentable(now))
        {
            throw CredentialRefused();
        }

        var card = await giftCards
            .GetCredentialSpendableAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var balance = await ledger
            .GetLockedBalanceAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);

        // Available value is posted balance minus every other active hold, of
        // either kind. Missing either term is how a share and a till end up
        // spending the same money (DOMAIN_RULES 10.20).
        var shared = await shareReservations
            .GetActiveReservedAmountAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var provisioned = await SumActiveProvisionsAsync(
            token.GiftCardId,
            now,
            cancellationToken).ConfigureAwait(false);
        var available = balance.Amount - shared - provisioned;

        // A gift card usually cannot settle a whole basket, and the customer
        // rarely knows the balance, so being asked for more than the card holds
        // is the ordinary case rather than an error. A till that says it can
        // collect the remainder is approved for what is actually there; one that
        // has not said so keeps the refusal, because approving it for less than
        // it asked without its knowledge would under-charge the sale.
        var amountToHold = request.Amount;
        if (request.Amount > available)
        {
            if (!request.AllowPartialApproval || available <= 0)
            {
                throw new ConflictException(
                    "payment.provision.insufficient_value",
                    "The card does not have enough available value for this payment.");
            }

            amountToHold = decimal.Round(available, PaymentProvision.AmountScale);

            // Rounding must never invent value the card does not have.
            if (amountToHold > available)
            {
                amountToHold -= (decimal)Math.Pow(10, -PaymentProvision.AmountScale);
            }

            if (amountToHold <= 0)
            {
                throw new ConflictException(
                    "payment.provision.insufficient_value",
                    "The card does not have enough available value for this payment.");
            }
        }

        var provision = PaymentProvision.Create(
            Guid.CreateVersion7(now),
            token.Id,
            card.Id,
            card.PublicReference,
            card.FundingOrganizationId,
            card.OwnerUserId,
            executionContext.PosClientId!.Value,
            executionContext.PosTerminalId!.Value,
            await ResolveStoreReferenceAsync(cancellationToken).ConfigureAwait(false),
            request.PosTransactionReference,
            idempotencyKey,
            amountToHold,
            request.Amount,
            balance.Currency,
            now,
            settings.WindowSeconds);

        token.Consume(now);
        dbContext.Provisions.Add(provision);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(provision, AuditOperations.PaymentProvisionCreated, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(provision);
    }

    public async Task<PaymentProvisionResult> GetAsync(
        Guid provisionId,
        CancellationToken cancellationToken)
    {
        EnsurePosDevice();
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var provision = await FindOwnProvisionAsync(provisionId, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(provision);
    }

    public async Task<PaymentProvisionResult> CancelAsync(
        Guid provisionId,
        CancellationToken cancellationToken)
    {
        EnsurePosDevice();
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        await AcquireProvisionLockAsync(provisionId, cancellationToken).ConfigureAwait(false);

        var provision = await FindOwnProvisionAsync(provisionId, cancellationToken)
            .ConfigureAwait(false);
        provision.Cancel(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecordAuditAsync(provision, AuditOperations.PaymentProvisionCancelled, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(provision);
    }

    public async Task<PaymentProvisionResult> ConfirmAsync(
        Guid provisionId,
        ConfirmPaymentProvisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePosDevice();

        // The POS-client RLS path can reveal only this client's own provision.
        // Read its server-owned credential identity first, then establish that
        // exact candidate before the financial transaction begins so every
        // cross-schema policy receives it through SET LOCAL.
        Guid paymentTokenId;
        await using (var lookup = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await lookup.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            var hint = await FindOwnProvisionHintAsync(provisionId, cancellationToken)
                .ConfigureAwait(false);
            paymentTokenId = hint.PaymentTokenId;
            await lookup.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        executionContext.SetPaymentTokenCandidate(paymentTokenId);
        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            await AcquireProvisionLockAsync(provisionId, cancellationToken).ConfigureAwait(false);

            var provision = await FindOwnProvisionAsync(provisionId, cancellationToken)
                .ConfigureAwait(false);
            if (provision.State == PaymentProvisionState.Confirmed)
            {
                if (!provision.MatchesConfirmation(request.Amount))
                {
                    throw new ConflictException(
                        "payment.confirmation.already_completed",
                        "The payment provision was already confirmed with different intent.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ToResult(provision);
            }

            var now = timeProvider.GetUtcNow();
            provision.EnsureCanConfirm(request.Amount, now);

            // This obtains the same gift-card lock as sharing and lifecycle,
            // then re-checks ownership state and validity inside this exact
            // confirmation transaction. The following Ledger call takes the
            // card-value lock after it, preserving the common lock order.
            var card = await giftCards.GetCredentialSpendableAsync(
                provision.GiftCardId,
                cancellationToken).ConfigureAwait(false);
            if (card.FundingOrganizationId != provision.FundingOrganizationId ||
                card.OwnerUserId != provision.OwnerUserId ||
                card.Currency != provision.Currency)
            {
                throw new ConflictException(
                    "payment.confirmation.unavailable",
                    "The payment provision cannot be confirmed.");
            }

            var redemption = await ledger.RecordRedemptionAsync(
                new RecordGiftCardRedemptionRequest(
                    provision.PaymentTokenId,
                    provision.Id,
                    provision.FundingOrganizationId,
                    provision.GiftCardId,
                    request.Amount,
                    provision.Currency,
                    provision.PosTransactionReference ?? $"PAYMENT-{provision.Id:N}"),
                cancellationToken).ConfigureAwait(false);
            provision.Confirm(
                request.Amount,
                redemption.TransactionId,
                redemption.PostedAtUtc);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordAuditAsync(
                provision,
                AuditOperations.PaymentProvisionConfirmed,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(provision);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry the confirmation safely.");
        }
    }

    public async Task<PaymentRefundResult> RefundAsync(
        Guid provisionId,
        CreatePaymentRefundRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePosDevice();
        var idempotencyKey = PaymentRefund.NormalizeIdempotencyKey(request.IdempotencyKey);
        PaymentRefund.ValidateIntent(
            request.Amount,
            request.PosTransactionReference,
            request.Reason);

        Guid paymentTokenId;
        await using (var lookup = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await lookup.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            paymentTokenId = (await FindOwnProvisionHintAsync(provisionId, cancellationToken)
                .ConfigureAwait(false)).PaymentTokenId;
            await lookup.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        executionContext.SetPaymentTokenCandidate(paymentTokenId);
        try
        {
            await using var transaction = await transactionCoordinator
                .BeginAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            await AcquireProvisionLockAsync(provisionId, cancellationToken).ConfigureAwait(false);
            var provision = await FindOwnProvisionAsync(provisionId, cancellationToken)
                .ConfigureAwait(false);

            var existing = await dbContext.Refunds.SingleOrDefaultAsync(
                refund => refund.PaymentProvisionId == provisionId &&
                    refund.IdempotencyKey == idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            var refunded = await SumRefundsAsync(provisionId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.Matches(request.Amount, request.PosTransactionReference, request.Reason))
                {
                    throw new ConflictException(
                        "payment.refund.idempotency_conflict",
                        "The idempotency key was already used with different refund intent.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ToResult(existing, provision.ConfirmedAmount!.Value - refunded);
            }

            if (provision.State != PaymentProvisionState.Confirmed ||
                provision.ConfirmedAmount is null ||
                provision.RedemptionLedgerTransactionId is null)
            {
                throw new ConflictException(
                    "payment.refund.not_confirmed",
                    "Only a confirmed payment can be refunded.");
            }
            if (request.Amount <= 0 || request.Amount > provision.ConfirmedAmount.Value - refunded)
            {
                throw new ConflictException(
                    "payment.refund.exceeds_remaining",
                    "The refund amount exceeds the remaining refundable amount.");
            }

            var card = await giftCards.GetCredentialRefundableAsync(
                provision.GiftCardId, cancellationToken).ConfigureAwait(false);
            if (card.FundingOrganizationId != provision.FundingOrganizationId ||
                card.OwnerUserId != provision.OwnerUserId || card.Currency != provision.Currency)
            {
                throw new ConflictException(
                    "payment.refund.unavailable", "The payment cannot be refunded.");
            }

            var refundId = Guid.CreateVersion7(timeProvider.GetUtcNow());
            var ledgerRefund = await ledger.RecordRefundAsync(
                new RecordGiftCardRefundRequest(
                    provision.PaymentTokenId, provision.Id, refundId,
                    provision.RedemptionLedgerTransactionId.Value,
                    provision.FundingOrganizationId, provision.GiftCardId,
                    request.Amount, provision.Currency,
                    request.PosTransactionReference ?? $"REFUND-{refundId:N}"),
                cancellationToken).ConfigureAwait(false);
            var refund = PaymentRefund.Create(
                refundId, provision, ledgerRefund.TransactionId,
                executionContext.PosTerminalId!.Value,
                await ResolveStoreReferenceAsync(cancellationToken).ConfigureAwait(false),
                request.PosTransactionReference, idempotencyKey, request.Reason,
                request.Amount, ledgerRefund.PostedAtUtc);
            dbContext.Refunds.Add(refund);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RecordRefundAuditAsync(refund, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(
                refund, provision.ConfirmedAmount.Value - refunded - refund.Amount);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry the refund safely.");
        }
    }

    public async Task<PaymentProvisionExpirationBatchResult> ProcessDueAsync(
        int maximumItems,
        CancellationToken cancellationToken)
    {
        if (maximumItems <= 0)
        {
            return new PaymentProvisionExpirationBatchResult(0, 0);
        }

        var now = timeProvider.GetUtcNow();
        List<Guid> due;
        await using (var scan = await transactionCoordinator
            .BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            await scan.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
            due = await dbContext.Provisions
                .IgnoreQueryFilters()
                .Where(provision =>
                    provision.State == PaymentProvisionState.Active &&
                    provision.ExpiresAtUtc <= now)
                .OrderBy(provision => provision.ExpiresAtUtc)
                .ThenBy(provision => provision.Id)
                .Select(provision => provision.Id)
                .Take(maximumItems)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            await scan.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var expired = 0;
        foreach (var provisionId in due)
        {
            try
            {
                await using var transaction = await transactionCoordinator
                    .BeginAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
                await AcquireProvisionLockAsync(provisionId, cancellationToken)
                    .ConfigureAwait(false);
                var provision = await dbContext.Provisions
                    .IgnoreQueryFilters()
                    .SingleOrDefaultAsync(item => item.Id == provisionId, cancellationToken)
                    .ConfigureAwait(false);
                if (provision is null || provision.State != PaymentProvisionState.Active)
                {
                    continue;
                }

                provision.Expire(timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                expired++;
            }
            catch (ConflictException)
            {
                // Another writer settled it first. Expiry is idempotent; the
                // reservation is released either way.
            }
        }

        return new PaymentProvisionExpirationBatchResult(due.Count, expired);
    }

    private Task<decimal> SumActiveProvisionsAsync(
        Guid giftCardId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        SumAsync(dbContext.Provisions
            .IgnoreQueryFilters()
            .Where(provision =>
                provision.GiftCardId == giftCardId &&
                provision.State == PaymentProvisionState.Active &&
                provision.ExpiresAtUtc > now),
            cancellationToken);

    private static async Task<decimal> SumAsync(
        IQueryable<PaymentProvision> query,
        CancellationToken cancellationToken) =>
        await query
            .SumAsync(provision => (decimal?)provision.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

    private async Task<decimal> SumRefundsAsync(
        Guid provisionId,
        CancellationToken cancellationToken) =>
        await dbContext.Refunds
            .Where(refund => refund.PaymentProvisionId == provisionId)
            .SumAsync(refund => (decimal?)refund.Amount, cancellationToken)
            .ConfigureAwait(false) ?? 0m;

    private async Task<PaymentProvision> FindOwnProvisionAsync(
        Guid provisionId,
        CancellationToken cancellationToken) =>
        await dbContext.Provisions
            .SingleOrDefaultAsync(
                item => item.Id == provisionId &&
                    item.PosClientId == executionContext.PosClientId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "payment.provision.not_found",
                "The payment provision was not found.");

    private async Task<PaymentProvision> FindOwnProvisionHintAsync(
        Guid provisionId,
        CancellationToken cancellationToken) =>
        await dbContext.Provisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == provisionId &&
                    item.PosClientId == executionContext.PosClientId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "payment.provision.not_found",
                "The payment provision was not found.");

    private Task<int> AcquireProvisionLockAsync(
        Guid provisionId,
        CancellationToken cancellationToken)
    {
        var lockKey = $"payment-provision|{provisionId:D}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private async Task<string> ResolveStoreReferenceAsync(CancellationToken cancellationToken) =>
        await dbContext.PosTerminals
            .AsNoTracking()
            .Where(terminal => terminal.Id == executionContext.PosTerminalId)
            .Select(terminal => terminal.StoreReference)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ForbiddenException(
                "pos.terminal.unavailable",
                "The POS terminal is not available.");


    public async Task<PaymentBalanceInquiryResult> InquireAsync(
        PaymentBalanceInquiryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsurePosDevice();

        // Resolved exactly as a provision resolves it, so an unknown, malformed,
        // expired or already-consumed credential is refused identically here
        // too. An inquiry that failed differently from a payment would be an
        // oracle for which cards exist (ADR-017).
        var tokenId = await ResolvePresentedTokenIdAsync(
            request.PaymentToken,
            request.PaymentCode,
            cancellationToken).ConfigureAwait(false);

        executionContext.SetPaymentTokenCandidate(tokenId);

        // Serializable because the card's spendability read requires it, and
        // PostgreSQL cannot raise isolation once a transaction has begun. What
        // this path still avoids is the card's advisory value lock, which is the
        // contention that actually matters: that lock is what serialises shares
        // against payments, and taking it on a read a till can repeat would put
        // an inquiry in the way of every payment on the card. This transaction
        // reserves nothing and writes nothing.
        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var token = await dbContext.Tokens
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == tokenId, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var hasQrToken = !string.IsNullOrWhiteSpace(request.PaymentToken);
        var credentialMatches = hasQrToken
            ? PaymentTokenCodec.Matches(
                token?.SecretHash ?? new string('0', PaymentTokenCodec.HashHexLength),
                ParseQrSecretOrEmpty(request.PaymentToken))
            : NumericPaymentCodeCodec.Matches(
                token?.NumericCodeHash ?? new string('0', NumericPaymentCodeCodec.HashHexLength),
                NormalizeNumericOrEmpty(request.PaymentCode));
        if (token is null || !credentialMatches || !token.IsPresentable(now))
        {
            throw CredentialRefused();
        }

        var card = await giftCards
            .GetCredentialSpendableAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);

        // Publish the tenant this device's credential authorises it to act for,
        // so the audit record can be attributed to the organization whose money
        // is being asked about. Only knowable now: a POS token carries no
        // organization, and the card is what supplies one. Transaction-local, so
        // it cannot outlive this request.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select set_config({SessionContextWriter.PosCredentialOrganizationIdSetting}, {card.FundingOrganizationId.ToString()}, true)",
            cancellationToken).ConfigureAwait(false);

        // The non-locking read. An inquiry must never contend with the payments
        // and shares it is about to sit alongside.
        var balance = await ledger
            .GetBalanceAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var shared = await shareReservations
            .GetActiveReservedAmountAsync(token.GiftCardId, cancellationToken)
            .ConfigureAwait(false);
        var provisioned = await SumActiveProvisionsAsync(
            token.GiftCardId,
            now,
            cancellationToken).ConfigureAwait(false);

        // Value already promised to a share or another till is not spendable
        // here, so telling a cashier the posted balance would overstate it.
        var available = balance.Amount - shared - provisioned;
        if (available < 0)
        {
            available = 0m;
        }

        // Audited inside the transaction, because the recorder requires an audit
        // record to commit atomically with what it describes.
        await auditRecorder.RecordAsync(
            new AuditEntry(
                executionContext.PosClientId!.Value,
                AuditActorType.PosClient,
                card.FundingOrganizationId,
                AuditOperations.PaymentBalanceInquired,
                nameof(PaymentProvision),
                token.GiftCardId.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["giftCardId"] = token.GiftCardId.ToString(),
                    ["posTerminalId"] = executionContext.PosTerminalId!.Value.ToString(),
                    ["availableAmount"] = available.ToString(
                        "0.####",
                        CultureInfo.InvariantCulture),
                    ["currency"] = balance.Currency,
                }),
            cancellationToken).ConfigureAwait(false);

        // The credential is deliberately not consumed. Asking what a card is
        // worth must not cost the customer the code they are about to pay with.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PaymentBalanceInquiryResult(
            card.PublicReference,
            available,
            balance.Currency,
            card.ExpiresAtUtc);
    }

    private static byte[] ParseQrSecretOrEmpty(string? paymentToken) =>
        PaymentTokenCodec.TryParse(paymentToken, out _, out var secret) ? secret : [];

    private static string NormalizeNumericOrEmpty(string? paymentCode) =>
        NumericPaymentCodeCodec.TryNormalize(paymentCode, out var normalized)
            ? normalized
            : string.Empty;

    /// <summary>
    /// One presented credential, in either form, reduced to the token id it
    /// names. Refuses malformed input exactly as an unknown credential is
    /// refused.
    /// </summary>
    private async Task<Guid> ResolvePresentedTokenIdAsync(
        string? paymentToken,
        string? paymentCode,
        CancellationToken cancellationToken)
    {
        var hasQrToken = !string.IsNullOrWhiteSpace(paymentToken);
        var hasNumericCode = !string.IsNullOrWhiteSpace(paymentCode);
        if (hasQrToken == hasNumericCode)
        {
            throw CredentialRefused();
        }

        if (hasQrToken)
        {
            if (!PaymentTokenCodec.TryParse(paymentToken, out var tokenId, out _))
            {
                throw CredentialRefused();
            }

            return tokenId;
        }

        if (!NumericPaymentCodeCodec.TryNormalize(paymentCode, out var numericCode))
        {
            throw CredentialRefused();
        }

        var codeHash = NumericPaymentCodeCodec.Hash(numericCode);
        executionContext.SetPaymentCodeCandidate(codeHash);
        await using var lookup = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await lookup.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var resolved = await dbContext.Tokens
            .AsNoTracking()
            .Where(token => token.NumericCodeHash == codeHash)
            .Select(token => token.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        await lookup.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (resolved == Guid.Empty)
        {
            throw CredentialRefused();
        }

        return resolved;
    }

    private Task RecordAuditAsync(
        PaymentProvision provision,
        string operation,
        CancellationToken cancellationToken) =>
        auditRecorder.RecordAsync(
            new AuditEntry(
                provision.PosClientId,
                AuditActorType.PosClient,
                provision.FundingOrganizationId,
                operation,
                nameof(PaymentProvision),
                provision.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["giftCardId"] = provision.GiftCardId.ToString(),
                    ["posTerminalId"] = provision.PosTerminalId.ToString(),
                    ["storeReference"] = provision.StoreReference,
                    // Invariant, so a server locale cannot change how a recorded
                    // amount reads back.
                    ["amount"] = provision.Amount.ToString("0.####", CultureInfo.InvariantCulture),
                    ["currency"] = provision.Currency,
                    ["confirmedAmount"] = provision.ConfirmedAmount?.ToString(
                        "0.####",
                        CultureInfo.InvariantCulture) ?? string.Empty,
                    ["redemptionLedgerTransactionId"] =
                        provision.RedemptionLedgerTransactionId?.ToString() ?? string.Empty,
                }),
            cancellationToken);

    private Task RecordRefundAuditAsync(
        PaymentRefund refund,
        CancellationToken cancellationToken) =>
        auditRecorder.RecordAsync(
            new AuditEntry(
                refund.PosClientId,
                AuditActorType.PosClient,
                refund.FundingOrganizationId,
                AuditOperations.PaymentRefundCreated,
                nameof(PaymentRefund),
                refund.Id.ToString(),
                AuditOutcome.Success,
                executionContext.CorrelationId,
                new Dictionary<string, string>
                {
                    ["paymentProvisionId"] = refund.PaymentProvisionId.ToString(),
                    ["giftCardId"] = refund.GiftCardId.ToString(),
                    ["posTerminalId"] = refund.PosTerminalId.ToString(),
                    ["amount"] = refund.Amount.ToString("0.####", CultureInfo.InvariantCulture),
                    ["currency"] = refund.Currency,
                    ["refundLedgerTransactionId"] = refund.RefundLedgerTransactionId.ToString(),
                    ["reason"] = refund.Reason,
                }),
            cancellationToken);

    private void EnsurePosDevice()
    {
        if (!executionContext.IsPosClient ||
            executionContext.PosClientId is null ||
            executionContext.PosTerminalId is null)
        {
            throw new ForbiddenException(
                "payment.provision.pos.required",
                "An authenticated POS client and terminal are required.");
        }
    }

    private static UnauthorizedException CredentialRefused() =>
        new("payment.credential.invalid", "The payment credential is not valid.");

    private static PaymentProvisionResult ToResult(PaymentProvision provision) =>
        new(
            provision.Id,
            provision.GiftCardId,
            provision.GiftCardPublicReference,
            provision.Amount,
            provision.RequestedAmount,
            provision.RequestedAmount - provision.Amount,
            provision.Currency,
            provision.State.ToString(),
            provision.StoreReference,
            provision.PosTransactionReference,
            provision.CreatedAtUtc,
            provision.ExpiresAtUtc,
            provision.SettledAtUtc,
            provision.ConfirmedAmount,
            provision.RedemptionLedgerTransactionId);

    private static PaymentRefundResult ToResult(PaymentRefund refund, decimal remaining) =>
        new(
            refund.Id, refund.PaymentProvisionId, refund.GiftCardId,
            refund.GiftCardPublicReference, refund.Amount, refund.Currency,
            refund.StoreReference, refund.PosTransactionReference, refund.Reason,
            refund.RefundLedgerTransactionId, refund.RefundedAtUtc, remaining);

    private static bool IsFinancialConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres &&
                postgres.SqlState is PostgresErrorCodes.UniqueViolation or
                    PostgresErrorCodes.SerializationFailure)
            {
                return true;
            }

            if (current is DbUpdateConcurrencyException)
            {
                return true;
            }
        }

        return false;
    }
}
