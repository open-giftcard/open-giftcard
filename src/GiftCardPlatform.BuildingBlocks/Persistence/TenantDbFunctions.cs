using Microsoft.EntityFrameworkCore;

namespace GiftCardPlatform.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL tenant-boundary functions that may be used by EF Core query
/// filters without making a module join the Organizations implementation.
/// Authorization scope remains an application concern; this function answers
/// only whether an organization belongs to the caller's customer tenant.
/// </summary>
public static class TenantDbFunctions
{
    [DbFunction(
        "organization_belongs_to_caller_tenant",
        Schema = "organizations",
        IsBuiltIn = false,
        IsNullable = false)]
    public static bool OrganizationBelongsToCallerTenant(Guid organizationId) =>
        throw new InvalidOperationException(
            "Tenant database functions may only be evaluated by PostgreSQL.");

    /// <summary>
    /// Registers the function explicitly because it lives in BuildingBlocks
    /// rather than on an individual module DbContext.
    /// </summary>
    public static ModelBuilder AddTenantDbFunctions(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var method = typeof(TenantDbFunctions).GetMethod(
            nameof(OrganizationBelongsToCallerTenant),
            [typeof(Guid)])!;

        modelBuilder
            .HasDbFunction(method)
            .HasName("organization_belongs_to_caller_tenant")
            .HasSchema("organizations");

        return modelBuilder;
    }
}
