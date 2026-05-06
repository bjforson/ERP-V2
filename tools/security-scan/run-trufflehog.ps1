<#
.SYNOPSIS
  Sprint 52 / FU-truffleHog-gitleaks-integration (Sprint 30) - production-grade secret scan.

.DESCRIPTION
  Wraps `trufflehog` (https://github.com/trufflesecurity/trufflehog) over the
  v2 git history + working tree. Replaces the best-effort regex shape of
  `check-secrets.ps1` with a real SAST tool that:

    - Verifies live secrets (API keys that resolve via the provider) and
      flags them P0.
    - Scans the full git history (not just the current tree) so a secret
      committed and later reverted is still caught.
    - Supports a `--no-verification` mode for offline / CI runs.

  Skip-when-tool-unavailable: if `trufflehog` is not on PATH, the script
  WARNS and exits 0 (CI doesn't break on dev boxes that haven't installed
  it). The Phase V audit is the moment to confirm it IS installed; the
  audit-checklist-2026 SEC-SECRETS-8 verify step explicitly requires the
  tool to be present, not the wrapper to soft-skip.

  We picked trufflehog over gitleaks because:
    - trufflehog has a verifier mode (active / inactive) — distinguishes
      "this looks like an AWS key" from "this IS an AWS key that resolves";
      reduces false-positive triage time substantially.
    - Single static binary, no runtime dependencies (Go-built); same
      install shape as gitleaks but the verifier is a meaningful
      differentiator at audit time.
    - Both are free-software / commercial-friendly licenses (Apache-2.0
      for trufflehog, MIT for gitleaks); license-posture neutral.

.NOTES
  Companion to docs/security/audit-checklist-2026.md SEC-SECRETS-1 + SEC-SECRETS-8.
  Replaces the SEC-SECRETS-8 verify line that previously called check-secrets.ps1.

  Install:
    Windows (winget):    winget install TruffleSecurity.TruffleHog
    Linux (Debian):      curl -sSfL https://raw.githubusercontent.com/trufflesecurity/trufflehog/main/scripts/install.sh | sh -s -- -b /usr/local/bin
    macOS (homebrew):    brew install trufflehog

  Tool homepage: https://github.com/trufflesecurity/trufflehog

.PARAMETER Mode
  'history' (default) - scan the full git history (slow first run; cached
  thereafter). 'tree' - scan the current working tree only (fast).
  Phase V audit MUST run 'history' at least once.

.PARAMETER OnlyVerified
  When $true, the report only contains findings the tool actively
  verified against the provider. False-positive count drops dramatically;
  use for noisy first runs. Default $false (verified + unverified both
  reported, marked separately).
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')),
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports'),
    [ValidateSet('history', 'tree')]
    [string]$Mode = 'history',
    [switch]$OnlyVerified
)

$ErrorActionPreference = 'Stop'
$date = Get-Date -Format 'yyyy-MM-dd'
$outputFile = Join-Path $OutputFolder "$date-secrets-trufflehog.md"

if (-not (Test-Path $OutputFolder)) {
    New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
}

# ----- Tool availability probe ------------------------------------------------
$trufflehog = Get-Command trufflehog -ErrorAction SilentlyContinue
if ($null -eq $trufflehog) {
    $stub = @()
    $stub += "# NickERP v2 trufflehog secret-scan report (skipped)"
    $stub += ""
    $stub += "**Date (UTC):** $date"
    $stub += "**Status:** SKIPPED — trufflehog not on PATH on this host."
    $stub += ""
    $stub += "Install: see header of run-trufflehog.ps1 for the per-OS install command."
    $stub += ""
    $stub += "Phase V audit verify step requires the tool to be present; this skip is"
    $stub += "tolerable on a dev box but BLOCKS at pilot-readiness review. See"
    $stub += "audit-checklist-2026 SEC-SECRETS-8."
    $stub -join "`n" | Out-File -FilePath $outputFile -Encoding utf8 -NoNewline

    Write-Host "trufflehog NOT FOUND on PATH." -ForegroundColor Yellow
    Write-Host "Wrote a 'skipped' placeholder report to: $outputFile" -ForegroundColor Yellow
    Write-Host "Install via the per-OS command in the script header, then re-run." -ForegroundColor Yellow

    # Skip-when-unavailable pattern — don't break CI.
    exit 0
}

