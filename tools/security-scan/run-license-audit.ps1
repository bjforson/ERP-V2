<#
.SYNOPSIS
  Sprint 52 / FU-license-audit-tool (Sprint 30) - third-party NuGet license audit.

.DESCRIPTION
  Wraps `dotnet list package --include-transitive` across every project in the
  v2 tree, resolves each package's license via the .nuspec metadata in the
  global packages folder, and cross-references against
  `tools/security-scan/license-allowlist.json`. Emits a markdown report that
  flags any package on a non-allowlisted license.

  Replaces the SEC-DEP-3 "manual review" line in audit-checklist-2026.md.

.NOTES
  Companion to docs/security/audit-checklist-2026.md SEC-DEP-3.
  Findings on a non-allowlisted license: P1.

  Why parse .nuspec rather than a NuGet API call: works offline; no auth /
  rate-limit dance; scales to "every project in the tree" without N HTTP
  round-trips. The trade-off is that the global-packages folder must be
  populated (i.e. `dotnet restore` must have run somewhere recently). The
  script no-ops with a warning if the package isn't found locally; CI runs
  `dotnet restore` before this script.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')),
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports'),
    [string]$AllowlistPath = (Join-Path $PSScriptRoot 'license-allowlist.json')
)

$ErrorActionPreference = 'Stop'
$date = Get-Date -Format 'yyyy-MM-dd'
$outputFile = Join-Path $OutputFolder "$date-license-audit.md"

if (-not (Test-Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
}

# Load + parse the allowlist.
if (-not (Test-Path $AllowlistPath)) {
    Write-Error "License allowlist not found at $AllowlistPath"
    exit 2
}
$allowlist = Get-Content -Raw -LiteralPath $AllowlistPath | ConvertFrom-Json
$allowed = @($allowlist.allowed)
# Case-insensitive alias map. NuGet metadata licenses arrive in mixed case;
# the allowlist canonical-form is SPDX-shaped; the aliases bridge them. We
# preserve display-case in the report but key off lower-case for lookup.
$aliases = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($prop in $allowlist.alias_normalisations.PSObject.Properties) {
    if ($prop.Name -ne '$comment') {
        $aliases[$prop.Name] = $prop.Value
    }
}

Write-Host "NickERP license audit" -ForegroundColor Cyan
Write-Host "  RepoRoot:  $RepoRoot"
Write-Host "  Allowlist: $AllowlistPath ($(($allowed -join ', ')))"
Write-Host "  Output:    $outputFile"
Write-Host ""

# Find every csproj in the tree, excluding bin/, obj/, v1-clone/, worktrees.
$rootFull = (Resolve-Path $RepoRoot).Path
$projects = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
    Where-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/')
        $relative -notmatch '(^|[\\/])(bin|obj|node_modules|v1-clone|publish)([\\/]|$)' -and
        $relative -notmatch '(^|[\\/])\.worktrees([\\/]|$)'
    } |
    Sort-Object FullName

Write-Host "Scanning $($projects.Count) projects..."

# Collect (package, version) pairs across all projects. Dedupe so the same
# package referenced from N projects shows once with its aggregated source list.
$packageMap = @{}

foreach ($project in $projects) {
    $relativePath = $project.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
    Write-Host "  $relativePath" -NoNewline
    try {
        $output = & dotnet list $project.FullName package --include-transitive 2>&1 | Out-String
    }
    catch {
        Write-Host "  ERROR" -ForegroundColor Red
        continue
    }
    if ($LASTEXITCODE -ne 0 -and $output -match 'No packages were found') {
        Write-Host "  (no packages)" -ForegroundColor DarkGray
        continue
    }

    # Lines look like:
    #   > Microsoft.IdentityModel.Tokens      8.16.0           8.16.0
    # for top-level, and
    #   > Microsoft.IdentityModel.Tokens      8.16.0
    # for transitive (only resolved version present). Capture both.
    $foundCount = 0
    foreach ($line in $output -split "`r?`n") {
        if ($line -match '^\s*>\s+([A-Za-z0-9_.-]+)\s+([0-9][^\s]*)') {
            $name = $matches[1]
            $version = $matches[2]
            $key = "$name|$version"
            if (-not $packageMap.ContainsKey($key)) {
                $packageMap[$key] = @{
                    Name = $name
                    Version = $version
                    Sources = New-Object System.Collections.Generic.List[string]
                }
            }
            [void]$packageMap[$key].Sources.Add($relativePath)
            $foundCount++
        }
    }
    Write-Host "  ($foundCount refs)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Resolving licenses for $($packageMap.Count) unique (package, version) pairs..." -ForegroundColor Cyan

# NuGet global-packages location. PSv7 / .NET SDK convention:
#   $env:USERPROFILE\.nuget\packages          (Windows)
#   $env:HOME/.nuget/packages                 (Linux / macOS)
$nugetGlobal = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} elseif ($IsWindows -or $env:USERPROFILE) {
    Join-Path $env:USERPROFILE '.nuget\packages'
} else {
    Join-Path $env:HOME '.nuget/packages'
}

