<#
.SYNOPSIS
    Полная проверка SWORD2 — раздел 12 ARCHITECTURE_PLAN.md.

.DESCRIPTION
    Шаги:
      build                 сборка SWORD.slnx (предупреждения — ошибки);
      test                  тесты решения, в том числе архитектурные и GPU-тесты рендера (категория Gpu:
                            устройство D3D12 с GPU-валидацией; -SkipGpu — без них) и тесты на шрифтах пака
                            (категория Content; -SkipReference — без них);
      forbidden-references  заготовки tests/fixtures/forbidden-references/SWORD00N-*: каждая обязана упасть со своим
                            кодом — иначе проверка ссылок в сборке сломана;
      without-agent         App и Headless с -p:WithoutAgent=true собираются и не содержат сборок агента;
      headless-smoke        Sword.Headless: 600 тиков с командой «бег по +X» — итог JSON в порядке, игрок ушёл
                            по +X со скоростью бега, тик после прогрева не выделяет памяти (симуляция игры без
                            окна, та же композиция, что у Sword.exe); каждый модуль по очереди выключен — 10 с
                            игрового времени без ошибок; опечатка в секции конфига и выключение ядра — ошибки;
      app-smoke             Sword.exe без фокуса: 90 кадров по 2 тика с командой «бег по +X», смена размера по
                            кругу, слой отладки D3D12 с GPU-валидацией, снимок последнего кадра — итог JSON в
                            порядке, предупреждений и ошибок D3D12 и предупреждений шейдеров нет, проход
                            scene.opaque нарисовал арену и Manny, снимок не пустой, тиков ровно 180, корень игрока
                            совпал с Headless на 180 тиках; прогон с удержанием действия move_forward (--hold) —
                            игрок бежит по направлению камеры; прогон --fps 45 — ровно 242 тика, доля тика 2/3,
                            показанный игрок интерполирован; HUD — проход hud нарисовал фигуры и текст, шаг кадра
                            посчитан; действия screenshot и hud_help (F4, F1, --hold) — один снимок в --screenshots и
                            справка скрыта; --no-hud — HUD не показан, прохода hud нет;
                            --bench 5 — выход «bench» с итогом замера и снимком последнего кадра (нужны видеокарта и
                            пак контента; -SkipGpu — пропустить);
      agent-smoke           агентский доступ (раздел 8, точка 1е) — настоящие сеансы MCP: Sword.Headless --agent
                            (stdio) — initialize, список инструментов, игрок по имени, команда движения на 120 тиков,
                            time.step, игрок ушёл по +X, ecs.explainSystem, session.stop — выход 0; Sword.exe --agent
                            (HTTP 127.0.0.1) — пауза, команда, шаги, capture.bundle (цвет, глубина, игрок в кадре с
                            рамкой), render.graph, session.stop — выход closed-by-tool (с -SkipGpu — только Headless);
      ecs-bench             только с -Bench (~2 мин): BenchmarkDotNet, tests/Sword.Benchmarks — обход ECS медленнее
                            сырого цикла по Span не более чем на 30 %, команды и журналы — 0 байт на тик (раздел 5
                            плана); итог — artifacts/bench/ecs.json;
      line-endings          все текстовые файлы — CRLF (соглашение AGENTS.md);
      reference             SHA-256 эталонного пака совпадает с tests/reference/reference.json.

.PARAMETER Json
    Итог одним JSON-объектом в stdout, UTF-8 (для агентов). Код выхода: 0 — всё прошло, 1 — нет.

.PARAMETER SkipReference
    Не проверять эталонный пак и не запускать тесты, которым он нужен (категория Content) — например, на машине без него.

.PARAMETER Bench
    Прогнать микробенчмарки ECS (BenchmarkDotNet, Release, около двух минут) и проверить цели производительности.

.PARAMETER SkipGpu
    Не запускать шаги, которым нужны окно и видеокарта.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/scripts/verify.ps1 -Json
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Json,
    [switch]$SkipReference,
    [switch]$SkipGpu,
    [switch]$Bench
)

$ErrorActionPreference = 'Stop'
# Вывод dotnet — на английском: шаги разбирают его по ключевым словам. Наш вывод — UTF-8.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$OutputEncoding = [Console]::OutputEncoding

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $root 'SWORD.slnx'
$steps = New-Object System.Collections.Generic.List[object]

function Add-Step([string]$Name, [scriptblock]$Body) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $result = & $Body
        $ok = [bool]$result.ok
        $detail = [string]$result.detail
    }
    catch {
        $ok = $false
        $detail = $_.Exception.Message
    }
    $watch.Stop()
    $step = [ordered]@{ name = $Name; ok = $ok; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1); detail = $detail }
    $steps.Add($step)
    if (-not $Json) {
        $mark = if ($ok) { 'OK  ' } else { 'FAIL' }
        Write-Host ('{0} {1,-22} {2,6:0.0} s  {3}' -f $mark, $Name, $step.seconds, $detail)
    }
}

