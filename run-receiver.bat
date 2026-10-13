@echo off
title StreamDesk Receiver
cd /d "%~dp0"
echo ===================================================
echo     Iniciando StreamDesk Receiver (Desktop PC)
echo ===================================================
dotnet run --project StreamDesk.Receiver/StreamDesk.Receiver.csproj -c Release %*
pause
