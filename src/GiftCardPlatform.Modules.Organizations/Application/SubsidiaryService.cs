using System.Globalization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Application behaviour for the customer organization hierarchy. The target
/// parent must belong to the caller's tenant and be covered by the verified
/// active membership's explicit permission scope. PostgreSQL RLS independently
/// enforces the tenant-root boundary (ADR-023, ADR-031).
/// </summary>
internal sealed class SubsidiaryService(
    OrganizationsDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOrganizationPermissionAuthorizer permissionAuthorizer,
    IOptions<OrganizationHierarchyOptions> hierarchyOptions,
    TimeProvider timeProvider) : ISubsidiaryService
{
    private const string UniqueViolation = "23505";

    public async Task<SubsidiaryResult> CreateSubsidiaryAsync(
        Guid parentOrganizationId,
        CreateSubsidiaryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            parentOrganizationId,
            OrganizationPermissions.CreateSubsidiary,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Permission scope and RLS have already admitted this exact target.
        var parent = await dbContext.Organizations
            .SingleOrDefaultAsync(x => x.Id == parentOrganizationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("organization.not_found", "Organization not found.");

        // Validates the parent state, name, and code, and computes depth and the
        // ltree path one level below the parent.
        var subsidiary = Organization.CreateSubsidiary(
            parent,
            request.Name,
            request.Code,
            executionContext.UserId!.Value,
            timeProvider.GetUtcNow(),
            hierarchyOptions.Value.MaxDepth);

        // Scoped to the caller's own tenant (ADR-024). A global check here would
        // let a customer discover another customer's codes by provoking a
        // conflict, and would let the first customer to claim a common code deny
        // it to everyone else.
        var codeTaken = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(
                x => x.Code == subsidiary.Code &&
                     x.RootOrganizationId == subsidiary.RootOrganizationId &&
                     x.ParentOrganizationId != null,
                cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            throw new ConflictException(
                "organization.code.duplicate",
                "An organization with this code already exists in your organization.");
        }

        dbContext.Organizations.Add(subsidiary);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Backstop for a concurrent insert that raced the pre-check.
            throw new ConflictException(
                "organization.code.duplicate",
                "An organization with this code already exists.");
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: executionContext.UserId!.Value,
                ActorType: AuditActorType.OrganizationMember,
                ActorMembershipId: executionContext.ActiveMembershipId,
                // The acting tenant scope is the parent organization.
                OrganizationScopeId: parent.Id,
                Operation: AuditOperations.SubsidiaryCreated,
                EntityType: nameof(Organization),
                EntityId: subsidiary.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["code"] = subsidiary.Code,
                    ["name"] = subsidiary.Name,
                    ["parent_organization_id"] = parent.Id.ToString(),
                    ["depth"] = subsidiary.Depth.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToResult(subsidiary, parent.Id);
    }

    public async Task<PagedResult<SubsidiaryResult>> ListSubsidiariesAsync(
        Guid parentOrganizationId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var requested = PageRequestValidator.Validate(page);

        // Reads also run inside a module transaction so the PostgreSQL session
        // context is established before the query executes (ADR-020).
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            parentOrganizationId,
            OrganizationPermissions.View,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var subsidiaries = await dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.ParentOrganizationId == parentOrganizationId)
            // Id breaks ties so paging is deterministic when timestamps collide.
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .Skip(requested.Offset)
            // One beyond the page, to learn whether more exist without a COUNT.
            .Take(requested.Limit + 1)
            .Select(x => new SubsidiaryResult(
                x.Id,
                x.ParentOrganizationId!.Value,
                x.Name,
                x.Code,
                x.Status.ToString(),
                x.Depth,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = subsidiaries.Count > requested.Limit;

        if (hasMore)
        {
            subsidiaries.RemoveAt(subsidiaries.Count - 1);
        }

        return new PagedResult<SubsidiaryResult>(subsidiaries, requested.Limit, requested.Offset, hasMore);
    }

    private static SubsidiaryResult ToResult(Organization subsidiary, Guid parentOrganizationId) => new(
        subsidiary.Id,
        parentOrganizationId,
        subsidiary.Name,
        subsidiary.Code,
        subsidiary.Status.ToString(),
        subsidiary.Depth,
        subsidiary.CreatedAtUtc);
}
