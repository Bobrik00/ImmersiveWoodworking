<#
  apply-sawing-animation.ps1 — превращает «сырой» экспорт анимации распила (sawing) в патч-файл мода.

  Сестринский к apply-animation.ps1 (тот — про замах топора woodchop). Берёт экспорт из Blender /
  редактора анимаций VS (сырой { "animations": [ sawing, sawing-fp ] }) и пишет
  assets/immersivewoodworking/patches/sawing_animations.json — патч из 4 записей, который игра реально
  загружает (2 метаданные клиентских анимаций + 2 анимации формы (shape) на seraph-faceless).

  Запускай после каждого ре-экспорта. Кейфреймы берутся из ТВОЕГО экспорта 1:1; сверху добавляется только
  слой бленд-метаданных («BLEND-МЕТАДАННЫЕ») и ФОРСИРУЕТСЯ обработка концов под зацикленный распил.

  Чем распил отличается от замаха (woodchop):
    • Анимация ЗАЦИКЛЕНА, пока зажата ПКМ: onAnimationEnd = Repeat (пила ходит туда-сюда). У woodchop —
      Hold (одиночный замах фиксируется на кадре удара). Мы ФОРСИМ Repeat здесь, потому что редактор
      экспортирует распил как EaseOut/PlayTillEnd (одноразовый), а нам нужен цикл.
    • Нет «импакт-кадра» и нет рантайм-переопределения скорости: animationSpeed из New-Meta — это РЕАЛЬНАЯ
      скорость воспроизведения (woodchop пересчитывал её под ChopSeconds в коде; распил — нет).
    • Триггер не на предмете, а на блоке: BlockSawhorse.OnBlockInteractStart запускает StartAnimation("sawing")
      ТОЛЬКО когда TryStartSawing прошёл гейты (pit saw + бревно + игрок с торца). Held-use анимация движка
      (interactstatic) играет на ЛЮБОМ зажатии ПКМ, поэтому она тут не подходит — отсюда ручной Start/Stop.

  Использование (Windows PowerShell 5.1):
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawing-animation.ps1            # пересобрать патч
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawing-animation.ps1 -Build      # + Release
    powershell -ExecutionPolicy Bypass -File path\to\tools\apply-sawing-animation.ps1 -Build -Run # + запустить клиент
#>

