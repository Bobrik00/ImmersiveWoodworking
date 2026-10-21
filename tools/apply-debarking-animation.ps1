<#
  apply-debarking-animation.ps1 — превращает «сырой» экспорт анимаций обдирки (debarking) в патч-файл мода.

  Сестринский к apply-sawing-animation.ps1 (распил) и apply-animation.ps1 (замах топора woodchop). Берёт
  экспорт из Blender / редактора анимаций VS (сырой { "animations": [ ... ] }) и пишет
  assets/immersivewoodworking/patches/debarking_animations.json — патч, который игра реально загружает
  (по 2 записи на КАЖДУЮ анимацию: метаданные клиентской анимации на player + сама анимация формы (shape)
  на seraph-faceless).

  Запускай после каждого ре-экспорта. Кейфреймы берутся из ТВОЕГО экспорта 1:1; сверху добавляется только
  слой бленд-метаданных («BLEND-МЕТАДАННЫЕ») и ФОРСИРУЕТСЯ обработка концов под зацикленную протяжку.

  КОД-АГНОСТИЧНОСТЬ (отличие от распильного скрипта): debarking-экспорт несёт несколько анимаций сразу —
  путь скобеля (тир-3, T03.01) И путь топор+молот (тир-2, T03.03), каждая в паре tp + `-fp`. Скрипт НЕ
  хардкодит коды: он перебирает ВСЕ анимации в экспорте и регистрирует каждую. Поэтому переименование кодов
  в Blender (напр. `debarking_barkspud` → `debarking-spud` для единообразия с `sawing`/`-fp`) подхватывается
  ре-экспортом БЕЗ правок скрипта. На вход кладётся то, что наавторено; здесь только бленд-слой и концы.

  Чем обдирка похожа на распил (и отличается от замаха woodchop):
    • Анимация ЗАЦИКЛЕНА, пока зажата ПКМ: onAnimationEnd = Repeat (скобель/топор ходят туда-сюда вдоль
      бревна). У woodchop — Hold (одиночный замах фиксируется). Мы ФОРСИМ Repeat здесь, как у распила,
      потому что редактор экспортирует протяжку как одноразовую (EaseOut/PlayTillEnd), а нам нужен цикл.
    • Нет рантайм-переопределения скорости: animationSpeed из New-Meta — это РЕАЛЬНАЯ скорость
      воспроизведения (как у распила; woodchop пересчитывал её под ChopSeconds в коде). Скорость кадров и
      накопление barkProgress — РАЗВЯЗАНЫ, ровно как у распила (см. BlockEntitySawhorse.Debarking.cs).
    • Триггер не на предмете, а на блоке: BlockSawhorse.OnBlockInteractStart запустит StartAnimation(code)
      ТОЛЬКО когда гейты обдирки прошли (бок бревна + инструмент). Это драйвинг — задача T03.02; здесь только
      РЕГИСТРАЦИЯ анимаций, чтобы их было что стартовать. Held-use анимация движка (interactstatic) играет на
      ЛЮБОМ зажатии ПКМ и контекст не различает — отсюда ручной Start/Stop в коде (как у распила).
    • Оба пути ДВУРУЧНЫЕ (скобель — две руки на рукояти; топор+молот — топор в осн., молот в offhand), поэтому
      бленд-веса рук симметрично-тяжёлые, как у распила.

  ЗВУК обдирки (animationSounds) — НЕ здесь: это задача T03.04. Когда дойдём, она добавит функцию
  New-DebarkSounds и повесит её на TP-варианты (НЕ на `-fp`, иначе в 1-м лице звук задвоится — см. подробный
  разбор в apply-sawing-animation.ps1). Точка врезки помечена ниже в New-Meta и в сборке патча.

  Использование (Windows PowerShell 5.1):
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-debarking-animation.ps1            # пересобрать патч
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-debarking-animation.ps1 -Build      # + Release
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-debarking-animation.ps1 -Build -Run # + запустить клиент
#>

