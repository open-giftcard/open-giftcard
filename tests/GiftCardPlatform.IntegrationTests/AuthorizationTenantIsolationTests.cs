using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.AuthorizationTestSupport;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Proves the authorization tables are isolated by PostgreSQL Row-Level Security,
/// not by application code. Every query here omits any organization filter, so
/// what is observed is the policy doing the work.
///
/// These tables decide who may do what, so a leak here is worse than a leak of
/// business data: it is the map of the security model.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class AuthorizationTenantIsolationTests(PlatformApiFixture fixture)
{
    // 42501 = insufficient_privilege, raised by an RLS WITH CHECK violation.
    private const string RlsViolation = "42501";

    /// <summary>An organization with a role, a granted permission, and an assignment.</summary>
    private async Task<(Guid Organization, Guid RoleId, Guid AssignmentId)> CreateTenantAsync()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var membership = await CreateMembershipAsync(fixture, organizationId);
        var role = await CreateRoleAsync(fixture, organizationId, OrganizationPermissions.MembershipsView);

        var assignment = await AssignRoleAsync(
            fixture, organizationId, membership.Id, role.Id, RoleScope.Organization, organizationId);

        return (organizationId, role.Id, assignment.Id);
    }

    private static async Task<List<Guid>> ReadIdsAsync(ScopedSqlSession session, string table)
    {
        // No WHERE clause: the application filter is deliberately absent.
        await using var command = session.Command($"""select id from "authorization".{table}""");

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_roles_or_assignments()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Organization);

        var roles = await ReadIdsAsync(session, "roles");
        Assert.Contains(tenantA.RoleId, roles);
        Assert.DoesNotContain(tenantB.RoleId, roles);

        var assignments = await ReadIdsAsync(session, "membership_role_assignments");
        Assert.Contains(tenantA.AssignmentId, assignments);
        Assert.DoesNotContain(tenantB.AssignmentId, assignments);
    }

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_permission_grants()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Organization);

        await using var command = session.Command(
            """select distinct organization_id from "authorization".role_permissions""");

        var organizations = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                organizations.Add(reader.GetGuid(0));
            }
        }

        Assert.All(organizations, id => Assert.Equal(tenantA.Organization, id));
        Assert.DoesNotContain(tenantB.Organization, organizations);
    }

    [Fact]
    public async Task A_tenant_cannot_insert_a_role_owned_by_another()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Organization);

        await using var command = session.Command(
            """
            insert into "authorization".roles (id, organization_id, name, created_at_utc, created_by_user_id)
            values (@id, @organization_id, @name, now(), gen_random_uuid())
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization_id", tenantB.Organization);
        command.Parameters.AddWithValue("name", UniqueRoleName("INJECT"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(RlsViolation, exception.SqlState);
    }

    [Fact]
    public async Task A_tenant_cannot_update_or_delete_another_tenants_roles()
    {
        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        await using (var session = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantA.Organization))
        {
            await using var update = session.Command(
                """update "authorization".roles set name = 'hijacked' where id = @id""");
            update.Parameters.AddWithValue("id", tenantB.RoleId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());

            await using var delete = session.Command(
                """delete from "authorization".roles where id = @id""");
            delete.Parameters.AddWithValue("id", tenantB.RoleId);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());

            await session.CommitAsync();
        }

        // Untouched under its owner's context.
        await using var verify = await ScopedSqlSession.OpenAsOrganizationAsync(fixture, tenantB.Organization);
        await using var command = verify.Command(
            """select count(*) from "authorization".roles where id = @id and name <> 'hijacked'""");
        command.Parameters.AddWithValue("id", tenantB.RoleId);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_platform_operator_may_read_but_never_write_authorization_rows()
    {
        var tenant = await CreateTenantAsync();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);

        // Read across tenants: support needs to see the security model.
        Assert.Contains(tenant.RoleId, await ReadIdsAsync(session, "roles"));

        // But writing one would be a privilege-escalation path, so WITH CHECK
        // omits the platform branch entirely.
        await using var command = session.Command(
            """
            insert into "authorization".roles (id, organization_id, name, created_at_utc, created_by_user_id)
            values (@id, @organization_id, @name, now(), gen_random_uuid())
            """);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization_id", tenant.Organization);
        command.Parameters.AddWithValue("name", UniqueRoleName("PLATFORM"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(RlsViolation, exception.SqlState);
    }

    [Fact]
    public async Task A_connection_with_no_session_context_sees_no_authorization_rows()
    {
        await CreateTenantAsync();

        // RLS fails closed: absent context is "no tenant", not "trusted".
        await using var connection = await fixture.OpenAppConnectionAsync();

        foreach (var table in new[]
                 {
                     "roles",
                     "role_permissions",
                     "membership_role_assignments",
                     "membership_role_assignment_scopes",
                 })
        {
            await using var command = new NpgsqlCommand(
                $"""select count(*) from "authorization".{table}""", connection);

            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task The_permission_catalogue_is_global_and_readable_without_tenant_context()
    {
        // Permission definitions are a global category (ADR-005): no
        // organization_id, no RLS, readable by anyone who can connect.
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """select count(*) from "authorization".permissions where name = @name""", connection);
        command.Parameters.AddWithValue("name", OrganizationPermissions.MembershipsView);

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Row_level_security_is_forced_on_every_tenant_owned_authorization_table()
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select c.relname, c.relrowsecurity, c.relforcerowsecurity
            from pg_class c
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'authorization'
              and c.relname in ('roles','role_permissions','membership_role_assignments',
                                'membership_role_assignment_scopes')
            order by c.relname
            """,
            connection);

        var checkedTables = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Assert.True(reader.GetBoolean(1), $"{reader.GetString(0)} must have RLS enabled.");
            Assert.True(reader.GetBoolean(2), $"{reader.GetString(0)} must have RLS forced.");
            checkedTables++;
        }

        Assert.Equal(4, checkedTables);
    }
}
