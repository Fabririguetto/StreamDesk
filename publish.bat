@echo off
title StreamDesk - Compilar Paquetes Portables
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk - Publicacion de Binarios Standalone
echo ========================================================
echo Compilando paquetes autocontenidos win-x64...
echo.

echo Cerrando instancias previas si estuvieran en ejecucion...
taskkill /f /im StreamDesk.Monitor.exe >nul 2>&1
taskkill /f /im StreamDesk.Agent.exe >nul 2>&1

echo.
echo [1/2] Publicando StreamDesk.Monitor (Visor de Pantalla y KVM)...
dotnet publish StreamDesk.Monitor/StreamDesk.Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Monitor
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Monitor.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Publicando StreamDesk.Agent (Receptor KVM 0%% CPU)...
dotnet publish StreamDesk.Agent/StreamDesk.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Agent
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Agent.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================================
echo     COMPILACION COMPLETADA EXITOSAMENTE!
echo     Carpetas generadas:
echo     - publish\Monitor\StreamDesk.Monitor.exe (PC de Escritorio con Capturadora)
echo     - publish\Agent\StreamDesk.Agent.exe     (Notebook con Salida HDMI)
echo ========================================================
echo.
pause