function Get-Lines([string]$Text, [string]$Pattern, [int]$Count) {
    ($Text -split "`r?`n" | Where-Object { $_ -match $Pattern } | ForEach-Object { $_.Trim() } | Select-Object -Last $Count) -join ' | '
}

Add-Step 'build' {
    $out = & dotnet build $solution -c $Configuration -nologo -v:minimal | Out-String
    $ok = $LASTEXITCODE -eq 0
    $script:buildOk = $ok
    $detail = if ($ok) { Get-Lines $out 'Warning\(s\)|Error\(s\)' 2 } else { Get-Lines $out ': error ' 5 }
    @{ ok = $ok; detail = $detail }
}

Add-Step 'test' {
    # Тесты идут без пересборки: после упавшей сборки они проверяли бы старые сборки.
    if (-not $script:buildOk) { return @{ ok = $false; detail = 'не запускались: сборка не прошла' } }
    $skip = @()
    if ($SkipGpu) { $skip += 'Category!=Gpu' }
    if ($SkipReference) { $skip += 'Category!=Content' }
    $filter = if ($skip.Count -gt 0) { @('--filter', ($skip -join '&')) } else { @() }
    $out = & dotnet test $solution -c $Configuration --no-build -nologo @filter | Out-String
    @{ ok = ($LASTEXITCODE -eq 0); detail = (Get-Lines $out 'Passed!|Failed!|\[FAIL\]' 6) }
}

Add-Step 'forbidden-references' {
    $cases = @(Get-ChildItem (Join-Path $root 'tests\fixtures\forbidden-references') -Directory | Where-Object { $_.Name -match '^SWORD\d{3}-' })
    if ($cases.Count -eq 0) { return @{ ok = $false; detail = 'нет заготовок SWORD00N-*' } }
    $failures = @()
    foreach ($case in $cases) {
        $code = $case.Name.Substring(0, 8)
        $project = Get-ChildItem $case.FullName -Recurse -Filter *.csproj | Select-Object -First 1
        $out = & dotnet build $project.FullName -nologo -v:minimal | Out-String
        if ($LASTEXITCODE -eq 0) { $failures += "$($case.Name): собралась, а должна упасть с $code" }
        elseif ($out -notmatch "error $code") { $failures += "$($case.Name): упала, но не с $code" }
    }
    if ($failures.Count -eq 0) { @{ ok = $true; detail = "$($cases.Count) заготовок упали со своими кодами" } }
    else { @{ ok = $false; detail = $failures -join '; ' } }
}

Add-Step 'without-agent' {
    # Сборка без агента (-p:WithoutAgent=true) не содержит ни одной его сборки — раздел 4 плана.
    $failures = @()
    foreach ($exe in @('Sword.Game.App', 'Sword.Game.Headless')) {
        $project = Join-Path $root "game\$exe\$exe.csproj"
        $output = Join-Path $root "artifacts\verify\without-agent\$exe"
        if (Test-Path $output) { Remove-Item $output -Recurse -Force }
        $out = & dotnet build $project -c $Configuration -nologo -v:minimal -p:WithoutAgent=true -o $output | Out-String
        if ($LASTEXITCODE -ne 0) { $failures += "$exe не собрался без агента: $(Get-Lines $out ': error ' 3)"; continue }
        $leaked = @(Get-ChildItem $output -Filter '*.Agent*.dll' | ForEach-Object { $_.Name })
        if ($leaked.Count -gt 0) { $failures += "$exe без агента содержит: $($leaked -join ', ')" }
    }
    if ($failures.Count -eq 0) { @{ ok = $true; detail = 'App и Headless без агента собрались и не содержат его сборок' } }
    else { @{ ok = $false; detail = $failures -join '; ' } }
}

