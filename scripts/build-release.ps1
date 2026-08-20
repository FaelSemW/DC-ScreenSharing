param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.7",
    [int]$StageTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

$repoRoot = Split-Path -Parent $PSScriptRoot

$dotnetRoot = "$env:LOCALAPPDATA\Microsoft\dotnet"
if (Test-Path $dotnetRoot) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
    $dotnet = Join-Path $dotnetRoot "dotnet.exe"
} else {
    $dotnet = "dotnet"
}

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" }

$publishDir = Join-Path $repoRoot "dist\publish"
$installerDir = Join-Path $repoRoot "dist\installer"
$maintainerDir = Join-Path $repoRoot "dist\maintainer"
$collectorDir = Join-Path $repoRoot "dist\profile-collector"

function Invoke-StepWithTimeout {
    param(
        [string]$StageName,
        [string]$FilePath,
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = $repoRoot,
        [int]$TimeoutSec = $StageTimeoutSeconds
    )

    Write-Host ">>> START $StageName" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $formattedArgs = ($ArgumentList | ForEach-Object {
        $a = $_.ToString()
        if ($a -match '\s' -and -not ($a.StartsWith('`"') -and $a.EndsWith('`"'))) {
            '`"' + $a + '`"'
        } else {
            $a
        }
    }) -join ' '

    $psi.Arguments = $formattedArgs
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    if ($env:DOTNET_ROOT) {
        $psi.EnvironmentVariables["DOTNET_ROOT"] = $env:DOTNET_ROOT
        $psi.EnvironmentVariables["PATH"] = "$($env:DOTNET_ROOT);$($env:PATH)"
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    try {
        if (-not $proc.Start()) {
            throw "Failed to start process: $FilePath"
        }

        $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        $completed = $proc.WaitForExit($TimeoutSec * 1000)
        if (-not $completed) {
            try { $proc.Kill($true) } catch { }
            throw "STAGE TIMEOUT: '$StageName' exceeded maximum timeout of $TimeoutSec seconds. Process was terminated."
        }

        $proc.WaitForExit()
        $sw.Stop()

        $outText = $stdoutTask.GetAwaiter().GetResult()
        $errText = $stderrTask.GetAwaiter().GetResult()

        if ($proc.ExitCode -ne 0) {
            throw "STAGE FAILED: '$StageName' exited with code $($proc.ExitCode).`nSTDERR: $errText`nSTDOUT: $outText"
        }

        Write-Host ">>> COMPLETE $StageName [Duration: $($sw.Elapsed.TotalSeconds.ToString('F1'))s]" -ForegroundColor Green
        return @{
            ExitCode = $proc.ExitCode
            Duration = $sw.Elapsed
            StdOut = $outText
            StdErr = $errText
        }
    }
    finally {
        $proc.Dispose()
    }
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "DC-ScreenSharing Release Build Pipeline v$Version ($Configuration)" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# [1/7] Clean
Write-Host ">>> START [1/7] Clean Distribution Directories" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }
if (Test-Path $maintainerDir) { Remove-Item $maintainerDir -Recurse -Force }
if (Test-Path $collectorDir) { Remove-Item $collectorDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
New-Item -ItemType Directory -Force -Path $maintainerDir | Out-Null
New-Item -ItemType Directory -Force -Path $collectorDir | Out-Null
Write-Host ">>> COMPLETE [1/7] Clean Distribution Directories" -ForegroundColor Green

# [2/7] Restore & Build Solution
Invoke-StepWithTimeout -StageName "[2/7] Build Solution" `
    -FilePath $dotnet `
    -ArgumentList @("build", (Join-Path $repoRoot "DC-ScreenSharing.sln"), "-c", $Configuration) `
    -TimeoutSec 180

# [3/7] Run Automated Tests
Invoke-StepWithTimeout -StageName "[3/7a] Run Core Tests" `
    -FilePath $dotnet `
    -ArgumentList @("test", (Join-Path $repoRoot "tests\DC-ScreenSharing.Core.Tests\DC-ScreenSharing.Core.Tests.csproj"), "-c", $Configuration, "--no-build") `
    -TimeoutSec 120

Invoke-StepWithTimeout -StageName "[3/7b] Run Networking Tests" `
    -FilePath $dotnet `
    -ArgumentList @("test", (Join-Path $repoRoot "tests\DC-ScreenSharing.Networking.Tests\DC-ScreenSharing.Networking.Tests.csproj"), "-c", $Configuration, "--no-build") `
    -TimeoutSec 120

Invoke-StepWithTimeout -StageName "[3/7c] Run Integration Tests" `
    -FilePath $dotnet `
    -ArgumentList @("test", (Join-Path $repoRoot "tests\DC-ScreenSharing.IntegrationTests\DC-ScreenSharing.IntegrationTests.csproj"), "-c", $Configuration, "--no-build") `
    -TimeoutSec 120

# [4/7] Publish Applications (App, NetworkService, Updater)
Invoke-StepWithTimeout -StageName "[4/7a] Publish DC-ScreenSharing.App" `
    -FilePath $dotnet `
    -ArgumentList @("publish", (Join-Path $repoRoot "src\DC-ScreenSharing.App\DC-ScreenSharing.App.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", $publishDir) `
    -TimeoutSec 180

Invoke-StepWithTimeout -StageName "[4/7b] Publish DCSS.NetworkService" `
    -FilePath $dotnet `
    -ArgumentList @("publish", (Join-Path $repoRoot "src\DC-ScreenSharing.NetworkService\DC-ScreenSharing.NetworkService.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-o", $publishDir) `
    -TimeoutSec 180

if (Test-Path "$publishDir\DC-ScreenSharing.NetworkService.exe") {
    Move-Item "$publishDir\DC-ScreenSharing.NetworkService.exe" "$publishDir\DCSS.NetworkService.exe" -Force
}

Invoke-StepWithTimeout -StageName "[4/7c] Publish DC-ScreenSharing.Updater" `
    -FilePath $dotnet `
    -ArgumentList @("publish", (Join-Path $repoRoot "src\DC-ScreenSharing.Updater\DC-ScreenSharing.Updater.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-o", $publishDir) `
    -TimeoutSec 180

# [5/7] Publish Tools (Maintainer, ProfileCollector)
Invoke-StepWithTimeout -StageName "[5/7a] Publish DCSS.Maintainer" `
    -FilePath $dotnet `
    -ArgumentList @("publish", (Join-Path $repoRoot "tools\DCSS.Maintainer\DCSS.Maintainer.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $maintainerDir) `
    -TimeoutSec 180

Invoke-StepWithTimeout -StageName "[5/7b] Publish DCSS.ProfileCollector" `
    -FilePath $dotnet `
    -ArgumentList @("publish", (Join-Path $repoRoot "tools\DCSS.ProfileCollector\DCSS.ProfileCollector.csproj"), "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $collectorDir) `
    -TimeoutSec 180

# Deploy native runtime files
$nativeTarget = Join-Path $publishDir "native"
New-Item -ItemType Directory -Force -Path $nativeTarget | Out-Null
$nativeSource = Join-Path $repoRoot "runtimes\win-x64\native"
if (Test-Path $nativeSource) {
    Copy-Item "$nativeSource\*" $nativeTarget -Force
}

# Deploy WFP Driver Package
$driverTarget = Join-Path $publishDir "driver"
New-Item -ItemType Directory -Force -Path $driverTarget | Out-Null

$driverScript = Join-Path $repoRoot "scripts\build-driver.ps1"
if (Test-Path $driverScript) {
    & powershell.exe -ExecutionPolicy Bypass -File $driverScript -Configuration $Configuration -Platform x64
}

$driverSourceDir = Join-Path $repoRoot "native\DCSS.WfpCallout\x64\$Configuration"
$pkgSourceDir = Join-Path $repoRoot "native\DCSS.WfpCallout\pkg\x64\$Configuration"

if (Test-Path "$driverSourceDir\DCSS.WfpCallout.sys") {
    Copy-Item "$driverSourceDir\DCSS.WfpCallout.sys" $driverTarget -Force
}
if (Test-Path "$pkgSourceDir\DCSS.WfpCallout.inf") {
    Copy-Item "$pkgSourceDir\DCSS.WfpCallout.inf" $driverTarget -Force
}
if (Test-Path "$pkgSourceDir\DCSS.WfpCallout.cat") {
    Copy-Item "$pkgSourceDir\DCSS.WfpCallout.cat" $driverTarget -Force
}

# Verify runtime dependencies
$requiredFiles = @(
    "$publishDir\DC-ScreenSharing.exe",
    "$publishDir\DCSS.NetworkService.exe",
    "$publishDir\DC-ScreenSharing.Updater.exe",
    "$publishDir\driver\DCSS.WfpCallout.sys",
    "$publishDir\driver\DCSS.WfpCallout.inf",
    "$publishDir\driver\DCSS.WfpCallout.cat",
    "$publishDir\native\dcss-engine.exe",
    "$publishDir\native\wintun.dll",
    "$maintainerDir\DCSS.Maintainer.exe",
    "$collectorDir\DCSS.ProfileCollector.exe"
)
foreach ($req in $requiredFiles) {
    if (-not (Test-Path $req)) {
        throw "Missing required packaged artifact: $req"
    }
}

# [6/7] Compile Installer with Inno Setup
$isccExitCode = 0
if (Test-Path $iscc) {
    $isccExitCode = (Invoke-StepWithTimeout -StageName "[6/7] Compile Installer (Inno Setup)" `
        -FilePath $iscc `
        -ArgumentList @("/DMyAppVersion=$Version", (Join-Path $repoRoot "installer\DC-ScreenSharing.iss")) `
        -TimeoutSec 240).ExitCode
} else {
    throw "Inno Setup Compiler (ISCC.exe) not found at $iscc"
}

# [7/7] Verify Artifacts & Checksums
Write-Host ">>> START [7/7] Verify Artifacts & Generate SHA-256 Checksums" -ForegroundColor Cyan
$setupExe = Join-Path $installerDir "DC-ScreenSharing-Setup-$Version.exe"
if (-not (Test-Path $setupExe)) {
    throw "Installer binary was not created at $setupExe"
}

$hash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumContent = "$hash  DC-ScreenSharing-Setup-$Version.exe"
Set-Content -Path (Join-Path $installerDir "SHA256SUMS.txt") -Value $checksumContent
Set-Content -Path (Join-Path $installerDir "DC-ScreenSharing-Setup-$Version.exe.sha256") -Value $checksumContent

$setupSize = (Get-Item $setupExe).Length
$setupSizeMB = ($setupSize / 1MB).ToString("F2") + " MB"
$swTotal.Stop()
$totalTimeStr = $swTotal.Elapsed.ToString("mm\:ss")

Write-Host ">>> COMPLETE [7/7] Verify Artifacts" -ForegroundColor Green

Write-Host "==================================================" -ForegroundColor Green
Write-Host "BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "Installer: $setupExe" -ForegroundColor Green
Write-Host "Installer size: $setupSizeMB ($setupSize bytes)" -ForegroundColor Green
Write-Host "SHA-256: $hash" -ForegroundColor Cyan
Write-Host "Inno Setup exit code: $isccExitCode" -ForegroundColor Green
Write-Host "Total build time: $totalTimeStr" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
