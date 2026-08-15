@echo off
setlocal
cd /d "%~dp0"
if not exist ".\release\RobloxAltClient.exe" (
  call ".\build-client.cmd"
  if errorlevel 1 exit /b 1
)
start "" ".\release\RobloxAltClient.exe"