Add-Step 'headless-smoke' {
    # Точка 1в: ECS, Runtime, ядро игры и Bootstrap вместе; 600 тиков — 5 с при 120 тиках/с.
    $exe = Get-ChildItem (Join-Path $root "game\Sword.Game.Headless\bin\$Configuration") -Recurse -Filter 'Sword.Headless.exe' | Select-Object -First 1
    if (-not $exe) { return @{ ok = $false; detail = 'не найден Sword.Headless.exe — сборка не прошла?' } }
    # Журнал идёт в stderr; в Windows PowerShell 5.1 при Stop каждая его строка стала бы исключением.
    $ErrorActionPreference = 'Continue'
    $text = (& $exe.FullName --ticks 600 --move 1,0 --json 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $text) { return @{ ok = $false; detail = "код выхода $LASTEXITCODE" } }
    $r = $text | ConvertFrom-Json
    $problems = @()
    if (-not $r.ok) { $problems += "итог: $($r.error)" }
    elseif ($r.ticks -ne 600) { $problems += "тиков $($r.ticks) из 600" }
    elseif ([math]::Abs($r.player.speed - 4.2) -gt 0.01) { $problems += "скорость $($r.player.speed), ждали 4.2 (бег)" }
    elseif ($r.player.x -lt 19 -or [math]::Abs($r.player.z) -gt 1e-3) { $problems += "игрок в ($($r.player.x), $($r.player.z)), ждали x ≈ 20, z = 0" }
    elseif ($r.allocatedBytesPerTick -ne 0) { $problems += "тик выделяет $($r.allocatedBytesPerTick) Б после прогрева, ждали 0 (раздел 12 плана)" }
    # Решение 9 и раздел 12: каждый модуль по очереди выключен — 10 с игрового времени без исключений.
    $modules = @($r.modules)
    $ticks10s = 10 * $r.tickRate
    foreach ($m in $modules) {
        $bare = (& $exe.FullName --ticks $ticks10s --move 1,0 --set "modules.$m.enabled=false" --json 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $bare) { $problems += "без модуля ${m}: код выхода $LASTEXITCODE"; continue }
        $b = $bare | ConvertFrom-Json
        if (-not $b.ok) { $problems += "без модуля ${m}: $($b.error)" }
        elseif ($b.ticks -ne $ticks10s -or @($b.inactiveModules) -notcontains $m) { $problems += "без модуля ${m}: тиков $($b.ticks), выключены $($b.inactiveModules)" }
    }
    # Клиентская секция общего конфига (привязки клавиш) Headless не мешает.
    $withInput = (& $exe.FullName --ticks 1 --set input.move_forward=Up --json 2>$null | Out-String).Trim() | ConvertFrom-Json
    if (-not $withInput.ok) { $problems += "Headless с --set input.move_forward=Up: $($withInput.error)" }
    # Строгость конфига на всей композиции: опечатка в секции и попытка выключить ядро (принцип 4) — ошибки запуска.
    foreach ($case in @(@('sim2.tickRate=60', 'неизвестные секции'), @('modules.core.enabled=false', 'modules.core'))) {
        $bad = (& $exe.FullName --ticks 1 --set $case[0] --json 2>$null | Out-String).Trim()
        $code = $LASTEXITCODE
        $b = if ($bad) { $bad | ConvertFrom-Json } else { $null }
        if ($code -eq 0 -or -not $b -or $b.ok -or "$($b.error)" -notmatch [regex]::Escape($case[1])) { $problems += "--set $($case[0]) прошёл или дал не ту ошибку: $bad" }
    }
    $moduleNote = if ($modules.Count -gt 0) { "по очереди без модуля ($($modules -join ', ')) — 10 с без ошибок" } else { 'модулей нет' }
    if ($problems.Count -eq 0) { @{ ok = $true; detail = "600 тиков: игрок в x = $([math]::Round($r.player.x, 3)), скорость $([math]::Round($r.player.speed, 3)) м/с, $($r.msPerTick) мс/тик, 0 Б/тик; $moduleNote; привязки клавиш не мешают; опечатка в конфиге и выключение ядра — ошибки" } }
    else { @{ ok = $false; detail = $problems -join '; ' } }
}

