@echo off
title StreamDesk - Configurar Pantalla Siempre Activa y Sin Bloqueo
echo ========================================================
echo   StreamDesk - Desactivar Suspension y Bloqueo de Windows
echo ========================================================
echo.
echo [1/3] Configurando tiempo de apagado de pantalla en 'Nunca' (AC y Bateria)...
powercfg /change monitor-timeout-ac 0
powercfg /change monitor-timeout-dc 0

echo [2/3] Configurando tiempo de suspension en 'Nunca' (AC y Bateria)...
powercfg /change standby-timeout-ac 0
powercfg /change standby-timeout-dc 0

echo [3/3] Desactivando protector de pantalla y bloqueo de sesion por inactividad...
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 0 /f >nul 2>&1
reg add "HKCU\Control Panel\Desktop" /v ScreenSaverIsSecure /t REG_SZ /d 0 /f >nul 2>&1

echo.
echo ========================================================
echo  [OK] Pantalla de Windows configurada como SIEMPRE ACTIVA!
echo       Windows ya no se apagara ni bloqueara por inactividad.
echo ========================================================
echo.
pause
