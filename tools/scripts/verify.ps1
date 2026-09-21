<#
.SYNOPSIS
    Полная проверка SWORD2 — раздел 12 ARCHITECTURE_PLAN.md.

.DESCRIPTION
    Шаги:
      build                 сборка SWORD.slnx (предупреждения — ошибки);
      test                  тесты решения, в том числе архитектурные и GPU-тесты рендера (категория Gpu:
                            устройство D3D12 с GPU-валидацией; -SkipGpu — без них);
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
                            совпал с Headless на 180 тиках (нужны видеокарта и пак контента; -SkipGpu — пропустить);
      ecs-bench             только с -Bench (~2 мин): BenchmarkDotNet, tests/Sword.Benchmarks — обход ECS медленнее
                            сырого цикла по Span не более чем на 30 %, команды и журналы — 0 байт на тик (раздел 5
                            плана); итог — artifacts/bench/ecs.json;
      line-endings          все текстовые файлы — CRLF (соглашение AGENTS.md);
      reference             SHA-256 эталонного пака совпадает с tests/reference/reference.json.

.PARAMETER Json
    Итог одним JSON-объектом в stdout, UTF-8 (для агентов). Код выхода: 0 — всё прошло, 1 — нет.

.PARAMETER SkipReference
    Не проверять эталонный пак (например, на машине без него).

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
    $filter = if ($SkipGpu) { @('--filter', 'Category!=Gpu') } else { @() }
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
    # Строгость конфига на всей композиции: опечатка в секции и попытка выключить ядро (принцип 4) — ошибки запуска.
    foreach ($case in @(@('sim2.tickRate=60', 'неизвестные секции'), @('modules.core.enabled=false', 'modules.core'))) {
        $bad = (& $exe.FullName --ticks 1 --set $case[0] --json 2>$null | Out-String).Trim()
        $code = $LASTEXITCODE
        $b = if ($bad) { $bad | ConvertFrom-Json } else { $null }
        if ($code -eq 0 -or -not $b -or $b.ok -or "$($b.error)" -notmatch [regex]::Escape($case[1])) { $problems += "--set $($case[0]) прошёл или дал не ту ошибку: $bad" }
    }
    $moduleNote = if ($modules.Count -gt 0) { "по очереди без модуля ($($modules -join ', ')) — 10 с без ошибок" } else { 'модулей нет' }
    if ($problems.Count -eq 0) { @{ ok = $true; detail = "600 тиков: игрок в x = $([math]::Round($r.player.x, 3)), скорость $([math]::Round($r.player.speed, 3)) м/с, $($r.msPerTick) мс/тик, 0 Б/тик; $moduleNote; опечатка в конфиге и выключение ядра — ошибки" } }
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
            if ($result.player.x -lt 1) { $problems += "игрок не побежал: x = $($result.player.x)" }
            elseif ($result.player.x -ne $h.player.x -or $result.player.z -ne $h.player.z -or $result.player.facing -ne $h.player.facing) {
                $problems += "корень игрока в Sword.exe ($($result.player.x), $($result.player.z), $($result.player.facing)) не совпал с Headless ($($h.player.x), $($h.player.z), $($h.player.facing))"
            }
        }
        if ($d3d12.Count -gt 0) { $problems += "сообщения D3D12: $($d3d12 | Select-Object -First 3)" }
        if ($shaderWarnings.Count -gt 0) { $problems += "предупреждения шейдеров ($($shaderWarnings.Count)): $($shaderWarnings | Select-Object -First 3)" }
        if ($result.ok) {
            # Пол, рельеф, колонны и Manny — четыре вызова отрисовки в одном проходе.
            $opaque = @($result.report.passes | Where-Object { $_.name -eq 'scene.opaque' })
            if ($opaque.Count -ne 1) { $problems += 'нет прохода scene.opaque' }
            elseif ($opaque[0].draws -lt 4) { $problems += "scene.opaque: вызовов отрисовки $($opaque[0].draws), ожидалось не меньше 4" }
            # Кадр одного цвета сжимается в PNG до пары килобайт; сцена — на порядки больше.
            if (-not (Test-Path $shot)) { $problems += 'нет снимка кадра' }
            elseif ((Get-Item $shot).Length -lt 50KB) { $problems += "снимок подозрительно мал: $((Get-Item $shot).Length) Б — пустой кадр?" }
        }
        if ($problems.Count -eq 0) {
            @{ ok = $true; detail = "$($result.report.adapter): $($result.report.frames) кадров, изменений размера $($result.report.resizes), $($result.report.simTicks) тиков, игрок в x = $([math]::Round($result.player.x, 3)) как в Headless, кадр $($result.report.averageFrameMs) мс, снимок $shot" }
        }
        else { @{ ok = $false; detail = $problems -join '; ' } }
    }
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
        '.editorconfig', '.gitattributes', '.gitignore', '.txt', '.yml', '.yaml')
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
