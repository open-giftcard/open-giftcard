[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CurrentArtifact,
    [Parameter(Mandatory)]
    [string]$RollbackArtifact,
    [Parameter(Mandatory)]
    [string]$ConnectionString,
    [Parameter(Mandatory)]
    [string]$DataProtectionKeysPath,
    [ValidateRange(1024, 65534)]
    [int]$CurrentPort = 5243,
    [ValidateRange(1024, 65534)]
    [int]$RollbackPort = 5244,
    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 45,
    [switch]$KeepLogs
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runId = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$logRoot = Join-Path $repoRoot ".local\rollback-probe\$runId"

function Resolve-ApiArtifact([string]$Path, [string]$Label) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        if ([IO.Path]::GetFileName($resolved) -ne 'GiftCardPlatform.Api.dll') {
            throw "$Label must be GiftCardPlatform.Api.dll or a directory containing it."
        }
        return $resolved
    }

    $candidate = Join-Path $resolved 'GiftCardPlatform.Api.dll'
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Label does not contain GiftCardPlatform.Api.dll: $resolved"
    }
    return $candidate
}

function Assert-IsolatedDatabase([string]$Value) {
    $match = [regex]::Match(
        $Value,
        '(?i)(?:^|;)\s*Database\s*=\s*([^;]+)')
    if (-not $match.Success) {
        throw 'ConnectionString must contain an explicit Database value.'
    }

    $database = $match.Groups[1].Value.Trim().Trim('"')
    if ($database -notmatch '^giftcard_recovery_test_[a-zA-Z0-9_]+$') {
        throw "Refusing rollback probe database '$database'. Use a guarded giftcard_recovery_test_* restore."
    }
}

function Assert-PortFree([int]$Port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync('127.0.0.1', $Port)
        if ($task.Wait(250) -and $client.Connected) {
            throw "Port $Port is already in use."
        }
    }
    catch [AggregateException] {
        # Connection refused means the port is available.
    }
    catch [Net.Sockets.SocketException] {
        # Connection refused means the port is available.
    }
    finally {
        $client.Dispose()
    }
}

function Wait-Ready([int]$Port, [Diagnostics.Process]$Process, [string]$Label) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $uri = "http://127.0.0.1:$Port/health/ready"
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "$Label exited before readiness with code $($Process.ExitCode)."
        }

        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $uri -TimeoutSec 2
            if ($response.StatusCode -eq 200 -and
                $response.Content -match '"status"\s*:\s*"ready"') {
                return
            }
        }
        catch {
            # The process may still be binding or checking schema readiness.
        }
        Start-Sleep -Milliseconds 250
    }

    throw "$Label did not become ready within $StartupTimeoutSeconds seconds."
}

function Test-Artifact(
    [string]$Dotnet,
    [string]$Artifact,
    [int]$Port,
    [string]$Label
) {
    Assert-PortFree $Port
    $stdout = Join-Path $logRoot "$Label.stdout.log"
    $stderr = Join-Path $logRoot "$Label.stderr.log"
    $previous = @{
        Connection = $env:ConnectionStrings__Default
        Keys = $env:DataProtection__KeysPath
        Environment = $env:ASPNETCORE_ENVIRONMENT
        Urls = $env:ASPNETCORE_URLS
        Dispatch = $env:Notifications__DispatchEnabled
        Demo = $env:Demo__Seed__Enabled
        Checkpoints = $env:Audit__Checkpoints__Enabled
    }

    try {
        $env:ConnectionStrings__Default = $ConnectionString
        $env:DataProtection__KeysPath = (Resolve-Path -LiteralPath $DataProtectionKeysPath).Path
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
        $env:Notifications__DispatchEnabled = 'false'
        $env:Demo__Seed__Enabled = 'false'
        $env:Audit__Checkpoints__Enabled = 'false'

        $process = Start-Process -FilePath $Dotnet `
            -ArgumentList @($Artifact) `
            -WorkingDirectory (Split-Path $Artifact -Parent) `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr `
            -PassThru
    }
    finally {
        $env:ConnectionStrings__Default = $previous.Connection
        $env:DataProtection__KeysPath = $previous.Keys
        $env:ASPNETCORE_ENVIRONMENT = $previous.Environment
        $env:ASPNETCORE_URLS = $previous.Urls
        $env:Notifications__DispatchEnabled = $previous.Dispatch
        $env:Demo__Seed__Enabled = $previous.Demo
        $env:Audit__Checkpoints__Enabled = $previous.Checkpoints
    }

    try {
        Wait-Ready $Port $process $Label
        Write-Host "$Label artifact became ready on port $Port."
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
            $null = $process.WaitForExit(10000)
        }
        $process.Dispose()
    }
}

Assert-IsolatedDatabase $ConnectionString
if ($CurrentPort -eq $RollbackPort) {
    throw 'CurrentPort and RollbackPort must be different.'
}

$currentDll = Resolve-ApiArtifact $CurrentArtifact 'CurrentArtifact'
$rollbackDll = Resolve-ApiArtifact $RollbackArtifact 'RollbackArtifact'
$keys = (Resolve-Path -LiteralPath $DataProtectionKeysPath).Path
if (@(Get-ChildItem -LiteralPath $keys -File -Recurse).Count -eq 0) {
    throw "Data Protection key ring '$keys' contains no files."
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

try {
    Test-Artifact $dotnet $currentDll $CurrentPort 'current'
    Test-Artifact $dotnet $rollbackDll $RollbackPort 'rollback'
    Write-Host 'Backend rollback compatibility passed: both artifacts are ready against the upgraded isolated database and shared key ring.'
}
finally {
    if ((Test-Path -LiteralPath $logRoot) -and -not $KeepLogs) {
        $resolvedLogs = (Resolve-Path -LiteralPath $logRoot).Path
        $allowedRoot = (Resolve-Path -LiteralPath (Split-Path $logRoot -Parent)).Path
        if (-not $resolvedLogs.StartsWith(
                $allowedRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected log path '$resolvedLogs'."
        }
        Remove-Item -LiteralPath $resolvedLogs -Recurse -Force
    }
}