if ($SkipGpu) {
    $steps.Add([ordered]@{ name = 'app-smoke'; ok = $true; seconds = 0; detail = 'пропущено (-SkipGpu)' })
    if (-not $Json) { Write-Host 'SKIP app-smoke                    пропущено (-SkipGpu)' }
}
else {
    Add-Step 'app-smoke' {
        # Точки 1а–1в: окно, устройство, изменение размера, арена и Manny — без фокуса, со слоем отладки D3D12
        # и GPU-валидацией (состояния ресурсов, взятых шейдерами из bindless-кучи); игрок бежит по команде (--move) ровно
        # два тика на кадр, и его корень совпадает с Headless на том же числе тиков — композиция у них одна.
        $exe = Get-ChildItem (Join-Path $root "game\Sword.Game.App\bin\$Configuration") -Recurse -Filter 'Sword.exe' | Select-Object -First 1
        if (-not $exe) { return @{ ok = $false; detail = 'не найден Sword.exe — сборка не прошла?' } }
        $dir = Join-Path $root 'artifacts\verify\app-smoke'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $stdout = Join-Path $dir 'stdout.json'
        $stderr = Join-Path $dir 'stderr.log'
        $shot = Join-Path $dir 'frame.png'
        if (Test-Path $shot) { Remove-Item $shot }
        $frames = 90
        $ticksPerFrame = 2
        $process = Start-Process -FilePath $exe.FullName -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
            -ArgumentList @('--windowed', '800x600', '--frames', "$frames", '--resize-cycle', '--no-activate', '--gpu-validation',
                '--move', '1,0', '--ticks-per-frame', "$ticksPerFrame", '--json', '--shot', "`"$shot`"")
        $text = [System.IO.File]::ReadAllText($stdout).Trim()
        if ($process.ExitCode -ne 0 -or -not $text) { return @{ ok = $false; detail = "код выхода $($process.ExitCode); $([System.IO.File]::ReadAllText($stderr).Trim())" } }
        $result = $text | ConvertFrom-Json
        # Сведения слоя («Message:», например «GPU-валидация включена») — не проблема; всё остальное от D3D12 — отказ.
        $d3d12 = @(Get-Content $stderr -Encoding UTF8 | Where-Object { $_ -match '^\[d3d12\]' -and $_ -notmatch '^\[d3d12\] Message:' })
        # Предупреждение компилятора шейдеров — тоже отказ: иначе оно тонет в журнале.
        $shaderWarnings = @(Get-Content $stderr -Encoding UTF8 | Where-Object { $_ -match '^\[shader\]' })
        $problems = @()
        if (-not $result.ok) { $problems += "итог: $($result.error)" }
        elseif ($result.report.frames -ne $frames) { $problems += "кадров $($result.report.frames) из $frames" }
        elseif ($result.report.resizes -lt 3) { $problems += "изменений размера $($result.report.resizes), ожидалось не меньше 3" }
        elseif ($result.report.simTicks -ne $frames * $ticksPerFrame) { $problems += "тиков симуляции $($result.report.simTicks), ждали $($frames * $ticksPerFrame)" }
        else {
            # Точка 1в: персонаж двигается по заданной команде — и так же, как в Headless.
            $headless = Get-ChildItem (Join-Path $root "game\Sword.Game.Headless\bin\$Configuration") -Recurse -Filter 'Sword.Headless.exe' | Select-Object -First 1
            $ErrorActionPreference = 'Continue'
            $h = (& $headless.FullName --ticks ($frames * $ticksPerFrame) --move 1,0 --json 2>$null | Out-String).Trim() | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or -not $h.ok) { $problems += "Headless для сверки: код $LASTEXITCODE, $($h.error)" }
            elseif ($result.player.x -lt 1) { $problems += "игрок не побежал: x = $($result.player.x)" }
            elseif ($result.player.x -ne $h.player.x -or $result.player.z -ne $h.player.z -or $result.player.facing -ne $h.player.facing) {
                $problems += "корень игрока в Sword.exe ($($result.player.x), $($result.player.z), $($result.player.facing)) не совпал с Headless ($($h.player.x), $($h.player.z), $($h.player.facing))"
            }
            elseif ($result.report.alpha -ne 1) { $problems += "при заданных тиках доля тика $($result.report.alpha), ждали 1" }
            else {
                # Дополнительные прогоны: вывод — в artifacts/verify/app-smoke, код выхода и итог проверяются.
                function Invoke-App([string]$Name, [string[]]$Arguments) {
                    $out = Join-Path $dir "$Name.json"
                    $err = Join-Path $dir "$Name.log"
                    $p = Start-Process -FilePath $exe.FullName -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err `
                        -ArgumentList (@('--windowed', '640x480', '--no-activate', '--json') + $Arguments)
                    $text = [System.IO.File]::ReadAllText($out).Trim()
                    if ($p.ExitCode -ne 0 -or -not $text) { return @{ ok = $false; error = "код выхода $($p.ExitCode); $([System.IO.File]::ReadAllText($err).Trim())" } }
                    return ($text | ConvertFrom-Json)
                }
                # Точка 1г: ввод действиями (move_forward удерживается), — игрок бежит «от камеры»: по её
                # GroundForward = (−sin yaw, −cos yaw).
                $held = Invoke-App 'hold' @('--frames', '60', '--hold', 'move_forward', '--ticks-per-frame', '2')
                if (-not $held.ok) { $problems += "прогон с --hold: $($held.error)" }
                else {
                    $len = [math]::Sqrt($held.player.x * $held.player.x + $held.player.z * $held.player.z)
                    $fx = -[math]::Sin($held.camera.yaw); $fz = -[math]::Cos($held.camera.yaw)
                    if ($len -lt 1 -or ($held.player.x * $fx + $held.player.z * $fz) / $len -lt 0.999) {
                        $problems += "--hold move_forward: игрок в ($($held.player.x), $($held.player.z)), камера yaw $($held.camera.yaw) — бег не «от камеры»"
                    }
                }
                # Точка 1г: интерполяция в самой игре. «Как при 45 кадрах/с», 91 кадр: 91 · 120 / 45 = 242,67 — ровно
                # 242 тика и доля тика 2/3; показанный игрок — на 1/3 шага тика позади последнего тика.
                $fps = Invoke-App 'fps45' @('--frames', '91', '--fps', '45', '--move', '1,0')
                if (-not $fps.ok) { $problems += "прогон с --fps 45: $($fps.error)" }
                elseif ($fps.report.simTicks -ne 242 -or [math]::Abs($fps.report.alpha - 2 / 3) -gt 1e-3) {
                    $problems += "--fps 45: тиков $($fps.report.simTicks), доля тика $($fps.report.alpha); ждали 242 и 0,667"
                }
                else {
                    $lag = $fps.player.x - $fps.shown.x
                    $expected = (1 - $fps.report.alpha) * 4.2 / 120
                    if ([math]::Abs($lag - $expected) -gt 0.2 * $expected) {
                        $problems += "--fps 45: показанный игрок позади тика на $lag м, ждали $expected (интерполяция)"
                    }
                }
                # Точка 1д: действия screenshot (F4) и hud_help (F1) — удержание нажимает их в первом кадре: ровно один
                # снимок, справка скрыта.
                $shots = Join-Path $dir 'screenshots'
                if (Test-Path $shots) { Remove-Item -Recurse -Force $shots }
                $f4 = Invoke-App 'f4' @('--frames', '5', '--hold', 'screenshot,hud_help', '--screenshots', "`"$shots`"")
                if (-not $f4.ok) { $problems += "прогон с --hold screenshot,hud_help: $($f4.error)" }
                else {
                    $files = @(if (Test-Path $shots) { Get-ChildItem $shots -Filter 'sword-*.png' })
                    if ($f4.report.screenshots -ne 1 -or $files.Count -ne 1) { $problems += "F4: снимков в итоге $($f4.report.screenshots), файлов $($files.Count); ждали 1" }
                    elseif ($files[0].Length -lt 20KB) { $problems += "F4: снимок подозрительно мал: $($files[0].Length) Б" }
                    if ($f4.hud.help -ne $false) { $problems += "F1: справка не скрыта (hud.help = $($f4.hud.help))" }
                }
                # Точка 1д: без HUD его нет ни в итоге, ни проходом (снимки, сравнимые между прогонами).
                $bare = Invoke-App 'no-hud' @('--frames', '3', '--no-hud')
                if (-not $bare.ok) { $problems += "прогон с --no-hud: $($bare.error)" }
                elseif ($bare.hud.shown -ne $false -or @($bare.report.passes | Where-Object { $_.name -eq 'hud' }).Count -ne 0) { $problems += "--no-hud: HUD показан ($($bare.hud.shown)) или проход hud есть" }
                # Точка 1д: бенч кадра — 5 с игрового времени, из них 4 — прогрев; выход сам, итог и снимок последнего кадра.
                $benchShot = Join-Path $dir 'bench.png'
                if (Test-Path $benchShot) { Remove-Item $benchShot }
                $bench = Invoke-App 'bench' @('--bench', '5', '--shot', "`"$benchShot`"")
                if (-not $bench.ok) { $problems += "прогон с --bench 5: $($bench.error)" }
                else {
                    $b = $bench.report.bench
                    if ($bench.report.exitReason -ne 'bench' -or -not $b -or $b.frames -lt 1) { $problems += "--bench 5: выход $($bench.report.exitReason), кадров замера $($b.frames)" }
                    elseif (-not ($b.p50Ms -le $b.p99Ms -and $b.p99Ms -le $b.maxMs -and $b.averageMs -gt 0)) { $problems += "--bench 5: перцентили не по порядку: p50 $($b.p50Ms), p99 $($b.p99Ms), макс $($b.maxMs)" }
                    elseif ($bench.report.simTicks -lt 600) { $problems += "--bench 5: тиков $($bench.report.simTicks), замер кончился раньше 5 с" }
                    elseif (-not (Test-Path $benchShot)) { $problems += '--bench 5: нет снимка последнего кадра замера' }
                    else { $script:benchNote = "бенч: $($b.frames) кадров, ср $($b.averageMs) мс, p99 $($b.p99Ms) мс" }
                }
            }
        }
        if ($d3d12.Count -gt 0) { $problems += "сообщения D3D12: $($d3d12 | Select-Object -First 3)" }
        if ($shaderWarnings.Count -gt 0) { $problems += "предупреждения шейдеров ($($shaderWarnings.Count)): $($shaderWarnings | Select-Object -First 3)" }
        if ($result.ok) {
            # Пол, рельеф, колонны и Manny — четыре вызова отрисовки в одном проходе.
            $opaque = @($result.report.passes | Where-Object { $_.name -eq 'scene.opaque' })
            if ($opaque.Count -ne 1) { $problems += 'нет прохода scene.opaque' }
            elseif ($opaque[0].draws -lt 4) { $problems += "scene.opaque: вызовов отрисовки $($opaque[0].draws), ожидалось не меньше 4" }
            # HUD: панели (фигуры) и текст — разные конвейеры, значит не меньше двух вызовов; FPS посчитан.
            $hud = @($result.report.passes | Where-Object { $_.name -eq 'hud' })
            if ($hud.Count -ne 1) { $problems += 'нет прохода hud' }
            elseif ($hud[0].draws -lt 2) { $problems += "hud: вызовов отрисовки $($hud[0].draws), ожидалось не меньше 2" }
            elseif ($result.hud.quads -lt 100) { $problems += "hud: квадов $($result.hud.quads) — HUD пуст?" }
            # Шаг кадра — от счётчика кадров (FPS начинается с 60 и сам по себе ничего не доказывает).
            elseif (-not ($result.report.stats.frameMs -gt 0 -and $result.report.stats.fps -gt 0)) { $problems += "числа кадра в итоге: шаг $($result.report.stats.frameMs) мс, FPS $($result.report.stats.fps)" }
            # Кадр одного цвета сжимается в PNG до пары килобайт; сцена — на порядки больше.
            if (-not (Test-Path $shot)) { $problems += 'нет снимка кадра' }
            elseif ((Get-Item $shot).Length -lt 50KB) { $problems += "снимок подозрительно мал: $((Get-Item $shot).Length) Б — пустой кадр?" }
        }
        if ($problems.Count -eq 0) {
            @{ ok = $true; detail = "$($result.report.adapter): $($result.report.frames) кадров, изменений размера $($result.report.resizes), $($result.report.simTicks) тиков, игрок в x = $([math]::Round($result.player.x, 3)) как в Headless; с удержанием move_forward бежит от камеры; при --fps 45 — 242 тика, доля 2/3, интерполяция; HUD $($result.hud.quads) квадов; F4 — снимок; $($script:benchNote); кадр $($result.report.averageFrameMs) мс, снимок $shot" }
        }
        else { @{ ok = $false; detail = $problems -join '; ' } }
    }
}

