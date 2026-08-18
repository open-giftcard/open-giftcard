using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// The demonstration seed writes real organizations, users, and money. Its only
/// protection from a deployment is that <c>Program</c> registers it on the
/// Development branch alone, exactly as it does for <c>/demo</c> and
/// <c>/swagger</c>. A refactor that moved the registration out of that branch
/// would leave no other guard, and nothing would notice.
///
/// These tests pin the registration itself rather than an observable behaviour,
/// because the seed has no route to probe. They deliberately use a dummy
/// database: the hosted service is never allowed to start here, so nothing
/// connects.
/// </summary>
public sealed class DemoSeedGatingTests
{
    private const string DummyConnection =
        "Host=localhost;Port=5432;Database=unused;Username=unused;Password=unused";
    private const string DummySigningKey =
        "test-only-signing-key-that-is-at-least-thirty-two-bytes";
    private static readonly string DummyEpinDeliveryKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static WebApplicationFactory<Program> FactoryFor(string environment, bool seedEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment(environment);
            webHost.UseSetting("ConnectionStrings:Default", DummyConnection);
            webHost.UseSetting("Authentication:Jwt:SigningKey", DummySigningKey);
            webHost.UseSetting("Partners:EpinDeliveryKey", DummyEpinDeliveryKey);
            webHost.UseSetting("Bootstrap:PlatformAdministrator:Secret", DummySigningKey);
            webHost.UseSetting("Demo:Seed:Enabled", seedEnabled ? "true" : "false");
            // Keep database-backed workers quiet against the dummy connection.
            webHost.UseSetting("GiftCards:Expiration:Enabled", "false");
            webHost.UseSetting("Distribution:BulkBatches:Enabled", "false");
            webHost.UseSetting("Sharing:ExpirationEnabled", "false");
            webHost.UseSetting("Payments:Provisions:ExpirationEnabled", "false");
            webHost.UseSetting("Notifications:DispatchEnabled", "false");
        });

    // The seed types are internal, matching the codebase convention of internal
    // implementations behind public contracts. Matching on the type name keeps
    // the test honest without widening visibility for a test's convenience.
    private const string HostedServiceTypeName = "DemoSeedHostedService";

    private static bool SeedIsRegistered(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetServices<IHostedService>()
            .Any(service => string.Equals(
                service.GetType().Name,
                HostedServiceTypeName,
                StringComparison.Ordinal));

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void Seed_is_not_registered_outside_development(string environment)
    {
        // Enabled on purpose. Configuration must not be able to reach it.
        using var factory = FactoryFor(environment, seedEnabled: true);

        Assert.False(
            SeedIsRegistered(factory),
            $"The demo seed hosted service is registered in {environment}. It writes real " +
            "organizations, users, and ledger entries, and Development-only registration is its " +
            "only guard.");
    }

    [Fact]
    public void Seed_is_registered_in_development()
    {
        using var factory = FactoryFor("Development", seedEnabled: true);

        Assert.True(SeedIsRegistered(factory));
    }

    [Fact]
    public void Seed_registration_does_not_depend_on_the_enabled_flag()
    {
        // Enabled is the second, independent gate: the service is registered in
        // Development either way and decides for itself whether to run. Pinning
        // this keeps the two gates from being collapsed into one.
        using var factory = FactoryFor("Development", seedEnabled: false);

        Assert.True(SeedIsRegistered(factory));
    }
}
