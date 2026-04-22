#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the YieldDataLogger Agent (Windows Service) and Manager (tray app).

.DESCRIPTION
    Run this script once from an Administrator PowerShell in the folder where you
    extracted the YieldDataLogger release zip.  It will:

      1. Copy the Agent exe to C:\YieldDataLogger\Agent\
      2. Register and start the "YieldDataLogger.Agent" Windows Service
         (connects automatically to Azure on every boot)
      3. Copy the Manager exe to C:\YieldDataLogger\Manager\
      4. Add the Manager to the current user's Windows startup
         (so the tray icon appears after every login)
      5. Launch the Manager immediately

    Re-running this script performs an in-place upgrade: the service is stopped,
    files are replaced, and the service is restarted.

.NOTES
    Must be run as Administrator (required for sc.exe / service registration).
    The Azure hub URL is baked into appsettings.json inside the Agent exe.
    No .NET runtime installation is required – both exes are self-contained.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$agentSrc    = Join-Path $scriptDir "..\dist\Agent"
$managerSrc  = Join-Path $scriptDir "..\dist\Manager"

$agentDest   = "C:\YieldDataLogger\Agent"
$managerDest = "C:\YieldDataLogger\Manager"
$agentExe    = Join-Path $agentDest  "YieldDataLogger.Agent.exe"
$managerExe  = Join-Path $managerDest "YieldDataLogger.Manager.exe"

$serviceName = "YieldDataLogger.Agent"
$startupKey  = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$startupName = "YieldDataLogger.Manager"

# ---------------------------------------------------------------------------
# Helper
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >> $msg" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Validate source folders exist
# ---------------------------------------------------------------------------
if (-not (Test-Path $agentSrc))   { throw "Agent source folder not found: $agentSrc" }
if (-not (Test-Path $managerSrc)) { throw "Manager source folder not found: $managerSrc" }

Write-Host ""
Write-Host "  YieldDataLogger Installer" -ForegroundColor Green
Write-Host "  =========================" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 1. Stop existing service (if running) so we can replace the exe
# ---------------------------------------------------------------------------
Write-Step "Checking for existing service..."
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Write-Host "     Stopping service..." -ForegroundColor Yellow
        Stop-Service -Name $serviceName -Force
        Start-Sleep -Seconds 2
    }
    Write-Host "     Removing old service registration..." -ForegroundColor Yellow
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

# ---------------------------------------------------------------------------
# 2. Copy Agent files
# ---------------------------------------------------------------------------
Write-Step "Installing Agent to $agentDest ..."
New-Item -ItemType Directory -Path $agentDest -Force | Out-Null
Copy-Item -Path "$agentSrc\*" -Destination $agentDest -Recurse -Force
Write-Host "     Done." -ForegroundColor Green

# ---------------------------------------------------------------------------
# 3. Register and start the Windows Service
# ---------------------------------------------------------------------------
Write-Step "Registering Windows Service '$serviceName'..."
$result = sc.exe create $serviceName `
    binPath= "`"$agentExe`"" `
    start= auto `
    DisplayName= "YieldDataLogger Agent"

if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed: $result" }

# Give the service a friendly description visible in services.msc
sc.exe description $serviceName "Connects to the YieldDataLogger Azure hub and writes live price ticks to local SQLite files." | Out-Null

Write-Step "Starting service..."
Start-Service -Name $serviceName
Start-Sleep -Seconds 3

$status = (Get-Service -Name $serviceName).Status
if ($status -ne "Running") {
    Write-Host "     WARNING: Service status is '$status' – check Event Viewer for details." -ForegroundColor Yellow
} else {
    Write-Host "     Service is RUNNING." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 4. Copy Manager files
# ---------------------------------------------------------------------------
Write-Step "Installing Manager to $managerDest ..."
New-Item -ItemType Directory -Path $managerDest -Force | Out-Null
Copy-Item -Path "$managerSrc\*" -Destination $managerDest -Recurse -Force
Write-Host "     Done." -ForegroundColor Green

# ---------------------------------------------------------------------------
# 5. Add Manager to Windows startup (per-user, no admin required at runtime)
# ---------------------------------------------------------------------------
Write-Step "Adding Manager to Windows startup..."
Set-ItemProperty -Path $startupKey -Name $startupName -Value "`"$managerExe`""
Write-Host "     Added to HKCU Run key." -ForegroundColor Green

# ---------------------------------------------------------------------------
# 6. Launch Manager now
# ---------------------------------------------------------------------------
Write-Step "Launching Manager..."
Start-Process -FilePath $managerExe

Write-Host ""
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  Agent  : running as Windows Service (auto-start on boot)" -ForegroundColor White
Write-Host "  Manager: running in system tray (auto-start on login)" -ForegroundColor White
Write-Host ""
Write-Host "  To check service status:  sc.exe query YieldDataLogger.Agent" -ForegroundColor Gray
Write-Host "  To uninstall:             scripts\uninstall.ps1  (run as admin)" -ForegroundColor Gray
Write-Host ""
