@echo off
REM === Normal pack ===
REM Packs the build already in bin\Release into Releases\immersivewoodworking_<version>.zip.
REM Refuses if a zip for this version already exists (use pack-release-force.bat to overwrite).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0pack-release.ps1"
echo.
pause
