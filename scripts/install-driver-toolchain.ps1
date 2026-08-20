# Requires -ExecutionPolicy Bypass
$ErrorActionPreference = "Continue"

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  DC-ScreenSharing Phase 2 - WFP Driver Development Toolchain Installer" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Check for Administrator Privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[INFO] Requesting Administrator Elevation (UAC)..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs
    exit 0
}

Write-Host "[OK] Running with Elevated Administrator Privileges." -ForegroundColor Green
Write-Host ""

$scratchDir = "C:\Users\Faelzinhown\.gemini\antigravity-ide\brain\b1a44a9f-6ecd-434f-ad60-07b96ed489ae\scratch"
$vsSetup = Join-Path $scratchDir "vs_BuildTools.exe"
$wdkLayoutSetup = Join-Path $scratchDir "wdk_layout\wdksetup.exe"
$wdkDirectSetup = Join-Path $scratchDir "wdksetup.exe"

# 2. Check Visual Studio 2022 Build Tools (MSVC v143)
Write-Host "[1/4] Checking Visual Studio 2022 Build Tools..." -ForegroundColor Cyan
$msvcDir = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC"
if (Test-Path $msvcDir) {
    $ver = (Get-ChildItem $msvcDir | Sort-Object Name -Descending | Select-Object -First 1).Name
    Write-Host "[OK] MSVC v143 toolset is already installed (Version: $ver)." -ForegroundColor Green
} else {
    Write-Host "[INFO] Installing Visual C++ Build Tools (MSVC v143, Spectre libs)..." -ForegroundColor Yellow
    if (-not (Test-Path $vsSetup)) {
        Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_BuildTools.exe" -OutFile $vsSetup
    }
    $p = Start-Process -FilePath $vsSetup -ArgumentList "--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --add Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre" -PassThru -Wait
    Write-Host "[OK] VS Build Tools exit code: $($p.ExitCode)" -ForegroundColor Green
}

# 3. Check Windows 11 SDK 22621
Write-Host ""
Write-Host "[2/4] Checking Windows 11 SDK 10.0.22621.0..." -ForegroundColor Cyan
$sdkInclude = "C:\Program Files (x86)\Windows Kits\10\Include\10.0.22621.0\um\windows.h"
if (Test-Path $sdkInclude) {
    Write-Host "[OK] Windows 11 SDK 10.0.22621.0 is already installed." -ForegroundColor Green
} else {
    Write-Host "[INFO] Installing Windows 11 SDK..." -ForegroundColor Yellow
    $p = Start-Process -FilePath $vsSetup -ArgumentList "--quiet --wait --norestart --nocache --add Microsoft.VisualStudio.Component.Windows11SDK.22621" -PassThru -Wait
    Write-Host "[OK] SDK installer exit code: $($p.ExitCode)" -ForegroundColor Green
}

# 4. Install WDK 10.0.22621
Write-Host ""
Write-Host "[3/4] Installing Windows Driver Kit (WDK) 10.0.22621..." -ForegroundColor Cyan

$wdkExe = if (Test-Path $wdkLayoutSetup) { $wdkLayoutSetup } else { $wdkDirectSetup }
if (-not (Test-Path $wdkExe)) {
    Write-Host "[INFO] Downloading WDK 10.0.22621 installer..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/?linkid=2196230" -OutFile $wdkDirectSetup
    $wdkExe = $wdkDirectSetup
}

Write-Host "[INFO] Launching WDK Setup from: $wdkExe" -ForegroundColor Yellow
$p = Start-Process -FilePath $wdkExe -ArgumentList "/features + /q" -PassThru -Wait
Write-Host "[OK] WDK installation step completed (ExitCode: $($p.ExitCode))." -ForegroundColor Green

# 5. Verify & Integrate WDK MSBuild Toolset
Write-Host ""
Write-Host "[4/4] Verifying WDK MSBuild Integration..." -ForegroundColor Cyan
$wdkBuild = "C:\Program Files (x86)\Windows Kits\10\build"
if (Test-Path $wdkBuild) {
    Write-Host "[OK] WDK build targets found at: $wdkBuild" -ForegroundColor Green
} else {
    Write-Warning "WDK build directory not found yet at $wdkBuild."
}

# 6. Test Driver Build
Write-Host ""
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  Toolchain Setup Complete! Testing Kernel Driver Compilation..." -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host ""

$buildDriverScript = Join-Path (Split-Path $MyInvocation.MyCommand.Path) "build-driver.ps1"
if (Test-Path $buildDriverScript) {
    & $buildDriverScript -Configuration Release -Platform x64
}

Write-Host ""
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  Setup process finished." -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "Press Enter to exit..." -ForegroundColor Yellow
[void][System.Console]::ReadLine()
