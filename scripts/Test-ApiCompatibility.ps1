<#
.SYNOPSIS
    Fails when the served OpenAPI document breaks the /api/v1 promise.

.DESCRIPTION
    VERSIONING.md commits this project to a stable /api/v1 for the life of 1.x
    and lists exactly what that forbids. This script is the enforcement it
    names. Until it existed the promise was a convention.

    Refused, from the forbidden list:

      * removing or renaming an endpoint
      * removing or renaming a request field
      * removing or renaming a response field
      * making an optional request field required
      * narrowing an accepted value set on a request
      * removing a documented status code from an operation

    Allowed, and deliberately not reported as failures:

      * new endpoints
      * new optional request fields
      * new response fields
      * new enum values in a response
      * relaxing a validation rule

    Anything outside both lists is reported as a warning rather than a failure,
    because the promise is the list and not this script's opinion.

.PARAMETER BaselinePath
    The accepted contract. Defaults to contracts/backend.openapi.json.

.PARAMETER CurrentPath
    The document this build serves. Supply either this or -CurrentUrl.

.PARAMETER CurrentUrl
    Fetch the current document from a running API, for example
    http://127.0.0.1:5143/swagger/v1/swagger.json

.EXAMPLE
    ./scripts/Test-ApiCompatibility.ps1 -CurrentUrl http://127.0.0.1:5143/swagger/v1/swagger.json
#>
[CmdletBinding()]
param(
    [Parameter()]
    [string]$BaselinePath,

    [Parameter()]
    [string]$CurrentPath,

    [Parameter()]
    [string]$CurrentUrl
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $BaselinePath) {
    $BaselinePath = Join-Path $repoRoot 'contracts/backend.openapi.json'
}

if (-not (Test-Path -LiteralPath $BaselinePath -PathType Leaf)) {
    throw "Missing compatibility baseline: $BaselinePath"
}

if (-not $CurrentPath -and -not $CurrentUrl) {
    throw 'Supply -CurrentPath or -CurrentUrl.'
}

