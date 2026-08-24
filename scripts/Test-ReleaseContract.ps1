[CmdletBinding()]
param(
    [Parameter()]
    [string]$Repository = 'open-giftcard/open-giftcard'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'RELEASE_COMPATIBILITY.json'

function Assert-ReleaseContract {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (!$Condition) {
        throw $Message
    }
}

Assert-ReleaseContract (Test-Path -LiteralPath $manifestPath -PathType Leaf) "Missing release contract: $manifestPath"
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

Assert-ReleaseContract ($manifest.schemaVersion -eq 1) 'RELEASE_COMPATIBILITY.json must use schemaVersion 1.'
Assert-ReleaseContract ($manifest.release -match '^v[0-9]+\.[0-9]+\.[0-9]+-rc\.[0-9]+$') 'Release must be an RC semantic version such as v0.5.0-rc.1.'
Assert-ReleaseContract ($manifest.channel -eq 'release-candidate') 'Channel must be release-candidate.'
Assert-ReleaseContract ($manifest.backendContract.repository -eq 'open-giftcard/open-giftcard') 'The backend contract must name the canonical backend repository.'
Assert-ReleaseContract ($manifest.backendContract.commit -match '^[0-9a-f]{40}$') 'The backend contract commit must be a full lowercase Git SHA.'
Assert-ReleaseContract ($manifest.backendContract.endpoint -eq '/swagger/v1/swagger.json') 'The backend contract endpoint is unexpected.'
Assert-ReleaseContract ($manifest.backendContract.sha256 -match '^[0-9A-F]{64}$') 'The backend contract SHA-256 must be uppercase hexadecimal.'

$expectedComponents = @(
    [pscustomobject]@{ Id = 'backend'; Repository = 'open-giftcard/open-giftcard'; Artifact = 'open-giftcard-backend' }
    [pscustomobject]@{ Id = 'portal'; Repository = 'open-giftcard/open-giftcard-portal'; Artifact = 'open-giftcard-portal' }
    [pscustomobject]@{ Id = 'cardholder'; Repository = 'open-giftcard/open-giftcard-cardholder'; Artifact = 'open-giftcard-cardholder' }
    [pscustomobject]@{ Id = 'pos'; Repository = 'open-giftcard/open-giftcard-pos'; Artifact = 'open-giftcard-pos' }
)

Assert-ReleaseContract ($manifest.components.Count -eq $expectedComponents.Count) 'The release contract must contain exactly four components.'
foreach ($expected in $expectedComponents) {
    $matches = @($manifest.components | Where-Object { $_.id -eq $expected.Id })
    Assert-ReleaseContract ($matches.Count -eq 1) "Component '$($expected.Id)' must appear exactly once."
    $component = $matches[0]
    Assert-ReleaseContract ($component.repository -eq $expected.Repository) "Component '$($expected.Id)' names the wrong repository."
    Assert-ReleaseContract ($component.tag -eq $manifest.release) "Component '$($expected.Id)' must use tag '$($manifest.release)'."
    Assert-ReleaseContract ($component.artifact -eq $expected.Artifact) "Component '$($expected.Id)' names the wrong artifact."
}

Assert-ReleaseContract (@($manifest.components | Where-Object { $_.repository -eq $Repository }).Count -eq 1) "This repository '$Repository' is not a member of the release contract."

$snapshotPath = Join-Path $repoRoot 'contracts/backend.openapi.json'
if (Test-Path -LiteralPath $snapshotPath -PathType Leaf) {
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $snapshotPath).Hash
    Assert-ReleaseContract ($actualHash -ceq $manifest.backendContract.sha256) "The backend OpenAPI snapshot hashes to $actualHash, not $($manifest.backendContract.sha256)."

    $contractReadmePath = Join-Path $repoRoot 'contracts/README.md'
    Assert-ReleaseContract (Test-Path -LiteralPath $contractReadmePath -PathType Leaf) 'The backend contract snapshot has no contracts/README.md provenance record.'
    $contractReadme = Get-Content -Raw -LiteralPath $contractReadmePath
    Assert-ReleaseContract ($contractReadme.Contains($manifest.backendContract.commit)) 'contracts/README.md does not name the accepted backend commit.'
    Assert-ReleaseContract ($contractReadme.Contains($manifest.backendContract.sha256)) 'contracts/README.md does not name the accepted OpenAPI SHA-256.'
}

Write-Host "Release contract verified for $Repository at $($manifest.release)."
