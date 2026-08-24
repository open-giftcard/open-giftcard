[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$testParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testParent (
    'open-giftcard-observability-' + [Guid]::NewGuid().ToString('N'))
$encoding = [Text.UTF8Encoding]::new($false)
$serverJob = $null

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $artifacts = @('backend', 'portal', 'cardholder', 'pos') | ForEach-Object {
        [ordered]@{
            component = $_
            file = "open-giftcard-$_-v0.5.0-rc.1.zip"
            sha256 = 'A' * 64
        }
    }
    $manifestPath = Join-Path $testRoot 'ARTIFACTS.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ([ordered]@{
            release = 'v0.5.0-rc.1'
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            rehearsal = $false
            artifacts = $artifacts
        } | ConvertTo-Json -Depth 6),
        $encoding)

    $portProbe = [Net.Sockets.TcpListener]::new(
        [Net.IPAddress]::Loopback,
        0)
    $portProbe.Start()
    $port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()

    $alertNames = @(
        'OpenGiftcardBackendTelemetryMissing',
        'OpenGiftcardBackendNotReady',
        'OpenGiftcardBackendHighErrorRate',
        'OpenGiftcardBackendHighLatency',
        'OpenGiftcardWorkerRepeatedFailures',
        'OpenGiftcardAuditVerificationFailure'
    )
    $serverJob = Start-Job -ArgumentList $port, $alertNames -ScriptBlock {
        param($Port, $AlertNames)

        $listener = [Net.Sockets.TcpListener]::new(
            [Net.IPAddress]::Loopback,
            $Port)
        $listener.Start()
        Write-Output 'READY'
        try {
            for ($requestIndex = 0; $requestIndex -lt 5; $requestIndex++) {
                $client = $listener.AcceptTcpClient()
                try {
                    $stream = $client.GetStream()
                    $reader = [IO.StreamReader]::new(
                        $stream,
                        [Text.Encoding]::ASCII,
                        $false,
                        1024,
                        $true)
                    $requestLine = $reader.ReadLine()
                    while (-not [string]::IsNullOrEmpty($reader.ReadLine())) { }
                    $target = ($requestLine -split ' ')[1]

                    if ($target.StartsWith('/api/v1/rules')) {
                        $rules = @($AlertNames | ForEach-Object {
                            [ordered]@{
                                name = $_
                                health = 'ok'
                                state = 'inactive'
                            }
                        })
                        $response = [ordered]@{
                            status = 'success'
                            data = [ordered]@{
                                groups = @([ordered]@{ rules = $rules })
                            }
                        }
                    }
                    else {
                        $query = [Uri]::UnescapeDataString(
                            ($target -split 'query=', 2)[1])
                        if (-not $query.Contains(
                                'deployment_environment_name="staging-test"')) {
                            throw "Query was not scoped to staging-test: $query"
                        }
                        $value = if ($query.StartsWith('min(')) { '1' } else { '2' }
                        $response = [ordered]@{
                            status = 'success'
                            data = [ordered]@{
                                resultType = 'vector'
                                result = @([ordered]@{
                                    metric = [ordered]@{}
                                    value = @(1, $value)
                                })
                            }
                        }
                    }

                    $body = $response | ConvertTo-Json -Compress -Depth 8
                    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($body)
                    $header = "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
                    $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
                    $stream.Write($headerBytes, 0, $headerBytes.Length)
                    $stream.Write($bodyBytes, 0, $bodyBytes.Length)
                }
                finally {
                    $client.Dispose()
                }
            }
        }
        finally {
            $listener.Stop()
        }
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 100 -and -not $ready; $attempt++) {
        $ready = @(Receive-Job -Job $serverJob -Keep) -contains 'READY'
        if (-not $ready) {
            Start-Sleep -Milliseconds 100
        }
    }
    if (-not $ready) {
        throw 'Local observability test server did not start.'
    }

    $evidencePath = Join-Path $testRoot 'observability.json'
    & (Join-Path $PSScriptRoot 'Test-OpenGiftCardObservability.ps1') `
        -MetricsBaseUrl "http://127.0.0.1:$port" `
        -EnvironmentName 'staging-test' `
        -ArtifactManifestPath $manifestPath `
        -EvidencePath $evidencePath `
        -ExpectedBackendInstances 2 `
        -BearerToken 'test-token-must-not-appear' `
        -AllowInsecureHttp

    Wait-Job -Job $serverJob -Timeout 10 | Out-Null
    if ($serverJob.State -ne 'Completed') {
        throw 'Local observability test server did not complete.'
    }
    $jobOutput = @(Receive-Job -Job $serverJob)
    if ($serverJob.ChildJobs[0].JobStateInfo.State -ne 'Completed') {
        throw "Local observability test server failed: $($jobOutput -join ' ')"
    }

    $evidenceText = Get-Content -LiteralPath $evidencePath -Raw
    $evidence = $evidenceText | ConvertFrom-Json
    if ([string]$evidence.result -ne 'passed' -or
        [int]$evidence.checks.requiredAlertsLoaded -ne 6 -or
        [int]$evidence.checks.backendMetricStreams -ne 2 -or
        $evidenceText.Contains('test-token-must-not-appear') -or
        -not (Test-Path -LiteralPath "$evidencePath.sha256" -PathType Leaf)) {
        throw 'Observability gate did not write valid redacted passing evidence.'
    }

    Write-Host 'Observability evidence gate tests passed.'
}
finally {
    if ($null -ne $serverJob) {
        Stop-Job -Job $serverJob -ErrorAction SilentlyContinue
        Remove-Job -Job $serverJob -Force -ErrorAction SilentlyContinue
    }
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith(
            (Join-Path $testParent 'open-giftcard-observability-'),
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