$breaking = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Read-OpenApiDocument {
    param([string]$Path, [string]$Url)

    if ($Url) {
        Write-Host "Fetching current contract from $Url"
        # -UseBasicParsing keeps this working on Windows PowerShell without IE.
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 60
        return $response.Content | ConvertFrom-Json
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing current contract: $Path"
    }
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Get-PropertyNames {
    param($Object)
    if ($null -eq $Object) { return @() }
    return @($Object.PSObject.Properties.Name)
}

function Get-PropertyValue {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# @($null) is a one-element array containing null, so wrapping an absent JSON
# member in @() yields a phantom entry that then compares as an empty-named
# parameter. Every list read from the document goes through here instead.
function Get-PropertyList {
    param($Object, [string]$Name)
    $value = Get-PropertyValue $Object $Name
    if ($null -eq $value) { return @() }
    return @($value | Where-Object { $null -ne $_ })
}

# Follows a local $ref into components/schemas. Remote refs are not used by
# this document and are left alone rather than guessed at.
function Resolve-Schema {
    param($Schema, $Document)

    $seen = 0
    while ($null -ne $Schema) {
        $ref = Get-PropertyValue $Schema '$ref'
        if (-not $ref) { break }
        if (++$seen -gt 32) { throw "Schema reference chain too deep at '$ref'." }
        if ($ref -notmatch '^#/components/schemas/(.+)$') { return $null }
        $Schema = Get-PropertyValue $Document.components.schemas $Matches[1]
    }
    return $Schema
}

# Compares one schema pair. Direction decides which rules apply, because a
# request and a response break in opposite directions: a request field that
# disappears strands a caller, a response field that disappears strands a
# reader, but only requests can be narrowed by making a field required.
function Compare-Schema {
    param(
        $Baseline,
        $Current,
        $BaselineDocument,
        $CurrentDocument,
        [ValidateSet('request', 'response')][string]$Direction,
        [string]$Location,
        [System.Collections.Generic.HashSet[string]]$Visited
    )

    $Baseline = Resolve-Schema $Baseline $BaselineDocument
    $Current = Resolve-Schema $Current $CurrentDocument

    if ($null -eq $Baseline) { return }
    if ($null -eq $Current) {
        $breaking.Add("$Location has no schema in the current document but had one in the baseline.")
        return
    }

    # Cycle guard. Schemas here are recursive through organization hierarchy.
    $key = "$Direction|$Location"
    if (-not $Visited.Add($key)) { return }
    if ($Visited.Count -gt 4000) { return }

    $baselineEnum = Get-PropertyValue $Baseline 'enum'
    if ($baselineEnum) {
        $currentEnum = Get-PropertyValue $Current 'enum'
        if ($currentEnum) {
            $currentValues = @($currentEnum | ForEach-Object { "$_" })
            foreach ($value in $baselineEnum) {
                if ($currentValues -notcontains "$value") {
                    if ($Direction -eq 'request') {
                        $breaking.Add("$Location no longer accepts value '$value'. Narrowing an accepted value set is a breaking change.")
                    }
                    else {
                        $warnings.Add("$Location no longer returns value '$value'.")
                    }
                }
            }
        }
    }

    $baselineItems = Get-PropertyValue $Baseline 'items'
    if ($baselineItems) {
        Compare-Schema -Baseline $baselineItems -Current (Get-PropertyValue $Current 'items') `
            -BaselineDocument $BaselineDocument -CurrentDocument $CurrentDocument `
            -Direction $Direction -Location "$Location[]" -Visited $Visited
    }

    $baselineProperties = Get-PropertyValue $Baseline 'properties'
    if (-not $baselineProperties) { return }
    $currentProperties = Get-PropertyValue $Current 'properties'

    foreach ($name in (Get-PropertyNames $baselineProperties)) {
        $currentProperty = Get-PropertyValue $currentProperties $name
        if ($null -eq $currentProperty) {
            $breaking.Add("$Location.$name was removed or renamed.")
            continue
        }
        Compare-Schema -Baseline (Get-PropertyValue $baselineProperties $name) -Current $currentProperty `
            -BaselineDocument $BaselineDocument -CurrentDocument $CurrentDocument `
            -Direction $Direction -Location "$Location.$name" -Visited $Visited
    }

    if ($Direction -eq 'request') {
        $baselineRequired = Get-PropertyList $Baseline 'required'
        $currentRequired = Get-PropertyList $Current 'required'
        foreach ($name in $currentRequired) {
            if ($baselineRequired -notcontains $name) {
                $breaking.Add("$Location.$name became required. An optional request field cannot be made required.")
            }
        }
    }
}

$baselineDocument = Read-OpenApiDocument -Path $BaselinePath
$currentDocument = Read-OpenApiDocument -Path $CurrentPath -Url $CurrentUrl

$visited = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

$baselinePaths = Get-PropertyNames $baselineDocument.paths
$operationCount = 0

foreach ($path in $baselinePaths) {
    $currentPathItem = Get-PropertyValue $currentDocument.paths $path
    if ($null -eq $currentPathItem) {
        $breaking.Add("Endpoint '$path' was removed or renamed.")
        continue
    }

    $baselinePathItem = Get-PropertyValue $baselineDocument.paths $path
    foreach ($method in (Get-PropertyNames $baselinePathItem)) {
        $baselineOperation = Get-PropertyValue $baselinePathItem $method
        $currentOperation = Get-PropertyValue $currentPathItem $method
        if ($null -eq $currentOperation) {
            $breaking.Add("Operation '$($method.ToUpperInvariant()) $path' was removed.")
            continue
        }
        $operationCount++
        $label = "$($method.ToUpperInvariant()) $path"

        # Parameters: path, query and header inputs.
        foreach ($baselineParameter in (Get-PropertyList $baselineOperation 'parameters')) {
            $match = (Get-PropertyList $currentOperation 'parameters') |
                Where-Object { $_.name -eq $baselineParameter.name -and $_.in -eq $baselineParameter.in } |
                Select-Object -First 1
            if ($null -eq $match) {
                $breaking.Add("$label parameter '$($baselineParameter.name)' ($($baselineParameter.in)) was removed or renamed.")
                continue
            }
            if ((-not $baselineParameter.required) -and $match.required) {
                $breaking.Add("$label parameter '$($baselineParameter.name)' became required.")
            }
            Compare-Schema -Baseline $baselineParameter.schema -Current $match.schema `
                -BaselineDocument $baselineDocument -CurrentDocument $currentDocument `
                -Direction 'request' -Location "$label parameter '$($baselineParameter.name)'" -Visited $visited
        }

        # Request body.
        $baselineBody = Get-PropertyValue $baselineOperation 'requestBody'
        if ($baselineBody) {
            $currentBody = Get-PropertyValue $currentOperation 'requestBody'
            if ($null -eq $currentBody) {
                $breaking.Add("$label no longer accepts a request body.")
            }
            else {
                foreach ($mediaType in (Get-PropertyNames $baselineBody.content)) {
                    $currentMedia = Get-PropertyValue $currentBody.content $mediaType
                    if ($null -eq $currentMedia) {
                        $breaking.Add("$label no longer accepts media type '$mediaType'.")
                        continue
                    }
                    Compare-Schema -Baseline (Get-PropertyValue $baselineBody.content $mediaType).schema -Current $currentMedia.schema `
                        -BaselineDocument $baselineDocument -CurrentDocument $currentDocument `
                        -Direction 'request' -Location "$label request" -Visited $visited
                }
            }
        }

        # Responses. A status code disappearing changes what a caller sees for
        # a condition that still exists, which the promise forbids.
        foreach ($status in (Get-PropertyNames (Get-PropertyValue $baselineOperation 'responses'))) {
            $currentResponse = Get-PropertyValue $currentOperation.responses $status
            if ($null -eq $currentResponse) {
                $breaking.Add("$label no longer documents status code $status.")
                continue
            }
            $baselineResponse = Get-PropertyValue $baselineOperation.responses $status
            foreach ($mediaType in (Get-PropertyNames (Get-PropertyValue $baselineResponse 'content'))) {
                $currentMedia = Get-PropertyValue $currentResponse.content $mediaType
                if ($null -eq $currentMedia) {
                    $breaking.Add("$label response $status no longer returns media type '$mediaType'.")
                    continue
                }
                Compare-Schema -Baseline (Get-PropertyValue $baselineResponse.content $mediaType).schema -Current $currentMedia.schema `
                    -BaselineDocument $baselineDocument -CurrentDocument $currentDocument `
                    -Direction 'response' -Location "$label response $status" -Visited $visited
            }
        }
    }
}

$currentPathCount = (Get-PropertyNames $currentDocument.paths).Count
$added = @(Get-PropertyNames $currentDocument.paths | Where-Object { $baselinePaths -notcontains $_ })

Write-Host "Compared $operationCount operations across $($baselinePaths.Count) baseline paths against $currentPathCount current paths."
if ($added.Count -gt 0) {
    Write-Host "$($added.Count) endpoint(s) added since the baseline, which is allowed:"
    foreach ($path in ($added | Sort-Object)) { Write-Host "  + $path" }
}

if ($warnings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($warnings.Count) change(s) worth noting, none of which break the promise:" -ForegroundColor Yellow
    foreach ($warning in $warnings) { Write-Host "  ! $warning" -ForegroundColor Yellow }
}

if ($breaking.Count -gt 0) {
    Write-Host ''
    Write-Host "$($breaking.Count) breaking change(s) against $BaselinePath" -ForegroundColor Red
    foreach ($change in $breaking) { Write-Host "  - $change" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'VERSIONING.md forbids these within a major version. Either revert the' -ForegroundColor Red
    Write-Host 'change, or serve it under /api/v2. Do not move the baseline to pass.' -ForegroundColor Red
    throw "$($breaking.Count) breaking API change(s) detected."
}

Write-Host 'No breaking API changes against the accepted baseline.'