Write-Host "NickERP secret scan — trufflehog $((& trufflehog --version 2>&1) -join ' ')" -ForegroundColor Cyan
Write-Host "  RepoRoot: $RepoRoot"
Write-Host "  Mode:     $Mode"
Write-Host "  Output:   $outputFile"
Write-Host ""

# ----- Build trufflehog argv --------------------------------------------------
$args = @()
switch ($Mode) {
    'history' {
        $args += 'git'
        $args += "file://$RepoRoot"
        # --no-verification is intentionally NOT set; we want the verifier
        # to discriminate live from inactive secrets.
        $args += '--json'
        $args += '--include-detectors'
        $args += 'all'
    }
    'tree' {
        $args += 'filesystem'
        $args += $RepoRoot
        $args += '--json'
        $args += '--include-detectors'
        $args += 'all'
        # Tree mode skips git-aware scanning — still scans the working
        # tree for secrets in uncommitted files.
    }
}

if ($OnlyVerified) {
    $args += '--only-verified'
}

# Excludes — don't scan the v1-clone (read-only ported source) or the
# .worktrees subdirs (parallel sprint masters' scratch space).
$excludesFile = Join-Path $env:TEMP "nickerp-trufflehog-excludes-$([Guid]::NewGuid().ToString('N')).txt"
@(
    'v1-clone/'
    '.worktrees/'
    '**/bin/'
    '**/obj/'
    '**/node_modules/'
    'storage/onnx-ocr/**'
    'tools/security-scan/reports/'
) | Out-File -FilePath $excludesFile -Encoding utf8 -NoNewline

if ($Mode -eq 'tree') {
    # filesystem mode honours --exclude-paths; git mode doesn't natively
    # so we filter post-hoc.
    $args += '--exclude-paths'
    $args += $excludesFile
}

# ----- Run --------------------------------------------------------------------
Write-Host "Running: trufflehog $($args -join ' ')" -ForegroundColor DarkGray
$rawOutput = & trufflehog $args 2>&1

# trufflehog emits one JSON object per finding on stdout. Stderr carries
# progress lines we ignore.
$findings = New-Object System.Collections.Generic.List[object]
foreach ($line in $rawOutput) {
    if ($line -isnot [string]) { $line = "$line" }
    $trim = $line.Trim()
    if (-not $trim.StartsWith('{')) { continue }
    try {
        $obj = $trim | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        continue
    }
    if ($null -eq $obj.SourceMetadata) { continue }
    [void]$findings.Add($obj)
}

# Post-hoc filter for git mode (since --exclude-paths isn't supported there).
if ($Mode -eq 'history') {
    $findings = @($findings | Where-Object {
        $path = $_.SourceMetadata.Data.Git.file
        if ([string]::IsNullOrEmpty($path)) { return $true }
        # Exclude v1-clone, worktrees, bin/obj, node_modules.
        return $path -notmatch '(^|/)v1-clone/' `
           -and $path -notmatch '(^|/)\.worktrees/' `
           -and $path -notmatch '(^|/)(bin|obj|node_modules)/'
    })
}

# Cleanup the excludes file if we wrote one.
if (Test-Path $excludesFile) {
    Remove-Item $excludesFile -Force
}

