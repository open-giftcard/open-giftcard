using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Payments.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PosAuthenticationTests(PlatformApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Registration_returns_the_secret_once_and_stores_only_its_hash()
    {
        var registered = await RegisterClientAsync();

        Assert.NotEqual(Guid.Empty, registered.Id);
        Assert.False(string.IsNullOrWhiteSpace(registered.Secret));

        // Listing must never expose the secret again.
        var listed = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .GetFromJsonAsync<IReadOnlyList<PosClientResult>>(
                "/api/v1/pos/clients",
                JsonOptions);
        Assert.Contains(listed!, client => client.Id == registered.Id);
        var body = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .GetStringAsync("/api/v1/pos/clients");
        Assert.DoesNotContain(registered.Secret, body, StringComparison.Ordinal);

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select secret_hash from payments.pos_clients where id = @id",
            connection);
        command.Parameters.AddWithValue("id", registered.Id);
        var storedHash = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal(64, storedHash.Length);
        Assert.DoesNotContain(registered.Secret, storedHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registration_requires_the_named_platform_permission()
    {
        var response = await PlatformOperator(fixture, PlatformPermissions.OrganizationsView)
            .PostAsJsonAsync(
                "/api/v1/pos/clients",
                new { code = "POS-" + Guid.NewGuid().ToString("N")[..8], displayName = "Denied" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Valid_credentials_exchange_for_a_short_lived_device_token()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);

        var before = DateTimeOffset.UtcNow;
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = client.Secret,
                terminalCode = terminal.Code,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var issued = (await response.Content
            .ReadFromJsonAsync<PosAccessTokenResult>(JsonOptions))!;

        Assert.Equal(client.Id, issued.PosClientId);
        Assert.Equal(terminal.Id, issued.PosTerminalId);
        Assert.Equal(terminal.StoreReference, issued.StoreReference);
        Assert.InRange(
            issued.ExpiresAtUtc,
            before.AddMinutes(14),
            before.AddMinutes(16));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_wrong_secret_is_refused(string? secret)
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);

        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = secret,
                terminalCode = terminal.Code,
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_client_and_a_wrong_secret_are_indistinguishable()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var anonymous = fixture.Factory.CreateClient();

        var unknownClient = await anonymous.PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = "POS-" + Guid.NewGuid().ToString("N")[..8],
                clientSecret = client.Secret,
                terminalCode = terminal.Code,
            });
        var wrongSecret = await anonymous.PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = "not-the-secret",
                terminalCode = terminal.Code,
            });
        var unknownTerminal = await anonymous.PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = client.Secret,
                terminalCode = "TILL-UNKNOWN",
            });

        // The refusal must not tell an attacker which POS codes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, unknownClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownTerminal.StatusCode);
        var first = await ReadRefusalAsync(unknownClient);
        var second = await ReadRefusalAsync(wrongSecret);
        var third = await ReadRefusalAsync(unknownTerminal);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    /// <summary>
    /// Renders a problem response without its per-request correlation identifier,
    /// which is expected to differ. What must not differ is anything that would
    /// reveal which of the three credentials was wrong.
    /// </summary>
    private static async Task<string> ReadRefusalAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement.EnumerateObject()
            .Where(property => property.Name is not "correlationId" and not "traceId")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}={property.Value.GetRawText()}");
        return string.Join('|', fields);
    }

    [Fact]
    public async Task A_pos_token_cannot_reach_cardholder_or_organization_endpoints()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var token = await AuthenticateAsync(client, terminal);

        using var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // A till is authenticated but holds no user identity, so cardholder
        // reads find nothing and organization context is refused outright.
        var cards = await pos.GetAsync("/api/v1/me/gift-cards");
        var me = await pos.GetAsync("/api/v1/me");

        Assert.NotEqual(HttpStatusCode.OK, me.StatusCode);
        Assert.True(
            cards.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden
                or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
            $"Unexpected cardholder status {cards.StatusCode}.");
        if (cards.StatusCode == HttpStatusCode.OK)
        {
            // If the route answers at all it must expose no cards, because a POS
            // principal owns none.
            var body = await cards.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"giftCardId\"", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_pos_token_authenticates_against_a_pos_endpoint()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var token = await AuthenticateAsync(client, terminal);

        using var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // A hold that does not exist, so the only interesting part is the status.
        // 404 means the device token was accepted and the request reached the
        // service; 401 would mean the API rejected a token it issued itself,
        // which is invisible to every other test here because they all assert
        // refusals and a refused token refuses identically.
        var response = await pos.GetAsync(
            $"/api/v1/pos/payment-provisions/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_pos_token_may_not_select_an_organization_context()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var token = await AuthenticateAsync(client, terminal);

        using var pos = fixture.Factory.CreateClient();
        pos.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);
        pos.DefaultRequestHeaders.Add("X-Organization-Id", Guid.CreateVersion7().ToString());

        var response = await pos.GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_terminal_refuses_new_tokens_but_not_its_siblings()
    {
        var client = await RegisterClientAsync();
        var retired = await RegisterTerminalAsync(client.Id);
        var sibling = await RegisterTerminalAsync(client.Id);
        var issuedBeforeDisable = await AuthenticateAsync(client, retired);
        var route =
            $"/api/v1/pos/clients/{client.Id}/terminals/{retired.Id}/disable";

        var first = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsync(route, content: null);
        var second = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsync(route, content: null);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var disabled = (await second.Content
            .ReadFromJsonAsync<PosTerminalResult>(JsonOptions))!;
        Assert.Equal("Disabled", disabled.Status);
        Assert.NotNull(disabled.DisabledAtUtc);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await TryAuthenticateAsync(client, retired)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await TryAuthenticateAsync(client, sibling)).StatusCode);
        Assert.Equal(
            1,
            await CountAuditRecordsAsync("pos.terminal.disabled", retired.Id));

        using var alreadyIssued = fixture.Factory.CreateClient();
        alreadyIssued.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", issuedBeforeDisable.AccessToken);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await alreadyIssued.GetAsync(
                $"/api/v1/pos/payment-provisions/{Guid.CreateVersion7()}"))
            .StatusCode);
    }

    [Fact]
    public async Task Disabling_a_client_refuses_new_tokens_and_is_idempotently_audited()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var route = $"/api/v1/pos/clients/{client.Id}/disable";

        var first = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsync(route, content: null);
        var second = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsync(route, content: null);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var disabled = (await second.Content
            .ReadFromJsonAsync<PosClientResult>(JsonOptions))!;
        Assert.Equal("Disabled", disabled.Status);
        Assert.NotNull(disabled.DisabledAtUtc);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await TryAuthenticateAsync(client, terminal)).StatusCode);
        Assert.Equal(
            1,
            await CountAuditRecordsAsync("pos.client.disabled", client.Id));

        var terminalRegistration = await PlatformOperator(
                fixture,
                PlatformPermissions.PosClientsManage)
            .PostAsJsonAsync(
                $"/api/v1/pos/clients/{client.Id}/terminals",
                new { code = "TILL-AFTER-DISABLE", storeReference = "STORE-DISABLED" });
        Assert.Equal(HttpStatusCode.Conflict, terminalRegistration.StatusCode);
    }

    [Fact]
    public async Task Pos_retirement_requires_the_named_platform_permission()
    {
        var client = await RegisterClientAsync();
        var terminal = await RegisterTerminalAsync(client.Id);
        var denied = PlatformOperator(fixture, PlatformPermissions.OrganizationsView);

        var clientResponse = await denied.PostAsync(
            $"/api/v1/pos/clients/{client.Id}/disable",
            content: null);
        var terminalResponse = await denied.PostAsync(
            $"/api/v1/pos/clients/{client.Id}/terminals/{terminal.Id}/disable",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, clientResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, terminalResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TryAuthenticateAsync(client, terminal)).StatusCode);
    }

    [Fact]
    public async Task Development_OpenAPI_exposes_pos_authentication_without_secrets()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/auth/token",
            out var tokenPath));
        Assert.True(tokenPath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/clients/{posClientId}/disable",
            out var disableClientPath));
        Assert.True(disableClientPath.TryGetProperty("post", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty(
            "/api/v1/pos/clients/{posClientId}/terminals/{posTerminalId}/disable",
            out var disableTerminalPath));
        Assert.True(disableTerminalPath.TryGetProperty("post", out _));

        var listed = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("PosClientResult")
            .GetProperty("properties");
        Assert.False(listed.TryGetProperty("secret", out _));
        Assert.False(listed.TryGetProperty("secretHash", out _));
        Assert.True(listed.TryGetProperty("disabledAtUtc", out _));
    }

    private async Task<RegisteredPosClientResult> RegisterClientAsync()
    {
        var response = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsJsonAsync(
                "/api/v1/pos/clients",
                new
                {
                    code = "POS-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                    displayName = "Counter Integration",
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<RegisteredPosClientResult>(JsonOptions))!;
    }

    private async Task<PosTerminalResult> RegisterTerminalAsync(Guid posClientId)
    {
        var response = await PlatformOperator(fixture, PlatformPermissions.PosClientsManage)
            .PostAsJsonAsync(
                $"/api/v1/pos/clients/{posClientId}/terminals",
                new
                {
                    code = "TILL-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                    storeReference = "STORE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
                });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PosTerminalResult>(JsonOptions))!;
    }

    private async Task<PosAccessTokenResult> AuthenticateAsync(
        RegisteredPosClientResult client,
        PosTerminalResult terminal)
    {
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = client.Secret,
                terminalCode = terminal.Code,
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content
            .ReadFromJsonAsync<PosAccessTokenResult>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> TryAuthenticateAsync(
        RegisteredPosClientResult client,
        PosTerminalResult terminal)
    {
        using var anonymous = fixture.Factory.CreateClient();
        return await anonymous.PostAsJsonAsync(
            "/api/v1/pos/auth/token",
            new
            {
                clientCode = client.Code,
                clientSecret = client.Secret,
                terminalCode = terminal.Code,
            });
    }

    private async Task<long> CountAuditRecordsAsync(string operation, Guid entityId)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "select set_config('app.is_platform_operator', 'true', true)",
            connection,
            transaction))
        {
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            """
            select count(*) from audit.audit_records
            where operation = @operation and entity_id = @entityId
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("entityId", entityId.ToString());
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
