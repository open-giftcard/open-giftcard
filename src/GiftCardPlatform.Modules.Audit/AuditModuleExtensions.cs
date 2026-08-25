using GiftCardPlatform.BuildingBlocks;
using GiftCardPlatform.BuildingBlocks.Persistence;
using GiftCardPlatform.Modules.Audit.Application;
using GiftCardPlatform.Modules.Audit.Contracts;
using GiftCardPlatform.Modules.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GiftCardPlatform.Modules.Audit;

public static class AuditModuleExtensions
{
    /// <summary>
    /// Registers the Audit module. Only <see cref="IAuditRecorder"/> is visible
    /// outside the module; the DbContext and entities stay internal.
    /// </summary>
    public static IServiceCollection AddAuditModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<AuditDbContext>((sp, options) =>
            options.UseNpgsql(
                sp.GetRequiredService<ScopedDatabaseConnection>().Connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    AuditDbContext.MigrationsHistoryTable,
                    AuditDbContext.Schema)));

        services.AddScoped<IAuditRecorder, AuditRecorder>();
        services.AddScoped<IAuditInvestigationQuery, AuditInvestigationQuery>();
        services.AddScoped<IAuditCheckpointProcessor, AuditCheckpointProcessor>();
        services.AddSingleton<IAuditCheckpointSigner, DisabledAuditCheckpointSigner>();
        services.AddSingleton<IAuditCheckpointWitness, DisabledAuditCheckpointWitness>();
        services.AddOptions<AuditCheckpointOptions>()
            .Configure(options =>
            {
                var section = configuration?.GetSection(AuditCheckpointOptions.SectionName);
                if (section is null)
                {
                    return;
                }

                options.Enabled = bool.TryParse(
                    section[nameof(options.Enabled)],
                    out var enabled) && enabled;
                options.Provider = section[nameof(options.Provider)];
                if (int.TryParse(
                        section[nameof(options.PollIntervalSeconds)],
                        out var pollIntervalSeconds))
                {
                    options.PollIntervalSeconds = pollIntervalSeconds;
                }

                if (int.TryParse(section[nameof(options.BatchSize)], out var batchSize))
                {
                    options.BatchSize = batchSize;
                }

                options.DevelopmentSigningKeyPath =
                    section[nameof(options.DevelopmentSigningKeyPath)];
                options.DevelopmentWitnessDirectory =
                    section[nameof(options.DevelopmentWitnessDirectory)];
                options.RemoteSignerEndpoint =
                    section[nameof(options.RemoteSignerEndpoint)];
                options.RemoteSignerKeyId =
                    section[nameof(options.RemoteSignerKeyId)];
                options.RemoteWitnessBaseUrl =
                    section[nameof(options.RemoteWitnessBaseUrl)];
                options.RemoteClientCertificatePath =
                    section[nameof(options.RemoteClientCertificatePath)];
                options.RemoteClientCertificatePassword =
                    section[nameof(options.RemoteClientCertificatePassword)];
                options.RemoteClientCertificateThumbprint =
                    section[nameof(options.RemoteClientCertificateThumbprint)];
                if (int.TryParse(
                        section[nameof(options.RemoteTimeoutSeconds)],
                        out var remoteTimeoutSeconds))
                {
                    options.RemoteTimeoutSeconds = remoteTimeoutSeconds;
                }
            })
            .Validate(
                options => options.PollIntervalSeconds is >= 30 and <= 86_400,
                "Audit:Checkpoints:PollIntervalSeconds must be between 30 and 86400.")
            .Validate(
                options => options.BatchSize is >= 1 and <= 10_000,
                "Audit:Checkpoints:BatchSize must be between 1 and 10000.")
            .Validate(
                options => options.RemoteTimeoutSeconds is >= 1 and <= 120,
                "Audit:Checkpoints:RemoteTimeoutSeconds must be between 1 and 120.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>Applies this module's migrations. Intended for local development and tests.</summary>
    public static async Task MigrateAuditModuleAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Migrations this build declares that the database has not recorded.
    /// Empty means the schema is at or ahead of what this build expects.
    /// </summary>
    public static Task<IReadOnlyCollection<string>> GetPendingAuditMigrationsAsync(
        this IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default) =>
        serviceProvider.GetPendingMigrationsAsync<AuditDbContext>(cancellationToken);
}
