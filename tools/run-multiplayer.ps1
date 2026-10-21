<#
  run-multiplayer.ps1 — поднимает ЛОКАЛЬНЫЙ выделенный сервер + клиент для теста мода в мультиплеере.

  Зачем отдельный dedicated server, а не «Open to LAN»: он гоняется в СВОЁМ процессе с собственной
  памятью → честно разносит клиент и сервер. Именно это вскрывает класс MP-багов, которого НЕТ в
  синглплеере (где клиент+сервер живут в одном процессе и делят память): неверные Api.Side-гарды,
  «забытая» сериализация полей блок-сущностей, рассинхрон пакетов, порча общих полей. Один игрок на
  таком сервере уже ловит ~90% мультиплеерных проблем мода.

  ВАЖНО про два игрока с ОДНОГО аккаунта: так не выйдет. Оба клиента залогинятся под одним playerUID,
  и сервер вышибет первый сеанс («logged in from another location»). VerifyPlayerAuth=false это НЕ
  меняет (клиент всё равно шлёт свою аккаунт-личность). Для co-op-распиловки (два игрока на концах
  козел) нужен ВТОРОЙ аккаунт VS или друг, подключившийся к этому серверу.

  Что делает скрипт:
    1. (опц. -Reset) сносит данные тест-сервера → свежий мир.
    2. Готовит serverconfig.json под приватный локальный сервер (один раз; потом твои правки сохраняются).
       Делает это в ДВА хода — генерация + патч (почему именно так — см. «ДВА УРОКА» у кода ниже):
         сначала даёт серверу сгенерировать ПОЛНЫЙ дефолтный конфиг (9 ролей вкл. admin + валидный мир в нашей
         dataPath), потом патчит:
           WhitelistMode = Off      — иначе dedicated-сервер пускает ТОЛЬКО вайтлист и тебя не впустит.
           VerifyPlayerAuth = false — offline: не ходит на мастер-сервер авторизации (работает и без сети).
           DefaultRoleCode = admin  — заходишь сразу админом: /gamemode 2, /give для спавна пилы/брёвен/козел.
           ModPaths += папка модов клиента + dev-сборка — паритет модов клиент↔сервер (иначе join отвалится).
           AdvertiseServer/Upnp off, Password пуст. WorldConfig НЕ трогаем: сервер сам прописал SaveFileLocation
           ВНУТРИ dataPath (твой реальный сейв не затрагивается).
    3. Запускает VintagestoryServer.dll в своём окне-консоли (там же командуешь: /stop, /time set day).
    4. (если не -NoClient) запускает клиент так же, как test.bat. Авто-коннекта в VS нет — подключайся
       вручную: Multiplayer → добавить сервер 127.0.0.1 → Connect (адрес сохранится на будущее).

  Паритет модов: клиент грузит моды из %APPDATA%\VintagestoryData\Mods + dev-сборку; серверу те же ДВА
  источника прописываются в ModPaths конфига, поэтому набор модов на обеих сторонах идентичен (иначе join
  отвалится по рассинхрону). Дубль immersivewoodworking (релизный .zip + dev) обе стороны разрешают одинаково —
  по наибольшей версии (dev), так что это безопасно.

  Использование (Windows PowerShell 5.1):
    powershell -ExecutionPolicy Bypass -File tools\run-multiplayer.ps1            # сервер + клиент
    powershell -ExecutionPolicy Bypass -File tools\run-multiplayer.ps1 -Reset     # + снести мир тест-сервера
    powershell -ExecutionPolicy Bypass -File tools\run-multiplayer.ps1 -NoClient  # только сервер (для друга/2-го аккаунта)
    powershell -ExecutionPolicy Bypass -File tools\run-multiplayer.ps1 -Port 42421
  Обычно запускается из test_multiplayer.bat (он сначала пересобирает патчи анимаций и мод).
#>

