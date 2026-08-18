using System.Globalization;
using System.Text;
using System.Text.Json;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Audit.Domain;
using GiftCardPlatform.Modules.Audit.Infrastructure;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Audit.Application;

internal sealed class AuditInvestigationQuery(
    AuditDbContext dbContext,
    IOrganizationPermissionAuthorizer organizationAuthorizer,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext) : IAuditInvestigationQuery
{
    private const string CursorVersion = "v1";

    public async Task<AuditInvestigationPage> GetAsync(
        Guid organizationId,
        AuditInvestigationRequest request,
        CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "audit.organization.required",
                "An organization is required.");
        }

        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > AuditInvestigationRequest.MaxLimit)
        {
            throw new ValidationFailedException(
                "audit.investigation.limit.invalid",
                $"Limit must be between 1 and {AuditInvestigationRequest.MaxLimit}.");
        }

        var operation = NormalizeOperation(request.Operation);
        if (request.CorrelationId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "audit.investigation.correlation.invalid",
                "A correlation identifier cannot be empty.");
        }

        if (request.Outcome is { } outcome &&
            !Enum.IsDefined(outcome))
        {
            throw new ValidationFailedException(
                "audit.investigation.outcome.invalid",
                "The audit outcome filter is invalid.");
        }

        var cursor = DecodeCursor(request.Cursor);
        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await AuthorizeAsync(organizationId, cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var query = dbContext.AuditRecords
            .AsNoTracking()
            .Where(record => record.OrganizationScopeId == organizationId);
        if (operation is not null)
        {
            query = query.Where(record => record.Operation == operation);
        }

        if (request.Outcome is not null)
        {
            query = query.Where(record => record.Outcome == request.Outcome);
        }

        if (request.CorrelationId is not null)
        {
            query = query.Where(
                record => record.CorrelationId == request.CorrelationId);
        }

        if (cursor is not null)
        {
            query = query.Where(record =>
                record.OccurredAtUtc < cursor.OccurredAtUtc ||
                (record.OccurredAtUtc == cursor.OccurredAtUtc &&
                 record.Id.CompareTo(cursor.Id) < 0));
        }

        var records = await query
            .OrderByDescending(record => record.OccurredAtUtc)
            .ThenByDescending(record => record.Id)
            .Take(request.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = records.Count > request.Limit;
        if (hasMore)
        {
            records.RemoveAt(records.Count - 1);
        }

        var nextCursor = hasMore && records.Count > 0
            ? EncodeCursor(records[^1].OccurredAtUtc, records[^1].Id)
            : null;
        return new AuditInvestigationPage(
            [.. records.Select(ToResult)],
            request.Limit,
            nextCursor);
    }

    private async Task AuthorizeAsync(
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        if (executionContext.IsPlatformOperator &&
            !executionContext.IsSystem)
        {
            if (!executionContext.HasPlatformPermission(
                    PlatformPermissions.AuditView))
            {
                throw new ForbiddenException(
                    "audit.platform_permission.required",
                    $"Permission '{PlatformPermissions.AuditView}' is required.");
            }

            return;
        }

        await organizationAuthorizer
            .RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.AuditView,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static AuditInvestigationItem ToResult(AuditRecord record) =>
        new(
            record.Id,
            record.ActorUserId,
            record.ActorType,
            record.ActorMembershipId,
            record.OrganizationScopeId,
            record.Operation,
            record.EntityType,
            record.EntityId,
            record.Outcome,
            record.CorrelationId,
            record.OccurredAtUtc,
            ParseMetadata(record.MetadataJson));

    private static Dictionary<string, string> ParseMetadata(
        string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new Dictionary<string, string>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)
            ?? new Dictionary<string, string>();
    }

    private static string? NormalizeOperation(string? operation)
    {
        var normalized = operation?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > 128)
        {
            throw new ValidationFailedException(
                "audit.investigation.operation.invalid",
                "The audit operation filter cannot exceed 128 characters.");
        }

        return normalized;
    }

    private static string EncodeCursor(DateTimeOffset occurredAtUtc, Guid id)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{CursorVersion}|{occurredAtUtc.UtcDateTime.Ticks}|{id:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static AuditCursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var encoded = value.Trim();
            if (encoded.Length > 256)
            {
                throw new FormatException("Cursor is too long.");
            }

            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + ((4 - (normalized.Length % 4)) % 4),
                '=');
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(normalized));
            var parts = decoded.Split('|', StringSplitOptions.None);
            if (parts.Length != 3 ||
                parts[0] != CursorVersion ||
                !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                !Guid.TryParseExact(parts[2], "N", out var id) ||
                id == Guid.Empty)
            {
                throw new FormatException("Cursor payload is invalid.");
            }

            return new AuditCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                id);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ValidationFailedException(
                "audit.investigation.cursor.invalid",
                "The audit cursor is invalid.");
        }
    }

    private sealed record AuditCursor(DateTimeOffset OccurredAtUtc, Guid Id);
}
