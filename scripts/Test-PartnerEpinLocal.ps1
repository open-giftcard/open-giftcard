[CmdletBinding()]
param(
    [string]$PostgresHost = 'localhost',
    [ValidateRange(1, 65535)]
    [int]$PostgresPort = 5432,
    [string]$AdminUser = 'postgres',
    [string]$Database = 'giftcard_partner_epin_test',
    [switch]$AllIntegrationTests,
    [switch]$IntegrationOnly,
    [switch]$SkipCardholder
)

$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cardholderRoot = Join-Path (Split-Path $backendRoot -Parent) 'open-giftcard-cardholder'
$previousTestConnection = $env:GIFTCARD_TEST_CONNECTION

function Assert-DisposableDatabaseName([string]$Value) {
    if ($Value -notmatch '^giftcard_partner_epin_test(?:_[a-z0-9_]+)?$') {
        throw "Refusing database '$Value'. Use 'giftcard_partner_epin_test' or that prefix plus a safe suffix."
    }
}

function Find-Psql {
    $command = Get-Command psql -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    if ($IsWindows) {
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
    }

    throw 'psql was not found. Install PostgreSQL client tools or add psql to PATH.'
}

function Invoke-Psql(
    [string]$Psql,
    [string]$TargetDatabase,
    [string]$Password,
    [string]$Sql,
    [switch]$Scalar
) {
    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $Password
        $arguments = @(
            '--host', $PostgresHost,
            '--port', $PostgresPort,
            '--username', $AdminUser,
            '--dbname', $TargetDatabase,
            '--no-psqlrc',
            '--set', 'ON_ERROR_STOP=1'
        )
        if ($Scalar) {
            $arguments += @('--tuples-only', '--no-align')
        }
        $arguments += @('--command', $Sql)

        $result = & $Psql @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "PostgreSQL command failed for database '$TargetDatabase'."
        }
        if ($Scalar) {
            return ($result | Out-String).Trim()
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

function Quote-ConnectionStringValue([string]$Value) {
    return '"' + $Value.Replace('"', '""') + '"'
}

function Invoke-Dotnet([string[]]$Arguments, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Write-TrxFailures([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) {
        Write-Warning "The test runner did not create the expected diagnostic file '$Path'."
        return
    }

    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $namespaces = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace(
        't',
        'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    )
    $failures = $document.SelectNodes(
        '//t:UnitTestResult[@outcome="Failed"]',
        $namespaces
    )
    if ($failures.Count -eq 0) {
        Write-Warning "The runner failed, but '$Path' contains no failed test result."
        return
    }

    Write-Host ''
    Write-Host "Failed integration tests ($($failures.Count)):" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $($failure.testName)" -ForegroundColor Red
        $message = $failure.SelectSingleNode(
            't:Output/t:ErrorInfo/t:Message',
            $namespaces
        )
        if ($null -ne $message -and ![string]::IsNullOrWhiteSpace($message.InnerText)) {
            Write-Host ('    ' + $message.InnerText.Replace("`n", "`n    "))
        }
    }
    Write-Host "Full diagnostics: $Path"
}

Assert-DisposableDatabaseName $Database
$psql = Find-Psql
$credential = Get-Credential -UserName $AdminUser -Message (
    "PostgreSQL administrator for $PostgresHost`:$PostgresPort. " +
    'The password stays in this PowerShell process and is cleared after the tests.'
)
if ($null -eq $credential) {
    throw 'PostgreSQL credentials are required.'
}

$plainPassword = $credential.GetNetworkCredential().Password
try {
    Write-Host "Checking local PostgreSQL at $PostgresHost`:$PostgresPort..."
    $psqlArguments = @{
        Psql = $psql
        TargetDatabase = 'postgres'
        Password = $plainPassword
        Sql = 'select rolsuper or rolcreaterole from pg_roles where rolname = current_user;'
        Scalar = $true
    }
    $canProvision = Invoke-Psql @psqlArguments
    if ($canProvision -ne 't') {
        throw "Role '$AdminUser' must be a superuser or have CREATEROLE for the integration fixture."
    }

    $psqlArguments.Sql = "select 1 from pg_database where datname = '$Database';"
    $exists = Invoke-Psql @psqlArguments
    if ($exists -ne '1') {
        Write-Host "Creating guarded disposable database '$Database'..."
        $psqlArguments.Sql = "create database `"$Database`";"
        $psqlArguments.Scalar = $false
        Invoke-Psql @psqlArguments
    }

    $hostValue = Quote-ConnectionStringValue $PostgresHost
    $databaseValue = Quote-ConnectionStringValue $Database
    $userValue = Quote-ConnectionStringValue $AdminUser
    $passwordValue = Quote-ConnectionStringValue $plainPassword
    $env:GIFTCARD_TEST_CONNECTION =
        "Host=$hostValue;Port=$PostgresPort;Database=$databaseValue;" +
        "Username=$userValue;Password=$passwordValue;Pooling=false"

    $backendAssets = Join-Path $backendRoot 'src\GiftCardPlatform.Api\obj\project.assets.json'
    if (!(Test-Path -LiteralPath $backendAssets)) {
        Write-Host 'Restoring backend dependencies...'
        Invoke-Dotnet -Arguments @('restore', 'GiftCardPlatform.slnx') -WorkingDirectory $backendRoot
    }

    if (!$IntegrationOnly) {
        Write-Host 'Building and running backend unit/architecture gates...'
        Invoke-Dotnet -Arguments @(
            'build', 'GiftCardPlatform.slnx', '-c', 'Release', '--no-restore'
        ) -WorkingDirectory $backendRoot
        Invoke-Dotnet -Arguments @(
            'test', 'tests/GiftCardPlatform.UnitTests/GiftCardPlatform.UnitTests.csproj',
            '-c', 'Release', '--no-build', '--no-restore'
        ) -WorkingDirectory $backendRoot
        Invoke-Dotnet -Arguments @(
            'test', 'tests/GiftCardPlatform.ArchitectureTests/GiftCardPlatform.ArchitectureTests.csproj',
            '-c', 'Release', '--no-build', '--no-restore'
        ) -WorkingDirectory $backendRoot

        Write-Host 'Checking that the Distribution model matches its migration snapshot...'
        Invoke-Dotnet -Arguments @(
            'ef', 'migrations', 'has-pending-model-changes',
            '--project', 'src/GiftCardPlatform.Modules.Distribution',
            '--startup-project', 'src/GiftCardPlatform.Api',
            '--context', 'DistributionDbContext', '--no-build'
        ) -WorkingDirectory $backendRoot
    }

    Write-Host "Running real-PostgreSQL integration tests against '$Database'..."
    $integrationArguments = @(
        'test', 'tests/GiftCardPlatform.IntegrationTests/GiftCardPlatform.IntegrationTests.csproj',
        '-c', 'Release', '--no-build', '--no-restore'
    )
    if (!$AllIntegrationTests) {
        $integrationArguments += @('--filter', 'FullyQualifiedName~PartnerMintingTests')
    }
    $resultsDirectory = Join-Path $backendRoot (
        'tests\GiftCardPlatform.IntegrationTests\TestResults'
    )
    $trxName = 'LocalCertification-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.trx'
    $trxPath = Join-Path $resultsDirectory $trxName
    $integrationArguments += @(
        '--logger', "trx;LogFileName=$trxName",
        '--results-directory', $resultsDirectory
    )
    try {
        Invoke-Dotnet -Arguments $integrationArguments -WorkingDirectory $backendRoot
    }
    catch {
        Write-TrxFailures $trxPath
        throw
    }
    Write-Host "Integration diagnostics: $trxPath"

    if (!$SkipCardholder -and !$IntegrationOnly) {
        if (!(Test-Path -LiteralPath (Join-Path $cardholderRoot 'GiftCardCardholder.slnx'))) {
            throw "Cardholder repository not found at '$cardholderRoot'. Use -SkipCardholder only intentionally."
        }
        $cardholderAssets = Join-Path $cardholderRoot (
            'src\GiftCardCardholder.Web\obj\project.assets.json'
        )
        if (!(Test-Path -LiteralPath $cardholderAssets)) {
            Write-Host 'Restoring cardholder dependencies...'
            Invoke-Dotnet -Arguments @(
                'restore', 'GiftCardCardholder.slnx'
            ) -WorkingDirectory $cardholderRoot
        }
        Write-Host 'Running the cardholder e-pin and regression suite...'
        Invoke-Dotnet -Arguments @(
            'test', 'GiftCardCardholder.slnx', '-c', 'Release', '--no-restore'
        ) -WorkingDirectory $cardholderRoot
    }

    Write-Host ''
    Write-Host 'Local reseller e-pin certification passed. No deployment was used.' -ForegroundColor Green
    if (!$AllIntegrationTests) {
        Write-Host 'Use -AllIntegrationTests to run the complete backend PostgreSQL suite.'
    }
}
finally {
    if ($null -eq $previousTestConnection) {
        Remove-Item Env:GIFTCARD_TEST_CONNECTION -ErrorAction SilentlyContinue
    }
    else {
        $env:GIFTCARD_TEST_CONNECTION = $previousTestConnection
    }
    $plainPassword = $null
    $credential = $null
}
