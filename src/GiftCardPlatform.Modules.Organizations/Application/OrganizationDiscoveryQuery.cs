using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Frontend-facing organization discovery without making browser state an
/// authorization boundary. PostgreSQL RLS remains active for every projection.
/// </summary>
internal sealed class OrganizationDiscoveryQuery(
    OrganizationsDbContext dbContext,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IPermissionEvaluator permissionEvaluator) : IOrganizationDiscoveryQuery
{
    private const int SearchMaxLength = Organization.NameMaxLength;

    public async Task<PagedResult<OrganizationResult>> ListPlatformOrganizationsAsync(
        OrganizationListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePlatformPermission(PlatformPermissions.OrganizationsView);

        var page = PageRequestValidator.Validate(request.Page);
        var search = NormalizeSearch(request.Search);
        var status = ParseStatus(request.Status);

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var query = dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.ParentOrganizationId == null);

        if (status is not null)
        {
            query = query.Where(x => x.Status == status);
        }

        if (search is not null)
        {
            var pattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.Name, pattern, "\\") ||
                EF.Functions.ILike(x.Code, pattern, "\\"));
        }

        var rows = await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Skip(page.Offset)
            .Take(page.Limit + 1)
            .Select(x => new OrganizationResult(
                x.Id,
                x.Name,
                x.Code,
                x.Status.ToString(),
                x.Depth,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToPage(rows, page);
    }

    public async Task<PagedResult<UserOrganizationResult>> ListCurrentUserOrganizationsAsync(
        PageRequest page,
        CancellationToken cancellationToken)
    {
        RequireAuthenticated();

        if (executionContext.ActiveOrganizationId is not null)
        {
            throw new ValidationFailedException(
                "organization.discovery.identity_context_required",
                "Organization discovery must be requested without a selected organization context.");
        }

        var requested = PageRequestValidator.Validate(page);
        var userId = executionContext.UserId!.Value;

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var rows = await (
                from membership in dbContext.Memberships.AsNoTracking()
                join organization in dbContext.Organizations.AsNoTracking()
                    on membership.OrganizationId equals organization.Id
                where membership.UserId == userId &&
                      membership.Status == OrganizationMembershipStatus.Active
                orderby organization.Name, organization.Id
                select new UserOrganizationProjection(
                    membership.Id,
                    organization.RootOrganizationId,
                    organization.Id,
                    organization.Name,
                    organization.Code,
                    organization.Status,
                    organization.Depth,
                    organization.CreatedAtUtc,
                    membership.CreatedAtUtc))
            .Skip(requested.Offset)
            .Take(requested.Limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var results = rows
            .Select(x => new UserOrganizationResult(
                x.MembershipId,
                x.TenantRootOrganizationId,
                new OrganizationResult(
                    x.OrganizationId,
                    x.Name,
                    x.Code,
                    x.Status.ToString(),
                    x.Depth,
                    x.OrganizationCreatedAtUtc),
                x.MembershipCreatedAtUtc))
            .ToList();

        return ToPage(results, requested);
    }

    public async Task<SelectedOrganizationContextResult> GetSelectedOrganizationContextAsync(
        CancellationToken cancellationToken)
    {
        RequireAuthenticated();

        if (executionContext.ActiveMembershipId is not { } membershipId ||
            executionContext.ActiveOrganizationId is not { } organizationId ||
            executionContext.TenantRootOrganizationId is not { } tenantRootOrganizationId)
        {
            throw new ValidationFailedException(
                "organization.context.not_selected",
                "A verified organization context is required.");
        }

        await using var transaction = await transactionCoordinator
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var organization = await dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.Id == organizationId)
            .Select(x => new OrganizationResult(
                x.Id,
                x.Name,
                x.Code,
                x.Status.ToString(),
                x.Depth,
                x.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(
                "organization.not_found",
                "Organization not found.");

        var permissions = await permissionEvaluator
            .GetEffectivePermissionsAsync(
                membershipId,
                organizationId,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SelectedOrganizationContextResult(
            membershipId,
            tenantRootOrganizationId,
            organization,
            permissions.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    private static string? NormalizeSearch(string? search)
    {
        var normalized = search?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > SearchMaxLength)
        {
            throw new ValidationFailedException(
                "organization.search.too_long",
                $"Search must not exceed {SearchMaxLength} characters.");
        }

        return normalized;
    }

    private static OrganizationStatus? ParseStatus(string? status)
    {
        var normalized = status?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (!Enum.TryParse<OrganizationStatus>(
                normalized,
                ignoreCase: true,
                out var parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw new ValidationFailedException(
                "organization.status.invalid",
                "Status must be Active, Suspended, or Disabled.");
        }

        return parsed;
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private void RequirePlatformPermission(string permission)
    {
        RequireAuthenticated();

        if (!executionContext.HasPlatformPermission(permission))
        {
            throw new ForbiddenException(
                "auth.forbidden",
                "The required permission is missing.");
        }
    }

    private void RequireAuthenticated()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new UnauthorizedException(
                "auth.unauthenticated",
                "Authentication is required.");
        }
    }

    private static PagedResult<T> ToPage<T>(List<T> rows, PageRequest page)
    {
        var hasMore = rows.Count > page.Limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new PagedResult<T>(rows, page.Limit, page.Offset, hasMore);
    }

    private sealed record UserOrganizationProjection(
        Guid MembershipId,
        Guid TenantRootOrganizationId,
        Guid OrganizationId,
        string Name,
        string Code,
        OrganizationStatus Status,
        int Depth,
        DateTimeOffset OrganizationCreatedAtUtc,
        DateTimeOffset MembershipCreatedAtUtc);
}
