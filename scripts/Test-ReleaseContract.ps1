<#
.SYNOPSIS
    Verifies RELEASE_COMPATIBILITY.json against the repository it sits in.

.DESCRIPTION
    The manifest names the set of four artifacts that go together. Its value
    depends entirely on every reference in it resolving, so this script refuses
    a manifest that names something which does not exist.

    Two channel states are allowed, and they have different rules.

      development        Nothing is released. 'release' must be the literal
                         string 'unreleased' and no component may carry a tag.
                         This is the honest state for a repository with no
                         public tags.

      release-candidate  A release is being cut. 'release' must be a semantic
      release            version, every component must carry a tag equal to it,
                         and this repository's own tag must exist locally.

    The earlier schema (version 1) required a tag on every component and had no
    way to express "not released yet", so the manifest named four tags that did
    not exist in any repository. That is what this version fixes.
#>
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

# Read as bytes rather than text. The manifest is meant to be byte-identical in
# all four repositories, so a stray CRLF or a byte order mark is a defect and
# not a formatting preference. One repository previously carried CRLF here
# while the other three carried LF.
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestPath)
Assert-ReleaseContract ($manifestBytes.Length -ge 3) 'RELEASE_COMPATIBILITY.json is empty.'
$hasBom = $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF
Assert-ReleaseContract (-not $hasBom) 'RELEASE_COMPATIBILITY.json must not start with a UTF-8 byte order mark.'
Assert-ReleaseContract ($manifestBytes -notcontains 0x0D) 'RELEASE_COMPATIBILITY.json must use LF line endings so it is byte-identical in every repository.'

$manifest = [System.Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json

Assert-ReleaseContract ($manifest.schemaVersion -eq 2) 'RELEASE_COMPATIBILITY.json must use schemaVersion 2.'

$validChannels = @('development', 'release-candidate', 'release')
Assert-ReleaseContract ($validChannels -contains $manifest.channel) "Channel must be one of: $($validChannels -join ', ')."

$isReleased = $manifest.channel -ne 'development'

if ($isReleased) {
    Assert-ReleaseContract ($manifest.release -match '^v[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$') 'A released channel requires a semantic version such as v1.0.0 or v0.5.0-rc.1.'
    if ($manifest.channel -eq 'release-candidate') {
        Assert-ReleaseContract ($manifest.release -match '-rc\.[0-9]+$') 'The release-candidate channel requires an -rc. suffix.'
    }
    else {
        Assert-ReleaseContract ($manifest.release -notmatch '-rc\.') 'The release channel must not use an -rc. suffix.'
    }
}
else {
    Assert-ReleaseContract ($manifest.release -eq 'unreleased') "The development channel requires release to be 'unreleased', not '$($manifest.release)'."
}

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
    $found = @($manifest.components | Where-Object { $_.id -eq $expected.Id })
    Assert-ReleaseContract ($found.Count -eq 1) "Component '$($expected.Id)' must appear exactly once."
    $component = $found[0]
    Assert-ReleaseContract ($component.repository -eq $expected.Repository) "Component '$($expected.Id)' names the wrong repository."
    Assert-ReleaseContract ($component.artifact -eq $expected.Artifact) "Component '$($expected.Id)' names the wrong artifact."

    $componentTag = $component.PSObject.Properties['tag']
    if ($isReleased) {
        Assert-ReleaseContract ($null -ne $componentTag) "Component '$($expected.Id)' must carry a tag on a released channel."
        Assert-ReleaseContract ($component.tag -eq $manifest.release) "Component '$($expected.Id)' must use tag '$($manifest.release)'."
    }
    else {
        Assert-ReleaseContract ($null -eq $componentTag) "Component '$($expected.Id)' must not carry a tag while the channel is development. A manifest that names a tag nobody has created is worse than one that names none."
    }
}

$selfComponent = @($manifest.components | Where-Object { $_.repository -eq $Repository })
Assert-ReleaseContract ($selfComponent.Count -eq 1) "This repository '$Repository' is not a member of the release contract."

# The check that would have caught the phantom tags: on a released channel, the
# tag this repository claims must actually exist. Cross-repository tags cannot
# be resolved from here, so each repository verifies its own.
#
# Look locally first, then at the remote. A CI checkout is shallow and carries
# no tags by default, so a local-only check fails on a correctly tagged commit,
# which is exactly what happened the first time v0.9.0 was pushed. The remote is
# the authority for whether a tag exists; the local working copy is a cache that
# may legitimately not have it.
if ($isReleased) {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $tag = $selfComponent[0].tag
        & git -C $repoRoot rev-parse --verify --quiet "refs/tags/$tag" *> $null
        $foundLocally = $LASTEXITCODE -eq 0

        if ($foundLocally) {
            Write-Host "Tag '$tag' resolves locally."
        }
        else {
            $remote = & git -C $repoRoot ls-remote --tags origin "refs/tags/$tag" 2>$null
            if ($LASTEXITCODE -ne 0) {
                # No network, no remote, or no credentials. Refusing here would
                # fail an offline contributor for something they cannot check.
                Write-Warning "Tag '$tag' is not in this working copy and the remote could not be reached, so its existence was not verified."
            }
            else {
                Assert-ReleaseContract ($remote -match [regex]::Escape("refs/tags/$tag")) "RELEASE_COMPATIBILITY.json names tag '$tag' but no such tag exists locally or on origin. Create and push the tag, or move the manifest back to the development channel."
                Write-Host "Tag '$tag' resolves on origin, though not in this shallow working copy."
            }
        }
    }
    else {
        Write-Warning 'git was not found, so the tag existence check was skipped.'
    }
}

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

$state = if ($isReleased) { $manifest.release } else { 'unreleased (development channel)' }
Write-Host "Release contract verified for $Repository at $state."
