<#
  apply-sawdust-points.ps1 — задаёт точки движения опилок при распиле и генерирует SawdustPoints.cs.

  При распиле спавнятся ДВЕ движущиеся частицы опилок, СВОЯ пара на каждую СЕКЦИЮ пропила:
    • FRONT — перед игроком, в месте разреза. ТОРЕЦ берётся ТЕКУЩИЙ (перешёл на другой конец — партикль за тобой).
    • UNDER — под бревном.
  Внутри секции точка спавна едет из Start в End по локальному времени секции (0 в начале → 1 перед дропом
  досок). Каждая выпущенная чешуйка потом падает по гравитации — путь Start→End рисуют последовательные спавны.

  СЕКЦИИ. Доски в коде падают на долях { 25%, 70%, 95%, 100% } времени SawSeconds (StageFractions).
  Два последних дропа (95%/100%) почти слитны — они СЛИТЫ в одну секцию. Итог: секции кончаются на 25% / 70% /
  100%. Их ЧИСЛО задаётся здесь, таблицами $Front/$Under ниже, и уезжает в SawdustPoints.cs константой
  SawdustSectionCount; C# объявляет SawdustSectionEnds как new double[SawdustSectionCount] { … }, так что
  добавленная сюда секция без своей доли времени в BlockEntitySawhorse — ошибка компиляции, а не рантайма.

  СИСТЕМА КООРДИНАТ точки = @(along, perp, up) (BlockEntitySawhorse.AxisToWorld):
    along : + = вдоль бревна ОТ тебя (к дальнему концу), − = к тебе. ДИНАМИЧНО — торец берётся ТЕКУЩИЙ (по игроку).
            0 = центр пары. Бревно ≈ [−0.81 .. +0.81].
    perp  : вбок поперёк бревна. 0 = ось, полуширина ≈ 0.35. Знак прибит к СТОРОНЕ РЕЗА, а не к игроку: с твоего
            вида лево/право меняется местами при переходе на другой торец, и это задумано. Реверсный пропил
            (sawCutReversed) ЗЕРКАЛИТ весь кадр целиком — AxisToWorld домножает орт на −1, ровно как модель
            зеркалит HideSequenceReversed, а доски sideSign. Числа ниже пишутся для ОБЫЧНОГО пропила.
    up    : высота над низом блока (Pos.Y). Бревно ≈ 0.58 .. 1.46 (диамант из sawhorse_log.json, повёрнутый на −45°).
  Кадр НЕ зависит от ориентации козел (X/Z) — те же числа работают и для С-Ю, и для З-В.

  ГДЕ РЕАЛЬНО ИДУТ РЕЗЫ (из sawhorse_log.json, центры керфов после разворота на −45° вокруг Z; AuditFixPlan T06.05):
    секция 1 = sawing_1..4   — поперёк +0.24, сверху вниз 1.21 → 0.83
    секция 2 = sawing_5..12  — поперёк  0.00, сверху вниз 1.44 → 0.61 (сквозной рез, весь диаметр)
    секция 3 = sawing_13..16 — поперёк −0.23, сверху вниз 1.22 → 0.82
  Поперечный ЗНАК в таблице обратен мировому (AxisToWorld: мир = −perp для Z-пары), поэтому секция 1 стоит на
  −0.24. Каждый рез идёт СВЕРХУ ВНИЗ, поэтому «зубец» (спуск, скачок вверх, спуск) — это правильная форма.

  ── ЧТО КРУТИТЬ: правь $Front и $Under ниже — и ВСЁ. Любая сборка (test.bat / -Build / голый dotnet build / IDE)
     САМА перегенерит ..\src\Sawhorse\SawdustPoints.cs из этих значений через csproj-таргет GenerateSawdustPoints. Отдельно
     запускать этот скрипт НЕ обязательно (раньше забыть этот шаг = старые значения в билде — теперь невозможно). ──

  Использование (Windows PowerShell 5.1) — ручной запуск опционален:
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawdust-points.ps1            # просто перегенерировать SawdustPoints.cs
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawdust-points.ps1 -Build      # + сборка Release (IW_DEBUG)
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawdust-points.ps1 -Build -Run # + запустить клиент
#>

