@echo off
title StreamDesk Sender
cd /d "%~dp0"
echo ===================================================
echo     Iniciando StreamDesk Sender (Notebook)
echo ===================================================
dotnet run --project StreamDesk.Sender/StreamDesk.Sender.csproj -c Release %*
pause
