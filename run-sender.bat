@echo off
title StreamDesk Sender
cd /d "%~dp0"
echo ===================================================
echo     Iniciando StreamDesk Sender (Notebook)
echo ===================================================

if exist "publish\Sender\StreamDesk.Sender.exe" (
    echo [Info] Ejecutando binario optimizado de StreamDesk Sender...
    "publish\Sender\StreamDesk.Sender.exe" %*
) else (
    echo [Info] Ejecutando via dotnet run...
    dotnet run --project StreamDesk.Sender/StreamDesk.Sender.csproj -c Release %*
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] La aplicacion se cerro con codigo de error: %ERRORLEVEL%
)

echo.
echo Presiona cualquier tecla para cerrar esta ventana...
pause >nul