param(
    # VS install dir — only needed for -Build / -Run (the generate-only path the csproj target uses never reads it).
    # Defaults to the VINTAGE_STORY env var, the same one ImmersiveWoodworking.csproj references for the engine DLLs;
    # the build/run block below throws a clear error if it's needed but unset (no machine-specific literal fallback).
    [string]$VintageStory = $env:VINTAGE_STORY,
    [switch]$Build,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$OutFile    = Join-Path $ProjectDir "src\Sawhorse\SawdustPoints.cs"

if (($Build -or $Run) -and -not $VintageStory) {
    throw "VINTAGE_STORY is not set. Set the VINTAGE_STORY environment variable to your Vintage Story install directory, or pass -VintageStory <path>."
}

# ============================ ТОЧКИ ПАРТИКЛЕЙ (РЕДАКТИРУЙ ЗДЕСЬ) ============================
# Формат каждой точки: @(along, perp, up).  Секции — пропилы, нумеруются подряд с 1 (см. шапку про доли времени).
# Внутри секции частица едет Start → End. Чтобы частица стояла на месте — сделай Start = End.
# Число секций берётся ОТСЮДА (ключи таблиц), отдельного счётчика в скрипте больше нет.

# --- FRONT: перед игроком, в месте разреза (торец берётся ТЕКУЩИЙ, по положению игрока) ---
# Высоты и поперечные смещения = центры реальных керфов модели, см. «ГДЕ РЕАЛЬНО ИДУТ РЕЗЫ» в шапке.
$Front = @{
    1 = @{ Start = @(-0.8, -0.24,  1.21); End = @(-0.81, -0.24,  0.83) }  # секция 1 (0%..25%),   керф sawing_1..4
    2 = @{ Start = @(-0.8,  0.00,  1.44); End = @(-0.81,  0.00,  0.61) }  # секция 2 (25%..70%),  керф sawing_5..12
    3 = @{ Start = @(-0.8,  0.23,  1.22); End = @(-0.81,  0.23,  0.82) }  # секция 3 (70%..100%), керф sawing_13..16
}

# --- UNDER: под бревном (по умолчанию идёт вдоль бревна ОТ игрока, секция за секцией) ---
# Поперёк — тот же керф, что у FRONT; высота ЧУТЬ НИЖЕ нижней грани бревна в этой точке (на оси она 0.58, а на
# ±0.24 от оси диамант поднимается до ≈0.81), чтобы пыль сыпалась из-под пропила, а не изнутри бревна.
$Under = @{
    1 = @{ Start = @(-0.8, -0.24,  0.75); End = @( 0.81, -0.24,  0.75) }  # секция 1 — ближняя треть
    2 = @{ Start = @(-0.8,  0.00,  0.60); End = @( 0.81,  0.00,  0.60) }  # секция 2 — средняя треть
    3 = @{ Start = @(-0.8,  0.23,  0.75); End = @( 0.81,  0.23,  0.75) }  # секция 3 — дальняя треть
}
# ===========================================================================================

# Секции: ЕДИНСТВЕННЫЙ источник их числа — ключи таблиц выше (AuditFixPlan T06.05, попутно F-L1-08; раньше
# число стояло ещё и литералом `-le 3` в цикле, и подписи были выписаны втроём вручную). Ключи обязаны быть
# сплошными 1..N и совпадать у FRONT и UNDER: EmitSawDust индексирует все четыре массива ОДНИМ и тем же sec.
$SectionKeys = @($Front.Keys | Sort-Object)
$UnderKeys   = @($Under.Keys | Sort-Object)
if (($SectionKeys -join ',') -ne ($UnderKeys -join ',')) {
    throw "FRONT и UNDER описывают разные секции: FRONT = $($SectionKeys -join ','), UNDER = $($UnderKeys -join ',')."
}
if ($SectionKeys.Count -lt 1 -or ($SectionKeys -join ',') -ne ((1..$SectionKeys.Count) -join ',')) {
    throw "Секции должны быть пронумерованы подряд с 1: сейчас $($SectionKeys -join ',')."
}
$SectionCount = $SectionKeys.Count

function Num([double]$v) { [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0,6:0.00}', $v) }

function Emit-Array($name, $table, $which, $suffix) {
    $out = @("    static readonly Vec3d[] $name =", "    {")
    foreach ($i in @($table.Keys | Sort-Object)) {
        $p = $table[$i][$which]
        $out += ("        new Vec3d({0}, {1}, {2}), // {3}" -f (Num $p[0]), (Num $p[1]), (Num $p[2]), "секция $i — $suffix")
    }
    $out += "    };"
    return ($out -join "`r`n")
}

# Подпись строки = номер секции + это слово. Раньше подписи лежали двумя массивами на три элемента и были
# третьим местом, где число секций было зашито числом.
$startSuffix = 'старт'
$endSuffix   = 'финиш'
$sectionList = ($SectionKeys | ForEach-Object { "секция $_" }) -join ', '

$header = @"
// <auto-generated>
//   GENERATED by tools/apply-sawdust-points.ps1 — DO NOT edit by hand (a build overwrites it).
//   To move a sawdust particle, edit the values at the top of that .ps1 and rebuild — the csproj target
//   GenerateSawdustPoints re-runs the generator on every build, so your edits always reach the compiled mod.
//
//   These are the START/END points of the two moving sawdust emitters, one pair per CUT SECTION ($SectionCount
//   sections — the rip's plank-drops at 25% / 70% / 100% of saw time, the last two drops merged). Within a
//   section the emitter's spawn point lerps START → END as that section's local time runs 0 → 1.
//
//   FRAME — each point is Vec3d(along, perp, up) (BlockEntitySawhorse.AxisToWorld):
//     along : + = ОТ тебя (к дальнему концу), − = к тебе. ДИНАМИЧНО — торец ТЕКУЩИЙ (по игроку). Бревно ∈ [−0.81..+0.81].
//     perp  : вбок поперёк бревна. 0 = ось, полуширина ≈ 0.35. Знак прибит к стороне РЕЗА, не к игроку; реверсный
//             пропил (sawCutReversed) зеркалит весь кадр в AxisToWorld, поэтому числа тут — для обычного пропила.
//     up    : высота над низом блока (Pos.Y). Бревно ≈ 0.58 .. 1.46.
//   Керфы модели (sawhorse_log.json после разворота на −45°): поперёк +0.24 / 0.00 / −0.23, сверху вниз
//   1.21 → 0.83 / 1.44 → 0.61 / 1.22 → 0.82. Мировое поперёк обратно знаку perp, отсюда −0.24 у секции 1.
// </auto-generated>
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
"@

$body = @(
    "    // Число секций опилок — ЕДИНСТВЕННЫЙ источник — таблицы `$Front/`$Under в tools/apply-sawdust-points.ps1."
    "    // BlockEntitySawhorse.SawdustSectionEnds объявлен как new double[SawdustSectionCount] { … }, поэтому"
    "    // расхождение между числом точек и числом долей времени ловит компилятор (AuditFixPlan T06.05 / F-L1-08)."
    "    const int SawdustSectionCount = $SectionCount;"
    ""
    "    // FRONT emitter — перед игроком, в месте разреза (торец — текущий). [$sectionList]"
    (Emit-Array 'FrontStart' $Front 'Start' $startSuffix)
    (Emit-Array 'FrontEnd'   $Front 'End'   $endSuffix)
    ""
    "    // UNDER emitter — под бревном. [$sectionList]"
    (Emit-Array 'UnderStart' $Under 'Start' $startSuffix)
    (Emit-Array 'UnderEnd'   $Under 'End'   $endSuffix)
) -join "`r`n"

$content = $header + "`r`n" + $body + "`r`n}`r`n"

# Пишем ТОЛЬКО при изменении содержимого: сборка зовёт этот скрипт на КАЖДОМ билде (csproj-таргет
# GenerateSawdustPoints), и лишняя перезапись зря триггерила бы рекомпиляцию. UTF-8 без BOM — как у соседних tools.
$existing = if (Test-Path $OutFile) { [System.IO.File]::ReadAllText($OutFile) } else { $null }
if ($existing -ne $content) {
    [System.IO.File]::WriteAllText($OutFile, $content)
    Write-Host "OK  generated (changed): $OutFile" -ForegroundColor Green
} else {
    Write-Host "OK  up to date: $OutFile" -ForegroundColor DarkGray
}

if ($Build -or $Run) {
    $env:VINTAGE_STORY = $VintageStory
    Write-Host "`nBuilding (Release, IW_DEBUG on)..." -ForegroundColor Cyan
    dotnet build (Join-Path $ProjectDir "ImmersiveWoodworking.csproj") -c Release -p:IwDebug=true --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if ($Run) {
    Write-Host "`nLaunching client..." -ForegroundColor Cyan
    Start-Process -FilePath "dotnet" -WorkingDirectory $VintageStory -ArgumentList @(
        "$VintageStory\Vintagestory.dll", "--tracelog",
        "--addModPath", "$ProjectDir\bin\Release-iwdebug\Mods"
    ) | Out-Null
    Write-Host "OK  client launched." -ForegroundColor Green
}
