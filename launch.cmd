@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch-RobloxAlts.ps1" %*
echo.
echo Launcher process finished. Press any key to close this window.
pause >nul
