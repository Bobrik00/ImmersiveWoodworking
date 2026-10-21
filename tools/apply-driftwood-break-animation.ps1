<#
  apply-driftwood-break-animation.ps1 — turns the raw driftwood-break export into the game-loaded patch.

  Sibling to apply-debarking-animation.ps1 / apply-sawing-animation.ps1 / apply-animation.ps1. Reads the raw
  Blender / VS animation-editor export (art\Animations\driftwood-break_animations.json) and writes
  assets\immersivewoodworking\patches\driftwood-break_animations.json — two Add ops per animation (client-animation
  metadata on the player entity + the shape animation on seraph-faceless), exactly like the debark patch. VS
  auto-loads every file in assets\<domain>\patches\, so the emitted patch needs no registration.

  The driftwood hand-break (E07, BlockBehaviorFlotsamBreak) is a ONE-SHOT snap, NOT a held loop. Two deliberate
  differences from apply-debarking-animation.ps1:
    * NO Set-EndHandling. The debark script FORCES onAnimationEnd=Repeat / onActivityStopped=EaseOut (a loop); we
      KEEP the export's authored ends (onActivityStopped=PlayTillEnd / onAnimationEnd=EaseOut) so the gesture plays
      through ONCE and eases out on its own. Started with a plain Entity.StartAnimation("driftwood-break"); no paired
      Stop is needed (the block behavior stops it defensively).
    * NO sound layer. The break's audio is the code-driven debarking1/2 loop with a fade in BlockBehaviorFlotsamBreak
      (UpdateBreakSound / StopBreakSound), NOT frame-locked animationSounds. So New-Meta carries no animationSounds.

  The break animates only the four ARM bones (UpperArmL/LowerArmL/UpperArmR/LowerArmR — no torso/head/ItemAnchor
  keyframes), so New-Meta's elementWeight/elementBlendMode list only those. animationSpeed 0.6 stretches the 21-frame
  gesture to ~1.17s so its pull-through lands near the default FlotsamBreakSeconds (1.2s) break — tune to taste.

  Code-agnostic: iterates EVERY animation in the export and registers each (base + -fp), so a re-export / rename is
  picked up with no script edit. New codes must ALSO be added to EntityBehaviorChopAnim.CustomAnimCodes so the
  gesture backfills onto reskin player shapes.

  Usage (Windows PowerShell 5.1):
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-driftwood-break-animation.ps1
#>

