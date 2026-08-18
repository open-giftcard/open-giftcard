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
using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.CorporateCredits.Application;

internal sealed class CorporateCreditAllocationService(
    CorporateCreditsDbContext dbContext,
    ILedgerWriter ledgerWriter,
    IOrganizationFinancialEligibilityQuery organizationEligibility,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : ICorporateCreditAllocationService
{
    private const string UniqueViolation = "23505";
    private const string SerializationFailure = "40001";

    public async Task<CorporateCreditAllocationResult> AllocateAsync(
        AllocateCorporateCreditRequest request,
        CancellationToken cancellationToken)
    {
        RequirePlatformPermission();
        var intent = CorporateCreditIntent.Create(request);
        var ledgerRequest = intent.ToLedgerRequest();

        await using var transaction = await transactionCoordinator
            .BeginAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.Allocations
            .AsNoTracking()
            .SingleOrDefaultAsync(
                allocation => allocation.IdempotencyKey == intent.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.Matches(ledgerRequest))
            {
                throw new ConflictException(
                    "corporate_credit.idempotency_key.reused",
                    "The idempotency key was already used for a different allocation.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToResult(existing);
        }

        if (!await organizationEligibility
                .IsActiveRootAsync(intent.OrganizationId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new ValidationFailedException(
                "corporate_credit.organization.ineligible",
                "Corporate credit can only be allocated to an active root customer organization.");
        }

        var ledgerResult = await ledgerWriter
            .RecordCorporateCreditAsync(ledgerRequest, cancellationToken)
            .ConfigureAwait(false);
        var allocation = CorporateCreditAllocation.Create(
            ledgerRequest,
            ledgerResult,
            executionContext.UserId!.Value);
        dbContext.Allocations.Add(allocation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await auditRecorder.RecordAsync(
                new AuditEntry(
                    executionContext.UserId.Value,
                    AuditActorType.PlatformOperator,
                    allocation.OrganizationId,
                    AuditOperations.CorporateCreditAllocated,
                    nameof(CorporateCreditAllocation),
                    allocation.Id.ToString(),
                    AuditOutcome.Success,
                    executionContext.CorrelationId,
                    new Dictionary<string, string>
                    {
                        ["ledgerTransactionId"] = allocation.LedgerTransactionId.ToString(),
                        ["amount"] = allocation.Amount.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = allocation.Currency,
                        ["businessReference"] = allocation.BusinessReference,
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

        return ToResult(allocation);
    }

    private void RequirePlatformPermission()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException("auth.unauthenticated", "Authentication is required.");
        }

        if (!executionContext.HasPlatformPermission(PlatformPermissions.CorporateCreditsAllocate))
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

    private static CorporateCreditAllocationResult ToResult(CorporateCreditAllocation allocation) =>
        new(
            allocation.Id,
            allocation.OrganizationId,
            allocation.LedgerTransactionId,
            allocation.Amount,
            allocation.Currency,
            allocation.BusinessReference,
            allocation.IdempotencyKey,
            allocation.AllocatedAtUtc);
}
