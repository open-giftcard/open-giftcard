[CmdletBinding()]
param(
    [string]$BackendBaseUrl = 'http://127.0.0.1:5143',
    [string]$PortalUrl = 'http://127.0.0.1:5173',
    [string]$PortalBffBaseUrl = 'http://127.0.0.1:5179',
    [string]$CardholderBaseUrl = 'http://127.0.0.1:5180',
    [string]$PosBaseUrl = 'http://127.0.0.1:5190',
    [string]$EnvironmentName = 'local',
    [string]$PlatformEmail = $env:OPEN_GIFTCARD_SMOKE_PLATFORM_EMAIL,
    [string]$RecipientEmail = $env:OPEN_GIFTCARD_SMOKE_RECIPIENT_EMAIL,
    [string]$DemoPassword = $env:OPEN_GIFTCARD_SMOKE_PASSWORD,
    [string]$RuntimeConnectionString = $env:ConnectionStrings__Default,
    [string]$PosClientCode = $env:OPEN_GIFTCARD_SMOKE_POS_CLIENT_CODE,
    [string]$PosTerminalCode = $env:OPEN_GIFTCARD_SMOKE_POS_TERMINAL_CODE,
    [string]$PosClientSecret = $env:OPEN_GIFTCARD_SMOKE_POS_CLIENT_SECRET,
    [string]$ArtifactManifestPath = '',
    [string]$EvidencePath = '',
    [switch]$AllowInsecureHttp,
    [decimal]$Amount = 1.00
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenGiftCardLocal.Common.ps1')

if ($Amount -le 0) {
    throw 'Amount must be greater than zero.'
}

function Resolve-SmokeBaseUrl {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Value)

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or
        -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "$Name must be an absolute HTTP(S) URL without credentials, query, or fragment."
    }
    return $Value.TrimEnd('/')
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