if (-not (Test-Path $nugetGlobal)) {
    Write-Error "NuGet global-packages folder not found at $nugetGlobal. Run 'dotnet restore' first."
    exit 2
}

$findings = New-Object System.Collections.Generic.List[object]

function Resolve-LicenseFromNuspec {
    param(
        [string]$PackageName,
        [string]$Version
    )

    $packageFolder = Join-Path $nugetGlobal ($PackageName.ToLowerInvariant())
    $versionFolder = Join-Path $packageFolder $Version
    if (-not (Test-Path $versionFolder)) {
        return @{ Status = 'not-restored'; License = $null; Source = $null }
    }

    $nuspec = Get-ChildItem -Path $versionFolder -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspec) {
        return @{ Status = 'no-nuspec'; License = $null; Source = $null }
    }

    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -Raw

    # Modern NuSpec uses <license type="expression">MIT</license>; older
    # ones use <licenseUrl>https://...</licenseUrl>. We honour both.
    $metadata = $xml.package.metadata

    $licenseNode = $metadata.license
    if ($null -ne $licenseNode -and $licenseNode -ne '') {
        if ($licenseNode -is [System.Xml.XmlElement]) {
            $type = $licenseNode.type
            $value = $licenseNode.'#text'
            if ([string]::IsNullOrEmpty($value)) {
                $value = $licenseNode.InnerText
            }

            # type="file" — the value is a relative path inside the package
            # to a LICENSE / LICENSE.txt / LICENSE.md. We try to identify
            # the actual license by scanning the first 80 lines for known
            # SPDX-id lines or canonical license markers.
            if ($type -eq 'file' -and -not [string]::IsNullOrEmpty($value)) {
                $licenseFile = Join-Path $versionFolder $value
                if (Test-Path $licenseFile) {
                    $sniff = Get-Content -LiteralPath $licenseFile -TotalCount 80 -ErrorAction SilentlyContinue | Out-String
                    $sniffMatch = switch -Regex ($sniff) {
                        'Apache License,?\s*Version\s*2\.0'                        { 'Apache-2.0'; break }
                        '(?im)^\s*MIT License\s*$'                                  { 'MIT'; break }
                        'Permission is hereby granted, free of charge,.*MIT'        { 'MIT'; break }
                        'Permission is hereby granted, free of charge,'             { 'MIT'; break }
                        'BSD 3-Clause'                                              { 'BSD-3-Clause'; break }
                        'BSD 2-Clause'                                              { 'BSD-2-Clause'; break }
                        'Redistribution and use in source and binary forms.*neither the name'    { 'BSD-3-Clause'; break }
                        'PostgreSQL License'                                        { 'PostgreSQL'; break }
                        'ISC License'                                               { 'ISC'; break }
                        'MICROSOFT SOFTWARE LICENSE TERMS'                          { 'MS-EULA'; break }
                        'Microsoft Public License'                                  { 'MS-PL'; break }
                        default { $null }
                    }
                    if ($null -ne $sniffMatch) {
                        return @{
                            Status = 'ok'
                            License = $sniffMatch
                            Source = "nuspec.license[type=file][sniff=$value]"
                        }
                    }
                }
            }

            return @{
                Status = 'ok'
                License = $value
                Source = "nuspec.license[type=$type]"
            }
        }
        elseif ($licenseNode -is [string]) {
            return @{
                Status = 'ok'
                License = $licenseNode
                Source = 'nuspec.license[string]'
            }
        }
    }

    $licenseUrl = $metadata.licenseUrl
    if (-not [string]::IsNullOrEmpty($licenseUrl)) {
        # Common URL-to-license mappings the .NET ecosystem uses.
        $mapped = switch -Wildcard ($licenseUrl) {
            '*licenses/MIT*'                                 { 'MIT' ; break }
            '*licenses/Apache-2.0*'                          { 'Apache-2.0' ; break }
            '*licenses/BSD-3-Clause*'                        { 'BSD-3-Clause' ; break }
            '*opensource.org/licenses/MIT*'                  { 'MIT' ; break }
            # MS .NET Library license (legacy fwlink LinkId; canonical for
            # System.* / Microsoft.* packages predating the SPDX migration).
            '*go.microsoft.com/fwlink*LinkId=329770*'        { 'MS-EULA' ; break }
            '*go.microsoft.com/fwlink*LinkID=329770*'        { 'MS-EULA' ; break }
            '*aka.ms/dotnet-license*'                        { 'MS-EULA' ; break }
            '*aka.ms/deprecateLicenseUrl*'                   { 'MS-EULA' ; break }
            '*github.com/dotnet/runtime/blob/main/LICENSE.TXT' { 'MIT' ; break }
            '*github.com/dotnet/*LICENSE*'                   { 'MIT' ; break }
            '*github.com/xunit/*LICENSE'                     { 'Apache-2.0' ; break }
            default { $null }
        }
        return @{
            Status = if ($null -ne $mapped) { 'ok' } else { 'url-only' }
            License = $mapped
            Source = "nuspec.licenseUrl=$licenseUrl"
        }
    }

    return @{ Status = 'no-license-metadata'; License = $null; Source = 'nuspec' }
}

