using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Organizations.Domain;
using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.Modules.Organizations.Infrastructure;

/// <summary>
/// Owns the <c>organizations</c> schema and its migrations only (ADR-004).
/// </summary>
internal sealed class OrganizationsDbContext(
    DbContextOptions<OrganizationsDbContext> options,
    IExecutionContext executionContext) : DbContext(options)
{
    public const string Schema = "organizations";
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMembership> Memberships => Set<OrganizationMembership>();

    // Context-rooted accessors referenced by the query filter. EF Core re-evaluates
    // members rooted on the DbContext instance per query, against the current
    // scoped instance, so the filter never captures a stale execution context.
    private bool CallerIsPlatformOperator => executionContext.IsPlatformOperator;
    private bool CallerHasIdentityOnlyContext =>
        executionContext.IsAuthenticated &&
        executionContext.UserId is not null &&
        executionContext.ActiveOrganizationId is null;
    private Guid? CallerUserId => executionContext.UserId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("ltree");
        modelBuilder.AddTenantDbFunctions();
        modelBuilder.ApplyConfiguration(new OrganizationConfiguration());
        modelBuilder.ApplyConfiguration(new OrganizationMembershipConfiguration());

        // Defense-in-depth tenant filter (ADR-005). The authoritative barrier is
        // the PostgreSQL RLS policy on this table; this filter simply keeps the
        // application query honest. A platform operator reads across tenants
        // through the controlled RLS path, so the filter must not hide their rows.
        modelBuilder.Entity<OrganizationMembership>().HasQueryFilter(m =>
            CallerIsPlatformOperator ||
            (CallerHasIdentityOnlyContext && m.UserId == CallerUserId) ||
            TenantDbFunctions.OrganizationBelongsToCallerTenant(m.OrganizationId));
    }
}
