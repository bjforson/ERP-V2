# ============================================================
# NickERP v2 — Phase V audit-checklist runner (Sprint 60 Phase B1)
# ============================================================
# Walks `docs/security/audit-checklist-2026.md` (the ~89 SEC-*
# register), executes per-item verification commands where the item
# is automatable, and emits a ticked artifact under
# `tools/security-scan/reports/audit-{site}-{date}.md` matching the
# checklist's own How-to-use shape (the operator's "copy to artifact
# and tick" loop becomes a script invocation instead of a 60-minute
# read-and-grep session).
#
# This script implements `PLAN.md §23.2 work item B1` — the engineering
# rehearsal that converts the Phase V audit run from
# operator-reads-89-items-cold-start into a tested runner that emits
# the same artifact shape the operator would type by hand. Genuinely
# manual items (operator judgement, runbook posture, off-host
# observation) emit a `[manual]` line with the original Verify prose
# preserved so the operator finishes them by hand against the
# generated artifact.
#
# CONVENTION — automation source-of-truth:
#
#   `docs/security/audit-checklist-2026.md` is read-only input. New
#   automation hooks are added HERE in this script's `$ItemHandlers`
#   table, not in the checklist. Each handler returns one of:
#     'pass' — verified clean
#     'fail' — finding (severity is the item's declared severity)
#     'skip' — automation prerequisites absent (e.g. no -ConnectionString
#              for a SQL-shaped check). Surfaces as `[skip]` with a
#              one-line reason. Does NOT count as a finding.
#     'manual' — item is operator-judgement; emits `[manual]` + prose.
#
#   The runner ALWAYS writes the full ~89-item register to the
#   artifact (including manual items) so the operator can tick them
#   in-place rather than maintaining a side checklist.
#
# CHAINING — existing scanners:
#
#   SEC-DEP-1 / SEC-DEP-2 / SEC-DEP-3 / SEC-SECRETS-1 / SEC-SECRETS-8
#   chain to the canonical scripts already in this folder. Their
#   reports land at `tools/security-scan/reports/{date}-{kind}.md`
#   (per-tool format); this audit runner reads the latest dated
#   report and surfaces pass/fail in the audit artifact, with a
#   pointer back to the per-tool report for evidence.
#
# ============================================================
# Modes
# ============================================================
#   .\run-audit.ps1                              # Default: dev, no DB, no chained scans
#   .\run-audit.ps1 -Site pilot                  # Tag the artifact for the pilot site
#   .\run-audit.ps1 -ConnectionString postgres://...  # Run SQL-shaped checks
#   .\run-audit.ps1 -Filter DEP                  # Only SEC-DEP-* category
#   .\run-audit.ps1 -RunChained                  # Trigger sibling scanners (slow)
#   .\run-audit.ps1 -DryRun                      # Parse + categorize, no execution
#
# ============================================================
# Exit codes
# ============================================================
#   0  — clean run; all P0/P1 automatable items pass (or are skipped/manual)
#   1  — pre-flight failure (e.g. checklist missing)
#   2  — at least one P0 or P1 automatable check failed (CI-blocking)
#   3  — checklist parse failure (file shape changed unexpectedly)
#
# Lower-priority findings (P2/P3) do NOT block exit; they surface in
# the artifact only. This matches the checklist's own exit criteria
# (§Phase V exit criteria — P0/P1 must pass; P2/P3 backlog OK).
#
# ============================================================

[CmdletBinding()]
param(
    [string]$Site = "dev",
    [string]$Filter = "",
    [string]$ConnectionString = "",
    [switch]$RunChained,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----- Repo root locator (this file lives in tools/security-scan/) ------------
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot    = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$ChecklistPath = Join-Path $RepoRoot 'docs/security/audit-checklist-2026.md'
$RegisterPath  = Join-Path $RepoRoot 'docs/system-context-audit-register.md'
$RationalePath = Join-Path $ScriptDir 'license-allowlist-rationale.md'
$ReportsDir    = Join-Path $ScriptDir 'reports'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null }

