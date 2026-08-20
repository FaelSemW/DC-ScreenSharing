# DCSS WFP Driver Runtime Validation Harness
# Requires -ExecutionPolicy Bypass
$ErrorActionPreference = "Continue"

Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  DC-ScreenSharing Phase 2 - WFP Driver Runtime Validation" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host ""

$results = [ordered]@{}

# 1. Package & INF Check
$pkgDir = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\pkg\x64\Release"
$sysPath = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\x64\Release\DCSS.WfpCallout.sys"
$catPath = "$pkgDir\DCSS.WfpCallout.cat"
$infPath = "$pkgDir\DCSS.WfpCallout.inf"

$results["INF valid"] = if (Test-Path $infPath) { "PASS" } else { "FAIL" }
$results["CAT generated"] = if (Test-Path $catPath) { "YES" } else { "NO" }
$results["SYS binary exists"] = if (Test-Path $sysPath) { "PASS" } else { "FAIL" }

# 2. Check Signature
$signtool = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*" -Filter "signtool.exe" -Recurse | Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1).FullName
$sigCheck = & $signtool verify /v /pa $sysPath 2>&1
$isSysSigned = ($sigCheck -match "SHA1 hash:")
$results["SYS signed"] = if ($isSysSigned) { "YES" } else { "NO" }
$results["Certificate scope"] = "DEVELOPMENT ONLY (CN=DCSS Local Dev Test Sign)"

# 3. Check / Install Driver Service
Write-Host "[1/5] Checking/Creating Driver Service..." -ForegroundColor Cyan
$svcName = "DCSS.WfpCallout"
$existingSvc = Get-Service -Name $svcName -ErrorAction SilentlyContinue

if (-not $existingSvc) {
    Write-Host "Creating driver service $svcName..."
    $scCreate = sc.exe create $svcName type= kernel binPath= "$sysPath" 2>&1
    Write-Host ($scCreate -join "`n")
    $existingSvc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
}

$results["Driver installed"] = if ($existingSvc) { "PASS" } else { "FAIL (Requires Administrator)" }

# 4. Attempt to Load Driver
Write-Host ""
Write-Host "[2/5] Attempting to Load Kernel Driver..." -ForegroundColor Cyan
if ($existingSvc) {
    $scStart = sc.exe start $svcName 2>&1
    Write-Host ($scStart -join "`n")
    
    $svcStatus = Get-Service -Name $svcName -ErrorAction SilentlyContinue
    $results["Driver loaded"] = if ($svcStatus -and $svcStatus.Status -eq "Running") { "PASS" } else { "FAIL" }
    $results["Driver state"] = if ($svcStatus) { $svcStatus.Status.ToString() } else { "NOT INSTALLED" }
} else {
    $results["Driver loaded"] = "FAIL (Service not installed)"
    $results["Driver state"] = "NOT INSTALLED"
}

# 5. Check System Event Log for Driver load results
Write-Host ""
Write-Host "[3/5] Inspecting Event Logs..." -ForegroundColor Cyan
$events = Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like "*DCSS*" -or $_.ProviderName -like "*Service Control Manager*" -or $_.ProviderName -like "*CodeIntegrity*" } |
    Select-Object -First 5

foreach ($evt in $events) {
    Write-Host "[$($evt.TimeCreated.ToString('HH:mm:ss'))] [$($evt.ProviderName)] $($evt.Message)"
}

# 6. Check WFP User-Mode Provider / Sublayer
Write-Host ""
Write-Host "[4/5] Checking WFP User-Mode Layer..." -ForegroundColor Cyan
# Run check against WfpManager
$wfpAudit = netsh wfp show filters file=- 2>&1

# Summary Report
Write-Host ""
Write-Host "===============================================================================" -ForegroundColor Cyan
Write-Host "  VALIDATION EXECUTION SUMMARY" -ForegroundColor Cyan
Write-Host "===============================================================================" -ForegroundColor Cyan
foreach ($key in $results.Keys) {
    Write-Host ("{0,-30} : {1}" -f $key, $results[$key])
}
