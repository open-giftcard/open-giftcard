[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testParent (
    'open-giftcard-acceptance-' + [Guid]::NewGuid().ToString('N'))
$encoding = [Text.UTF8Encoding]::new($false)

function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth 12),
        $encoding)
}

function Write-SmokeSidecar([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        "$Path.sha256",
        "$hash  $(Split-Path $Path -Leaf)`n",
        [Text.Encoding]::ASCII)
}

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $smoke = [ordered]@{
        schemaVersion = 1
        environment = [ordered]@{
            name = 'staging-recorder-test'
            scope = 'staging-automated-smoke'
            endpoints = [ordered]@{
                backend = 'https://api.staging.test'
                portal = 'https://portal.staging.test'
                portalBff = 'https://portal.staging.test'
                cardholder = 'https://card.staging.test'
                pos = 'https://pos.staging.test'
            }
            allEndpointsHttps = $true
        }
        release = [ordered]@{
            release = 'v0.5.0-rc.1'
            artifactManifestSha256 = 'A' * 64
            releaseContractSha256 = 'B' * 64
            backendOpenApiSha256 = 'C' * 64
            components = @('backend', 'portal', 'cardholder', 'pos') |
                ForEach-Object {
                    [ordered]@{
                        component = $_
                        repository = "open-giftcard-$_"
                        commit = 'd' * 40
                    }
                }
        }
        completedAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')
        result = 'passed'
        countsAsDeploymentVerifiedAutomatedSmoke = $true
    }
    $smokePath = Join-Path $testRoot 'smoke.json'
    Write-Json $smokePath $smoke
    Write-SmokeSidecar $smokePath

    $review = Get-Content -LiteralPath (
        Join-Path $repoRoot 'STAGING_ACCEPTANCE.example.json') `
        -Raw | ConvertFrom-Json
    $review.environmentName = 'staging-recorder-test'
    $review.reviewedAtUtc = [DateTimeOffset]::UtcNow
    $review.decision = 'approve'
    $review.blockingIssues = @()
    $review.checks | ForEach-Object { $_.result = 'passed' }
    $reviewPath = Join-Path $testRoot 'review.json'
    Write-Json $reviewPath $review

    $acceptedPath = Join-Path $testRoot 'accepted.json'
    & (Join-Path $PSScriptRoot 'New-StagingAcceptanceRecord.ps1') `
        -AutomatedSmokeEvidencePath $smokePath `
        -ReviewPath $reviewPath `
        -OutputPath $acceptedPath
    $accepted = Get-Content -LiteralPath $acceptedPath -Raw | ConvertFrom-Json
    if (-not [bool]$accepted.promotion.eligible -or
        @($accepted.review.checks).Count -ne 17 -or
        -not (Test-Path -LiteralPath "$acceptedPath.sha256" -PathType Leaf)) {
        throw 'A complete staging review did not create eligible evidence.'
    }

    $review.decision = 'reject'
    $review.checks[0].result = 'not-run'
    $review.blockingIssues = @('Test blocker')
    $rejectedReviewPath = Join-Path $testRoot 'rejected-review.json'
    Write-Json $rejectedReviewPath $review
    $rejectedPath = Join-Path $testRoot 'rejected.json'
    try {
        & (Join-Path $PSScriptRoot 'New-StagingAcceptanceRecord.ps1') `
            -AutomatedSmokeEvidencePath $smokePath `
            -ReviewPath $rejectedReviewPath `
            -OutputPath $rejectedPath
        throw 'An incomplete staging review unexpectedly passed.'
    }
    catch {
        if ($_.Exception.Message -notlike '*not eligible for promotion*') {
            throw
        }
    }
    $rejected = Get-Content -LiteralPath $rejectedPath -Raw | ConvertFrom-Json
    if ([bool]$rejected.promotion.eligible -or
        -not (Test-Path -LiteralPath "$rejectedPath.sha256" -PathType Leaf)) {
        throw 'A rejected review was not preserved as ineligible evidence.'
    }

    $smoke.environment.scope = 'local-source-smoke'
    $smoke.environment.allEndpointsHttps = $false
    $localSmokePath = Join-Path $testRoot 'local-smoke.json'
    Write-Json $localSmokePath $smoke
    Write-SmokeSidecar $localSmokePath
    try {
        & (Join-Path $PSScriptRoot 'New-StagingAcceptanceRecord.ps1') `
            -AutomatedSmokeEvidencePath $localSmokePath `
            -ReviewPath $reviewPath `
            -OutputPath (Join-Path $testRoot 'local.json')
        throw 'Local smoke evidence unexpectedly passed as staging evidence.'
    }
    catch {
        if ($_.Exception.Message -notlike
            '*not a passing named HTTPS deployment record*') {
            throw
        }
    }

    Write-Host 'Staging acceptance recorder tests passed.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            (Join-Path $testParent 'open-giftcard-acceptance-'),
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
