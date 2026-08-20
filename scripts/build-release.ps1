param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.3",
    [int]$StageTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" }

$publishDir = Join-Path $repoRoot "dist\publish"
$installerDir = Join-Path $repoRoot "dist\installer"
$maintainerDir = Join-Path $repoRoot "dist\maintainer"
$collectorDir = Join-Path $repoRoot "dist\profile-collector"

function Run-StepWithTimeout {
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
    $psi.Arguments = [string]::Join(" ", $ArgumentList)
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi

    $stdOutBuilder = New-Object System.Text.StringBuilder
    $stdErrBuilder = New-Object System.Text.StringBuilder

    $outHandler = {
        if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
            $stdOutBuilder.AppendLine($EventArgs.Data) | Out-Null
        }
    }
    $errHandler = {
        if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
            $stdErrBuilder.AppendLine($EventArgs.Data) | Out-Null
        }
    }

    $outEvent = Register-ObjectEvent -InputObject $proc -EventName "OutputDataReceived" -Action $outHandler
    $errEvent = Register-ObjectEvent -InputObject $proc -EventName "ErrorDataReceived" -Action $errHandler

    try {
        if (-not $proc.Start()) {
            throw "Failed to start process: $FilePath"
        }

        $proc.BeginOutputReadLine()
        $proc.BeginErrorReadLine()

        $completed = $proc.WaitForExit($TimeoutSec * 1000)
        if (-not $completed) {
            try { $proc.Kill($true) } catch { }
            throw "STAGE TIMEOUT: '$StageName' exceeded maximum timeout of $TimeoutSec seconds. Process was terminated."
        }

        $proc.WaitForExit()
        $sw.Stop()

        if ($proc.ExitCode -ne 0) {
            $errText = $stdErrBuilder.ToString()
            $outText = $stdOutBuilder.ToString()
            throw "STAGE FAILED: '$StageName' exited with code $($proc.ExitCode).`nSTDERR: $errText`nSTDOUT: $outText"
        }

        Write-Host ">>> COMPLETE $StageName [Duration: $($sw.Elapsed.TotalSeconds.ToString('F1'))s]" -ForegroundColor Green
        return @{
            ExitCode = $proc.ExitCode
            Duration = $sw.Elapsed
            StdOut = $stdOutBuilder.ToString()
            StdErr = $stdErrBuilder.ToString()
        }
    }
    finally {
        Unregister-Event -SourceIdentifier $outEvent.Name -ErrorAction SilentlyContinue
        Unregister-Event -SourceIdentifier $errEvent.Name -ErrorAction SilentlyContinue
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
Run-StepWithTimeout -StageName "[2/7] Build Solution" `
    -FilePath $dotnet `
    -ArgumentList @("build", "`"$repoRoot\DC-ScreenSharing.sln`"", "-c", $Configuration) `
    -TimeoutSec 180

# [3/7] Run Automated Tests
Run-StepWithTimeout -StageName "[3/7] Run Automated Tests" `
    -FilePath $dotnet `
    -ArgumentList @("test", "`"$repoRoot\tests\DC-ScreenSharing.IntegrationTests\DC-ScreenSharing.IntegrationTests.csproj`"", "-c", $Configuration) `
    -TimeoutSec 120

# [4/7] Publish Applications (App, NetworkService, Updater)
Run-StepWithTimeout -StageName "[4/7a] Publish DC-ScreenSharing.App" `
    -FilePath $dotnet `
    -ArgumentList @("publish", "`"$repoRoot\src\DC-ScreenSharing.App\DC-ScreenSharing.App.csproj`"", "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", "`"$publishDir`"") `
    -TimeoutSec 180

Run-StepWithTimeout -StageName "[4/7b] Publish DCSS.NetworkService" `
    -FilePath $dotnet `
    -ArgumentList @("publish", "`"$repoRoot\src\DC-ScreenSharing.NetworkService\DC-ScreenSharing.NetworkService.csproj`"", "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-o", "`"$publishDir`"") `
    -TimeoutSec 180

if (Test-Path "$publishDir\DC-ScreenSharing.NetworkService.exe") {
    Move-Item "$publishDir\DC-ScreenSharing.NetworkService.exe" "$publishDir\DCSS.NetworkService.exe" -Force
}

Run-StepWithTimeout -StageName "[4/7c] Publish DC-ScreenSharing.Updater" `
    -FilePath $dotnet `
    -ArgumentList @("publish", "`"$repoRoot\src\DC-ScreenSharing.Updater\DC-ScreenSharing.Updater.csproj`"", "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-o", "`"$publishDir`"") `
    -TimeoutSec 180

# [5/7] Publish Tools (Maintainer, ProfileCollector)
Run-StepWithTimeout -StageName "[5/7a] Publish DCSS.Maintainer" `
    -FilePath $dotnet `
    -ArgumentList @("publish", "`"$repoRoot\tools\DCSS.Maintainer\DCSS.Maintainer.csproj`"", "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", "`"$maintainerDir`"") `
    -TimeoutSec 180

Run-StepWithTimeout -StageName "[5/7b] Publish DCSS.ProfileCollector" `
    -FilePath $dotnet `
    -ArgumentList @("publish", "`"$repoRoot\tools\DCSS.ProfileCollector\DCSS.ProfileCollector.csproj`"", "-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", "`"$collectorDir`"") `
    -TimeoutSec 180

# Deploy native runtime files
$nativeTarget = Join-Path $publishDir "native"
New-Item -ItemType Directory -Force -Path $nativeTarget | Out-Null
$nativeSource = Join-Path $repoRoot "runtimes\win-x64\native"
if (Test-Path $nativeSource) {
    Copy-Item "$nativeSource\*" $nativeTarget -Force
}

# Verify runtime dependencies
$requiredFiles = @(
    "$publishDir\DC-ScreenSharing.exe",
    "$publishDir\DCSS.NetworkService.exe",
    "$publishDir\DC-ScreenSharing.Updater.exe",
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
    $isccRes = Run-StepWithTimeout -StageName "[6/7] Compile Installer (Inno Setup)" `
        -FilePath $iscc `
        -ArgumentList @("/DMyAppVersion=$Version", "`"$repoRoot\installer\DC-ScreenSharing.iss`"") `
        -TimeoutSec 240
    $isccExitCode = $isccRes.ExitCode
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
