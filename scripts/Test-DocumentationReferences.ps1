<#
.SYNOPSIS
    Verifies that documentation references in tracked files actually resolve.

.DESCRIPTION
    The source cites architecture decisions by number, for example "(ADR-019)"
    in a migration comment. Those citations are only useful if the reader can
    look them up, so this script fails when a cited ADR has no entry in
    docs/DECISIONS.md.

    This check exists because docs/ was once excluded from publication wholesale
    while the tracked source carried 214 ADR citations. Every one of them
    resolved on the maintainer's machine and none of them resolved in the public
    repository, which is exactly the failure the check now prevents.

    It also verifies relative Markdown links between tracked files, so a
    published document cannot link to something that was left unpublished.

    Only tracked files are examined. An untracked working note may cite whatever
    it likes; it is not what a reader of the repository sees.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $failures = [System.Collections.Generic.List[string]]::new()

    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed; this script must run inside a Git working tree.'
    }
    $trackedSet = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]$tracked,
        [System.StringComparer]::OrdinalIgnoreCase)

    # ---------------------------------------------------------------- ADRs
    $decisionsPath = 'docs/DECISIONS.md'
    if (-not $trackedSet.Contains($decisionsPath)) {
        $failures.Add("$decisionsPath is not tracked, so no ADR citation in this repository can resolve.")
        $defined = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    }
    else {
        $decisionsText = Get-Content -Raw -LiteralPath $decisionsPath
        $defined = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($m in [regex]::Matches($decisionsText, '(?m)^#{1,6}\s*(ADR-\d{3})')) {
            [void]$defined.Add($m.Groups[1].Value)
        }
        if ($defined.Count -eq 0) {
            $failures.Add("$decisionsPath defines no ADR headings; the heading format may have changed.")
        }
    }

    # Text-bearing tracked files only. Binary and generated payloads are skipped:
    # the pinned OpenAPI snapshot is a capture, not prose.
    $textExtensions = @('.cs', '.md', '.ps1', '.sh', '.yml', '.yaml', '.sql', '.csproj', '.props', '.targets')
    $skipPaths = @('contracts/backend.openapi.json')

    $citations = @{}
    foreach ($file in $tracked) {
        if ($skipPaths -contains $file) { continue }
        $extension = [System.IO.Path]::GetExtension($file)
        if ($textExtensions -notcontains $extension) { continue }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }

        $content = Get-Content -Raw -LiteralPath $file
        if ([string]::IsNullOrEmpty($content)) { continue }

        foreach ($m in [regex]::Matches($content, 'ADR-\d{3}')) {
            $adr = $m.Value
            if (-not $citations.ContainsKey($adr)) {
                $citations[$adr] = $file
            }
        }
    }

    foreach ($adr in ($citations.Keys | Sort-Object)) {
        if (-not $defined.Contains($adr)) {
            $failures.Add("$adr is cited in $($citations[$adr]) but has no entry in $decisionsPath.")
        }
    }

    Write-Host "Checked $($citations.Count) distinct ADR citations against $($defined.Count) published decisions."

    # ------------------------------------------------- relative Markdown links
    $linkCount = 0
    foreach ($file in $tracked) {
        if ([System.IO.Path]::GetExtension($file) -ne '.md') { continue }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }

        $content = Get-Content -Raw -LiteralPath $file
        $directory = [System.IO.Path]::GetDirectoryName($file)

        foreach ($m in [regex]::Matches($content, '\]\(([^)]+)\)')) {
            $target = $m.Groups[1].Value.Trim()

            # Absolute URLs, anchors, and mail links are out of scope.
            if ($target -match '^(https?:|mailto:|#)') { continue }

            # Drop any fragment; the file is what must exist.
            $target = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($target)) { continue }

            $linkCount++
            $combined = if ([string]::IsNullOrEmpty($directory)) { $target } else { Join-Path $directory $target }
            $normalized = ($combined -replace '\\', '/')

            # Resolve any ../ segments without touching the file system.
            $segments = [System.Collections.Generic.List[string]]::new()
            foreach ($segment in ($normalized -split '/')) {
                if ($segment -eq '.' -or $segment -eq '') { continue }
                if ($segment -eq '..') {
                    if ($segments.Count -gt 0) { $segments.RemoveAt($segments.Count - 1) }
                    continue
                }
                $segments.Add($segment)
            }
            $resolved = [string]::Join('/', $segments)

            if (-not $trackedSet.Contains($resolved)) {
                # A link to a directory is fine when the directory has tracked content.
                $isDirectory = $tracked | Where-Object { $_.StartsWith("$resolved/", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
                if (-not $isDirectory) {
                    $failures.Add("$file links to '$($m.Groups[1].Value)', which is not a tracked file.")
                }
            }
        }
    }

    Write-Host "Checked $linkCount relative Markdown links between tracked files."

    if ($failures.Count -gt 0) {
        Write-Host ''
        Write-Host "Documentation reference check failed with $($failures.Count) problem(s):" -ForegroundColor Red
        foreach ($failure in $failures) {
            Write-Host "  - $failure" -ForegroundColor Red
        }
        throw "$($failures.Count) documentation reference(s) do not resolve."
    }

    Write-Host 'Documentation references verified.'
}
finally {
    Pop-Location
}
