using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// PostgreSQL audit isolation is a tenant-root boundary. Permission checks
/// control application APIs; RLS independently prevents a missing query filter
/// from exposing another customer's security and financial history.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class AuditTenantIsolationTests(PlatformApiFixture fixture)
{
    [Fact]
    public async Task Audit_row_level_security_is_enabled_and_forced()
    {
        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select relrowsecurity, relforcerowsecurity
            from pg_class
            where oid = 'audit.audit_records'::regclass
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
    }

    [Fact]
    public async Task A_connection_without_context_sees_no_audit_rows()
    {
        _ = await CreateOrganizationAsync(fixture);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from audit.audit_records",
            connection);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_tenant_sees_its_own_audit_history_and_not_another_tenants()
    {
        var ownOrganizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);

        await using var ownSession =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, ownOrganizationId);
        await using var command = ownSession.Command(
            """
            select
                count(*) filter (where entity_id = @own_id),
                count(*) filter (where entity_id = @other_id)
            from audit.audit_records
            """);
        command.Parameters.AddWithValue("own_id", ownOrganizationId.ToString());
        command.Parameters.AddWithValue("other_id", otherOrganizationId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public async Task Controlled_platform_context_reads_across_tenants()
    {
        var first = await CreateOrganizationAsync(fixture);
        var second = await CreateOrganizationAsync(fixture);

        await using var platformSession =
            await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = platformSession.Command(
            """
            select count(*)
            from audit.audit_records
            where entity_id in (@first, @second)
            """);
        command.Parameters.AddWithValue("first", first.ToString());
        command.Parameters.AddWithValue("second", second.ToString());

        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Identity_context_sees_only_its_own_global_audit_rows()
    {
        var ownUserId = Guid.CreateVersion7();
        var otherUserId = Guid.CreateVersion7();
        var operation = "test.identity.audit." + Guid.NewGuid().ToString("N");

        await using (var platformSession =
                     await ScopedSqlSession.OpenAsPlatformAsync(fixture))
        {
            foreach (var actorUserId in new[] { ownUserId, otherUserId })
            {
                await using var insert = platformSession.Command(
                    """
                    insert into audit.audit_records (
                        id, actor_user_id, actor_type, actor_membership_id,
                        organization_scope_id, operation, entity_type, entity_id,
                        outcome, correlation_id, occurred_at_utc, metadata)
                    values (
                        @id, @actor, 'IdentityUser', null, null, @operation,
                        'IdentityProbe', @entity_id, 'Success', @correlation,
                        now(), null)
                    """);
                insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
                insert.Parameters.AddWithValue("actor", actorUserId);
                insert.Parameters.AddWithValue("operation", operation);
                insert.Parameters.AddWithValue("entity_id", actorUserId.ToString());
                insert.Parameters.AddWithValue("correlation", Guid.CreateVersion7());
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await platformSession.CommitAsync();
        }

        await using var identitySession =
            await ScopedSqlSession.OpenAsIdentityAsync(fixture, ownUserId);
        await using var command = identitySession.Command(
            """
            select
                count(*) filter (where actor_user_id = @own),
                count(*) filter (where actor_user_id = @other)
            from audit.audit_records
            where operation = @operation
            """);
        command.Parameters.AddWithValue("own", ownUserId);
        command.Parameters.AddWithValue("other", otherUserId);
        command.Parameters.AddWithValue("operation", operation);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }
}
