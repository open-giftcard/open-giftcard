using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Payments.Application;
using GiftCardPlatform.Modules.Payments.Contracts;
using GiftCardPlatform.Modules.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Payments;

public static class PaymentsModuleExtensions
{
    public static IServiceCollection AddPaymentsModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<PaymentTokenOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(PaymentTokenOptions.SectionName);
                if (section is not null &&
                    int.TryParse(section[nameof(options.LifetimeSeconds)], out var lifetime))
                {
                    options.LifetimeSeconds = lifetime;
                }
            })
            // ADR-017 fixes the TTL at 60 seconds. Validated on start so a
            // configuration change cannot silently widen the replay window.
            .Validate(
                options => options.LifetimeSeconds == 60,
                "Payments:Tokens:LifetimeSeconds must be 60.")
            .ValidateOnStart();

        services.AddDbContext<PaymentsDbContext>((serviceProvider, options) =>
            options.UseNpgsql(
                serviceProvider.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    PaymentsDbContext.MigrationsHistoryTable,
                    PaymentsDbContext.Schema)));
        services.AddOptions<PosAuthenticationOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(PosAuthenticationOptions.SectionName);
                if (section is not null &&
                    int.TryParse(section[nameof(options.AccessTokenMinutes)], out var minutes))
                {
                    options.AccessTokenMinutes = minutes;
                }
            })
            .Validate(
                options => options.AccessTokenMinutes is >= 1 and <= 60,
                "Payments:Pos:AccessTokenMinutes must be between 1 and 60.")
            .ValidateOnStart();

        services.AddOptions<PosTokenSigningOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(PosTokenSigningOptions.SectionName);
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
                "Authentication:Jwt:SigningKey is required to issue POS access tokens.")
            // An empty issuer or audience produces a token the API's own bearer
            // validation rejects, which would surface as an unexplained 401 at a
            // till rather than as a configuration error here.
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Issuer) &&
                    !string.IsNullOrWhiteSpace(options.Audience),
                "Authentication:Jwt:Issuer and Authentication:Jwt:Audience are required "
                + "to issue POS access tokens.")
            .ValidateOnStart();

        services.AddOptions<PaymentProvisionOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(PaymentProvisionOptions.SectionName);
                if (section is null)
                {
                    return;
                }

                if (int.TryParse(section[nameof(options.WindowSeconds)], out var window))
                {
                    options.WindowSeconds = window;
                }
                if (bool.TryParse(section[nameof(options.ExpirationEnabled)], out var enabled))
                {
                    options.ExpirationEnabled = enabled;
                }
                if (int.TryParse(
                        section[nameof(options.ExpirationPollIntervalSeconds)],
                        out var interval))
                {
                    options.ExpirationPollIntervalSeconds = interval;
                }
                if (int.TryParse(section[nameof(options.ExpirationBatchSize)], out var batch))
                {
                    options.ExpirationBatchSize = batch;
                }
            })
            // ADR-044 fixes the window at 2 minutes. Validated on start so an
            // environment cannot widen how long an abandoned till holds value.
            .Validate(
                options => options.WindowSeconds == 120,
                "Payments:Provisions:WindowSeconds must be 120.")
            .Validate(
                options => options.ExpirationPollIntervalSeconds is >= 5 and <= 3600,
                "Payments:Provisions:ExpirationPollIntervalSeconds must be between 5 and 3600.")
            .Validate(
                options => options.ExpirationBatchSize is >= 1 and <= 100,
                "Payments:Provisions:ExpirationBatchSize must be between 1 and 100.")
            .ValidateOnStart();

        services.AddScoped<IPaymentTokenService, PaymentTokenService>();
        services.AddScoped<PaymentProvisionService>();
        services.AddScoped<IPaymentProvisionService>(provider =>
            provider.GetRequiredService<PaymentProvisionService>());
        services.AddScoped<IPaymentProvisionExpirationProcessor>(provider =>
            provider.GetRequiredService<PaymentProvisionService>());
        services.AddScoped<IPaymentBalanceInquiryService>(provider =>
            provider.GetRequiredService<PaymentProvisionService>());
        services.AddScoped<IPaymentReservationQuery, PaymentReservationQuery>();
        services.AddScoped<IPosRegistrationService, PosRegistrationService>();
        services.AddScoped<IPosAuthenticationService, PosAuthenticationService>();
        return services;
    }

    public static async Task MigratePaymentsModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingPaymentsMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<PaymentsDbContext>(cancellationToken);
}
