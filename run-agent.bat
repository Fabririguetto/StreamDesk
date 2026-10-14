@echo off
title StreamDesk Agent - Receptor KVM (Notebook)
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk Agent - Receptor KVM (0%% CPU)
echo ========================================================
echo.

if exist "publish\Agent\StreamDesk.Agent.exe" (
    echo [Info] Ejecutando binario optimizado de StreamDesk Agent...
    "publish\Agent\StreamDesk.Agent.exe" %*
) else (
    echo [Info] Ejecutando via dotnet run...
    dotnet run --project StreamDesk.Agent/StreamDesk.Agent.csproj -c Release %*
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La aplicacion se cerro con codigo de error: %ERRORLEVEL%
    pause
)
