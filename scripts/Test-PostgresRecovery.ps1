[CmdletBinding()]
param(
    [string]$PostgresHost = 'localhost',
    [ValidateRange(1, 65535)]
    [int]$PostgresPort = 5432,
    [string]$AdminUser = 'postgres',
    [string]$AdminPassword = $env:POSTGRES_SUPERUSER_PASSWORD,
    [string]$SourceDatabase = 'giftcard_register_test',
    [string]$RestoreDatabase = '',
    [string[]]$KeyRingPath = @(),
    [switch]$KeepRestoreDatabase,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$drillRoot = Join-Path $repoRoot '.local\recovery-drill'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '_' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$artifactRoot = Join-Path $drillRoot $runId
$backupPath = Join-Path $artifactRoot 'database.dump'
$restoreCreated = $false
$startedAt = [DateTimeOffset]::UtcNow

if ([string]::IsNullOrWhiteSpace($RestoreDatabase)) {
    $RestoreDatabase = "giftcard_recovery_test_$runId"
}

function Assert-SafeRestoreDatabase([string]$Value) {
    if ($Value -notmatch '^giftcard_recovery_test_[a-zA-Z0-9_]+$') {
        throw "Refusing restore database '$Value'. Its name must start with giftcard_recovery_test_."
    }

    if ([string]::Equals($Value, $SourceDatabase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The restore database must be different from the source database.'
    }
}

function Find-PostgresTool([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $postgresRoot = 'C:\Program Files\PostgreSQL'
    if (Test-Path -LiteralPath $postgresRoot) {
        $candidate = Get-ChildItem -LiteralPath $postgresRoot -Directory |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "bin\$Name.exe" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate
        }
    }

    throw "$Name was not found. Install PostgreSQL client tools or add them to PATH."
}

function Invoke-PostgresTool(
    [string]$Tool,
    [string[]]$Arguments,
    [switch]$Capture
) {
    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $AdminPassword
        if ($Capture) {
            $output = & $Tool @Arguments
        }
        else {
            & $Tool @Arguments
        }

        if ($LASTEXITCODE -ne 0) {
            throw "PostgreSQL tool '$([IO.Path]::GetFileName($Tool))' failed."
        }

        if ($Capture) {
            return @($output)
        }
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

function Invoke-Query([string]$Psql, [string]$Database, [string]$Sql) {
    return @(Invoke-PostgresTool -Tool $Psql -Capture -Arguments @(
        '--host', $PostgresHost,
        '--port', $PostgresPort,
        '--username', $AdminUser,
        '--dbname', $Database,
        '--no-psqlrc',
        '--set', 'ON_ERROR_STOP=1',
        '--tuples-only',
        '--no-align',
        '--command', $Sql
    )) | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -ne '' }
}

function Get-CatalogManifest([string]$Psql, [string]$Database) {
    return @(Invoke-Query $Psql $Database @'
select format('%I.%I|%I|%s|%s',
              n.nspname,
              c.relname,
              pg_get_userbyid(c.relowner),
              c.relrowsecurity,
              c.relforcerowsecurity)
from pg_class c
join pg_namespace n on n.oid = c.relnamespace
where c.relkind in ('r', 'p')
  and n.nspname not in ('pg_catalog', 'information_schema')
  and n.nspname not like 'pg_toast%'
order by n.nspname, c.relname;
'@)
}

function Get-RowCountManifest([string]$Psql, [string]$Database) {
    $tables = @(Invoke-Query $Psql $Database @'
select format('%I.%I', n.nspname, c.relname)
from pg_class c
join pg_namespace n on n.oid = c.relnamespace
where c.relkind in ('r', 'p')
  and n.nspname not in ('pg_catalog', 'information_schema')
  and n.nspname not like 'pg_toast%'
order by n.nspname, c.relname;
'@)
    $manifest = foreach ($table in $tables) {
        $count = @(Invoke-Query $Psql $Database "select count(*) from $table;")
        "$table=$($count[0])"
    }
    return @($manifest)
}

function Get-SequenceManifest([string]$Psql, [string]$Database) {
    $sequences = @(Invoke-Query $Psql $Database @'
select format('%I.%I', schemaname, sequencename)
from pg_sequences
where schemaname not in ('pg_catalog', 'information_schema')
order by schemaname, sequencename;
'@)
    $manifest = foreach ($sequence in $sequences) {
        $state = @(Invoke-Query $Psql $Database "select last_value || '|' || is_called from $sequence;")
        "$sequence=$($state[0])"
    }
    return @($manifest)
}

function Get-KeyRingManifest([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $files = @(Get-ChildItem -LiteralPath $resolved -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Key ring '$resolved' contains no files."
    }

    return @($files | ForEach-Object {
        $relative = $_.FullName.Substring($resolved.Length).TrimStart('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$relative|$hash|$($_.Length)"
    })
}

function Assert-ManifestEqual(
    [string]$Name,
    [AllowNull()]
    [string[]]$Expected,
    [AllowNull()]
    [string[]]$Actual
) {
    $expectedItems = @($Expected | Where-Object { $null -ne $_ })
    $actualItems = @($Actual | Where-Object { $null -ne $_ })
    $expectedText = $expectedItems -join "`n"
    $actualText = $actualItems -join "`n"
    if (-not [string]::Equals(
            $expectedText,
            $actualText,
            [StringComparison]::Ordinal)) {
        if ($expectedItems.Count -gt 0 -and $actualItems.Count -gt 0) {
            $difference = @(Compare-Object `
                -ReferenceObject $expectedItems `
                -DifferenceObject $actualItems)
            $preview = ($difference | Select-Object -First 10 | Out-String).Trim()
        }
        else {
            $preview = "Expected $($expectedItems.Count) entries; restored $($actualItems.Count)."
        }
        throw "$Name differs after restore.`n$preview"
    }
}

Assert-SafeRestoreDatabase $RestoreDatabase
if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
    throw 'AdminPassword or POSTGRES_SUPERUSER_PASSWORD is required.'
}

$pgDump = Find-PostgresTool 'pg_dump'
$pgRestore = Find-PostgresTool 'pg_restore'
$createdb = Find-PostgresTool 'createdb'
$dropdb = Find-PostgresTool 'dropdb'
$psql = Find-PostgresTool 'psql'

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

try {
    $existing = @(Invoke-Query $psql 'postgres' (
        "select 1 from pg_database where datname = '" +
        $RestoreDatabase.Replace("'", "''") + "';"))
    if ($existing.Count -ne 0) {
        throw "Restore database '$RestoreDatabase' already exists. Choose a new guarded name."
    }

    Write-Host "Capturing source manifests from '$SourceDatabase'..."
    $sourceCatalog = Get-CatalogManifest $psql $SourceDatabase
    $sourceRows = Get-RowCountManifest $psql $SourceDatabase
    $sourceSequences = Get-SequenceManifest $psql $SourceDatabase

    Write-Host 'Creating PostgreSQL custom-format backup...'
    Invoke-PostgresTool -Tool $pgDump -Arguments @(
        '--host', $PostgresHost,
        '--port', $PostgresPort,
        '--username', $AdminUser,
        '--dbname', $SourceDatabase,
        '--format', 'custom',
        '--file', $backupPath
    )
    if (-not (Test-Path -LiteralPath $backupPath) -or
        (Get-Item -LiteralPath $backupPath).Length -eq 0) {
        throw 'The database backup was not created or is empty.'
    }

    $keyRingManifests = @()
    for ($index = 0; $index -lt $KeyRingPath.Count; $index++) {
        $sourcePath = (Resolve-Path -LiteralPath $KeyRingPath[$index]).Path
        $backupKeyPath = Join-Path $artifactRoot "key-rings\$index"
        New-Item -ItemType Directory -Path $backupKeyPath -Force | Out-Null
        Get-ChildItem -LiteralPath $sourcePath -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $backupKeyPath -Recurse -Force
        }
        $sourceManifest = Get-KeyRingManifest $sourcePath
        $backupManifest = Get-KeyRingManifest $backupKeyPath
        Assert-ManifestEqual "Key ring $index backup" $sourceManifest $backupManifest
        $keyRingManifests += ,@($sourceManifest)
    }

    Write-Host "Creating guarded restore database '$RestoreDatabase'..."
    Invoke-PostgresTool -Tool $createdb -Arguments @(
        '--host', $PostgresHost,
        '--port', $PostgresPort,
        '--username', $AdminUser,
        $RestoreDatabase
    )
    $restoreCreated = $true

    Write-Host 'Restoring backup...'
    Invoke-PostgresTool -Tool $pgRestore -Arguments @(
        '--host', $PostgresHost,
        '--port', $PostgresPort,
        '--username', $AdminUser,
        '--dbname', $RestoreDatabase,
        '--exit-on-error',
        $backupPath
    )

    Write-Host 'Comparing catalog, RLS, row-count, and sequence manifests...'
    Assert-ManifestEqual 'Database catalog and RLS manifest' `
        $sourceCatalog (Get-CatalogManifest $psql $RestoreDatabase)
    Assert-ManifestEqual 'Database row-count manifest' `
        $sourceRows (Get-RowCountManifest $psql $RestoreDatabase)
    Assert-ManifestEqual 'Database sequence manifest' `
        $sourceSequences (Get-SequenceManifest $psql $RestoreDatabase)

    for ($index = 0; $index -lt $keyRingManifests.Count; $index++) {
        $backupKeyPath = Join-Path $artifactRoot "key-rings\$index"
        $restoredKeyPath = Join-Path $artifactRoot "restored-key-rings\$index"
        New-Item -ItemType Directory -Path $restoredKeyPath -Force | Out-Null
        Get-ChildItem -LiteralPath $backupKeyPath -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $restoredKeyPath -Recurse -Force
        }
        Assert-ManifestEqual "Key ring $index restore" `
            $keyRingManifests[$index] (Get-KeyRingManifest $restoredKeyPath)
    }

    $elapsed = [DateTimeOffset]::UtcNow - $startedAt
    Write-Host (("Recovery drill passed in {0:n1}s: database objects, RLS flags, " +
        "row counts, sequences, and {1} key ring(s) match.") -f `
        $elapsed.TotalSeconds, $KeyRingPath.Count)
}
finally {
    if ($restoreCreated -and -not $KeepRestoreDatabase) {
        Assert-SafeRestoreDatabase $RestoreDatabase
        Invoke-PostgresTool -Tool $dropdb -Arguments @(
            '--host', $PostgresHost,
            '--port', $PostgresPort,
            '--username', $AdminUser,
            '--if-exists',
            $RestoreDatabase
        )
        Write-Host "Removed guarded restore database '$RestoreDatabase'."
    }

    if ((Test-Path -LiteralPath $artifactRoot) -and -not $KeepArtifacts) {
        $resolvedArtifacts = (Resolve-Path -LiteralPath $artifactRoot).Path
        $resolvedDrillRoot = (Resolve-Path -LiteralPath $drillRoot).Path
        if (-not $resolvedArtifacts.StartsWith(
                $resolvedDrillRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected artifact path '$resolvedArtifacts'."
        }
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
        Write-Host 'Removed temporary recovery artifacts.'
    }
}
