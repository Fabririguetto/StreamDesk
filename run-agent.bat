@echo off
title StreamDesk Agent - Receptor KVM (Notebook)
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk Agent - Receptor KVM (0%% CPU)
echo ========================================================
echo.
echo [Energia] Configurando accion al cerrar tapa (No suspender con corriente)...
powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0 >nul 2>&1
powercfg /setactive SCHEME_CURRENT >nul 2>&1
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
