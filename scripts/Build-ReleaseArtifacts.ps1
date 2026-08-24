[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [switch]$NoRestore,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspaceRoot = Split-Path $backendRoot -Parent
$repos = [ordered]@{
    backend = $backendRoot
    portal = Join-Path $workspaceRoot 'open-giftcard-portal'
    cardholder = Join-Path $workspaceRoot 'open-giftcard-cardholder'
    pos = Join-Path $workspaceRoot 'open-giftcard-pos'
}
$projects = [ordered]@{
    backend = 'src\GiftCardPlatform.Api\GiftCardPlatform.Api.csproj'
    portal = 'src\GiftCardPortal.Bff\GiftCardPortal.Bff.csproj'
    cardholder = 'src\GiftCardCardholder.Web\GiftCardCardholder.Web.csproj'
    pos = 'src\GiftCardPos.Web\GiftCardPos.Web.csproj'
}
$entryPoints = [ordered]@{
    backend = 'GiftCardPlatform.Api.dll'
    portal = 'GiftCardPortal.Bff.dll'
    cardholder = 'GiftCardCardholder.Web.dll'
    pos = 'GiftCardPos.Web.dll'
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)

function Invoke-Checked(
    [string]$Command,
    [string[]]$Arguments,
    [string]$WorkingDirectory
) {
    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Command failed in '$WorkingDirectory'."
        }
    }
    finally {
        Pop-Location
    }
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Assert-SafeTemporaryPath([string]$RepoRoot, [string]$Path) {
    $allowed = [IO.Path]::GetFullPath((Join-Path $RepoRoot '.local\release-package'))
    $target = [IO.Path]::GetFullPath($Path)
    if (-not $target.StartsWith(
            $allowed + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing temporary release path '$target'."
    }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Read-ZipJson(
    [IO.Compression.ZipArchive]$Archive,
    [string]$EntryName
) {
    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Release archive is missing '$EntryName'."
    }

    $stream = $entry.Open()
    $reader = New-Object IO.StreamReader($stream)
    try {
        return $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-ReleaseArchive(
    [string]$Component,
    [string]$ArchivePath,
    [string]$ExpectedContractHash
) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expectedRoot = "open-giftcard-$Component-$version"
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $forbiddenPattern = '(^|/)(\.env($|\.)|.*\.pfx$|.*\.pem$|.*\.key$|' +
            'secrets?\.json$|key-.*\.xml$)'
        $entries = @($archive.Entries)
        $roots = @($entries | ForEach-Object {
            ($_.FullName -split '/')[0]
        } | Sort-Object -Unique)
        if ($roots.Count -ne 1 -or $roots[0] -cne $expectedRoot) {
            throw "$Component archive must contain only the '$expectedRoot' root."
        }

        $forbidden = @($entries | Where-Object {
            $_.FullName -match $forbiddenPattern -or
            $_.FullName -match '(^|/)\.\.(/|$)'
        })
        if ($forbidden.Count -ne 0) {
            throw "$Component archive contains forbidden path '$($forbidden[0].FullName)'."
        }

        $entryPoint = "$expectedRoot/app/$($entryPoints[$Component])"
        if ($null -eq $archive.GetEntry($entryPoint)) {
            throw "$Component archive is missing entry point '$entryPoint'."
        }

        $contractEntry = $archive.GetEntry(
            "$expectedRoot/RELEASE_COMPATIBILITY.json")
        if ($null -eq $contractEntry) {
            throw "$Component archive is missing its release contract."
        }
        $contractStream = $contractEntry.Open()
        try {
            $contractHash = Get-StreamSha256 $contractStream
        }
        finally {
            $contractStream.Dispose()
        }
        if ($contractHash -cne $ExpectedContractHash) {
            throw "$Component archive carries release contract $contractHash, expected $ExpectedContractHash."
        }

        $info = Read-ZipJson $archive "$expectedRoot/BUILD_INFO.json"
        if ([string]$info.release -cne $version -or
            [string]$info.component -cne $Component -or
            [string]$info.commit -cne [string]$buildInfo[$Component].commit -or
            [bool]$info.dirty -ne [bool]$buildInfo[$Component].dirty) {
            throw "$Component archive BUILD_INFO.json does not match this build."
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($entry in $repos.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Container)) {
        throw "Missing $($entry.Key) repository: $($entry.Value)"
    }
}

& (Join-Path $backendRoot 'scripts\Test-ReleaseSet.ps1')
if (-not $?) {
    throw 'The cross-repository release contract failed.'
}

$contract = Get-Content -LiteralPath (Join-Path $backendRoot 'RELEASE_COMPATIBILITY.json') `
    -Raw | ConvertFrom-Json
$version = [string]$contract.release
if ($version -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Release version '$version' is not a supported semantic version."
}
$contractHash = (Get-FileHash -LiteralPath `
    (Join-Path $backendRoot 'RELEASE_COMPATIBILITY.json') `
    -Algorithm SHA256).Hash

$buildInfo = [ordered]@{}
foreach ($entry in $repos.GetEnumerator()) {
    $safeDirectory = "safe.directory=$($entry.Value)"
    $status = @(git -c $safeDirectory -C $entry.Value status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect $($entry.Key) repository status."
    }
    if ($status.Count -ne 0 -and -not $AllowDirty) {
        throw "$($entry.Key) has uncommitted changes. Commit them or use -AllowDirty for a non-release rehearsal."
    }

    $commit = (git -c $safeDirectory -C $entry.Value rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve $($entry.Key) commit."
    }
    $buildInfo[$entry.Key] = [ordered]@{
        repository = Split-Path $entry.Value -Leaf
        commit = $commit
        dirty = $status.Count -ne 0
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $backendRoot ".local\release-artifacts\$version\$runId"
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) {
    throw "Output directory already exists: $outputRoot"
}

$workRoot = Join-Path $outputRoot '.work'
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$temporaryBuildRoots = @()

try {
    foreach ($component in $projects.Keys) {
        $repoRoot = $repos[$component]
        $publishPath = Join-Path $workRoot "$component\app"
        $bundlePath = Join-Path $workRoot "$component\bundle\open-giftcard-$component-$version"
        $temporaryBuildRoot = Join-Path $repoRoot ".local\release-package\$runId"
        Assert-SafeTemporaryPath $repoRoot $temporaryBuildRoot
        $temporaryBuildRoots += ,@($repoRoot, $temporaryBuildRoot)

        New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
        New-Item -ItemType Directory -Path $bundlePath -Force | Out-Null
        $arguments = @(
            'publish',
            $projects[$component],
            '--configuration', 'Release',
            '--output', $publishPath,
            "-p:BaseOutputPath=$temporaryBuildRoot\bin\"
        )
        if ($NoRestore) {
            $arguments += '--no-restore'
        }

        Write-Host "Publishing $component..."
        Invoke-Checked 'dotnet' $arguments $repoRoot

        Copy-Item -LiteralPath $publishPath -Destination (Join-Path $bundlePath 'app') -Recurse
        Copy-Item -LiteralPath (Join-Path $repoRoot 'RELEASE_COMPATIBILITY.json') `
            -Destination $bundlePath
        foreach ($document in @('README.md', 'LICENSE', 'SECURITY.md')) {
            $source = Join-Path $repoRoot $document
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                Copy-Item -LiteralPath $source -Destination $bundlePath
            }
        }
        if ($component -eq 'backend') {
            Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\DEPLOYMENT.md') `
                -Destination $bundlePath
            Copy-Item -LiteralPath (Join-Path $repoRoot 'RELEASE_READINESS.md') `
                -Destination $bundlePath
        }

        $componentInfo = [ordered]@{
            release = $version
            component = $component
            repository = $buildInfo[$component].repository
            commit = $buildInfo[$component].commit
            dirty = $buildInfo[$component].dirty
            builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            frameworkDependent = $true
        }
        Write-Utf8NoBom `
            (Join-Path $bundlePath 'BUILD_INFO.json') `
            ($componentInfo | ConvertTo-Json -Depth 5)

        $archivePath = Join-Path $outputRoot "open-giftcard-$component-$version.zip"
        Compress-Archive -LiteralPath $bundlePath -DestinationPath $archivePath `
            -CompressionLevel Optimal
        Test-ReleaseArchive $component $archivePath $contractHash
    }

    $archives = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' -File |
        Sort-Object Name)
    if ($archives.Count -ne $projects.Count) {
        throw "Expected $($projects.Count) archives but found $($archives.Count)."
    }

    $artifactEntries = foreach ($archive in $archives) {
        $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
        [ordered]@{
            file = $archive.Name
            sha256 = $hash
            bytes = $archive.Length
        }
    }
    @($artifactEntries | ForEach-Object { "$($_.sha256)  $($_.file)" }) |
        Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS') -Encoding ascii
    $artifactsManifest = [ordered]@{
        release = $version
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        rehearsal = [bool]$AllowDirty
        artifacts = @($artifactEntries)
    }
    Write-Utf8NoBom `
        (Join-Path $outputRoot 'ARTIFACTS.json') `
        ($artifactsManifest | ConvertTo-Json -Depth 6)

    Write-Host "Release artifacts built at $outputRoot"
}
finally {
    foreach ($temporary in $temporaryBuildRoots) {
        $repoRoot = $temporary[0]
        $temporaryBuildRoot = $temporary[1]
        if (Test-Path -LiteralPath $temporaryBuildRoot) {
            Assert-SafeTemporaryPath $repoRoot $temporaryBuildRoot
            Remove-Item -LiteralPath $temporaryBuildRoot -Recurse -Force
        }
    }

    if (Test-Path -LiteralPath $workRoot) {
        $resolvedOutput = (Resolve-Path -LiteralPath $outputRoot).Path
        $resolvedWork = (Resolve-Path -LiteralPath $workRoot).Path
        if (-not $resolvedWork.StartsWith(
                $resolvedOutput + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing release work path '$resolvedWork'."
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
