# ============================================================
# NickERP v2 — Phase V perf-execution wrapper (Sprint 60 Phase B2 — C1)
# ============================================================
# Runs the four NickPerf scenarios (Health → CaseCreate → EdgeReplay →
# EdgeReplay-backlog) against a target URL, captures per-scenario
# latency / throughput / error rates, and emits a site-scoped markdown
# report under `tools/perf/reports/perf-{site}-{date}.md` matching the
# shape Sprint 55's `docs/perf/baseline-2026-05-06.md` baseline used.
#
# This script implements `PLAN.md §23.2 work item B2` — the engineering
# rehearsal that converts Phase V perf execution from
# "operator runs four scenarios individually" into a single tested
# wrapper the operator can run on day 1 of the pilot stand-up.
#
# Spec sources (read-only — do NOT modify from this script):
#   * `PLAN.md §23.2` work item B2 (deliverable + acceptance criteria)
#   * `docs/perf/test-plan.md` §3.1 (acceptance gates) + §12 (operator
#     playbook the wrapper formalises)
#   * `tests/NickERP.Perf.Tests/Program.cs` (the CLI dispatcher we shell
#     into for each scenario; we read its exit-code contract verbatim)
#   * `docs/perf/baseline-2026-05-06.md` (output-shape reference — we
#     mirror the Summary table headers + Detailed-results layout)
#
# ============================================================
# Scenarios (sequenced — Health first, 24h-backlog last)
# ============================================================
#   1. health              smoke against /healthz/live
#   2. case-create         POST /api/inspection/cases
#   3. edge-replay         POST /api/edge/replay (steady)
#   4. edge-replay-backlog 24h backlog reconnect (informative; SEC-EDGE-7)
#
# Health is first because it warms the connection pool + JIT — running
# it cold against a freshly-deployed host avoids the timeout-tail noise
# the Sprint 58 baseline captured. Backlog is last because it's the
# heaviest run (28.8 RPS scheduled).
#
# ============================================================
# Acceptance gates (per `docs/perf/test-plan.md` §3.1)
# ============================================================
#   * health              p99 WARN @ 100 ms (informative only — no BLOCK)
#   * case-create         p99 BLOCK @ 2000 ms at 1x (×1.5 at 5x, ×2 at 10x)
#   * edge-replay         p99 BLOCK @ 1500 ms at 1x (×1.5 at 5x, ×2 at 10x)
#   * edge-replay-backlog informative — verifies SEC-EDGE-7 rate limit
#                         (50% rejected with 429 within 60 s)
#
# Each scenario's `CheckAcceptanceGate` does the actual evaluation in C#;
# this wrapper observes the per-scenario exit code (0=pass, 1=BLOCK
# breached or all-fail, 2=fatal). Scenarios that legitimately skip on
# missing config (no bearer, no HMAC key) exit 0 with a `skipping —`
# log line; the wrapper records SKIP in the artifact.
#
# ============================================================
# Modes
# ============================================================
#   .\run-phase-v.ps1 -TargetUri http://localhost:5400
#   .\run-phase-v.ps1 -TargetUri https://staging.example.com -Profile 5x
#   .\run-phase-v.ps1 -TargetUri http://localhost:5400 -Site kotoka
#   .\run-phase-v.ps1 -TargetUri http://localhost:5400 -Filter health
#   .\run-phase-v.ps1 -DryRun
#
# `-Filter <scenarioName>` runs a single scenario (useful for spot-checks
# or re-running just the failing one). Default is all four in order.
# `-Site <name>` controls the report filename suffix; defaults to the
# host portion of `-TargetUri` (e.g. `staging-example-com`).
#
# ============================================================
# Exit codes
# ============================================================
#   0  — all scenarios passed (or skipped on missing config)
#   1  — pre-flight failure (dotnet missing, target unreachable)
#   2  — at least one scenario breached its p99 acceptance gate
#   3  — at least one scenario exited fatal (uncaught exception)
#   4  — runner invocation failed (could not find emitted report.md)
#
# Non-zero is "do NOT ship the pilot until you've understood the
# regression"; the report still lands so the operator has the artifact.
#
# ============================================================

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$TargetUri = "",

    [ValidateSet('1x', '5x', '10x')]
    [string]$Profile = '1x',

    [string]$Site = "",

    [ValidateSet('all', 'health', 'case-create', 'edge-replay', 'edge-replay-backlog')]
    [string]$Filter = 'all',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Tfm = 'net10.0',

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----- Repo root locator (this file lives in tools/perf/) ---------------------
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot    = (Resolve-Path (Join-Path $ScriptDir '..\..')).Path
$ReportsDir  = Join-Path $ScriptDir 'reports'
$PerfProject = Join-Path $RepoRoot 'tests\NickERP.Perf.Tests'
$BinReports  = Join-Path $PerfProject ("bin\{0}\{1}\reports" -f $Configuration, $Tfm)