function Get-StreamPortableTextSha256([IO.Stream]$Stream) {
    $reader = [IO.StreamReader]::new($Stream)
    try {
        $text = $reader.ReadToEnd().Replace("`r`n", "`n").Replace("`r", "`n")
    }
    finally {
        $reader.Dispose()
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Read-ArchiveJson {
    param(
        [Parameter(Mandatory)][IO.Compression.ZipArchive]$Archive,
        [Parameter(Mandatory)][string]$EntryName
    )

    $entry = $Archive.GetEntry($EntryName)
    if ($null -eq $entry) {
        throw "Release archive is missing '$EntryName'."
    }
    $reader = [IO.StreamReader]::new($entry.Open())
    try {
        return $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $reader.Dispose()
    }
}

function Get-ArtifactSetEvidence {
    param([Parameter(Mandatory)][string]$ManifestPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $resolvedManifest = (Resolve-Path -LiteralPath $ManifestPath -ErrorAction Stop).Path
    $artifactRoot = Split-Path $resolvedManifest -Parent
    $manifest = Get-Content -LiteralPath $resolvedManifest -Raw | ConvertFrom-Json
    if ([bool]$manifest.rehearsal) {
        throw 'A rehearsal artifact set cannot certify a deployment.'
    }
    if ([string]$manifest.release -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+-rc\.[0-9]+$') {
        throw "Artifact manifest release '$($manifest.release)' is invalid."
    }

    $expectedComponents = @('backend', 'portal', 'cardholder', 'pos')
    if (@($manifest.artifacts).Count -ne $expectedComponents.Count) {
        throw 'Artifact manifest must contain exactly four applications.'
    }

    $contractHashes = [System.Collections.Generic.List[string]]::new()
    $backendOpenApiSha256 = $null
    $components = foreach ($component in $expectedComponents) {
        $artifact = @($manifest.artifacts | Where-Object {
            [string]$_.component -ceq $component
        })
        if ($artifact.Count -ne 1) {
            throw "Artifact manifest must contain exactly one '$component' entry."
        }
        $artifact = $artifact[0]
        $expectedArchiveName = "open-giftcard-$component-$($manifest.release).zip"
        $expectedSbomName = "open-giftcard-$component-$($manifest.release).spdx.json"
        if ([string]$artifact.file -cne $expectedArchiveName -or
            [string]$artifact.sbom.file -cne $expectedSbomName -or
            [string]$artifact.sbom.format -cne 'SPDX-2.2') {
            throw "$component artifact names or SBOM format do not match the release."
        }

        $archivePath = Join-Path $artifactRoot $expectedArchiveName
        $sbomPath = Join-Path $artifactRoot $expectedSbomName
        foreach ($file in @($archivePath, $sbomPath)) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                throw "Artifact set is missing '$(Split-Path $file -Leaf)'."
            }
        }
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        $sbomHash = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
        if ($archiveHash -cne [string]$artifact.sha256 -or
            $sbomHash -cne [string]$artifact.sbom.sha256) {
            throw "$component artifact or SBOM checksum does not match ARTIFACTS.json."
        }

        $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
        if ([string]$sbom.spdxVersion -cne 'SPDX-2.2' -or
            @($sbom.files).Count -eq 0 -or @($sbom.packages).Count -eq 0) {
            throw "$component SBOM is not a populated SPDX 2.2 document."
        }

        $root = "open-giftcard-$component-$($manifest.release)"
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $build = Read-ArchiveJson $archive "$root/BUILD_INFO.json"
            $expectedRepository = if ($component -ceq 'backend') {
                'open-giftcard'
            }
            else {
                "open-giftcard-$component"
            }
            if ([string]$build.release -cne [string]$manifest.release -or
                [string]$build.component -cne $component -or
                [string]$build.repository -cne $expectedRepository -or
                [string]$build.commit -notmatch '^[0-9a-f]{40}$' -or
                [bool]$build.dirty) {
                throw "$component BUILD_INFO.json is not a clean matching build."
            }
            $contractEntry = $archive.GetEntry("$root/RELEASE_COMPATIBILITY.json")
            if ($null -eq $contractEntry) {
                throw "$component archive has no release contract."
            }
            $contractStream = $contractEntry.Open()
            try {
                $contractHash = Get-StreamPortableTextSha256 $contractStream
            }
            finally {
                $contractStream.Dispose()
            }
            $contractHashes.Add($contractHash)
            $contract = Read-ArchiveJson $archive "$root/RELEASE_COMPATIBILITY.json"
            if ([string]$contract.release -cne [string]$manifest.release -or
                [string]$contract.channel -cne 'release-candidate' -or
                @($contract.components).Count -ne 4 -or
                [string]$contract.backendContract.sha256 -notmatch '^[0-9A-F]{64}$') {
                throw "$component release contract is structurally invalid."
            }
            $member = @($contract.components | Where-Object {
                [string]$_.id -ceq $component
            })
            if ($member.Count -ne 1 -or
                [string]$member[0].tag -cne [string]$manifest.release -or
                [string]$member[0].artifact -cne "open-giftcard-$component") {
                throw "$component release contract membership is invalid."
            }
            if ($null -eq $backendOpenApiSha256) {
                $backendOpenApiSha256 = [string]$contract.backendContract.sha256
            }
            elseif ($backendOpenApiSha256 -cne
                [string]$contract.backendContract.sha256) {
                throw 'The artifacts do not accept one backend OpenAPI hash.'
            }

            $embeddedSbom = $archive.GetEntry(
                "$root/_manifest/spdx_2.2/manifest.spdx.json")
            if ($null -eq $embeddedSbom) {
                throw "$component archive has no embedded SPDX 2.2 SBOM."
            }
            $embeddedSbomStream = $embeddedSbom.Open()
            try {
                if ((Get-StreamSha256 $embeddedSbomStream) -cne $sbomHash) {
                    throw "$component embedded SBOM does not match its sidecar."
                }
            }
            finally {
                $embeddedSbomStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }

        [ordered]@{
            component = $component
            repository = [string]$build.repository
            commit = [string]$build.commit
            artifact = $expectedArchiveName
            sha256 = $archiveHash
            sbom = $expectedSbomName
            sbomSha256 = $sbomHash
        }
    }

    if (@($contractHashes | Sort-Object -Unique).Count -ne 1) {
        throw 'The four artifacts do not carry one identical release contract.'
    }

    return [ordered]@{
        release = [string]$manifest.release
        artifactManifestSha256 =
            (Get-FileHash -LiteralPath $resolvedManifest -Algorithm SHA256).Hash
        releaseContractSha256 = $contractHashes[0]
        backendOpenApiSha256 = $backendOpenApiSha256
        components = @($components)
    }
}

function Write-SmokeEvidence {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $checksumPath = "$resolved.sha256"
    if ((Test-Path -LiteralPath $resolved) -or
        (Test-Path -LiteralPath $checksumPath)) {
        throw "Refusing to overwrite smoke evidence '$resolved'."
    }
    Write-OpenGiftCardJson -Path $resolved -Value $Value
    $digest = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    "$digest  $(Split-Path $resolved -Leaf)" |
        Set-Content -LiteralPath $checksumPath -Encoding ascii
    Write-Host "Redacted smoke evidence written to $resolved ($digest)"
}

function Assert-SmokeStatus {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][int]$ExpectedStatus
    )

    $status = 0
    try {
        $status = (Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 10).StatusCode
    }
    catch {
        if ($null -ne $_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
        }
        else {
            throw "$Name did not return an HTTP response."
        }
    }
    if ($status -ne $ExpectedStatus) {
        throw "$Name returned HTTP $status, expected $ExpectedStatus."
    }
}

