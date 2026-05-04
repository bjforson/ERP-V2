<#
.SYNOPSIS
  Sprint 30 Phase V helper - best-effort secret-scan across the v2 tree.

.DESCRIPTION
  Greps for common secret-shaped patterns (AWS keys, GitHub PATs, JWT bearers,
  Slack tokens, hardcoded passwords) across the repo. Excludes bin/, obj/,
  node_modules/, .git/, .worktrees/, v1-clone/, and binary file extensions.

  Best-effort. NOT a replacement for a real SAST tool (truffleHog, gitleaks)
  during Phase V. Use this for quick local sanity checks; rely on a real tool
  for the production audit.

  Companion to docs/security/audit-checklist-2026.md SEC-SECRETS-1 + SEC-SECRETS-8.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')),
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports')
)

$ErrorActionPreference = 'Stop'
$date = Get-Date -Format 'yyyy-MM-dd'
$outputFile = Join-Path $OutputFolder "$date-secret-scan.md"

if (-not (Test-Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
}

# Pattern catalogue. Each entry = (name, regex, severity).
$patterns = @(
    @{ Name = 'AWS Access Key'; Regex = 'AKIA[0-9A-Z]{16}'; Severity = 'P0' },
    @{ Name = 'AWS Secret (heuristic 40-char base64)'; Regex = '(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{40}(?![A-Za-z0-9+/])'; Severity = 'P2-noisy' },
    @{ Name = 'GitHub Personal Access Token'; Regex = 'ghp_[A-Za-z0-9]{36}'; Severity = 'P0' },
    @{ Name = 'GitHub OAuth Token'; Regex = 'gho_[A-Za-z0-9]{36}'; Severity = 'P0' },
    @{ Name = 'Slack Bot Token'; Regex = 'xoxb-[A-Za-z0-9-]+'; Severity = 'P0' },
    @{ Name = 'JWT Bearer (header pattern)'; Regex = 'Bearer\s+ey[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+'; Severity = 'P0' },
    @{ Name = 'Hardcoded password assignment'; Regex = '(password|pwd|secret)\s*[=:]\s*"[A-Za-z0-9!@#$%^&*()_+\-]{8,}"'; Severity = 'P1' },
    @{ Name = 'Connection string with password'; Regex = 'Password=[A-Za-z0-9!@#$%^&*()_+\-]{6,}'; Severity = 'P1' },
    @{ Name = 'Private key block'; Regex = '-----BEGIN (RSA |DSA |EC |OPENSSH )?PRIVATE KEY-----'; Severity = 'P0' }
)

# Excluded relative-path segments. Filtering applies after stripping $RepoRoot so
# the exclusion does not catch legitimate code when invoked from a worktree.
$excludeRelativeRegex = '(^|[\\/])(bin|obj|node_modules|\.git|v1-clone|publish|reports)([\\/]|$)'
$excludeWorktreeRegex = '(^|[\\/])\.worktrees([\\/]|$)'

# Excluded file extensions.
$excludeExtensions = @(
    '.dll', '.exe', '.pdb', '.so', '.dylib', '.bin',
    '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tiff', '.ico',
    '.pdf', '.zip', '.7z', '.tar', '.gz',
    '.lock', '.user', '.suo', '.cache'
)

Write-Host "NickERP secret-scan (best effort)" -ForegroundColor Cyan
Write-Host "  RepoRoot: $RepoRoot"
Write-Host ""

$rootFull = (Resolve-Path $RepoRoot).Path
$files = Get-ChildItem -Path $RepoRoot -Recurse -File |
    Where-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/')
        $relative -notmatch $excludeRelativeRegex -and
        $relative -notmatch $excludeWorktreeRegex -and
        ($excludeExtensions -notcontains $_.Extension.ToLower())
    }

Write-Host "Scanning $($files.Count) files..."

$findings = @()

foreach ($file in $files) {
    $content = $null
    try {
        # Read as text; skip files > 5MB (heuristic for binaries we missed)
        if ($file.Length -gt 5MB) { continue }
        $content = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop
    }
    catch {
        continue
    }
    if ([string]::IsNullOrEmpty($content)) { continue }

    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches($content, $pattern.Regex)
        foreach ($m in $matches) {
            # Skip the noisy AWS-secret heuristic if the file looks like sample data
            if ($pattern.Name -like '*heuristic*' -and $file.FullName -match '\.(test|sample|spec|md)\.|test-corpus') {
                continue
            }

            # Find line number
            $lineNumber = ($content.Substring(0, $m.Index) -split "`n").Length

            $findings += [pscustomobject]@{
                File = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                Line = $lineNumber
                Pattern = $pattern.Name
                Severity = $pattern.Severity
                Match = if ($m.Value.Length -gt 80) { $m.Value.Substring(0, 80) + '...' } else { $m.Value }
            }
        }
    }
}

# Build report
$report = @()
$report += "# NickERP v2 secret-scan report (best effort)"
$report += ""
$report += "**Date (UTC):** $date"
$report += "**Files scanned:** $($files.Count)"
$report += "**Findings:** $($findings.Count)"
$report += ""
$report += "Reference: docs/security/audit-checklist-2026.md SEC-SECRETS-1 + SEC-SECRETS-8."
$report += ""
$report += "**Tool note:** This is a best-effort regex scan. Real Phase V audit MUST run a"
$report += "production-grade SAST tool (truffleHog, gitleaks, etc.) over the full git history."
$report += ""
$report += "---"
$report += ""

if ($findings.Count -eq 0) {
    $report += "**Result:** clean. No secret-shaped patterns matched."
}
else {
    $bySeverity = $findings | Group-Object -Property Severity | Sort-Object Name

    $report += "## Findings by severity"
    $report += ""
    $report += "| Severity | Count |"
    $report += "|---|---|"
    foreach ($g in $bySeverity) {
        $report += "| $($g.Name) | $($g.Count) |"
    }
    $report += ""
    $report += "## Detail"
    $report += ""
    $report += "| File | Line | Pattern | Severity | Match preview |"
    $report += "|---|---|---|---|---|"
    foreach ($f in $findings | Sort-Object Severity, File) {
        $previewSafe = ($f.Match -replace '\|', '\|') -replace '`', "'"
        $report += "| ``$($f.File)`` | $($f.Line) | $($f.Pattern) | $($f.Severity) | ``$previewSafe`` |"
    }
    $report += ""
    $report += "## Triage guidance"
    $report += ""
    $report += "- **P0** findings: investigate IMMEDIATELY. Real secret = rotate + git-history rewrite + audit access logs."
    $report += "- **P1** findings: investigate within 1 business day. Most likely a hardcoded test-only credential to move to env var."
    $report += "- **P2-noisy** findings: many false positives expected (random base64 strings happen). Manual review only."
    $report += ""
    $report += "False-positive triage: add a comment ``// NOSECRET: ...`` near the match (real SAST tools honour this; this script does NOT yet)."
}

$report -join "`n" | Out-File -FilePath $outputFile -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "Report written to: $outputFile" -ForegroundColor Cyan
Write-Host "Summary: $($findings.Count) potential finding(s)" -ForegroundColor $(if ($findings.Count -gt 0) { 'Yellow' } else { 'Green' })

# Exit code: 0 if no P0; 2 if any P0
$p0Count = ($findings | Where-Object { $_.Severity -eq 'P0' }).Count
if ($p0Count -gt 0) {
    Write-Host "WARN: $p0Count P0 finding(s) require IMMEDIATE investigation." -ForegroundColor Red
    exit 2
}

exit 0