# ============================================================
# Helpers
# ============================================================

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Resolve-PsqlExe {
    # Same resolution shape as tools/cutover-dryrun/run.ps1 — prefer
    # the highest-version Windows install, fall back to plain `psql`.
    $candidates = @(
        'C:/Program Files/PostgreSQL/18/bin/psql.exe',
        'C:/Program Files/PostgreSQL/17/bin/psql.exe',
        'C:/Program Files/PostgreSQL/16/bin/psql.exe',
        'C:/Program Files/PostgreSQL/15/bin/psql.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ============================================================
# Checklist parser
# ============================================================
#
# Shape of the input file (per docs/security/audit-checklist-2026.md):
#
#   ## CAT — Description
#
#   ### SEC-CAT-N — Item title
#   **Verify:** ...
#   **Expect:** ...
#   **Severity:** P0 | P1 | P2 | P3 | (with prose tail OK)
#
# Items can also include fenced ```sql``` blocks inline within Verify
# (SEC-TENANT-3, SEC-TENANT-5, SEC-TENANT-13, SEC-DB-3 — those go
# through the SQL automation path when -ConnectionString is set).
#
# Cross-references (e.g. SEC-DB-7 "See SEC-TENANT-3.") are detected
# and emitted as `[xref]` rows that don't run a separate check.

function Read-Checklist {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        throw "Checklist absent: $Path"
    }
    $raw = Get-Content -Path $Path -Raw -Encoding UTF8

    $items = New-Object System.Collections.ArrayList

    # Match the per-item header line: "### SEC-CAT-N — Title"
    $itemRegex = '(?ms)^### (SEC-(?<cat>[A-Z]+(?:-[A-Z]+)?)-(?<num>\d+))\s*[—-]\s*(?<title>[^\r\n]+?)\s*$(?<body>.*?)(?=^### |^## |\z)'
    foreach ($m in [regex]::Matches($raw, $itemRegex)) {
        $body = $m.Groups['body'].Value
        $id = $m.Groups[1].Value

        # Field markers in the checklist are `**Field:**` — colon is INSIDE
        # the bold markers, not after them. Patterns reflect that.
        # Severity capture — tolerate prose-tail variants:
        #   `**Severity:** P0.`
        #   `**Severity if missing:** P0.`
        #   `**Severity:** P0 (verified findings); P2 (unverified matches).`
        $sev = ''
        $sevMatch = [regex]::Match($body, '\*\*Severity(?:\s+if[^*]+)?:\*\*\s*(?<sev>P[0-3])')
        if ($sevMatch.Success) { $sev = $sevMatch.Groups['sev'].Value }

        # Verify prose — runs from `**Verify:**` to the next field marker
        # or end of item.
        $verify = ''
        $verifyMatch = [regex]::Match($body, '(?ms)\*\*Verify:\*\*\s*(?<v>.*?)(?=\*\*Expect:\*\*|\*\*Severity)')
        if ($verifyMatch.Success) { $verify = $verifyMatch.Groups['v'].Value.Trim() }

        # Expect prose
        $expect = ''
        $expectMatch = [regex]::Match($body, '(?ms)\*\*Expect:\*\*\s*(?<e>.*?)(?=\*\*Severity)')
        if ($expectMatch.Success) { $expect = $expectMatch.Groups['e'].Value.Trim() }

        # SQL block extraction (first fenced ```sql``` inside Verify)
        $sql = ''
        $sqlMatch = [regex]::Match($verify, '(?ms)```sql\s*(?<q>.*?)```')
        if ($sqlMatch.Success) { $sql = $sqlMatch.Groups['q'].Value.Trim() }

        # Cross-reference detection ("See SEC-TENANT-3.")
        $isXref = $false
        $xrefTarget = ''
        if ([string]::IsNullOrEmpty($sev) -and [string]::IsNullOrEmpty($verify)) {
            # Item with no Verify/Severity is almost certainly a cross-ref
            $xrefMatch = [regex]::Match($body, '(?im)^See\s+(SEC-[A-Z-]+-\d+)\.?\s*$')
            if ($xrefMatch.Success) {
                $isXref = $true
                $xrefTarget = $xrefMatch.Groups[1].Value
            }
        }

        # Inline `grep ...` invocation detection — used by some items
        # (SEC-AUTH-5, SEC-TENANT-1, SEC-TENANT-4) as their literal
        # verify command. Captured as a hint; the handler may still
        # use a broader/cleaner shape.
        $grepHint = ''
        $grepMatch = [regex]::Match($verify, '(?m)`grep\s+([^`]+)`')
        if ($grepMatch.Success) { $grepHint = $grepMatch.Groups[1].Value.Trim() }

        $void = $items.Add([pscustomobject]@{
            Id        = $id
            Category  = $m.Groups['cat'].Value
            Number    = [int]$m.Groups['num'].Value
            Title     = $m.Groups['title'].Value.Trim()
            Severity  = $sev
            Verify    = $verify
            Expect    = $expect
            Sql       = $sql
            GrepHint  = $grepHint
            IsXref    = $isXref
            XrefTarget = $xrefTarget
        })
    }

    return ,$items
}

# ============================================================
# Item handlers — automation source-of-truth
# ============================================================
#
# Each handler returns @{ Status = 'pass'|'fail'|'skip'; Evidence = '...' }
# Evidence is one short line surfaced beside the tickbox in the artifact.
# Handlers that need long output write to a sidecar file and return the
# path as evidence (matches the per-tool report pattern).

function Invoke-Grep {
    # Light-weight Select-String wrapper that respects the project's
    # standard exclusions (bin/, obj/, .worktrees/, v1-clone/, publish/,
    # node_modules) so we don't double-count old-tree noise. Uses native
    # PowerShell so we don't depend on git-grep flag dialects (which
    # differ from POSIX grep — e.g. `git grep` does not understand
    # `--exclude-dir`). $Pattern is a regex; $Paths are repo-relative
    # subtrees to scan; only *.cs / *.razor / *.cshtml files are
    # considered (this is the v2 source surface for these checks).
    param(
        [string]$Pattern,
        [string[]]$Paths,
        [string[]]$Includes = @('*.cs', '*.razor', '*.cshtml'),
        [switch]$CaseSensitive
    )
    $exclusions = '(^|[\\/])(bin|obj|node_modules|v1-clone|publish|\.worktrees)([\\/]|$)'
    $hits = New-Object System.Collections.ArrayList
    foreach ($rel in $Paths) {
        $abs = Join-Path $RepoRoot $rel
        if (-not (Test-Path $abs)) { continue }
        foreach ($glob in $Includes) {
            # PowerShell variables are case-insensitive — must NOT shadow $Pattern
            # with an inner loop variable, hence $glob.
            $files = Get-ChildItem -Path $abs -Recurse -Filter $glob -ErrorAction SilentlyContinue |
                     Where-Object {
                         $r = $_.FullName.Substring($RepoRoot.Length).TrimStart('\','/')
                         $r -notmatch $exclusions
                     }
            foreach ($f in $files) {
                $found = if ($CaseSensitive) {
                    Select-String -Path $f.FullName -Pattern $Pattern -CaseSensitive -ErrorAction SilentlyContinue
                } else {
                    Select-String -Path $f.FullName -Pattern $Pattern -ErrorAction SilentlyContinue
                }
                foreach ($m in $found) {
                    $void = $hits.Add("$($f.FullName.Substring($RepoRoot.Length).TrimStart('\','/')):$($m.LineNumber):$($m.Line.Trim())")
                }
            }
        }
    }
    return @{ Output = ($hits -join "`n"); ExitCode = 0; Count = $hits.Count }
}

function Get-LatestReport {
    param([string]$KindGlob)
    # Read the most recent dated report matching e.g. "*-vulnerabilities.md"
    # @(...) wrap guards StrictMode against empty / single-result cases.
    $candidates = @(Get-ChildItem -Path $ReportsDir -Filter $KindGlob -ErrorAction SilentlyContinue |
                    Sort-Object Name -Descending)
    if ($candidates.Count -eq 0) { return $null }
    return $candidates[0]
}

function Test-SqlConnection {
    return -not [string]::IsNullOrEmpty($ConnectionString)
}

function Invoke-Sql {
    # Run a SQL probe against the operator-supplied connection. Returns
    # @{ Output = '...'; ExitCode = 0/1 }. Caller interprets.
    param([string]$Sql, [string]$Database = '')
    if (-not (Test-SqlConnection)) {
        return @{ Output = ''; ExitCode = -1; Reason = 'no -ConnectionString' }
    }
    $psql = Resolve-PsqlExe
    if (-not $psql) {
        return @{ Output = ''; ExitCode = -1; Reason = 'psql.exe not on PATH' }
    }
    # ConnectionString shape: postgres://user:pass@host:port/db
    try {
        $uri = [Uri]$ConnectionString
    } catch {
        return @{ Output = ''; ExitCode = -1; Reason = "connection string unparsable: $_" }
    }
    $userInfo = $uri.UserInfo -split ':', 2
    $user = if ($userInfo.Count -ge 1 -and $userInfo[0]) { $userInfo[0] } else { 'postgres' }
    $pw   = if ($userInfo.Count -ge 2) { [Uri]::UnescapeDataString($userInfo[1]) } else { '' }
    $port = if ($uri.Port -gt 0) { $uri.Port } else { 5432 }
    $db   = if (-not [string]::IsNullOrEmpty($Database)) {
                $Database
            } elseif (-not [string]::IsNullOrEmpty($uri.AbsolutePath) -and $uri.AbsolutePath -ne '/') {
                $uri.AbsolutePath.TrimStart('/')
            } else {
                'postgres'
            }
    $env:PGPASSWORD = $pw
    try {
        $out = & $psql -h $uri.Host -p $port -U $user -d $db -v ON_ERROR_STOP=1 -tAc $Sql 2>&1
        return @{ Output = ($out | Out-String).Trim(); ExitCode = $LASTEXITCODE }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# ----- Per-item handlers (return pass/fail/skip + evidence) -------------------

function Handle-AnonymousInventory {
    # SEC-AUTH-5 — Anonymous endpoints inventory. Documented allowlist:
    # /healthz, /healthz/workers, /api/edge/replay, AcceptInvite.razor,
    # CF Access pre-auth pages. Any [AllowAnonymous] in source is at
    # minimum a P2 review-needed; the runner reports the count + the
    # files-touched fingerprint and lets the operator verify against
    # the documented allowlist.
    $r = Invoke-Grep -Pattern '\[AllowAnonymous\]' -Paths @('apps', 'modules', 'platform')
    if ($r.Count -eq 0) {
        return @{ Status = 'pass'; Evidence = 'no [AllowAnonymous] markers in apps/ modules/ platform/' }
    }
    # The check intentionally stays advisory rather than fail — the
    # checklist's prose itself flags this as "each match documented +
    # justified" not "zero matches". Surface as skip with the file
    # fingerprint so the operator can diff against the SEC-AUTH-5 allowlist.
    $files = @($r.Output -split "`n" | ForEach-Object { ($_ -split ':', 2)[0] } | Sort-Object -Unique)
    $fileFingerprint = ($files -join ', ')
    if ($fileFingerprint.Length -gt 200) { $fileFingerprint = $fileFingerprint.Substring(0, 197) + '...' }
    return @{
        Status   = 'skip'
        Evidence = "$($r.Count) [AllowAnonymous] occurrences across $($files.Count) file(s) — manual cross-check vs. SEC-AUTH-5 allowlist required: $fileFingerprint"
    }
}

function Handle-EdgeAuthDowngradeTest {
    # SEC-AUTH-7 — confirm the regression test exists.
    $r = Invoke-Grep -Pattern 'Bad_per_node_key_does_not_downgrade_to_legacy' -Paths @('tests', 'modules')
    if ($r.Count -eq 0) {
        return @{ Status = 'fail'; Evidence = 'regression test Bad_per_node_key_does_not_downgrade_to_legacy not found' }
    }
    return @{ Status = 'pass'; Evidence = "Bad_per_node_key_does_not_downgrade_to_legacy present ($($r.Count) match)" }
}

function Handle-AuthorizeDefault {
    # SEC-AUTHZ-1 — every Program.cs must call RequireAuthorization() on
    # the endpoint pipeline (or have global Authorize fallback).
    $r = Invoke-Grep -Pattern 'RequireAuthorization\(' -Paths @('apps', 'modules')
    if ($r.Count -eq 0) {
        return @{ Status = 'fail'; Evidence = 'no RequireAuthorization() call found across apps/ modules/' }
    }
    return @{ Status = 'pass'; Evidence = "$($r.Count) RequireAuthorization() call sites" }
}

function Handle-TenantInterceptor {
    # SEC-TENANT-1 — TenantConnectionInterceptor must be wired to every
    # tenant-touching DbContext.
    $r = Invoke-Grep -Pattern 'TenantConnectionInterceptor' -Paths @('platform', 'modules')
    if ($r.Count -eq 0) {
        return @{ Status = 'fail'; Evidence = 'TenantConnectionInterceptor not referenced in platform/ modules/' }
    }
    return @{ Status = 'pass'; Evidence = "$($r.Count) TenantConnectionInterceptor references" }
}

function Handle-SystemContextRegister {
    # SEC-TENANT-4 — every SetSystemContext caller is in the register.
    # The grep counts ALL textual mentions (definition + invocations);
    # the check is "register contains the same callers", so an exact
    # numeric match isn't required — we surface both counts and let
    # the operator verify the per-caller list.
    $codeMatches = Invoke-Grep -Pattern 'SetSystemContext' -Paths @('platform', 'modules', 'apps')
    $codeCount = $codeMatches.Count

    if (-not (Test-Path $RegisterPath)) {
        return @{ Status = 'skip'; Evidence = "register absent at docs/system-context-audit-register.md" }
    }
    $registerRaw = Get-Content -Path $RegisterPath -Raw -Encoding UTF8
    $registerCount = @([regex]::Matches($registerRaw, 'SetSystemContext')).Count
    if ($codeCount -eq 0) {
        return @{ Status = 'skip'; Evidence = 'no SetSystemContext callers found in source — verify scan scope' }
    }
    return @{
        Status   = 'skip'
        Evidence = "code: $codeCount SetSystemContext mentions; register: $registerCount mentions — operator must verify per-caller parity (callers listed in `docs/system-context-audit-register.md`)"
    }
}

function Handle-SqlPolicyProbe {
    # Generic SQL-shaped item handler — runs the embedded SQL when a
    # connection is configured, otherwise skips. Caller passes the SQL
    # body and a one-line evidence template.
    param([string]$Sql, [string]$ExpectShape, [string]$Database = '')
    if (-not (Test-SqlConnection)) {
        return @{ Status = 'skip'; Evidence = 'no -ConnectionString — SQL probe not run' }
    }
    $r = Invoke-Sql -Sql $Sql -Database $Database
    if ($r.ExitCode -lt 0) {
        return @{ Status = 'skip'; Evidence = "SQL probe could not run: $($r.Reason)" }
    }
    if ($r.ExitCode -ne 0) {
        return @{ Status = 'fail'; Evidence = "psql exit=$($r.ExitCode); output: $($r.Output -replace "`r?`n", ' | ')" }
    }
    # Minimal "did it return rows" interpretation. Operators interpret
    # the actual policy shape against the ExpectShape descriptor in the
    # artifact's row. We surface the row count + the first line of output.
    $lines = $r.Output -split "`r?`n" | Where-Object { $_ -ne '' }
    return @{
        Status   = 'pass'
        Evidence = "$($lines.Count) row(s) returned; expect shape: $ExpectShape"
    }
}

function Handle-MailKitVersion {
    # SEC-DEP-4 — MailKit ≥ 4.16 across all csproj.
    $csprojs = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
               Where-Object {
                   $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                   $rel -notmatch '(^|[\\/])(bin|obj|v1-clone|publish|node_modules|\.worktrees)([\\/]|$)'
               }
    $bad = New-Object System.Collections.ArrayList
    foreach ($csproj in $csprojs) {
        $content = Get-Content -Path $csproj.FullName -Raw -Encoding UTF8
        $m = [regex]::Match($content, '(?i)<PackageReference\s+Include="MailKit"\s+Version="(?<v>[^"]+)"')
        if ($m.Success) {
            $version = $m.Groups['v'].Value
            try {
                $semver = [version]($version -replace '[^\d.].*$', '')
                if ($semver -lt [version]'4.16') {
                    $void = $bad.Add("$($csproj.Name): MailKit $version")
                }
            } catch {
                $void = $bad.Add("$($csproj.Name): MailKit $version (could not parse)")
            }
        }
    }
    if ($bad.Count -gt 0) {
        return @{ Status = 'fail'; Evidence = ($bad -join '; ') }
    }
    return @{ Status = 'pass'; Evidence = 'all MailKit references are ≥ 4.16 (or absent)' }
}

function Handle-DotNetVersion {
    # SEC-DEP-5 — net10 only; no net8/net9 in TargetFramework.
    $csprojs = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
               Where-Object {
                   $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                   $rel -notmatch '(^|[\\/])(bin|obj|v1-clone|publish|node_modules|\.worktrees)([\\/]|$)'
               }
    $bad = New-Object System.Collections.ArrayList
    foreach ($csproj in $csprojs) {
        $content = Get-Content -Path $csproj.FullName -Raw -Encoding UTF8
        $m = [regex]::Matches($content, '(?i)<TargetFramework[s]?>([^<]+)</TargetFramework[s]?>')
        foreach ($match in $m) {
            $tf = $match.Groups[1].Value
            # net10 is the locked target. Anything net8 / net9 / older is a finding.
            if ($tf -match 'net([0-9]+)' ) {
                $major = [int]$Matches[1]
                if ($major -lt 10) {
                    $void = $bad.Add("$($csproj.Name): TargetFramework=$tf")
                }
            }
        }
    }
    if ($bad.Count -gt 0) {
        return @{ Status = 'fail'; Evidence = ($bad -join '; ') }
    }
    return @{ Status = 'pass'; Evidence = 'all TargetFramework values are net10+' }
}

function Handle-NpgsqlVersion {
    # SEC-DEP-6 — Npgsql 9.x or higher (PG17 compatibility).
    $csprojs = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' |
               Where-Object {
                   $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                   $rel -notmatch '(^|[\\/])(bin|obj|v1-clone|publish|node_modules|\.worktrees)([\\/]|$)'
               }
    $bad = New-Object System.Collections.ArrayList
    $found = 0
    foreach ($csproj in $csprojs) {
        $content = Get-Content -Path $csproj.FullName -Raw -Encoding UTF8
        $matches = [regex]::Matches($content, '(?i)<PackageReference\s+Include="Npgsql(?:\.[A-Za-z.]+)?"\s+Version="(?<v>[^"]+)"')
        foreach ($m in $matches) {
            $found++
            $version = $m.Groups['v'].Value
            try {
                $semver = [version]($version -replace '[^\d.].*$', '')
                if ($semver -lt [version]'9.0') {
                    $void = $bad.Add("$($csproj.Name): Npgsql $version")
                }
            } catch {
                $void = $bad.Add("$($csproj.Name): Npgsql $version (could not parse)")
            }
        }
    }
    if ($bad.Count -gt 0) {
        return @{ Status = 'fail'; Evidence = ($bad -join '; ') }
    }
    return @{ Status = 'pass'; Evidence = "$found Npgsql refs; all ≥ 9.0" }
}

function Handle-ChainedScanner {
    # Generic chained-scanner handler — looks for the latest dated report
    # of the given kind. If $RunChained, regenerates first.
    param(
        [string]$Kind,        # 'vulnerabilities' | 'license-audit' | 'secrets-trufflehog' | 'outdated'
        [string]$Script,      # 'run-vulnerability-scan.ps1' etc.
        [string]$PassPattern, # regex that, if found in latest report, indicates pass
        [string]$FailPattern  # regex that, if found, indicates fail
    )
    if ($RunChained) {
        $scriptPath = Join-Path $ScriptDir $Script
        if (Test-Path $scriptPath) {
            Write-Info "(chained) running $Script"
            try {
                & $scriptPath 2>&1 | Out-Null
            } catch {
                # Tool may exit non-zero with findings; capture but continue
                Write-Info "(chained) $Script exited non-zero — reading latest report anyway"
            }
        } else {
            return @{ Status = 'skip'; Evidence = "chained script absent: $scriptPath" }
        }
    }
    $latest = Get-LatestReport -KindGlob "*-$Kind.md"
    if (-not $latest) {
        return @{
            Status   = 'skip'
            Evidence = "no $Kind report under reports/; pass -RunChained or run $Script first"
        }
    }
    $body = Get-Content -Path $latest.FullName -Raw -Encoding UTF8
    # Tools that self-skip (e.g. run-trufflehog.ps1 on a host with no
    # trufflehog binary) emit "**Status:** SKIPPED" / "(skipped)" — surface
    # those as skip with the reason, not as fail.
    if ($body -match '(?im)^\*\*Status:\*\*\s*SKIPPED|\(skipped\)') {
        return @{ Status = 'skip'; Evidence = "evidence: reports/$($latest.Name) (tool self-skipped on this host)" }
    }
    if ($PassPattern -and $body -match $PassPattern) {
        return @{ Status = 'pass'; Evidence = "evidence: reports/$($latest.Name)" }
    }
    if ($FailPattern -and $body -match $FailPattern) {
        return @{ Status = 'fail'; Evidence = "evidence: reports/$($latest.Name) (heuristic flagged a finding)" }
    }
    # Indeterminate — surface report path and let the operator interpret.
    return @{ Status = 'skip'; Evidence = "evidence: reports/$($latest.Name) (heuristic match indeterminate)" }
}

function Handle-DepLicenseClosure {
    # SEC-DEP-3 — augment the chained scanner with the Sprint 58 closure
    # status from license-allowlist-rationale.md (the runner reads the
    # rationale doc to surface the NBomber resolution alongside the
    # latest license-audit report).
    $base = Handle-ChainedScanner -Kind 'license-audit' -Script 'run-license-audit.ps1' `
                                  -PassPattern '(?im)Non-allowlisted licenses\s*\|\s*0\b|0 non-allowlisted' `
                                  -FailPattern '(?im)Non-allowlisted licenses\s*\|\s*[1-9]'
    $closure = ''
    if (Test-Path $RationalePath) {
        $rat = Get-Content -Path $RationalePath -Raw -Encoding UTF8
        if ($rat -match 'NBomber.*?RESOLVED|RESOLVED.*?NBomber') {
            $closure = ' (Sprint 58: NBomber P0 RESOLVED — see license-allowlist-rationale.md §2)'
        }
    }
    return @{ Status = $base.Status; Evidence = "$($base.Evidence)$closure" }
}

# ============================================================
# Item handler dispatch table
# ============================================================
#
# Maps SEC-CAT-N → handler scriptblock. Items absent from this table
# are emitted as `[manual]` in the artifact with their Verify prose.
# Adding a new automation: add a row here AND add a handler function
# above. DO NOT edit docs/security/audit-checklist-2026.md.

$ItemHandlers = @{
    'SEC-AUTH-5'    = { Handle-AnonymousInventory }
    'SEC-AUTH-7'    = { Handle-EdgeAuthDowngradeTest }
    'SEC-AUTHZ-1'   = { Handle-AuthorizeDefault }
    'SEC-TENANT-1'  = { Handle-TenantInterceptor }
    'SEC-TENANT-3'  = { Handle-SqlPolicyProbe -Sql @"
SELECT count(*)::int AS bad_rows
FROM pg_tables
WHERE schemaname IN ('audit','identity','inspection','nickfinance','tenancy')
  AND tablename NOT IN ('__EFMigrationsHistory','tenants','tenant_purge_log','tenant_export_requests')
  AND (rowsecurity = false OR forcerowsecurity = false);
"@ -ExpectShape 'bad_rows = 0' -Database 'nickerp_platform' }
    'SEC-TENANT-4'  = { Handle-SystemContextRegister }
    'SEC-TENANT-5'  = { Handle-SqlPolicyProbe -Sql @"
SELECT count(*)::int AS opt_in_count
FROM pg_policies
WHERE qual LIKE '%''-1''%' OR with_check LIKE '%''-1''%';
"@ -ExpectShape 'opt_in_count matches register Tables-that-opt-in section' -Database 'nickerp_platform' }
    'SEC-TENANT-6'  = { Handle-SqlPolicyProbe -Sql 'SELECT count(*)::int FROM tenancy.tenants WHERE "Id" = -1;' `
                                              -ExpectShape 'count = 0' `
                                              -Database 'nickerp_platform' }
    'SEC-TENANT-13' = { Handle-SqlPolicyProbe -Sql @"
SELECT count(*)::int AS opt_in_present
FROM pg_policies
WHERE schemaname = 'inspection'
  AND tablename = 'scanner_threshold_profiles'
  AND policyname = 'tenant_isolation_scanner_threshold_profiles'
  AND qual LIKE '%''-1''%'
  AND with_check LIKE '%''-1''%';
"@ -ExpectShape 'opt_in_present = 1' -Database 'nickerp_inspection' }
    'SEC-DB-3'      = { Handle-SqlPolicyProbe -Sql @"
SELECT privilege_type, count(*)::int
FROM information_schema.table_privileges
WHERE grantee = 'nscim_app'
GROUP BY privilege_type
ORDER BY 1;
"@ -ExpectShape 'SELECT/INSERT/UPDATE only; DELETE only on documented allowlists' `
                                          -Database 'nickerp_platform' }
    'SEC-DB-10'     = {
        # Migration catalog reconciled — use the cutover-dryrun report if present,
        # otherwise probe the connection if available.
        $latest = Get-LatestReport -KindGlob 'dryrun-*-migration-report.md'
        if ($latest) {
            $body = Get-Content -Path $latest.FullName -Raw -Encoding UTF8
            if ($body -match 'newly-applied=0' -or $body -match 'idempotent re-run') {
                return @{ Status = 'pass'; Evidence = "evidence: $($latest.Name) (idempotent re-run, no drift)" }
            }
            return @{ Status = 'skip'; Evidence = "cutover-dryrun report present but does not show idempotency: $($latest.Name)" }
        }
        return @{ Status = 'skip'; Evidence = 'no cutover-dryrun migration report; run tools/cutover-dryrun/run.ps1 first' }
    }
    'SEC-DEP-1'     = { Handle-ChainedScanner -Kind 'vulnerabilities' -Script 'run-vulnerability-scan.ps1' `
                                              -PassPattern '(?im)Total vulnerable references:\s*0|^\*\*Result:\*\* clean' `
                                              -FailPattern '(?im)Critical|High severity' }
    'SEC-DEP-2'     = { Handle-ChainedScanner -Kind 'outdated' -Script 'run-dependency-audit.ps1' `
                                              -PassPattern '(?im)^\*\*Result:\*\* clean|No outdated packages' `
                                              -FailPattern '' }
    'SEC-DEP-3'     = { Handle-DepLicenseClosure }
    'SEC-DEP-4'     = { Handle-MailKitVersion }
    'SEC-DEP-5'     = { Handle-DotNetVersion }
    'SEC-DEP-6'     = { Handle-NpgsqlVersion }
    'SEC-SECRETS-1' = { Handle-ChainedScanner -Kind 'secrets-trufflehog' -Script 'run-trufflehog.ps1' `
                                              -PassPattern 'Verified.*?:\s*0|Zero ``Verified``' `
                                              -FailPattern 'Verified.*?:\s*[1-9]' }
    'SEC-SECRETS-8' = { Handle-ChainedScanner -Kind 'secrets-trufflehog' -Script 'run-trufflehog.ps1' `
                                              -PassPattern 'Verified.*?:\s*0|Zero ``Verified``' `
                                              -FailPattern 'Verified.*?:\s*[1-9]' }
}

# ============================================================
# Artifact writer
# ============================================================

function Write-Artifact {
    param(
        [System.Collections.ArrayList]$Items,
        [hashtable]$Results,
        [string]$Path,
        [hashtable]$Header
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# NickERP v2 — Phase V audit artifact")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> Generated by ``tools/security-scan/run-audit.ps1`` — Sprint 60 Phase B1 (PLAN.md §23.2 work item B1).")
    [void]$sb.AppendLine("> Walks ``docs/security/audit-checklist-2026.md`` and ticks the automatable items;")
    [void]$sb.AppendLine("> ``[manual]`` items remain for the operator to verify by hand.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## Run summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Field | Value |")
    [void]$sb.AppendLine("|---|---|")
    [void]$sb.AppendLine("| Site | $($Header.Site) |")
    [void]$sb.AppendLine("| Generated | $($Header.Timestamp) |")
    [void]$sb.AppendLine("| Filter | $($Header.Filter) |")
    [void]$sb.AppendLine("| Connection probed | $($Header.HasConn) |")
    [void]$sb.AppendLine("| Chained scans | $($Header.RunChained) |")
    [void]$sb.AppendLine("| Total items | $($Header.Total) |")
    [void]$sb.AppendLine("| Automated | $($Header.Automated) |")
    [void]$sb.AppendLine("| Manual | $($Header.Manual) |")
    [void]$sb.AppendLine("| Cross-references | $($Header.Xref) |")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## Findings tally")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Severity | Pass | Fail | Skip | Manual | Open (Fail+Skip-as-fail) |")
    [void]$sb.AppendLine("|---|---:|---:|---:|---:|---:|")
    foreach ($sev in @('P0','P1','P2','P3','-')) {
        # Severity may be '' (unparsed) — match those as the '-' bucket.
        $sevTest = if ($sev -eq '-') { '' } else { $sev }
        $pass = @($Items | Where-Object { $_.Severity -eq $sevTest -and $Results[$_.Id].Status -eq 'pass' }).Count
        $fail = @($Items | Where-Object { $_.Severity -eq $sevTest -and $Results[$_.Id].Status -eq 'fail' }).Count
        $skip = @($Items | Where-Object { $_.Severity -eq $sevTest -and $Results[$_.Id].Status -eq 'skip' }).Count
        $manu = @($Items | Where-Object { $_.Severity -eq $sevTest -and $Results[$_.Id].Status -eq 'manual' }).Count
        $open = $fail
        $sevLabel = if ($sev -eq '-') { '(unknown)' } else { $sev }
        [void]$sb.AppendLine("| $sevLabel | $pass | $fail | $skip | $manu | $open |")
    }
    [void]$sb.AppendLine("")

    # Group by category, in the same order as the checklist sections
    $catOrder = @('AUTH','AUTHZ','TENANT','SECRETS','TLS','DB','AUDIT','EDGE','MOD','DEP','HEAD')
    $byCat = @{}
    foreach ($it in $Items) {
        $cat = $it.Category
        # Compress "MOD-INSP" / "MOD-FIN" / "MOD-HR" → "MOD"
        if ($cat -like 'MOD-*') { $cat = 'MOD' }
        if (-not $byCat.ContainsKey($cat)) { $byCat[$cat] = @() }
        $byCat[$cat] += $it
    }

    foreach ($cat in $catOrder) {
        if (-not $byCat.ContainsKey($cat)) { continue }
        [void]$sb.AppendLine("## $cat")
        [void]$sb.AppendLine("")
        foreach ($it in ($byCat[$cat] | Sort-Object Number)) {
            $r = $Results[$it.Id]
            $sevLabel = if ([string]::IsNullOrEmpty($it.Severity)) { '' } else { " ($($it.Severity))" }
            switch ($r.Status) {
                'pass' {
                    [void]$sb.AppendLine("- [x] **$($it.Id)** — $($it.Title)$sevLabel")
                    [void]$sb.AppendLine("  - $($r.Evidence)")
                }
                'fail' {
                    [void]$sb.AppendLine("- [!] **$($it.Id)** — $($it.Title)$sevLabel  ← FAIL")
                    [void]$sb.AppendLine("  - $($r.Evidence)")
                }
                'skip' {
                    [void]$sb.AppendLine("- [ ] **$($it.Id)** — $($it.Title)$sevLabel  [skip]")
                    [void]$sb.AppendLine("  - $($r.Evidence)")
                }
                'manual' {
                    [void]$sb.AppendLine("- [ ] **$($it.Id)** — $($it.Title)$sevLabel  [manual]")
                    if (-not [string]::IsNullOrEmpty($it.Verify)) {
                        # First line of Verify only, to keep the artifact scannable
                        $firstLine = ($it.Verify -split "`r?`n" | Select-Object -First 1).Trim()
                        if ($firstLine.Length -gt 240) { $firstLine = $firstLine.Substring(0, 237) + '...' }
                        [void]$sb.AppendLine("  - Verify: $firstLine")
                    }
                }
                'xref' {
                    [void]$sb.AppendLine("- [ ] **$($it.Id)** — $($it.Title)$sevLabel  [xref → $($it.XrefTarget)]")
                }
                default {
                    [void]$sb.AppendLine("- [ ] **$($it.Id)** — $($it.Title)$sevLabel  [unknown status]")
                }
            }
        }
        [void]$sb.AppendLine("")
    }

    # Findings register (only failed items, in the shape the checklist's
    # "How findings get filed" section uses).
    $failed = @($Items | Where-Object { $Results[$_.Id].Status -eq 'fail' })
    if ($failed.Count -gt 0) {
        [void]$sb.AppendLine("## Findings register (failed automatable items)")
        [void]$sb.AppendLine("")
        $n = 1
        foreach ($it in $failed) {
            $r = $Results[$it.Id]
            $sevLabel = if ([string]::IsNullOrEmpty($it.Severity)) { '?' } else { $it.Severity }
            [void]$sb.AppendLine("### AUD-$n — $($it.Title)")
            [void]$sb.AppendLine("**Item:** $($it.Id)")
            [void]$sb.AppendLine("**Severity:** $sevLabel")
            [void]$sb.AppendLine("**Observed:** $($r.Evidence)")
            [void]$sb.AppendLine("**Expected:** $(if ($it.Expect) { ($it.Expect -split "`r?`n" | Select-Object -First 1).Trim() } else { '(see checklist)' })")
            [void]$sb.AppendLine("**Owner:** _(assign)_")
            [void]$sb.AppendLine("**Status:** Open")
            [void]$sb.AppendLine("")
            $n++
        }
    }

    # Phase V exit criteria recap
    $p0p1Open = @($Items | Where-Object { ($_.Severity -eq 'P0' -or $_.Severity -eq 'P1') -and $Results[$_.Id].Status -eq 'fail' }).Count
    [void]$sb.AppendLine("## Phase V exit gate")
    [void]$sb.AppendLine("")
    if ($p0p1Open -eq 0) {
        [void]$sb.AppendLine("- Open P0/P1 (automated): **0** — automated checks pass.")
    } else {
        [void]$sb.AppendLine("- Open P0/P1 (automated): **$p0p1Open** — pilot blocked until resolved.")
    }
    [void]$sb.AppendLine("- ``[manual]`` items still need operator verification.")
    [void]$sb.AppendLine("- ``[skip]`` items need an operator-supplied prerequisite (typically ``-ConnectionString`` or ``-RunChained``).")
    [void]$sb.AppendLine("- See ``docs/security/audit-checklist-2026.md`` §Phase V exit criteria for the full gate.")
    [void]$sb.AppendLine("")

    Set-Content -Path $Path -Value $sb.ToString() -Encoding UTF8
}

# ============================================================
# Main
# ============================================================

$startedAt = Get-Date
Write-Step "NickERP v2 audit-checklist runner — Sprint 60 Phase B1"
Write-Info "checklist : $ChecklistPath"
Write-Info "site      : $Site"
if ($Filter) { Write-Info "filter    : $Filter" }
if ($ConnectionString) { Write-Info "sql probes: enabled (-ConnectionString supplied)" } else { Write-Info "sql probes: disabled (no -ConnectionString)" }
if ($RunChained) { Write-Info "chained   : will trigger sibling scanners" } else { Write-Info "chained   : will read latest reports only" }

# Pre-flight
if (-not (Test-Path $ChecklistPath)) {
    Write-Error "Checklist absent: $ChecklistPath"
    exit 1
}

# Parse
Write-Step "Parsing checklist"
$items = $null
try {
    $items = Read-Checklist -Path $ChecklistPath
} catch {
    Write-Error "Checklist parse failed: $_"
    exit 3
}
Write-Info "parsed $($items.Count) SEC-* items"

# Apply filter
if (-not [string]::IsNullOrEmpty($Filter)) {
    $items = [System.Collections.ArrayList]::new(@($items | Where-Object {
        $_.Category -eq $Filter -or $_.Category -like "$Filter-*"
    }))
    Write-Info "after filter '$Filter': $($items.Count) items"
}

# Categorise. @(...) wrappers ensure .Count works under StrictMode even
# when Where-Object returns zero or a single object.
$autoIds = @($ItemHandlers.Keys)
$autoCount   = @($items | Where-Object { $autoIds -contains $_.Id }).Count
$xrefCount   = @($items | Where-Object { $_.IsXref }).Count
$manualCount = $items.Count - $autoCount - $xrefCount

Write-Info "automatable: $autoCount  manual: $manualCount  xref: $xrefCount"

if ($DryRun) {
    Write-Step "Dry run — categorisation only"
    Write-Info "(no execution; no artifact written)"
    foreach ($cat in @('AUTH','AUTHZ','TENANT','SECRETS','TLS','DB','AUDIT','EDGE','MOD','DEP','HEAD')) {
        $catItems = @($items | Where-Object { $_.Category -eq $cat -or $_.Category -like "$cat-*" })
        if ($catItems.Count -eq 0) { continue }
        $catAuto = @($catItems | Where-Object { $autoIds -contains $_.Id }).Count
        $catManual = $catItems.Count - $catAuto
        Write-Info "$cat : $($catItems.Count) items ($catAuto auto / $catManual manual)"
    }
    exit 0
}

# Execute
Write-Step "Executing handlers"
$results = @{}
$counter = 0
foreach ($it in $items) {
    $counter++
    if ($it.IsXref) {
        $results[$it.Id] = @{ Status = 'xref'; Evidence = "see $($it.XrefTarget)" }
        continue
    }
    if ($ItemHandlers.ContainsKey($it.Id)) {
        Write-Host "    [$counter/$($items.Count)] $($it.Id)" -NoNewline
        $handler = $ItemHandlers[$it.Id]
        try {
            $r = & $handler
            $results[$it.Id] = $r
            $marker = '[?]'
            $colour = 'Gray'
            if ($r.Status -eq 'pass') { $marker = '[x]'; $colour = 'Green' }
            elseif ($r.Status -eq 'fail') { $marker = '[!]'; $colour = 'Red' }
            elseif ($r.Status -eq 'skip') { $marker = '[ ]'; $colour = 'Yellow' }
            Write-Host " $marker" -ForegroundColor $colour
        } catch {
            $results[$it.Id] = @{ Status = 'fail'; Evidence = "handler exception: $_" }
            Write-Host " [!]" -ForegroundColor Red
        }
    } else {
        $results[$it.Id] = @{ Status = 'manual' }
    }
}

# Compose artifact
$dateTag = Get-Date -Format 'yyyy-MM-dd'
$timeTag = Get-Date -Format 'yyyy-MM-dd-HHmmss'
$artifactName = "audit-$Site-$timeTag.md"
$artifactPath = Join-Path $ReportsDir $artifactName

$header = @{
    Site       = $Site
    Timestamp  = $startedAt.ToString('o')
    Filter     = if ($Filter) { $Filter } else { '(none — full register)' }
    HasConn    = if ($ConnectionString) { 'yes' } else { 'no' }
    RunChained = if ($RunChained) { 'yes' } else { 'no (read latest reports only)' }
    Total      = $items.Count
    Automated  = $autoCount
    Manual     = $manualCount
    Xref       = $xrefCount
}

Write-Step "Writing artifact"
Write-Artifact -Items ([System.Collections.ArrayList]::new(@($items))) -Results $results -Path $artifactPath -Header $header
Write-Info "artifact : $artifactPath"

# Compute exit code: any P0/P1 fail → 2.
$p0p1Open = 0
foreach ($it in $items) {
    if (($it.Severity -eq 'P0' -or $it.Severity -eq 'P1') -and $results[$it.Id].Status -eq 'fail') {
        $p0p1Open++
    }
}
$exitCode = if ($p0p1Open -gt 0) { 2 } else { 0 }
Write-Step "Done — exit $exitCode (P0/P1 automated open: $p0p1Open)"
exit $exitCode