function Test-SmokeSecurityHeaders {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Uri,
        [switch]$RequireHsts,
        [switch]$RequireNoStore
    )

    $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 10
    $csp = [string]$response.Headers['Content-Security-Policy']
    $contentTypeOptions = [string]$response.Headers['X-Content-Type-Options']
    $referrerPolicy = [string]$response.Headers['Referrer-Policy']
    if ($csp -notmatch "default-src\s+'self'" -or
        $csp -notmatch "frame-ancestors\s+'none'" -or
        $contentTypeOptions -cne 'nosniff' -or
        $referrerPolicy -cne 'no-referrer') {
        throw "$Name is missing its required browser security headers."
    }
    if ($RequireNoStore -and
        [string]$response.Headers['Cache-Control'] -notmatch '(?i)no-store') {
        throw "$Name HTML responses must be no-store."
    }
    if ($RequireHsts -and
        [string]$response.Headers['Strict-Transport-Security'] -notmatch
            '(?i)max-age=') {
        throw "$Name is missing HSTS on its HTTPS response."
    }
}

function Invoke-SmokeJson {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Headers = @{},
        $Body,
        [int[]]$ExpectedStatus = @(200)
    )

    $parameters = @{
        Method = $Method
        Uri = $BackendBaseUrl.TrimEnd('/') + $Path
        Headers = $Headers
        UseBasicParsing = $true
        TimeoutSec = 20
    }
    if ($null -ne $Body) {
        $parameters['ContentType'] = 'application/json'
        $parameters['Body'] = $Body | ConvertTo-Json -Depth 8 -Compress
    }

    $response = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $response = Invoke-WebRequest @parameters
            break
        }
        catch {
            $status = if ($_.Exception.Response) {
                [int]$_.Exception.Response.StatusCode
            }
            else {
                0
            }
            if ($status -eq 429 -and $attempt -lt 3) {
                $headerText = $_.Exception.Response.Headers.ToString()
                $delay = if ($headerText -match '(?im)^Retry-After:\s*(\d+)\s*$') {
                    [int]$matches[1]
                } else { 15 }
                $delay = [Math]::Max(1, [Math]::Min(60, $delay))
                Write-Host "$Method $Path is rate limited; retrying in $delay seconds."
                Start-Sleep -Seconds $delay
                continue
            }
            throw "$Method $Path failed with HTTP $status."
        }
    }
    if ($null -eq $response) {
        throw "$Method $Path did not return a response."
    }

    if ($response.StatusCode -notin $ExpectedStatus) {
        throw "$Method $Path returned HTTP $($response.StatusCode), expected $($ExpectedStatus -join ' or ')."
    }
    if ([string]::IsNullOrWhiteSpace($response.Content)) {
        return $null
    }
    return $response.Content | ConvertFrom-Json
}

