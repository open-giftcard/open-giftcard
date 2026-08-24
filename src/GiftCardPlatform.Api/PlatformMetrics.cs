using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GiftCardPlatform.Api;

internal sealed class PlatformMetrics : IDisposable
{
    public const string MeterName = "OpenGiftcard.Api";

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> httpRequests;
    private readonly Histogram<double> httpDuration;
    private readonly Counter<long> workerRuns;
    private readonly Counter<long> workerItems;
    private readonly Counter<long> auditVerificationFailures;
    private int ready;
    private int auditVerificationFailed;

    public PlatformMetrics()
    {
        httpRequests = meter.CreateCounter<long>(
            "open_giftcard_http_server_requests",
            description: "Completed non-probe HTTP requests.");
        httpDuration = meter.CreateHistogram<double>(
            "open_giftcard_http_server_duration",
            unit: "s",
            description: "Duration of completed non-probe HTTP requests.");
        workerRuns = meter.CreateCounter<long>(
            "open_giftcard_worker_runs",
            description: "Background worker loop outcomes.");
        workerItems = meter.CreateCounter<long>(
            "open_giftcard_worker_items",
            description: "Items processed by background workers by outcome.");
        auditVerificationFailures = meter.CreateCounter<long>(
            "open_giftcard_audit_verification_failures",
            description: "Audit checkpoint verification failures.");
        meter.CreateObservableGauge(
            "open_giftcard_audit_verification_failure",
            () => Volatile.Read(ref auditVerificationFailed),
            description: "Whether this process has observed an audit verification failure.");
        meter.CreateObservableGauge(
            "open_giftcard_readiness",
            () => Volatile.Read(ref ready),
            description: "Whether this API instance most recently passed readiness.");
    }

    public void RecordHttpRequest(
        string method,
        string route,
        int statusCode,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "method", method },
            { "route", route },
            { "status_code", statusCode },
            { "status_class", $"{statusCode / 100}xx" },
        };
        httpRequests.Add(1, tags);
        httpDuration.Record(duration.TotalSeconds, tags);
    }

    public void SetReadiness(bool isReady) =>
        Volatile.Write(ref ready, isReady ? 1 : 0);

    public void RecordWorkerRun(string worker, string outcome) =>
        workerRuns.Add(
            1,
            new KeyValuePair<string, object?>("worker", worker),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordWorkerItems(string worker, string outcome, long count)
    {
        if (count <= 0)
        {
            return;
        }

        workerItems.Add(
            count,
            new KeyValuePair<string, object?>("worker", worker),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    public void RecordAuditVerificationFailure()
    {
        auditVerificationFailures.Add(1);
        Volatile.Write(ref auditVerificationFailed, 1);
    }

    public void Dispose()
    {
        meter.Dispose();
    }
}
