[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$metricsPath = Join-Path $repoRoot 'src\GiftCardPlatform.Api\PlatformMetrics.cs'
$rulesPath = Join-Path $repoRoot 'monitoring\open-giftcard-alerts.yml'
$configurationPath = Join-Path $repoRoot 'src\GiftCardPlatform.Api\ObservabilityConfiguration.cs'

foreach ($requiredPath in @($metricsPath, $rulesPath, $configurationPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing observability contract file '$requiredPath'."
    }
}

$metrics = Get-Content -LiteralPath $metricsPath -Raw
$rules = Get-Content -LiteralPath $rulesPath -Raw
$configuration = Get-Content -LiteralPath $configurationPath -Raw

$requiredInstruments = @(
    'open_giftcard_http_server_requests',
    'open_giftcard_http_server_duration',
    'open_giftcard_worker_runs',
    'open_giftcard_worker_items',
    'open_giftcard_audit_verification_failures',
    'open_giftcard_audit_verification_failure',
    'open_giftcard_readiness'
)
foreach ($instrument in $requiredInstruments) {
    if (-not $metrics.Contains('"' + $instrument + '"')) {
        throw "PlatformMetrics does not define required instrument '$instrument'."
    }
}

$requiredAlerts = @(
    'OpenGiftcardBackendTelemetryMissing',
    'OpenGiftcardBackendNotReady',
    'OpenGiftcardBackendHighErrorRate',
    'OpenGiftcardBackendHighLatency',
    'OpenGiftcardWorkerRepeatedFailures',
    'OpenGiftcardAuditVerificationFailure'
)
foreach ($alert in $requiredAlerts) {
    $matches = [regex]::Matches(
        $rules,
        '(?m)^\s*- alert:\s*' + [regex]::Escape($alert) + '\s*$')
    if ($matches.Count -ne 1) {
        throw "Alert '$alert' must appear exactly once."
    }
}

foreach ($expression in @(
    'absent_over_time(open_giftcard_readiness[2m])',
    'open_giftcard_http_server_requests_total',
    'open_giftcard_http_server_duration_seconds_bucket',
    'open_giftcard_worker_runs_total',
    'max(open_giftcard_audit_verification_failure)')) {
    if (-not $rules.Contains($expression)) {
        throw "Alert rules do not exercise required metric expression '$expression'."
    }
}

foreach ($requiredConfiguration in @(
    'OtlpExportProtocol.HttpProtobuf',
    'deployment.environment.name',
    'open-giftcard-backend',
    'endpoint.IsLoopback')) {
    if (-not $configuration.Contains($requiredConfiguration)) {
        throw "Metrics export does not enforce '$requiredConfiguration'."
    }
}

foreach ($forbiddenDimension in @(
    'tenant_id',
    'organization_id',
    'gift_card_id',
    'user_id',
    'email')) {
    if ($metrics.Contains('"' + $forbiddenDimension + '"')) {
        throw "Metric dimension '$forbiddenDimension' is forbidden."
    }
}

Write-Host "Observability contract verified with $($requiredInstruments.Count) instruments and $($requiredAlerts.Count) release-critical alerts."
