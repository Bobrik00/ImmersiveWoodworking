@echo off
REM === Build + pack ===
REM Compiles a fresh Release build (dotnet build -c Release), then packs it.
REM Add -Force at the end of the powershell line below if you also need to overwrite an
REM existing zip for the same version.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-release.ps1" -Build
echo.
pause
