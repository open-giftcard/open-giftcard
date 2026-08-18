using GiftCardPlatform.BuildingBlocks.Persistence;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Proves that PostgreSQL Row-Level Security — not the application query — is the
/// tenant-isolation barrier for the first tenant-owned table (ADR-005, ADR-020).
/// These tests deliberately talk to the database directly, omitting any
/// application-level organization filter, and set only the RLS session context.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class MembershipTenantIsolationTests(PlatformApiFixture fixture)
{
    // 42501 = insufficient_privilege, raised by an RLS WITH CHECK violation.
    private const string RlsViolation = "42501";

    [Fact]
    public async Task A_caller_scoped_to_one_organization_cannot_read_another_even_without_a_query_filter()
    {
        var organizationA = await CreateOrganizationAsync(fixture);
        var organizationB = await CreateOrganizationAsync(fixture);

        var membershipA = await CreateMembershipAsync(fixture, organizationA);
        var membershipB = await CreateMembershipAsync(fixture, organizationB);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationA, isPlatformOperator: false);

        // No WHERE clause: the application filter is deliberately absent, so any
        // isolation observed here is enforced by RLS alone.
        await using var command = new NpgsqlCommand(
            "select id, organization_id from organizations.organization_memberships",
            connection,
            transaction);

        var visibleIds = new List<Guid>();
        var visibleOrganizations = new HashSet<Guid>();

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visibleIds.Add(reader.GetGuid(0));
                visibleOrganizations.Add(reader.GetGuid(1));
            }
        }

        await transaction.RollbackAsync();

        Assert.Contains(membershipA.Id, visibleIds);
        Assert.DoesNotContain(membershipB.Id, visibleIds);
        Assert.All(visibleOrganizations, id => Assert.Equal(organizationA, id));
    }

    [Fact]
    public async Task A_caller_scoped_to_one_organization_cannot_insert_a_row_owned_by_another()
    {
        var organizationA = await CreateOrganizationAsync(fixture);
        var organizationB = await CreateOrganizationAsync(fixture);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, transaction, organizationA, isPlatformOperator: false);

        await using var command = new NpgsqlCommand(
            """
            insert into organizations.organization_memberships
                (id, organization_id, user_id, status, created_at_utc, disabled_at_utc)
            values (@id, @organization_id, @user_id, 'Active', now(), null)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("organization_id", organizationB);
        command.Parameters.AddWithValue("user_id", Guid.CreateVersion7());

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(RlsViolation, exception.SqlState);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_caller_scoped_to_one_organization_cannot_update_or_delete_anothers_rows()
    {
        var organizationA = await CreateOrganizationAsync(fixture);
        var organizationB = await CreateOrganizationAsync(fixture);
        var membershipB = await CreateMembershipAsync(fixture, organizationB);

        await using var connection = await fixture.OpenAppConnectionAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetSessionContextAsync(connection, transaction, organizationA, isPlatformOperator: false);

            await using var update = new NpgsqlCommand(
                "update organizations.organization_memberships set status = 'Disabled' where id = @id",
                connection,
                transaction);
            update.Parameters.AddWithValue("id", membershipB.Id);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());

            await using var delete = new NpgsqlCommand(
                "delete from organizations.organization_memberships where id = @id",
                connection,
                transaction);
            delete.Parameters.AddWithValue("id", membershipB.Id);
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());

            await transaction.CommitAsync();
        }

        // Under organization B's own context the row is untouched and still active.
        await using var verifyTransaction = await connection.BeginTransactionAsync();
        await SetSessionContextAsync(connection, verifyTransaction, organizationB, isPlatformOperator: false);

        await using var verify = new NpgsqlCommand(
            "select status from organizations.organization_memberships where id = @id",
            connection,
            verifyTransaction);
        verify.Parameters.AddWithValue("id", membershipB.Id);

        Assert.Equal("Active", (string)(await verify.ExecuteScalarAsync())!);
        await verifyTransaction.RollbackAsync();
    }

    [Fact]
    public async Task The_runtime_role_cannot_bypass_rls()
    {
        await using var connection = await fixture.OpenAppConnectionAsync();

        await using var command = new NpgsqlCommand(
            "select rolbypassrls, rolsuper from pg_roles where rolname = current_user",
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0), "The runtime role must not hold BYPASSRLS.");
        Assert.False(reader.GetBoolean(1), "The runtime role must not be a superuser.");
    }

    [Fact]
    public async Task Session_context_for_one_organization_does_not_leak_onto_a_pooled_connection()
    {
        var organizationA = await CreateOrganizationAsync(fixture);
        var organizationB = await CreateOrganizationAsync(fixture);

        var membershipA = await CreateMembershipAsync(fixture, organizationA);
        var membershipB = await CreateMembershipAsync(fixture, organizationB);

        // First caller sets organization A's context, then returns its connection
        // to the pool.
        await using (var first = new ScopedDatabaseConnection(fixture.AppConnectionString))
        {
            var connection = await first.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetSessionContextAsync(connection, transaction, organizationA, isPlatformOperator: false);
            await transaction.CommitAsync();
        }

        // Second caller opens a connection from the same pool. With no context of
        // its own, RLS must fail closed — no stale organization A rows appear.
        await using var second = new ScopedDatabaseConnection(fixture.AppConnectionString);
        var reused = await second.OpenAsync(CancellationToken.None);

        await using (var noContextTransaction = await reused.BeginTransactionAsync())
        {
            await using var count = new NpgsqlCommand(
                "select count(*) from organizations.organization_memberships",
                reused,
                noContextTransaction);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            await noContextTransaction.RollbackAsync();
        }

        // And when the second caller sets organization B's context, it sees only
        // organization B — never the first caller's organization A.
        await using var scopedTransaction = await reused.BeginTransactionAsync();
        await SetSessionContextAsync(reused, scopedTransaction, organizationB, isPlatformOperator: false);

        await using var scopedQuery = new NpgsqlCommand(
            "select id from organizations.organization_memberships",
            reused,
            scopedTransaction);

        var visible = new List<Guid>();
        await using (var reader = await scopedQuery.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visible.Add(reader.GetGuid(0));
            }
        }

        await scopedTransaction.RollbackAsync();

        Assert.Contains(membershipB.Id, visible);
        Assert.DoesNotContain(membershipA.Id, visible);
    }
}
