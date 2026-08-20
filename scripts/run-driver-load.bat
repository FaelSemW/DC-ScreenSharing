@echo off
setlocal
cd /d "%~dp0"
echo ===============================================================================
echo   Launching DCSS WFP Driver Load Runner (Admin)...
echo ===============================================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-driver-load.ps1"
echo.
pause
