[CmdletBinding()]
param(
    [string]$BackendBaseUrl = 'http://127.0.0.1:5143',
    [string]$PlatformEmail = 'platform.admin@example.test',
    [string]$RecipientEmail = 'recipient@example.test',
    [string]$DemoPassword = 'Demo passphrase 2026!',
    [string]$RuntimeConnectionString = $env:ConnectionStrings__Default,
    [decimal]$Amount = 1.00
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'OpenGiftCardLocal.Common.ps1')

if ($Amount -le 0) {
    throw 'Amount must be greater than zero.'
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

$healthChecks = [ordered]@{
    Backend = "$($BackendBaseUrl.TrimEnd('/'))/health/ready"
    Portal = 'http://127.0.0.1:5173/'
    'Portal BFF' = 'http://127.0.0.1:5179/health/ready'
    Cardholder = 'http://127.0.0.1:5180/health/ready'
    POS = 'http://127.0.0.1:5190/health/ready'
}
foreach ($check in $healthChecks.GetEnumerator()) {
    if (!(Test-OpenGiftCardUrl -Uri $check.Value)) {
        throw "$($check.Key) is not ready at $($check.Value)."
    }
    Write-Host "$($check.Key) readiness passed."
}

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
Write-Host "Runtime role and $forcedRlsCount forced-RLS tables passed."

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
Write-Host 'Seeded platform organization passed.'

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
Write-Host 'Seeded cardholder ownership passed.'

$smokePosPath = Join-Path (Get-OpenGiftCardStackDirectory) 'smoke-pos.json'
$posLogin = $null
$posClientCode = $null
$posTerminalCode = $null
$posClientSecret = $null
if (Test-Path -LiteralPath $smokePosPath) {
    try {
        $savedPos = Get-Content -LiteralPath $smokePosPath -Raw | ConvertFrom-Json
        $posClientCode = $savedPos.ClientCode
        $posTerminalCode = $savedPos.TerminalCode
        $posClientSecret = Unprotect-OpenGiftCardLocalValue -Value $savedPos.ProtectedSecret
        $posLogin = Invoke-SmokeJson `
            -Method POST `
            -Path '/api/v1/pos/auth/token' `
            -Body @{
                clientCode = $posClientCode
                clientSecret = $posClientSecret
                terminalCode = $posTerminalCode
            }
        Write-Host 'Reused the encrypted local smoke till registration.'
    }
    catch {
        $posLogin = $null
        Write-Host 'The saved smoke till is no longer valid; registering a replacement.'
    }
}

if ($null -eq $posLogin) {
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
    $posClientCode = $posClient.code
    $posTerminalCode = $terminal.code
    $posClientSecret = $posClient.secret
    $posLogin = Invoke-SmokeJson `
        -Method POST `
        -Path '/api/v1/pos/auth/token' `
        -Body @{
            clientCode = $posClientCode
            clientSecret = $posClientSecret
            terminalCode = $posTerminalCode
        }
    Write-OpenGiftCardJson -Path $smokePosPath -Value ([ordered]@{
        ClientCode = $posClientCode
        TerminalCode = $posTerminalCode
        ProtectedSecret = Protect-OpenGiftCardLocalValue -Value $posClientSecret
    })
}
$posHeaders = New-BearerHeaders -Token $posLogin.accessToken
Write-Host 'POS registration and device authentication passed.'

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

$confirmed = Invoke-SmokeJson `
    -Method POST `
    -Path "/api/v1/pos/payment-provisions/$($provision.id)/confirm" `
    -Headers $posHeaders `
    -Body @{ amount = $Amount }
if ($confirmed.state -ne 'Confirmed' -or $confirmed.confirmedAmount -ne $Amount) {
    throw 'The payment confirmation did not settle the expected amount.'
}

$receipt = Invoke-SmokeJson `
    -Method GET `
    -Path "/api/v1/platform/reports/payments/$($provision.id)" `
    -Headers $platformHeaders
if ($receipt.payment.paymentProvisionId -ne $provision.id) {
    throw 'The platform payment report did not return the confirmed provision.'
}

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

$status = Invoke-SmokeJson `
    -Method GET `
    -Path "/api/v1/me/gift-cards/$($card.id)/payment-tokens/$($credential.id)" `
    -Headers $recipientHeaders
if ($status.paymentProvisionId -ne $provision.id -or $status.state -ne 'Confirmed') {
    throw 'The cardholder payment status does not identify the confirmed provision.'
}

Write-Host ''
Write-Host 'Open Giftcard smoke test passed.'
Write-Host "  Organization: $($demoOrganization.code)"
Write-Host "  Card:         $($card.publicReference)"
Write-Host "  Sale:         $saleReference"
Write-Host "  Amount:       $Amount $($card.currency), fully refunded"
Write-Host 'No access token, payment credential, POS secret, or database password was printed.'
