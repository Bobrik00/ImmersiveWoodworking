<#
.SYNOPSIS
  Pack the current ImmersiveWoodworking build into Releases\immersivewoodworking_<version>.zip

.DESCRIPTION
  Reads the version from the mod's modinfo.json and zips the compiled Release output
  (bin\Release\Mods\mod) into the mod's Releases folder, named
  immersivewoodworking_<version>.zip.

  The archive layout matches the existing immersivewoodworking_0.9.5.zip: the CONTENTS of
  the build output (assets\, the .dll and modinfo.json) sit at the archive root - that is
  the layout Vintage Story / ModDB expect.

  This is the quick local packager, working off whatever is already compiled in bin\. The
  OFFICIAL release path is build.ps1 in the mod root, which runs the shared CakeBuild
  (.shared\CakeBuild) and packs from a clean publish. Both produce the same file list.

  Only what the game loads goes into the zip: the files at the root of the build output plus
  assets\ recursively. Any other subfolder is a build artifact and is skipped - notably
  publish\, which `dotnet publish` (i.e. build.ps1) leaves behind inside the same bin folder.
  From the root files the debug symbols (.pdb) and the .NET dependency manifest (.deps.json)
  are dropped too. They only bloat the download; the runtime loads ImmersiveWoodworking.dll directly.

  On a Release build the JSON assets are already comment-stripped by the csproj
  StripJsonComments target, so the packaged JSON is clean.

.PARAMETER Build
  Compile a fresh Release build (dotnet build -c Release) before packaging. Without this the
  script packages whatever is already in bin\Release; it only builds automatically when that
  output is missing.

.PARAMETER Configuration
  Build configuration to package. Default: Release. (Use Release for shipping - Debug ships
  un-stripped JSON, and the *-iwdebug folders are never touched.)

.PARAMETER Force
  Overwrite the release zip if one already exists for this version. Without -Force the script
  refuses to clobber an already-packaged version.

.EXAMPLE
  # Package whatever you just built in Release (from the mod's repo root):
  powershell -File tools\pack-release.ps1

.EXAMPLE
  # Build Release fresh, then package, overwriting an existing zip for this version:
  powershell -File tools\pack-release.ps1 -Build -Force

.NOTES
  Double-click the sibling pack-release.bat / pack-release-build.bat / pack-release-force.bat
  in this tools\ folder to run without a shell. They invoke this script via %~dp0, so they work
  regardless of the current directory.
#>
[CmdletBinding()]
param(
    [switch]$Build,
    [string]$Configuration = 'Release',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This script lives in <mod>\tools - walk up to the mod dir. The release zip lands in that mod's
# OWN Releases\ folder (each mod is its own repo now, and matching
# ImmersiveRainbow\tools\pack-release.ps1).
$modDir   = Split-Path $PSScriptRoot -Parent                  # ...\VS-Mods\ImmersiveWoodworking
$csproj   = Join-Path $modDir 'ImmersiveWoodworking.csproj'
$modInfo  = Join-Path $modDir 'modinfo.json'
$outDir   = Join-Path $modDir "bin\$Configuration\Mods\mod"
$releases = Join-Path $modDir 'Releases'

# --- read version + modid from modinfo.json ---
if (-not (Test-Path -LiteralPath $modInfo)) { throw "modinfo.json not found: $modInfo" }
$info    = Get-Content -Raw -LiteralPath $modInfo | ConvertFrom-Json
$version = "$($info.version)".Trim()
$modid   = "$($info.modid)".Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw "No 'version' field in $modInfo" }
if ([string]::IsNullOrWhiteSpace($modid))   { throw "No 'modid' field in $modInfo" }

Write-Host "ImmersiveWoodworking release packager"
Write-Host "  mod id        : $modid"
Write-Host "  version       : $version"
Write-Host "  configuration : $Configuration"

# --- build if asked, or if the output is missing ---
$dll = Join-Path $outDir 'ImmersiveWoodworking.dll'
if ($Build -or -not (Test-Path -LiteralPath $dll)) {
    if (-not $Build) {
        Write-Host "  build output not found - building automatically" -ForegroundColor Yellow
    }
    Write-Host "==> dotnet build `"$csproj`" -c $Configuration"
    & dotnet build $csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Build output still missing after build: $dll"
}

# --- resolve target zip path ---
if (-not (Test-Path -LiteralPath $releases)) {
    New-Item -ItemType Directory -Path $releases | Out-Null
}
$zipName = "${modid}_${version}.zip"
$zipPath = Join-Path $releases $zipName

if (Test-Path -LiteralPath $zipPath) {
    if (-not $Force) {
        throw "Release already exists: $zipPath`n       Pass -Force to overwrite it."
    }
    Write-Host "  overwriting existing $zipName" -ForegroundColor Yellow
    Remove-Item -LiteralPath $zipPath -Force
}

# --- zip the CONTENTS of the build output at archive root ---
# NOTE: we add entries by hand instead of ZipFile.CreateFromDirectory because under Windows
# PowerShell 5.1 (.NET Framework) CreateFromDirectory writes the OS separator '\' into entry
# names (assets\foo\bar.json). The ZIP spec mandates '/', and Vintage Story / ModDB expect
# forward slashes - backslash entries flatten the tree and break mod loading. Building each
# entry with a normalized forward-slash relative path keeps parity with immersivewoodworking_0.9.5.zip.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$base  = (Resolve-Path -LiteralPath $outDir).Path.TrimEnd('\','/') + [System.IO.Path]::DirectorySeparatorChar
# Ship only what the game loads, and only from where the game looks: the files sitting at the root
# of the build output, plus assets\ recursively. Anything else under bin\<cfg>\Mods\mod is a build
# artifact. The one that bites is publish\ - `dotnet publish`, which .shared\CakeBuild runs, drops a
# SECOND copy of the whole mod there, and a plain -Recurse would zip it as publish/...dll. Whitelisting
# assets\ instead of blacklisting publish\ also covers ref\ and whatever the SDK adds next.
# From the root files we still drop the debug symbols (.pdb) and the .NET dependency manifest
# (.deps.json): the runtime reads neither. .shared\CakeBuild\Program.cs drops the same two - keep in sync.
$rootFiles = @(Get-ChildItem -LiteralPath $outDir -File |
    Where-Object { $_.Extension -ne '.pdb' -and $_.Name -notlike '*.deps.json' })
$assetsDir = Join-Path $outDir 'assets'
$assetFiles = @()
if (Test-Path -LiteralPath $assetsDir) {
    $assetFiles = @(Get-ChildItem -LiteralPath $assetsDir -Recurse -File)
}
$files = $rootFiles + $assetFiles

foreach ($dir in @(Get-ChildItem -LiteralPath $outDir -Directory | Where-Object { $_.Name -ne 'assets' })) {
    Write-Host "  skipping      : $($dir.Name)\ (build artifact, not shipped)" -ForegroundColor Yellow
}
Write-Host "  packing       : $($files.Count) files (excluding .pdb / .deps.json)"

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($base.Length) -replace '\\', '/'
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $rel,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

$sizeKb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB, 1)
Write-Host ""
Write-Host "Packaged -> $zipPath  ($sizeKb KB)" -ForegroundColor Green
