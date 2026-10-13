@echo off
title StreamDesk - Compilar Paquetes Portables
cd /d "%~dp0"
echo ========================================================
echo     StreamDesk - Publicacion de Binarios Standalone
echo ========================================================
echo Compilando paquetes autocontenidos win-x64...
echo.

echo [1/2] Publicando StreamDesk.Sender...
dotnet publish StreamDesk.Sender/StreamDesk.Sender.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\Sender
if %ERRORLEVEL% NEQ 0 (
    echo Error publicando Sender.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo [2/2] Publicando StreamDesk.Receiver...
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
echo     - publish\Sender\StreamDesk.Sender.exe (Notebook)
echo     - publish\Receiver\StreamDesk.Receiver.exe (PC Receptor)
echo ========================================================
echo.
pause
