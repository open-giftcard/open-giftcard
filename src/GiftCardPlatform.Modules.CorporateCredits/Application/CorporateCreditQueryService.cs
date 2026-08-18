using System.Globalization;
using System.Text;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Contracts;
using GiftCardPlatform.Modules.CorporateCredits.Infrastructure;
using GiftCardPlatform.Modules.Ledger.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.CorporateCredits.Application;

internal sealed class CorporateCreditQueryService(
    CorporateCreditsDbContext dbContext,
    ILedgerBalanceQuery ledgerBalanceQuery,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : ICorporateCreditQueryService
{
    public async Task<IReadOnlyList<CorporateCreditBalanceResult>> GetBalancesAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        ValidateOrganization(organizationId);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await AuthorizeAsync(organizationId, cancellationToken).ConfigureAwait(false);

        var balances = await ledgerBalanceQuery
            .GetOrganizationCorporateCreditBalancesAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. balances.Select(
                balance => new CorporateCreditBalanceResult(balance.Currency, balance.Amount)),
        ];
    }

    public async Task<CorporateCreditHistoryPage> GetAllocationHistoryAsync(
        Guid organizationId,
        CorporateCreditHistoryRequest request,
        CancellationToken cancellationToken)
    {
        ValidateOrganization(organizationId);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Limit is < 1 or > CorporateCreditHistoryRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "corporate_credit.history.limit.invalid",
                $"Limit must be between 1 and {CorporateCreditHistoryRequest.MaxLimit}.");
        }

        var cursor = DecodeCursor(request.Cursor);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await AuthorizeAsync(organizationId, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var query = dbContext.Allocations
            .AsNoTracking()
            .Where(allocation => allocation.OrganizationId == organizationId);

        if (cursor is not null)
        {
            query = query.Where(allocation =>
                allocation.AllocatedAtUtc < cursor.AllocatedAtUtc ||
                (allocation.AllocatedAtUtc == cursor.AllocatedAtUtc &&
                 allocation.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(allocation => allocation.AllocatedAtUtc)
            .ThenByDescending(allocation => allocation.Id)
            .Take(request.Limit + 1)
            .Select(allocation => new CorporateCreditAllocationHistoryItem(
                allocation.Id,
                allocation.OrganizationId,
                allocation.LedgerTransactionId,
                allocation.Amount,
                allocation.Currency,
                allocation.BusinessReference,
                allocation.AllocatedByUserId,
                allocation.AllocatedAtUtc,
                dbContext.Reversals
                    .Where(reversal => reversal.AllocationId == allocation.Id)
                    .Select(reversal => new CorporateCreditReversalSummary(
                        reversal.Id,
                        reversal.LedgerTransactionId,
                        reversal.Reason,
                        reversal.ReversedByUserId,
                        reversal.ReversedAtUtc))
                    .SingleOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > request.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var nextCursor = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].AllocatedAtUtc, rows[^1].Id)
            : null;

        return new CorporateCreditHistoryPage(rows, request.Limit, nextCursor);
    }

    private async Task AuthorizeAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (executionContext.IsPlatformOperator)
        {
            if (!executionContext.IsAuthenticated ||
                executionContext.UserId is null ||
                !executionContext.HasPlatformPermission(PlatformPermissions.CorporateCreditsView))
            {
                throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
            }

            return;
        }

        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.CorporateCreditsView,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateOrganization(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "corporate_credit.organization.required",
                "An organization is required.");
        }
    }

    private static string EncodeCursor(DateTimeOffset allocatedAtUtc, Guid id)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{allocatedAtUtc.UtcDateTime.Ticks}:{id:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static HistoryCursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4),
                '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var parts = decoded.Split(':', StringSplitOptions.None);

            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id) ||
                id == Guid.Empty)
            {
                throw new FormatException("Invalid cursor payload.");
            }

            return new HistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ValidationFailedException(
                "corporate_credit.history.cursor.invalid",
                "The history cursor is invalid.");
        }
    }

    private sealed record HistoryCursor(DateTimeOffset AllocatedAtUtc, Guid Id);
}
