using System.Security.Cryptography;
using GiftCardPlatform.Api;
using GiftCardPlatform.Api.Services;
using GiftCardPlatform.Modules.Notifications.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GiftCardPlatform.IntegrationTests;

public sealed class DataProtectionConfigurationTests
{
    [Fact]
    public void Non_development_host_refuses_to_start_without_a_durable_key_path()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            webHost.UseEnvironment("Production");
            webHost.UseSetting(
                "ConnectionStrings:Default",
                "Host=localhost;Database=unused;Username=unused;Password=unused");
            webHost.UseSetting(
                "Authentication:Jwt:SigningKey",
                "test-only-signing-key-that-is-at-least-thirty-two-bytes");
            webHost.UseSetting(
                "Partners:EpinDeliveryKey",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });

        var exception = Assert.Throws<InvalidOperationException>(() => _ = factory.Services);

        Assert.Contains("DataProtection:KeysPath is required", exception.Message);
    }

    [Fact]
    public void Non_development_requires_a_durable_key_path()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestHostEnvironment("Production", directory.Path);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionConfiguration.ResolveKeysPath(configuration, environment));

        Assert.Contains("DataProtection:KeysPath is required", exception.Message);
    }

    [Fact]
    public void Development_uses_a_repository_local_key_ring_by_default()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder().Build();
        var environment = new TestHostEnvironment("Development", directory.Path);

        var resolved = DataProtectionConfiguration.ResolveKeysPath(
            configuration,
            environment);

        Assert.Equal(
            System.IO.Path.Combine(directory.Path, ".local", "dataprotection-keys"),
            resolved);
    }

    [Fact]
    public void Protected_notification_payload_survives_a_provider_restart()
    {
        using var directory = new TemporaryDirectory();
        var keysPath = System.IO.Path.Combine(directory.Path, "shared-keys");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeysPath"] = keysPath,
            })
            .Build();
        var environment = new TestHostEnvironment("Production", directory.Path);

        string protectedValue;
        using (var firstHost = BuildProvider(configuration, environment))
        {
            var protector = firstHost.GetRequiredService<INotificationPayloadProtector>();
            protectedValue = protector.Protect("https://card.example/activate/one-time-token");
        }

        using (var restartedHost = BuildProvider(configuration, environment))
        {
            var protector = restartedHost.GetRequiredService<INotificationPayloadProtector>();
            Assert.Equal(
                "https://card.example/activate/one-time-token",
                protector.TryUnprotect(protectedValue));
        }

        Assert.NotEmpty(Directory.GetFiles(keysPath, "*.xml"));
    }

    private static ServiceProvider BuildProvider(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var services = new ServiceCollection();
        DataProtectionConfiguration.Configure(services, configuration, environment);
        services.AddSingleton<INotificationPayloadProtector>(serviceProvider =>
            new DataProtectionNotificationProtector(
                serviceProvider.GetRequiredService<IDataProtectionProvider>()));
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(
        string environmentName,
        string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "GiftCardPlatform.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "giftcard-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