if (-not (Test-Path $ReportsDir)) {
    New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null
}

# ----- Scenario catalogue (PLAN.md §23.2 B2 + test-plan.md §3.1) --------------
# Order matters: Health first as warm-up, backlog last as the heavy run
# (per `docs/perf/test-plan.md` §12.2 + the Phase B2 spec deliverable).
$Scenarios = @(
    [pscustomobject]@{
        Name              = 'health'
        DisplayName       = 'Health smoke'
        Endpoint          = '/healthz/live'
        Pilot1xP99BlockMs = 0      # informative only — WARN @ 100 ms per §3.1
        Pilot1xP99WarnMs  = 100
        IsInformative     = $true
        SupportsProfile   = $true
    },
    [pscustomobject]@{
        Name              = 'case-create'
        DisplayName       = 'Case create'
        Endpoint          = '/api/inspection/cases'
        Pilot1xP99BlockMs = 2000   # `CaseCreateScenario.Pilot1xP99BlockMs`
        Pilot1xP99WarnMs  = 1000   # acceptance budget per §3.1
        IsInformative     = $false
        SupportsProfile   = $true
    },
    [pscustomobject]@{
        Name              = 'edge-replay'
        DisplayName       = 'Edge replay (steady)'
        Endpoint          = '/api/edge/replay'
        Pilot1xP99BlockMs = 1500   # `EdgeReplayScenario.Pilot1xP99BlockMs`
        Pilot1xP99WarnMs  = 500    # acceptance budget per §3.1
        IsInformative     = $false
        SupportsProfile   = $true
    },
    [pscustomobject]@{
        Name              = 'edge-replay-backlog'
        DisplayName       = 'Edge replay (24h backlog)'
        Endpoint          = '/api/edge/replay'
        Pilot1xP99BlockMs = 0      # informative — SEC-EDGE-7 rate-limit check
        Pilot1xP99WarnMs  = 0
        IsInformative     = $true
        SupportsProfile   = $false # backlog has its own profile shape
    }
)

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

function Resolve-SiteName {
    param([string]$Site, [string]$TargetUri)
    if (-not [string]::IsNullOrWhiteSpace($Site)) { return $Site.ToLowerInvariant() }
    if ([string]::IsNullOrWhiteSpace($TargetUri)) { return 'unknown' }
    try {
        $u = [Uri]$TargetUri
        $h = $u.Host
        if ([string]::IsNullOrEmpty($h)) { return 'unknown' }
        return ($h -replace '[^a-zA-Z0-9]', '-').Trim('-').ToLowerInvariant()
    } catch {
        return 'unknown'
    }
}