foreach ($key in ($packageMap.Keys | Sort-Object)) {
    $pkg = $packageMap[$key]
    $resolved = Resolve-LicenseFromNuspec -PackageName $pkg.Name -Version $pkg.Version

    $rawLicense = $resolved.License
    $canonical = if ($rawLicense -and $aliases.ContainsKey($rawLicense)) { $aliases[$rawLicense] } else { $rawLicense }

    $isAllowed = $false
    if ($canonical -and $allowed -contains $canonical) {
        $isAllowed = $true
    }

    $finding = [pscustomobject]@{
        Package = $pkg.Name
        Version = $pkg.Version
        License = $canonical
        Raw = $rawLicense
        Status = $resolved.Status
        Source = $resolved.Source
        Allowed = $isAllowed
        UsedBy = ($pkg.Sources | Sort-Object -Unique) -join ', '
    }
    [void]$findings.Add($finding)
}

# ----- Build report -------------------------------------------------------
$report = @()
$report += "# NickERP v2 license audit report"
$report += ""
$report += "**Date (UTC):** $date"
$report += "**Tool:** ``run-license-audit.ps1`` + ``dotnet list package --include-transitive``"
$report += "**Allowlist:** ``tools/security-scan/license-allowlist.json`` (version $($allowlist.version))"
$report += "**Allowed licenses:** $($allowed -join ', ')"
$report += "**Unique (package, version) pairs:** $($findings.Count)"
$report += ""
$report += "Reference: docs/security/audit-checklist-2026.md SEC-DEP-3."
$report += ""
$report += "---"
$report += ""

$blocked = @($findings | Where-Object { -not $_.Allowed })
$unknown = @($findings | Where-Object { $_.Status -ne 'ok' })

$report += "## Summary"
$report += ""
$report += "| Bucket | Count |"
$report += "|---|---|"
$report += "| Allowed | $((($findings | Where-Object { $_.Allowed }) | Measure-Object).Count) |"
$report += "| Non-allowlisted licenses | $((($findings | Where-Object { $_.Status -eq 'ok' -and -not $_.Allowed }) | Measure-Object).Count) |"
$report += "| Unknown / missing license metadata | $(($unknown | Measure-Object).Count) |"
$report += ""

if ($blocked.Count -eq 0) {
    $report += "**Result:** clean. Every package's license is on the allowlist."
}
else {
    $report += "**Result:** $($blocked.Count) finding(s) require triage."
    $report += ""
    $report += "## Non-allowlisted findings"
    $report += ""
    $report += "| Package | Version | License (canonical) | License (raw) | Status | Used by |"
    $report += "|---|---|---|---|---|---|"
    foreach ($f in $blocked | Sort-Object Package, Version) {
        $licenseDisplay = if ($f.License) { $f.License } else { '*(unresolved)*' }
        $rawDisplay = if ($f.Raw) { $f.Raw } else { '' }
        $usedByShort = if ($f.UsedBy.Length -gt 80) {
            $f.UsedBy.Substring(0, 80) + '...'
        }
        else {
            $f.UsedBy
        }
        $report += "| ``$($f.Package)`` | $($f.Version) | $licenseDisplay | $rawDisplay | $($f.Status) | $usedByShort |"
    }
    $report += ""
    $report += "### Triage guidance"
    $report += ""
    $report += "- **non-allowlisted-but-known**: review the license. If permissive (e.g. MPL-2.0 with weak copyleft on modified files), legal review can promote it to the allowlist with a rationale entry. If strong copyleft (GPL family), find a replacement package."
    $report += "- **unresolved / not-restored**: run ``dotnet restore`` from repo root to populate the global packages folder, then re-run this script."
    $report += "- **no-license-metadata**: the package's .nuspec has neither ``<license>`` nor ``<licenseUrl>``. Check the package source / GitHub repo manually; capture the license in the alias_normalisations map of ``license-allowlist.json``."
}

$report += ""

$report -join "`n" | Out-File -FilePath $outputFile -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "Report written to: $outputFile" -ForegroundColor Cyan
Write-Host "Summary: $(($findings | Where-Object { $_.Allowed } | Measure-Object).Count) allowed, $($blocked.Count) flagged, $(($unknown | Measure-Object).Count) unresolved." `
    -ForegroundColor $(if ($blocked.Count -gt 0) { 'Yellow' } else { 'Green' })

if ($blocked.Count -gt 0) {
    Write-Host "WARN: $($blocked.Count) package(s) on a non-allowlisted license. Triage required." -ForegroundColor Yellow
    exit 2
}

exit 0
