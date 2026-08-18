using System.Data;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Domain;
using GiftCardPlatform.Modules.CorporateCredits.Infrastructure;
using GiftCardPlatform.Modules.Ledger.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.CorporateCredits.Application;

internal sealed class CorporateCreditReversalService(
    CorporateCreditsDbContext dbContext,
    ILedgerWriter ledgerWriter,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : ICorporateCreditReversalService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<CorporateCreditReversalResult> ReverseAsync(
        ReverseCorporateCreditRequest request,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var intent = CorporateCreditReversalIntent.Create(request);

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existingByKey = await dbContext.Reversals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                reversal => reversal.IdempotencyKey == intent.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingByKey is not null)
        {
            if (!existingByKey.Matches(intent))
            {
                throw new ConflictException(
                    "corporate_credit.reversal.idempotency_key.reused",
                    "The idempotency key was already used for a different reversal.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(existingByKey);
        }

        var allocation = await dbContext.Allocations
            .SingleOrDefaultAsync(
                item => item.Id == intent.AllocationId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "corporate_credit.allocation.not_found",
                "Corporate-credit allocation not found.");

        var existingForAllocation = await dbContext.Reversals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                reversal => reversal.AllocationId == allocation.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingForAllocation is not null)
        {
            throw new ConflictException(
                "corporate_credit.allocation.already_reversed",
                "The corporate-credit allocation has already been reversed.");
        }

        var ledgerResult = await ledgerWriter
            .RecordCorporateCreditReversalAsync(
                intent.ToLedgerRequest(allocation),
                cancellationToken)
            .ConfigureAwait(false);
        var reversal = CorporateCreditReversal.Create(
            allocation,
            intent,
            ledgerResult,
            executionContext.UserId!.Value);
        dbContext.Reversals.Add(reversal);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await auditRecorder.RecordAsync(
                new AuditEntry(
                    executionContext.UserId.Value,
                    AuditActorType.PlatformOperator,
                    reversal.OrganizationId,
                    AuditOperations.CorporateCreditReversed,
                    nameof(CorporateCreditReversal),
                    reversal.Id.ToString(),
                    AuditOutcome.Success,
                    executionContext.CorrelationId,
                    new Dictionary<string, string>
                    {
                        ["allocationId"] = reversal.AllocationId.ToString(),
                        ["ledgerTransactionId"] = reversal.LedgerTransactionId.ToString(),
                        ["amount"] = reversal.Amount.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = reversal.Currency,
                        ["reason"] = reversal.Reason,
                    }),
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFinancialConcurrencyConflict(exception))
        {
            throw new ConflictException(
                "financial.concurrent_conflict",
                "A concurrent financial operation conflicted. Retry safely with the same idempotency key.");
        }

        return ToResult(reversal);
    }

    private void RequirePlatformPermission()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException("auth.unauthenticated", "Authentication is required.");
        }

        if (!executionContext.HasPlatformPermission(PlatformPermissions.CorporateCreditsReverse))
        {
            throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
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

    private static CorporateCreditReversalResult ToResult(CorporateCreditReversal reversal) =>
        new(
            reversal.Id,
            reversal.AllocationId,
            reversal.OrganizationId,
            reversal.LedgerTransactionId,
            reversal.Amount,
            reversal.Currency,
            reversal.Reason,
            reversal.IdempotencyKey,
            reversal.ReversedAtUtc);
}
