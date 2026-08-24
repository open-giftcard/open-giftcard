# Backend observability contract

The backend emits OpenTelemetry metrics through stable OTLP HTTP/protobuf. It
does not expose an unauthenticated public scrape endpoint. Point it at an
operator-owned collector:

```text
Observability__Metrics__Enabled=true
Observability__Metrics__OtlpEndpoint=https://collector.<domain>
Observability__Metrics__ExportIntervalSeconds=15
```

`OtlpEndpoint` is a base URL. The OpenTelemetry exporter adds the metrics signal
path. Outside Development it must use HTTPS unless the collector is on the same
host through loopback. Put collector authentication and routing at the network
or workload-identity boundary. Do not put credentials in the endpoint URL.

The collector and metrics store must:

1. retain `deployment.environment.name` and expose it to PromQL as
   `deployment_environment_name`;
2. use the default OpenTelemetry-to-Prometheus suffix translation, including
   `_total` for counters and `_seconds` for the HTTP duration histogram;
3. keep each API instance as a distinct metric stream;
4. load `open-giftcard-alerts.yml` into a Prometheus-compatible rule engine;
5. route `critical` alerts to the incident owner and `warning` alerts to the
   operational review channel.

Run this rule group in an environment-isolated metrics tenant or ruler scope.
Its missing-telemetry rule deliberately fails closed when that environment has
no readiness stream. Do not load the rules into an unscoped view that mixes
staging and production; one environment could then hide another's missing
telemetry and the rule-level state would not certify the named deployment.

The application metrics use only bounded operational labels: HTTP method,
route template, status code/class, worker name, and outcome. They never label by
tenant, organization, user, card, email, raw path, token, or credential.

After the artifact-bound smoke test has generated traffic on every API replica,
run the observability gate from an operator host with access to the metrics API:

```powershell
$env:OPEN_GIFTCARD_OBSERVABILITY_TOKEN = '<read-only metrics API token>'
./scripts/Test-OpenGiftCardObservability.ps1 `
  -MetricsBaseUrl 'https://metrics.<domain>' `
  -EnvironmentName 'staging-rc1' `
  -ArtifactManifestPath '<verified-download>\ARTIFACTS.json' `
  -ExpectedBackendInstances 2 `
  -EvidencePath '<new-evidence-directory>\observability.json'
Remove-Item Env:OPEN_GIFTCARD_OBSERVABILITY_TOKEN
```

The gate refuses insecure remote metrics APIs, rehearsal artifacts, missing
replica streams, failed readiness, missing traffic/worker metrics, missing or
unhealthy rules, and any firing release-critical rule. It writes a redacted
JSON record and SHA-256 sidecar without the bearer token.
