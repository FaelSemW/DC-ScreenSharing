param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

Write-Host "=================================================="
Write-Host "DCSS WFP Kernel Callout Driver Build Pipeline"
Write-Host "Configuration: $Configuration | Platform: $Platform"
Write-Host "=================================================="

# 1. Locate vswhere / MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    $vswhere = "vswhere.exe"
}

$vsPath = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
if (-not $vsPath -or -not (Test-Path $vsPath)) {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools",
        "C:\Program Files\Microsoft Visual Studio\2022\Community",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $vsPath = $cand
            break
        }
    }
}

if (-not $vsPath) {
    Write-Error "Visual Studio / Build Tools 2022 not found. Please run scripts\install-driver-toolchain.bat first."
}

$msBuildExe = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msBuildExe)) {
    $msBuildExe = Join-Path $vsPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
}

if (-not (Test-Path $msBuildExe)) {
    Write-Error "MSBuild.exe not found at $msBuildExe"
}

Write-Host "[OK] Using MSBuild: $msBuildExe"
Write-Host "[OK] VS Installation: $vsPath"

# 2. Build Driver Project
$projectPath = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\DCSS.WfpCallout.vcxproj"
Write-Host "Building $projectPath ($Configuration|$Platform)..."

$buildArgs = @(
    $projectPath,
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/p:TargetVersion=Windows10",
    "/p:SignMode=Off",
    "/p:EnableInf2Cat=false",
    "/m",
    "/v:m"
)

& $msBuildExe $buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "MSBuild failed with exit code $LASTEXITCODE"
}

# 3. Locate & Verify SYS binary
$candidates = @(
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\$Platform\$Configuration\DCSS.WfpCallout.sys",
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\bin\$Platform\$Configuration\DCSS.WfpCallout.sys",
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\x64\$Configuration\DCSS.WfpCallout.sys"
)

$sysFile = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sysFile -or -not (Test-Path $sysFile)) {
    Write-Error "Driver binary was not produced at expected path."
}

$item = Get-Item $sysFile
Write-Host "=================================================="
Write-Host "DRIVER SYS COMPILED: $sysFile ($($item.Length) bytes)"
Write-Host "=================================================="

# 4. Stage driver package directory
$pkgDir = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\pkg\$Platform\$Configuration"
New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null
Copy-Item $sysFile (Join-Path $pkgDir "DCSS.WfpCallout.sys") -Force

# Stage and stamp INF
$rawInf = "D:\DC-ScreenSharing\native\DCSS.WfpCallout\DCSS.WfpCallout.inf"
$stagedInf = Join-Path $pkgDir "DCSS.WfpCallout.inf"
$infContent = Get-Content $rawInf -Raw
$infContent = $infContent.Replace('$ARCH$', 'amd64')
Set-Content -Path $stagedInf -Value $infContent -Encoding Ascii

# 5. Run InfVerif
$infverif = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Tools\*" -Filter "infverif.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1).FullName
if ($infverif -and (Test-Path $infverif)) {
    Write-Host "Running InfVerif validation on $stagedInf..."
    $infVerifOut = & $infverif /v /w $stagedInf 2>&1
    Write-Host ($infVerifOut -join "`n")
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "InfVerif reported issues (exit code $LASTEXITCODE)."
    } else {
        Write-Host "[OK] InfVerif passed with 0 errors." -ForegroundColor Green
    }
}

# 6. Run Inf2Cat
$inf2cat = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*" -Filter "Inf2Cat.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
if ($inf2cat -and (Test-Path $inf2cat)) {
    Write-Host "Generating Catalog (.cat) with Inf2Cat..."
    $inf2catOut = & $inf2cat /driver:$pkgDir /os:10_X64,Server10_X64,10_CO_X64,10_NI_X64 2>&1
    Write-Host ($inf2catOut -join "`n")
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Inf2Cat reported errors (exit code $LASTEXITCODE)."
    } else {
        Write-Host "[OK] Catalog file generated: $pkgDir\DCSS.WfpCallout.cat" -ForegroundColor Green
    }
}

# 7. Local Dev Test Signing (if certificate present)
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like "*DCSS Local Dev Test Sign*" } | Select-Object -First 1
if (-not $cert) {
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*DCSS Local Dev Test Sign*" } | Select-Object -First 1
}
if ($cert) {
    $signtool = (Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1).FullName
    if ($signtool -and (Test-Path $signtool)) {
        Write-Host "Signing driver SYS and CAT with local test certificate ($($cert.Thumbprint))..."
        & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $sysFile
        $stagedSys = Join-Path $pkgDir "DCSS.WfpCallout.sys"
        & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $stagedSys
        $catFile = Join-Path $pkgDir "DCSS.WfpCallout.cat"
        if (Test-Path $catFile) {
            & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $catFile
        }
        Write-Host "[OK] Driver and package test-signed successfully." -ForegroundColor Green
    }
}

Write-Host "=================================================="
Write-Host "DRIVER BUILD COMPLETE"
Write-Host "SYS: $sysFile"
Write-Host "PKG: $pkgDir"
Write-Host "=================================================="
