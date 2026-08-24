[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MetricsBaseUrl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$')]
    [string]$EnvironmentName,

    [Parameter(Mandatory)]
    [string]$ArtifactManifestPath,

    [Parameter(Mandatory)]
    [string]$EvidencePath,

    [ValidateRange(1, 100)]
    [int]$ExpectedBackendInstances = 2,

    [string]$BearerToken = $env:OPEN_GIFTCARD_OBSERVABILITY_TOKEN,

    [switch]$AllowInsecureHttp
)

$ErrorActionPreference = 'Stop'
$encoding = [Text.UTF8Encoding]::new($false)

function Resolve-MetricsBaseUrl([string]$Value) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw 'MetricsBaseUrl must be an absolute HTTP(S) URL without credentials, query, or fragment.'
    }
    if ($uri.Scheme -ne 'https' -and
        -not ($AllowInsecureHttp -and $uri.IsLoopback)) {
        throw 'MetricsBaseUrl must use HTTPS. HTTP is allowed only for an explicit loopback test.'
    }

    return $Value.TrimEnd('/')
}

function Invoke-MetricsApi([string]$Path) {
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
        $headers.Authorization = "Bearer $BearerToken"
    }

    $response = Invoke-RestMethod `
        -Method Get `
        -Uri "$resolvedBaseUrl$Path" `
        -Headers $headers `
        -TimeoutSec 30
    if ([string]$response.status -ne 'success') {
        throw "Metrics API request '$Path' did not report success."
    }
    return $response
}

