<#
  apply-animation.ps1 — превращает «сырой» экспорт анимации woodchop в патч-файл мода.

  Берёт экспорт из Blender / редактора анимаций VS (сырой { "animations": [...] }) и пишет
  assets/immersivewoodworking/patches/woodchop_animations.json — патч из 4 записей, который
  игра реально загружает (2 метаданные клиентских анимаций + 2 анимации формы (shape)).

  Запускай после каждого ре-экспорта. Кейфреймы берутся из ТВОЕГО экспорта 1:1; сверху добавляется
  только слой бленд-метаданных — настраивай его в блоке «BLEND-МЕТАДАННЫЕ».

  Использование (Windows PowerShell 5.1 — работает из cmd и из PowerShell):
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-animation.ps1                 # пересобрать патч-файл
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-animation.ps1 -Build          # + скомпилировать (Release)
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-animation.ps1 -Build -Run     # + запустить клиент
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-animation.ps1 -Source "C:\other\export.json" -Run
#>

param(
    # Raw Blender / VS animation-editor export. Defaults to the repo's art\Animations folder, resolved
    # RELATIVE to this script (tools\ -> ..\art\Animations), so it works wherever the
    # repo is checked out. Override with -Source. (The exports were moved under an Animations\ subfolder alongside
    # the .blend sources; shapes/audio stay where they were.)
    [string]$Source = (Join-Path $PSScriptRoot "..\art\Animations\woodchop_animations.json"),
    # VS install dir — only needed for -Build / -Run. Defaults to the VINTAGE_STORY env var (as the .csproj uses);
    # the build/run block below throws a clear error if it's needed but unset (no machine-specific literal fallback).
    [string]$VintageStory = $env:VINTAGE_STORY,
    [switch]$Build,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$OutFile    = Join-Path $ProjectDir "assets\immersivewoodworking\patches\woodchop_animations.json"

if (($Build -or $Run) -and -not $VintageStory) {
    throw "VINTAGE_STORY is not set. Set the VINTAGE_STORY environment variable to your Vintage Story install directory, or pass -VintageStory <path>."
}

# ============================ BLEND-МЕТАДАННЫЕ (настраивать здесь) ============================
# Применяется к ОБЕИМ анимациям woodchop и woodchop-fp. Это слой «как оно блендится», которого в сыром
# экспорте из Blender НЕТ. ВАЖНО: числа ниже — это ФАКТИЧЕСКИ отгруженные, ПОДОБРАННЫЕ В ИГРЕ значения
# (держи их в синхроне с функцией New-Meta ниже и с готовым woodchop_animations.json). Это уже НЕ
# чисто-нулевая схема «двигаются только руки» из reference-мода ImmersiveWoodSawing — веса подняты, чтобы
# замах читался и корректно переходил из/в idle (это и был баг «не хватает весов»). Ниже — что делает
# каждое значение и в какую сторону крутить.
#
# weight = 0.25 (база): вес для каждого элемента, НЕ перечисленного в elementWeight (root, голова, шея).
#   При 0 эти кости полностью остаются в idle/walk (исходная идея «только руки»); при 0.25 они на четверть
#   тянутся к позе из кейфреймов. Сырой экспорт кеймит ВСЁ тело, поэтому слишком большой базовый вес
#   заставляет верх «плыть»/задваиваться поверх idle; 0.25 — подобранный компромисс. НОГИ (ступни/голени)
#   больше НЕ управляются этим базовым весом — они явно перечислены в elementWeight с весом 0.001, т.е.
#   практически исключены из бленда (см. коммент у elementWeight). Хочешь неподвижным ещё и верх (голову/шею)
#   — либо ставь базовый вес в 0.001, либо добавляй их в elementWeight так же, как ноги.
#
# weightCapFactor = 1: формула движка CalcBlendedWeight = EasingFactor / max(weightCapFactor, weightSum).
#   При НАШИХ больших весах рук (400, см. ниже) weightSum всегда >> 1, поэтому max() берёт weightSum и
#   конкретное значение капа тут почти ни на что не влияет — 1 это просто «вес не превышает 1». (Для
#   справки: при МАЛЫХ весах кап важен — 0 сокращает easing (E/E=1) и поза СНАПается вкл/выкл, как у
#   зацикленной пилы; <1 даёт плавный ease. Нам это сейчас не нужно из-за больших весов рук.)
#
# easeInSpeed = 10 / easeOutSpeed = 1: плавность ВХОДА / ВЫХОДА веса. Работают только потому, что
#   rotShortestDistance срезается ниже (иначе вращение игнорирует вес бленда). Кадры анимации продвигаются
#   на полной скорости, а вес поднимается ОТДЕЛЬНО через easeInSpeed: tau = 30/easeInSpeed кадров, т.е. при
#   10 это ~3 кадра до ~63% веса. Слишком мало -> вес не успевает за кадрами, замах ВВЕРХ идёт на неполном
#   весе, усредняется с idle («начинается снизу», руки не доводятся за голову); 20+ -> почти снап. 10 —
#   подобранное значение; easeOutSpeed=1 даёт мягкий сход при отпускании и на амплитуду не влияет.
#
# Разбивка elementWeight: руки доминируют (400), торс ИСКЛЮЧЁН (0.001 — не кренится вперёд, см. коммент ниже),
#   ItemAnchor высокий (100, Average), чтобы топор следовал за рукой. Почему руки так велики: пока держишь ПКМ на блоке, ванила
#   параллельно крутит `interactstatic` (поза «тянусь к блоку») с весом ПРАВОЙ руки = 8 (AddAverage), плюс
#   `idle1` (Average, вес 1 на все кости). Итоговая доля = ourWeight / (ourWeight + конкуренты). При весе
#   руки 400: правая рука ≈ 400/(400+8+1) ≈ 0.98 — амплитуда практически полная. (Для сравнения вес 20 дал
#   бы 20/29 ≈ 0.69 -> замах НЕ доводится, тянется к interactstatic — это и был баг «не хватает весов».)
#   Нужно ещё чище — поднимай руки выше; нужен более вялый замах — опускай. Левую руку interactstatic почти
#   не трогает, но держим симметрично.
function New-Meta($code) {
    [ordered]@{
        code                  = $code
        animation             = $code
        animationSpeed        = 0.5            # переопределяется в рантайме через StartChopAnimation в коде
        blendMode             = "AddAverage" # базовый режим бленда для элементов без override в elementBlendMode; влияет на неперечисленные кости (база weight=0.25, не 0) — оставлен явным для ясности / будущих per-element добавлений
        weight                = 0.25            # базовый вес неперечисленных костей (root/голова/шея) = 25% позы замаха; подобранное значение (не 0 — см. шапку «только руки»). Ноги вынесены в elementWeight=0.001 (исключены)
        weightCapFactor       = 1          # потолок: вес не превышает 1. При наших больших весах рук weightSum доминирует в max(cap,weightSum), так что кап тут почти ни на что не влияет — см. шапку
        easeInSpeed           = 10           # скорость набора веса; tau=30/easeInSpeed кадров (10 -> ~3 кадра до ~63%). Ниже = недобор амплитуды («старт снизу»), 20+ = почти снап. Подобрано
        easeOutSpeed          = 1            # мягкий выход при отпускании (на амплитуду замаха не влияет)
        holdEyePosAfterEasein = 0.1
        # НОГИ и ТОРС ИСКЛЮЧЕНЫ из бленда: замах не должен трогать ступни/голени НИ корпус (LowerTorso/UpperTorso).
        # Иначе низ «плывёт» поверх idle, а торс кренится вперёд (rotationZ до ~18° + сдвиг LowerTorso вниз-вперёд) —
        # это и есть «персонаж неестественно отклоняется вперёд» в 3-м лице. Теперь весь корпус держит стойку idle,
        # рубят только руки. Вес 0.001 (а НЕ 0) — намеренно: движок делит weightSum / sew.Weight
        # (ClientAnimator.calculateMatrices), поэтому ровно 0 даёт 0/0 = NaN и ломает позу кости в кадры, когда ни
        # одна другая анимация этой кости не набрала вес. Ванила по той же причине использует 0.001 для «двигаются
        # только руки» (см. helditemready в game player.json: "A value of 0 seems to break things"). 0.001 -> вклад
        # замаха ~0.1%, визуально ноль. НУЖЕН лёгкий естественный наклон корпуса обратно — подними торс до ~0.2-0.3
        # (НЕ ноги; при малом весе торса замах читается мягче, чем прежние 0.8).
        elementWeight    = [ordered]@{ UpperArmL = 400; LowerArmL = 400; UpperArmR = 400; LowerArmR = 400; UpperTorso = 0.001; LowerTorso = 0.001; ItemAnchor = 100; UpperFootL = 0.001; LowerFootL = 0.001; UpperFootR = 0.001; LowerFootR = 0.001 }
        elementBlendMode = [ordered]@{ UpperArmL = "AddAverage"; LowerArmL = "AddAverage"; UpperArmR = "AddAverage"; LowerArmR = "AddAverage"; UpperTorso = "Average"; LowerTorso = "Average"; ItemAnchor = "Average" }
    }
}
# ===================================================================================

if (-not (Test-Path $Source)) { throw "Source export not found: $Source" }

$src = Get-Content $Source -Raw | ConvertFrom-Json
$byCode = @{}
foreach ($a in $src.animations) { $byCode[$a.code] = $a }
foreach ($need in @('woodchop', 'woodchop-fp')) {
    if (-not $byCode.ContainsKey($need)) { throw "Export '$Source' is missing animation '$need'" }
}

# --- rotShortestDistance ОБЯЗАН быть выключен для БЛЕНДЯЩЕЙСЯ удерживаемой анимации -------------------
# Движковый ElementPose.Add ИГНОРИРУЕТ вес бленда на любой оси вращения с флагом
# rotShortestDistance=true (отдельная ветка кода: `deg += tf.deg + dist*l`, БЕЗ умножения на *weight).
# Поэтому при ВКЛ easeInSpeed / easeOutSpeed / weightCapFactor НИЧЕГО не делают для вращения — замах
# снапается в полную позу на кадре 0 вместо плавного входа (баг «анимация прыгает сразу в замах над
# головой»). Редактор анимаций экспортирует их =true по умолчанию, поэтому мы срезаем их при каждой сборке.
# Нюанс: вращение тогда интерполируется ЛИНЕЙНО в «сырых» градусах между кейфреймами (а не по кратчайшей
# дуге). Результат идентичен, ЕСЛИ два СОСЕДНИХ кейфрейма не отличаются больше чем на 180° по одной оси
# (иначе пойдёт длинным путём). Держи повороты соседних кейфреймов в пределах 180° на ось (текущий замах — с запасом).
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
foreach ($c in @('woodchop', 'woodchop-fp')) { Disable-RotShortestDistance $byCode[$c] }

# Обработка концов (onAnimationEnd / onActivityStopped) берётся прямо из ТВОЕГО экспорта — задавай её в
# редакторе анимаций, НЕ здесь. Для этого мода замаху нужно:
#   onAnimationEnd    = Hold     -> на кадре удара поза фиксируется. При EaseOut/Repeat кадр отматывается
#                                   назад к замаху, пока держишь ПКМ = баг «топор отскакивает обратно за
#                                   голову». Поэтому Hold обязателен.
#   onActivityStopped = EaseOut  -> при отпускании код вызывает StopAnimation, и зафиксированная поза
#                                   плавно угасает. См. BlockChoppingBlock.OnBlockInteractStop.
# Мы больше не переопределяем это; только ПРЕДУПРЕЖДАЕМ, если экспорт принёс настройку, которая вернёт
# отскок, — чтобы ре-экспорт с неверной настройкой ловился здесь, а не в игре.
foreach ($c in @('woodchop', 'woodchop-fp')) {
    if ($byCode[$c].onAnimationEnd -ne 'Hold') {
        Write-Host ("WARNING  {0}.onAnimationEnd = '{1}' (expected 'Hold'). EaseOut/Repeat reintroduce the swing bounce. Set 'On animation end = Hold' in the animation editor and re-export." -f `
            $c, $byCode[$c].onAnimationEnd) -ForegroundColor Yellow
    }
    if ($byCode[$c].onActivityStopped -ne 'EaseOut') {
        Write-Host ("WARNING  {0}.onActivityStopped = '{1}' (expected 'EaseOut'). The API default is Rewind, which rewinds the frame on release instead of letting the held chop pose ease out (see BlockChoppingBlock.OnBlockInteractStop). Set 'On activity stopped = EaseOut' in the animation editor and re-export." -f `
            $c, $byCode[$c].onActivityStopped) -ForegroundColor Yellow
    }
}

$patch = @(
    [ordered]@{ file = "game:entities/humanoid/player";              op = "Add"; path = "/client/animations/-"; value = (New-Meta "woodchop") }
    [ordered]@{ file = "game:entities/humanoid/player";              op = "Add"; path = "/client/animations/-"; value = (New-Meta "woodchop-fp") }
    [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode['woodchop'] }
    [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode['woodchop-fp'] }
)

# UTF-8 без BOM, читаемый JSON
[System.IO.File]::WriteAllText($OutFile, (ConvertTo-Json -InputObject $patch -Depth 100))
Write-Host "OK  patch written: $OutFile" -ForegroundColor Green

# Инфо по кадрам (справочно). Импакт-кадр мод читает в РАНТАЙМЕ (BlockChoppingBlock.GetImpactFrame) —
# последний кейфрейм загруженной woodchop, так что для держащего игрока ре-экспорт тайминг не
# рассинхронит, хардкод-константы на основном пути больше нет. НО на случай, когда анимацию не прочитать
# с сущности (не-seraph шейп игрока; возможно, серверный аниматор), в коде остаётся запасное значение
# `const int FallbackImpactFrame` в BlockChoppingBlock.cs. Если импакт-кадр сменился — синхронизируй его
# (см. напоминание ниже). Держи импакт на ПОСЛЕДНЕМ кейфрейме (onAnimationEnd=Hold его фиксирует).
foreach ($c in 'woodchop', 'woodchop-fp') {
    $last = ($byCode[$c].keyframes | Measure-Object -Property frame -Maximum).Maximum
    Write-Host ("    {0,-12} quantityframes={1}  lastKeyframe={2}  onAnimationEnd={3}  onActivityStopped={4}" -f `
        $c, $byCode[$c].quantityframes, $last, $byCode[$c].onAnimationEnd, $byCode[$c].onActivityStopped)
}
$impact = ($byCode['woodchop'].keyframes | Measure-Object -Property frame -Maximum).Maximum
Write-Host ("    -> if the impact frame changed, keep  const int FallbackImpactFrame = {0};  in BlockChoppingBlock.cs in sync (fallback only - used when the animation can't be read off the entity)." -f $impact) -ForegroundColor Yellow

if ($Build -or $Run) {
    $env:VINTAGE_STORY = $VintageStory
    Write-Host "`nBuilding (Release, IW_DEBUG on)..." -ForegroundColor Cyan
    # -p:IwDebug=true defines the IW_DEBUG compile symbol (see the csproj), so the [IW-DBG] diagnostic
    # logging is INCLUDED in tools builds. The csproj also redirects this build to a SEPARATE output dir
    # (bin\Release-iwdebug\Mods\mod), so it can never be mistaken for / packaged as the shipped release.
    # A plain `dotnet build -c Release` (the shipped release) omits the flag — no logging, zero cost.
    dotnet build (Join-Path $ProjectDir "ImmersiveWoodworking.csproj") -c Release -p:IwDebug=true --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if ($Run) {
    Write-Host "`nLaunching client..." -ForegroundColor Cyan
    # Дев-сборка (IwDebug=true) пишется в ОТДЕЛЬНЫЙ bin\Release-iwdebug\Mods (см. OutputPath под IwDebug в
    # csproj) — запускаемся именно оттуда. Собранный мод там уже содержит ассеты (копируются при сборке).
    # НЕ передавай дополнительно --addOrigin assets — это загрузит тот же домен "immersivewoodworking"
    # второй раз (двойной origin) и запутает, какую копию реально использует игра.
    Start-Process -FilePath "dotnet" -WorkingDirectory $VintageStory -ArgumentList @(
        "$VintageStory\Vintagestory.dll", "--tracelog",
        "--addModPath", "$ProjectDir\bin\Release-iwdebug\Mods"
    ) | Out-Null
    Write-Host "OK  client launched." -ForegroundColor Green
}
