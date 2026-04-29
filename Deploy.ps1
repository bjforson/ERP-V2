# ============================================================
# NickERP v2 (ERP V2) Deployment Script
# ============================================================
# Publishes one or more ERP V2 services to the canonical publish
# directory, restarts the NSSM-supervised Windows services, and
# probes /healthz/ready on each one.
#
# CANONICAL PATHS (never change without updating each Windows
# service's binPath via `nssm set <service> Application`):
#   - NickERP_Inspection_Web : C:\Shared\ERP V2\publish\Inspection.Web
#   - NickERP_Portal         : C:\Shared\ERP V2\publish\Portal
#   - (reserved) :5420 range : NickERP_NickFinance_*
#   - (reserved) :5430 range : NickERP_NickHR_*
#
# The folder name "ERP V2" contains a space; every internal path
# is single-quoted or wrapped in [string]/Join-Path so the space
# is preserved.
#
# Usage:
#   .\Deploy.ps1                # Default: Inspection Web + Portal
#   .\Deploy.ps1 -ApiOnly       # Inspection Web only (v2 has no API/WebApp split)
#   .\Deploy.ps1 -WebAppOnly    # Portal only
#   .\Deploy.ps1 -SkipBuild     # Skip dotnet publish; just restart + probe
#   .\Deploy.ps1 -DryRun        # Print plan, do nothing
#
# Run from the worktree / checkout root (the dir that contains
# this Deploy.ps1, NickERP.Tests.slnx, modules/, apps/, platform/).
#
# ============================================================
# Robocopy strategy (v0 - same-host build + copy)
# ============================================================
#
# This v0 assumes the script runs ON the prod box and that the
# checkout/build root IS the prod box's working tree. Robocopy
# is an in-place mirror from each project's `bin/Release/<tfm>/publish`
# (output of `dotnet publish`) into `C:\Shared\ERP V2\publish\<service>`.
# Same disk; this is essentially a fast directory rename.
#
# Why this v0 shape, not v1's `Y:\` pattern:
#   v1's Deploy.ps1 robocopies from `Y:\` (a remote/mapped drive
#   from the dev machine) into `C:\Shared\NSCIM_PRODUCTION\`. That
#   pattern assumes a separate dev box that pre-builds on Y:\.
#   For ERP V2's first cut we keep dev+prod colocated on one box
#   and let `dotnet publish` write directly into the publish dir
#   (`-o <target>`). This matches the simpler dev-box pattern of
#   the docs/runbooks/01-deploy.md §5.3 manual flow.
#
# Future "cross-host" variant (when the dev box is split off):
#   Step 1 - on dev box:
#       dotnet publish ... -o Y:\ERP_V2\publish\<service>
#   Step 2 - on prod box (this script):
#       robocopy "Y:\ERP_V2\publish\<service>" `
#                "C:\Shared\ERP V2\publish\<service>" `
#                /MIR /NFL /NDL /NP
#   The script's `Publish-Project` helper would gain a
#   `-Source <unc-or-mapped-path>` parameter, and the build phase
#   would be split into "build on dev" (skipped here) + "robocopy
#   from dev → prod" (this becomes the publish phase). Keeping the
#   v0 shape reduces moving parts until the dev/prod split is real.
#
# ============================================================
# NSSM service registration (one-time, ops-only - NOT done here)
# ============================================================
#
# This script restarts services that already exist. To install them
# the first time:
#
#   $tools = "C:\Shared\NSCIM_PRODUCTION\tools\nssm-2.24\win64\nssm.exe"
#
#   # Inspection Web
#   & $tools install NickERP_Inspection_Web `
#       "C:\Program Files\dotnet\dotnet.exe" `
#       "C:\Shared\ERP V2\publish\Inspection.Web\NickERP.Inspection.Web.dll"
#   & $tools set NickERP_Inspection_Web AppDirectory "C:\Shared\ERP V2\publish\Inspection.Web"
#   & $tools set NickERP_Inspection_Web ObjectName "LocalSystem"
#   & $tools set NickERP_Inspection_Web Start SERVICE_AUTO_START
#   & $tools set NickERP_Inspection_Web AppEnvironmentExtra `
#       "ASPNETCORE_ENVIRONMENT=Production" `
#       "ASPNETCORE_URLS=http://127.0.0.1:5410" `
#       "NICKSCAN_DB_PASSWORD=<from secret store; required>"
#   # TODO(FU-userid): when FU-userid lands, an `app.user_id` plumbing
#   # env var (or a row written by IdentityTenancyInterceptor) will be
#   # referenced here. See docs/product-calls-2026-04-29.md §3.2.
#
#   # Portal
#   & $tools install NickERP_Portal `
#       "C:\Program Files\dotnet\dotnet.exe" `
#       "C:\Shared\ERP V2\publish\Portal\NickERP.Portal.dll"
#   & $tools set NickERP_Portal AppDirectory "C:\Shared\ERP V2\publish\Portal"
#   & $tools set NickERP_Portal ObjectName "LocalSystem"
#   & $tools set NickERP_Portal Start SERVICE_AUTO_START
#   & $tools set NickERP_Portal AppEnvironmentExtra `
#       "ASPNETCORE_ENVIRONMENT=Production" `
#       "ASPNETCORE_URLS=http://127.0.0.1:5400" `
#       "NICKSCAN_DB_PASSWORD=<from secret store; required>"
#
# All services run as LocalSystem (mirroring v1 + Sprint 5/G1-3 +
# Sprint 8/P3 deployments). NICKSCAN_DB_PASSWORD is required (the
# nscim_app Postgres role's password); the script fails fast if
# the env var is missing for the running service. Never put the
# password literal in this script.
#
# ============================================================
# Compatibility
# ============================================================
# Targets PowerShell 5.1+ (Windows-bundled) and PowerShell 7+. No
# 7-only syntax (no ternary, no null-coalescing, no `&&`/`||`
# pipeline chains in script bodies). Output uses Write-Host for
# operator visibility - this is a deploy tool, not a function.
# ============================================================

