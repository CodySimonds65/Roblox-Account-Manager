@echo off
setlocal
cd /d "%~dp0"
echo Building self-contained Roblox Alt Client...
dotnet publish ".\client\RobloxAltClient.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ".\release"
if errorlevel 1 (
  echo.
  echo Build failed. Review the messages above.
  pause
  exit /b 1
)
echo.
echo Build complete: %~dp0release\RobloxAltClient.exe
pause
