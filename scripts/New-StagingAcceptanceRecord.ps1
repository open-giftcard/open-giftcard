[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AutomatedSmokeEvidencePath,

    [Parameter(Mandatory)]
    [string]$ReviewPath,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$requiredChecks = [ordered]@{
    'portal-primary-journey' = 'Portal primary journey'
    'cardholder-primary-journey' = 'Cardholder primary journey'
    'pos-primary-journey' = 'POS primary journey'
    'keyboard-navigation' = 'Keyboard navigation'
    'mobile-layout' = 'Mobile layout'
    'zoom-200-percent' = '200 percent zoom'
    'reduced-motion' = 'Reduced motion'
    'screen-reader' = 'Screen reader'
    'visual-review' = 'Visual review'
    'tls-dns-ingress' = 'TLS, DNS, and ingress'
    'replica-restart-handoff' = 'Replica restart and session handoff'
    'smtp-email-delivery' = 'SMTP email delivery'
    'central-logs-correlation' = 'Central logs and correlation'
    'backup-restore' = 'Backup and restored-session drill'
    'application-rollback' = 'Application rollback compatibility'
    'metrics-alerts' = 'Metrics and alert paths'
    'ingress-rate-limits' = 'Public ingress rate limits'
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-SafeText(
    [string]$Name,
    [string]$Value,
    [int]$MaximumLength = 500
) {
    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt $MaximumLength -or
        $Value -match '[\x00-\x08\x0B\x0C\x0E-\x1F]') {
        throw "$Name must be non-empty plain text of at most $MaximumLength characters."
    }
}

function Read-VerifiedSmokeEvidence([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $checksumPath = "$resolved.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw 'Automated smoke evidence has no SHA-256 sidecar.'
    }

    $sidecar = [IO.File]::ReadAllText($checksumPath).Trim()
    $expectedFile = Split-Path $resolved -Leaf
    if ($sidecar -notmatch '^([0-9A-Fa-f]{64})  (.+)$' -or
        $Matches[2] -cne $expectedFile) {
        throw 'Automated smoke evidence has an invalid SHA-256 sidecar.'
    }
    $actualHash = Get-FileSha256 $resolved
    if ($actualHash -cne $Matches[1].ToUpperInvariant()) {
        throw 'Automated smoke evidence does not match its SHA-256 sidecar.'
    }

    $smoke = Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
    if ([int]$smoke.schemaVersion -ne 1 -or
        [string]$smoke.result -cne 'passed' -or
        -not [bool]$smoke.countsAsDeploymentVerifiedAutomatedSmoke -or
        [string]$smoke.environment.scope -cne 'staging-automated-smoke' -or
        -not [bool]$smoke.environment.allEndpointsHttps) {
        throw 'Automated smoke evidence is not a passing named HTTPS deployment record.'
    }
    Assert-SafeText 'Smoke environment name' ([string]$smoke.environment.name) 100

    foreach ($name in @('backend', 'portal', 'portalBff', 'cardholder', 'pos')) {
        $value = [string]$smoke.environment.endpoints.$name
        $uri = $null
        if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -or
            $uri.Scheme -cne 'https' -or
            -not [string]::IsNullOrEmpty($uri.UserInfo) -or
            -not [string]::IsNullOrEmpty($uri.Query) -or
            -not [string]::IsNullOrEmpty($uri.Fragment)) {
            throw "Smoke endpoint '$name' is not a redacted HTTPS origin."
        }
    }

    if ([string]$smoke.release.release -notmatch '^v\d+\.\d+\.\d+-rc\.\d+$' -or
        [string]$smoke.release.artifactManifestSha256 -notmatch '^[0-9A-F]{64}$' -or
        [string]$smoke.release.releaseContractSha256 -notmatch '^[0-9A-F]{64}$' -or
        [string]$smoke.release.backendOpenApiSha256 -notmatch '^[0-9A-F]{64}$' -or
        @($smoke.release.components).Count -ne 4) {
        throw 'Automated smoke evidence has incomplete release identity.'
    }

    return [pscustomobject]@{
        Path = $resolved
        Sha256 = $actualHash
        Value = $smoke
    }
}

function Write-EvidenceFile([string]$Path, $Value) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $checksumPath = "$resolved.sha256"
    if ((Test-Path -LiteralPath $resolved) -or
        (Test-Path -LiteralPath $checksumPath)) {
        throw "Refusing to overwrite staging acceptance evidence '$resolved'."
    }

    $parent = Split-Path $resolved -Parent
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $encoding = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText(
        $resolved,
        ($Value | ConvertTo-Json -Depth 12),
        $encoding)
    $digest = Get-FileSha256 $resolved
    [IO.File]::WriteAllText(
        $checksumPath,
        "$digest  $(Split-Path $resolved -Leaf)`n",
        [Text.Encoding]::ASCII)
    Write-Host "Staging acceptance evidence written to $resolved ($digest)"
}