Add-Step 'agent-smoke' {
    # Сеанс MCP как у Claude Code: запрос — строка JSON-RPC, ответ — строка (stdio) или тело POST (HTTP).
    $problems = @()
    $headless = Get-ChildItem (Join-Path $root "game\Sword.Game.Headless\bin\$Configuration") -Recurse -Filter 'Sword.Headless.exe' | Select-Object -First 1
    if (-not $headless) { return @{ ok = $false; detail = 'не найден Sword.Headless.exe' } }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $headless.FullName
    $psi.Arguments = '--agent'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $psi.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stderr = $proc.StandardError.ReadToEndAsync()
    $script:rpcId = 0
    $utf8 = New-Object System.Text.UTF8Encoding $false
    function Write-Stdio([string]$line) {
        # Байтами UTF-8: у StandardInput в PowerShell 5.1 кодировка консоли (OEM), кириллица исказилась бы.
        $bytes = $utf8.GetBytes($line + "`n")
        $proc.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
        $proc.StandardInput.BaseStream.Flush()
    }
    function Send-Stdio($method, $params) {
        $script:rpcId++
        Write-Stdio (@{ jsonrpc = '2.0'; id = $script:rpcId; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 20)
        $read = $proc.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(60000)) { throw "Headless не ответил на $method за 60 с" }
        if (-not $read.Result) { throw "Headless закрыл вывод на $method" }
        return ($read.Result | ConvertFrom-Json)
    }
    function Call-Stdio($tool, $arguments) {
        $r = Send-Stdio 'tools/call' @{ name = $tool; arguments = $arguments }
        if ($r.error) { throw "${tool}: $($r.error.message)" }
        if ($r.result.isError) { throw "${tool}: $($r.result.structuredContent.error)" }
        return $r.result.structuredContent
    }
    try {
        $init = Send-Stdio 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'verify' } }
        if ($init.result.protocolVersion -ne '2025-06-18') { $problems += "Headless: протокол $($init.result.protocolVersion)" }
        Write-Stdio '{"jsonrpc":"2.0","method":"notifications/initialized"}'
        $tools = @((Send-Stdio 'tools/list' @{}).result.tools | ForEach-Object { $_.name })
        foreach ($t in @('session.status', 'time.step', 'world.query', 'entity.get', 'command.send', 'log.tail', 'ecs.explainSystem', 'architecture.projects')) {
            if ($tools -notcontains $t) { $problems += "Headless: нет инструмента $t" }
        }
        $player = Call-Stdio 'entity.get' @{ entity = 'player' }
        if ($player.name -ne 'player') { $problems += "Headless: entity.get player — $($player.name)" }
        $null = Call-Stdio 'command.send' @{ command = 'PlayerCommand'; fields = @{ Move = @{ X = 1; Y = 0 } }; repeat = 120 }
        $step = Call-Stdio 'time.step' @{ ticks = 120 }
        if ($step.receipt.tickId -ne 120) { $problems += "Headless: после time.step тик $($step.receipt.tickId), ждали 120" }
        $moved = Call-Stdio 'entity.get' @{ entity = 'player'; components = @('Transform') }
        $x = $moved.components.Transform.Position.X
        if (-not ($x -gt 3.5)) { $problems += "Headless: игрок не ушёл по +X за 120 тиков: x = $x" }
        $explain = Call-Stdio 'ecs.explainSystem' @{ name = 'PlayerCommand' }
        if ($explain.stage -ne 'Intent' -or -not $explain.source) { $problems += "Headless: ecs.explainSystem — стадия $($explain.stage), источник $($explain.source)" }
        $null = Call-Stdio 'session.stop' @{}
        if (-not $proc.WaitForExit(10000)) { $problems += 'Headless: не вышел после session.stop'; $proc.Kill() }
        elseif ($proc.ExitCode -ne 0) { $problems += "Headless: код выхода $($proc.ExitCode)" }
    }
    catch { $problems += "Headless: $($_.Exception.Message) $(if ($stderr.IsCompleted) { $stderr.Result })" }
    finally { if (-not $proc.HasExited) { $proc.Kill() } }
    $headlessNote = "Headless: игрок в x = $([math]::Round([double]$x, 3)) после 120 тиков команды агента"

    $appNote = 'живая игра — пропущено (-SkipGpu)'
    if (-not $SkipGpu) {
        $exe = Get-ChildItem (Join-Path $root "game\Sword.Game.App\bin\$Configuration") -Recurse -Filter 'Sword.exe' | Select-Object -First 1
        $port = Get-Random -Minimum 47900 -Maximum 48900
        $dir = Join-Path $root 'artifacts\verify\agent-smoke'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $out = Join-Path $dir 'app.json'
        $err = Join-Path $dir 'app.log'
        $app = Start-Process -FilePath $exe.FullName -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err `
            -ArgumentList @('--windowed', '640x480', '--no-activate', '--json', '--gpu-validation', '--agent-port', "$port")
        $url = "http://127.0.0.1:$port/mcp"
        $script:httpId = 0
        function Send-Http($method, $params) {
            $script:httpId++
            $body = @{ jsonrpc = '2.0'; id = $script:httpId; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 20
            return Invoke-RestMethod -Method Post -Uri $url -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json' -TimeoutSec 60
        }
        function Call-Http($tool, $arguments) {
            $r = Send-Http 'tools/call' @{ name = $tool; arguments = $arguments }
            if ($r.error) { throw "${tool}: $($r.error.message)" }
            if ($r.result.isError) { throw "${tool}: $($r.result.structuredContent.error)" }
            return $r.result.structuredContent
        }
        try {
            $ready = $false
            for ($i = 0; $i -lt 150 -and -not $ready -and -not $app.HasExited; $i++) {
                try { $null = Send-Http 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'verify' } }; $ready = $true }
                catch { Start-Sleep -Milliseconds 200 }
            }
            if (-not $ready) { throw "сервер не поднялся на $url" }
            $paused = Call-Http 'time.pause' @{}
            $null = Call-Http 'command.send' @{ command = 'PlayerCommand'; fields = @{ Move = @{ X = 1; Y = 0 } }; repeat = 60 }
            $step = Call-Http 'time.step' @{ ticks = 60 }
            if ($step.tick -ne $paused.tick + 60) { $problems += "App: после time.step тик $($step.tick), ждали $($paused.tick + 60)" }
            $bundle = Call-Http 'capture.bundle' @{ name = 'verify' }
            $player = @($bundle.entities | Where-Object { $_.name -eq 'player' })
            if (-not (Test-Path $bundle.color) -or -not (Test-Path $bundle.depth) -or -not (Test-Path $bundle.bundle)) { $problems += 'App: capture.bundle — нет файлов' }
            elseif ($player.Count -ne 1) { $problems += "App: capture.bundle — игрока в кадре нет ($(@($bundle.entities).Count) сущностей)" }
            elseif ($bundle.tickId -ne $step.tick) { $problems += "App: снимок на тике $($bundle.tickId), ждали $($step.tick) (пауза)" }
            elseif (-not ($bundle.depthCoverage -gt 0.5)) { $problems += "App: глубина почти пуста: $($bundle.depthCoverage)" }
            $graph = Call-Http 'render.graph' @{}
            $passes = @($graph.passes | ForEach-Object { $_.name })
            if ($passes -notcontains 'scene.opaque' -or $passes -notcontains 'hud') { $problems += "App: render.graph — проходы $($passes -join ', ')" }
            $null = Call-Http 'session.stop' @{}
            if (-not $app.WaitForExit(15000)) { $problems += 'App: не вышла после session.stop' }
            else {
                $report = ([System.IO.File]::ReadAllText($out).Trim()) | ConvertFrom-Json
                if ($report.report.exitReason -ne 'closed-by-tool') { $problems += "App: выход $($report.report.exitReason)" }
            }
            # Снимок глубины и цвета — под GPU-валидацией: сообщения слоя и предупреждения шейдеров — отказ.
            $d3d12 = @(Get-Content $err -Encoding UTF8 | Where-Object { ($_ -match '^\[d3d12\]' -and $_ -notmatch '^\[d3d12\] Message:') -or $_ -match '^\[shader\]' })
            if ($d3d12.Count -gt 0) { $problems += "App: сообщения D3D12 или шейдеров: $($d3d12 | Select-Object -First 3)" }
            $appNote = "живая игра (GPU-валидация): пауза, 60 тиков команды, снимок кадра $($bundle.width)×$($bundle.height) на тике $($bundle.tickId) с рамкой игрока [$($player[0].rect -join ', ')]"
        }
        catch { $problems += "App: $($_.Exception.Message) — журнал $err" }
        finally { if (-not $app.HasExited) { $app.Kill() } }
    }
    if ($problems.Count -eq 0) { @{ ok = $true; detail = "$headlessNote; $appNote" } }
    else { @{ ok = $false; detail = $problems -join '; ' } }
}

if ($Bench) {
    Add-Step 'ecs-bench' {
        # BenchmarkDotNet пишет много в stdout; вердикт — в artifacts/bench/ecs.json и код выхода.
        $ErrorActionPreference = 'Continue'
        $out = Join-Path $root 'artifacts\bench\ecs.json'
        if (Test-Path $out) { Remove-Item $out }
        $log = & dotnet run -c Release --project (Join-Path $root 'tests\Sword.Benchmarks') 2>&1 | Out-String
        if (-not (Test-Path $out)) { return @{ ok = $false; detail = "нет итога бенчмарков: $(Get-Lines $log 'error|Exception' 3)" } }
        $r = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
        $ratios = ($r.iteration | ForEach-Object { "чанк $($_.chunkBytes / 1024) КБ: ×$($_.ratio)" }) -join ', '
        $detail = "обход ECS / сырой цикл — $ratios (не больше ×$($r.maxRatio)); тик команд и журналов $([math]::Round($r.commandsAndJournals.tickNs / 1000, 1)) мкс, $($r.commandsAndJournals.bytesPerTick) Б"
        @{ ok = [bool]$r.ok; detail = $detail }
    }
}
else {
    $steps.Add([ordered]@{ name = 'ecs-bench'; ok = $true; seconds = 0; detail = 'пропущено (без -Bench)' })
    if (-not $Json) { Write-Host 'SKIP ecs-bench                    пропущено (без -Bench)' }
}

Add-Step 'line-endings' {
    $extensions = @('.cs', '.csproj', '.props', '.targets', '.slnx', '.md', '.json', '.ps1', '.xml', '.hlsl', '.hlsli',
        '.editorconfig', '.gitattributes', '.gitignore', '.txt', '.yml', '.yaml', '.cmd')
    $skip = '\\(bin|obj|\.git|\.vs|artifacts|TestResults)\\'
    $bad = @(Get-ChildItem $root -Recurse -File -Force | Where-Object {
            ($extensions -contains $_.Extension.ToLowerInvariant()) -and ($_.FullName -notmatch $skip) -and
            [regex]::IsMatch([System.IO.File]::ReadAllText($_.FullName), '(?<!\r)\n')
        } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
    if ($bad.Count -eq 0) { @{ ok = $true; detail = 'все текстовые файлы — CRLF' } }
    else { @{ ok = $false; detail = 'LF без CR: ' + ($bad -join ', ') } }
}

if ($SkipReference) {
    $steps.Add([ordered]@{ name = 'reference'; ok = $true; seconds = 0; detail = 'пропущено (-SkipReference)' })
    if (-not $Json) { Write-Host 'SKIP reference                    пропущено (-SkipReference)' }
}
else {
    Add-Step 'reference' {
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'reference.ps1') -Check -Json | Out-String
        @{ ok = ($LASTEXITCODE -eq 0); detail = $out.Trim() }
    }
}

$allOk = @($steps | Where-Object { -not $_.ok }).Count -eq 0
if ($Json) {
    [ordered]@{ ok = $allOk; configuration = $Configuration; steps = $steps } | ConvertTo-Json -Depth 4 -Compress
}
else {
    Write-Host ''
    Write-Host $(if ($allOk) { 'Проверка пройдена.' } else { 'Проверка НЕ пройдена.' })
}
if ($allOk) { exit 0 } else { exit 1 }
