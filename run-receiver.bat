@echo off
title StreamDesk Receiver
cd /d "%~dp0"
echo ===================================================
echo     Iniciando StreamDesk Receiver (Desktop PC)
echo ===================================================

if exist "publish\Receiver\StreamDesk.Receiver.exe" (
    echo [Info] Ejecutando binario optimizado de StreamDesk Receiver...
    "publish\Receiver\StreamDesk.Receiver.exe" %*
) else (
    echo [Info] Ejecutando via dotnet run...
    dotnet run --project StreamDesk.Receiver/StreamDesk.Receiver.csproj -c Release %*
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La aplicacion se cerro con codigo de error: %ERRORLEVEL%
)

echo.
echo Presiona cualquier tecla para cerrar esta ventana...
pause >nul