param(
    [switch]$ApiOnly,
    [switch]$WebAppOnly,
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$ScriptRoot = $PSScriptRoot

# --- Canonical paths (must match each NSSM service's binPath / AppDirectory) ---
$PUBLISH_ROOT = "C:\Shared\ERP V2\publish"

# --- Service catalogue ------------------------------------------------------
# Single source of truth for every supervised .NET service this script can
# deploy. Each entry is a hashtable used by all phases below.
#
#   Key       NSSM service name (also Get-Service name)
#   Csproj    Absolute path to the project to `dotnet publish`
#   Publish   Absolute target directory (must match the service AppDirectory)
#   Dll       Filename of the published entry-point DLL (for verification)
#   Tag       Short label used in console output / phase headers
#   Group     Logical group used by the per-service flags
#             ("inspection" maps to -ApiOnly, "portal" maps to -WebAppOnly)
#   HealthUrl /healthz/ready URL on 127.0.0.1 (Use 127.0.0.1 not localhost
#             to avoid IPv6 ECONNREFUSED on dual-stack - see
#             reference_service_binpaths_and_deploy.md)
# ----------------------------------------------------------------------------
$SERVICES = @(
    @{
        Key       = "NickERP_Inspection_Web"
        Csproj    = Join-Path $ScriptRoot "modules\inspection\src\NickERP.Inspection.Web\NickERP.Inspection.Web.csproj"
        Publish   = Join-Path $PUBLISH_ROOT "Inspection.Web"
        Dll       = "NickERP.Inspection.Web.dll"
        Tag       = "Inspection.Web"
        Group     = "inspection"
        HealthUrl = "http://127.0.0.1:5410/healthz/ready"
    },
    @{
        Key       = "NickERP_Portal"
        Csproj    = Join-Path $ScriptRoot "apps\portal\NickERP.Portal.csproj"
        Publish   = Join-Path $PUBLISH_ROOT "Portal"
        Dll       = "NickERP.Portal.dll"
        Tag       = "Portal"
        Group     = "portal"
        HealthUrl = "http://127.0.0.1:5400/healthz/ready"
    }
    # Future reservations (do not enable until the projects exist):
    # @{ Key = "NickERP_NickFinance_*" ; ... ; HealthUrl = "http://127.0.0.1:5420/healthz/ready" }
    # @{ Key = "NickERP_NickHR_*"      ; ... ; HealthUrl = "http://127.0.0.1:5430/healthz/ready" }
)

# --- Selection logic --------------------------------------------------------
# Resolve which $SERVICES entries to deploy this run, based on flags.
# Default (no flags): every service in $SERVICES.
# -ApiOnly : Inspection Web only (v2 has no API/WebApp split - Inspection Web
#            is the closest analog to v1's API tier).
# -WebAppOnly : Portal only.
# -ApiOnly + -WebAppOnly : both (same as default).
# ----------------------------------------------------------------------------
function Get-SelectedServices {
    $onlyFlags = @{
        "inspection" = $ApiOnly
        "portal"     = $WebAppOnly
    }
    $anyOnly = $false
    foreach ($v in $onlyFlags.Values) { if ($v) { $anyOnly = $true; break } }

    if ($anyOnly) {
        return $SERVICES | Where-Object { $onlyFlags[$_.Group] }
    }
    return $SERVICES
}

function Write-Header($text) {
    Write-Host ""
    Write-Host "----------------------------------------------------" -ForegroundColor Cyan
    Write-Host " $text" -ForegroundColor Cyan
    Write-Host "----------------------------------------------------" -ForegroundColor Cyan
}

function Write-Step($text) {
    Write-Host ">>> $text" -ForegroundColor Yellow
}

function Write-OK($text) {
    Write-Host "    [OK] $text" -ForegroundColor Green
}

function Write-Fail($text) {
    Write-Host "    [FAIL] $text" -ForegroundColor Red
}

function Write-Plan($text) {
    Write-Host "    [PLAN] $text" -ForegroundColor Magenta
}

function Stop-SvcIfRunning($name) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "    Service $name not installed, skipping" -ForegroundColor Gray
        return
    }
    if ($svc.Status -eq 'Running') {
        if ($DryRun) {
            Write-Plan "Would stop $name"
        } else {
            Write-Step "Stopping $name..."
            Stop-Service -Name $name -Force
            Start-Sleep -Seconds 2
            Write-OK "Stopped $name"
        }
    } else {
        Write-Host "    $name already stopped" -ForegroundColor Gray
    }
}

