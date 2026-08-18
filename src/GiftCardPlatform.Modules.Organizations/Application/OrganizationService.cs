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
using Npgsql;

namespace GiftCardPlatform.Modules.Organizations.Application;

/// <summary>
/// Application behaviour for organizations. Authorization is enforced here,
/// below the controller layer, so it stays valid when a handler is invoked
/// outside HTTP.
/// </summary>
internal sealed class OrganizationService(
    OrganizationsDbContext dbContext,
    IAuditRecorder auditRecorder,
    ITransactionCoordinator transactionCoordinator,
    IExecutionContext executionContext,
    TimeProvider timeProvider) : IOrganizationService
{
    private const string UniqueViolation = "23505";

    public async Task<OrganizationResult> CreateRootOrganizationAsync(
        CreateRootOrganizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequirePlatformPermission(PlatformPermissions.OrganizationsCreate);

        // Validates name and code, and assigns the UUID v7 identity and ltree path.
        var organization = Organization.CreateRoot(
            request.Name,
            request.Code,
            executionContext.UserId!.Value,
            timeProvider.GetUtcNow());

        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        // Root codes share one platform-wide namespace (ADR-024), so this check is
        // deliberately global — but restricted to roots, so it cannot reveal any
        // customer's subsidiary codes.
        var codeTaken = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(x => x.Code == organization.Code && x.ParentOrganizationId == null, cancellationToken)
            .ConfigureAwait(false);

        if (codeTaken)
        {
            throw new ConflictException(
                "organization.code.duplicate",
                "An organization with this code already exists.");
        }

        dbContext.Organizations.Add(organization);

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

        // Recorded through the Audit module's public contract, inside this same
        // transaction. If it throws, disposal rolls the organization back too.
        await auditRecorder.RecordAsync(
            new AuditEntry(
                ActorUserId: executionContext.UserId!.Value,
                ActorType: AuditActorType.PlatformOperator,
                OrganizationScopeId: organization.Id,
                Operation: AuditOperations.OrganizationCreated,
                EntityType: nameof(Organization),
                EntityId: organization.Id.ToString(),
                Outcome: AuditOutcome.Success,
                CorrelationId: executionContext.CorrelationId,
                Metadata: new Dictionary<string, string>
                {
                    ["code"] = organization.Code,
                    ["name"] = organization.Name,
                    ["depth"] = organization.Depth.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToResult(organization);
    }

    public async Task<OrganizationResult> GetOrganizationAsync(Guid id, CancellationToken cancellationToken)
    {
        RequirePlatformPermission(PlatformPermissions.OrganizationsView);

        // Reads also run inside a module transaction so the PostgreSQL session
        // context is established before the query executes (ADR-020).
        await using var transaction = await transactionCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EnlistAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var organization = await dbContext.Organizations
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new OrganizationResult(
                x.Id,
                x.Name,
                x.Code,
                x.Status.ToString(),
                x.Depth,
                x.CreatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return organization
            ?? throw new NotFoundException("organization.not_found", "Organization not found.");
    }

    private void RequirePlatformPermission(string permission)
    {
        // Checked by named permission, never by a bare IsPlatformOperator flag
        // or a role-name comparison.
        if (!executionContext.IsAuthenticated || executionContext.UserId is null)
        {
            throw new ForbiddenException("auth.unauthenticated", "Authentication is required.");
        }

        if (!executionContext.HasPlatformPermission(permission))
        {
            // Deliberately does not reveal whether the target resource exists.
            throw new ForbiddenException("auth.forbidden", "The required permission is missing.");
        }
    }

    private static OrganizationResult ToResult(Organization organization) => new(
        organization.Id,
        organization.Name,
        organization.Code,
        organization.Status.ToString(),
        organization.Depth,
        organization.CreatedAtUtc);
}
