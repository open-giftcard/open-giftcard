using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftCardPlatform.BuildingBlocks.Errors;
using GiftCardPlatform.BuildingBlocks.Execution;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Ledger.Contracts;
using Microsoft.Extensions.DependencyInjection;
using GiftCardPlatform.Modules.Partners.Contracts;
using Npgsql;
using static GiftCardPlatform.IntegrationTests.MembershipTestSupport;

namespace GiftCardPlatform.IntegrationTests;

[Collection(PlatformApiCollection.Name)]
public sealed class PartnerAuthenticationTests(PlatformApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Registration_returns_the_secret_once_and_stores_only_its_hash()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();

        Assert.False(string.IsNullOrWhiteSpace(client.Secret));

        // Listing must never expose the secret again.
        var body = await Operator()
            .GetStringAsync($"/api/v1/partners/{client.Client.PartnerId}/clients");
        Assert.DoesNotContain(client.Secret, body, StringComparison.Ordinal);

        // Unlike payments.pos_clients, these tables carry RLS, so the raw read
        // needs a context of its own before the row is visible at all.
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
            "select secret_hash from partners.api_clients where id = @id",
            connection,
            transaction);
        command.Parameters.AddWithValue("id", client.Client.Id);
        var storedHash = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal(64, storedHash.Length);
        Assert.DoesNotContain(client.Secret, storedHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Registration_requires_the_named_platform_permission()
    {
        var organizationId = await CreateRootOrganizationAsync();

        var response = await PlatformOperator(fixture, PlatformPermissions.OrganizationsView)
            .PostAsJsonAsync(
                "/api/v1/partners",
                new
                {
                    rootOrganizationId = organizationId,
                    code = UniquePartnerCode(),
                    displayName = "Denied",
                });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_partner_must_be_anchored_to_a_real_active_root_organization()
    {
        var response = await Operator().PostAsJsonAsync(
            "/api/v1/partners",
            new
            {
                rootOrganizationId = Guid.CreateVersion7(),
                code = UniquePartnerCode(),
                displayName = "No Such Organization",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_organization_cannot_back_two_partners()
    {
        var organizationId = await CreateRootOrganizationAsync();
        await RegisterPartnerAsync(organizationId);

        var second = await Operator().PostAsJsonAsync(
            "/api/v1/partners",
            new
            {
                rootOrganizationId = organizationId,
                code = UniquePartnerCode(),
                displayName = "Second Partner",
            });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Valid_credentials_exchange_for_a_short_lived_token()
    {
        var (partner, client) = await RegisterPartnerAndClientAsync();

        var before = DateTimeOffset.UtcNow;
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = client.Client.Code, clientSecret = client.Secret });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var issued = (await response.Content
            .ReadFromJsonAsync<PartnerAccessTokenResult>(JsonOptions))!;
        Assert.Equal(partner.Id, issued.PartnerId);
        Assert.Equal(client.Client.Id, issued.PartnerClientId);
        Assert.InRange(issued.ExpiresAtUtc, before.AddMinutes(4), before.AddMinutes(6));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_wrong_secret_is_refused(string? secret)
    {
        var (_, client) = await RegisterPartnerAndClientAsync();

        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = client.Client.Code, clientSecret = secret });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The refusal must not tell an attacker which partner codes exist, nor
    /// whether a key has been killed. All four cases are one response.
    /// </summary>
    [Fact]
    public async Task Unknown_disabled_and_wrong_credentials_are_indistinguishable()
    {
        var (_, live) = await RegisterPartnerAndClientAsync();
        var (disabledPartner, disabledClient) = await RegisterPartnerAndClientAsync();
        var (_, killedKey) = await RegisterPartnerAndClientAsync();

        await Operator().PostAsync(
            $"/api/v1/partners/{disabledPartner.Id}/disable",
            content: null);
        await Operator().PostAsync(
            $"/api/v1/partners/{killedKey.Client.PartnerId}/clients/{killedKey.Client.Id}/disable",
            content: null);

        var anonymous = fixture.Factory.CreateClient();
        var unknownClient = await anonymous.PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = UniqueClientCode(), clientSecret = live.Secret });
        var wrongSecret = await anonymous.PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = live.Client.Code, clientSecret = "not-the-secret" });
        var partnerDisabled = await anonymous.PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = disabledClient.Client.Code, clientSecret = disabledClient.Secret });
        var clientDisabled = await anonymous.PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = killedKey.Client.Code, clientSecret = killedKey.Secret });

        Assert.Equal(HttpStatusCode.Unauthorized, unknownClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, partnerDisabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, clientDisabled.StatusCode);

        var refusals = new[]
        {
            await ReadRefusalAsync(unknownClient),
            await ReadRefusalAsync(wrongSecret),
            await ReadRefusalAsync(partnerDisabled),
            await ReadRefusalAsync(clientDisabled),
        };
        Assert.Single(refusals.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// The kill switch is only meaningful if it bites before the token expires.
    /// Because the principal is re-resolved per request, an already-issued token
    /// stops working immediately rather than lingering for its full lifetime.
    /// </summary>
    [Fact]
    public async Task Disabling_a_client_invalidates_tokens_it_already_issued()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();
        var token = await AuthenticateAsync(client);

        using var partnerClient = fixture.Factory.CreateClient();
        partnerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var beforeDisable = await partnerClient.GetAsync("/api/v1/me/gift-cards");
        Assert.NotEqual(HttpStatusCode.Unauthorized, beforeDisable.StatusCode);

        await Operator().PostAsync(
            $"/api/v1/partners/{client.Client.PartnerId}/clients/{client.Client.Id}/disable",
            content: null);

        var afterDisable = await partnerClient.GetAsync("/api/v1/me/gift-cards");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDisable.StatusCode);
    }

    [Fact]
    public async Task A_partner_token_cannot_select_an_organization_context()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();
        var token = await AuthenticateAsync(client);

        using var partnerClient = fixture.Factory.CreateClient();
        partnerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);
        partnerClient.DefaultRequestHeaders.Add(OrganizationIdHeader, Guid.CreateVersion7().ToString());

        var response = await partnerClient.GetAsync("/api/v1/organizations");

        // Refused outright rather than ignored, so a reseller cannot appear to
        // act inside another customer's organization.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_partner_token_holds_no_user_or_platform_authority()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();
        var token = await AuthenticateAsync(client);

        using var partnerClient = fixture.Factory.CreateClient();
        partnerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var me = await partnerClient.GetAsync("/api/v1/me");
        var partners = await partnerClient.GetAsync("/api/v1/partners");

        // Authenticated, but neither a person nor a platform operator: it cannot
        // read its own identity, and it certainly cannot administer the registry.
        Assert.NotEqual(HttpStatusCode.OK, me.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, partners.StatusCode);
    }

    /// <summary>
    /// Proves the forced RLS policy directly, independently of the application
    /// query: as the runtime role with one tenant's context established, the
    /// other tenant's partner row must be invisible.
    /// </summary>
    [Fact]
    public async Task Partner_rows_are_invisible_across_tenants_under_raw_rls()
    {
        var (mine, _) = await RegisterPartnerAndClientAsync();
        var (theirs, _) = await RegisterPartnerAndClientAsync();

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "select set_config('app.organization_id', @organizationId, true), "
            + "set_config('app.is_platform_operator', 'false', true)",
            connection,
            transaction))
        {
            scope.Parameters.AddWithValue("organizationId", mine.RootOrganizationId.ToString());
            await scope.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            "select id from partners.partners where id = any(@ids)",
            connection,
            transaction);
        command.Parameters.AddWithValue("ids", new[] { mine.Id, theirs.Id });

        var visible = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                visible.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal([mine.Id], visible);
    }

    /// <summary>
    /// The credential-lookup escape is read-only by construction: it appears in
    /// the policy's using clause and not in with check, so even with the flag set
    /// an insert for another tenant must fail.
    /// </summary>
    [Fact]
    public async Task The_credential_lookup_escape_cannot_be_used_to_write()
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "select set_config('app.is_partner_credential_lookup', 'true', true), "
            + "set_config('app.is_platform_operator', 'false', true)",
            connection,
            transaction))
        {
            await scope.ExecuteNonQueryAsync();
        }

        await using var insert = new NpgsqlCommand(
            """
            insert into partners.partners
                (id, root_organization_id, code, display_name, status, registered_at_utc)
            values
                (@id, @organizationId, @code, 'Smuggled', 'Active', now())
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("organizationId", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("code", UniquePartnerCode());

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_secret_is_never_written_to_the_audit_trail()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();

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
            "select count(*) from audit.audit_records where metadata::text like @secret",
            connection,
            transaction);
        command.Parameters.AddWithValue("secret", $"%{client.Secret}%");

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// A matching tenant root is not authority on its own. A partner principal
    /// carries one without any membership, so the ledger scope guard must refuse
    /// it rather than infer a verified member from the tenant root alone.
    /// </summary>
    [Fact]
    public async Task A_partner_principal_cannot_read_its_own_corporate_credit_balances()
    {
        var (partner, client) = await RegisterPartnerAndClientAsync();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MutableExecutionContext>();
        context.SetPartnerClient(
            client.Client.Id,
            partner.Id,
            partner.RootOrganizationId,
            partner.RootOrganizationId,
            [PartnerScopes.GiftCardsMint]);

        var exception = await Assert.ThrowsAsync<ForbiddenException>(() =>
            scope.ServiceProvider
                .GetRequiredService<ILedgerBalanceQuery>()
                .GetOrganizationCorporateCreditBalancesAsync(
                    partner.RootOrganizationId,
                    CancellationToken.None));

        Assert.Equal("ledger.scope.forbidden", exception.Code);
    }

    /// <summary>
    /// The audit store is append-only by privilege, so an entry claiming a
    /// disable that never happened could never be corrected.
    /// </summary>
    [Fact]
    public async Task Disabling_an_already_disabled_client_is_not_audited_twice()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();
        var route =
            $"/api/v1/partners/{client.Client.PartnerId}/clients/{client.Client.Id}/disable";

        var first = await Operator().PostAsync(route, content: null);
        var second = await Operator().PostAsync(route, content: null);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        Assert.Equal(
            1,
            await CountAuditRecordsAsync("partner.api_client.disabled", client.Client.Id));
    }

    [Fact]
    public async Task Disabling_an_already_disabled_partner_is_not_audited_twice()
    {
        var (partner, _) = await RegisterPartnerAndClientAsync();
        var route = $"/api/v1/partners/{partner.Id}/disable";

        var first = await Operator().PostAsync(route, content: null);
        var second = await Operator().PostAsync(route, content: null);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        Assert.Equal(1, await CountAuditRecordsAsync("partner.disabled", partner.Id));
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

    /// <summary>
    /// The endpoint limiter partitions by IP, which cannot separate resellers
    /// sharing an egress address, and cannot be keyed on the submitted code
    /// because a guesser would just vary it. This throttle keys on the resolved
    /// client instead, so guessing one credential costs that credential its
    /// budget and nothing else.
    /// </summary>
    [Fact]
    public async Task Repeated_wrong_secrets_throttle_that_client_even_once_the_secret_is_right()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = await ExchangeAsync(client.Client.Code, "not-the-secret");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var withCorrectSecret = await ExchangeAsync(client.Client.Code, client.Secret);

        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectSecret.StatusCode);
    }

    /// <summary>
    /// The isolation property the IP-partitioned limiter cannot provide: one
    /// reseller being brute-forced must not stop any other reseller trading,
    /// even though both arrive from the same address.
    /// </summary>
    [Fact]
    public async Task Throttling_one_client_does_not_affect_another()
    {
        var (_, attacked) = await RegisterPartnerAndClientAsync();
        var (_, bystander) = await RegisterPartnerAndClientAsync();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await ExchangeAsync(attacked.Client.Code, "not-the-secret");
        }

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await ExchangeAsync(attacked.Client.Code, attacked.Secret)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ExchangeAsync(bystander.Client.Code, bystander.Secret)).StatusCode);
    }

    /// <summary>
    /// A throttled client must be indistinguishable from a wrong secret, or the
    /// endpoint becomes an oracle for which codes are real and worth attacking.
    /// </summary>
    [Fact]
    public async Task A_throttled_refusal_is_indistinguishable_from_a_wrong_secret()
    {
        var (_, throttledClient) = await RegisterPartnerAndClientAsync();
        var (_, freshClient) = await RegisterPartnerAndClientAsync();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await ExchangeAsync(throttledClient.Client.Code, "not-the-secret");
        }

        var throttled = await ExchangeAsync(throttledClient.Client.Code, throttledClient.Secret);
        var wrongSecret = await ExchangeAsync(freshClient.Client.Code, "not-the-secret");
        var unknown = await ExchangeAsync(UniqueClientCode(), "not-the-secret");

        Assert.Equal(
            await ReadRefusalAsync(throttled),
            await ReadRefusalAsync(wrongSecret));
        Assert.Equal(
            await ReadRefusalAsync(wrongSecret),
            await ReadRefusalAsync(unknown));
    }

    /// <summary>
    /// An operator who mistypes a secret and then gets it right must not be left
    /// throttled, so a success has to clear the record.
    /// </summary>
    [Fact]
    public async Task A_successful_exchange_clears_earlier_failures()
    {
        var (_, client) = await RegisterPartnerAndClientAsync();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await ExchangeAsync(client.Client.Code, "not-the-secret");
        }

        Assert.Equal(
            HttpStatusCode.OK,
            (await ExchangeAsync(client.Client.Code, client.Secret)).StatusCode);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await ExchangeAsync(client.Client.Code, "not-the-secret");
        }

        // Without the reset, these eight failures would have crossed the limit.
        Assert.Equal(
            HttpStatusCode.OK,
            (await ExchangeAsync(client.Client.Code, client.Secret)).StatusCode);
    }

    private Task<HttpResponseMessage> ExchangeAsync(string clientCode, string? clientSecret) =>
        fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode, clientSecret });

    private HttpClient Operator() =>
        PlatformOperator(fixture, PlatformPermissions.PartnersManage, PlatformPermissions.OrganizationsCreate);

    private static string UniquePartnerCode() => "PTR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string UniqueClientCode() => "PTRC-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private async Task<Guid> CreateRootOrganizationAsync()
    {
        var response = await Operator().PostAsJsonAsync(
            "/api/v1/organizations",
            new { name = "Reseller " + Guid.NewGuid().ToString("N")[..6], code = UniquePartnerCode() });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<PartnerResult> RegisterPartnerAsync(Guid organizationId)
    {
        var response = await Operator().PostAsJsonAsync(
            "/api/v1/partners",
            new
            {
                rootOrganizationId = organizationId,
                code = UniquePartnerCode(),
                displayName = "Reseller",
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PartnerResult>(JsonOptions))!;
    }

    private async Task<(PartnerResult Partner, RegisteredPartnerApiClientResult Client)>
        RegisterPartnerAndClientAsync()
    {
        var organizationId = await CreateRootOrganizationAsync();
        var partner = await RegisterPartnerAsync(organizationId);

        var response = await Operator().PostAsJsonAsync(
            $"/api/v1/partners/{partner.Id}/clients",
            new { code = UniqueClientCode(), displayName = "Production key" });
        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var client = (await response.Content
            .ReadFromJsonAsync<RegisteredPartnerApiClientResult>(JsonOptions))!;
        return (partner, client);
    }

    private async Task<PartnerAccessTokenResult> AuthenticateAsync(
        RegisteredPartnerApiClientResult client)
    {
        var response = await fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/partners/auth/token",
            new { clientCode = client.Client.Code, clientSecret = client.Secret });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PartnerAccessTokenResult>(JsonOptions))!;
    }

    /// <summary>
    /// Renders a problem response without its per-request correlation identifier,
    /// which is expected to differ. What must not differ is anything that would
    /// reveal which credential was wrong, or that a key was killed.
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
}
