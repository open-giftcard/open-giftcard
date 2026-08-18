using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Proves the organizations table is isolated by PostgreSQL Row-Level Security
/// (ADR-023), not by application code. Every test here talks to the database
/// directly with no application-level organization filter, so anything observed
/// is the policy doing the work.
///
/// The policy predicate is the tenant boundary — a caller sees the customer
/// hierarchy identified by <c>root_organization_id</c>. Which part of its own
/// tenant a caller may act on is authorization's concern (ADR-006), enforced
/// above this layer.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class OrganizationTenantIsolationTests(PlatformApiFixture fixture)
{
    // 42501 = insufficient_privilege, raised by an RLS WITH CHECK violation.
    private const string RlsViolation = "42501";

    /// <summary>Creates a root organization with one subsidiary beneath it.</summary>
    private async Task<(Guid Root, Guid Child)> CreateTenantAsync()
    {
        var root = await CreateOrganizationAsync(fixture);

        var client = OrganizationMember(fixture, root, OrganizationPermissions.CreateSubsidiary);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{root}/subsidiaries",
            new { name = "Isolation Subsidiary", code = "ISO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<SubsidiaryIdResponse>();
        return (root, body!.Id);
    }

    private sealed record SubsidiaryIdResponse(Guid Id);

    [Fact]
    public async Task A_tenant_sees_its_own_hierarchy_and_no_other_even_without_a_query_filter()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Root);

        // No WHERE clause: the application filter is deliberately absent.
        await using var command = session.Command("select id from organizations.organizations");

        var visible = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visible.Add(reader.GetGuid(0));
            }
        }

        // Its own root and descendant are visible...
        Assert.Contains(tenantA.Root, visible);
        Assert.Contains(tenantA.Child, visible);

        // ...and nothing belonging to the other customer is.
        Assert.DoesNotContain(tenantB.Root, visible);
        Assert.DoesNotContain(tenantB.Child, visible);
    }

    [Fact]
    public async Task A_subsidiary_scoped_caller_still_sees_the_whole_tenant_and_no_other()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        // Acting as the subsidiary rather than the root: the tenant boundary is
        // the root of the hierarchy, so the parent remains visible.
        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Child);
        await using var command = session.Command("select id from organizations.organizations");

        var visible = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visible.Add(reader.GetGuid(0));
            }
        }

        Assert.Contains(tenantA.Root, visible);
        Assert.Contains(tenantA.Child, visible);
        Assert.DoesNotContain(tenantB.Root, visible);
        Assert.DoesNotContain(tenantB.Child, visible);
    }

    [Fact]
    public async Task A_tenant_cannot_insert_an_organization_into_another_tenant()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Root);

        var id = Guid.CreateVersion7();
        await using var command = session.Command(
            """
            insert into organizations.organizations
                (id, name, code, status, parent_organization_id, root_organization_id,
                 hierarchy_path, depth, created_at_utc, created_by_user_id)
            values (@id, 'Injected', @code, 'Active', @parent, @root,
                    text2ltree('org_' || replace(@root::text,'-','') || '.org_' || replace(@id::text,'-','')),
                    1, now(), gen_random_uuid())
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("code", "INJ" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant());
        command.Parameters.AddWithValue("parent", tenantB.Root);
        command.Parameters.AddWithValue("root", tenantB.Root);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(RlsViolation, exception.SqlState);
    }

    [Fact]
    public async Task A_tenant_cannot_update_or_delete_another_tenants_organizations()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using (var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Root))
        {
            await using var update = session.Command(
                "update organizations.organizations set name = 'Hijacked' where id = @id");
            update.Parameters.AddWithValue("id", tenantB.Child);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());

            await using var delete = session.Command(
                "delete from organizations.organizations where id = @id");
            delete.Parameters.AddWithValue("id", tenantB.Child);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());

            await session.CommitAsync();
        }

        // The other tenant's row is untouched.
        await using var verify = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantB.Root);
        await using var command = verify.Command(
            "select name from organizations.organizations where id = @id");
        command.Parameters.AddWithValue("id", tenantB.Child);

        Assert.Equal("Isolation Subsidiary", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_platform_operator_reads_across_tenants()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command("select id from organizations.organizations");

        var visible = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            visible.Add(reader.GetGuid(0));
        }

        Assert.Contains(tenantA.Root, visible);
        Assert.Contains(tenantB.Root, visible);
    }

    [Fact]
    public async Task A_platform_operator_cannot_inject_a_subsidiary_into_a_customers_tree()
    {
        var tenant = await CreateTenantAsync();

        // The platform write path is restricted to rows with no parent, so a
        // platform operator can create customer organizations but cannot reach
        // inside one.
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);

        var id = Guid.CreateVersion7();
        await using var command = session.Command(
            """
            insert into organizations.organizations
                (id, name, code, status, parent_organization_id, root_organization_id,
                 hierarchy_path, depth, created_at_utc, created_by_user_id)
            values (@id, 'Platform Injected', @code, 'Active', @parent, @root,
                    text2ltree('org_' || replace(@root::text,'-','') || '.org_' || replace(@id::text,'-','')),
                    1, now(), gen_random_uuid())
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("code", "PINJ" + Guid.NewGuid().ToString("N")[..11].ToUpperInvariant());
        command.Parameters.AddWithValue("parent", tenant.Root);
        command.Parameters.AddWithValue("root", tenant.Root);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(RlsViolation, exception.SqlState);
    }

    [Fact]
    public async Task A_connection_with_no_session_context_sees_nothing()
    {
        await CreateTenantAsync();

        // RLS fails closed: absent context is not "trusted", it is "no tenant".
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from organizations.organizations", connection);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task The_runtime_role_is_still_subject_to_row_level_security()
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select r.rolbypassrls, r.rolsuper,
                   c.relrowsecurity
            from pg_roles r
            cross join pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where r.rolname = current_user
              and n.nspname = 'organizations'
              and c.relname = 'organizations'
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0), "The runtime role must not hold BYPASSRLS.");
        Assert.False(reader.GetBoolean(1), "The runtime role must not be a superuser.");
        Assert.True(reader.GetBoolean(2), "Row-level security must be enabled on the organizations table.");
    }
}
