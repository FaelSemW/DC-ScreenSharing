param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.1"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" }

$publishDir = Join-Path $repoRoot "dist\publish"
$installerDir = Join-Path $repoRoot "dist\installer"

Write-Host "=== Building DC-ScreenSharing v$Version ($Configuration) ===" -ForegroundColor Cyan

# Clean distribution directories
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

# 1. Publish Main WPF App
Write-Host "Publishing DC-ScreenSharing.App (win-x64 self-contained)..." -ForegroundColor Yellow
& $dotnet publish "$repoRoot\src\DC-ScreenSharing.App\DC-ScreenSharing.App.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish DC-ScreenSharing.App" }

if (-not (Test-Path "$publishDir\DC-ScreenSharing.exe")) {
    throw "Critical: DC-ScreenSharing.exe was not generated in $publishDir!"
}

# 2. Publish NetworkService
Write-Host "Publishing DCSS.NetworkService..." -ForegroundColor Yellow
& $dotnet publish "$repoRoot\src\DC-ScreenSharing.NetworkService\DC-ScreenSharing.NetworkService.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish DCSS.NetworkService" }

if (Test-Path "$publishDir\DC-ScreenSharing.NetworkService.exe") {
    Move-Item "$publishDir\DC-ScreenSharing.NetworkService.exe" "$publishDir\DCSS.NetworkService.exe" -Force
}

if (-not (Test-Path "$publishDir\DCSS.NetworkService.exe")) {
    throw "Critical: DCSS.NetworkService.exe was not generated in $publishDir!"
}

# 3. Publish Updater
Write-Host "Publishing DC-ScreenSharing.Updater..." -ForegroundColor Yellow
& $dotnet publish "$repoRoot\src\DC-ScreenSharing.Updater\DC-ScreenSharing.Updater.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish DC-ScreenSharing.Updater" }

# 4. Copy Native Engine & Wintun Driver
Write-Host "Deploying native engine dependencies..." -ForegroundColor Yellow
$nativeTarget = Join-Path $publishDir "native"
New-Item -ItemType Directory -Force -Path $nativeTarget | Out-Null

$nativeSource = Join-Path $repoRoot "runtimes\win-x64\native"
if (Test-Path $nativeSource) {
    Copy-Item "$nativeSource\*" $nativeTarget -Force
}

# Verify critical files before building installer
$requiredFiles = @(
    "$publishDir\DC-ScreenSharing.exe",
    "$publishDir\DCSS.NetworkService.exe",
    "$publishDir\native\dcss-engine.exe",
    "$publishDir\native\wintun.dll"
)
foreach ($req in $requiredFiles) {
    if (-not (Test-Path $req)) {
        throw "Missing required packaged file: $req"
    }
}

Write-Host "All required runtime files verified in staging directory." -ForegroundColor Green

# 5. Compile Inno Setup Installer
if (Test-Path $iscc) {
    Write-Host "Compiling single installer executable with Inno Setup..." -ForegroundColor Green
    & $iscc "/DMyAppVersion=$Version" "$repoRoot\installer\DC-ScreenSharing.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }
    
    $setupExe = Join-Path $installerDir "DC-ScreenSharing-Setup-$Version.exe"
    if (Test-Path $setupExe) {
        $hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumContent = "$hash  DC-ScreenSharing-Setup-$Version.exe"
        Set-Content -Path (Join-Path $installerDir "SHA256SUMS.txt") -Value $checksumContent
        Set-Content -Path (Join-Path $installerDir "DC-ScreenSharing-Setup-$Version.exe.sha256") -Value $checksumContent
        Write-Host "==================================================" -ForegroundColor Green
        Write-Host "Installer built successfully: $setupExe" -ForegroundColor Green
        Write-Host "SHA256: $hash" -ForegroundColor Cyan
        Write-Host "==================================================" -ForegroundColor Green
    }
} else {
    Write-Warning "Inno Setup compiler (ISCC.exe) not found. Published files are in $publishDir."
}

Write-Host "=== Build Complete ===" -ForegroundColor Cyan
