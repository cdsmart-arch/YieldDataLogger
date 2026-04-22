#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the YieldDataLogger Agent service and Manager startup entry.

.PARAMETER KeepData
    If specified, the SQLite price data in C:\ProgramData\YieldDataLogger\Yields
    is preserved.  By default all application data is removed.

.EXAMPLE
    # Full uninstall (removes everything including price history)
    .\uninstall.ps1

    # Remove app but keep the accumulated price data
    .\uninstall.ps1 -KeepData
#>
param(
    [switch]$KeepData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$serviceName = "YieldDataLogger.Agent"
$startupKey  = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$startupName = "YieldDataLogger.Manager"
$installRoot = "C:\YieldDataLogger"
$dataRoot    = "C:\ProgramData\YieldDataLogger"

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >> $msg" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "  YieldDataLogger Uninstaller" -ForegroundColor Yellow
Write-Host "  ===========================" -ForegroundColor Yellow

# ---------------------------------------------------------------------------
# Stop and remove the service
# ---------------------------------------------------------------------------
Write-Step "Stopping and removing service '$serviceName'..."
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Stop-Service -Name $serviceName -Force
        Start-Sleep -Seconds 2
    }
    sc.exe delete $serviceName | Out-Null
    Write-Host "     Service removed." -ForegroundColor Green
} else {
    Write-Host "     Service not found (already removed)." -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# Kill the Manager tray app if it is running
# ---------------------------------------------------------------------------
Write-Step "Closing Manager tray app..."
Get-Process -Name "YieldDataLogger.Manager" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "     Done." -ForegroundColor Green

# ---------------------------------------------------------------------------
# Remove startup entry
# ---------------------------------------------------------------------------
Write-Step "Removing startup entry..."
Remove-ItemProperty -Path $startupKey -Name $startupName -ErrorAction SilentlyContinue
Write-Host "     Done." -ForegroundColor Green

# ---------------------------------------------------------------------------
# Remove installed files
# ---------------------------------------------------------------------------
Write-Step "Removing installed files from $installRoot ..."
if (Test-Path $installRoot) {
    Remove-Item -Path $installRoot -Recurse -Force
    Write-Host "     Done." -ForegroundColor Green
} else {
    Write-Host "     Folder not found (already removed)." -ForegroundColor Gray
}

# ---------------------------------------------------------------------------
# Optionally remove data
# ---------------------------------------------------------------------------
if ($KeepData) {
    Write-Host ""
    Write-Host "  -KeepData specified: price history preserved at $dataRoot" -ForegroundColor Cyan
} else {
    Write-Step "Removing application data from $dataRoot ..."
    if (Test-Path $dataRoot) {
        Remove-Item -Path $dataRoot -Recurse -Force
        Write-Host "     Done." -ForegroundColor Green
    } else {
        Write-Host "     Folder not found (already removed)." -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "  Uninstall complete." -ForegroundColor Yellow
Write-Host ""
