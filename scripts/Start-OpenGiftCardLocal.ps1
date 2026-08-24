[CmdletBinding()]
param(
    [switch]$UseExisting,
    [switch]$SkipBuild,
    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 180,
    [string]$PortalConnectionString = $env:ConnectionStrings__Portal,
    [string]$CardholderConnectionString = $env:ConnectionStrings__Cardholder,
    [string]$PosClientSecret = $env:Pos__ClientSecret
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenGiftCardLocal.Common.ps1')

$paths = Get-OpenGiftCardRepositoryPaths
foreach ($entry in $paths.GetEnumerator()) {
    if (!(Test-Path -LiteralPath $entry.Value -PathType Container)) {
        throw "Sibling repository '$($entry.Key)' was not found at '$($entry.Value)'."
    }
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$pnpm = (Get-Command pnpm -ErrorAction Stop).Source
$stackDirectory = Get-OpenGiftCardStackDirectory
$logDirectory = Join-Path $stackDirectory 'logs'
$statePath = Join-Path $stackDirectory 'processes.json'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

if (Test-Path -LiteralPath $statePath) {
    $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $liveManaged = @($previous.Services | Where-Object {
        if (!$_.Managed) { return $false }
        $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) { return $false }
        $recorded = [DateTimeOffset]::Parse($_.ProcessStartedAtUtc).UtcDateTime
        return [Math]::Abs(
            ($process.StartTime.ToUniversalTime() - $recorded).TotalSeconds) -le 2
    })
    if ($liveManaged.Count -gt 0) {
        throw "A managed local stack is already recorded. Run scripts\Stop-OpenGiftCardLocal.ps1 first."
    }
}

$backendEnv = Read-OpenGiftCardDotEnv -Path (Join-Path $paths.Backend '.env')
$portalEnv = Read-OpenGiftCardDotEnv -Path (Join-Path $paths.Portal '.env')
$cardholderEnv = Read-OpenGiftCardDotEnv -Path (Join-Path $paths.Cardholder '.env')
$posEnv = Read-OpenGiftCardDotEnv -Path (Join-Path $paths.Pos '.env')

if ([string]::IsNullOrWhiteSpace($PortalConnectionString) -and
    $portalEnv.Contains('ConnectionStrings__Portal')) {
    $PortalConnectionString = [string]$portalEnv['ConnectionStrings__Portal']
}
if ([string]::IsNullOrWhiteSpace($CardholderConnectionString) -and
    $cardholderEnv.Contains('ConnectionStrings__Cardholder')) {
    $CardholderConnectionString = [string]$cardholderEnv['ConnectionStrings__Cardholder']
}
if ([string]::IsNullOrWhiteSpace($PosClientSecret) -and
    $posEnv.Contains('Pos__ClientSecret')) {
    $PosClientSecret = [string]$posEnv['Pos__ClientSecret']
}

$services = [System.Collections.Generic.List[object]]::new()

function Save-State {
    Write-OpenGiftCardJson -Path $statePath -Value ([ordered]@{
        Version = 1
        StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Services = @($services)
    })
}

function Add-ExistingService {
    param([string]$Name, [int]$Port, [string]$ReadyUri)

    $services.Add([pscustomobject]@{
        Name = $Name
        Port = $Port
        ReadyUri = $ReadyUri
        Managed = $false
        ProcessId = $null
        ProcessName = $null
        ProcessStartedAtUtc = $null
        StandardOutputLog = $null
        StandardErrorLog = $null
    })
    Save-State
}

function Start-StackProcess {
    param(
        [string]$Name,
        [int]$Port,
        [string]$ReadyUri,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory,
        [hashtable]$Environment
    )

    if (Test-OpenGiftCardUrl -Uri $ReadyUri) {
        if (!$UseExisting) {
            throw "$Name is already healthy on port $Port. Pass -UseExisting to keep and verify it."
        }
        Write-Host "$Name is already healthy on port $Port; leaving it unmanaged."
        Add-ExistingService -Name $Name -Port $Port -ReadyUri $ReadyUri
        return
    }
    if (Test-OpenGiftCardPort -Port $Port) {
        throw "Port $Port is occupied but $Name is not healthy at $ReadyUri."
    }

    $safeName = $Name.ToLowerInvariant().Replace(' ', '-')
    $outLog = Join-Path $logDirectory "$safeName.out.log"
    $errorLog = Join-Path $logDirectory "$safeName.err.log"
    $process = Start-OpenGiftCardProcess `
        -FilePath $FilePath `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory `
        -Environment $Environment `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errorLog

    $services.Add([pscustomobject]@{
        Name = $Name
        Port = $Port
        ReadyUri = $ReadyUri
        Managed = $true
        ProcessId = $process.Id
        ProcessName = $process.ProcessName
        ProcessStartedAtUtc = $process.StartTime.ToUniversalTime().ToString('O')
        StandardOutputLog = $outLog
        StandardErrorLog = $errorLog
    })
    Save-State
    Wait-OpenGiftCardUrl `
        -Name $Name `
        -Uri $ReadyUri `
        -TimeoutSeconds $TimeoutSeconds `
        -Process $process
    Write-Host "$Name is ready on port $Port."
}

$runArguments = @('run', '--no-launch-profile')
if ($SkipBuild) {
    $runArguments += '--no-build'
}

try {
    $backendReady = 'http://127.0.0.1:5143/health/ready'
    $backendAlreadyReady = Test-OpenGiftCardUrl -Uri $backendReady
    if (!$backendAlreadyReady) {
        $migrationConnection = Get-OpenGiftCardSetting `
            -Values $backendEnv `
            -Name 'GIFTCARD_MIGRATIONS_CONNECTION' `
            -Required
        $migrationOut = Join-Path $logDirectory 'backend-migrations.out.log'
        $migrationError = Join-Path $logDirectory 'backend-migrations.err.log'
        $migration = Start-OpenGiftCardProcess `
            -FilePath $dotnet `
            -ArgumentList @(
                'run', '--no-launch-profile',
                '--project', 'src\GiftCardPlatform.Api',
                '--', '--migrate') `
            -WorkingDirectory $paths.Backend `
            -Environment @{ GIFTCARD_MIGRATIONS_CONNECTION = $migrationConnection } `
            -RedirectStandardOutput $migrationOut `
            -RedirectStandardError $migrationError `
            -Wait
        if ($migration.ExitCode -ne 0) {
            throw "Backend migrations failed. Check '$migrationError'."
        }
    }

    $backendProcessEnv = if ($backendAlreadyReady) { @{} } else {
        @{
            ASPNETCORE_ENVIRONMENT = 'Development'
            ASPNETCORE_URLS = 'http://127.0.0.1:5143'
            ConnectionStrings__Default = Get-OpenGiftCardSetting -Values $backendEnv -Name 'ConnectionStrings__Default' -Required
            Authentication__Jwt__SigningKey = Get-OpenGiftCardSetting -Values $backendEnv -Name 'Authentication__Jwt__SigningKey' -Required
            Bootstrap__PlatformAdministrator__Secret = Get-OpenGiftCardSetting -Values $backendEnv -Name 'Bootstrap__PlatformAdministrator__Secret' -Required
            Partners__EpinDeliveryKey = Get-OpenGiftCardSetting -Values $backendEnv -Name 'Partners__EpinDeliveryKey' -Required
            Demo__Seed__Enabled = 'true'
            DataProtection__KeysPath = Join-Path $stackDirectory 'keys\backend'
        }
    }
    Start-StackProcess `
        -Name 'Backend' `
        -Port 5143 `
        -ReadyUri $backendReady `
        -FilePath $dotnet `
        -ArgumentList ($runArguments + @('--project', 'src\GiftCardPlatform.Api')) `
        -WorkingDirectory $paths.Backend `
        -Environment $backendProcessEnv

    if (!(Test-OpenGiftCardUrl -Uri 'http://127.0.0.1:5179/health/ready') -and
        [string]::IsNullOrWhiteSpace($PortalConnectionString)) {
        throw 'ConnectionStrings__Portal is required to start the portal BFF. Set it in the environment, pass -PortalConnectionString, or add an ignored portal .env file.'
    }
    $portalProcessEnv = @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = 'http://127.0.0.1:5179'
        Backend__BaseUrl = 'http://127.0.0.1:5143'
        DataProtection__KeysPath = Join-Path $stackDirectory 'keys\portal'
    }
    if (![string]::IsNullOrWhiteSpace($PortalConnectionString)) {
        $portalProcessEnv['ConnectionStrings__Portal'] = $PortalConnectionString
    }
    Start-StackProcess `
        -Name 'Portal BFF' `
        -Port 5179 `
        -ReadyUri 'http://127.0.0.1:5179/health/ready' `
        -FilePath $dotnet `
        -ArgumentList ($runArguments + @('--project', 'src\GiftCardPortal.Bff')) `
        -WorkingDirectory $paths.Portal `
        -Environment $portalProcessEnv

    $portalWebRoot = Join-Path $paths.Portal 'src\GiftCardPortal.Web'
    if (!(Test-OpenGiftCardUrl -Uri 'http://127.0.0.1:5173/') -and
        !(Test-Path -LiteralPath (Join-Path $portalWebRoot 'node_modules'))) {
        throw "Portal dependencies are missing. Run 'pnpm install --frozen-lockfile' in '$portalWebRoot'."
    }
    Start-StackProcess `
        -Name 'Portal Web' `
        -Port 5173 `
        -ReadyUri 'http://127.0.0.1:5173/' `
        -FilePath $pnpm `
        -ArgumentList @('run', 'dev') `
        -WorkingDirectory $portalWebRoot `
        -Environment @{}

    if (!(Test-OpenGiftCardUrl -Uri 'http://127.0.0.1:5180/health/ready') -and
        [string]::IsNullOrWhiteSpace($CardholderConnectionString)) {
        throw 'ConnectionStrings__Cardholder is required to start the cardholder app. Set it in the environment, pass -CardholderConnectionString, or add an ignored cardholder .env file.'
    }
    $cardholderProcessEnv = @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = 'http://127.0.0.1:5180'
        Backend__BaseUrl = 'http://127.0.0.1:5143'
        DataProtection__KeysPath = Join-Path $stackDirectory 'keys\cardholder'
    }
    if (![string]::IsNullOrWhiteSpace($CardholderConnectionString)) {
        $cardholderProcessEnv['ConnectionStrings__Cardholder'] = $CardholderConnectionString
    }
    Start-StackProcess `
        -Name 'Cardholder' `
        -Port 5180 `
        -ReadyUri 'http://127.0.0.1:5180/health/ready' `
        -FilePath $dotnet `
        -ArgumentList ($runArguments + @('--project', 'src\GiftCardCardholder.Web')) `
        -WorkingDirectory $paths.Cardholder `
        -Environment $cardholderProcessEnv

    $posProcessEnv = @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = 'http://127.0.0.1:5190'
        Pos__BackendBaseUrl = 'http://127.0.0.1:5143'
        DataProtection__KeysPath = Join-Path $stackDirectory 'keys\pos'
    }
    if (![string]::IsNullOrWhiteSpace($PosClientSecret)) {
        $posProcessEnv['Pos__ClientSecret'] = $PosClientSecret
    }
    Start-StackProcess `
        -Name 'POS' `
        -Port 5190 `
        -ReadyUri 'http://127.0.0.1:5190/health/ready' `
        -FilePath $dotnet `
        -ArgumentList ($runArguments + @('--project', 'src\GiftCardPos.Web')) `
        -WorkingDirectory $paths.Pos `
        -Environment $posProcessEnv
}
catch {
    Write-Error $_
    Write-Warning 'Startup did not complete. Run scripts\Stop-OpenGiftCardLocal.ps1 to stop only processes recorded by this runner.'
    exit 1
}

Write-Host ''
Write-Host 'Open Giftcard local stack is ready:'
Write-Host '  Backend:   http://127.0.0.1:5143/demo'
Write-Host '  Portal:    http://127.0.0.1:5173/'
Write-Host '  Portal BFF http://127.0.0.1:5179/'
Write-Host '  Cardholder http://127.0.0.1:5180/'
Write-Host '  POS:       http://127.0.0.1:5190/'
Write-Host ''
Write-Host 'Run scripts\Test-OpenGiftCardSmoke.ps1 for the live transaction gate.'
