using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using GiftCardPlatform.Modules.Organizations.Domain;
using GiftCardPlatform.Modules.Organizations.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Application behaviour for organization memberships, the first tenant-owned
/// records. Authorization and tenant scope are enforced here, below the controller
/// layer, and again by the PostgreSQL RLS policy so a missed check cannot leak
/// across tenants (ADR-005, ADR-020).
/// </summary>
internal sealed class MembershipService(
    OrganizationsDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    IOrganizationPermissionAuthorizer permissionAuthorizer,
    TimeProvider timeProvider) : IMembershipService
{
    private const string UniqueViolation = "23505";

    public async Task<MembershipResult> CreateMembershipAsync(
        Guid organizationId,
        CreateMembershipRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.MembershipsCreate,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var membership = OrganizationMembership.Create(
            organizationId,
            request.UserId,
            timeProvider.GetUtcNow());

        var alreadyExists = await dbContext.Memberships
            .AsNoTracking()
            .AnyAsync(x => x.UserId == membership.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyExists)
        {
            throw new ConflictException(
                "membership.duplicate",
                "The user already has a membership in this organization.");
        }

        dbContext.Memberships.Add(membership);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Backstop for a concurrent insert that raced the pre-check.
            throw new ConflictException(
                "membership.duplicate",
                "The user already has a membership in this organization.");
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: executionContext.UserId!.Value,
                ActorType: AuditActorType.OrganizationMember,
                ActorMembershipId: executionContext.ActiveMembershipId,
                OrganizationScopeId: membership.OrganizationId,
                Operation: AuditOperations.MembershipCreated,
                EntityType: nameof(OrganizationMembership),
                EntityId: membership.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["user_id"] = membership.UserId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToResult(membership);
    }

    public async Task<PagedResult<MembershipResult>> ListMembershipsAsync(
        Guid organizationId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        var requested = PageRequestValidator.Validate(page);

        // Reads run inside a module transaction so the PostgreSQL session context
        // (and therefore RLS) is established before the query executes (ADR-020).
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);

        if (executionContext.IsPlatformOperator)
        {
            RequirePlatformMembershipRead();
        }
        else
        {
            await permissionAuthorizer.RequirePermissionAsync(
                organizationId,
                OrganizationPermissions.MembershipsView,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var memberships = await dbContext.Memberships
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            // Id breaks ties so paging is deterministic when timestamps collide.
            .OrderBy(x => x.CreatedAtUtc)
            .ThenBy(x => x.Id)
            .Skip(requested.Offset)
            // One beyond the page, to learn whether more exist without a COUNT.
            .Take(requested.Limit + 1)
            .Select(x => new MembershipResult(
                x.Id,
                x.OrganizationId,
                x.UserId,
                x.Status.ToString(),
                x.CreatedAtUtc,
                x.DisabledAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToPage(memberships, requested);
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

    public async Task<MembershipResult> DisableMembershipAsync(
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await permissionAuthorizer.RequirePermissionAsync(
            organizationId,
            OrganizationPermissions.MembershipsDisable,
            cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // RLS and the query filter constrain this to the caller's organization, so
        // a membership owned by another tenant is simply not found.
        var membership = await dbContext.Memberships
            .SingleOrDefaultAsync(x => x.Id == membershipId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("membership.not_found", "Membership not found.");

        membership.Disable(timeProvider.GetUtcNow());

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer changed the row between the read and the write.
            // Surfaced rather than retried: the caller should re-read and decide.
            throw new ConflictException(
                "membership.concurrent_modification",
                "The membership was modified concurrently. Retry the operation.");
        }

        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: executionContext.UserId!.Value,
                ActorType: AuditActorType.OrganizationMember,
                ActorMembershipId: executionContext.ActiveMembershipId,
                OrganizationScopeId: membership.OrganizationId,
                Operation: AuditOperations.MembershipDisabled,
                EntityType: nameof(OrganizationMembership),
                EntityId: membership.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["user_id"] = membership.UserId.ToString(),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToResult(membership);
    }

    private void RequirePlatformMembershipRead()
    {
        RequireAuthenticated();

        if (!executionContext.IsPlatformOperator ||
            !executionContext.HasPlatformPermission(PlatformPermissions.MembershipsView))
        {
            throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
        }
    }

    private void RequireAuthenticated()
    {
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException("auth.unauthenticated", "Authentication is required.");
        }
    }

    private static MembershipResult ToResult(OrganizationMembership membership) => new(
        membership.Id,
        membership.OrganizationId,
        membership.UserId,
        membership.Status.ToString(),
        membership.CreatedAtUtc,
        membership.DisabledAtUtc);
}
