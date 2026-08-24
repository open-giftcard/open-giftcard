using Microsoft.AspNetCore.DataProtection;

namespace GiftCardPlatform.Api;

/// <summary>
/// Configures the key ring that protects credential-bearing notification
/// payloads.
///
/// Every API instance in one deployment must use the same durable directory.
/// Otherwise an instance restart or a request handled by another replica can
/// turn a deliverable activation message into an undecryptable dead letter.
/// </summary>
internal static class DataProtectionConfiguration
{
    internal const string ApplicationName = "GiftCardPlatform";

    internal static string Configure(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var keysPath = ResolveKeysPath(configuration, environment);
        Directory.CreateDirectory(keysPath);

        services
            .AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        return keysPath;
    }

    internal static string ResolveKeysPath(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration["DataProtection:KeysPath"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "DataProtection:KeysPath is required outside Development so " +
                    "notification keys survive restarts and are shared across instances.");
            }

            configured = Path.Combine(
                environment.ContentRootPath,
                ".local",
                "dataprotection-keys");
        }

        return Path.GetFullPath(configured, environment.ContentRootPath);
    }
}
