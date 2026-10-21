<#
.SYNOPSIS
    Strips // line and /* */ block comments from the SHIPPED copy of the mod's JSON assets.

.DESCRIPTION
    The source JSON under assets/ keeps its developer //-docs ON PURPOSE (they document the
    blocktypes / itemtypes / recipes). The build runs this over the build OUTPUT
    (bin\<Config>\Mods\mod) so the game only ever loads clean files while the source stays
    documented. Wired into ImmersiveWoodworking.csproj as the StripJsonComments target
    (AfterTargets="Build", EVERY configuration).

    This is a CORRECTNESS pass, not just cosmetics. A whole-line // comment sitting directly
    inside a top-level JSON array survives as a JTokenType.Comment child of the JArray, and the
    recipe loaders walk that array token by token: RecipeLoader.LoadRecipes logs "Failed to parse
    <kind> recipe" once per comment line, and RecipeRegistrySystem.loadRecipe (cooking) NREs on it
    and takes the whole cooking registry down. Running Release-only meant dev builds silently ran
    with broken recipes; see changelog-internal.md, 2026-07-31.

    The stripper is STRING-AWARE: it tracks whether it is inside a JSON "..." string (honouring \"
    escapes) so a // or /* that lives INSIDE a string value -- e.g. "handbook://..." links in the
    lang files, or the "https://..." $schema in modinfo -- is never mistaken for a comment. A naive
    regex would corrupt exactly those. After comments are removed, lines that became blank (a
    whole-line comment) are dropped and trailing whitespace trimmed, so the result is tidy. VS JSON
    never puts a real newline inside a string (newlines are escaped as \n), so the line-based tidy
    pass can never split a string.

    Idempotent and write-if-different: a file with no comments is left byte-identical and not
    rewritten, so repeated builds neither churn files nor reset PreserveNewest timestamps.

    Safety net: each stripped result is re-parsed with the SAME Newtonsoft.Json the game uses
    (loaded from $env:VINTAGE_STORY\Lib). If the strip somehow produced invalid JSON the original
    file is kept untouched (it still works in-game -- VS tolerates comments) and a warning is
    emitted. If the DLL can't be found, validation is skipped (the state machine is correct by
    construction; validation is only a backstop).

    NOTE: keep this script ASCII-only. PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a non-ASCII
    char in a string literal would corrupt the parse. The data files it writes are handled by
    [System.IO.File] (byte-correct UTF-8), independent of this script's own encoding.

.PARAMETER Path
    Directory to process. All *.json under it (recursively) are stripped, except build artefacts
    (*.deps.json, *.runtimeconfig.json) and anything under an obj\ or ref\ folder.
#>
param(
    [Parameter(Mandatory = $true)][string]$Path
)

$ErrorActionPreference = 'Stop'

# -- String-aware comment removal (char state machine) -------------------------------------------
function Remove-JsonComments {
    param([string]$text)

    $sb = [System.Text.StringBuilder]::new($text.Length)
    $n = $text.Length
    $i = 0
    $inString = $false

    while ($i -lt $n) {
        $c = $text[$i]

        if ($inString) {
            [void]$sb.Append($c)
            if ($c -eq '\') {
                # Copy the escaped char verbatim so a \" doesn't end the string early.
                if ($i + 1 -lt $n) { [void]$sb.Append($text[$i + 1]); $i += 2; continue }
            }
            elseif ($c -eq '"') {
                $inString = $false
            }
            $i++
            continue
        }

        # Outside a string:
        if ($c -eq '"') {
            $inString = $true
            [void]$sb.Append($c)
            $i++
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/') {
            $i += 2
            while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }   # keep the newline
            continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i + 1] -eq '*') {
            $i += 2
            while ($i + 1 -lt $n -and -not ($text[$i] -eq '*' -and $text[$i + 1] -eq '/')) { $i++ }
            $i += 2
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

# -- Drop now-blank lines + trim trailing whitespace ---------------------------------------------
function Remove-BlankLines {
    param([string]$text)
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($text -split "`n")) {
        $t = $line.TrimEnd("`r", ' ', "`t")
        if ($t.Trim().Length -gt 0) { [void]$out.Add($t) }
    }
    return (($out -join "`n") + "`n")
}

# -- Optional validator: the exact Newtonsoft the game ships (comment/trailing-comma tolerance) ---
$validator = $null
if ($env:VINTAGE_STORY) {
    $dll = Join-Path $env:VINTAGE_STORY 'Lib\Newtonsoft.Json.dll'
    if (Test-Path $dll) {
        try { Add-Type -Path $dll; $validator = $true } catch { $validator = $null }
    }
}
function Test-Json {
    param([string]$text)
    if (-not $validator) { return $true }            # no validator available -> trust the algorithm
    try { [void][Newtonsoft.Json.Linq.JToken]::Parse($text); return $true } catch { return $false }
}

# -- Walk the tree -------------------------------------------------------------------------------
if (-not (Test-Path $Path)) {
    Write-Host "[strip-json] path not found, nothing to do: $Path"
    exit 0
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$changed = 0
$skipped = 0
$total = 0

$files = Get-ChildItem -Path $Path -Recurse -Filter '*.json' -File | Where-Object {
    $_.Name -notlike '*.deps.json' -and
    $_.Name -notlike '*.runtimeconfig.json' -and
    $_.FullName -notmatch '\\(obj|ref)\\'
}

foreach ($f in $files) {
    $total++
    $original = [System.IO.File]::ReadAllText($f.FullName)
    $stripped = Remove-BlankLines (Remove-JsonComments $original)

    if ($stripped -eq $original) { continue }        # already clean -> leave byte-identical

    if (-not (Test-Json $stripped)) {
        Write-Warning "[strip-json] strip produced invalid JSON, keeping original: $($f.FullName)"
        $skipped++
        continue
    }

    [System.IO.File]::WriteAllText($f.FullName, $stripped, $utf8NoBom)
    $changed++
}

$summary = "[strip-json] cleaned $changed of $total JSON file(s) under $Path"
if ($skipped -gt 0) { $summary += " ($skipped skipped - invalid after strip)" }
Write-Host $summary
