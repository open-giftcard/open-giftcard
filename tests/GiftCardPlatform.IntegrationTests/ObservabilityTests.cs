using System.Diagnostics.Metrics;
using GiftCardPlatform.Api;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GiftCardPlatform.IntegrationTests;

public sealed class ObservabilityTests
{
    [Theory]
    [InlineData("https://collector.example", "Production")]
    [InlineData("http://127.0.0.1:4318", "Production")]
    [InlineData("http://collector.internal:4318", "Development")]
    public void Metrics_export_accepts_supported_endpoints(
        string endpoint,
        string environmentName)
    {
        var options = new MetricsExportOptions
        {
            Enabled = true,
            OtlpEndpoint = endpoint,
            ExportIntervalSeconds = 15,
        };

        var resolved = ObservabilityConfiguration.ResolveEndpoint(
            options,
            new TestHostEnvironment(environmentName));

        Assert.Equal(endpoint, resolved.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://collector.internal:4318", "Production")]
    [InlineData("ftp://collector.example", "Production")]
    [InlineData("https://user:password@collector.example", "Production")]
    [InlineData("https://collector.example?token=secret", "Production")]
    public void Metrics_export_rejects_unsafe_endpoints(
        string endpoint,
        string environmentName)
    {
        var options = new MetricsExportOptions
        {
            Enabled = true,
            OtlpEndpoint = endpoint,
            ExportIntervalSeconds = 15,
        };

        Assert.Throws<InvalidOperationException>(() =>
            ObservabilityConfiguration.ResolveEndpoint(
                options,
                new TestHostEnvironment(environmentName)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public void Metrics_export_rejects_unbounded_intervals(int intervalSeconds)
    {
        var options = new MetricsExportOptions
        {
            Enabled = true,
            OtlpEndpoint = "https://collector.example",
            ExportIntervalSeconds = intervalSeconds,
        };

        Assert.Throws<InvalidOperationException>(() =>
            ObservabilityConfiguration.ResolveEndpoint(
                options,
                new TestHostEnvironment("Production")));
    }

    [Fact]
    public void Platform_metrics_emit_only_bounded_operational_dimensions()
    {
        var longMeasurements = new List<Measurement<long>>();
        var intMeasurements = new List<Measurement<int>>();
        var doubleMeasurements = new List<Measurement<double>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PlatformMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longMeasurements.Add(new Measurement<long>(
                instrument.Name,
                value,
                tags.ToArray())));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            intMeasurements.Add(new Measurement<int>(
                instrument.Name,
                value,
                tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubleMeasurements.Add(new Measurement<double>(
                instrument.Name,
                value,
                tags.ToArray())));
        listener.Start();

        using var metrics = new PlatformMetrics();
        metrics.RecordHttpRequest(
            "POST",
            "/api/v1/pos/payments/{paymentId}/confirm",
            201,
            TimeSpan.FromMilliseconds(125));
        metrics.RecordWorkerRun("notifications", "failed");
        metrics.RecordWorkerItems("notifications", "delivered", 2);
        metrics.RecordAuditVerificationFailure();
        metrics.SetReadiness(true);
        listener.RecordObservableInstruments();

        Assert.Contains(longMeasurements, item =>
            item.Name == "open_giftcard_http_server_requests" &&
            item.Value == 1 &&
            HasTag(item.Tags, "method", "POST") &&
            HasTag(item.Tags, "route", "/api/v1/pos/payments/{paymentId}/confirm") &&
            HasTag(item.Tags, "status_code", 201) &&
            HasTag(item.Tags, "status_class", "2xx"));
        Assert.Contains(doubleMeasurements, item =>
            item.Name == "open_giftcard_http_server_duration" &&
            item.Value == 0.125);
        Assert.Contains(longMeasurements, item =>
            item.Name == "open_giftcard_worker_runs" &&
            HasTag(item.Tags, "worker", "notifications") &&
            HasTag(item.Tags, "outcome", "failed"));
        Assert.Contains(longMeasurements, item =>
            item.Name == "open_giftcard_worker_items" && item.Value == 2);
        Assert.Contains(longMeasurements, item =>
            item.Name == "open_giftcard_audit_verification_failures" &&
            item.Value == 1);
        Assert.Contains(intMeasurements, item =>
            item.Name == "open_giftcard_audit_verification_failure" &&
            item.Value == 1);
        Assert.Contains(intMeasurements, item =>
            item.Name == "open_giftcard_readiness" && item.Value == 1);

        var tagNames = longMeasurements
            .SelectMany(item => item.Tags)
            .Concat(doubleMeasurements.SelectMany(item => item.Tags))
            .Concat(intMeasurements.SelectMany(item => item.Tags))
            .Select(tag => tag.Key)
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(tagNames, name =>
            name.Contains("tenant", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("organization", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("card", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTag(
        IReadOnlyCollection<KeyValuePair<string, object?>> tags,
        string name,
        object value) =>
        tags.Any(tag => tag.Key == name && Equals(tag.Value, value));

    private sealed record Measurement<T>(
        string Name,
        T Value,
        IReadOnlyCollection<KeyValuePair<string, object?>> Tags);

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "GiftCardPlatform.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
