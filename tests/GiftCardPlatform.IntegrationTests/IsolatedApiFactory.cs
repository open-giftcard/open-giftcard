using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GiftCardPlatform.IntegrationTests;

/// <summary>
/// Hosts a standalone API probe with its own durable Data Protection key ring.
/// Tests that boot Staging or Production must satisfy the same key-custody gate
/// as a deployment, without sharing key material or leaving it behind.
/// </summary>
internal sealed class IsolatedApiFactory(
    Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
{
    private readonly string keysPath = Path.Combine(
        Path.GetTempPath(),
        "giftcard-platform-tests",
        "isolated-api-keys",
        Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("DataProtection:KeysPath", keysPath);
        configure(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(keysPath))
        {
            Directory.Delete(keysPath, recursive: true);
        }
    }
}