function Get-QueryValue([string]$Expression) {
    $encoded = [Uri]::EscapeDataString($Expression)
    $response = Invoke-MetricsApi "/api/v1/query?query=$encoded"
    $results = @($response.data.result)
    if ($results.Count -ne 1 -or @($results[0].value).Count -ne 2) {
        throw "Metrics query '$Expression' did not return one scalar vector result."
    }

    $parsed = 0.0
    if (-not [double]::TryParse(
            [string]$results[0].value[1],
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Metrics query '$Expression' returned a non-numeric value."
    }
    return $parsed
}

$resolvedBaseUrl = Resolve-MetricsBaseUrl $MetricsBaseUrl
$resolvedManifestPath = (Resolve-Path -LiteralPath $ArtifactManifestPath -ErrorAction Stop).Path
$resolvedEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
if (Test-Path -LiteralPath $resolvedEvidencePath) {
    throw "Evidence path already exists: $resolvedEvidencePath"
}
if (Test-Path -LiteralPath "$resolvedEvidencePath.sha256") {
    throw "Evidence checksum path already exists: $resolvedEvidencePath.sha256"
}

$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
if ([bool]$manifest.rehearsal) {
    throw 'A rehearsal artifact set cannot certify observability.'
}
$artifacts = @($manifest.artifacts)
$componentNames = @($artifacts | ForEach-Object { [string]$_.component } | Sort-Object -Unique)
if ($artifacts.Count -ne 4 -or
    $componentNames.Count -ne 4 -or
    @($componentNames | Where-Object { $_ -notin @('backend', 'portal', 'cardholder', 'pos') }).Count -ne 0) {
    throw 'ARTIFACTS.json must contain the exact four Open Giftcard components.'
}
foreach ($artifact in $artifacts) {
    if ([string]$artifact.file -notmatch (
            '^open-giftcard-' + [regex]::Escape([string]$artifact.component) +
            '-v.+\.zip$') -or
        [string]$artifact.sha256 -notmatch '^[0-9A-F]{64}$') {
        throw "ARTIFACTS.json contains invalid metadata for '$($artifact.component)'."
    }
}
if ([string]$manifest.release -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+-rc\.[0-9]+$') {
    throw "ARTIFACTS.json carries invalid release '$($manifest.release)'."
}
$manifestHash = (Get-FileHash -LiteralPath $resolvedManifestPath -Algorithm SHA256).Hash
$environmentSelector = "deployment_environment_name=`"$EnvironmentName`""

$queries = [ordered]@{
    backendMetricStreams = "count(open_giftcard_readiness{$environmentSelector})"
    minimumReadiness = "min(open_giftcard_readiness{$environmentSelector})"
    httpMetricStreams = "count(open_giftcard_http_server_requests_total{$environmentSelector})"
    workerMetricStreams = "count(open_giftcard_worker_runs_total{$environmentSelector})"
}
$values = [ordered]@{}
foreach ($query in $queries.GetEnumerator()) {
    $values[$query.Key] = Get-QueryValue $query.Value
}

if ($values.backendMetricStreams -lt $ExpectedBackendInstances) {
    throw "Only $($values.backendMetricStreams) backend metric streams are visible; expected at least $ExpectedBackendInstances."
}
if ($values.minimumReadiness -ne 1) {
    throw 'At least one reporting backend instance is not ready.'
}
if ($values.httpMetricStreams -lt $ExpectedBackendInstances) {
    throw 'HTTP metrics are not visible from every expected backend instance.'
}
if ($values.workerMetricStreams -lt $ExpectedBackendInstances) {
    throw 'Worker metrics are not visible from every expected backend instance.'
}

$requiredAlerts = @(
    'OpenGiftcardBackendTelemetryMissing',
    'OpenGiftcardBackendNotReady',
    'OpenGiftcardBackendHighErrorRate',
    'OpenGiftcardBackendHighLatency',
    'OpenGiftcardWorkerRepeatedFailures',
    'OpenGiftcardAuditVerificationFailure'
)
$rulesResponse = Invoke-MetricsApi '/api/v1/rules?type=alert'
$rules = @($rulesResponse.data.groups | ForEach-Object { @($_.rules) })
foreach ($alertName in $requiredAlerts) {
    $matches = @($rules | Where-Object { [string]$_.name -ceq $alertName })
    if ($matches.Count -ne 1) {
        throw "Required alert '$alertName' is not loaded exactly once."
    }
    if ([string]$matches[0].health -ne 'ok') {
        throw "Required alert '$alertName' is not healthy."
    }
    if ([string]$matches[0].state -eq 'firing') {
        throw "Required alert '$alertName' is firing."
    }
}

$completedAtUtc = [DateTimeOffset]::UtcNow
$evidence = [ordered]@{
    schemaVersion = 1
    environment = [ordered]@{
        name = $EnvironmentName
        metricsBaseUrl = $resolvedBaseUrl
        expectedBackendInstances = $ExpectedBackendInstances
    }
    release = [ordered]@{
        release = [string]$manifest.release
        artifactManifestSha256 = $manifestHash
        artifacts = @($artifacts | ForEach-Object {
            [ordered]@{
                component = [string]$_.component
                file = [string]$_.file
                sha256 = [string]$_.sha256
            }
        })
    }
    completedAtUtc = $completedAtUtc.ToString('O')
    result = 'passed'
    checks = [ordered]@{
        backendMetricStreams = [int]$values.backendMetricStreams
        minimumReadiness = [int]$values.minimumReadiness
        httpMetricStreams = [int]$values.httpMetricStreams
        workerMetricStreams = [int]$values.workerMetricStreams
        requiredAlertsLoaded = $requiredAlerts.Count
        requiredAlertsHealthy = $true
        requiredAlertsFiring = 0
    }
}

$evidenceDirectory = Split-Path $resolvedEvidencePath -Parent
if (-not (Test-Path -LiteralPath $evidenceDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
}
[IO.File]::WriteAllText(
    $resolvedEvidencePath,
    ($evidence | ConvertTo-Json -Depth 10),
    $encoding)
$evidenceHash = (Get-FileHash -LiteralPath $resolvedEvidencePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    "$resolvedEvidencePath.sha256",
    "$evidenceHash  $(Split-Path $resolvedEvidencePath -Leaf)`n",
    [Text.Encoding]::ASCII)

Write-Host "Observability evidence passed for '$EnvironmentName': $resolvedEvidencePath"
Write-Host "SHA-256: $evidenceHash"