param(
    # Raw Blender / VS animation-editor export. Defaults to the repo's art\Animations folder, resolved
    # RELATIVE to this script (tools\ -> ..\art\Animations), so it works wherever the
    # repo is checked out. Override with -Source. (The exports were moved under an Animations\ subfolder alongside
    # the .blend sources; shapes/audio stay where they were.)
    [string]$Source = (Join-Path $PSScriptRoot "..\art\Animations\sawing_animations.json"),
    # VS install dir — only needed for -Build / -Run. Defaults to the VINTAGE_STORY env var (as the .csproj uses);
    # the build/run block below throws a clear error if it's needed but unset (no machine-specific literal fallback).
    [string]$VintageStory = $env:VINTAGE_STORY,
    [switch]$Build,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$OutFile    = Join-Path $ProjectDir "assets\immersivewoodworking\patches\sawing_animations.json"

if (($Build -or $Run) -and -not $VintageStory) {
    throw "VINTAGE_STORY is not set. Set the VINTAGE_STORY environment variable to your Vintage Story install directory, or pass -VintageStory <path>."
}

# ============================ BLEND-МЕТАДАННЫЕ (настраивать здесь) ============================
# Применяется к ОБЕИМ анимациям sawing и sawing-fp. Слой «как оно блендится», которого в сыром экспорте нет.
# Веса подобраны теми же соображениями, что и для woodchop (см. apply-animation.ps1, подробная шапка там):
# пока держишь ПКМ на козлах, движок параллельно крутит `interactstatic` (вес правой руки 8, AddAverage) и
# `idle1` (вес 1 на все кости). Чтобы распил читался на полную, веса рук подняты до 400 (доля ≈ 400/409 ≈ 0.98).
# Распил ДВУРУЧНЫЙ — поэтому ОБЕ руки одинаково тяжёлые (у топора правая ведущая, но симметрия не вредит).
#
#   weight = 0.25 (база): вес неперечисленных костей (ноги/ступни/голова/шея) — четверть позы; низ слегка
#     подстраивается под стойку у козел. 0 = низ полностью в idle (если будет «плыть» — ставь 0).
#   easeInSpeed = 8: набор веса на старте (~3 кадра до 63%) — мягче woodchop-овых 10, цикличной пиле резкий
#     снап на старте не нужен.
#   easeOutSpeed = 12: УГАСАНИЕ веса при отпускании ПКМ. Движок гасит EasingFactor экспоненциально
#     (RunningAnimation.cs: `EasingFactor -= EasingFactor*dt*easeOutSpeed`) и снимает анимацию при
#     EasingFactor < 0.002, поэтому полное угасание ≈ 6.2/easeOutSpeed сек (до ~10% ≈ 2.3/easeOutSpeed сек).
#     Было 1 = ≈6 сек «медленного доедания» распила после отпускания (пила ещё долго еле-еле ходит, т.к.
#     цикл Repeat крутится, пока вес ползёт к нулю) — это и был баг. 12 ≈ 0.5 сек. Резче — поднимай,
#     мягче — опускай (дефолт движка = 10).
#   elementWeight: руки доминируют (400), торс — лёгкий доворот (0.8, Average), ItemAnchor высокий (100,
#     Average), чтобы пила следовала за руками.
# --- Звук распила: НАТИВНЫЙ механизм движка `animationSounds` (НЕ код) -------------------------------------
# `AnimationMetaData.AnimationSounds` (vsapi) — массив звуков, привязанных к КАДРУ анимации: `ClientAnimator`
# проигрывает каждый раз, когда `CurrentFrame >= frame` (раз за итерацию цикла, трекается `SoundPlayedAtIteration`).
# Это даёт ИДЕАЛЬНУЮ синхронизацию звука с руками БЕЗ кода и КОРРЕКТНО в мультиплеере: каждый клиент, который
# рендерит анимацию (и сам пилильщик, и наблюдатели — анимация синкается сервером), проигрывает штрих в такт
# СВОЕМУ кадру. Заменяет прежний ручной серверный троттл по времени (он «плыл» относительно фаз анимации).
#
# КЛАДЁМ ТОЛЬКО НА `sawing` (3-е лицо), НЕ на `sawing-fp`: в 1-м лице TP-аниматор всё равно тикается
# (`PlayerAnimationManager.OnClientFrame` явно тикает `TpAnimManager`), поэтому звук сыграет ОДИН раз во всех
# режимах; добавь его и на `-fp` — и в 1-м лице звук задвоится (играют оба аниматора).
#
# Два штриха за цикл, ПО ФАЗАМ движения: sawing_ff* — ход ВПЕРЁД (медленный толчок 1→10, 9 кадров ≈ 0.6с),
# триггер в середине (frame 5); sawing_fb* — ход НАЗАД (быстрый возврат 10→1, ~2 кадра ≈ 0.13с), триггер в начале
# возврата (frame 10). Это «fast»-набор (ff/fb) — ТОТ ЖЕ, что использует автоматическая лесопилка на полной
# скорости (BlockEntitySawmill.Effects.cs): ручной и авто-распил намеренно делят одни сэмплы. pitch =
# NatFloat(avg 1.0 / var 0.1 / uniform) → разлёт 0.9…1.1 (±10%): лёгкая
# вариативность без заметного искажения. Пресет randomizePitch давал ±0.25 (0.75…1.25) — звук сильно «плыл».
# Разлёт регулируется здесь полем var (0.05 = совсем чуть-чуть; dist можно сменить на gaussian — кучнее у 1.0).
# Зазоры между триггерами при animationSpeed 0.5 (15 кадр/с): ff1→fb1 = 5 кадров ≈ 0.333с, fb1→следующий
# ff1 = 6 кадров ≈ 0.4с. Длины сэмплов после пересведения (моно, верная герцовка) задаёт автор; если хвост
# «назад» наезжает на следующий толчок — это асимметрия кейфреймов (медленно вперёд, резко назад): чтобы
# «назад» звучал ровно по движению, кейфрейм 10 надо сдвинуть к ~6 (но это меняет вид анимации).
# `location` с `*` на конце — НАТИВНЫЙ рандом вариантов движком (как ванильные footstep-wolf-dirt* / bellowlarge-in*):
# sawing_ff* → случайно sawing_ff1/ff2 (ход вперёд), sawing_fb* → случайно sawing_fb1/fb2 (ход назад). Каждый клиент,
# рендерящий анимацию (ClientAnimator), сам тянет случайный вариант — кода НЕ нужно, синхро с руками сохраняется.
# Авто-префикс "sounds/" → файлы лежат в assets/immersivewoodworking/sounds/saw/. Добавить вариант = просто положить
# sawing_ff3.ogg / sawing_fb3.ogg (wildcard подхватит). Перетюнить кадры/файлы/радиус — здесь.
function New-SawSounds {
    @(
        [ordered]@{ frame = 5;  location = "immersivewoodworking:saw/sawing_ff*"; range = 16; pitch = [ordered]@{ avg = 1.0; var = 0.1; dist = "uniform" }; chance = 1 }
        [ordered]@{ frame = 10; location = "immersivewoodworking:saw/sawing_fb*"; range = 16; pitch = [ordered]@{ avg = 1.0; var = 0.1; dist = "uniform" }; chance = 1 }
    )
}

function New-Meta($code, $withSound = $false) {
    $meta = [ordered]@{
        code                  = $code
        animation             = $code
        animationSpeed        = 0.5            # РЕАЛЬНАЯ скорость (не переопределяется в коде). 11 кадров / (30*0.5) ≈ 0.73с на цикл «вперёд-назад». Медленнее — опускай.
        blendMode             = "AddAverage"
        weight                = 0.25
        weightCapFactor       = 1
        easeInSpeed           = 8            # набор веса на старте: tau=30/easeInSpeed кадров (~3 кадра до 63%)
        easeOutSpeed          = 12           # угасание веса при отпускании ПКМ: полное ≈ 6.2/easeOutSpeed сек. Было 1 (≈6 сек «доедания» распила — слишком долго); 12 ≈ 0.5 сек. Дефолт движка = 10. Резче — поднимай, мягче — опускай
        holdEyePosAfterEasein = 0.1
        elementWeight    = [ordered]@{ UpperArmL = 400; LowerArmL = 400; UpperArmR = 400; LowerArmR = 400; UpperTorso = 0.8; LowerTorso = 0.8; ItemAnchor = 100 }
        elementBlendMode = [ordered]@{ UpperArmL = "AddAverage"; LowerArmL = "AddAverage"; UpperArmR = "AddAverage"; LowerArmR = "AddAverage"; UpperTorso = "Average"; LowerTorso = "Average"; ItemAnchor = "Average" }
    }
    if ($withSound) { $meta.animationSounds = New-SawSounds }  # frame-synced saw strokes (tp variant only)
    return $meta
}
# ===================================================================================

if (-not (Test-Path $Source)) { throw "Source export not found: $Source" }

$src = Get-Content $Source -Raw | ConvertFrom-Json
$byCode = @{}
foreach ($a in $src.animations) { $byCode[$a.code] = $a }
foreach ($need in @('sawing', 'sawing-fp')) {
    if (-not $byCode.ContainsKey($need)) { throw "Export '$Source' is missing animation '$need'" }
}

# --- rotShortestDistance ОБЯЗАН быть false для БЛЕНДЯЩЕЙСЯ удерживаемой анимации (см. apply-animation.ps1).
# Движковый ElementPose.Add игнорирует вес бленда на оси с rotShortestDistance=true → поза снапается на кадре 0.
# Дефолт поля в движке = false, и экспорт распила его обычно вовсе не пишет (тогда и так false) — но если
# редактор вдруг выставил true, срезаем здесь.
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

# --- Обработка концов — ФОРСИМ под зацикленный распил (в отличие от woodchop, где брали из экспорта) -----
#   onAnimationEnd    = Repeat   -> пока держишь ПКМ, пила ходит туда-сюда (кадр зацикливается).
#   onActivityStopped = EaseOut  -> при отпускании код вызывает StopAnimation, и поза плавно угасает.
# Редактор экспортирует распил как одноразовый (EaseOut/PlayTillEnd) — это «один проход и стоп», нам не годится.
function Set-EndHandling($anim) {
    if ($anim.PSObject.Properties.Name -contains 'onAnimationEnd') { $anim.onAnimationEnd = 'Repeat' }
    else { $anim | Add-Member -NotePropertyName 'onAnimationEnd' -NotePropertyValue 'Repeat' }
    if ($anim.PSObject.Properties.Name -contains 'onActivityStopped') { $anim.onActivityStopped = 'EaseOut' }
    else { $anim | Add-Member -NotePropertyName 'onActivityStopped' -NotePropertyValue 'EaseOut' }
}

foreach ($c in @('sawing', 'sawing-fp')) {
    Disable-RotShortestDistance $byCode[$c]
    Set-EndHandling $byCode[$c]
}

$patch = @(
    [ordered]@{ file = "game:entities/humanoid/player";              op = "Add"; path = "/client/animations/-"; value = (New-Meta "sawing" $true) }
    [ordered]@{ file = "game:entities/humanoid/player";              op = "Add"; path = "/client/animations/-"; value = (New-Meta "sawing-fp") }
    [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode['sawing'] }
    [ordered]@{ file = "game:shapes/entity/humanoid/seraph-faceless"; op = "Add"; path = "/animations/-"; value = $byCode['sawing-fp'] }
)

# UTF-8 без BOM, читаемый JSON
[System.IO.File]::WriteAllText($OutFile, (ConvertTo-Json -InputObject $patch -Depth 100))
Write-Host "OK  patch written: $OutFile" -ForegroundColor Green

foreach ($c in 'sawing', 'sawing-fp') {
    $last = ($byCode[$c].keyframes | Measure-Object -Property frame -Maximum).Maximum
    Write-Host ("    {0,-12} quantityframes={1}  lastKeyframe={2}  onAnimationEnd={3}  onActivityStopped={4}" -f `
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