function New-BearerHeaders {
    param([Parameter(Mandatory)][string]$Token)
    return @{ Authorization = "Bearer $Token" }
}

$startedAt = [DateTimeOffset]::UtcNow
$currentStage = 'configuration'
$isLocalEnvironment = $EnvironmentName -ceq 'local'
$artifactSet = $null
$checks = [ordered]@{
    readiness = [ordered]@{}
    browserSecurity = [ordered]@{}
    database = [ordered]@{}
    transaction = [ordered]@{}
}

try {
    if ([string]::IsNullOrWhiteSpace($EnvironmentName)) {
        throw 'EnvironmentName is required.'
    }
    if (-not $isLocalEnvironment -and
        [string]::IsNullOrWhiteSpace($EvidencePath)) {
        throw 'EvidencePath is required for a named non-local smoke run.'
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $resolvedEvidenceTarget = [IO.Path]::GetFullPath($EvidencePath)
        if ((Test-Path -LiteralPath $resolvedEvidenceTarget) -or
            (Test-Path -LiteralPath "$resolvedEvidenceTarget.sha256")) {
            throw "Refusing to overwrite smoke evidence '$resolvedEvidenceTarget'."
        }
    }
    $BackendBaseUrl = Resolve-SmokeBaseUrl 'BackendBaseUrl' $BackendBaseUrl
    $PortalUrl = Resolve-SmokeBaseUrl 'PortalUrl' $PortalUrl
    $PortalBffBaseUrl = Resolve-SmokeBaseUrl 'PortalBffBaseUrl' $PortalBffBaseUrl
    $CardholderBaseUrl = Resolve-SmokeBaseUrl 'CardholderBaseUrl' $CardholderBaseUrl
    $PosBaseUrl = Resolve-SmokeBaseUrl 'PosBaseUrl' $PosBaseUrl

    $endpointValues = @(
        $BackendBaseUrl,
        $PortalUrl,
        $PortalBffBaseUrl,
        $CardholderBaseUrl,
        $PosBaseUrl)
    $allHttps = @($endpointValues | Where-Object {
        ([Uri]$_).Scheme -cne 'https'
    }).Count -eq 0
    if (-not $isLocalEnvironment -and -not $allHttps -and -not $AllowInsecureHttp) {
        throw 'A named non-local smoke run requires HTTPS for every endpoint. Use AllowInsecureHttp only for a non-certifying rehearsal.'
    }

    if ([string]::IsNullOrWhiteSpace($PlatformEmail)) {
        if (-not $isLocalEnvironment) {
            throw 'OPEN_GIFTCARD_SMOKE_PLATFORM_EMAIL is required outside local runs.'
        }
        $PlatformEmail = 'platform.admin@example.test'
    }
    if ([string]::IsNullOrWhiteSpace($RecipientEmail)) {
        if (-not $isLocalEnvironment) {
            throw 'OPEN_GIFTCARD_SMOKE_RECIPIENT_EMAIL is required outside local runs.'
        }
        $RecipientEmail = 'recipient@example.test'
    }
    if ([string]::IsNullOrWhiteSpace($DemoPassword)) {
        if (-not $isLocalEnvironment) {
            throw 'OPEN_GIFTCARD_SMOKE_PASSWORD is required outside local runs.'
        }
        $DemoPassword = 'Demo passphrase 2026!'
    }
    if (-not $isLocalEnvironment -and $DemoPassword -ceq 'Demo passphrase 2026!') {
        throw 'The public local demo password cannot certify a non-local environment.'
    }

    if ([string]::IsNullOrWhiteSpace($ArtifactManifestPath)) {
        if (-not $isLocalEnvironment) {
            throw 'ArtifactManifestPath is required to bind a non-local run to exact clean artifacts.'
        }
    }
    else {
        $artifactSet = Get-ArtifactSetEvidence -ManifestPath $ArtifactManifestPath
    }

    $providedPosValues = @(@(
        $PosClientCode,
        $PosTerminalCode,
        $PosClientSecret) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($providedPosValues.Count -notin @(0, 3)) {
        throw 'POS client code, terminal code, and client secret must be provided together.'
    }
    if (-not $isLocalEnvironment -and $providedPosValues.Count -ne 3) {
        throw 'A named non-local smoke run requires a pre-provisioned POS client, terminal, and secret.'
    }

    $currentStage = 'readiness'
$healthChecks = [ordered]@{
    Backend = "$BackendBaseUrl/health/ready"
    Portal = "$PortalUrl/"
    'Portal BFF' = "$PortalBffBaseUrl/health/ready"
    Cardholder = "$CardholderBaseUrl/health/ready"
    POS = "$PosBaseUrl/health/ready"
}
foreach ($check in $healthChecks.GetEnumerator()) {
    if (!(Test-OpenGiftCardUrl -Uri $check.Value)) {
        throw "$($check.Key) is not ready at $($check.Value)."
    }
    $checks.readiness[$check.Key] = $true
    Write-Host "$($check.Key) readiness passed."
}

$currentStage = 'browser-security'
$browserSurfaces = @(
    [pscustomobject]@{
        Name = 'Portal BFF and SPA'
        Uri = "$PortalBffBaseUrl/"
        RequireNoStore = $false
    },
    [pscustomobject]@{
        Name = 'Cardholder'
        Uri = "$CardholderBaseUrl/"
        RequireNoStore = $true
    },
    [pscustomobject]@{
        Name = 'POS'
        Uri = "$PosBaseUrl/"
        RequireNoStore = $true
    })
foreach ($surface in $browserSurfaces) {
    Test-SmokeSecurityHeaders `
        -Name $surface.Name `
        -Uri $surface.Uri `
        -RequireHsts:(-not $isLocalEnvironment -and $allHttps) `
        -RequireNoStore:$surface.RequireNoStore
    $checks.browserSecurity[$surface.Name] = $true
    Write-Host "$($surface.Name) browser security headers passed."
}
if (-not $isLocalEnvironment) {
    Assert-SmokeStatus 'Development demo endpoint' "$BackendBaseUrl/demo" 404
    Assert-SmokeStatus 'Development Swagger endpoint' "$BackendBaseUrl/swagger" 404
    $checks.browserSecurity.developmentEndpointsHidden = $true
    Write-Host 'Development-only backend endpoints are hidden.'
}

$currentStage = 'database-safety'
$backendEnv = Read-OpenGiftCardDotEnv -Path (Join-Path $script:OpenGiftCardBackendRoot '.env')
if ([string]::IsNullOrWhiteSpace($RuntimeConnectionString)) {
    $RuntimeConnectionString = Get-OpenGiftCardSetting `
        -Values $backendEnv `
        -Name 'ConnectionStrings__Default' `
        -Required
}
$roleCheck = Invoke-OpenGiftCardPsqlScalar `
    -ConnectionString $RuntimeConnectionString `
    -Sql "select rolsuper::text || '|' || rolbypassrls::text from pg_roles where rolname = current_user;"
if ($roleCheck -ne 'false|false') {
    throw 'The backend runtime connection is not using a NOSUPERUSER, NOBYPASSRLS role.'
}
$forcedRlsCount = [int](Invoke-OpenGiftCardPsqlScalar `
    -ConnectionString $RuntimeConnectionString `
    -Sql "select count(*) from pg_class where relkind = 'r' and relrowsecurity and relforcerowsecurity;")
if ($forcedRlsCount -lt 1) {
    throw 'No table with forced row-level security was found.'
}
$checks.database = [ordered]@{
    runtimeRoleSuperuser = $false
    runtimeRoleBypassesRls = $false
    forcedRlsTableCount = $forcedRlsCount
}
Write-Host "Runtime role and $forcedRlsCount forced-RLS tables passed."

$currentStage = 'platform-authentication'
$platformLogin = Invoke-SmokeJson `
    -Method POST `
    -Path '/api/v1/auth/login' `
    -Body @{ email = $PlatformEmail; password = $DemoPassword }
$platformHeaders = New-BearerHeaders -Token $platformLogin.accessToken
$organizations = Invoke-SmokeJson `
    -Method GET `
    -Path '/api/v1/organizations?search=DEMO-NORTHWIND&limit=20' `
    -Headers $platformHeaders
$demoOrganization = @($organizations.items | Where-Object { $_.code -eq 'DEMO-NORTHWIND' }) |
    Select-Object -First 1
if ($null -eq $demoOrganization) {
    throw 'The seeded DEMO-NORTHWIND organization was not found.'
}
$checks.transaction.platformOrganizationLookup = $true
Write-Host 'Seeded platform organization passed.'

$currentStage = 'recipient-authentication'
$recipientLogin = Invoke-SmokeJson `
    -Method POST `
    -Path '/api/v1/auth/login' `
    -Body @{ email = $RecipientEmail; password = $DemoPassword }
$recipientHeaders = New-BearerHeaders -Token $recipientLogin.accessToken
$cards = Invoke-SmokeJson `
    -Method GET `
    -Path '/api/v1/me/gift-cards?limit=20' `
    -Headers $recipientHeaders
$card = @($cards.items | Where-Object { $_.availableBalance -ge $Amount }) |
    Sort-Object availableBalance -Descending |
    Select-Object -First 1
if ($null -eq $card) {
    throw "The seeded recipient has no card with $Amount available."
}
$checks.transaction.cardholderOwnership = $true
Write-Host 'Seeded cardholder ownership passed.'

$currentStage = 'pos-authentication'
$smokePosPath = Join-Path (Get-OpenGiftCardStackDirectory) 'smoke-pos.json'
$posLogin = $null
$hasProvidedPos = $providedPosValues.Count -eq 3
if ($hasProvidedPos) {
    $posLogin = Invoke-SmokeJson `
        -Method POST `
        -Path '/api/v1/pos/auth/token' `
        -Body @{
            clientCode = $PosClientCode
            clientSecret = $PosClientSecret
            terminalCode = $PosTerminalCode
        }
    Write-Host 'Authenticated the pre-provisioned smoke till.'
}
elseif ($isLocalEnvironment -and (Test-Path -LiteralPath $smokePosPath)) {
    try {
        $savedPos = Get-Content -LiteralPath $smokePosPath -Raw | ConvertFrom-Json
        $PosClientCode = $savedPos.ClientCode
        $PosTerminalCode = $savedPos.TerminalCode
        $PosClientSecret = Unprotect-OpenGiftCardLocalValue -Value $savedPos.ProtectedSecret
        $posLogin = Invoke-SmokeJson `
            -Method POST `
            -Path '/api/v1/pos/auth/token' `
            -Body @{
                clientCode = $PosClientCode
                clientSecret = $PosClientSecret
                terminalCode = $PosTerminalCode
            }
        Write-Host 'Reused the encrypted local smoke till registration.'
    }
    catch {
        $posLogin = $null
        Write-Host 'The saved smoke till is no longer valid; registering a replacement.'
    }
}

if ($null -eq $posLogin -and $isLocalEnvironment) {
    $registrationSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant()
    $posClient = Invoke-SmokeJson `
        -Method POST `
        -Path '/api/v1/pos/clients' `
        -Headers $platformHeaders `
        -Body @{ code = "SMOKE-$registrationSuffix"; displayName = 'Local smoke till' } `
        -ExpectedStatus @(201)
    $terminal = Invoke-SmokeJson `
        -Method POST `
        -Path "/api/v1/pos/clients/$($posClient.id)/terminals" `
        -Headers $platformHeaders `
        -Body @{
            code = "T-$($registrationSuffix.Substring(0, 6))"
            storeReference = "SMOKE-$registrationSuffix"
        } `
        -ExpectedStatus @(201)
    $PosClientCode = $posClient.code
    $PosTerminalCode = $terminal.code
    $PosClientSecret = $posClient.secret
    $posLogin = Invoke-SmokeJson `
        -Method POST `
        -Path '/api/v1/pos/auth/token' `
        -Body @{
            clientCode = $PosClientCode
            clientSecret = $PosClientSecret
            terminalCode = $PosTerminalCode
        }
    Write-OpenGiftCardJson -Path $smokePosPath -Value ([ordered]@{
        ClientCode = $PosClientCode
        TerminalCode = $PosTerminalCode
        ProtectedSecret = Protect-OpenGiftCardLocalValue -Value $PosClientSecret
    })
}
if ($null -eq $posLogin) {
    throw 'POS authentication did not produce an access token.'
}
$posHeaders = New-BearerHeaders -Token $posLogin.accessToken
$checks.transaction.posAuthentication = $true
Write-Host 'POS registration and device authentication passed.'

$currentStage = 'payment-transaction'
$credential = Invoke-SmokeJson `
    -Method POST `
    -Path "/api/v1/me/gift-cards/$($card.id)/payment-tokens" `
    -Headers $recipientHeaders `
    -ExpectedStatus @(201)
$balance = Invoke-SmokeJson `
    -Method POST `
    -Path '/api/v1/pos/balance-inquiries' `
    -Headers $posHeaders `
    -Body @{ paymentToken = $credential.rawToken; paymentCode = $null }
if ($balance.availableAmount -lt $Amount) {
    throw 'The balance inquiry returned less value than the smoke payment requires.'
}
$checks.transaction.balanceInquiry = $true

$saleSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 8).ToUpperInvariant()
$saleReference = "SMOKE-SALE-$saleSuffix"
$provision = Invoke-SmokeJson `
    -Method POST `
    -Path '/api/v1/pos/payment-provisions' `
    -Headers $posHeaders `
    -Body @{
        paymentToken = $credential.rawToken
        paymentCode = $null
        amount = $Amount
        posTransactionReference = $saleReference
        idempotencyKey = "smoke-provision-$saleSuffix"
    } `
    -ExpectedStatus @(201)
if ($provision.state -ne 'Active') {
    throw "The payment hold entered unexpected state '$($provision.state)'."
}
$checks.transaction.paymentProvision = $true

$confirmed = Invoke-SmokeJson `
    -Method POST `
    -Path "/api/v1/pos/payment-provisions/$($provision.id)/confirm" `
    -Headers $posHeaders `
    -Body @{ amount = $Amount }
if ($confirmed.state -ne 'Confirmed' -or $confirmed.confirmedAmount -ne $Amount) {
    throw 'The payment confirmation did not settle the expected amount.'
}
$checks.transaction.paymentConfirmation = $true

$receipt = Invoke-SmokeJson `
    -Method GET `
    -Path "/api/v1/platform/reports/payments/$($provision.id)" `
    -Headers $platformHeaders
if ($receipt.payment.paymentProvisionId -ne $provision.id) {
    throw 'The platform payment report did not return the confirmed provision.'
}
$checks.transaction.platformReceipt = $true

$refund = Invoke-SmokeJson `
    -Method POST `
    -Path "/api/v1/pos/payment-provisions/$($provision.id)/refunds" `
    -Headers $posHeaders `
    -Body @{
        amount = $Amount
        idempotencyKey = "smoke-refund-$saleSuffix"
        posTransactionReference = $saleReference
        reason = 'Local smoke test reversal'
    } `
    -ExpectedStatus @(201)
if ($refund.remainingRefundableAmount -ne 0) {
    throw 'The smoke refund did not reverse the full confirmed amount.'
}
$checks.transaction.fullRefund = $true

$status = Invoke-SmokeJson `
    -Method GET `
    -Path "/api/v1/me/gift-cards/$($card.id)/payment-tokens/$($credential.id)" `
    -Headers $recipientHeaders
if ($status.paymentProvisionId -ne $provision.id -or $status.state -ne 'Confirmed') {
    throw 'The cardholder payment status does not identify the confirmed provision.'
}
$checks.transaction.cardholderPaymentStatus = $true
$checks.transaction.amount = $Amount
$checks.transaction.currency = [string]$card.currency
$checks.transaction.saleReference = $saleReference
$checks.transaction.paymentProvisionId = [string]$provision.id

$completedAt = [DateTimeOffset]::UtcNow
$countsAsDeploymentVerifiedAutomatedSmoke =
    -not $isLocalEnvironment -and $allHttps -and $null -ne $artifactSet
$evidence = [ordered]@{
    schemaVersion = 1
    environment = [ordered]@{
        name = $EnvironmentName
        scope = if ($isLocalEnvironment) {
            'local-source-smoke'
        } else {
            'staging-automated-smoke'
        }
        endpoints = [ordered]@{
            backend = $BackendBaseUrl
            portal = $PortalUrl
            portalBff = $PortalBffBaseUrl
            cardholder = $CardholderBaseUrl
            pos = $PosBaseUrl
        }
        allEndpointsHttps = $allHttps
    }
    release = $artifactSet
    startedAtUtc = $startedAt.ToString('O')
    completedAtUtc = $completedAt.ToString('O')
    durationSeconds = [Math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
    result = 'passed'
    countsAsDeploymentVerifiedAutomatedSmoke =
        $countsAsDeploymentVerifiedAutomatedSmoke
    checks = $checks
    redaction = [ordered]@{
        secretValuesIncluded = $false
        excluded = @(
            'access tokens',
            'payment credentials',
            'POS client secrets',
            'database passwords',
            'user passwords')
    }
    limitations = @(
        'This evidence covers the automated smoke gate only.',
        'Manual UI acceptance and operator-controlled infrastructure evidence remain separate gates.')
}
Write-SmokeEvidence -Path $EvidencePath -Value $evidence

Write-Host ''
Write-Host 'Open Giftcard smoke test passed.'
Write-Host "  Organization: $($demoOrganization.code)"
Write-Host "  Card:         $($card.publicReference)"
Write-Host "  Sale:         $saleReference"
Write-Host "  Amount:       $Amount $($card.currency), fully refunded"
Write-Host 'No access token, payment credential, POS secret, or database password was printed.'
}
catch {
    $completedAt = [DateTimeOffset]::UtcNow
    $failedEvidence = [ordered]@{
        schemaVersion = 1
        environment = [ordered]@{
            name = $EnvironmentName
            scope = if ($isLocalEnvironment) {
                'local-source-smoke'
            } else {
                'staging-automated-smoke'
            }
        }
        release = $artifactSet
        startedAtUtc = $startedAt.ToString('O')
        completedAtUtc = $completedAt.ToString('O')
        durationSeconds = [Math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
        result = 'failed'
        failedStage = $currentStage
        countsAsDeploymentVerifiedAutomatedSmoke = $false
        checks = $checks
        redaction = [ordered]@{
            secretValuesIncluded = $false
            failureMessageIncluded = $false
        }
    }
    Write-SmokeEvidence -Path $EvidencePath -Value $failedEvidence
    throw
}
