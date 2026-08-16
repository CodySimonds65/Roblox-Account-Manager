@echo off
setlocal
cd /d "%~dp0"
if not exist ".\release\RobloxAccountManager.exe" (
  call ".\build-client.cmd"
  if errorlevel 1 exit /b 1
)
start "" ".\release\RobloxAccountManager.exe"
