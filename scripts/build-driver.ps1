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
    # Fallback to standard locations
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

# 2. Check WDK Targets
$wdkTargets = "${env:ProgramFiles(x86)}\Windows Kits\10\build\WindowsDriver.Default.props"
if (-not (Test-Path $wdkTargets)) {
    Write-Warning "WDK MSBuild props not found at standard path: $wdkTargets. Build may fail if WDK is not integrated with MSBuild."
} else {
    Write-Host "[OK] WDK Targets found: $wdkTargets"
}

# 3. Build Driver Project
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

# 4. Verify Output
$candidates = @(
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\$Platform\$Configuration\DCSS.WfpCallout.sys",
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\bin\$Platform\$Configuration\DCSS.WfpCallout.sys",
    "D:\DC-ScreenSharing\native\DCSS.WfpCallout\x64\$Configuration\DCSS.WfpCallout.sys"
)

$sysFile = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (Test-Path $sysFile) {
    $item = Get-Item $sysFile
    Write-Host "=================================================="
    Write-Host "BUILD SUCCESSFUL: $sysFile"
    Write-Host "Size: $($item.Length) bytes"
    Write-Host "Created: $($item.CreationTime)"
    Write-Host "=================================================="

    # 5. Sign driver with test certificate if available
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like "*DCSS Local Dev Test Sign*" } | Select-Object -First 1
    if (-not $cert) {
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like "*DCSS Local Dev Test Sign*" } | Select-Object -First 1
    }
    if ($cert) {
        $signtool = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1).FullName
        if ($signtool -and (Test-Path $signtool)) {
            Write-Host "Signing driver with test certificate ($($cert.Thumbprint))..."
            & $signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $sysFile
            Write-Host "[OK] Driver test signed successfully."
        }
    }
} else {
    Write-Error "Driver binary was not produced at expected path: $sysFile"
}

