[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [Parameter(Mandatory)]
    [string]$SbomToolPath,
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
$sbomToolVersion = '4.1.5'
$sbomToolSha256 = '625767B371B7FDD58F40F618B8A86DA0247A33C89E419039C86B4EDBA1DAD4B5'

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

function Get-PortableTextSha256([string]$Content) {
    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-StreamPortableTextSha256([IO.Stream]$Stream) {
    $reader = [IO.StreamReader]::new($Stream)
    try {
        return Get-PortableTextSha256 ($reader.ReadToEnd())
    }
    finally {
        $reader.Dispose()
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

function Test-SbomManifest(
    [string]$Path,
    [string]$PackageName,
    [string]$PackageVersion
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SBOM generator did not create '$Path'."
    }

    $sbom = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([string]$sbom.spdxVersion -cne 'SPDX-2.2') {
        throw "$PackageName SBOM is not SPDX 2.2."
    }
    if ([string]$sbom.name -cne "$PackageName $PackageVersion") {
        throw "$PackageName SBOM carries unexpected package identity '$($sbom.name)'."
    }
    $expectedNamespace =
        "https://github.com/open-giftcard/$PackageName/$PackageVersion/*"
    if ([string]$sbom.documentNamespace -notlike $expectedNamespace) {
        throw "$PackageName SBOM carries an unexpected document namespace."
    }
    if (@($sbom.files).Count -eq 0 -or @($sbom.packages).Count -eq 0) {
        throw "$PackageName SBOM must describe files and packages."
    }
}

function Test-ReleaseArchive(
    [string]$Component,
    [string]$ArchivePath,
    [string]$ExpectedContractHash,
    [string]$ExpectedSbomHash
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
        if ($Component -ceq 'backend') {
            foreach ($operatorFile in @(
                'DEPLOYMENT.md',
                'scripts/OpenGiftCardLocal.Common.ps1',
                'scripts/Test-BackendRollback.ps1',
                'scripts/Test-OpenGiftCardSmoke.ps1',
                'scripts/Test-PostgresRecovery.ps1')) {
                if ($null -eq $archive.GetEntry("$expectedRoot/$operatorFile")) {
                    throw "Backend archive is missing operator file '$operatorFile'."
                }
            }
        }

        $contractEntry = $archive.GetEntry(
            "$expectedRoot/RELEASE_COMPATIBILITY.json")
        if ($null -eq $contractEntry) {
            throw "$Component archive is missing its release contract."
        }
        $contractStream = $contractEntry.Open()
        try {
            $contractHash = Get-StreamPortableTextSha256 $contractStream
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

        $sbomEntry = $archive.GetEntry(
            "$expectedRoot/_manifest/spdx_2.2/manifest.spdx.json")
        if ($null -eq $sbomEntry) {
            throw "$Component archive is missing its SPDX 2.2 SBOM."
        }
        $sbomStream = $sbomEntry.Open()
        try {
            $sbomHash = Get-StreamSha256 $sbomStream
        }
        finally {
            $sbomStream.Dispose()
        }
        if ($sbomHash -cne $ExpectedSbomHash) {
            throw "$Component archive carries SBOM $sbomHash, expected $ExpectedSbomHash."
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

$resolvedSbomTool = (Resolve-Path -LiteralPath $SbomToolPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedSbomTool -PathType Leaf)) {
    throw "Missing Microsoft SBOM Tool executable: $resolvedSbomTool"
}
$actualSbomToolHash = (Get-FileHash -LiteralPath $resolvedSbomTool `
    -Algorithm SHA256).Hash
if ($actualSbomToolHash -cne $sbomToolSha256) {
    throw "Microsoft SBOM Tool v$sbomToolVersion hash $actualSbomToolHash does not match pinned hash $sbomToolSha256."
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
$contractPath = Join-Path $backendRoot 'RELEASE_COMPATIBILITY.json'
$contractText = [IO.File]::ReadAllText($contractPath)
$contractHash = Get-PortableTextSha256 $contractText

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
$sbomEntries = [ordered]@{}

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
        $releaseContract = [IO.File]::ReadAllText(
            (Join-Path $repoRoot 'RELEASE_COMPATIBILITY.json'))
        $releaseContract = $releaseContract.Replace("`r`n", "`n").Replace("`r", "`n")
        Write-Utf8NoBom `
            (Join-Path $bundlePath 'RELEASE_COMPATIBILITY.json') `
            $releaseContract
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
            $operatorScriptPath = Join-Path $bundlePath 'scripts'
            New-Item -ItemType Directory -Path $operatorScriptPath -Force |
                Out-Null
            foreach ($operatorScript in @(
                'OpenGiftCardLocal.Common.ps1',
                'Test-BackendRollback.ps1',
                'Test-OpenGiftCardSmoke.ps1',
                'Test-PostgresRecovery.ps1')) {
                Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\$operatorScript") `
                    -Destination $operatorScriptPath
            }
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

        $packageName = "open-giftcard-$component"
        $packageVersion = $version.TrimStart('v')
        Write-Host "Generating $component SPDX 2.2 SBOM..."
        Invoke-Checked $resolvedSbomTool @(
            'generate',
            '-b', $bundlePath,
            '-bc', (Join-Path $repoRoot 'src'),
            '-pn', $packageName,
            '-pv', $packageVersion,
            '-ps', 'Organization: open-giftcard',
            '-nsb', 'https://github.com/open-giftcard',
            '-nsu', "$component/$version/$($buildInfo[$component].commit)",
            '-mi', 'SPDX:2.2',
            '-D', 'true',
            '-V', 'Warning'
        ) $repoRoot
        $embeddedSbomPath = Join-Path $bundlePath `
            '_manifest\spdx_2.2\manifest.spdx.json'
        Test-SbomManifest $embeddedSbomPath $packageName $packageVersion
        $sbomPath = Join-Path $outputRoot "$packageName-$version.spdx.json"
        Copy-Item -LiteralPath $embeddedSbomPath -Destination $sbomPath
        $sbomHash = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
        $sbomEntries[$component] = [ordered]@{
            file = Split-Path $sbomPath -Leaf
            sha256 = $sbomHash
            bytes = (Get-Item -LiteralPath $sbomPath).Length
            format = 'SPDX-2.2'
        }

        $archivePath = Join-Path $outputRoot "open-giftcard-$component-$version.zip"
        Compress-Archive -LiteralPath $bundlePath -DestinationPath $archivePath `
            -CompressionLevel Optimal
        Test-ReleaseArchive $component $archivePath $contractHash $sbomHash
    }

    $archives = @(Get-ChildItem -LiteralPath $outputRoot -Filter '*.zip' -File |
        Sort-Object Name)
    if ($archives.Count -ne $projects.Count) {
        throw "Expected $($projects.Count) archives but found $($archives.Count)."
    }

    $artifactEntries = foreach ($archive in $archives) {
        $component = $projects.Keys | Where-Object {
            $archive.Name -ceq "open-giftcard-$_-$version.zip"
        }
        if (@($component).Count -ne 1) {
            throw "Could not map release archive '$($archive.Name)' to one component."
        }
        $component = [string]$component
        $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
        [ordered]@{
            component = $component
            file = $archive.Name
            sha256 = $hash
            bytes = $archive.Length
            sbom = $sbomEntries[$component]
        }
    }
    $checksumEntries = foreach ($artifact in $artifactEntries) {
        "$($artifact.sha256)  $($artifact.file)"
        "$($artifact.sbom.sha256)  $($artifact.sbom.file)"
    }
    @($checksumEntries | Sort-Object { ($_ -split '  ', 2)[1] }) |
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