# ----- Build report -----------------------------------------------------------
$report = @()
$report += "# NickERP v2 secret-scan report (trufflehog)"
$report += ""
$report += "**Date (UTC):** $date"
$report += "**Tool:** $((& trufflehog --version 2>&1) -join ' ')"
$report += "**Mode:** $Mode"
$report += "**OnlyVerified:** $($OnlyVerified.IsPresent)"
$report += "**Source:** $RepoRoot"
$report += "**Findings:** $($findings.Count)"
$report += ""
$report += "Reference: docs/security/audit-checklist-2026.md SEC-SECRETS-1 + SEC-SECRETS-8."
$report += ""
$report += "Replaces the legacy ``check-secrets.ps1`` (best-effort regex). The"
$report += "regex script is preserved for now as a fast smoke check; this"
$report += "wrapper is the canonical Phase V tool."
$report += ""
$report += "---"
$report += ""

if ($findings.Count -eq 0) {
    $report += "**Result:** clean. trufflehog reported zero detections across the $Mode scan."
}
else {
    $verified = @($findings | Where-Object { $_.Verified })
    $unverified = @($findings | Where-Object { -not $_.Verified })

    $report += "## Summary"
    $report += ""
    $report += "| Verified | Count |"
    $report += "|---|---|"
    $report += "| YES (live secret) | $(($verified | Measure-Object).Count) |"
    $report += "| NO (high-entropy match, not validated) | $(($unverified | Measure-Object).Count) |"
    $report += ""

    if ($verified.Count -gt 0) {
        $report += "## Verified secrets — P0 (rotate IMMEDIATELY)"
        $report += ""
        $report += "| Detector | File | Commit | Line | Source |"
        $report += "|---|---|---|---|---|"
        foreach ($f in $verified) {
            $det = $f.DetectorName
            $file = $f.SourceMetadata.Data.Git.file
            $commit = if ($f.SourceMetadata.Data.Git.commit) { $f.SourceMetadata.Data.Git.commit.Substring(0, 8) } else { '' }
            $line = $f.SourceMetadata.Data.Git.line
            $report += "| $det | ``$file`` | $commit | $line | $($f.SourceName) |"
        }
        $report += ""
    }

    if ($unverified.Count -gt 0) {
        $report += "## Unverified high-entropy matches — P2 (manual review)"
        $report += ""
        $report += "Many of these are false positives (random base64 strings,"
        $report += "test fixtures, placeholder credentials). Review each;"
        $report += "annotate false positives with a ``# trufflehog:ignore``"
        $report += "comment near the line."
        $report += ""
        $report += "| Detector | File | Commit | Line |"
        $report += "|---|---|---|---|"
        foreach ($f in $unverified) {
            $det = $f.DetectorName
            $file = $f.SourceMetadata.Data.Git.file
            $commit = if ($f.SourceMetadata.Data.Git.commit) { $f.SourceMetadata.Data.Git.commit.Substring(0, 8) } else { '' }
            $line = $f.SourceMetadata.Data.Git.line
            $report += "| $det | ``$file`` | $commit | $line |"
        }
        $report += ""
    }

    $report += "## Triage guidance"
    $report += ""
    $report += "- **VERIFIED**: rotate the secret on the provider side BEFORE rewriting git history. trufflehog's verifier already proved the secret is live; treat as compromised."
    $report += "- **UNVERIFIED**: open the file at the cited line. False positive (e.g. UUID in a test) → add a ``# trufflehog:ignore`` comment. Real secret → rotate + git-history rewrite."
}

$report += ""
$report -join "`n" | Out-File -FilePath $outputFile -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "Report written to: $outputFile" -ForegroundColor Cyan
Write-Host "Summary: $($findings.Count) finding(s)" -ForegroundColor $(if ($findings.Count -gt 0) { 'Yellow' } else { 'Green' })

# Exit code: 2 if any verified findings (P0), 0 otherwise.
$verifiedCount = ($findings | Where-Object { $_.Verified } | Measure-Object).Count
if ($verifiedCount -gt 0) {
    Write-Host "ERROR: $verifiedCount VERIFIED secret(s) detected. Rotate IMMEDIATELY." -ForegroundColor Red
    exit 2
}

exit 0