function Start-Svc($name) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        Write-Host "    Service $name not installed, skipping" -ForegroundColor Gray
        return
    }
    if ($DryRun) {
        Write-Plan "Would start $name"
        return
    }
    Write-Step "Starting $name..."
    Start-Service -Name $name
    Start-Sleep -Seconds 3
    $svc.Refresh()
    if ($svc.Status -eq 'Running') {
        Write-OK "Started $name"
    } else {
        Write-Fail "$name failed to start (status: $($svc.Status))"
        throw "Service start failed: $name"
    }
}

function Publish-Project($name, $csproj, $target) {
    if ($SkipBuild) {
        Write-Host "    [SkipBuild] Skipping publish of $name" -ForegroundColor Gray
        return
    }
    if ($DryRun) {
        Write-Plan "Would dotnet publish $csproj -c Release -o $target"
        return
    }
    if (-not (Test-Path $csproj)) {
        Write-Fail "$name csproj missing at $csproj"
        throw "Publish failed: csproj not found for $name"
    }
    Write-Step "Publishing $name to $target..."
    # `dotnet publish` writes directly into the canonical publish dir.
    # No separate robocopy step in v0 (same-host build + copy is just
    # "publish to the right place").
    $output = & dotnet publish $csproj -c Release -o $target --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "Publish failed for $name"
    }
    Write-OK "Published $name"
}

function Test-DeploymentBinary($name, $dllPath) {
    if ($DryRun) { return }
    if ($SkipBuild) { return }
    if (-not (Test-Path $dllPath)) {
        Write-Fail "$name DLL missing at $dllPath"
        throw "Deployment verification failed"
    }
    $dll = Get-Item $dllPath
    $age = (Get-Date) - $dll.LastWriteTime
    if ($age.TotalMinutes -gt 10) {
        Write-Fail "$name DLL is $([int]$age.TotalMinutes) minutes old - did publish go to the wrong location?"
        throw "Deployment verification failed"
    }
    Write-OK "$name DLL verified ($([int]$age.TotalSeconds)s old)"
}

