@echo off
REM === Force pack (overwrite) ===
REM Same as pack-release.bat, but overwrites an existing zip for this version.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-release.ps1" -Build -Force
echo.
pause
