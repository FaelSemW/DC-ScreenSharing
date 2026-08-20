# Requires -ExecutionPolicy Bypass
$ErrorActionPreference = "Continue"

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  DCSS WFP Kernel Callout Driver - Installation & Load Runner" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[INFO] Elevating to Administrator..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" -Verb RunAs
    exit 0
}

Write-Host "[OK] Running as Administrator." -ForegroundColor Green

$sysPath = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\x64\Release\DCSS.WfpCallout.sys"
$pkgDir = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\pkg\x64\Release"
$catPath = "$pkgDir\DCSS.WfpCallout.cat"
$infPath = "$pkgDir\DCSS.WfpCallout.inf"

# 1. Ensure test certificate is in TrustedPublisher / Root on local machine for development
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like "*DCSS Local Dev Test Sign*" } | Select-Object -First 1
if ($cert) {
    Write-Host "[1/4] Installing development test certificate to LocalMachine stores..." -ForegroundColor Cyan
    try {
        $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
        $rootStore.Open("ReadWrite")
        $rootStore.Add($cert)
        $rootStore.Close()

        $pubStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", "LocalMachine")
        $pubStore.Open("ReadWrite")
        $pubStore.Add($cert)
        $pubStore.Close()
        Write-Host "[OK] Development test certificate installed in LocalMachine Root and TrustedPublisher." -ForegroundColor Green
    } catch {
        Write-Warning "Could not add certificate to local store: $_"
    }
}

# 2. Register Driver Service
Write-Host ""
Write-Host "[2/4] Registering DCSS.WfpCallout Driver Service..." -ForegroundColor Cyan
$svcName = "DCSS.WfpCallout"
$existingSvc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Host "[INFO] Stopping existing service instance..."
    sc.exe stop $svcName | Out-Null
    Start-Sleep -Seconds 1
    sc.exe delete $svcName | Out-Null
    Start-Sleep -Seconds 1
}

$createOut = sc.exe create $svcName type= kernel binPath= "$sysPath" DisplayName= "DC-ScreenSharing WFP Callout Driver"
Write-Host ($createOut -join "`n")

# 3. Start Driver Service
Write-Host ""
Write-Host "[3/4] Starting DCSS.WfpCallout Driver..." -ForegroundColor Cyan
$startOut = sc.exe start $svcName
Write-Host ($startOut -join "`n")

Start-Sleep -Seconds 1
$svcStatus = Get-Service -Name $svcName -ErrorAction SilentlyContinue

# 4. Check Status & Event Logs
Write-Host ""
Write-Host "[4/4] Verifying Driver State..." -ForegroundColor Cyan
if ($svcStatus) {
    Write-Host "Service Name   : $($svcStatus.Name)"
    Write-Host "Status         : $($svcStatus.Status)"
    Write-Host "ServiceType    : $($svcStatus.ServiceType)"
} else {
    Write-Host "Service status : Not Found"
}

# Capture any Windows CodeIntegrity or SCM errors
Write-Host ""
Write-Host "System Event Log entries (past 2 minutes):" -ForegroundColor Cyan
$events = Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddMinutes(-2)} -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like "*DCSS*" -or $_.ProviderName -like "*Service Control Manager*" -or $_.ProviderName -like "*CodeIntegrity*" } |
    Select-Object -First 8

foreach ($evt in $events) {
    Write-Host "[$($evt.TimeCreated.ToString('HH:mm:ss'))] [$($evt.ProviderName)] $($evt.Message)"
}

Write-Host ""
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  Driver Load Runner Finished." -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "Press Enter to exit..."
[void][System.Console]::ReadLine()