function Test-HealthzReady($name, $url) {
    if ($DryRun) {
        Write-Plan "Would probe $url"
        return
    }
    Write-Step "Probing $url ..."
    # Retry briefly: services may take a few seconds after Start-Service to
    # bind their listener and finish the readiness checks.
    $maxAttempts = 10
    $delay = 2
    for ($i = 1; $i -le $maxAttempts; $i++) {
        try {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                Write-OK "$name /healthz/ready returned 200 (attempt $i)"
                return
            }
            Write-Host "    Attempt ${i}: $($resp.StatusCode)" -ForegroundColor Gray
        } catch {
            $msg = $_.Exception.Message
            Write-Host "    Attempt $i/${maxAttempts}: $msg" -ForegroundColor Gray
        }
        if ($i -lt $maxAttempts) { Start-Sleep -Seconds $delay }
    }
    Write-Fail "$name /healthz/ready did not return 200 after $maxAttempts attempts"
    throw "Healthz probe failed for $name"
}

# --- MAIN ------------------------------------------------------------------

Write-Header "NickERP v2 Deployment"
Write-Host "Worktree root: $ScriptRoot"
Write-Host "Publish root:  $PUBLISH_ROOT"

$selected = @(Get-SelectedServices)
if ($selected.Count -eq 0) {
    Write-Fail "No services selected. Check your flags."
    exit 1
}

# Compose a human-readable mode label from the active flags.
$modeParts = @()
if ($DryRun)    { $modeParts += "DRY RUN" }
if ($SkipBuild) { $modeParts += "Skip build" }
$modeParts += "Selected: " + (($selected | ForEach-Object { $_.Tag }) -join ", ")
Write-Host "Mode:          $($modeParts -join ' | ')"
Write-Host "Services:      $(($selected | ForEach-Object { $_.Key }) -join ', ')"
Write-Host ""

# Cheap pre-flight: every selected csproj must exist before we touch any
# service. Catches typos and missing repos without leaving services half-
# stopped mid-deploy.
foreach ($s in $selected) {
    if (-not (Test-Path $s.Csproj)) {
        Write-Fail "Cannot find $($s.Csproj) for $($s.Key) - are you running from the worktree root?"
        exit 1
    }
}

# --- Phase 1: Stop services ---
# Stop in reverse selection order. With the default Inspection+Portal pair
# this stops Portal first, then Inspection - Portal calls Inspection's
# AppSwitcher endpoints, so it should not be answering requests when its
# downstream cycles.
Write-Header "Phase 1: Stop services"
$stopOrder = @($selected); [array]::Reverse($stopOrder)
foreach ($s in $stopOrder) {
    Stop-SvcIfRunning $s.Key
}

# --- Phase 2: Publish ---
# `dotnet publish ... -o <Publish>` writes directly to the canonical publish
# dir (same-host v0). For the future cross-host pattern, see header docstring.
Write-Header "Phase 2: Publish"
foreach ($s in $selected) {
    Publish-Project $s.Tag $s.Csproj $s.Publish
}

# --- Phase 3: Verify binaries ---
Write-Header "Phase 3: Verify binaries"
foreach ($s in $selected) {
    Test-DeploymentBinary $s.Tag (Join-Path $s.Publish $s.Dll)
}

# --- Phase 4: Start services ---
# Start in original (forward) selection order; with default Inspection+Portal
# this means Inspection.Web comes up first so the Portal's AppSwitcher hits
# a live downstream when it boots.
Write-Header "Phase 4: Start services"
foreach ($s in $selected) {
    Start-Svc $s.Key
}

# --- Phase 5: Healthz probe ---
# Each service exposes /healthz/ready (Phase F5). 200 = all checks healthy.
# We retry briefly to ride out service-bind latency.
Write-Header "Phase 5: Healthz probe"
foreach ($s in $selected) {
    Test-HealthzReady $s.Tag $s.HealthUrl
}

# --- Phase 6: Per-service summary ---
Write-Header "Phase 6: Summary"
foreach ($s in $selected) {
    Write-Host "    $($s.Tag.PadRight(20)) $($s.Key.PadRight(28)) $($s.HealthUrl)" -ForegroundColor Green
}

Write-Header "Deployment complete"
if ($DryRun) {
    Write-Host " DRY RUN - no changes made." -ForegroundColor Magenta
} else {
    Write-Host " Reload the browser to see UI changes." -ForegroundColor Green
}
Write-Host ""