$smokeRecord = Read-VerifiedSmokeEvidence $AutomatedSmokeEvidencePath
$resolvedReview = (Resolve-Path -LiteralPath $ReviewPath -ErrorAction Stop).Path
$review = Get-Content -LiteralPath $resolvedReview -Raw | ConvertFrom-Json
if ([int]$review.schemaVersion -ne 1) {
    throw 'The staging acceptance review schemaVersion must be 1.'
}
if ([string]$review.environmentName -cne
    [string]$smokeRecord.Value.environment.name) {
    throw 'The review and automated smoke evidence name different environments.'
}
Assert-SafeText 'Reviewer' ([string]$review.reviewer) 200
if ([bool]$review.secretValuesIncluded) {
    throw 'A staging acceptance review must not contain secret values.'
}

$reviewedAt = try {
    [DateTimeOffset]$review.reviewedAtUtc
}
catch {
    throw 'reviewedAtUtc must be an ISO-8601 timestamp.'
}
$smokeCompletedAt = try {
    [DateTimeOffset]$smokeRecord.Value.completedAtUtc
}
catch {
    throw 'Automated smoke evidence has an invalid completion timestamp.'
}
if ($reviewedAt -lt $smokeCompletedAt -or
    $reviewedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
    throw 'The review must occur after automated smoke and cannot be future-dated.'
}

$checksById = @{}
foreach ($check in @($review.checks)) {
    $id = [string]$check.id
    if (-not $requiredChecks.Contains($id)) {
        throw "The review contains unknown check '$id'."
    }
    if ($checksById.ContainsKey($id)) {
        throw "The review contains duplicate check '$id'."
    }
    if ([string]$check.result -notin @('passed', 'failed', 'not-run')) {
        throw "Check '$id' must be passed, failed, or not-run."
    }
    Assert-SafeText "Check '$id' owner" ([string]$check.owner) 200
    Assert-SafeText `
        "Check '$id' evidenceReference" `
        ([string]$check.evidenceReference) `
        500
    $checksById[$id] = $check
}

$missingChecks = @($requiredChecks.Keys | Where-Object {
    -not $checksById.ContainsKey($_)
})
if ($missingChecks.Count -ne 0) {
    throw "The review is missing required check '$($missingChecks[0])'."
}

$knownLimitations = @($review.knownLimitations)
foreach ($limitation in $knownLimitations) {
    Assert-SafeText 'Known limitation' ([string]$limitation) 500
}
$blockingIssues = @($review.blockingIssues)
foreach ($issue in $blockingIssues) {
    Assert-SafeText 'Blocking issue' ([string]$issue) 500
}

$allChecksPassed = @($checksById.Values | Where-Object {
    [string]$_.result -cne 'passed'
}).Count -eq 0
$requestedDecision = [string]$review.decision
if ($requestedDecision -notin @('approve', 'reject')) {
    throw 'The review decision must be approve or reject.'
}
$promotionEligible =
    $requestedDecision -ceq 'approve' -and
    $allChecksPassed -and
    $blockingIssues.Count -eq 0

$orderedChecks = foreach ($entry in $requiredChecks.GetEnumerator()) {
    $check = $checksById[$entry.Key]
    [ordered]@{
        id = $entry.Key
        name = $entry.Value
        result = [string]$check.result
        owner = [string]$check.owner
        evidenceReference = [string]$check.evidenceReference
    }
}

$record = [ordered]@{
    schemaVersion = 1
    recordType = 'staging-release-acceptance'
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    environment = $smokeRecord.Value.environment
    release = $smokeRecord.Value.release
    automatedSmoke = [ordered]@{
        evidenceFile = Split-Path $smokeRecord.Path -Leaf
        evidenceSha256 = $smokeRecord.Sha256
        completedAtUtc = [string]$smokeRecord.Value.completedAtUtc
        result = [string]$smokeRecord.Value.result
    }
    review = [ordered]@{
        reviewFile = Split-Path $resolvedReview -Leaf
        reviewSha256 = Get-FileSha256 $resolvedReview
        reviewer = [string]$review.reviewer
        reviewedAtUtc = $reviewedAt.ToUniversalTime().ToString('O')
        decision = $requestedDecision
        secretValuesIncluded = $false
        checks = @($orderedChecks)
        knownLimitations = @($knownLimitations)
        blockingIssues = @($blockingIssues)
    }
    promotion = [ordered]@{
        eligible = $promotionEligible
        allRequiredChecksPassed = $allChecksPassed
        blockingIssueCount = $blockingIssues.Count
        statement = if ($promotionEligible) {
            'Automated staging smoke and recorded acceptance gates passed for this exact release set.'
        }
        else {
            'This release set is not eligible for promotion.'
        }
    }
}

Write-EvidenceFile $OutputPath $record
if (-not $promotionEligible) {
    throw 'Staging acceptance was recorded, but the release set is not eligible for promotion.'
}

Write-Host 'Named staging acceptance passed. The record contains references, not secret values.'
