@echo off
setlocal
cd /d "%~dp0"
echo ===============================================================================
echo   Launching DC-ScreenSharing Driver Toolchain Installer...
echo ===============================================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-driver-toolchain.ps1"
echo.
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Installation script returned code: %ERRORLEVEL%
)
echo.
pause
