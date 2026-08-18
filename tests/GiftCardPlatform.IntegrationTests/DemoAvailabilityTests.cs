using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GiftCardPlatform.Modules.Authorization.Contracts;
using GiftCardPlatform.Modules.Organizations.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// The development-only demo UI must be reachable in Development and absent
/// everywhere else. These tests need no database: serving the page (200) and
/// leaving the route unmapped (404) never touch PostgreSQL, so a dummy
/// connection string is enough to build the host.
/// </summary>
public sealed class DemoAvailabilityTests
{
    // Syntactically valid but never connected to — the demo route does not open it.
    private const string DummyConnection =
        "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused";
    private const string DummySigningKey =
        "test-only-signing-key-that-is-at-least-thirty-two-bytes";
    private static readonly string DummyEpinDeliveryKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static WebApplicationFactory<Program> FactoryFor(
        string environment,
        bool replaceApiServices = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment(environment);
            webHost.UseSetting("ConnectionStrings:Default", DummyConnection);
            webHost.UseSetting("Authentication:Jwt:SigningKey", DummySigningKey);
            // Partners validates this secret on every host startup, even when
            // the test exercises only route availability and never mints.
            // Keep the standalone factory production-shaped without sharing a
            // real delivery key or weakening startup validation.
            webHost.UseSetting("Partners:EpinDeliveryKey", DummyEpinDeliveryKey);
            // These route/authentication probes intentionally use a dummy
            // database. Disable database-backed workers so they neither add
            // retry delays nor obscure the assertions with expected connection
            // failures while the host is alive.
            webHost.UseSetting("GiftCards:Expiration:Enabled", "false");
            webHost.UseSetting("Distribution:BulkBatches:Enabled", "false");
            webHost.UseSetting("Sharing:ExpirationEnabled", "false");
            webHost.UseSetting("Payments:Provisions:ExpirationEnabled", "false");
            webHost.UseSetting("Notifications:DispatchEnabled", "false");
            if (replaceApiServices)
            {
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<IPlatformPermissionResolver>();
                    services.RemoveAll<IOrganizationService>();
                    services.AddSingleton<IPlatformPermissionResolver>(
                        new StubPlatformPermissionResolver());
                    services.AddSingleton<IOrganizationService>(
                        new StubOrganizationService());
                });
            }
        });

    [Fact]
    public async Task Demo_is_available_in_development()
    {
        using var factory = FactoryFor("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/demo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Gift Card Platform Console", body, StringComparison.Ordinal);
        Assert.Contains("data-workflow=\"bootstrap\"", body, StringComparison.Ordinal);
        Assert.Contains("data-workflow=\"login\"", body, StringComparison.Ordinal);
        Assert.Contains("data-workspace=\"platform\"", body, StringComparison.Ordinal);
        Assert.Contains("data-workspace=\"organization\"", body, StringComparison.Ordinal);
        Assert.Contains("headers.Authorization = \"Bearer \"", body, StringComparison.Ordinal);
        Assert.Contains("headers[\"X-Organization-Id\"]", body, StringComparison.Ordinal);
        Assert.Contains("sessionStorage", body, StringComparison.Ordinal);
        Assert.Contains("issueGiftCardForm", body, StringComparison.Ordinal);
        Assert.Contains("giftCardInventoryList", body, StringComparison.Ordinal);
        Assert.Contains("loginIdentifier", body, StringComparison.Ordinal);
        Assert.Contains("distributeGiftCardForm", body, StringComparison.Ordinal);
        Assert.Contains("claimGiftCardForm", body, StringComparison.Ordinal);
        Assert.Contains("claimDeliveryList", body, StringComparison.Ordinal);
        Assert.Contains("bulkGiftCardBatchForm", body, StringComparison.Ordinal);
        Assert.Contains("bulkGiftCardBatchList", body, StringComparison.Ordinal);
        Assert.Contains(
            "/gift-card-batches/",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every card, Ledger posting, invitation, audit row",
            body,
            StringComparison.Ordinal);
        Assert.Contains("organization.gift_cards.distribute", body, StringComparison.Ordinal);
        Assert.Contains("giftCardLifecycleForm", body, StringComparison.Ordinal);
        Assert.Contains("ownedGiftCardLifecycleResult", body, StringComparison.Ordinal);
        Assert.Contains(
            "organization.gift_cards.lifecycle.manage",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "/api/v1/me/gift-cards/",
            body,
            StringComparison.Ordinal);
        Assert.Contains("financialSummaryList", body, StringComparison.Ordinal);
        Assert.Contains("financialHistoryList", body, StringComparison.Ordinal);
        Assert.Contains("financialReconciliationList", body, StringComparison.Ordinal);
        Assert.Contains("myGiftCardList", body, StringComparison.Ordinal);
        Assert.Contains("myGiftCardFinancialHistoryList", body, StringComparison.Ordinal);
        Assert.Contains("createProtectedShareForm", body, StringComparison.Ordinal);
        Assert.Contains("claimProtectedShareForm", body, StringComparison.Ordinal);
        Assert.Contains("createDirectShareForm", body, StringComparison.Ordinal);
        Assert.Contains("claimDirectShareForm", body, StringComparison.Ordinal);
        Assert.Contains("for=\"directShareRecipientContact\"", body, StringComparison.Ordinal);
        Assert.Contains("for=\"directShareClaimToken\"", body, StringComparison.Ordinal);
        Assert.Contains("for=\"myShareDirection\"", body, StringComparison.Ordinal);
        Assert.Contains("for=\"myShareKind\"", body, StringComparison.Ordinal);
        Assert.Contains("for=\"myShareState\"", body, StringComparison.Ordinal);
        Assert.Contains("active reservation(s)", body, StringComparison.Ordinal);
        Assert.Contains("sharing, transfer", body, StringComparison.Ordinal);
        Assert.Contains("name=\"viewport\"", body, StringComparison.Ordinal);
        Assert.Contains("myShareList", body, StringComparison.Ordinal);
        Assert.Contains("/api/v1/share-claims", body, StringComparison.Ordinal);
        Assert.Contains("/api/v1/share-invitation-claims", body, StringComparison.Ordinal);
        Assert.Contains(
            "/reports/financial-summary",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "/reports/financial-history",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "/reports/reconciliation",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "/api/v1/organizations/{id}/audit-records",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "Read-only reconciliation",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("X-Dev-", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Demo_is_not_available_outside_development(string environment)
    {
        using var factory = FactoryFor(environment);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/demo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Caller_selected_development_headers_cannot_authenticate()
    {
        using var factory = FactoryFor("Development");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User-Id", Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(
            "X-Dev-Platform-Permissions",
            PlatformPermissions.OrganizationsView);

        var response = await client.GetAsync(
            $"/api/v1/organizations/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Signed_bearer_authentication_is_available_in_every_environment(
        string environment)
    {
        using var factory = FactoryFor(environment, replaceApiServices: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateAccessToken());

        var response = await client.GetAsync(
            $"/api/v1/organizations/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string CreateAccessToken()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "GiftCardPlatform",
            audience: "GiftCardPlatform.Api",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, Guid.CreateVersion7().ToString()),
                new Claim(JwtRegisteredClaimNames.Sid, Guid.CreateVersion7().ToString()),
            ],
            notBefore: now.AddMinutes(-1).UtcDateTime,
            expires: now.AddMinutes(15).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DummySigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StubPlatformPermissionResolver : IPlatformPermissionResolver
    {
        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
            Guid userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(
                    [PlatformPermissions.OrganizationsView],
                    StringComparer.Ordinal));
    }

    private sealed class StubOrganizationService : IOrganizationService
    {
        public Task<OrganizationResult> CreateRootOrganizationAsync(
            CreateRootOrganizationRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OrganizationResult> GetOrganizationAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new OrganizationResult(
                    id,
                    "Authentication Probe",
                    "AUTH-PROBE",
                    "Active",
                    0,
                    DateTimeOffset.UtcNow));
    }
}