param(
    # Папка установки VS. По умолчанию из env VINTAGE_STORY (как и в .csproj / apply-sawing-animation.ps1).
    [string]$VintageStory = $env:VINTAGE_STORY,
    # Изолированная папка данных тест-сервера (свой мир/логи/конфиг, отдельно от твоего синглплеера).
    [string]$ServerData = (Join-Path $env:APPDATA "VintagestoryDataMpTest"),
    [int]$Port = 42420,
    [switch]$NoClient,   # поднять только сервер
    [switch]$Reset       # снести данные тест-сервера перед стартом (свежий мир)
)

$ErrorActionPreference = "Stop"
$ProjectDir = Split-Path $PSScriptRoot -Parent
$ModPath    = Join-Path $ProjectDir "bin\Release-iwdebug\Mods"          # dev-сборка (как в test.bat)
$ClientMods = Join-Path $env:APPDATA "VintagestoryData\Mods"            # моды клиента — для паритета

if (-not $VintageStory) {
    throw "VINTAGE_STORY не задан. Установи переменную окружения VINTAGE_STORY на папку установки Vintage Story, либо передай -VintageStory <путь>."
}
if (-not (Test-Path (Join-Path $VintageStory "VintagestoryServer.dll"))) {
    throw "VintagestoryServer.dll не найден в '$VintageStory'."
}
if (-not (Test-Path $ModPath)) {
    throw "Dev-сборка не найдена в '$ModPath'. Сначала собери мод (test_multiplayer.bat делает это сам, либо apply-sawing-animation.ps1 -Build)."
}

