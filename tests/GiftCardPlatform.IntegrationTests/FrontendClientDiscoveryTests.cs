using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class FrontendClientDiscoveryTests(PlatformApiFixture fixture)
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task Current_user_endpoint_composes_identity_platform_and_selected_organization_context()
    {
        var user = await CreateUserAsync();

        var identity = IdentityClient(user.Id);
        var identityResponse = await identity.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");

        Assert.Equal(user.Id, identityResponse!.Id);
        Assert.Equal(user.Email, identityResponse.Email);
        Assert.Equal("Identity", identityResponse.ContextType);
        Assert.Empty(identityResponse.PlatformPermissions);
        Assert.Null(identityResponse.OrganizationContext);

        var platform = MembershipTestSupport.PlatformOperator(
            fixture,
            user.Id,
            PlatformPermissions.OrganizationsView);
        var platformResponse = await platform.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");

        Assert.Equal("Platform", platformResponse!.ContextType);
        Assert.Equal([PlatformPermissions.OrganizationsView], platformResponse.PlatformPermissions);
        Assert.Null(platformResponse.OrganizationContext);

        var organization = await CreateOrganizationAsync("Portal Context");
        var organizationClient = MembershipTestSupport.OrganizationMember(
            fixture,
            user.Id,
            organization.Id,
            OrganizationPermissions.GiftCardsView);
        var organizationResponse =
            await organizationClient.GetFromJsonAsync<CurrentUserResponse>("/api/v1/me");

        Assert.Equal("Organization", organizationResponse!.ContextType);
        Assert.Empty(organizationResponse.PlatformPermissions);
        Assert.NotNull(organizationResponse.OrganizationContext);
        Assert.Equal(organization.Id, organizationResponse.OrganizationContext.Organization.Id);
        Assert.Equal(
            [OrganizationPermissions.GiftCardsView],
            organizationResponse.OrganizationContext.EffectivePermissions);
    }

    [Fact]
    public async Task Organization_picker_lists_only_the_current_users_active_memberships()
    {
        var user = await CreateUserAsync();
        var otherUser = await CreateUserAsync();
        var first = await CreateOrganizationAsync("Picker Alpha");
        var second = await CreateOrganizationAsync("Picker Beta");
        var disabled = await CreateOrganizationAsync("Picker Disabled");
        var other = await CreateOrganizationAsync("Picker Other User");

        await MembershipTestSupport.ProvisionOrganizationActorAsync(
            fixture,
            user.Id,
            first.Id,
            []);
        await MembershipTestSupport.ProvisionOrganizationActorAsync(
            fixture,
            user.Id,
            second.Id,
            []);
        var disabledMembershipId =
            await MembershipTestSupport.ProvisionOrganizationActorAsync(
                fixture,
                user.Id,
                disabled.Id,
                []);
        await MembershipTestSupport.ProvisionOrganizationActorAsync(
            fixture,
            otherUser.Id,
            other.Id,
            []);
        await DisableMembershipAsync(disabledMembershipId, disabled.Id);

        var client = IdentityClient(user.Id);
        var page = await client.GetFromJsonAsync<PagedResponse<UserOrganizationResponse>>(
            "/api/v1/me/organizations?limit=1&offset=0");

        Assert.Single(page!.Items);
        Assert.Equal(first.Id, page.Items[0].Organization.Id);
        Assert.True(page.HasMore);

        var remainder = await client.GetFromJsonAsync<PagedResponse<UserOrganizationResponse>>(
            "/api/v1/me/organizations?limit=10&offset=1");

        Assert.Single(remainder!.Items);
        Assert.Equal(second.Id, remainder.Items[0].Organization.Id);
        Assert.False(remainder.HasMore);
        Assert.DoesNotContain(
            remainder.Items,
            item => item.Organization.Id == disabled.Id || item.Organization.Id == other.Id);
    }

    [Fact]
    public async Task Organization_picker_rejects_an_already_selected_organization_context()
    {
        var user = await CreateUserAsync();
        var organization = await CreateOrganizationAsync("Selected Picker Context");
        var client = MembershipTestSupport.OrganizationMember(
            fixture,
            user.Id,
            organization.Id);

        var response = await client.GetAsync("/api/v1/me/organizations");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "organization.discovery.identity_context_required",
            await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task Platform_organization_list_supports_literal_search_status_and_bounded_paging()
    {
        var marker = "Portal_" + Guid.NewGuid().ToString("N")[..10];
        var active = await CreateOrganizationAsync(marker + " Active");
        var suspended = await CreateOrganizationAsync(marker + " Suspended");
        await SetOrganizationStatusAsync(suspended.Id, "Suspended");

        var client = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsView);

        var activePage = await client.GetFromJsonAsync<PagedResponse<OrganizationResponse>>(
            $"/api/v1/organizations?search={Uri.EscapeDataString(marker)}&status=active&limit=10");

        Assert.Single(activePage!.Items);
        Assert.Equal(active.Id, activePage.Items[0].Id);

        var suspendedPage = await client.GetFromJsonAsync<PagedResponse<OrganizationResponse>>(
            $"/api/v1/organizations?search={Uri.EscapeDataString(suspended.Code)}&status=Suspended");

        Assert.Single(suspendedPage!.Items);
        Assert.Equal(suspended.Id, suspendedPage.Items[0].Id);
        Assert.Equal("Suspended", suspendedPage.Items[0].Status);

        var invalidPage = await client.GetAsync("/api/v1/organizations?limit=201");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [Fact]
    public async Task Platform_organization_list_requires_its_named_permission()
    {
        var missingPermission = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.UsersCreate);

        var response = await missingPermission.GetAsync("/api/v1/organizations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Discovery_endpoints_require_authentication()
    {
        var client = fixture.Factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/me/organizations")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/organizations")).StatusCode);
    }

    [Fact]
    public async Task Identity_discovery_RLS_is_exact_user_and_read_only()
    {
        var user = await CreateUserAsync();
        var otherUser = await CreateUserAsync();
        var ownOrganization = await CreateOrganizationAsync("RLS Own Discovery");
        var otherOrganization = await CreateOrganizationAsync("RLS Other Discovery");
        var ownMembershipId =
            await MembershipTestSupport.ProvisionOrganizationActorAsync(
                fixture,
                user.Id,
                ownOrganization.Id,
                []);
        await MembershipTestSupport.ProvisionOrganizationActorAsync(
            fixture,
            otherUser.Id,
            otherOrganization.Id,
            []);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await MembershipTestSupport.SetSessionContextAsync(
            connection,
            transaction,
            organizationId: null,
            isPlatformOperator: false,
            user.Id);

        Assert.Equal(
            1L,
            await ScalarCountAsync(
                connection,
                transaction,
                "select count(*) from organizations.organization_memberships",
                []));
        Assert.Equal(
            1L,
            await ScalarCountAsync(
                connection,
                transaction,
                "select count(*) from organizations.organizations",
                []));
        Assert.Equal(
            0L,
            await ScalarCountAsync(
                connection,
                transaction,
                "select count(*) from organizations.organizations where id = @id",
                [new NpgsqlParameter("id", otherOrganization.Id)]));

        await using var membershipWrite = new NpgsqlCommand(
            """
            update organizations.organization_memberships
            set status = 'Disabled'
            where id = @id
            """,
            connection,
            transaction);
        membershipWrite.Parameters.AddWithValue("id", ownMembershipId);
        Assert.Equal(0, await membershipWrite.ExecuteNonQueryAsync());

        await using var organizationWrite = new NpgsqlCommand(
            """
            update organizations.organizations
            set name = 'Forbidden identity write'
            where id = @id
            """,
            connection,
            transaction);
        organizationWrite.Parameters.AddWithValue("id", ownOrganization.Id);
        Assert.Equal(0, await organizationWrite.ExecuteNonQueryAsync());

        await transaction.RollbackAsync();

        await using var contextFreeTransaction = await connection.BeginTransactionAsync();
        await MembershipTestSupport.SetSessionContextAsync(
            connection,
            contextFreeTransaction,
            organizationId: null,
            isPlatformOperator: false,
            userId: null);
        Assert.Equal(
            0L,
            await ScalarCountAsync(
                connection,
                contextFreeTransaction,
                "select count(*) from organizations.organization_memberships",
                []));
        Assert.Equal(
            0L,
            await ScalarCountAsync(
                connection,
                contextFreeTransaction,
                "select count(*) from organizations.organizations",
                []));
        await contextFreeTransaction.RollbackAsync();
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_the_frontend_discovery_contract()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/v1/me", out _));
        Assert.True(paths.TryGetProperty("/api/v1/me/organizations", out _));
        Assert.True(paths.TryGetProperty("/api/v1/organizations", out var organizations));
        Assert.True(organizations.TryGetProperty("get", out _));
    }

    private HttpClient IdentityClient(Guid userId)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateAccessToken(userId));
        return client;
    }

    private async Task<UserResponse> CreateUserAsync()
    {
        var email = $"frontend-{Guid.NewGuid():N}@example.com";
        var client = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.UsersCreate);
        var response = await client.PostAsJsonAsync(
            "/api/v1/users",
            new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>())!;
    }

    private async Task<OrganizationResponse> CreateOrganizationAsync(string name)
    {
        var client = MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.OrganizationsCreate);
        var code = "FED" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var response = await client.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name, code });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrganizationResponse>())!;
    }

    private async Task DisableMembershipAsync(Guid membershipId, Guid organizationId)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await MembershipTestSupport.SetSessionContextAsync(
            connection,
            transaction,
            organizationId,
            isPlatformOperator: false);
        await using var command = new NpgsqlCommand(
            """
            update organizations.organization_memberships
            set status = 'Disabled', disabled_at_utc = now()
            where id = @id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", membershipId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private async Task SetOrganizationStatusAsync(Guid organizationId, string status)
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            update organizations.organizations
            set status = @status
            where id = @id
            """,
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", organizationId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<long> ScalarCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyCollection<NpgsqlParameter> parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private sealed record UserResponse(Guid Id, string Email);

    private sealed record CurrentUserResponse(
        Guid Id,
        string? Email,
        string? PhoneNumber,
        string Status,
        string ContextType,
        IReadOnlyList<string> PlatformPermissions,
        CurrentOrganizationResponse? OrganizationContext);

    private sealed record CurrentOrganizationResponse(
        Guid MembershipId,
        Guid TenantRootOrganizationId,
        OrganizationResponse Organization,
        IReadOnlyList<string> EffectivePermissions);

    private sealed record UserOrganizationResponse(
        Guid MembershipId,
        Guid TenantRootOrganizationId,
        OrganizationResponse Organization,
        DateTimeOffset MembershipCreatedAtUtc);

    private sealed record PagedResponse<T>(
        IReadOnlyList<T> Items,
        int Limit,
        int Offset,
        bool HasMore);
}
