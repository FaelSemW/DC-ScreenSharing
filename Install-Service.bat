@echo off
setlocal EnableDelayedExpansion

:: Check for administrative privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo ========================================================
    echo Soliciting Administrator Privileges...
    echo ========================================================
    powershell -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo ========================================================
echo Installing DC-ScreenSharing Network Service...
echo ========================================================

:: Ensure destination folder exists
if not exist "C:\Program Files\DC-ScreenSharing" mkdir "C:\Program Files\DC-ScreenSharing"
if not exist "C:\Program Files\DC-ScreenSharing\native" mkdir "C:\Program Files\DC-ScreenSharing\native"

:: Stop existing service if running
echo Stopping any existing service...
sc.exe stop DCSS.NetworkService >nul 2>&1
timeout /t 1 /nobreak >nul

:: Copy all files
echo Copying files to C:\Program Files\DC-ScreenSharing...
xcopy /E /Y /I "%~dp0dist\publish\*" "C:\Program Files\DC-ScreenSharing\"

:: Register service in SCM
echo Registering DCSS.NetworkService...
sc.exe create DCSS.NetworkService binPath= "C:\Program Files\DC-ScreenSharing\DCSS.NetworkService.exe" start= auto displayname= "DC-ScreenSharing Network Service"
sc.exe config DCSS.NetworkService binPath= "C:\Program Files\DC-ScreenSharing\DCSS.NetworkService.exe" start= auto
sc.exe description DCSS.NetworkService "Provides privileged application-specific routing for DC-ScreenSharing."

:: Start service
echo Starting DCSS.NetworkService...
sc.exe start DCSS.NetworkService

echo.
echo ========================================================
echo Status do Servico:
sc.exe query DCSS.NetworkService
echo ========================================================
echo Instalacao concluida com sucesso!
echo ========================================================
pause
