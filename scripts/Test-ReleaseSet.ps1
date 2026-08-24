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

function Get-PortableTextSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

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

    $manifestHash = Get-PortableTextSha256 $manifestPath
    if ($null -eq $expectedHash) {
        $expectedHash = $manifestHash
    }
    elseif ($manifestHash -cne $expectedHash) {
        throw "$($member.Repository) carries a different release contract. Expected $expectedHash but found $manifestHash."
    }

    & $verifierPath -Repository $member.Repository
}

Write-Host "All four repositories carry release contract $expectedHash."
