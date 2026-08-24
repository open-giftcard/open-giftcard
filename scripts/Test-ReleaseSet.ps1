[CmdletBinding()]
param(
    [Parameter()]
    [string]$PortalRoot,

    [Parameter()]
    [string]$CardholderRoot,

    [Parameter()]
    [string]$PosRoot
)

$ErrorActionPreference = 'Stop'
$backendRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $backendRoot

if ([string]::IsNullOrWhiteSpace($PortalRoot)) {
    $PortalRoot = Join-Path $workspaceRoot 'open-giftcard-portal'
}
if ([string]::IsNullOrWhiteSpace($CardholderRoot)) {
    $CardholderRoot = Join-Path $workspaceRoot 'open-giftcard-cardholder'
}
if ([string]::IsNullOrWhiteSpace($PosRoot)) {
    $PosRoot = Join-Path $workspaceRoot 'open-giftcard-pos'
}

$members = @(
    [pscustomobject]@{ Repository = 'open-giftcard/open-giftcard'; Root = $backendRoot }
    [pscustomobject]@{ Repository = 'open-giftcard/open-giftcard-portal'; Root = $PortalRoot }
    [pscustomobject]@{ Repository = 'open-giftcard/open-giftcard-cardholder'; Root = $CardholderRoot }
    [pscustomobject]@{ Repository = 'open-giftcard/open-giftcard-pos'; Root = $PosRoot }
)

$expectedHash = $null
foreach ($member in $members) {
    $root = [System.IO.Path]::GetFullPath($member.Root)
    $manifestPath = Join-Path $root 'RELEASE_COMPATIBILITY.json'
    $verifierPath = Join-Path $root 'scripts/Test-ReleaseContract.ps1'

    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "$($member.Repository) has no RELEASE_COMPATIBILITY.json."
    }
    if (!(Test-Path -LiteralPath $verifierPath -PathType Leaf)) {
        throw "$($member.Repository) has no release contract verifier."
    }

    $manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash
    if ($null -eq $expectedHash) {
        $expectedHash = $manifestHash
    }
    elseif ($manifestHash -cne $expectedHash) {
        throw "$($member.Repository) carries a different release contract. Expected $expectedHash but found $manifestHash."
    }

    & $verifierPath -Repository $member.Repository
}

Write-Host "All four repositories carry release contract $expectedHash."