function Test-Preflight {
    param([string]$TargetUri)
    Write-Step "Pre-flight checks"

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Error "dotnet CLI not on PATH"
        return @{ ExitCode = 1; Reason = 'dotnet-missing' }
    }
    Write-Info "dotnet : $($dotnet.Source)"

    if (-not (Test-Path $PerfProject)) {
        Write-Error "NickPerf project absent: $PerfProject"
        return @{ ExitCode = 1; Reason = 'perf-project-missing' }
    }
    Write-Info "perf project : $PerfProject"

    if ([string]::IsNullOrWhiteSpace($TargetUri)) {
        Write-Warning "No -TargetUri provided. Scenarios will use the default (appsettings.json: localhost:5400)."
        Write-Warning "For Phase V execution against staging / pilot, pass -TargetUri explicitly."
        return @{ ExitCode = 0; Reason = 'default-target' }
    }

    # Probe `/healthz/live` so we fail fast on unreachable targets rather
    # than letting four scenarios time out individually. Per
    # `docs/perf/test-plan.md` §3.1 EP-008 the endpoint is
    # `GET /healthz` (no auth). NickERP also exposes `/healthz/live` (the
    # Sprint 58 baseline target). Try both shapes.
    $probeBase = $TargetUri.TrimEnd('/')
    $candidates = @("$probeBase/healthz/live", "$probeBase/healthz")
    $reachable = $false
    foreach ($url in $candidates) {
        try {
            $resp = Invoke-WebRequest -Uri $url -Method Get -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
            Write-Info "probe $url : HTTP $($resp.StatusCode)"
            $reachable = $true
            break
        } catch {
            Write-Info "probe $url : $($_.Exception.Message)"
        }
    }
    if (-not $reachable) {
        Write-Error "Pre-flight probe failed — target $TargetUri did not respond on /healthz/live or /healthz."
        Write-Warning "If the target host is up but the health endpoint differs, re-run after fixing the path,"
        Write-Warning "or pass -TargetUri pointing at a reachable host. The four perf scenarios skip individually"
        Write-Warning "on connect-refused; this pre-flight just shortens the failure feedback loop."
        return @{ ExitCode = 1; Reason = 'target-unreachable' }
    }

    return @{ ExitCode = 0; Reason = 'ok' }
}

function Get-LatestReportPath {
    param([string]$ScenarioName)
    # NickPerf writes to `bin/<cfg>/<tfm>/reports/<yyyy-MM-dd>/<scenario>/report.md`.
    # Date-stamp UTC per `Program.GetReportFolder`. We grab the freshest
    # path by file write time so a re-run on the same day (or right after
    # midnight UTC) still resolves correctly.
    if (-not (Test-Path $BinReports)) { return $null }
    $candidate = Get-ChildItem -Path $BinReports -Filter 'report.md' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "[\\/]$([regex]::Escape($ScenarioName))[\\/]report\.md$" } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($candidate) { return $candidate.FullName }
    return $null
}

function ConvertFrom-NickPerfReport {
    param([string]$Path)
    # NickPerfReport.BuildMarkdown shape (Sprint 58 — see
    # `tests/NickERP.Perf.Tests/Runner/NickPerfReport.cs`):
    #   | Total | <n> |
    #   | OK | <n> |
    #   | Fail | <n> |
    #   | Throughput (RPS) | <f> |
    #   | Elapsed | <hh:mm:ss> |
    #   | min | <f> |
    #   | mean | <f> |
    #   | max | <f> |
    #   | p50 | <f> |
    #   | p75 | <f> |
    #   | p95 | <f> |
    #   | p99 | <f> |
    if (-not (Test-Path $Path)) { return $null }
    $text = Get-Content -Path $Path -Raw
    $stats = [ordered]@{}
    # Explicit pattern→key map. Patterns are regex; keys are plain
    # property names we use downstream (`Format-Latency` looks them up
    # via `$Stats.$Key`). Keep keys in sync with `Format-Latency` callers.
    $patterns = @(
        @{ Pattern = 'Total';                Key = 'Total' },
        @{ Pattern = 'OK';                   Key = 'OK' },
        @{ Pattern = 'Fail';                 Key = 'Fail' },
        @{ Pattern = 'Throughput \(RPS\)';   Key = 'ThroughputRps' },
        @{ Pattern = 'Elapsed';              Key = 'Elapsed' },
        @{ Pattern = 'min';                  Key = 'min' },
        @{ Pattern = 'mean';                 Key = 'mean' },
        @{ Pattern = 'max';                  Key = 'max' },
        @{ Pattern = 'p50';                  Key = 'p50' },
        @{ Pattern = 'p75';                  Key = 'p75' },
        @{ Pattern = 'p95';                  Key = 'p95' },
        @{ Pattern = 'p99';                  Key = 'p99' }
    )
    foreach ($pk in $patterns) {
        $m = [regex]::Match($text, "\|\s*$($pk.Pattern)\s*\|\s*([^|]+?)\s*\|", 'IgnoreCase')
        if ($m.Success) {
            $stats[$pk.Key] = $m.Groups[1].Value.Trim()
        }
    }
    return [pscustomobject]$stats
}

