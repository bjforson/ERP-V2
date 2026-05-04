#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Start all NickHR services after a server reboot.
    Run this script once after reboot, or set it as a startup task.
.DESCRIPTION
    1. Starts NickHR API Windows service
    2. Starts NickHR WebApp Windows service
    3. Installs and starts Cloudflare Tunnel service
    4. Verifies all services are running
#>

# Continues past errors intentionally: startup script must attempt to start API, WebApp, and Cloudflare Tunnel even if one fails — partial startup is reported per-service rather than aborting.
$ErrorActionPreference = "Continue"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-OK($msg) { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }
function Write-Err($msg) { Write-Host "    $msg" -ForegroundColor Red }

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  NickHR Startup Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# --- Step 1: NickHR API ---
Write-Step "Starting NickHR API service..."
$apiSvc = Get-Service -Name "NickHR_API" -ErrorAction SilentlyContinue
if ($apiSvc) {
    if ($apiSvc.Status -ne 'Running') {
        Start-Service "NickHR_API" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
    }
    $apiSvc = Get-Service -Name "NickHR_API"
    if ($apiSvc.Status -eq 'Running') { Write-OK "NickHR API: Running" }
    else { Write-Warn "NickHR API: $($apiSvc.Status) (may need more time for DB migration)" }
} else {
    Write-Err "NickHR API service not found!"
}

# Wait for API to fully start (DB migrations)
Write-Step "Waiting for API to complete startup (30 seconds)..."
Start-Sleep -Seconds 30
$apiSvc = Get-Service -Name "NickHR_API" -ErrorAction SilentlyContinue
if ($apiSvc -and $apiSvc.Status -eq 'Running') { Write-OK "NickHR API: Running" }
else { Write-Warn "NickHR API: May still be starting" }

# --- Step 2: NickHR WebApp ---
Write-Step "Starting NickHR WebApp service..."
$webSvc = Get-Service -Name "NickHR_WebApp" -ErrorAction SilentlyContinue
if ($webSvc) {
    if ($webSvc.Status -ne 'Running') {
        Start-Service "NickHR_WebApp" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 15
    }
    $webSvc = Get-Service -Name "NickHR_WebApp"
    if ($webSvc.Status -eq 'Running') { Write-OK "NickHR WebApp: Running" }
    else { Write-Warn "NickHR WebApp: $($webSvc.Status)" }
} else {
    Write-Err "NickHR WebApp service not found!"
}

# --- Step 3: Cloudflare Tunnel ---
Write-Step "Setting up Cloudflare Tunnel..."

# Check if cloudflared service exists
$cfSvc = Get-Service -Name "Cloudflared" -ErrorAction SilentlyContinue

if (-not $cfSvc) {
    Write-Warn "Cloudflared service not installed. Installing..."
    & cloudflared service install 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    $cfSvc = Get-Service -Name "Cloudflared" -ErrorAction SilentlyContinue
}

if ($cfSvc) {
    if ($cfSvc.Status -ne 'Running') {
        Start-Service "Cloudflared" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 10
    }
    $cfSvc = Get-Service -Name "Cloudflared"
    if ($cfSvc.Status -eq 'Running') { Write-OK "Cloudflare Tunnel: Running" }
    else {
        Write-Warn "Cloudflared service failed to start. Starting tunnel manually..."
        Start-Process -FilePath "cloudflared" -ArgumentList "tunnel run nickhr" -WindowStyle Hidden
        Start-Sleep -Seconds 10
        Write-OK "Cloudflare Tunnel: Started manually"
    }
} else {
    Write-Warn "Could not install Cloudflared service. Starting tunnel manually..."
    Start-Process -FilePath "cloudflared" -ArgumentList "tunnel run nickhr" -WindowStyle Hidden
    Start-Sleep -Seconds 10
    Write-OK "Cloudflare Tunnel: Started manually"
}

# --- Step 4: Verify ---
Write-Step "Verifying services..."
Start-Sleep -Seconds 5

$results = @()

# Check NickHR API
$apiRunning = (Get-Service "NickHR_API" -ErrorAction SilentlyContinue).Status -eq 'Running'
$results += [PSCustomObject]@{ Service = "NickHR API"; Status = if ($apiRunning) { "Running" } else { "NOT RUNNING" }; URL = "http://10.0.0.79:5215/swagger" }

# Check NickHR WebApp
$webRunning = (Get-Service "NickHR_WebApp" -ErrorAction SilentlyContinue).Status -eq 'Running'
$results += [PSCustomObject]@{ Service = "NickHR WebApp (HTTP)"; Status = if ($webRunning) { "Running" } else { "NOT RUNNING" }; URL = "http://10.0.0.79:5310" }
$results += [PSCustomObject]@{ Service = "NickHR WebApp (HTTPS)"; Status = if ($webRunning) { "Running" } else { "NOT RUNNING" }; URL = "https://10.0.0.79:5311" }

# Check Cloudflare Tunnel
$tunnelInfo = & cloudflared tunnel info nickhr 2>&1
$tunnelRunning = $tunnelInfo -match "CONNECTOR"
$results += [PSCustomObject]@{ Service = "Cloudflare Tunnel (hr)"; Status = if ($tunnelRunning) { "Running" } else { "NOT RUNNING" }; URL = "https://hr.nickscan.net" }
$results += [PSCustomObject]@{ Service = "Cloudflare Tunnel (lan)"; Status = if ($tunnelRunning) { "Running" } else { "NOT RUNNING" }; URL = "https://lan.nickscan.net" }

$results | Format-Table -AutoSize

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  NickHR Startup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  LAN:      https://lan.nickscan.net" -ForegroundColor White
Write-Host "  External: https://hr.nickscan.net" -ForegroundColor White
Write-Host "  API:      http://10.0.0.79:5215/swagger" -ForegroundColor White
Write-Host ""
