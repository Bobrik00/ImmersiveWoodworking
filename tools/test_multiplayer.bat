@echo off
REM ============================================================================
REM  Dev build + run for ImmersiveWoodworking, MULTIPLAYER variant of test.bat.
REM
REM  Same build pipeline as test.bat (regenerate BOTH animation patches, THEN
REM  compile Release + IwDebug), but instead of launching a single client it
REM  brings up a LOCAL DEDICATED SERVER (mod loaded, joinable) + one client.
REM
REM  Why a dedicated server and not single player: it runs in its OWN process
REM  with its own memory, so client and server are genuinely split. That exposes
REM  the class of MP bugs that single player hides (wrong Api.Side guards, missing
REM  block-entity serialization, packet desync, shared-field corruption). One
REM  player on this server already catches ~90% of the mod's multiplayer issues.
REM
REM  Steps:
REM    1) woodchop -> apply-animation.ps1          (patch only)
REM    2) sawing   -> apply-sawing-animation.ps1   (patch + build, NO launch)
REM    3) server+client -> run-multiplayer.ps1     (seed server config, launch both)
REM
REM  Build details (patch order, sawdust points) are identical to test.bat - see
REM  its header for the full rationale. The patches MUST be regenerated before the
REM  compile, or the build copies a STALE animation into the run output.
REM
REM  Extra args are forwarded to run-multiplayer.ps1, e.g.:
REM    test_multiplayer.bat -Reset       (wipe the test-server world, start fresh)
REM    test_multiplayer.bat -NoClient    (server only - for a friend / 2nd account)
REM    test_multiplayer.bat -Port 42421
REM
REM  NOTE: two simultaneous players from ONE account is not possible (same
REM  playerUID -> the server kicks the first session). Co-op sawing needs a second
REM  VS account or a friend connecting to 127.0.0.1.
REM ============================================================================

echo [1/3] Regenerating woodchop animation patch...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-animation.ps1"
if errorlevel 1 (
    echo.
    echo !! FAILED to regenerate the woodchop patch - aborting before compile.
    pause
    exit /b 1
)

echo.
echo [2/3] Regenerating sawing patch, then building (Release, IwDebug)...
powershell -ExecutionPolicy Bypass -File "%~dp0apply-sawing-animation.ps1" -Build
if errorlevel 1 (
    echo.
    echo !! Sawing patch / build FAILED.
    pause
    exit /b 1
)

echo.
echo [3/3] Launching local dedicated server + client...
powershell -ExecutionPolicy Bypass -File "%~dp0run-multiplayer.ps1" %*
if errorlevel 1 (
    echo.
    echo !! Server / client launch FAILED.
    pause
    exit /b 1
)

pause