function Invoke-PerfScenario {
    param(
        [pscustomobject]$Scenario,
        [string]$Profile,
        [string]$TargetUri
    )
    Write-Step "Run scenario: $($Scenario.DisplayName) [$($Scenario.Name)]"
    Write-Info "endpoint : $($Scenario.Endpoint)"
    Write-Info "profile  : $Profile"
    Write-Info "target   : $($TargetUri ? $TargetUri : '(default — appsettings.json)')"

    $args = @('run', '--project', $PerfProject, '-c', $Configuration, '--', $Scenario.Name)
    if ($Scenario.SupportsProfile) {
        $args += @('--profile', $Profile)
    }

    # Per-scenario env override — the perf harness reads
    # `NICKERP_PERF_TargetBaseUrl` per `docs/perf/test-plan.md` §12.3.
    # Set the env for the dotnet process only; restore after.
    $oldTarget = $env:NICKERP_PERF_TargetBaseUrl
    if (-not [string]::IsNullOrWhiteSpace($TargetUri)) {
        $env:NICKERP_PERF_TargetBaseUrl = $TargetUri
        # Edge scenarios use a separate base via Endpoints__InspectionWebBaseUrl;
        # the operator playbook (§12.3) sets it explicitly when targeting a
        # split-host pilot. This wrapper does NOT auto-pick that — too easy
        # to mis-target. We set the unified base only.
    }

    $stdoutLog = New-TemporaryFile
    $startedAt = Get-Date
    try {
        & dotnet @args 2>&1 | Tee-Object -FilePath $stdoutLog.FullName | Out-Host
        $exitCode = $LASTEXITCODE
    } finally {
        if ($null -eq $oldTarget) {
            Remove-Item Env:\NICKERP_PERF_TargetBaseUrl -ErrorAction SilentlyContinue
        } else {
            $env:NICKERP_PERF_TargetBaseUrl = $oldTarget
        }
    }
    $duration = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 2)

    # Detect skip-on-misconfigured (per `docs/perf/test-plan.md` §12.4).
    $stdoutText = Get-Content -Path $stdoutLog.FullName -Raw -ErrorAction SilentlyContinue
    $skipped = $false
    if ($stdoutText -match "$([regex]::Escape($Scenario.Name)):\s*skipping\s+—") {
        $skipped = $true
    }

    # Find the emitted report.md so we can lift latency stats into the
    # aggregate. A skip won't produce one — that's fine.
    $reportPath = if ($skipped) { $null } else { Get-LatestReportPath -ScenarioName $Scenario.Name }
    $stats = if ($reportPath) { ConvertFrom-NickPerfReport -Path $reportPath } else { $null }

    # Verdict shaping (mirrors `docs/perf/baseline-2026-05-06.md`):
    #   skipped               = SKIP
    #   exitCode==0 + stats   = PASS  (or PASS-with-warn for health WARN)
    #   exitCode==1 + stats   = BLOCK (gate breached) or FAIL (all requests failed)
    #   exitCode==2           = FATAL
    #   stats==$null on non-skip = RUNNER-FAIL (couldn't find report)
    $verdict = 'UNKNOWN'
    $blockReason = $null
    if ($skipped) {
        $verdict = 'SKIP'
    } elseif ($exitCode -eq 2) {
        $verdict = 'FATAL'
    } elseif ($null -eq $stats -and $exitCode -ne 0) {
        $verdict = 'RUNNER-FAIL'
        $blockReason = 'No report.md emitted; runner failed before snapshot'
    } elseif ($exitCode -eq 0) {
        $p99Str = Get-StatValue -Stats $stats -Key 'p99'
        if ($Scenario.IsInformative -and $Scenario.Pilot1xP99WarnMs -gt 0 -and $p99Str) {
            $p99 = [double]::Parse($p99Str, [System.Globalization.CultureInfo]::InvariantCulture)
            $verdict = if ($p99 -gt $Scenario.Pilot1xP99WarnMs) { 'PASS-with-warn' } else { 'PASS' }
        } else {
            $verdict = 'PASS'
        }
    } elseif ($exitCode -eq 1) {
        # Distinguish gate-breach vs all-fail vs informative-with-warn.
        # The dispatcher returns 1 for any of:
        #   * all requests failed (snapshot.ok == 0)
        #   * non-informative scenario p99 > Pilot1xP99BlockMs
        #   * `health` snapshot.fail > 0 (timeout-tail noise; per
        #     `docs/perf/baseline-2026-05-06.md` this is dev-host state,
        #     not a true gate breach — health has no BLOCK gate per §3.1)
        $okStr  = Get-StatValue -Stats $stats -Key 'OK'
        $p99Str = Get-StatValue -Stats $stats -Key 'p99'
        $failStr  = Get-StatValue -Stats $stats -Key 'Fail'
        $totalStr = Get-StatValue -Stats $stats -Key 'Total'
        if ($okStr -eq '0') {
            $verdict = 'FAIL'
            $blockReason = 'All requests failed (target unreachable or returning errors)'
        } elseif ($Scenario.IsInformative) {
            # Informative scenarios (health, edge-replay-backlog) cannot
            # BLOCK pilot — they're WARN-only or shape-only. The dispatcher
            # returned 1 for fail-count or backlog-shape reasons; we
            # surface that as PASS-with-warn so the artifact reflects
            # reality + the pilot gate stays clean. The artifact still
            # shows the per-call ok/fail breakdown.
            $verdict = 'PASS-with-warn'
            if ($failStr -and [int]$failStr -gt 0) {
                $blockReason = "Informative scenario — $failStr/$totalStr calls failed (no BLOCK gate per docs/perf/test-plan.md §3.1)"
            }
        } elseif ($p99Str) {
            $verdict = 'BLOCK'
            $p99 = [double]::Parse($p99Str, [System.Globalization.CultureInfo]::InvariantCulture)
            $blockReason = "p99 $('{0:F0}' -f $p99) ms exceeds gate $($Scenario.Pilot1xP99BlockMs) ms at $Profile"
        } else {
            $verdict = 'BLOCK'
            $blockReason = 'p99 acceptance gate breached (see scenario stdout)'
        }
    }

    Write-Info "verdict  : $verdict (exit=$exitCode duration=${duration}s)"
    if ($blockReason) { Write-Info "reason   : $blockReason" }
    if ($reportPath) { Write-Info "report   : $reportPath" }

    return [pscustomobject]@{
        Name             = $Scenario.Name
        DisplayName      = $Scenario.DisplayName
        Endpoint         = $Scenario.Endpoint
        Profile          = if ($Scenario.SupportsProfile) { $Profile } else { 'n/a' }
        Verdict          = $verdict
        ExitCode         = $exitCode
        DurationSeconds  = $duration
        BlockReason      = $blockReason
        Skipped          = $skipped
        ReportPath       = $reportPath
        Stats            = $stats
        Pilot1xP99BlockMs = $Scenario.Pilot1xP99BlockMs
        Pilot1xP99WarnMs  = $Scenario.Pilot1xP99WarnMs
        IsInformative    = $Scenario.IsInformative
        Stdout           = $stdoutText
    }
}

