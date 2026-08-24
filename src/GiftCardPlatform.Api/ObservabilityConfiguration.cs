using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace GiftCardPlatform.Api;

internal sealed class MetricsExportOptions
{
    public const string SectionName = "Observability:Metrics";

    public bool Enabled { get; init; }

    public string OtlpEndpoint { get; init; } = string.Empty;

    public int ExportIntervalSeconds { get; init; } = 15;
}

internal static class ObservabilityConfiguration
{
    public static void Configure(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton<PlatformMetrics>();

        var settings = configuration
            .GetSection(MetricsExportOptions.SectionName)
            .Get<MetricsExportOptions>() ?? new MetricsExportOptions();
        if (!settings.Enabled)
        {
            return;
        }

        var endpoint = ResolveEndpoint(settings, environment);
        services.Configure<MetricReaderOptions>(options =>
        {
            options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                checked(settings.ExportIntervalSeconds * 1000);
            options.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds =
                Math.Min(10_000, checked(settings.ExportIntervalSeconds * 1000));
        });
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService("open-giftcard-backend")
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment.name",
                        environment.EnvironmentName),
                ]))
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, endpoint)
            .WithMetrics(metrics => metrics.AddMeter(PlatformMetrics.MeterName));
    }

    internal static Uri ResolveEndpoint(
        MetricsExportOptions settings,
        IHostEnvironment environment)
    {
        if (settings.ExportIntervalSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException(
                "Observability:Metrics:ExportIntervalSeconds must be between 5 and 300.");
        }
        if (!Uri.TryCreate(settings.OtlpEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "Observability:Metrics:OtlpEndpoint must be an absolute HTTP(S) base URL without credentials, query, or fragment.");
        }
        if (!environment.IsDevelopment() &&
            endpoint.Scheme != Uri.UriSchemeHttps &&
            !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                "Observability:Metrics:OtlpEndpoint must use HTTPS outside Development unless the collector is on loopback.");
        }

        return endpoint;
    }
}
