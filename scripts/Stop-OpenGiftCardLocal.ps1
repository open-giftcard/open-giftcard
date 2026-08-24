[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenGiftCardLocal.Common.ps1')

$statePath = Join-Path (Get-OpenGiftCardStackDirectory) 'processes.json'
if (!(Test-Path -LiteralPath $statePath)) {
    Write-Host 'No managed Open Giftcard local stack is recorded.'
    return
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$managed = @($state.Services | Where-Object { $_.Managed })

$processTable = @()
if ($managed.Count -gt 0) {
    try {
        $processTable = @(Get-CimInstance Win32_Process -ErrorAction Stop)
    }
    catch {
        Write-Warning 'Child process discovery is unavailable. Parent processes will still be stopped.'
    }
}

function Get-DescendantIds {
    param([int]$ParentId)

    $children = @($processTable | Where-Object { $_.ParentProcessId -eq $ParentId })
    $ids = [System.Collections.Generic.List[int]]::new()
    foreach ($child in $children) {
        foreach ($descendant in Get-DescendantIds -ParentId $child.ProcessId) {
            $ids.Add($descendant)
        }
        $ids.Add([int]$child.ProcessId)
    }
    return @($ids)
}

for ($index = $managed.Count - 1; $index -ge 0; $index--) {
    $service = $managed[$index]
    $process = Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        continue
    }

    $recordedStart = [DateTimeOffset]::Parse($service.ProcessStartedAtUtc).UtcDateTime
    $actualStart = $process.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -gt 2) {
        Write-Warning "Skipping $($service.Name): PID $($service.ProcessId) now belongs to another process."
        continue
    }

    if ($PSCmdlet.ShouldProcess("$($service.Name) PID $($service.ProcessId)", 'Stop')) {
        $descendants = @(Get-DescendantIds -ParentId $service.ProcessId)
        foreach ($id in $descendants) {
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
        Stop-Process -Id $service.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "Stopped $($service.Name)."
    }
}

if ($PSCmdlet.ShouldProcess($statePath, 'Remove process record')) {
    Remove-Item -LiteralPath $statePath -Force
}

$external = @($state.Services | Where-Object { !$_.Managed })
if ($external.Count -gt 0) {
    Write-Host 'Existing services were left running because the local runner did not start them.'
}