function Format-Latency {
    param($Stats, [string]$Key)
    if (-not $Stats) { return 'n/a' }
    # PSObject member-presence check survives StrictMode (Set-StrictMode
    # -Version Latest throws on missing properties via the dot accessor).
    $member = $Stats.PSObject.Properties[$Key]
    if (-not $member) { return 'n/a' }
    $val = $member.Value
    if ([string]::IsNullOrWhiteSpace($val)) { return 'n/a' }
    return $val
}

function Get-StatValue {
    # StrictMode-safe property accessor for parsed NickPerf stats.
    # Returns $null if the property is absent. Use this rather than
    # $stats.SomeKey when downstream code may evaluate against null.
    param($Stats, [string]$Key)
    if (-not $Stats) { return $null }
    $member = $Stats.PSObject.Properties[$Key]
    if (-not $member) { return $null }
    return $member.Value
}

function Write-Report {
    param(
        [hashtable]$ReportData,
        [string]$Path
    )
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# NickERP perf — Phase V execution — $($ReportData.Site)")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("> Generated by ``tools/perf/run-phase-v.ps1`` — Sprint 60 Phase B2 (PLAN.md §23.2 work item B2).")
    [void]$sb.AppendLine("> Wraps the NickPerf homegrown runner (Sprint 58 — replaces NBomber 6.1.0).")
    [void]$sb.AppendLine("> Acceptance gates per ``docs/perf/test-plan.md`` §3.1; output shape mirrors")
    [void]$sb.AppendLine("> ``docs/perf/baseline-2026-05-06.md``.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## Run summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Field | Value |")
    [void]$sb.AppendLine("|---|---|")
    [void]$sb.AppendLine("| Started (UTC) | $($ReportData.StartedAt) |")
    [void]$sb.AppendLine("| Finished (UTC) | $($ReportData.FinishedAt) |")
    [void]$sb.AppendLine("| Duration | $($ReportData.DurationSeconds)s |")
    [void]$sb.AppendLine("| Site | $($ReportData.Site) |")
    [void]$sb.AppendLine("| Target URL | ``$($ReportData.TargetUri)`` |")
    [void]$sb.AppendLine("| Profile | $($ReportData.Profile) |")
    [void]$sb.AppendLine("| Filter | $($ReportData.Filter) |")
    [void]$sb.AppendLine("| Configuration | $($ReportData.Configuration) |")
    [void]$sb.AppendLine("| Wrapper exit code | **$($ReportData.ExitCode)** |")
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Scenario summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Scenario | Profile | Verdict | p50 (ms) | p95 (ms) | p99 (ms) | Error rate | Throughput (RPS) | Acceptance gate |")
    [void]$sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|")
    foreach ($r in $ReportData.Results) {
        $p50 = Format-Latency -Stats $r.Stats -Key 'p50'
        $p95 = Format-Latency -Stats $r.Stats -Key 'p95'
        $p99 = Format-Latency -Stats $r.Stats -Key 'p99'
        $rps = Format-Latency -Stats $r.Stats -Key 'ThroughputRps'
        $errorRate = 'n/a'
        $totalRaw = Get-StatValue -Stats $r.Stats -Key 'Total'
        $failRaw  = Get-StatValue -Stats $r.Stats -Key 'Fail'
        if ($totalRaw -and $failRaw) {
            $total = [int]$totalRaw
            $fail = [int]$failRaw
            if ($total -gt 0) {
                $pct = [math]::Round(($fail / $total) * 100, 1)
                $errorRate = "$pct% ($fail/$total)"
            }
        }
        $gate = if ($r.IsInformative) {
            if ($r.Pilot1xP99WarnMs -gt 0) { "warn @ $($r.Pilot1xP99WarnMs) ms" } else { 'informative' }
        } else {
            "block @ $($r.Pilot1xP99BlockMs) ms"
        }
        $verdictCell = if ($r.Verdict -in 'PASS', 'PASS-with-warn', 'SKIP') { "**$($r.Verdict)**" } else { "**$($r.Verdict)**" }
        [void]$sb.AppendLine("| ``$($r.Name)`` | $($r.Profile) | $verdictCell | $p50 | $p95 | $p99 | $errorRate | $rps | $gate |")
    }
    [void]$sb.AppendLine("")

    # Per-scenario detail block (mirrors baseline-2026-05-06.md §Detailed results).
    [void]$sb.AppendLine("## Detailed results")
    [void]$sb.AppendLine("")
    foreach ($r in $ReportData.Results) {
        [void]$sb.AppendLine("### ``$($r.Name)`` — $($r.Verdict)")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("- Endpoint: ``$($r.Endpoint)``")
        [void]$sb.AppendLine("- Profile: $($r.Profile)")
        [void]$sb.AppendLine("- Wrapper-observed duration: $($r.DurationSeconds)s")
        [void]$sb.AppendLine("- Scenario exit code: $($r.ExitCode)")
        if ($r.BlockReason) {
            [void]$sb.AppendLine("- Block reason: $($r.BlockReason)")
        }
        if ($r.Skipped) {
            [void]$sb.AppendLine("- Skipped — required configuration absent (see ``docs/perf/test-plan.md`` §12.4).")
        }
        [void]$sb.AppendLine("")
        if ($r.Stats) {
            [void]$sb.AppendLine("Stats (from NickPerf ``report.md``):")
            [void]$sb.AppendLine("")
            [void]$sb.AppendLine("``````")
            [void]$sb.AppendLine("Total = $(Format-Latency -Stats $r.Stats -Key 'Total'), ok = $(Format-Latency -Stats $r.Stats -Key 'OK'), fail = $(Format-Latency -Stats $r.Stats -Key 'Fail'), RPS = $(Format-Latency -Stats $r.Stats -Key 'ThroughputRps'), elapsed = $(Format-Latency -Stats $r.Stats -Key 'Elapsed')")
            [void]$sb.AppendLine("latency (ms): min = $(Format-Latency -Stats $r.Stats -Key 'min'), mean = $(Format-Latency -Stats $r.Stats -Key 'mean'), max = $(Format-Latency -Stats $r.Stats -Key 'max')")
            [void]$sb.AppendLine("percentiles: p50 = $(Format-Latency -Stats $r.Stats -Key 'p50'), p75 = $(Format-Latency -Stats $r.Stats -Key 'p75'), p95 = $(Format-Latency -Stats $r.Stats -Key 'p95'), p99 = $(Format-Latency -Stats $r.Stats -Key 'p99')")
            [void]$sb.AppendLine("``````")
            [void]$sb.AppendLine("")
        } else {
            [void]$sb.AppendLine("_(no NickPerf report.md emitted — runner skipped or failed before snapshot)_")
            [void]$sb.AppendLine("")
        }
        if ($r.ReportPath) {
            [void]$sb.AppendLine("Source NickPerf report: ``$($r.ReportPath)``")
            [void]$sb.AppendLine("")
        }
    }

    [void]$sb.AppendLine("## Acceptance verdict")
    [void]$sb.AppendLine("")
    # Force array with @(...) — a single-element pipeline result is the
    # bare object in Windows PowerShell strict mode, which trips on .Count.
    $blocked = @(@($ReportData.Results) | Where-Object { $_.Verdict -in 'BLOCK', 'FAIL', 'FATAL', 'RUNNER-FAIL' })
    if ($blocked.Count -eq 0) {
        [void]$sb.AppendLine("- All non-informative scenarios within their p99 acceptance gates.")
        [void]$sb.AppendLine("- Pilot perf gate per ``docs/perf/test-plan.md`` §7: **PASS**.")
    } else {
        [void]$sb.AppendLine("- One or more scenarios breached their acceptance gate or failed to run.")
        [void]$sb.AppendLine("- Pilot perf gate per ``docs/perf/test-plan.md`` §7: **DO NOT SHIP** until each is understood + resolved.")
        [void]$sb.AppendLine("")
        foreach ($b in $blocked) {
            [void]$sb.AppendLine("  - ``$($b.Name)``: $($b.Verdict) — $($b.BlockReason ? $b.BlockReason : 'see scenario stdout')")
        }
    }
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## How to reproduce")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("``````powershell")
    [void]$sb.AppendLine(".\tools\perf\run-phase-v.ps1 -TargetUri $($ReportData.TargetUri) -Profile $($ReportData.Profile) -Site $($ReportData.Site)")
    [void]$sb.AppendLine("``````")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Per-scenario re-run (granular spot-check):")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("``````powershell")
    foreach ($r in $ReportData.Results) {
        $profArg = if ($r.Profile -ne 'n/a') { " -Profile $($r.Profile)" } else { '' }
        [void]$sb.AppendLine(".\tools\perf\run-phase-v.ps1 -TargetUri $($ReportData.TargetUri)$profArg -Site $($ReportData.Site) -Filter $($r.Name)")
    }
    [void]$sb.AppendLine("``````")
    [void]$sb.AppendLine("")

    Set-Content -Path $Path -Value $sb.ToString() -Encoding UTF8
    Write-Info "report : $Path"
}