param(
    # Raw export. Defaults to the repo's art folder, resolved relative to this script (tools\ ->
    # ..\art\), so it works wherever the repo is checked out. Override with -Source.
    [string]$Source = (Join-Path $PSScriptRoot "..\art\Animations\driftwood-break_animations.json")
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$OutFile    = Join-Path $ProjectDir "assets\immersivewoodworking\patches\driftwood-break_animations.json"

if (-not (Test-Path $Source)) { throw "Source export not found: $Source" }

# ============================ BLEND METADATA (tune here) ============================
# The "how it blends" layer the raw export lacks. Only the four arm bones the break keyframes touch are listed;
# unlisted bones stay at the base weight 0.25. Weights mirror the debark meta (arms dominate so the gesture reads
# over the engine's concurrent interactstatic + idle1), minus the torso/anchor entries the break never animates.
# animationSpeed 0.6: stretch the 21-frame one-shot to ~1.17s (natural export cadence is 0.7s at 1.0). NO
# animationSounds — the break audio is the code-driven debarking1/2 fade loop in BlockBehaviorFlotsamBreak.
function New-Meta($code) {
    [ordered]@{
        code                  = $code
        animation             = $code
        animationSpeed        = 0.6
        blendMode             = "AddAverage"
        weight                = 0.25
        weightCapFactor       = 1
        easeInSpeed           = 8
        easeOutSpeed          = 12
        holdEyePosAfterEasein = 0.1
        elementWeight    = [ordered]@{ UpperArmL = 400; LowerArmL = 400; UpperArmR = 400; LowerArmR = 400 }
        elementBlendMode = [ordered]@{ UpperArmL = "AddAverage"; LowerArmL = "AddAverage"; UpperArmR = "AddAverage"; LowerArmR = "AddAverage" }
    }
}
# ===================================================================================

$src = Get-Content $Source -Raw | ConvertFrom-Json
if (-not $src.animations -or @($src.animations).Count -eq 0) {
    throw "Export '$Source' has no animations array (or it is empty)."
}

# Preserve export order; register every animation.
$byCode = [ordered]@{}
foreach ($a in $src.animations) {
    if (-not $a.code) { throw "Export '$Source' has an animation without a 'code' field." }
    if ($byCode.Contains($a.code)) { throw "Export '$Source' has a duplicate animation code '$($a.code)'." }
    $byCode[$a.code] = $a
}
$codes = @($byCode.Keys)

# --- Soft tp/-fp pairing check (WARNING, not throw) — catches "exported tp, forgot fp" or vice versa.
foreach ($c in $codes) {
    if ($c.EndsWith("-fp")) {
        $base = $c.Substring(0, $c.Length - 3)
        if (-not $byCode.Contains($base)) { Write-Host ("WARNING  '{0}' has no third-person base '{1}'." -f $c, $base) -ForegroundColor Yellow }
    } else {
        if (-not $byCode.Contains("$c-fp")) { Write-Host ("WARNING  '{0}' has no first-person variant '{0}-fp'." -f $c) -ForegroundColor Yellow }
    }
}

# --- rotShortestDistance MUST be false for a BLENDING held/played anim (else ElementPose.Add ignores the blend
# weight on that axis and snaps the pose to frame 0). Engine default is false and the export usually omits it, but
# if the editor set it true, strip it here. (Same guard as apply-debarking-animation.ps1.)
function Disable-RotShortestDistance($anim) {
    foreach ($kf in $anim.keyframes) {
        if (-not $kf.elements) { continue }
        foreach ($elemName in @($kf.elements.PSObject.Properties.Name)) {
            $el = $kf.elements.$elemName
            foreach ($axis in 'rotShortestDistanceX', 'rotShortestDistanceY', 'rotShortestDistanceZ') {
                if ($el.PSObject.Properties.Name -contains $axis) { $el.$axis = $false }
            }
        }
    }
}
foreach ($c in $codes) { Disable-RotShortestDistance $byCode[$c] }

# NOTE: NO Set-EndHandling here (unlike the debark script). The export's onActivityStopped=PlayTillEnd /
# onAnimationEnd=EaseOut ARE the authored one-shot ends — keep them.

# Patch: all client-animation metadata first (player), then all shape animations (seraph-faceless) — same order as
# the debark patch. No per-code sound branch (the break audio is code-driven, not animationSounds).
$patch = @()
foreach ($c in $codes) {
    $patch += [ordered]@{ file = "game:entities/humanoid/player"; op = "Add"; path = "/client/animations/-"; value = (New-Meta $c) }
}
foreach ($c in $codes) {
    $patch += [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode[$c] }
}

# UTF-8 without BOM, readable JSON (like the sibling patches). WriteAllText defaults to UTF-8-no-BOM.
[System.IO.File]::WriteAllText($OutFile, (ConvertTo-Json -InputObject $patch -Depth 100))
Write-Host "OK  patch written: $OutFile" -ForegroundColor Green
Write-Host ("    registered {0} animation(s): {1}" -f $codes.Count, ($codes -join ', ')) -ForegroundColor Green
foreach ($c in $codes) {
    $last = ($byCode[$c].keyframes | Measure-Object -Property frame -Maximum).Maximum
    Write-Host ("    {0,-22} quantityframes={1}  lastKeyframe={2}  onAnimationEnd={3}  onActivityStopped={4}" -f `
        $c, $byCode[$c].quantityframes, $last, $byCode[$c].onAnimationEnd, $byCode[$c].onActivityStopped)
}