if ($Reset -and (Test-Path $ServerData)) {
    Write-Host "Reset: удаляю $ServerData" -ForegroundColor Yellow
    Remove-Item $ServerData -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $ServerData | Out-Null

# ----------------------- конфиг тест-сервера: ГЕНЕРАЦИЯ + ПАТЧ (только при первом запуске) -----------------------
# При повторных запусках файл уже есть → не трогаем, чтобы сохранились твои правки.
#
# ДВА УРОКА, добытые кровью (проверено в игре):
#   1) Серверу НЕЛЬЗЯ давать моды через CLI `--addModPath` — он падает с NullReferenceException в
#      ServerProgram..ctor() ещё ДО инициализации логов (поэтому ни лога, ни окна — просто мгновенный выход).
#      У КЛИЕНТА --addModPath работает (см. test.bat), у СЕРВЕРА — нет. Серверу мод-пути задаются ТОЛЬКО
#      через поле `ModPaths` в serverconfig.json.
#   2) Частичный/самописный конфиг НЕ получает дефолтные роли (Roles остаётся пустым) → DefaultRoleCode="admin"
#      не к чему привязать, и обнулённый/неполный WorldConfig роняет SaveGame.SetNewWorldConfig при создании мира.
# Поэтому: сперва даём серверу СГЕНЕРИРОВАТЬ полный валидный конфиг (9 ролей вкл. admin + корректный WorldConfig,
# где SaveFileLocation уже ВНУТРИ нашей dataPath — изолировано от твоего реального сейва), затем патчим пару полей.
$srvCfg = Join-Path $ServerData "serverconfig.json"
if (-not (Test-Path $srvCfg)) {
    Write-Host "Первый запуск: генерирую дефолтный конфиг сервера..." -ForegroundColor Cyan
    $genProc = Start-Process -FilePath "dotnet" -WorkingDirectory $VintageStory -PassThru -WindowStyle Hidden `
        -ArgumentList @("$VintageStory\VintagestoryServer.dll", "--dataPath", $ServerData)
    for ($i = 0; $i -lt 40 -and -not (Test-Path $srvCfg); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 800                       # дать серверу дописать файл
    try { Stop-Process -Id $genProc.Id -Force -ErrorAction SilentlyContinue } catch {}
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path $srvCfg)) { throw "Не удалось сгенерировать serverconfig.json (сервер не стартовал?)." }
    # снести возможный недописанный мир/кэш от фазы генерации — реальный запуск создаст чистый мир заново
    foreach ($sub in 'Saves', 'BackupSaves', 'Backups', 'Cache') {
        $sp = Join-Path $ServerData $sub
        if (Test-Path $sp) { Remove-Item $sp -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # --- патч сгенерированного (полного, валидного) конфига ---
    $cfg = Get-Content $srvCfg -Raw | ConvertFrom-Json
    function Set-Prop($o, $n, $v) {
        if ($o.PSObject.Properties.Name -contains $n) { $o.$n = $v }
        else { $o | Add-Member -NotePropertyName $n -NotePropertyValue $v }
    }
    Set-Prop $cfg "ServerName"       "ImmersiveWoodworking MP Test"
    Set-Prop $cfg "Port"             $Port
    Set-Prop $cfg "Upnp"             $false
    Set-Prop $cfg "AdvertiseServer"  $false             # не светить сервер в публичном списке
    Set-Prop $cfg "Password"         $null
    Set-Prop $cfg "WhitelistMode"    1                  # 1 = Off (0=Default у dedicated = только вайтлист!)
    Set-Prop $cfg "OnlyWhitelisted"  $false
    Set-Prop $cfg "VerifyPlayerAuth" $false             # offline: без обращения к мастер-серверу авторизации
    Set-Prop $cfg "DefaultRoleCode"  "admin"            # заходишь админом (gamemode/give для спавна тест-предметов)
    # мод-пути серверу — через ModPaths (НЕ через CLI!). К дефолтным ("Mods" + <dataPath>\Mods) добавляем папку
    # модов клиента и dev-сборку → набор модов на сервере совпадает с клиентом (паритет обязателен для join).
    $paths = @()
    if (($cfg.PSObject.Properties.Name -contains 'ModPaths') -and $cfg.ModPaths) { $paths += @($cfg.ModPaths) }
    foreach ($mp in @($ClientMods, $ModPath)) { if ($paths -notcontains $mp) { $paths += $mp } }
    Set-Prop $cfg "ModPaths" $paths
    # WorldConfig НЕ трогаем: сервер уже прописал SaveFileLocation ВНУТРИ $ServerData и валидный PlayStyle.

    [System.IO.File]::WriteAllText($srvCfg, (ConvertTo-Json -InputObject $cfg -Depth 100))
    Write-Host "OK  конфиг готов  (WhitelistMode=Off, DefaultRole=admin, VerifyPlayerAuth=false; моды через ModPaths)" -ForegroundColor Green
} else {
    Write-Host "Использую существующий $srvCfg  (правь его руками; для пересоздания запусти с -Reset)" -ForegroundColor DarkGray
}

# ----------------------- запуск сервера (своё окно-консоль) -----------------------
Write-Host "`nЗапускаю выделенный сервер (порт $Port, данные: $ServerData)..." -ForegroundColor Cyan
# ВНИМАНИЕ: серверу НЕ передаём --addModPath (он на нём падает, см. урок №1 выше) — моды уже в ModPaths конфига.
Start-Process -FilePath "dotnet" -WorkingDirectory $VintageStory -ArgumentList @(
    "$VintageStory\VintagestoryServer.dll",
    "--dataPath", $ServerData,
    "--tracelog"
) | Out-Null
Write-Host "OK  сервер стартует. Жди в его консоли строку 'Dedicated Server now running' (первый раз — генерит мир, ~15-30с)." -ForegroundColor Green
Write-Host "    Команды в консоли сервера: /stop (выключить), /time set day. Заходишь сразу админом." -ForegroundColor DarkGray

# ----------------------- запуск клиента (как в test.bat) -----------------------
if (-not $NoClient) {
    Write-Host "`nЗапускаю клиент..." -ForegroundColor Cyan
    Start-Process -FilePath "dotnet" -WorkingDirectory $VintageStory -ArgumentList @(
        "$VintageStory\Vintagestory.dll", "--tracelog",
        "--addModPath", $ModPath
    ) | Out-Null
    Write-Host "OK  клиент запущен." -ForegroundColor Green
    Write-Host "`n  -> В клиенте: Multiplayer -> Add Server -> 127.0.0.1:$Port -> Connect (адрес сохранится)." -ForegroundColor Yellow
    Write-Host "     Второго игрока с ЭТОГО аккаунта не подключить (конфликт playerUID) — нужен 2-й аккаунт или друг." -ForegroundColor Yellow
}