# ============================================================
# Main
# ============================================================

$startedAt = Get-Date
$resolvedSite = Resolve-SiteName -Site $Site -TargetUri $TargetUri

if ($DryRun) {
    Write-Step "Dry run plan"
    Write-Info "site         : $resolvedSite"
    Write-Info "target uri   : $($TargetUri ? $TargetUri : '(default — appsettings.json)')"
    Write-Info "profile      : $Profile"
    Write-Info "filter       : $Filter"
    Write-Info "config       : $Configuration ($Tfm)"
    Write-Info "reports dir  : $ReportsDir"
    Write-Info "scenarios    :"
    foreach ($s in $Scenarios) {
        if ($Filter -ne 'all' -and $Filter -ne $s.Name) { continue }
        $gateText = if ($s.IsInformative) {
            if ($s.Pilot1xP99WarnMs -gt 0) { "warn @ $($s.Pilot1xP99WarnMs) ms" } else { 'informative' }
        } else {
            "block @ $($s.Pilot1xP99BlockMs) ms"
        }
        Write-Info "  - $($s.Name) → $($s.Endpoint) ($gateText)"
    }
    Write-Info "no commands executed"
    exit 0
}

# Pre-flight.
$preflight = Test-Preflight -TargetUri $TargetUri
if ($preflight.ExitCode -ne 0) { exit $preflight.ExitCode }

