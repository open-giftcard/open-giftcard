using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Proves the organization write and its audit write commit as one unit.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class AtomicAuditTests(PlatformApiFixture fixture)
{
    // Random (v4): a UUID v7's leading hex is a timestamp and collides per millisecond.
    private static string UniqueCode() => "ATOM" + Guid.NewGuid().ToString("N")[..11].ToUpperInvariant();

    /// <summary>
    /// Wraps the real recorder so the audit row genuinely joins the transaction
    /// and is then abandoned by a failure. This decorates the production
    /// implementation rather than weakening it: the failure hook exists only in
    /// this test project and is never registered by the application.
    /// </summary>
    private sealed class FailAfterWritingAuditRecorder(IAuditRecorder inner) : IAuditRecorder
    {
        public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            await inner.RecordAsync(entry, cancellationToken);
            throw new InvalidOperationException("Simulated audit failure after the audit row was written.");
        }

        // The independent path is deliberately left working: only the atomic
        // write is being made to fail.
        public Task RecordIndependentlyAsync(AuditEntry entry, CancellationToken cancellationToken) =>
            inner.RecordIndependentlyAsync(entry, cancellationToken);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_organization()
    {
        var code = UniqueCode();

        using var factory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.ConfigureServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAuditRecorder));
                services.Remove(original);

                services.Add(ServiceDescriptor.Describe(
                    typeof(IAuditRecorder),
                    sp =>
                    {
                        // Build the real recorder, then wrap it.
                        var inner = (IAuditRecorder)ActivatorUtilities.CreateInstance(
                            sp, original.ImplementationType!);

                        return new FailAfterWritingAuditRecorder(inner);
                    },
                    original.Lifetime));
            }));

        var userId = Guid.CreateVersion7();
        await MembershipTestSupport.ProvisionPlatformActorAsync(
            fixture,
            userId,
            [PlatformPermissions.OrganizationsCreate]);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Rolled Back Company", code });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // Neither write may survive. The organizations table is behind RLS, so
        // the check runs as a platform operator.
        await using (var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture))
        {
            Assert.Equal(0L, await session.ScalarCountAsync(
                "select count(*) from organizations.organizations where code = @code",
                command => command.Parameters.AddWithValue("code", code)));
        }

        await using var auditSession = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var auditCommand = auditSession.Command(
            "select count(*) from audit.audit_records where metadata->>'code' = @code");
        auditCommand.Parameters.AddWithValue("code", code);
        Assert.Equal(0L, (long)(await auditCommand.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Successful_creation_commits_both_rows()
    {
        var client = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsCreate);

        var code = UniqueCode();
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Committed Company", code });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select
                (select count(*) from organizations.organizations where code = @code),
                (select count(*) from audit.audit_records where metadata->>'code' = @code)
            """);
        command.Parameters.AddWithValue("code", code);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }
}

/// <summary>
/// Proves the append-only guarantee is enforced by database privileges, not only
/// by the absence of application code that mutates audit rows (ADR-008, ADR-019).
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class AuditAppendOnlyTests(PlatformApiFixture fixture)
{
    private async Task<Guid> CreateOrganizationAsync()
    {
        var client = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsCreate);

        var code = "APPEND" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();
        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Append Only Company", code });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        return body!.Id;
    }

    [Fact]
    public async Task Runtime_role_cannot_update_committed_audit_records()
    {
        var organizationId = await CreateOrganizationAsync();

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "update audit.audit_records set operation = 'tampered' where entity_id = @entity_id",
            connection);
        command.Parameters.AddWithValue("entity_id", organizationId.ToString());

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        // 42501 = insufficient_privilege
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Runtime_role_cannot_delete_committed_audit_records()
    {
        var organizationId = await CreateOrganizationAsync();

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "delete from audit.audit_records where entity_id = @entity_id",
            connection);
        command.Parameters.AddWithValue("entity_id", organizationId.ToString());

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task Runtime_role_can_still_read_and_insert_audit_records()
    {
        var organizationId = await CreateOrganizationAsync();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            "select count(*) from audit.audit_records where entity_id = @entity_id");
        command.Parameters.AddWithValue("entity_id", organizationId.ToString());

        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
