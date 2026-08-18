using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class OrganizationCreationTests(PlatformApiFixture fixture)
{
    private HttpClient CreateClient(Guid? userId, params string[] permissions)
    {
        if (userId is null)
        {
            var anonymous = fixture.Factory.CreateClient();
            anonymous.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            return anonymous;
        }

        return MembershipTestSupport.PlatformOperator(fixture, permissions);
    }

    private HttpClient CreatePlatformOperator() =>
        CreateClient(Guid.CreateVersion7(), PlatformPermissions.OrganizationsCreate, PlatformPermissions.OrganizationsView);

    // Random (v4), not v7: a UUID v7's leading hex is a millisecond timestamp,
    // so codes derived from it collide when generated in the same millisecond.
    private static string UniqueCode() => "ORG" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    [Fact]
    public async Task Operator_with_the_create_permission_can_create_an_organization()
    {
        var client = CreatePlatformOperator();
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Example Customer Company", code });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("Example Customer Company", body.Name);
        Assert.Equal(code, body.Code);
        Assert.Equal("Active", body.Status);
        Assert.Equal(0, body.Depth);
        Assert.Equal($"/api/v1/organizations/{body.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Created_organization_is_persisted_with_its_hierarchy_path()
    {
        var client = CreatePlatformOperator();
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Persisted Company", code });
        var body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select name, code, status, depth, parent_organization_id, hierarchy_path::text
            from organizations.organizations
            where id = @id
            """);
        command.Parameters.AddWithValue("id", body!.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal("Persisted Company", reader.GetString(0));
        Assert.Equal(code, reader.GetString(1));
        Assert.Equal("Active", reader.GetString(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.True(reader.IsDBNull(4), "A root organization must have no parent.");
        Assert.Equal("org_" + body.Id.ToString("N"), reader.GetString(5));
    }

    [Fact]
    public async Task Creating_an_organization_writes_a_matching_audit_record()
    {
        var client = CreatePlatformOperator();
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Audited Company", code });
        var body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select operation, entity_type, entity_id, outcome, actor_type, organization_scope_id, metadata::text
            from audit.audit_records
            where entity_id = @entity_id
            """);
        command.Parameters.AddWithValue("entity_id", body!.Id.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "An audit record must exist for the created organization.");

        Assert.Equal("organization.created", reader.GetString(0));
        Assert.Equal("Organization", reader.GetString(1));
        Assert.Equal(body.Id.ToString(), reader.GetString(2));
        Assert.Equal("Success", reader.GetString(3));
        Assert.Equal("PlatformOperator", reader.GetString(4));
        Assert.Equal(body.Id, reader.GetGuid(5));

        var metadata = reader.GetString(6);
        Assert.Contains(code, metadata, StringComparison.Ordinal);
        // Audit metadata must never carry credentials or full request payloads.
        Assert.DoesNotContain("password", metadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthenticated_caller_receives_401()
    {
        var client = CreateClient(userId: null);

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Denied", code = UniqueCode() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Caller_without_the_create_permission_receives_403()
    {
        // Authenticated as a platform operator, but holding only the view permission.
        var client = CreateClient(Guid.CreateVersion7(), PlatformPermissions.OrganizationsView);
        var code = UniqueCode();

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Denied", code });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoOrganizationOrAuditRowsAsync(code);
    }

    [Fact]
    public async Task Duplicate_organization_codes_are_rejected()
    {
        var client = CreatePlatformOperator();
        var code = UniqueCode();

        var first = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "First Company", code });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same code in a different case must normalize to the same value.
        var second = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Second Company", code = code.ToLowerInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(1, await CountOrganizationsAsync(code));
    }

    [Theory]
    [InlineData(null, "VALIDCODE")]
    [InlineData("", "VALIDCODE")]
    [InlineData("Valid Name", null)]
    [InlineData("Valid Name", "")]
    [InlineData("Valid Name", "has space")]
    [InlineData("Valid Name", "-leading-hyphen")]
    [InlineData("Valid Name", "A")]
    public async Task Invalid_requests_are_rejected_without_creating_rows(string? name, string? code)
    {
        var client = CreatePlatformOperator();

        var response = await client.PostAsJsonAsync("/api/v1/organizations", new { name, code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        if (!string.IsNullOrWhiteSpace(code))
        {
            await AssertNoOrganizationOrAuditRowsAsync(code);
        }
    }

    [Fact]
    public async Task Read_endpoint_returns_an_existing_organization()
    {
        var client = CreatePlatformOperator();
        var code = UniqueCode();

        var created = await client.PostAsJsonAsync("/api/v1/organizations", new { name = "Readable Company", code });
        var createdBody = await created.Content.ReadFromJsonAsync<OrganizationResponse>();

        var response = await client.GetAsync($"/api/v1/organizations/{createdBody!.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<OrganizationResponse>();
        Assert.Equal(createdBody.Id, body!.Id);
        Assert.Equal("Readable Company", body.Name);
        Assert.Equal(code, body.Code);
    }

    [Fact]
    public async Task Read_endpoint_returns_404_for_an_unknown_id()
    {
        var client = CreatePlatformOperator();

        var response = await client.GetAsync($"/api/v1/organizations/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Read_endpoint_requires_the_view_permission()
    {
        var reader = CreatePlatformOperator();
        var code = UniqueCode();
        var created = await reader.PostAsJsonAsync("/api/v1/organizations", new { name = "Guarded Company", code });
        var body = await created.Content.ReadFromJsonAsync<OrganizationResponse>();

        var client = CreateClient(Guid.CreateVersion7(), PlatformPermissions.OrganizationsCreate);

        var response = await client.GetAsync($"/api/v1/organizations/{body!.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task AssertNoOrganizationOrAuditRowsAsync(string code)
    {
        Assert.Equal(0, await CountOrganizationsAsync(code));

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var auditCommand = session.Command(
            "select count(*) from audit.audit_records where metadata->>'code' = @code");
        auditCommand.Parameters.AddWithValue("code", code.ToUpperInvariant());

        Assert.Equal(0L, (long)(await auditCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// Counts as a platform operator: the organizations table is behind RLS, so a
    /// context-free connection would see nothing regardless of what exists.
    /// </summary>
    private async Task<int> CountOrganizationsAsync(string code)
    {
        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);

        return (int)await session.ScalarCountAsync(
            "select count(*) from organizations.organizations where code = @code",
            command => command.Parameters.AddWithValue("code", code.ToUpperInvariant()));
    }
}

internal sealed record OrganizationResponse(
    Guid Id,
    string Name,
    string Code,
    string Status,
    int Depth,
    DateTimeOffset CreatedAtUtc);
