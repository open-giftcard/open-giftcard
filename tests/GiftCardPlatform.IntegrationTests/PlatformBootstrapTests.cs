using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PlatformBootstrapTests(PlatformApiFixture fixture)
{
    private const string Password = "bootstrap administrator passphrase";
    private const string BootstrapSecretHeader = "X-Platform-Bootstrap-Secret";

    [Fact]
    public async Task Bootstrap_is_secret_protected_one_time_persisted_and_audited_without_credentials()
    {
        await ResetBootstrapAsync();
        var email = UniqueEmail("platform-admin");

        var wrongSecret = await BootstrapAsync(
            email,
            Password,
            "incorrect-bootstrap-secret-value");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);

        var missingSecret = await BootstrapAsync(
            email,
            Password,
            secret: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missingSecret.StatusCode);

        var created = await BootstrapAsync(email, Password, fixture.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var administrator =
            (await created.Content.ReadFromJsonAsync<PlatformBootstrapResponse>())!;

        var second = await BootstrapAsync(
            UniqueEmail("second-admin"),
            Password,
            fixture.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var uncredentialedSecond = await BootstrapAsync(
            UniqueEmail("second-admin-without-secret"),
            Password,
            secret: null);
        Assert.Equal(HttpStatusCode.Conflict, uncredentialedSecond.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select
                (select count(*) from "authorization".platform_roles),
                (select count(*) from "authorization".platform_role_assignments
                    where user_id = @user_id),
                (select count(*) from "authorization".platform_role_permissions
                    where role_id = @role_id),
                (select completed_at_utc is not null
                    from "authorization".platform_bootstrap_state where id = 1),
                (select password_hash <> @password from identity.users where id = @user_id),
                (select coalesce(string_agg(coalesce(metadata::text, ''), ' '), '')
                    from audit.audit_records
                    where operation in (
                        'identity.user.created',
                        'authorization.platform_administrator.bootstrapped'
                    )
                    and actor_user_id = @user_id)
            """);
        command.Parameters.AddWithValue("user_id", administrator.UserId);
        command.Parameters.AddWithValue("role_id", administrator.PlatformRoleId);
        command.Parameters.AddWithValue("password", Password);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(PlatformPermissions.All.Count, reader.GetInt64(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        var metadata = reader.GetString(5);
        Assert.DoesNotContain(Password, metadata, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.BootstrapSecret, metadata, StringComparison.Ordinal);

        var tokens = await LoginAsync(email, Password);
        var platform = BearerClient(tokens.AccessToken);
        var organization = await platform.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Persisted Platform Authorization", code = UniqueCode() });
        Assert.Equal(HttpStatusCode.Created, organization.StatusCode);
    }

    [Fact]
    public async Task Concurrent_bootstrap_allows_exactly_one_success()
    {
        await ResetBootstrapAsync();

        var attempts = await Task.WhenAll(
            BootstrapAsync(UniqueEmail("concurrent-a"), Password, fixture.BootstrapSecret),
            BootstrapAsync(UniqueEmail("concurrent-b"), Password, fixture.BootstrapSecret));

        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            "select count(*) from \"authorization\".platform_role_assignments",
            connection);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Initial_company_administrator_gets_subtree_authority_and_is_idempotent()
    {
        await ResetBootstrapAsync();
        var platformEmail = UniqueEmail("platform");
        var bootstrap = await BootstrapAsync(
            platformEmail,
            Password,
            fixture.BootstrapSecret);
        bootstrap.EnsureSuccessStatusCode();
        var platformTokens = await LoginAsync(platformEmail, Password);
        var platform = BearerClient(platformTokens.AccessToken);

        var organizationResponse = await platform.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Bootstrap Customer", code = UniqueCode() });
        organizationResponse.EnsureSuccessStatusCode();
        var organization =
            (await organizationResponse.Content.ReadFromJsonAsync<OrganizationIdResponse>())!;

        var customerEmail = UniqueEmail("company-admin");
        var customerUserResponse = await platform.PostAsJsonAsync(
            "/api/v1/users",
            new { email = customerEmail, password = Password });
        customerUserResponse.EnsureSuccessStatusCode();
        var customer =
            (await customerUserResponse.Content.ReadFromJsonAsync<UserIdResponse>())!;

        var assigned = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/initial-administrator",
            new { userId = customer.Id });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var assignment =
            (await assigned.Content.ReadFromJsonAsync<InitialAdministratorResponse>())!;

        var repeated = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/initial-administrator",
            new { userId = customer.Id });
        Assert.Equal(HttpStatusCode.Created, repeated.StatusCode);
        var repeatedAssignment =
            (await repeated.Content.ReadFromJsonAsync<InitialAdministratorResponse>())!;
        Assert.Equal(assignment.RoleAssignmentId, repeatedAssignment.RoleAssignmentId);

        var otherUserResponse = await platform.PostAsJsonAsync(
            "/api/v1/users",
            new { email = UniqueEmail("other-admin"), password = Password });
        var otherUser =
            (await otherUserResponse.Content.ReadFromJsonAsync<UserIdResponse>())!;
        var conflicting = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/initial-administrator",
            new { userId = otherUser.Id });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);

        var customerTokens = await LoginAsync(customerEmail, Password);
        var customerClient = BearerClient(customerTokens.AccessToken, organization.Id);
        var subsidiaryResponse = await customerClient.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/subsidiaries",
            new { name = "Admin Created Subsidiary", code = "SUB" + UniqueCode()[..10] });
        Assert.Equal(HttpStatusCode.Created, subsidiaryResponse.StatusCode);
        var subsidiary =
            (await subsidiaryResponse.Content.ReadFromJsonAsync<OrganizationIdResponse>())!;

        var subsidiaryAssignment = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{subsidiary.Id}/initial-administrator",
            new { userId = otherUser.Id });
        Assert.Equal(HttpStatusCode.NotFound, subsidiaryAssignment.StatusCode);

        var otherOrganizationResponse = await platform.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Unrelated Customer", code = UniqueCode() });
        var otherOrganization =
            (await otherOrganizationResponse.Content.ReadFromJsonAsync<OrganizationIdResponse>())!;
        var crossTenant = BearerClient(customerTokens.AccessToken, otherOrganization.Id);
        var refused = await crossTenant.GetAsync(
            $"/api/v1/organizations/{otherOrganization.Id}/memberships?limit=10&offset=0");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select r.is_system,
                   count(rp.id),
                   a.scope_type,
                   count(ar.id)
            from "authorization".roles r
            join "authorization".role_permissions rp on rp.role_id = r.id
            join "authorization".membership_role_assignments a on a.role_id = r.id
            left join audit.audit_records ar
              on ar.operation = 'authorization.initial_organization_administrator.assigned'
             and ar.organization_scope_id = r.organization_id
            where r.id = @role_id
            group by r.is_system, a.scope_type
            """);
        command.Parameters.AddWithValue("role_id", assignment.RoleId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(OrganizationPermissions.All.Count, reader.GetInt64(1));
        Assert.Equal("Subtree", reader.GetString(2));
        Assert.True(reader.GetInt64(3) >= 1);
    }

    [Fact]
    public async Task Persisted_permission_and_active_root_user_checks_are_enforced()
    {
        await ResetBootstrapAsync();
        var platformEmail = UniqueEmail("restricted-platform");
        (await BootstrapAsync(platformEmail, Password, fixture.BootstrapSecret))
            .EnsureSuccessStatusCode();
        var platformTokens = await LoginAsync(platformEmail, Password);
        var platform = BearerClient(platformTokens.AccessToken);

        var organizationResponse = await platform.PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Permission Check Customer", code = UniqueCode() });
        var organization =
            (await organizationResponse.Content.ReadFromJsonAsync<OrganizationIdResponse>())!;
        var inactiveUserResponse = await platform.PostAsJsonAsync(
            "/api/v1/users",
            new { email = UniqueEmail("inactive-admin"), password = Password });
        var inactiveUser =
            (await inactiveUserResponse.Content.ReadFromJsonAsync<UserIdResponse>())!;
        var disable = await platform.PostAsync(
            $"/api/v1/users/{inactiveUser.Id}/disable",
            content: null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var inactiveDenied = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/initial-administrator",
            new { userId = inactiveUser.Id });
        Assert.Equal(HttpStatusCode.NotFound, inactiveDenied.StatusCode);

        var ordinaryEmail = UniqueEmail("ordinary-user");
        var ordinaryUserResponse = await platform.PostAsJsonAsync(
            "/api/v1/users",
            new { email = ordinaryEmail, password = Password });
        var ordinaryUser =
            (await ordinaryUserResponse.Content.ReadFromJsonAsync<UserIdResponse>())!;
        var ordinaryTokens = await LoginAsync(ordinaryEmail, Password);
        var ordinaryCreate = await BearerClient(ordinaryTokens.AccessToken).PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Unauthorized Organization", code = UniqueCode() });
        Assert.Equal(HttpStatusCode.Forbidden, ordinaryCreate.StatusCode);

        await using (var connection = new NpgsqlConnection(fixture.MigratorConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                delete from "authorization".platform_role_permissions
                where permission = @permission
                """,
                connection);
            command.Parameters.AddWithValue(
                "permission",
                PlatformPermissions.InitialAdministratorsAssign);
            await command.ExecuteNonQueryAsync();
        }

        var denied = await platform.PostAsJsonAsync(
            $"/api/v1/organizations/{organization.Id}/initial-administrator",
            new { userId = ordinaryUser.Id });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private async Task<HttpResponseMessage> BootstrapAsync(
        string email,
        string password,
        string? secret)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/bootstrap/platform-administrator")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        if (secret is not null)
        {
            request.Headers.Add(BootstrapSecretHeader, secret);
        }

        return await fixture.Factory.CreateClient().SendAsync(request);
    }

    private async Task<TokenResponse> LoginAsync(string email, string password)
    {
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private HttpClient BearerClient(string accessToken, Guid? organizationId = null)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        if (organizationId is not null)
        {
            client.DefaultRequestHeaders.Add(
                "X-Organization-Id",
                organizationId.Value.ToString());
        }

        return client;
    }

    private async Task ResetBootstrapAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await MembershipTestSupport.SetSessionContextAsync(
            connection,
            transaction,
            organizationId: null,
            isPlatformOperator: true);

        await using var command = new NpgsqlCommand(
            """
            delete from "authorization".organization_administrator_bootstraps;
            delete from "authorization".membership_role_assignments
            where role_id in (
                select id from "authorization".roles where is_system
            );
            delete from "authorization".roles where is_system;
            delete from "authorization".platform_role_assignments;
            delete from "authorization".platform_role_permissions;
            delete from "authorization".platform_roles;
            update "authorization".platform_bootstrap_state
            set completed_at_utc = null, completed_by_user_id = null
            where id = 1;
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static string UniqueEmail(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@example.com";

    private static string UniqueCode() =>
        "BST" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private sealed record PlatformBootstrapResponse(
        Guid UserId,
        string Email,
        Guid PlatformRoleId,
        DateTimeOffset CompletedAtUtc);

    private sealed record TokenResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAtUtc,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAtUtc);

    private sealed record OrganizationIdResponse(Guid Id);

    private sealed record UserIdResponse(Guid Id);

    private sealed record InitialAdministratorResponse(
        Guid OrganizationId,
        Guid UserId,
        Guid MembershipId,
        Guid RoleId,
        Guid RoleAssignmentId,
        DateTimeOffset AssignedAtUtc);
}
