Set-StrictMode -Version Latest

$script:OpenGiftCardBackendRoot =
    (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:OpenGiftCardWorkspaceRoot =
    Split-Path $script:OpenGiftCardBackendRoot -Parent

function Get-OpenGiftCardRepositoryPaths {
    [CmdletBinding()]
    param()

    return [ordered]@{
        Backend = $script:OpenGiftCardBackendRoot
        Portal = Join-Path $script:OpenGiftCardWorkspaceRoot 'open-giftcard-portal'
        Cardholder = Join-Path $script:OpenGiftCardWorkspaceRoot 'open-giftcard-cardholder'
        Pos = Join-Path $script:OpenGiftCardWorkspaceRoot 'open-giftcard-pos'
    }
}

function Get-OpenGiftCardStackDirectory {
    [CmdletBinding()]
    param()

    return Join-Path $script:OpenGiftCardBackendRoot '.local\stack'
}

function Read-OpenGiftCardDotEnv {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $values = [ordered]@{}
    if (!(Test-Path -LiteralPath $Path)) {
        return $values
    }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -notmatch '^\s*([^#\s][^=]*)=(.*)$') {
            continue
        }

        $name = $matches[1].Trim()
        $value = $matches[2].Trim()
        if ($value.Length -ge 2 -and
            (($value.StartsWith('"') -and $value.EndsWith('"')) -or
             ($value.StartsWith("'") -and $value.EndsWith("'")))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$name] = $value
    }

    return $values
}

function Get-OpenGiftCardSetting {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Values,
        [Parameter(Mandatory)][string]$Name,
        [switch]$Required
    )

    $value = if ($Values.Contains($Name)) { [string]$Values[$Name] } else { '' }
    if ($Required -and [string]::IsNullOrWhiteSpace($value)) {
        throw "Required setting '$Name' is missing."
    }
    return $value
}

function Test-OpenGiftCardUrl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri)

    try {
        $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 3
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
    }
    catch {
        return $false
    }
}

function Test-OpenGiftCardPort {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Port)

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $attempt = $client.ConnectAsync('127.0.0.1', $Port)
        return $attempt.Wait(300) -and $client.Connected
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-OpenGiftCardUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($null -ne $Process -and $Process.HasExited) {
            throw "$Name exited before $Uri became ready. Check its stack log."
        }
        if (Test-OpenGiftCardUrl -Uri $Uri) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Name did not become ready at $Uri within $TimeoutSeconds seconds."
}

function Get-OpenGiftCardPsql {
    [CmdletBinding()]
    param()

    $command = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $postgresRoot = 'C:\Program Files\PostgreSQL'
    if (Test-Path -LiteralPath $postgresRoot) {
        $candidate = Get-ChildItem -LiteralPath $postgresRoot -Directory |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'bin\psql.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate
        }
    }

    throw 'psql was not found. Install the PostgreSQL client tools or add psql to PATH.'
}

function ConvertFrom-OpenGiftCardConnectionString {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ConnectionString)

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    # PowerShell adapts this type as a dictionary, so ordinary property syntax
    # creates a key named ConnectionString instead of calling the CLR setter.
    $builder.set_ConnectionString($ConnectionString)
    return ,$builder
}

function Invoke-OpenGiftCardPsqlScalar {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$Sql
    )

    $connection = ConvertFrom-OpenGiftCardConnectionString -ConnectionString $ConnectionString
    $hostName = if ($connection.ContainsKey('Host')) { $connection['Host'] } else { 'localhost' }
    $port = if ($connection.ContainsKey('Port')) { $connection['Port'] } else { 5432 }
    $database = $connection['Database']
    $username = if ($connection.ContainsKey('Username')) {
        $connection['Username']
    }
    else {
        $connection['User ID']
    }
    $password = $connection['Password']
    $psql = Get-OpenGiftCardPsql
    $previousPassword = $env:PGPASSWORD

    try {
        $env:PGPASSWORD = [string]$password
        $result = & $psql `
            --host $hostName `
            --port $port `
            --username $username `
            --dbname $database `
            --no-psqlrc `
            --set ON_ERROR_STOP=1 `
            --tuples-only `
            --no-align `
            --command $Sql
        if ($LASTEXITCODE -ne 0) {
            throw "PostgreSQL validation failed for database '$database'."
        }
        return ($result | Out-String).Trim()
    }
    finally {
        if ($null -eq $previousPassword) {
            Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        }
        else {
            $env:PGPASSWORD = $previousPassword
        }
    }
}

function Write-OpenGiftCardJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Path
    )

    $directory = Split-Path $Path -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Start-OpenGiftCardProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [string]$RedirectStandardOutput,
        [string]$RedirectStandardError,
        [switch]$Wait
    )

    # Windows PowerShell 5.1 has no Start-Process -Environment parameter.
    # Change only this process, start the child, then restore the exact prior
    # values. The child inherits the temporary snapshot before it is restored.
    $previous = [ordered]@{}
    try {
        foreach ($name in $Environment.Keys) {
            $item = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            $previous[$name] = [pscustomobject]@{
                Existed = $null -ne $item
                Value = if ($null -ne $item) { $item.Value } else { $null }
            }
            Set-Item -LiteralPath "Env:$name" -Value ([string]$Environment[$name])
        }

        $parameters = @{
            FilePath = $FilePath
            ArgumentList = $ArgumentList
            WorkingDirectory = $WorkingDirectory
            WindowStyle = 'Hidden'
            PassThru = $true
        }
        if (![string]::IsNullOrWhiteSpace($RedirectStandardOutput)) {
            $parameters['RedirectStandardOutput'] = $RedirectStandardOutput
        }
        if (![string]::IsNullOrWhiteSpace($RedirectStandardError)) {
            $parameters['RedirectStandardError'] = $RedirectStandardError
        }
        if ($Wait) {
            $parameters['Wait'] = $true
        }
        return Start-Process @parameters
    }
    finally {
        foreach ($name in $Environment.Keys) {
            if ($previous[$name].Existed) {
                Set-Item -LiteralPath "Env:$name" -Value $previous[$name].Value
            }
            else {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
        }
    }
}

function Protect-OpenGiftCardLocalValue {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    if ($env:OS -ne 'Windows_NT') {
        throw 'Local secret persistence currently requires Windows DPAPI.'
    }
    $secureValue = ConvertTo-SecureString -String $Value -AsPlainText -Force
    return ConvertFrom-SecureString -SecureString $secureValue
}

function Unprotect-OpenGiftCardLocalValue {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    if ($env:OS -ne 'Windows_NT') {
        throw 'Local secret persistence currently requires Windows DPAPI.'
    }
    $secureValue = ConvertTo-SecureString -String $Value
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}
