using System.Net;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Refused operations must leave a trace, otherwise tenant-boundary probing is
/// invisible (ADR-025). The record is written on its own connection so it
/// survives the rollback of the operation that was refused.
/// </summary>
[Collection(PlatformApiCollection.Name)]
public sealed class DenialAuditTests(PlatformApiFixture fixture)
{
    private async Task<long> CountDenialsAsync(Guid actorUserId)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select count(*) from audit.audit_records
            where operation = @operation and actor_user_id = @actor and outcome = 'Failure'
            """);
        command.Parameters.AddWithValue("operation", AuditOperations.AuthorizationDenied);
        command.Parameters.AddWithValue("actor", actorUserId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_denied_organization_scoped_write_is_recorded()
    {
        var ownOrganizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();

        var client = OrganizationMember(
            fixture, userId, ownOrganizationId, OrganizationPermissions.MembershipsCreate);

        // Reaching into another tenant.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{otherOrganizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await CountDenialsAsync(userId));
    }

    [Fact]
    public async Task A_denial_record_captures_the_actor_scope_and_reason()
    {
        var organizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();

        // Authenticated in the organization but lacking the permission.
        var client = OrganizationMember(
            fixture, userId, organizationId, OrganizationPermissions.MembershipsView);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/subsidiaries",
            new { name = "Denied Subsidiary", code = "DEN" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var session =
            await ScopedSqlSession.OpenAsOrganizationAsync(fixture, organizationId);
        await using var command = session.Command(
            """
            select actor_type, organization_scope_id, actor_membership_id,
                   entity_type, entity_id, outcome, metadata::text
            from audit.audit_records
            where operation = @operation and actor_user_id = @actor
            """);
        command.Parameters.AddWithValue("operation", AuditOperations.AuthorizationDenied);
        command.Parameters.AddWithValue("actor", userId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The denial must be recorded.");

        Assert.Equal("OrganizationMember", reader.GetString(0));
        Assert.Equal(organizationId, reader.GetGuid(1));
        Assert.NotEqual(Guid.Empty, reader.GetGuid(2));
        Assert.Equal("HttpEndpoint", reader.GetString(3));
        Assert.Contains("/subsidiaries", reader.GetString(4), StringComparison.Ordinal);
        Assert.Equal("Failure", reader.GetString(5));

        var metadata = reader.GetString(6);
        Assert.Contains("POST", metadata, StringComparison.Ordinal);
        Assert.Contains("auth.forbidden", metadata, StringComparison.Ordinal);
        // A denial record must not leak credentials any more than a success one.
        Assert.DoesNotContain("password", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_denied_platform_operation_is_recorded_against_the_operator()
    {
        var userId = Guid.CreateVersion7();

        // Authenticated platform operator holding only the view permission.
        var client = PlatformOperator(
            fixture,
            userId,
            PlatformPermissions.OrganizationsView);

        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Denied Company", code = "DENP" + Guid.NewGuid().ToString("N")[..11].ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select actor_type, organization_scope_id
            from audit.audit_records
            where operation = @operation and actor_user_id = @actor
            """);
        command.Parameters.AddWithValue("operation", AuditOperations.AuthorizationDenied);
        command.Parameters.AddWithValue("actor", userId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("PlatformOperator", reader.GetString(0));
        // A platform operator acts outside any customer organization.
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task An_unauthenticated_request_is_not_recorded()
    {
        var organizationId = await CreateOrganizationAsync(fixture);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var before = session.Command(
            "select count(*) from audit.audit_records where operation = @operation");
        before.Parameters.AddWithValue("operation", AuditOperations.AuthorizationDenied);
        var countBefore = (long)(await before.ExecuteScalarAsync())!;

        // No principal to attribute, and auditing these would let anyone fill the
        // table by hammering a protected route.
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{organizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var after = session.Command(
            "select count(*) from audit.audit_records where operation = @operation");
        after.Parameters.AddWithValue("operation", AuditOperations.AuthorizationDenied);

        Assert.Equal(countBefore, (long)(await after.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_denial_record_survives_although_the_refused_operation_wrote_nothing()
    {
        var ownOrganizationId = await CreateOrganizationAsync(fixture);
        var otherOrganizationId = await CreateOrganizationAsync(fixture);
        var userId = Guid.CreateVersion7();

        var client = OrganizationMember(
            fixture, userId, ownOrganizationId, OrganizationPermissions.MembershipsCreate);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/organizations/{otherOrganizationId}/memberships",
            new { userId = Guid.CreateVersion7() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The denial is durable...
        Assert.Equal(1, await CountDenialsAsync(userId));

        // ...while the operation itself left no row behind.
        Assert.Equal(0, await CountMembershipsAsync(fixture, otherOrganizationId));
    }
}
