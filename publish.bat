@echo off
title StreamDesk - Compilar Paquetes Portables
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk - Publicacion de Binarios Standalone
echo ========================================================
echo Compilando paquetes autocontenidos win-x64...
echo.

echo [1/4] Publicando StreamDesk.Monitor (Visor de Pantalla & KVM)...
dotnet publish StreamDesk.Monitor/StreamDesk.Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Monitor
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Monitor.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/4] Publicando StreamDesk.Agent (Receptor KVM 0%% CPU)...
dotnet publish StreamDesk.Agent/StreamDesk.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Agent
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Agent.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [3/4] Publicando StreamDesk.Sender...
dotnet publish StreamDesk.Sender/StreamDesk.Sender.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Sender
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Sender.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [4/4] Publicando StreamDesk.Receiver...
dotnet publish StreamDesk.Receiver/StreamDesk.Receiver.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Receiver
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Receiver.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================================
echo     COMPILACION COMPLETADA EXITOSAMENTE!
echo     Carpetas generadas:
echo     - publish\Monitor\StreamDesk.Monitor.exe (PC Monitor)
echo     - publish\Agent\StreamDesk.Agent.exe     (Notebook KVM 0%% CPU)
echo     - publish\Sender\StreamDesk.Sender.exe   (Streaming LAN)
echo     - publish\Receiver\StreamDesk.Receiver.exe
echo ========================================================
echo.
pause