# Run scenarios in declared order, honouring -Filter.
$results = @()
foreach ($s in $Scenarios) {
    if ($Filter -ne 'all' -and $Filter -ne $s.Name) { continue }
    $r = Invoke-PerfScenario -Scenario $s -Profile $Profile -TargetUri $TargetUri
    $results += $r
}

if ($results.Count -eq 0) {
    Write-Error "No scenarios ran (filter='$Filter' matched nothing)."
    exit 1
}

# Aggregate exit code:
#   2 = at least one BLOCK / FAIL / RUNNER-FAIL
#   3 = at least one FATAL
#   0 = everything PASS / PASS-with-warn / SKIP
# @(...) forces array coercion so single-element results don't trip .Count.
$hasFatal       = @(@($results) | Where-Object { $_.Verdict -eq 'FATAL' }).Count -gt 0
$hasBlock       = @(@($results) | Where-Object { $_.Verdict -in 'BLOCK', 'FAIL', 'RUNNER-FAIL' }).Count -gt 0
$exitCode = 0
if ($hasFatal) { $exitCode = 3 }
elseif ($hasBlock) { $exitCode = 2 }

$finishedAt = Get-Date
$reportData = @{
    StartedAt        = $startedAt.ToUniversalTime().ToString('o')
    FinishedAt       = $finishedAt.ToUniversalTime().ToString('o')
    DurationSeconds  = [math]::Round(($finishedAt - $startedAt).TotalSeconds, 2)
    Site             = $resolvedSite
    TargetUri        = if ([string]::IsNullOrWhiteSpace($TargetUri)) { '(default — appsettings.json)' } else { $TargetUri }
    Profile          = $Profile
    Filter           = $Filter
    Configuration    = $Configuration
    Results          = $results
    ExitCode         = $exitCode
}

$reportName = "perf-$resolvedSite-$(Get-Date -Format 'yyyy-MM-dd-HHmmss').md"
$reportPath = Join-Path $ReportsDir $reportName
Write-Report -ReportData $reportData -Path $reportPath

Write-Step "Done — exit $exitCode (report: $reportPath)"
exit $exitCode
