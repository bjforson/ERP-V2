<#
.SYNOPSIS
  Sprint 30 Phase V dependency hygiene helper - find outdated NuGet references.

.DESCRIPTION
  Wraps `dotnet list package --outdated --include-transitive` across every project
  in the v2 tree. Writes a markdown report.

  Companion to docs/security/audit-checklist-2026.md SEC-DEP-2.
  Outdated packages > 12 months behind on a major version need a justification
  (or upgrade) before pilot.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')),
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports')
)

$ErrorActionPreference = 'Stop'
$date = Get-Date -Format 'yyyy-MM-dd'
$outputFile = Join-Path $OutputFolder "$date-outdated.md"

if (-not (Test-Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
}

Write-Host "NickERP outdated-package scan" -ForegroundColor Cyan

$rootFull = (Resolve-Path $RepoRoot).Path
$projects = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
    Where-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/')
        $relative -notmatch '(^|[\\/])(bin|obj|node_modules|v1-clone|publish)([\\/]|$)' -and
        $relative -notmatch '(^|[\\/])\.worktrees([\\/]|$)'
    } |
    Sort-Object FullName

$report = @()
$report += "# NickERP v2 outdated-package report"
$report += ""
$report += "**Date (UTC):** $date"
$report += "**Tool:** ``dotnet list package --outdated --include-transitive``"
$report += "**Project count:** $($projects.Count)"
$report += ""
$report += "Reference: docs/security/audit-checklist-2026.md SEC-DEP-2."
$report += ""
$report += "---"
$report += ""

$projectsWithOutdated = 0
foreach ($project in $projects) {
    $relativePath = $project.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
    Write-Host "  Scanning: $relativePath"

    try {
        $output = & dotnet list $project.FullName package --outdated --include-transitive 2>&1 | Out-String
    }
    catch {
        $report += "## $relativePath"
        $report += ""
        $report += '```'
        $report += "ERROR: $($_.Exception.Message)"
        $report += '```'
        $report += ""
        continue
    }

    $hasOutdated = $output -match 'has the following updates'
    if ($hasOutdated) {
        $projectsWithOutdated++
        $report += "## $relativePath"
        $report += ""
        $report += '```'
        $report += $output.TrimEnd()
        $report += '```'
        $report += ""
    }
}

$report += "---"
$report += ""
$report += "## Summary"
$report += ""
$report += "- Projects with outdated packages: $projectsWithOutdated of $($projects.Count)"
$report += ""
$report += "Severity guidance: any package > 12 months behind on a major version needs justification."

$report -join "`n" | Out-File -FilePath $outputFile -Encoding utf8 -NoNewline

Write-Host "Report written to: $outputFile" -ForegroundColor Cyan
exit 0