param(
    # Raw Blender / VS animation-editor export. Defaults to the repo's art\Animations folder, resolved
    # RELATIVE to this script (tools\ -> ..\art\Animations), so it works wherever the
    # repo is checked out. Override with -Source. (Mirror of apply-sawing-animation.ps1.)
    [string]$Source = (Join-Path $PSScriptRoot "..\art\Animations\debarking_animations.json"),
    # VS install dir — only needed for -Build / -Run. Defaults to the VINTAGE_STORY env var (as the .csproj uses);
    # the build/run block below throws a clear error if it's needed but unset (no machine-specific literal fallback).
    [string]$VintageStory = $env:VINTAGE_STORY,
    [switch]$Build,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$OutFile    = Join-Path $ProjectDir "assets\immersivewoodworking\patches\debarking_animations.json"

if (($Build -or $Run) -and -not $VintageStory) {
    throw "VINTAGE_STORY is not set. Set the VINTAGE_STORY environment variable to your Vintage Story install directory, or pass -VintageStory <path>."
}

# ============================ BLEND-МЕТАДАННЫЕ (настраивать здесь) ============================
# Применяется к КАЖДОЙ debarking-анимации (скобель и топор+молот, tp и `-fp`). Слой «как оно блендится»,
# которого в сыром экспорте нет. Веса 1:1 с распилом (apply-sawing-animation.ps1) — те же соображения:
# пока держишь ПКМ на козлах, движок параллельно крутит `interactstatic` (вес правой руки 8, AddAverage) и
# `idle1` (вес 1 на все кости). Чтобы протяжка читалась на полную, веса рук подняты до 400 (доля ≈ 0.98).
# Обе руки одинаково тяжёлые: оба пути обдирки ДВУРУЧНЫЕ.
#
#   weight = 0.25 (база): вес неперечисленных костей (ноги/ступни/голова/шея). Кейфреймы скобеля слегка
#     докручивают Neck/Head/UpperTorso (наклон к бревну) — при базовом 0.25 они показываются на четверть.
#     Хочешь сильнее читаемый наклон корпуса/головы — подними здесь или добавь их в elementWeight ниже.
#   easeInSpeed = 8 / easeOutSpeed = 12: набор/угасание веса (tau = 30/speed кадров; полное угасание
#     ≈ 6.2/easeOutSpeed сек). 12 ≈ 0.5 сек на сход после отпускания ПКМ. Те же значения, что у распила.
#   elementWeight: руки доминируют (400), торс — лёгкий доворот (0.8, Average), ItemAnchor высокий (100,
#     Average), чтобы инструмент следовал за руками.
#
# animationSpeed = 0.5: РЕАЛЬНАЯ скорость (НЕ переопределяется в коде — как у распила). Это визуальная
#   каденция протяжки, РАЗВЯЗАННАЯ с накоплением barkProgress (BarkSeconds). Скобель (28 кадров) и
#   топор+молот (21 кадр) тут делят одну каденцию для тонкого среза; если понадобится разная скорость по
#   инструменту — разнеси per-code в New-Meta (T03.02/T03.03). Медленнее — опускай.
# --- Звук обдирки: НАТИВНЫЙ механизм движка `animationSounds` (НЕ код) -------------------------------------
# (T03.04) Тот же механизм, что у распила (см. подробный разбор в apply-sawing-animation.ps1): `ClientAnimator`
# проигрывает звук каждый раз, когда `CurrentFrame >= frame` (раз за итерацию цикла). Идеальная синхронизация с
# руками БЕЗ кода и КОРРЕКТНО в мультиплеере (каждый клиент, рендерящий анимацию, проигрывает в такт своему кадру).
# Вешаем ТОЛЬКО на TP-коды (3-е лицо), НЕ на `-fp`: TP-аниматор тикается и в 1-м лице, поэтому звук сыграет один
# раз во всех режимах; добавить и на `-fp` — задвоится в 1-м лице.
#
# ПОКАДРОВЫЙ набор ПО ИНСТРУМЕНТУ (а не один скол в середине): у каждого тира своя пара контактов в цикле, заданная
# ЯВНЫМИ кадрами (не 0.5*quantityframes — точные кадры финалятся по анимации/слуху). Каждый звук тянется движком из
# wildcard НАТИВНО (как ванильные footstep-*), случайно из *1/*2/*3 (все МОНО — OpenAL спатиализует только моно,
# память vs-positional-audio-mono). Авто-префикс "sounds/" → файлы в assets/immersivewoodworking/sounds/debark/.
#
#   debarking-axe (топор+молот, 21 кадр): на кадрах 7 и 17 (два удара) — ОБА звука сразу: удар молота по обуху
#     топора = металлический «хит» (debarking-hammerhit*) + смещение топора в коре (debarking-axehit*). 4 триггера.
#   debarking-barkspud (скобель, 28 кадров): протяжка скобеля на кадрах 6 и 13 — debarking-axehit* (скол/срез коры).
#   debarking-knife (нож, 25 кадров): два прохода скобления на кадрах 6 и 18 — debarking-knifescrape* (срез коры ножом).
#
# ВАЖНО: генерик `debarking*` (буквально звук СДИРАНИЯ коры — debarking1/2/3) здесь НЕ используется — он
# зарезервирован под ОТДЕЛЬНЫЙ триггер (повесится позже). ОСТОРОЖНО на будущее: голый wildcard `debark/debarking*`
# поймал бы И debarking-axehit*/debarking-hammerhit*/debarking-knifescrape* (общий префикс "debarking") → для
# генерик-триггера бери НЕ голый `debarking*`, а конкретику (debarking1*/debarking2*…) или вынеси удары в подпапку.
# Здесь префиксы конкретны (`-axehit`/`-hammerhit`/`-knifescrape`) и не пересекаются. pitch var 0.1 = разлёт 0.9…1.1.
# Перетюнить кадры/радиус — в New-DebarkSounds.

# Один триггер animationSounds: звук `location` на кадре `frame`, радиус 16, лёгкий разлёт pitch 0.9…1.1.
function New-Sound($location, $frame) {
    [ordered]@{ frame = [int]$frame; location = $location; range = 16; pitch = [ordered]@{ avg = 1.0; var = 0.1; dist = "uniform" }; chance = 1 }
}

# Набор звуков ПО КОДУ анимации (только TP). `-fp` сюда не попадают (другой код) → default = пусто, что и нужно
# (TP-only). Кадры заданы ЯВНО под каждый инструмент (см. блок-комментарий выше); перетюнить — здесь.
function New-DebarkSounds($code) {
    $axehit      = "immersivewoodworking:debark/debarking-axehit*"
    $hammerhit   = "immersivewoodworking:debark/debarking-hammerhit*"
    $knifescrape = "immersivewoodworking:debark/debarking-knifescrape*"
    switch ($code) {
        # Топор+молот: на кадрах 7 и 17 — оба звука вместе (полный «щелчок» удара молота по обуху топора).
        "debarking-axe" {
            @( (New-Sound $axehit 7), (New-Sound $hammerhit 7), (New-Sound $axehit 17), (New-Sound $hammerhit 17) )
        }
        # Скобель: протяжка/скол коры на кадрах 6 и 13.
        "debarking-barkspud" {
            @( (New-Sound $axehit 6), (New-Sound $axehit 13) )
        }
        # Нож: два прохода скобления коры на кадрах 6 и 18 — debarking-knifescrape* (срез коры ножом).
        "debarking-knife" {
            @( (New-Sound $knifescrape 6), (New-Sound $knifescrape 18) )
        }
        default { @() }
    }
}

function New-Meta($code, $isTp = $false) {
    $meta = [ordered]@{
        code                  = $code
        animation             = $code
        animationSpeed        = 0.5
        blendMode             = "AddAverage"
        weight                = 0.25
        weightCapFactor       = 1
        easeInSpeed           = 8
        easeOutSpeed          = 12
        holdEyePosAfterEasein = 0.1
        elementWeight    = [ordered]@{ UpperArmL = 400; LowerArmL = 400; UpperArmR = 400; LowerArmR = 400; UpperTorso = 0.8; LowerTorso = 0.8; ItemAnchor = 100 }
        elementBlendMode = [ordered]@{ UpperArmL = "AddAverage"; LowerArmL = "AddAverage"; UpperArmR = "AddAverage"; LowerArmR = "AddAverage"; UpperTorso = "Average"; LowerTorso = "Average"; ItemAnchor = "Average" }
    }
    # Звук — frame-synced на TP-коды ТОЛЬКО (НЕ на `-fp`, иначе в 1-м лице задвоится — см. apply-sawing).
    # Каст к [object[]] ОБЯЗАТЕЛЕН: ConvertTo-Json в PS 5.1 разворачивает массив из ОДНОГО элемента в голый объект,
    # а движок ждёт МАССИВ звуков (как в рабочем патче распила). Каст форсит `[ {...} ]` даже для одного триггера.
    if ($isTp) {
        $sounds = @(New-DebarkSounds $code)
        if ($sounds.Count -gt 0) { $meta.animationSounds = [object[]]$sounds }
    }
    return $meta
}
# ===================================================================================

if (-not (Test-Path $Source)) { throw "Source export not found: $Source" }

$src = Get-Content $Source -Raw | ConvertFrom-Json
if (-not $src.animations -or @($src.animations).Count -eq 0) {
    throw "Export '$Source' has no animations array (or it is empty)."
}

# Сохраняем ПОРЯДОК экспорта (ConvertFrom-Json -> массив в исходном порядке), регистрируем КАЖДУЮ анимацию.
$byCode = [ordered]@{}
foreach ($a in $src.animations) {
    if (-not $a.code) { throw "Export '$Source' has an animation without a 'code' field." }
    if ($byCode.Contains($a.code)) { throw "Export '$Source' has a duplicate animation code '$($a.code)'." }
    $byCode[$a.code] = $a
}
$codes = @($byCode.Keys)

# --- Проверка парности tp/`-fp` (мягкая: WARNING, не throw) ------------------------------------------------
# Конвенция мода: каждая held-анимация = пара <code> (3-е лицо) + <code>-fp (1-е лицо). Распил/woodchop
# проверяют это жёстким throw по фикс-списку; обдирка код-агностична, поэтому только предупреждаем — это
# ловит «экспортнул tp, забыл fp» (или наоборот), не мешая частичным/будущим наборам.
foreach ($c in $codes) {
    if ($c.EndsWith("-fp")) {
        $base = $c.Substring(0, $c.Length - 3)
        if (-not $byCode.Contains($base)) {
            Write-Host ("WARNING  '{0}' has no third-person base '{1}' in the export." -f $c, $base) -ForegroundColor Yellow
        }
    } else {
        if (-not $byCode.Contains("$c-fp")) {
            Write-Host ("WARNING  '{0}' has no first-person variant '{0}-fp' in the export (1st person will fall back / look wrong)." -f $c) -ForegroundColor Yellow
        }
    }
}

# --- rotShortestDistance ОБЯЗАН быть false для БЛЕНДЯЩЕЙСЯ удерживаемой анимации (см. apply-animation.ps1).
# Движковый ElementPose.Add игнорирует вес бленда на оси с rotShortestDistance=true → поза снапается на
# кадре 0. Дефолт поля в движке = false, и debarking-экспорт его обычно не пишет (тогда и так false) — но
# если редактор вдруг выставил true, срезаем здесь.
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

# --- Обработка концов — ФОРСИМ под зацикленную протяжку (как у распила) -------------------------------------
#   onAnimationEnd    = Repeat   -> пока держишь ПКМ, инструмент ходит туда-сюда (кадр зацикливается).
#   onActivityStopped = EaseOut  -> при отпускании код вызывает StopAnimation, и поза плавно угасает.
# Редактор экспортирует протяжку как одноразовую (EaseOut/PlayTillEnd) — это «один проход и стоп», не годится.
function Set-EndHandling($anim) {
    if ($anim.PSObject.Properties.Name -contains 'onAnimationEnd') { $anim.onAnimationEnd = 'Repeat' }
    else { $anim | Add-Member -NotePropertyName 'onAnimationEnd' -NotePropertyValue 'Repeat' }
    if ($anim.PSObject.Properties.Name -contains 'onActivityStopped') { $anim.onActivityStopped = 'EaseOut' }
    else { $anim | Add-Member -NotePropertyName 'onActivityStopped' -NotePropertyValue 'EaseOut' }
}

foreach ($c in $codes) {
    Disable-RotShortestDistance $byCode[$c]
    Set-EndHandling $byCode[$c]
}

# Патч: сперва ВСЕ метаданные клиентских анимаций (player), затем ВСЕ анимации формы (seraph-faceless) —
# порядок как у распила. Звук (T03.04) вешается на TP-коды (НЕ `-fp`), кадры — явные по инструменту (New-DebarkSounds).
$patch = @()
foreach ($c in $codes) {
    $isTp = -not $c.EndsWith("-fp")   # звук только на 3-е лицо (TP); `-fp` тикается тем же аниматором → задвоилось бы
    $patch += [ordered]@{ file = "game:entities/humanoid/player"; op = "Add"; path = "/client/animations/-"; value = (New-Meta $c $isTp) }
}
foreach ($c in $codes) {
    $patch += [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode[$c] }
}

# UTF-8 без BOM, читаемый JSON (как у распильного/woodchop патча)
[System.IO.File]::WriteAllText($OutFile, (ConvertTo-Json -InputObject $patch -Depth 100))
Write-Host "OK  patch written: $OutFile" -ForegroundColor Green
Write-Host ("    registered {0} animation(s): {1}" -f $codes.Count, ($codes -join ', ')) -ForegroundColor Green

foreach ($c in $codes) {
    $last = ($byCode[$c].keyframes | Measure-Object -Property frame -Maximum).Maximum
    Write-Host ("    {0,-24} quantityframes={1}  lastKeyframe={2}  onAnimationEnd={3}  onActivityStopped={4}" -f `
        $c, $byCode[$c].quantityframes, $last, $byCode[$c].onAnimationEnd, $byCode[$c].onActivityStopped)
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
