@echo off
REM ============================================================================
REM  Dev build + run for ImmersiveWoodworking.
REM
REM  Regenerates ALL player-animation patches from the latest Blender exports,
REM  THEN compiles the mod (Release + IwDebug) and launches the client.
REM
REM  Why all, and why this order:
REM    Each apply-*.ps1 rewrites a patch under assets\immersivewoodworking\patches\.
REM    The dotnet build copies assets\** into the run output (csproj Content,
REM    CopyToOutputDirectory=PreserveNewest). So every patch MUST be regenerated
REM    BEFORE the build step, or the client launches a STALE animation. The bug
REM    this fixes: the batch used to regenerate only woodchop, so re-exported
REM    sawing keyframes never reached the running game. Same bug bit driftwood-break
REM    (E07, the flotsam hand-snap): its apply-*.ps1 existed but was never wired in
REM    here, so a re-export of that gesture never reached the running client either.
REM
REM    1) woodchop        -> apply-animation.ps1                 (patch only)
REM    2) sawing          -> apply-sawing-animation.ps1          (patch only)
REM    3) driftwood-break -> apply-driftwood-break-animation.ps1 (patch only)
REM    4) debarking       -> apply-debarking-animation.ps1       (patch + build + launch)
REM
REM  Sawdust particle points (SawdustPoints.cs) are NOT regenerated here on purpose: the csproj target
REM  GenerateSawdustPoints re-runs tools\apply-sawdust-points.ps1 on every build, so editing the point
REM  values in that .ps1 reaches the build via the final step's compile automatically - no step to add here.
REM
REM  The LAST step owns the build/launch (debarking is the animation under active work), so its
REM  frame diagnostics print last, right before the client comes up. The build-owner is always whichever
REM  animation is under active work: it must run last, after every patch has been regenerated. (Sawing was
REM  the previous build-owner and is now patch-only; promote a different apply-*.ps1 to -Build -Run when the
REM  active work moves on.)
REM
REM  Double-click this file, or run it from cmd. Extra args are forwarded to the
REM  debarking script, e.g.:  test.bat -Source "C:\other\debarking_export.json"
REM ============================================================================

echo [1/4] Regenerating woodchop animation patch...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-animation.ps1"
if errorlevel 1 (
    echo.
    echo !! FAILED to regenerate the woodchop patch - aborting before compile.
    pause
    exit /b 1
)

echo.
echo [2/4] Regenerating sawing animation patch...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-sawing-animation.ps1"
if errorlevel 1 (
    echo.
    echo !! FAILED to regenerate the sawing patch - aborting before compile.
    pause
    exit /b 1
)

echo.
echo [3/4] Regenerating driftwood-break animation patch...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-driftwood-break-animation.ps1"
if errorlevel 1 (
    echo.
    echo !! FAILED to regenerate the driftwood-break patch - aborting before compile.
    pause
    exit /b 1
)

echo.
echo [4/4] Regenerating debarking patch, then building + launching client...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-debarking-animation.ps1" -Build -Run %*
if errorlevel 1 (
    echo.
    echo !! Debarking patch / build / launch FAILED.
    pause
    exit /b 1
)

pause
