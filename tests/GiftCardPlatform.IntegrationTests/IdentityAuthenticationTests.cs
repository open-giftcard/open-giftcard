using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GiftCardPlatform.Modules.Authorization.Contracts;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class IdentityAuthenticationTests(PlatformApiFixture fixture)
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task Platform_operator_can_create_a_user_and_credentials_are_only_stored_as_hashes()
    {
        var email = UniqueEmail();
        var client = PlatformUserAdministrator();

        var response = await client.PostAsJsonAsync(
            "/api/v1/users",
            new { email, password = Password });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        Assert.Equal(email, user!.Email);
        Assert.Equal("Active", user.Status);

        var login = await LoginAsync(email, Password);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.InRange(
            login.AccessTokenExpiresAtUtc - DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(13),
            TimeSpan.FromMinutes(16));
        Assert.InRange(
            login.RefreshTokenExpiresAtUtc - DateTimeOffset.UtcNow,
            TimeSpan.FromDays(29),
            TimeSpan.FromDays(31));

        await using var connection = await fixture.OpenAppConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            select u.password_hash,
                   exists (
                       select 1
                       from identity.refresh_tokens rt
                       where rt.token_hash = @plaintext
                   )
            from identity.users u
            where u.id = @user_id
            """,
            connection);
        command.Parameters.AddWithValue("plaintext", login.RefreshToken);
        command.Parameters.AddWithValue("user_id", user.Id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.NotEqual(Password, reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        await reader.DisposeAsync();

        await using var auditSession = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var auditCommand = auditSession.Command(
            """
            select coalesce(string_agg(coalesce(metadata::text, ''), ' '), '')
            from audit.audit_records
            where operation like 'identity.%'
            """);
        var auditMetadata = (string)(await auditCommand.ExecuteScalarAsync())!;
        Assert.DoesNotContain(Password, auditMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain(login.RefreshToken, auditMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("password", auditMetadata, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", auditMetadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normalized_email_is_globally_unique()
    {
        var email = UniqueEmail();
        await CreateUserAsync(email);

        var duplicate = await PlatformUserAdministrator().PostAsJsonAsync(
            "/api/v1/users",
            new { email = $"  {email.ToUpperInvariant()}  ", password = Password });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Email_login_is_case_insensitive_and_unknown_and_wrong_credentials_do_not_disclose_the_account()
    {
        var email = UniqueEmail();
        await CreateUserAsync(email);

        var successful = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = email.ToUpperInvariant(), password = Password });
        Assert.Equal(HttpStatusCode.OK, successful.StatusCode);

        var wrongPassword = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = "this password is incorrect" });
        var unknownUser = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = UniqueEmail(), password = "this password is incorrect" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        var wrongProblem = await ReadProblemAsync(wrongPassword);
        var unknownProblem = await ReadProblemAsync(unknownUser);
        Assert.Equal("auth.invalid_credentials", wrongProblem.Code);
        Assert.Equal(wrongProblem.Code, unknownProblem.Code);
        Assert.Equal(wrongProblem.Detail, unknownProblem.Detail);
    }

    [Fact]
    public async Task Refresh_token_rotates_once_and_reuse_revokes_the_whole_session_family()
    {
        var email = UniqueEmail();
        await CreateUserAsync(email);
        var initial = await LoginAsync(email, Password);

        var rotatedResponse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = initial.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = (await rotatedResponse.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        Assert.NotEqual(initial.RefreshToken, rotated.RefreshToken);

        var reuse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = initial.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        var familyTokenAfterReuse = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, familyTokenAfterReuse.StatusCode);

        await using var session = await ScopedSqlSession.OpenAsPlatformAsync(fixture);
        await using var command = session.Command(
            """
            select count(*)
            from audit.audit_records
            where operation = 'identity.refresh_token.reuse_detected'
              and actor_type = 'IdentityUser'
            """);
        Assert.True((long)(await command.ExecuteScalarAsync())! >= 1);
    }

    [Fact]
    public async Task Concurrent_refresh_cannot_issue_two_valid_replacements()
    {
        var email = UniqueEmail();
        await CreateUserAsync(email);
        var initial = await LoginAsync(email, Password);
        var firstClient = fixture.Factory.CreateClient();
        var secondClient = fixture.Factory.CreateClient();

        var attempts = await Task.WhenAll(
            firstClient.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new { refreshToken = initial.RefreshToken }),
            secondClient.PostAsJsonAsync(
                "/api/v1/auth/refresh",
                new { refreshToken = initial.RefreshToken }));

        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Unauthorized);

        var issued = (await attempts
            .Single(response => response.StatusCode == HttpStatusCode.OK)
            .Content.ReadFromJsonAsync<TokenPairResponse>())!;
        var afterReuseDetection = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = issued.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuseDetection.StatusCode);
    }

    [Fact]
    public async Task Revocation_is_idempotent_and_prevents_further_refresh()
    {
        var email = UniqueEmail();
        await CreateUserAsync(email);
        var tokens = await LoginAsync(email, Password);
        var client = fixture.Factory.CreateClient();

        var first = await client.PostAsJsonAsync(
            "/api/v1/auth/revoke",
            new { refreshToken = tokens.RefreshToken });
        var second = await client.PostAsJsonAsync(
            "/api/v1/auth/revoke",
            new { refreshToken = tokens.RefreshToken });
        var refresh = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_revokes_sessions_and_blocks_login()
    {
        var email = UniqueEmail();
        var user = await CreateUserAsync(email);
        var tokens = await LoginAsync(email, Password);

        var disable = await PlatformUserAdministrator().PostAsync(
            $"/api/v1/users/{user.Id}/disable",
            content: null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var login = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password = Password });
        var refresh = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Expired_session_cannot_refresh()
    {
        var email = UniqueEmail();
        var user = await CreateUserAsync(email);
        var tokens = await LoginAsync(email, Password);

        await using (var connection = await fixture.OpenAppConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            update identity.refresh_tokens
            set created_at_utc = now() - interval '2 days',
                expires_at_utc = now() - interval '1 day'
            where session_id in (
                select id from identity.sessions where user_id = @user_id
            );

            update identity.sessions
            set created_at_utc = now() - interval '2 days',
                expires_at_utc = now() - interval '1 day'
            where user_id = @user_id;
            """,
            connection))
        {
            command.Parameters.AddWithValue("user_id", user.Id);
            await command.ExecuteNonQueryAsync();
        }

        var refresh = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Jwt_bearer_identity_can_select_only_a_verified_organization_membership()
    {
        var email = UniqueEmail();
        var user = await CreateUserAsync(email);
        var tokens = await LoginAsync(email, Password);
        var organizationId = await MembershipTestSupport.CreateOrganizationAsync(fixture);
        await MembershipTestSupport.ProvisionOrganizationActorAsync(
            fixture,
            user.Id,
            organizationId,
            [OrganizationPermissions.MembershipsView]);

        var authorized = fixture.Factory.CreateClient();
        authorized.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        authorized.DefaultRequestHeaders.Add("X-Organization-Id", organizationId.ToString());

        var accepted = await authorized.GetAsync(
            $"/api/v1/organizations/{organizationId}/memberships?limit=10&offset=0");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var unverified = fixture.Factory.CreateClient();
        unverified.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        unverified.DefaultRequestHeaders.Add("X-Organization-Id", Guid.CreateVersion7().ToString());

        var refused = await unverified.GetAsync(
            $"/api/v1/organizations/{organizationId}/memberships?limit=10&offset=0");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task Login_endpoint_is_rate_limited()
    {
        await using var limitedFactory = fixture.Factory.WithWebHostBuilder(webHost =>
            webHost.UseSetting("Authentication:LoginRateLimit:PermitLimit", "2"));
        var client = limitedFactory.CreateClient();

        var first = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = UniqueEmail(), password = "invalid password value" });
        var second = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = UniqueEmail(), password = "invalid password value" });
        var third = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = UniqueEmail(), password = "invalid password value" });

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    private HttpClient PlatformUserAdministrator() =>
        MembershipTestSupport.PlatformOperator(
            fixture,
            PlatformPermissions.UsersCreate,
            PlatformPermissions.UsersDisable);

    private async Task<UserResponse> CreateUserAsync(string email)
    {
        var response = await PlatformUserAdministrator().PostAsJsonAsync(
            "/api/v1/users",
            new { email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>())!;
    }

    private async Task<TokenPairResponse> LoginAsync(string email, string password)
    {
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenPairResponse>())!;
    }

    private static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ProblemResponse(
            document.RootElement.GetProperty("code").GetString()!,
            document.RootElement.GetProperty("detail").GetString()!);
    }

    private static string UniqueEmail() =>
        $"identity-{Guid.NewGuid():N}@example.com";

    private sealed record UserResponse(
        Guid Id,
        string Email,
        string Status,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? DisabledAtUtc);

    private sealed record TokenPairResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAtUtc,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAtUtc);

    private sealed record ProblemResponse(string Code, string Detail);
}
