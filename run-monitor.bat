@echo off
title StreamDesk Monitor - Visor de Pantalla y KVM
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk Monitor - Visor de Capturadora y KVM
echo ========================================================
echo.

if exist "publish\Monitor\StreamDesk.Monitor.exe" (
    echo [Info] Ejecutando binario optimizado de StreamDesk Monitor...
    start "" "publish\Monitor\StreamDesk.Monitor.exe" %*
) else (
    echo [Info] Ejecutando via dotnet run...
    dotnet run --project StreamDesk.Monitor/StreamDesk.Monitor.csproj -c Release %*
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La aplicacion se cerro con codigo de error: %ERRORLEVEL%
    pause
)
