<#
.SYNOPSIS
    Rehearses what a stranger gets from a fresh clone, without trusting the
    working tree.

.DESCRIPTION
    The README is the first thing a visitor follows and the easiest thing to
    let rot. CI proves the Docker path on Linux runners; this proves the parts
    that do not need Docker, on the maintainer's own machine, against a clone
    rather than against the working tree.

    Cloning matters. An untracked file that only exists locally is invisible in
    a clone, and that is precisely how documentation ends up citing something
    nobody else can read. Everything here therefore runs in a temporary clone
    of HEAD.

    Checks, in order:

      1. The clone contains what the README tells a reader to open.
      2. Documentation references resolve, in the clone.
      3. The release contract is valid, in the clone.
      4. No file that should stay local was published.
      5. The published API compatibility baseline matches the manifest.
      6. .env.example covers every variable the application requires.
      7. The solution restores and builds from clean, at zero warnings.
      8. The suites that need no database pass.

    What it deliberately does not do: start PostgreSQL, run the integration
    suite, or run Docker. Those need an environment this script should not
    assume. CI covers the Docker path; -IncludeIntegration runs the database
    suite when GIFTCARD_TEST_CONNECTION is already set.

.PARAMETER IncludeIntegration
    Also run the integration suite in the clone. Requires
    GIFTCARD_TEST_CONNECTION to point at a disposable database whose name
    contains "test".

.PARAMETER KeepClone
    Leave the temporary clone in place for inspection.

.EXAMPLE
    ./scripts/Test-FreshCloneRehearsal.ps1
