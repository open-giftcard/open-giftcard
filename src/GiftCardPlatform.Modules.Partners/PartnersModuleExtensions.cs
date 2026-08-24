using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using System.Globalization;
using GiftCardPlatform.Modules.Partners.Application;
using GiftCardPlatform.Modules.Partners.Contracts;
using GiftCardPlatform.Modules.Partners.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Partners;

public static class PartnersModuleExtensions
{
    public static IServiceCollection AddPartnersModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<PartnersOptions>()
            .Configure(options =>
            {
                if (configuration is null)
                {
                    return;
                }

                var section = configuration.GetSection(PartnersOptions.SectionName);
                if (int.TryParse(section[nameof(options.AccessTokenMinutes)], out var accessTokenMinutes))
                {
                    options.AccessTokenMinutes = accessTokenMinutes;
                }

                if (int.TryParse(
                        section[nameof(options.CredentialFailureLimit)],
                        out var failureLimit))
                {
                    options.CredentialFailureLimit = failureLimit;
                }

                if (int.TryParse(
                        section[nameof(options.CredentialFailureWindowSeconds)],
                        out var failureWindow))
                {
                    options.CredentialFailureWindowSeconds = failureWindow;
                }

                if (int.TryParse(
                        section.GetSection("MintRateLimit")["PermitLimit"],
                        out var mintPermitLimit))
                {
                    options.MintPermitLimit = mintPermitLimit;
                }

                if (int.TryParse(
                        section.GetSection("MintRateLimit")["WindowSeconds"],
                        out var mintWindowSeconds))
                {
                    options.MintWindowSeconds = mintWindowSeconds;
                }

                options.ClaimBaseUrl = section[nameof(options.ClaimBaseUrl)] ?? options.ClaimBaseUrl;
                if (int.TryParse(
                        section[nameof(options.OrphanClaimLifetimeDays)],
                        out var orphanClaimLifetimeDays))
                {
                    options.OrphanClaimLifetimeDays = orphanClaimLifetimeDays;
                }

                if (decimal.TryParse(
                        section[nameof(options.MaximumEpinAmount)],
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var maximumEpinAmount))
                {
                    options.MaximumEpinAmount = maximumEpinAmount;
                }

                options.EpinDeliveryKey =
                    section[nameof(options.EpinDeliveryKey)] ?? options.EpinDeliveryKey;
            })
            .Validate(
                options => options.AccessTokenMinutes is >= 1 and <= 15,
                "Partners:AccessTokenMinutes must be between 1 and 15.")
            .Validate(
                options => options.CredentialFailureLimit is >= 1 and <= 50,
                "Partners:CredentialFailureLimit must be between 1 and 50.")
            .Validate(
                options => options.CredentialFailureWindowSeconds is >= 10 and <= 3600,
                "Partners:CredentialFailureWindowSeconds must be between 10 and 3600.")
            .Validate(
                options => options.MintPermitLimit is >= 1 and <= 100_000,
                "Partners:MintRateLimit:PermitLimit must be between 1 and 100000.")
            .Validate(
                options => options.MintWindowSeconds is >= 1 and <= 3600,
                "Partners:MintRateLimit:WindowSeconds must be between 1 and 3600.")
            .Validate(
                options => Uri.TryCreate(options.ClaimBaseUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https",
                "Partners:ClaimBaseUrl must be an absolute HTTP or HTTPS URL.")
            .Validate(
                options => options.OrphanClaimLifetimeDays is >= 1 and <= 730,
                "Partners:OrphanClaimLifetimeDays must be between 1 and 730.")
            .Validate(
                options => options.MaximumEpinAmount is > 0m and <= 1_000_000m,
                "Partners:MaximumEpinAmount must be greater than zero and no more than 1000000.")
            .Validate(
                options => IsValidEpinDeliveryKey(options.EpinDeliveryKey),
                "Partners:EpinDeliveryKey must be a Base64-encoded 256-bit secret.")
            .ValidateOnStart();

        services.AddOptions<PartnerTokenSigningOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(PartnerTokenSigningOptions.SectionName);
                if (section is null)
                {
                    return;
                }

                options.Issuer = section[nameof(options.Issuer)] ?? options.Issuer;
                options.Audience = section[nameof(options.Audience)] ?? options.Audience;
                options.SigningKey = section[nameof(options.SigningKey)] ?? options.SigningKey;
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SigningKey),
                "Authentication:Jwt:SigningKey is required to issue partner access tokens.")
            // An empty issuer or audience produces a token the API's own bearer
            // validation rejects, which would surface to a reseller as an
            // unexplained 401 rather than as a configuration error here.
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer) &&
                    !string.IsNullOrWhiteSpace(options.Audience),
                "Authentication:Jwt:Issuer and Authentication:Jwt:Audience are required "
                + "to issue partner access tokens.")
            .ValidateOnStart();

        services.AddDbContext<PartnersDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    PartnersDbContext.MigrationsHistoryTable,
                    PartnersDbContext.Schema)));

        // Singleton: the failure window must be shared across requests, which
        // is the whole point of counting per client rather than per request.
        services.AddSingleton<IPartnerCredentialThrottle, PartnerCredentialThrottle>();
        services.AddScoped<IPartnerMintQuota, PartnerMintQuota>();
        services.AddScoped<IPartnerRegistrationService, PartnerRegistrationService>();
        services.AddScoped<PartnerAuthenticationService>();
        services.AddScoped<IPartnerAuthenticationService>(provider =>
            provider.GetRequiredService<PartnerAuthenticationService>());
        services.AddScoped<IPartnerPrincipalResolver>(provider =>
            provider.GetRequiredService<PartnerAuthenticationService>());

        return services;
    }

    private static bool IsValidEpinDeliveryKey(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static async Task MigratePartnersModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PartnersDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingPartnersMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<PartnersDbContext>(cancellationToken);
}