#>
[CmdletBinding()]
param(
    [switch]$IncludeIntegration,
    [switch]$KeepClone
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$failures = [System.Collections.Generic.List[string]]::new()
$step = 0

function Start-Step {
    param([string]$Name)
    $script:step++
    Write-Host ''
    Write-Host "[$script:step] $Name" -ForegroundColor Cyan
}

function Add-Failure {
    param([string]$Message)
    $failures.Add($Message)
    Write-Host "    FAIL  $Message" -ForegroundColor Red
}

function Add-Pass {
    param([string]$Message)
    Write-Host "    ok    $Message" -ForegroundColor DarkGray
}

$clone = Join-Path ([System.IO.Path]::GetTempPath()) ("open-giftcard-rehearsal-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

try {
    Start-Step "Clone HEAD into $clone"
    & git clone --quiet --no-hardlinks $repoRoot $clone
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    $head = (& git -C $repoRoot rev-parse --short HEAD)
    Add-Pass "cloned $head"

    # ------------------------------------------------------------------ 1
    Start-Step 'The clone contains what the README points at'
    $readme = Get-Content -Raw -LiteralPath (Join-Path $clone 'README.md')
    $required = @(
        'README.md', 'LICENSE', 'SECURITY.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md',
        'CHANGELOG.md', 'VERSIONING.md', 'RELEASE_READINESS.md', 'RELEASE_COMPATIBILITY.json',
        '.env.example', 'docker-compose.yml', 'docker-compose.full.yml', 'Dockerfile', 'global.json',
        'infra/postgres/create-client-databases.sh',
        'docs/README.md', 'docs/ARCHITECTURE.md', 'docs/DECISIONS.md', 'docs/DOMAIN_RULES.md',
        'docs/CODEMAP.md', 'docs/DEPLOYMENT.md', 'docs/FRONTEND_INTEGRATION.md',
        'contracts/backend.openapi.json', 'infra/postgres/init/01-roles-and-privileges.sh'
    )
    foreach ($path in $required) {
        if (Test-Path -LiteralPath (Join-Path $clone $path)) { Add-Pass $path }
        else { Add-Failure "$path is missing from a fresh clone." }
    }

    # ------------------------------------------------------------------ 2
    Start-Step 'Documentation references resolve in the clone'
    try {
        & (Join-Path $clone 'scripts/Test-DocumentationReferences.ps1') | ForEach-Object { Add-Pass $_ }
    }
    catch {
        Add-Failure "Documentation references do not resolve in a clone: $_"
    }

    # ------------------------------------------------------------------ 3
    Start-Step 'The release contract is valid in the clone'
    try {
        & (Join-Path $clone 'scripts/Test-ReleaseContract.ps1') | ForEach-Object { Add-Pass $_ }
    }
    catch {
        Add-Failure "Release contract invalid in a clone: $_"
    }

    # ------------------------------------------------------------------ 4
    Start-Step 'Nothing that should stay local was published'
    # Secrets, machine notes, and assistant scaffolding. A clone is the only
    # honest place to check: locally these are present but untracked.
    $mustNotExist = @(
        '.env', 'CLAUDE.md', 'CLAUDE.local.md', 'AGENTS.md', 'codex_job_short.md',
        '.claude', '.agents', '.local',
        'docs/CURRENT_TASK.md', 'docs/TASK_HISTORY.md', 'docs/HANDOFF.md',
        'docs/HANDOFF_IMPL_034.md', 'docs/REVIEW-001.md', 'docs/STATUS_AUDIT.md'
    )
    foreach ($path in $mustNotExist) {
        if (Test-Path -LiteralPath (Join-Path $clone $path)) {
            Add-Failure "$path reached the clone and should not have."
        }
        else { Add-Pass "$path absent" }
    }

    # A blunt sweep for credential-shaped content in tracked files.
    $suspects = Get-ChildItem -LiteralPath $clone -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\\.git\\' } |
        Where-Object { $_.Extension -in '.json', '.yml', '.yaml', '.env', '.config', '.ps1', '.sh', '.cs' } |
        Select-String -Pattern 'BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY' -List
    if ($suspects) {
        foreach ($hit in $suspects) { Add-Failure "Private key material in $($hit.Path)" }
    }
    else { Add-Pass 'no private key material in tracked files' }

    # ------------------------------------------------------------------ 5
    Start-Step 'The compatibility baseline matches the manifest'
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $clone 'RELEASE_COMPATIBILITY.json') | ConvertFrom-Json
    $baseline = Join-Path $clone 'contracts/backend.openapi.json'
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $baseline).Hash
    if ($hash -ceq $manifest.backendContract.sha256) {
        Add-Pass "baseline hashes to $($hash.Substring(0, 16))..."
    }
    else {
        Add-Failure "Baseline hashes to $hash but the manifest declares $($manifest.backendContract.sha256)."
    }

    # ------------------------------------------------------------------ 6
    Start-Step '.env.example covers every required variable'
    # Startup validation throws by configuration key, so a variable the
    # application demands and the example omits is a setup dead end.
    $exampleKeys = Get-Content -LiteralPath (Join-Path $clone '.env.example') |
        ForEach-Object { if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=') { $Matches[1] } }
    foreach ($composeFile in 'docker-compose.yml', 'docker-compose.full.yml') {
        $composePath = Join-Path $clone $composeFile
        if (-not (Test-Path -LiteralPath $composePath)) {
            Add-Failure "$composeFile is missing from a fresh clone."
            continue
        }

        # ${VAR:?message} is compose's "required, fail with this message". Any
        # such variable the example does not define is a setup dead end.
        $composeRequired = Select-String -LiteralPath $composePath -Pattern '\$\{([A-Za-z_][A-Za-z0-9_]*)\:\?' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        foreach ($key in $composeRequired) {
            if ($exampleKeys -contains $key) { Add-Pass "$composeFile requires $key, documented" }
            else { Add-Failure "$composeFile requires $key but .env.example does not define it." }
        }
    }

    # Every placeholder must be replaced before use, so the example must not
    # ship a value that would silently work.
    $unreplaced = Get-Content -LiteralPath (Join-Path $clone '.env.example') |
        Where-Object { $_ -match '^\s*[A-Za-z_][A-Za-z0-9_]*\s*=.*(change_me_locally|replace-me)' }
    if ($unreplaced) { Add-Pass "$($unreplaced.Count) placeholder value(s) still marked for replacement" }
    else { Add-Failure '.env.example has no placeholder markers, so a reader cannot tell what to change.' }

    # ------------------------------------------------------------------ 7
    Start-Step 'The solution restores and builds at zero warnings'
    Push-Location $clone
    try {
        & dotnet restore 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Add-Failure 'dotnet restore failed in a fresh clone.' }
        else {
            Add-Pass 'restore'
            $build = & dotnet build --no-restore --configuration Release 2>&1
            if ($LASTEXITCODE -ne 0) {
                Add-Failure 'dotnet build failed in a fresh clone.'
                $build | Select-Object -Last 25 | ForEach-Object { Write-Host "      $_" }
            }
            else {
                $warnings = ($build | Select-String -Pattern '^\s*(\d+) Warning\(s\)' | Select-Object -Last 1)
                $count = if ($warnings) { [int]$warnings.Matches[0].Groups[1].Value } else { -1 }
                if ($count -eq 0) { Add-Pass 'build, 0 warnings' }
                else { Add-Failure "Build produced $count warning(s); this project builds clean." }
            }
        }

        # -------------------------------------------------------------- 8
        Start-Step 'Suites that need no database pass'
        foreach ($suite in 'ArchitectureTests', 'UnitTests') {
            $result = & dotnet test "tests/GiftCardPlatform.$suite/GiftCardPlatform.$suite.csproj" `
                --no-build --configuration Release 2>&1
            $line = $result | Select-String -Pattern '(Passed!|Failed!).*Total:\s*\d+' | Select-Object -Last 1
            if ($LASTEXITCODE -eq 0) { Add-Pass "$suite $($line -replace '\s+', ' ')" }
            else {
                Add-Failure "$suite failed in a fresh clone."
                $result | Select-Object -Last 20 | ForEach-Object { Write-Host "      $_" }
            }
        }

        if ($IncludeIntegration) {
            Start-Step 'Integration suite'
            if (-not $env:GIFTCARD_TEST_CONNECTION) {
                Add-Failure 'GIFTCARD_TEST_CONNECTION is not set, so the integration suite cannot run.'
            }
            else {
                $result = & dotnet test 'tests/GiftCardPlatform.IntegrationTests/GiftCardPlatform.IntegrationTests.csproj' `
                    --no-build --configuration Release 2>&1
                $line = $result | Select-String -Pattern '(Passed!|Failed!).*Total:\s*\d+' | Select-Object -Last 1
                if ($LASTEXITCODE -eq 0) { Add-Pass "IntegrationTests $($line -replace '\s+', ' ')" }
                else {
                    Add-Failure 'Integration suite failed in a fresh clone.'
                    $result | Select-Object -Last 20 | ForEach-Object { Write-Host "      $_" }
                }
            }
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($KeepClone) {
        Write-Host ''
        Write-Host "Clone kept at $clone"
    }
    elseif (Test-Path -LiteralPath $clone) {
        Remove-Item -LiteralPath $clone -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "Fresh clone rehearsal failed with $($failures.Count) problem(s):" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    throw "$($failures.Count) fresh-clone problem(s)."
}

Write-Host 'Fresh clone rehearsal passed.' -ForegroundColor Green
Write-Host 'Not covered here: Docker, PostgreSQL provisioning, and the client repositories.'
