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
      without-devtools      App с -p:WithoutDevTools=true собирается без сборок DevTools (ни DLL в выходе, ни записи в
                            Sword.deps.json), а в обычной сборке Engine.DevTools есть (проверка не пуста);
      without-modules       Headless с -p:Without<Модуль>=true для каждого модуля game/modules по очереди: собирается
                            без ссылки на модуль, DLL модуля нет в выходе, 10 с игрового времени без ошибок, модуля нет
                            в составе; своё поведение без модуля — по таблице $expect (Locomotion: корень ушёл по +X,
                            поза игрока — поза привязки);
      headless-smoke        Sword.Headless: 600 тиков с командой «бег по +X» — итог JSON в порядке, игрок ушёл
                            по +X со скоростью бега, тик после прогрева не выделяет памяти (симуляция игры без
                            окна, та же композиция, что у Sword.exe); второй такой же прогон — тот же хеш
                            состояния по всем участникам (детерминизм, этап 3); каждый модуль по очереди выключен — 10 с
                            игрового времени без ошибок; опечатка в секции конфига и выключение ядра — ошибки; этап П —
                            толпа с --profile: 0 Б/тик, тот же хеш, profile.json с зонами тика, стадий, систем, инвариантов;
                            точка Пб — machine в итоге (вердикт «код или машина»), среда тика и машина в отчёте;
                            --profile runtime — события среды с калибровкой часов, 0 Б/тик, тот же хеш; точка Пв —
                            секция anomalies в итоге и отчёте, порог --profile-threshold 0.001 (аномалия — каждый тик):
                            0 Б/тик и при захвате, файлов не больше лимита, файл аномалии и его трасса — верный JSON,
                            --profile-trace — trace.json (Chrome Trace); аномалии ровного прогона — сообщение, не провал;
      app-smoke             Sword.exe без фокуса: 90 кадров по 2 тика с командой «бег по +X», смена размера по
                            кругу, слой отладки D3D12 с GPU-валидацией, снимок последнего кадра — итог JSON в
                            порядке, предупреждений и ошибок D3D12 и предупреждений шейдеров нет, проход
                            scene.opaque нарисовал арену и Manny, снимок не пустой, тиков ровно 180, корень игрока
                            совпал с Headless на 180 тиках; прогон с удержанием действия move_forward (--hold) —
                            игрок бежит по направлению камеры; 6ж — окно, показанное свёрнутым (--minimized), —
                            60 кадров идут и свёрнутыми; каждый запуск Sword.exe — с пределом 300 с (процесс снимается,
                            ошибка называет, свёрнуто ли окно); прогон --fps 45 — ровно 242 тика, доля тика 2/3,
                            показанный игрок интерполирован; HUD — проход hud нарисовал фигуры и текст, шаг кадра
                            посчитан; действия screenshot и hud_help (F4, F1, --hold) — один снимок в --screenshots и
                            справка скрыта; debug_bodies (F7, 5в) — капсулы тел 11 бойцов проходом debug.bodies;
                            --no-hud — HUD не показан, прохода hud нет; точка 6е — I и F3 (--hold inventory,tuning) —
                            панели инвентаря и ручек видны, вкладка «Бой», HUD боя в итоге (combat); этап П — --profile: те же байты на кадр, что без него,
                            profile.json с фазами кадра, тиками, проходами графа и зонами игры; точка Пб — среда кадра и тика,
                            машина в отчёте, report.machine в итоге и бенче; точка Пв — --profile с захватом аномалий: те же
                            байты на кадр, секция anomalies (аномалии под нагрузкой машины — сообщение, не провал); F2
                            (--hold hud_profiler) — секция профайлера в HUD и профайлер включён; точка Пг — у каждого
                            прохода итога gpuMs (GPU-время того же кадра, под GPU-валидацией), report.gpu: отставание 0,
                            кадр больше нуля, сумма проходов не больше кадра; profile.json — frames.gpu (кадр и проходы,
                            отставание 3); бенч — GPU-время кадра (среднее, p99) и проходы мс на кадр;
                            --bench 5 — выход «bench» с итогом замера и снимком последнего кадра (нужны видеокарта и
                            пак контента; -SkipGpu — пропустить);
      replay-smoke          реплеи (этап 3б): Sword.exe --record (удержание move_forward и walk, --fps 45) —
                            Sword.Headless --replay воспроизводит запись с теми же хешами по участникам на каждом
                            тике; испорченный реплей (команда одного тика в середине) называет тик и участника,
                            код выхода 3; эталоны tests/reference/replays воспроизводятся из командной строки
                            (-SkipGpu — запись Headless вместо Sword.exe; -SkipReference — без эталонов);
      features              приёмка фич (этап 3д): Sword.Headless --feature all — манифест feature.json каждой фичи:
                            сценарии, их проверки и хеши реплеев, инварианты, метрики (свои числа SWORD2 с допуском),
                            бюджеты (время и кадр App на записи сценария — только с -Configuration Release,
                            байты — всегда), виды — кадры
                            Sword.exe --replay по записи сценария с теми же хешами; итоги — artifacts/features/<id>
                            (-SkipGpu — без видов; -SkipReference — пропустить: эталоны на эталонном паке);
      agent-smoke           агентский доступ (раздел 8, точка 1е) — настоящие сеансы MCP: Sword.Headless --agent
                            (stdio) — initialize, список инструментов, игрок по имени, команда движения на 120 тиков,
                            time.step, игрок ушёл по +X, ecs.explainSystem, играет клип бега (anim.clips по номеру
                            из LegacyLocomotionState), anim.pose, physics.bodies (5б), state.hash, state.describe; реплеи (этап 3б) —
                            replay.save, time.seek назад и вперёд к тому же хешу, session.start с реплеем — совпал,
                            replay.check; этап 3д — feature.list, feature.verify, experiment.fork (ветка с другой
                            командой расходится на тике после неё), experiment.compare; этап П — profile.stages (включить,
                            30 тиков, зоны, среда тика и машина — точка Пб, аномалии и trace — точка Пв, выключить);
                            session.stop — выход 0;
                            Sword.exe --agent
                            (HTTP 127.0.0.1) — пауза, команда, шаги, capture.bundle (цвет, глубина, игрок в кадре с
                            рамкой; 5в — ID-буфер: сегменты игрока в его рамке с легендой, оверлей капсул), render.graph,
                            replay.status (запись с тика 0), profile.stages (кадры: фазы, тики, проходы; точка Пг — gpu),
                            точка Пг — gpuMs проходов в render.graph и gpu снимка (отставание 0), session.stop — выход
                            closed-by-tool (с -SkipGpu — только Headless); точка 6а — время жизни сервера: конец stdin
                            посреди долгого time.runUntil — выход за секунды, снятая обёртка cmd (вход открыт) — выход,
                            --idle-exit 0.05 — выход после простоя;
      ecs-bench             только с -Bench (~2 мин): BenchmarkDotNet, tests/Sword.Benchmarks — обход ECS медленнее
                            сырого цикла по Span не более чем на 30 %, команды и журналы — 0 байт на тик (раздел 5
                            плана); сэмплинг поз (1 и 33 бойца, кроссфейд) — 0 байт на тик; итог —
                            artifacts/bench/ecs.json; пара Begin/End профайлера CPU (этап П) — выключенного не дороже
                            1,5 нс, 0 байт с ним и без; счётчики среды, период, снимок машины, строка HUD (точка Пб) —
                            0 байт — artifacts/bench/profiler.json;
      line-endings          все текстовые файлы — CRLF (соглашение AGENTS.md); рабочие деревья других агентов
                            (.claude\worktrees\) не проверяются;
      reference             SHA-256 эталонного пака совпадает с tests/reference/reference.json;
      processes             идёт всегда, последним (точка 6а): после verify не осталось процессов SWORD — ни в его
                            задании Windows (фоновые запуски Sword.Headless --agent и Sword.exe --agent — в задании с
                            KILL_ON_JOB_CLOSE: verify снят или упал — они завершаются с ним), ни среди его потомков.

.PARAMETER Json
    Итог одним JSON-объектом в stdout, UTF-8 (для агентов). Код выхода: 0 — всё прошло, 1 — нет.

.PARAMETER SkipReference
    Не проверять эталонный пак и не запускать тесты, которым он нужен (категория Content) — например, на машине без него.

.PARAMETER Bench
    Прогнать микробенчмарки ECS (BenchmarkDotNet, Release, около двух минут) и проверить цели производительности.

.PARAMETER SkipGpu
    Не запускать шаги, которым нужны окно и видеокарта.

.PARAMETER Only
    Только эти шаги (через запятую); `build` идёт всегда, остальные — «пропущено (-Only)»; неизвестный шаг — ошибка до
    сборки; итог — только выбранных шагов (поле `only` в `-Json`, пометка в последней строке). Для Release-замеров после
    полного прогона Debug: `-Configuration Release -Only headless-smoke,app-smoke,features` — тесты и проверки правил в
    Release не повторяются (проверки мира ECS включены только в Debug, время бюджетов проверяется только в Release).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/scripts/verify.ps1 -Json

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/scripts/verify.ps1 -Json -Configuration Release -Only headless-smoke,app-smoke,features
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Json,
    [switch]$SkipReference,
    [switch]$SkipGpu,
    [switch]$Bench,
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
# Вывод dotnet — на английском: шаги разбирают его по ключевым словам. Наш вывод — UTF-8.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$OutputEncoding = [Console]::OutputEncoding

$knownSteps = @('build', 'test', 'forbidden-references', 'without-agent', 'without-devtools', 'without-modules', 'headless-smoke', 'app-smoke',
    'replay-smoke', 'features', 'agent-smoke', 'ecs-bench', 'line-endings', 'reference', 'processes')
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($name in $Only) {
    if ($knownSteps -notcontains $name) { throw "-Only: неизвестный шаг «$name»; шаги: $($knownSteps -join ', ')" }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $root 'SWORD.slnx'
$steps = New-Object System.Collections.Generic.List[object]

# Точка 6а: процессы SWORD, которые verify запускает в фоне (Sword.Headless --agent, Sword.exe --agent), — в задании
# Windows (Job Object) с KILL_ON_JOB_CLOSE: verify снят или упал — ОС закрывает ручку задания, они завершаются. Сам
# verify в задание не входит: узлы MSBuild и сервер компилятора, общие с другими сборками пользователя, оно не трогает.
# Шаг processes в конце — процессов SWORD не осталось.
if (-not ('SwordVerifyJob' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SwordVerifyJob
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Basic
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Extended
    {
        public Basic Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref Extended info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    public static IntPtr Create()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        Extended info = new Extended();
        info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        return SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(Extended))) ? job : IntPtr.Zero;
    }

    public static bool Add(IntPtr job, IntPtr process) { return job != IntPtr.Zero && AssignProcessToJobObject(job, process); }

    public static int[] Processes(IntPtr job)
    {
        if (job == IntPtr.Zero) return new int[0];
        int size = 8 + IntPtr.Size * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, size, IntPtr.Zero)) return new int[0];
            int count = Marshal.ReadInt32(buffer, 4);
            int[] ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + IntPtr.Size * i).ToInt64();
            return ids;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static bool Terminate(IntPtr job) { return job != IntPtr.Zero && TerminateJobObject(job, 1); }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    public static bool Minimized(IntPtr window) { return window != IntPtr.Zero && IsIconic(window); }
}
'@
}
$script:job = [SwordVerifyJob]::Create()
$script:jobAdded = 0
function Add-ToJob($Process) {
    if ([SwordVerifyJob]::Add($script:job, $Process.Handle)) { $script:jobAdded++ }
}

# 6ж: каждый запуск Sword.exe — с пределом времени (бенч в свёрнутом окне висел 28 мин: кадров нет, игровое время бенча стоит).
# Не закончил — процесс снят, ошибка словами: окно свёрнуто или нет. Сам Sword.exe рисует проверочный прогон и в свёрнутом окне и
# снимает его сторожем кадров («нет кадров N с», код 5) — предел здесь только страховка.
$script:swordSeconds = 300
function Start-Sword([string]$Exe, [string[]]$Arguments, [string]$Stdout, [string]$Stderr, [int]$Seconds = $script:swordSeconds) {
    $p = Start-Process -FilePath $Exe -NoNewWindow -PassThru -RedirectStandardOutput $Stdout -RedirectStandardError $Stderr -ArgumentList $Arguments
    Add-ToJob $p   # и ручка процесса: без неё PowerShell теряет код выхода
    if (-not $p.WaitForExit($Seconds * 1000)) {
        $p.Refresh()
        $state = if ([SwordVerifyJob]::Minimized($p.MainWindowHandle)) { 'окно свёрнуто' } else { 'окно не свёрнуто' }
        try { $p.Kill() } catch { }
        $null = $p.WaitForExit(5000)
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Error = "Sword.exe не закончил за $Seconds с ($state) — процесс снят" }
    }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; TimedOut = $false; Error = $null }
}

function Add-Step([string]$Name, [scriptblock]$Body) {
    if ($Only.Count -gt 0 -and $Name -ne 'build' -and $Name -ne 'processes' -and $Only -notcontains $Name) {
        $steps.Add([ordered]@{ name = $Name; ok = $true; seconds = 0; detail = 'пропущено (-Only)' })
        if (-not $Json) { Write-Host ('SKIP {0,-22} пропущено (-Only)' -f $Name) }
        return
    }
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

Add-Step 'without-devtools' {
    # Сборка без инструментов разработчика (-p:WithoutDevTools=true, раздел 4 плана): Sword.exe собирается без Engine.DevTools
    # (и Game.DevTools, когда появится) — ни DLL в выходе, ни записи в Sword.deps.json. Обычная сборка (шаг build) их
    # содержит — иначе проверка была бы пустой.
    $failures = @()
    $normal = Get-ChildItem (Join-Path $root "game\Sword.Game.App\bin\$Configuration") -Recurse -Filter 'Sword.Engine.DevTools.dll' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $normal) { $failures += "в обычной сборке Sword.exe нет Sword.Engine.DevTools.dll — проверка без DevTools пуста" }
    $project = Join-Path $root 'game\Sword.Game.App\Sword.Game.App.csproj'
    $output = Join-Path $root 'artifacts\verify\without-devtools\Sword.Game.App'
    if (Test-Path $output) { Remove-Item $output -Recurse -Force }
    $out = & dotnet build $project -c $Configuration -nologo -v:minimal -p:WithoutDevTools=true -o $output | Out-String
    if ($LASTEXITCODE -ne 0) { $failures += "Sword.exe не собрался без DevTools: $(Get-Lines $out ': error ' 3)" }
    else {
        $leaked = @(Get-ChildItem $output -Filter '*DevTools*.dll' | ForEach-Object { $_.Name })
        if ($leaked.Count -gt 0) { $failures += "без DevTools в выходе: $($leaked -join ', ')" }
        $deps = Join-Path $output 'Sword.deps.json'
        if (-not (Test-Path $deps)) { $failures += 'нет Sword.deps.json' }
        elseif ((Get-Content $deps -Raw) -match 'DevTools') { $failures += 'Sword.deps.json без DevTools упоминает DevTools' }
    }
    if ($failures.Count -eq 0) { @{ ok = $true; detail = 'Sword.exe без DevTools собрался без их сборок; в обычной сборке Engine.DevTools есть' } }
    else { @{ ok = $false; detail = $failures -join '; ' } }
}

Add-Step 'without-modules' {
    # Сборка без модуля (раздел 7 плана, -p:Without<Модуль>=true): Headless собирается без ссылки на сборку модуля
    # и 10 с игрового времени проходят без ошибок — игры без модуля это не ломает (решение 9).
    $modules = @(Get-ChildItem (Join-Path $root 'game\modules') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Sword\.Game\.\w+$' } | ForEach-Object { $_.Name.Substring('Sword.Game.'.Length) })
    if ($modules.Count -eq 0) { return @{ ok = $true; detail = 'модулей нет' } }
    # Что должно быть видно без модуля — по итогу Headless (1200 тиков бега по +X). Модуль без записи здесь — только
    # общие проверки; новый модуль добавляет свою строку.
    $expect = @{
        'Locomotion' = {
            param($r)
            if ($r.player.x -lt 30) { "корень не ушёл: x = $($r.player.x)" }
            elseif (-not $r.pose -or -not $r.pose.bindPose) { "поза не поза привязки (отклонение $($r.pose.maxBindDeviation) м) — слой локомоции остался?" }
        }
    }
    $failures = @()
    $project = Join-Path $root 'game\Sword.Game.Headless\Sword.Game.Headless.csproj'
    foreach ($m in $modules) {
        $output = Join-Path $root "artifacts\verify\without-modules\$m"
        if (Test-Path $output) { Remove-Item $output -Recurse -Force }
        $out = & dotnet build $project -c $Configuration -nologo -v:minimal "-p:Without$m=true" -o $output | Out-String
        if ($LASTEXITCODE -ne 0) { $failures += "без ${m} не собрался: $(Get-Lines $out ': error ' 3)"; continue }
        if (Test-Path (Join-Path $output "Sword.Game.$m.dll")) { $failures += "без ${m}: в выходе есть Sword.Game.$m.dll" ; continue }
        $ErrorActionPreference = 'Continue'
        $r = (& (Join-Path $output 'Sword.Headless.exe') --ticks 1200 --move 1,0 --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $r.ok) { $failures += "без ${m}: прогон — код $LASTEXITCODE, $($r.error)" }
        elseif (@($r.modules) -contains $m.ToLowerInvariant()) { $failures += "без ${m}: модуль всё равно в составе" }
        elseif ($expect.ContainsKey($m) -and ($problem = & $expect[$m] $r)) { $failures += "без ${m}: $problem" }
    }
    $checked = @($modules | Where-Object { $expect.ContainsKey($_) })
    if ($failures.Count -eq 0) { @{ ok = $true; detail = "Headless без модуля ($($modules -join ', ')) собрался без его сборки, 10 с без ошибок; своё поведение без модуля проверено у: $($checked -join ', ')" } }
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
    elseif (-not $r.pose -or $r.pose.bindPose) { $problems += "игрок бежит, а поза — поза привязки: локомоция не работает" }
    elseif (-not $r.stateHash -or @($r.stateHash.parts.PSObject.Properties.Name) -notcontains 'animation') { $problems += "нет хеша состояния с участником animation: $($r.stateHash | ConvertTo-Json -Compress)" }
    elseif ($r.invariants.enabled -lt 6 -or $r.invariants.violations -ne 0) { $problems += "инварианты: включено $($r.invariants.enabled), нарушений $($r.invariants.violations): $(@($r.invariants.first) -join '; ')" }
    # Точка 4а: арена из sword.json — 10 врагов (Units SWORD), бойцов 11; 32 врага (--units 32) — 0 байт на тик, мест без дыр.
    elseif (-not $r.arena -or $r.arena.enemies -ne 10 -or $r.arena.fighters -ne 11 -or @($r.arena.places).Count -ne 10) {
        $problems += "арена: врагов $($r.arena.enemies), бойцов $($r.arena.fighters), мест $(@($r.arena.places).Count); ждали 10, 11, 10 (sword.json)"
    }
    $crowdNote = ''
    if ($problems.Count -eq 0) {
        $crowd = (& $exe.FullName --ticks 600 --units 40 --move 1,0 --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        if (-not $crowd.ok -or $crowd.arena.enemies -ne 32 -or $crowd.arena.fighters -ne 33) { $problems += "--units 40: врагов $($crowd.arena.enemies), бойцов $($crowd.arena.fighters); ждали предел 32 и 33 ($($crowd.error))" }
        elseif ($crowd.allocatedBytesPerTick -ne 0) { $problems += "32 врага: тик выделяет $($crowd.allocatedBytesPerTick) Б, ждали 0" }
        elseif ($crowd.invariants.violations -ne 0) { $problems += "32 врага: нарушения инвариантов $(@($crowd.invariants.first) -join '; ')" }
        # Точка 4в: столкновения бойцов — ближайшая пара корней не ближе 0,7 − 0,02 м (допуск movement.rootOverlap).
        elseif (-not $crowd.arena.closestPair -or $crowd.arena.closestPair.distance -lt 0.68) { $problems += "32 врага: ближайшая пара корней $($crowd.arena.closestPair.distance) м (места $(@($crowd.arena.closestPair.slots) -join ', ')), ждали не ближе 0.68" }
        else { $crowdNote = "; 10 врагов из sword.json, --units 40 → 32 врага: $($crowd.msPerTick) мс/тик, 0 Б/тик, ближайшая пара $([math]::Round($crowd.arena.closestPair.distance, 3)) м" }
    }
    # Этап П, точка Па: профайлер CPU на той же толпе — 0 Б/тик с ним, хеш тот же, отчёт profile.json с зонами тика, стадий,
    # систем и инвариантов, несогласованных зон нет.
    if ($problems.Count -eq 0) {
        $prof = (& $exe.FullName --ticks 600 --units 40 --move 1,0 --profile --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        $top = @($prof.profile.top | ForEach-Object { $_.name })
        $report = if ($prof.profile.file -and (Test-Path $prof.profile.file)) { Get-Content $prof.profile.file -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        $zones = if ($report) { @($report.ticks.zones | ForEach-Object { $_.name }) } else { @() }
        $missing = @(@('stage.Animation', 'core.animation-tick@Animation', 'barrier.Animation', 'invariant.ecs.finite') | Where-Object { $zones -notcontains $_ })
        if (-not $prof.ok -or -not $prof.profile) { $problems += "--profile: итог без секции profile ($($prof.error))" }
        elseif ($prof.allocatedBytesPerTick -ne 0) { $problems += "--profile: тик выделяет $($prof.allocatedBytesPerTick) Б, ждали 0 (профайлер не выделяет)" }
        elseif (($prof.stateHash | ConvertTo-Json -Compress) -ne ($crowd.stateHash | ConvertTo-Json -Compress)) { $problems += "--profile: хеш $($prof.stateHash.total), без профайлера — $($crowd.stateHash.total)" }
        elseif (-not $report -or $report.format -ne 'sword-profile' -or $report.ticks.count -ne 480) { $problems += "--profile: отчёт $($prof.profile.file) — формат $($report.format), тиков $($report.ticks.count) (ждали 480 после прогрева)" }
        elseif ($top[0] -ne 'tick' -or $missing.Count -gt 0) { $problems += "--profile: нет зон [$($missing -join ', ')] или первая не tick: [$($zones -join ', ')]" }
        elseif ($prof.profile.mismatches -ne 0) { $problems += "--profile: несогласованных зон $($prof.profile.mismatches)" }
        # Точка Пб: машина за тики после прогрева в итоге --json (и без профайлера), среда на тик и машина — в отчёте.
        elseif (-not $crowd.machine -or $crowd.machine.verdict -notin @('код', 'среда', 'машина') -or -not $crowd.machine.text) { $problems += "итог --json без machine (вердикт «код или машина»): $($crowd.machine | ConvertTo-Json -Compress)" }
        elseif (-not $report.ticks.runtime -or -not ($report.ticks.runtime.cpuMs -gt 0) -or -not $report.machine.verdict) { $problems += "--profile: в отчёте нет среды тика (ticks.runtime) или машины (machine)" }
        else { $crowdNote += "; --profile — 0 Б/тик, тот же хеш, зон $($zones.Count), тик на CPU $($report.ticks.runtime.onCpuPct) %, отчёт $($prof.profile.file); машина: $($crowd.machine.text)" }
    }
    # Точка Пб: диагностический режим (--profile runtime) — события среды с местами, 0 Б/тик, тот же хеш.
    if ($problems.Count -eq 0) {
        $diag = (& $exe.FullName --ticks 600 --units 40 --move 1,0 --profile runtime --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        $diagReport = if ($diag.profile.file -and (Test-Path $diag.profile.file)) { Get-Content $diag.profile.file -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        if (-not $diag.ok -or -not $diagReport) { $problems += "--profile runtime: итог или отчёт — $($diag.error)" }
        elseif ($diag.allocatedBytesPerTick -ne 0) { $problems += "--profile runtime: тик выделяет $($diag.allocatedBytesPerTick) Б, ждали 0 (слушатель выделяет на своём потоке)" }
        elseif (($diag.stateHash | ConvertTo-Json -Compress) -ne ($crowd.stateHash | ConvertTo-Json -Compress)) { $problems += "--profile runtime: хеш $($diag.stateHash.total), без профайлера — $($crowd.stateHash.total)" }
        elseif (-not $diagReport.events -or -not $diagReport.events.calibrated -or $diagReport.events.received -lt 1) { $problems += "--profile runtime: события — калибровка $($diagReport.events.calibrated), принято $($diagReport.events.received)" }
        else { $crowdNote += "; --profile runtime — событий $($diagReport.events.received), JIT $($diagReport.events.jit.methods), 0 Б/тик, тот же хеш" }
    }
    # Точка Пв: захват аномалий. Ровный прогон толпы (--profile выше) — аномалии не провал: первые секунды свежего процесса — JIT
    # (OSR в тике, класс JIT), под нагрузкой машины — «машина»; о них — сообщение. Проверка устройства — порогом 0.001 мс
    # (аномалия — каждый тик после 32 первых): главный поток не выделяет и в кадре аномалии (0 Б/тик), файлов — не больше лимита
    # (20), каждый — формат sword-profile-anomaly с главной зоной и классом, трасса аномалии и trace.json — Chrome Trace.
    if ($problems.Count -eq 0) {
        $normal = $prof.profile.anomalies
        $forced = (& $exe.FullName --ticks 240 --units 40 --move 1,0 --profile-threshold 0.001 --profile-trace --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        $a = $forced.profile.anomalies
        $dirA = if ($a) { $a.directory } else { $null }
        $files = @(if ($dirA -and (Test-Path $dirA)) { Get-ChildItem $dirA -Filter 'anomaly-*.json' | Where-Object { $_.Name -notlike '*.trace.json' } })
        $traces = @(if ($dirA -and (Test-Path $dirA)) { Get-ChildItem $dirA -Filter 'anomaly-*.trace.json' })
        $first = if ($files.Count -gt 0) { Get-Content $files[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        $firstTrace = if ($traces.Count -gt 0) { Get-Content $traces[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        $ringTrace = if ($forced.profile.trace -and (Test-Path $forced.profile.trace)) { Get-Content $forced.profile.trace -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        if (-not $normal) { $problems += '--profile: в итоге нет profile.anomalies (захват аномалий, точка Пв)' }
        elseif (-not $forced.ok -or -not $a) { $problems += "--profile-threshold: итог — $($forced.error)" }
        elseif ([int]$a.detected -ne 240 - 32) { $problems += "--profile-threshold 0.001: аномалий $($a.detected), ждали $(240 - 32) (каждый тик после 32 первых)" }
        elseif ($forced.allocatedBytesPerTick -ne 0) { $problems += "--profile-threshold 0.001: тик выделяет $($forced.allocatedBytesPerTick) Б при захвате аномалий, ждали 0" }
        # После Пв: лимиты файлов по классам — код/JIT/GC 20, «машина» 5 (ожидания GPU и заметных у Headless нет): не больше 25.
        elseif (-not $a.limits -or [int]$a.limits.general.files -gt 20 -or [int]$a.limits.machine.files -gt 5 -or [int]$a.limits.gpu.files -ne 0 -or [int]$a.limits.noticeable.files -ne 0) { $problems += "--profile-threshold 0.001: лимиты файлов по классам — $($a.limits | ConvertTo-Json -Compress -Depth 3)" }
        elseif ($files.Count -lt 1 -or $files.Count -gt 25 -or [int]$a.written -ne $files.Count) { $problems += "--profile-threshold 0.001: файлов аномалий $($files.Count) (в итоге — $($a.written)), ждали 1…25 (20 код/JIT/GC + 5 «машина»)" }
        # Исправления после Пд: порог 0.001 — аномалия и на тике не дольше обычного; у такого главной зоны нет (mainZone.name null и
        # mainZone.note «главной зоны нет»), у остальных — имя зоны. Одно из двух — у каждого файла.
        elseif (-not $first -or $first.format -ne 'sword-profile-anomaly' -or $first.class -notin @('code', 'jit', 'gc', 'gpu', 'machine') -or
            @($files | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } | Where-Object {
                -not ([bool]$_.mainZone.name -xor ([string]$_.mainZone.note).StartsWith('главной зоны нет')) }).Count -gt 0) {
            $problems += "файл аномалии $($files[0].FullName): формат $($first.format), зона $($first.mainZone.name) ($($first.mainZone.note)), класс $($first.class); у каждого файла — имя главной зоны или «главной зоны нет»"
        }
        elseif (-not $firstTrace -or @($firstTrace.traceEvents | Where-Object { $_.ph -eq 'X' }).Count -lt 10) { $problems += "трасса аномалии: событий X $(@($firstTrace.traceEvents | Where-Object { $_.ph -eq 'X' }).Count)" }
        elseif (-not $ringTrace -or @($ringTrace.traceEvents | Where-Object { $_.ph -eq 'X' -and $_.name -like 'tick *' }).Count -lt 100) { $problems += "--profile-trace: trace.json — $($forced.profile.trace)" }
        else {
            $normalText = if ([int]$normal.detected -gt 0) { "аномалий $($normal.detected) ($(@($normal.last | ForEach-Object { "тик $($_.number) $($_.classText) $($_.zone)" }) -join '; '))" } else { 'аномалий нет' }
            $crowdNote += "; точка Пв — ровный прогон: $normalText; порог 0.001 мс — $($a.detected) аномалий, 0 Б/тик, файлов $($files.Count) (код/JIT/GC — $($a.limits.general.files) из 20, «машина» — $($a.limits.machine.files) из 5), трасса и trace.json — Chrome Trace"
        }
    }
    # Детерминизм (этап 3): тот же прогон ещё раз — тот же хеш каждого участника.
    if ($problems.Count -eq 0) {
        $again = (& $exe.FullName --ticks 600 --move 1,0 --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        if (($again.stateHash | ConvertTo-Json -Compress) -ne ($r.stateHash | ConvertTo-Json -Compress)) {
            $problems += "два одинаковых прогона — разные хеши: $($r.stateHash | ConvertTo-Json -Compress) и $($again.stateHash | ConvertTo-Json -Compress)"
        }
    }
    # Решение 9 и раздел 12: каждый модуль по очереди выключен — 10 с игрового времени без исключений.
    $modules = @($r.modules)
    $ticks10s = 10 * $r.tickRate
    foreach ($m in $modules) {
        $bare = (& $exe.FullName --ticks $ticks10s --move 1,0 --set "modules.$m.enabled=false" --json 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $bare) { $problems += "без модуля ${m}: код выхода $LASTEXITCODE"; continue }
        $b = $bare | ConvertFrom-Json
        if (-not $b.ok) { $problems += "без модуля ${m}: $($b.error)" }
        elseif ($b.ticks -ne $ticks10s -or @($b.inactiveModules) -notcontains $m) { $problems += "без модуля ${m}: тиков $($b.ticks), выключены $($b.inactiveModules)" }
        elseif ($m -eq 'locomotion' -and -not $b.pose.bindPose) { $problems += "модуль locomotion выключен конфигом, а поза не поза привязки" }
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
    if ($problems.Count -eq 0) { @{ ok = $true; detail = "600 тиков: игрок в x = $([math]::Round($r.player.x, 3)), скорость $([math]::Round($r.player.speed, 3)) м/с, $($r.msPerTick) мс/тик, 0 Б/тик; хеш состояния $($r.stateHash.total) повторился; инвариантов $($r.invariants.enabled), нарушений 0 (с ними — 0 Б/тик)$crowdNote; $moduleNote; привязки клавиш не мешают; опечатка в конфиге и выключение ядра — ошибки" } }
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
        $process = Start-Sword $exe.FullName @('--windowed', '800x600', '--frames', "$frames", '--resize-cycle', '--no-activate', '--gpu-validation',
                '--move', '1,0', '--ticks-per-frame', "$ticksPerFrame", '--json', '--shot', "`"$shot`"") $stdout $stderr
        if ($process.TimedOut) { return @{ ok = $false; detail = $process.Error } }
        $text = [System.IO.File]::ReadAllText($stdout).Trim()
        if ($process.ExitCode -ne 0 -or -not $text) { return @{ ok = $false; detail = "код выхода $($process.ExitCode); $text $([System.IO.File]::ReadAllText($stderr).Trim())" } }
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
            # Точка 4а: 10 врагов из sword.json — кадр отдаёт скиннингу всех 11 бойцов.
            elseif (-not $result.arena -or $result.arena.enemies -ne 10 -or $result.arena.skinned -ne 11) {
                $problems += "арена в кадре: врагов $($result.arena.enemies), отдано скиннингу $($result.arena.skinned); ждали 10 и 11"
            }
            # Точка 6б: меч в руке у каждого из 11 бойцов — отдан рендеру жёстким вложением в кисть.
            elseif (-not $result.weapons -or $result.weapons.armed -ne 11 -or $result.weapons.drawn -ne 11) {
                $problems += "мечи в кадре: в руках $($result.weapons.armed), отдано рендеру $($result.weapons.drawn); ждали 11 и 11"
            }
            else {
                # Дополнительные прогоны: вывод — в artifacts/verify/app-smoke, код выхода и итог проверяются.
                function Invoke-App([string]$Name, [string[]]$Arguments) {
                    $out = Join-Path $dir "$Name.json"
                    $err = Join-Path $dir "$Name.log"
                    $p = Start-Sword $exe.FullName (@('--windowed', '640x480', '--no-activate', '--json') + $Arguments) $out $err
                    if ($p.TimedOut) { return @{ ok = $false; error = $p.Error } }
                    $text = [System.IO.File]::ReadAllText($out).Trim()
                    if ($p.ExitCode -ne 0 -or -not $text) { return @{ ok = $false; error = "код выхода $($p.ExitCode); $text $(Get-Lines ([System.IO.File]::ReadAllText($err)) '.' 3)" } }
                    return ($text | ConvertFrom-Json)
                }
                # 6ж: окно, показанное свёрнутым (--minimized, без фокуса), — проверочный прогон всё равно рисует все кадры и выходит сам
                # (бенч в свёрнутом окне висел 28 мин); итог — кадров, нарисованных свёрнутыми.
                $mini = Invoke-App 'minimized' @('--frames', '60', '--ticks-per-frame', '2', '--minimized', '--stall', '30')
                if (-not $mini.ok) { $problems += "прогон в свёрнутом окне (--minimized): $($mini.error)" }
                elseif ($mini.report.frames -ne 60 -or $mini.report.simTicks -ne 120 -or [int]$mini.report.minimizedFrames -lt 55) {
                    $problems += "--minimized: кадров $($mini.report.frames) (свёрнутыми $($mini.report.minimizedFrames)), тиков $($mini.report.simTicks); ждали 60 (свёрнутыми почти все) и 120"
                }
                else { $script:miniNote = "окно свёрнуто — $($mini.report.frames) кадров (свёрнутыми $($mini.report.minimizedFrames)), $($mini.report.simTicks) тиков" }
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
                # снимок, справка скрыта. Точка 5в: debug_bodies (F7) — капсулы тел всех 11 бойцов поверх кадра проходом
                # debug.bodies; 6б — с ними клинки всех 11 бойцов (вызов отрезков в том же проходе).
                $shots = Join-Path $dir 'screenshots'
                if (Test-Path $shots) { Remove-Item -Recurse -Force $shots }
                $f4 = Invoke-App 'f4' @('--frames', '5', '--hold', 'screenshot,hud_help,debug_bodies', '--screenshots', "`"$shots`"")
                if (-not $f4.ok) { $problems += "прогон с --hold screenshot,hud_help,debug_bodies: $($f4.error)" }
                else {
                    $files = @(if (Test-Path $shots) { Get-ChildItem $shots -Filter 'sword-*.png' })
                    if ($f4.report.screenshots -ne 1 -or $files.Count -ne 1) { $problems += "F4: снимков в итоге $($f4.report.screenshots), файлов $($files.Count); ждали 1" }
                    elseif ($files[0].Length -lt 20KB) { $problems += "F4: снимок подозрительно мал: $($files[0].Length) Б" }
                    if ($f4.hud.help -ne $false) { $problems += "F1: справка не скрыта (hud.help = $($f4.hud.help))" }
                    $debugPass = @($f4.report.passes | Where-Object { $_.name -eq 'debug.bodies' })
                    if (-not $f4.bodies.shown -or $f4.bodies.drawn -ne 143 -or $f4.bodies.capsules -ne 143 -or $debugPass.Count -ne 1 -or $debugPass[0].draws -lt 2) {
                        $problems += "F7: капсулы тел — показаны $($f4.bodies.shown), нарисовано $($f4.bodies.drawn) из $($f4.bodies.capsules), проходов debug.bodies $($debugPass.Count), вызовов $($debugPass[0].draws); ждали 143 капсулы (11 бойцов × 13) и отрезки клинков"
                    }
                    elseif (-not $f4.blades.shown -or $f4.blades.drawn -ne 11) { $problems += "F7: клинки — показаны $($f4.blades.shown), нарисовано $($f4.blades.drawn); ждали 11" }
                }
                # Точка 1д: без HUD его нет ни в итоге, ни проходом (снимки, сравнимые между прогонами).
                $bare = Invoke-App 'no-hud' @('--frames', '3', '--no-hud')
                if (-not $bare.ok) { $problems += "прогон с --no-hud: $($bare.error)" }
                elseif ($bare.hud.shown -ne $false -or @($bare.report.passes | Where-Object { $_.name -eq 'hud' }).Count -ne 0) { $problems += "--no-hud: HUD показан ($($bare.hud.shown)) или проход hud есть" }
                # Исправления после Пд: без F7 и заказанного слоя капсулы тел не снимаются (только таз игрока) — 0 в итоге.
                elseif ($bare.bodies.shown -ne $false -or [int]$bare.bodies.capsules -ne 0 -or [int]$bare.bodies.drawn -ne 0) { $problems += "без F7: капсулы сняты — $($bare.bodies | ConvertTo-Json -Compress)" }
                # Точка 6е: I и F3 (удержание нажимает их в первом кадре) — панели инвентаря и ручек видны, вкладка «Бой»; HUD боя — в итоге
                # (здоровье полное, врагов 10, меч игрока — по умолчанию).
                $panelsRun = Invoke-App 'panels' @('--frames', '5', '--hold', 'inventory,tuning')
                if (-not $panelsRun.ok) { $problems += "прогон с --hold inventory,tuning: $($panelsRun.error)" }
                elseif (-not $panelsRun.panels.shown -or -not $panelsRun.panels.inventory -or @($panelsRun.panels.tuning) -notcontains 'Бой') {
                    $problems += "I, F3: панели — $($panelsRun.panels | ConvertTo-Json -Compress)"
                }
                elseif ($panelsRun.combat.enemies -ne 10 -or $panelsRun.combat.playerHp -ne 100 -or $panelsRun.combat.playerDead -or $panelsRun.combat.weapon -ne 'sword_1h') {
                    $problems += "HUD боя в итоге — $($panelsRun.combat | ConvertTo-Json -Compress)"
                }
                # Точка 4а: PageUp и R (удержание нажимает их в первом кадре) — команды арены в записи Sword.exe; Headless
                # повторяет запись с теми же хешами; врагов 11, кадр рисует 12 бойцов. Точка 6б: T — меч игрока следующий
                # (эталонный пак: sword_1h → sword_dss), в руках у всех 12.
                $arenaFile = Join-Path $dir 'arena.swreplay'
                if (Test-Path $arenaFile) { Remove-Item $arenaFile }
                $arenaRun = Invoke-App 'arena' @('--frames', '30', '--ticks-per-frame', '2', '--hold', 'enemies_more,arena_reset,next_weapon', '--record', "`"$arenaFile`"")
                if (-not $arenaRun.ok) { $problems += "прогон с --hold enemies_more,arena_reset,next_weapon: $($arenaRun.error)" }
                elseif ($arenaRun.arena.enemies -ne 11 -or $arenaRun.arena.skinned -ne 12) { $problems += "PageUp: врагов $($arenaRun.arena.enemies), отдано скиннингу $($arenaRun.arena.skinned); ждали 11 и 12" }
                elseif ($arenaRun.weapons.player -ne 'sword_dss' -or $arenaRun.weapons.drawn -ne 12) { $problems += "T: меч игрока $($arenaRun.weapons.player), мечей отдано рендеру $($arenaRun.weapons.drawn); ждали sword_dss и 12" }
                elseif (-not ([System.IO.File]::ReadAllText($arenaFile)).Contains('Sword.Game.Contracts.ArenaCommand')) { $problems += 'в записи Sword.exe нет команд арены' }
                else {
                    $ErrorActionPreference = 'Continue'
                    $arenaPlay = (& $headless.FullName --replay $arenaFile --json 2>$null | Out-String).Trim() | ConvertFrom-Json
                    if ($LASTEXITCODE -ne 0 -or -not $arenaPlay.replay.match -or $arenaPlay.replay.hashesChecked -ne 61 -or $arenaPlay.arena.enemies -ne 11) {
                        $problems += "запись с командами арены: Headless — код $LASTEXITCODE, совпал $($arenaPlay.replay.match), хешей $($arenaPlay.replay.hashesChecked) из 61, врагов $($arenaPlay.arena.enemies)"
                    }
                }
                # Этап П, точка Па: профайлер CPU (--profile) — те же байты на кадр, что без него (профайлер не выделяет), отчёт
                # profile.json: кадры с фазами кадра, тиками, проходами графа и зонами игры; несогласованных зон нет.
                $plainRun = Invoke-App 'profile-off' @('--frames', '120', '--ticks-per-frame', '2', '--units', '32')
                $profRun = Invoke-App 'profile' @('--frames', '120', '--ticks-per-frame', '2', '--units', '32', '--profile')
                if (-not $plainRun.ok -or -not $profRun.ok -or -not $profRun.profile) { $problems += "--profile: прогоны — $($plainRun.error) $($profRun.error)" }
                else {
                    $profReport = if (Test-Path $profRun.profile.file) { Get-Content $profRun.profile.file -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
                    $frameZones = if ($profReport) { @($profReport.frames.zones | ForEach-Object { $_.name }) } else { @() }
                    $needZones = @('frame', 'frame.messages', 'frame.input', 'frame.simulate', 'tick', 'frame.tools', 'frame.extract', 'frame.render',
                        'frame.fenceWait', 'frame.build', 'frame.record', 'frame.present', 'render.scene.opaque', 'render.hud', 'arena.extract.bodies',
                        'arena.build.skin', 'stage.Animation', 'core.animation-tick@Animation')
                    $missingZones = @($needZones | Where-Object { $frameZones -notcontains $_ })
                    if ($profRun.report.allocatedBytesPerFrame -ne $plainRun.report.allocatedBytesPerFrame) {
                        $problems += "--profile: $($profRun.report.allocatedBytesPerFrame) Б/кадр, без него — $($plainRun.report.allocatedBytesPerFrame): профайлер выделяет?"
                    }
                    elseif (-not $profReport -or $profReport.frames.count -ne 119 -or $profReport.frames.service -ne 1 -or $profReport.ticks.count -ne 240) {
                        $problems += "--profile: отчёт — кадров $($profReport.frames.count) (+ служебных $($profReport.frames.service)), тиков $($profReport.ticks.count); ждали 119 + 1 и 240"
                    }
                    elseif ($missingZones.Count -gt 0) { $problems += "--profile: в кадре нет зон [$($missingZones -join ', ')]" }
                    elseif ($profReport.mismatches -ne 0 -or $profReport.abandoned -ne 0) { $problems += "--profile: несогласованных зон $($profReport.mismatches), брошенных периодов $($profReport.abandoned)" }
                    # Точка Пб: среда на кадр и тик, машина в отчёте и в итоге прогона (и без профайлера).
                    elseif (-not $profReport.frames.runtime -or -not $profReport.ticks.runtime -or -not $profReport.machine.verdict) { $problems += '--profile: в отчёте нет frames.runtime, ticks.runtime или machine' }
                    elseif (-not $plainRun.report.machine -or -not $plainRun.report.machine.text) { $problems += 'итог Sword.exe без report.machine (вердикт «код или машина»)' }
                    # Точка Пв: захват аномалий включён с --profile (байты на кадр — те же, проверено выше); аномалии этого прогона —
                    # сообщение, а не провал: 120 кадров с GPU-валидацией на машине пользователя (GPU и CPU делят с другими
                    # программами) — рывки ожидания GPU и снятия с процессора бывают, и это не ошибка кода.
                    elseif (-not $profReport.anomalies -or -not $profRun.profile.anomalies) { $problems += '--profile: в отчёте или итоге нет anomalies (точка Пв)' }
                    # Точка Пг: GPU-время кадра и проходов рядом с зонами CPU — секция frames.gpu, данные кадра N − 3; в зонах CPU их нет.
                    elseif (-not $profReport.frames.gpu -or $profReport.frames.gpu.lagFrames -ne 3 -or $profReport.frames.gpu.frame.name -ne 'gpu.frame' -or
                        -not ($profReport.frames.gpu.frame.meanMs -gt 0) -or @($profReport.frames.gpu.passes | Where-Object { $_.name -eq 'gpu.scene.opaque' }).Count -ne 1 -or
                        @($frameZones | Where-Object { $_ -like 'gpu.*' }).Count -ne 0) {
                        $problems += "--profile: frames.gpu (точка Пг) — $($profReport.frames.gpu | ConvertTo-Json -Compress -Depth 4)"
                    }
                    else {
                        $an = $profReport.anomalies
                        $anText = if ([int]$an.detected -gt 0) { "аномалий $($an.detected) ($(@($an.list | Select-Object -Last 3 | ForEach-Object { "кадр $($_.number) $($_.classText) $($_.zone)" }) -join '; '))" } else { 'аномалий нет' }
                        $script:profileNote = "--profile — $($profRun.report.allocatedBytesPerFrame) Б/кадр, как без него (и с захватом аномалий), зон кадра $($frameZones.Count), кадр на CPU $($profReport.frames.runtime.onCpuPct) %, $anText"
                    }
                }
                # Точка Пв: F2 (удержание нажимает его в первом кадре) — секция профайлера в HUD, профайлер включён им самим.
                $f2 = Invoke-App 'f2' @('--frames', '60', '--hold', 'hud_profiler')
                if (-not $f2.ok) { $problems += "прогон с --hold hud_profiler: $($f2.error)" }
                elseif (-not $f2.hud.profiler -or -not $f2.profiler.enabled -or [int]$f2.profiler.frames -lt 50) {
                    $problems += "F2: секция показана $($f2.hud.profiler), профайлер включён $($f2.profiler.enabled), кадров в итогах $($f2.profiler.frames); ждали да, да, ≥ 50"
                }
                elseif ([int]$f2.hud.quads -le [int]$plainRun.hud.quads) { $problems += "F2: квадов HUD $($f2.hud.quads), без F2 — $($plainRun.hud.quads): секции нет?" }
                else { $script:profileNote += "; F2 — секция профайлера ($($f2.hud.quads) квадов HUD против $($plainRun.hud.quads)), профайлер включён клавишей" }
                # Точка 1д: бенч кадра — 5 с игрового времени, из них 4 — прогрев; выход сам, итог и снимок последнего кадра.
                # Точка 4а: с толпой — 32 врага (--units 32), кадр рисует всех 33 бойцов.
                $benchShot = Join-Path $dir 'bench.png'
                if (Test-Path $benchShot) { Remove-Item $benchShot }
                $bench = Invoke-App 'bench' @('--bench', '5', '--units', '32', '--shot', "`"$benchShot`"")
                if (-not $bench.ok) { $problems += "прогон с --bench 5: $($bench.error)" }
                else {
                    $b = $bench.report.bench
                    if ($bench.report.exitReason -ne 'bench' -or -not $b -or $b.frames -lt 1) { $problems += "--bench 5: выход $($bench.report.exitReason), кадров замера $($b.frames)" }
                    elseif (-not ($b.p50Ms -le $b.p99Ms -and $b.p99Ms -le $b.maxMs -and $b.averageMs -gt 0)) { $problems += "--bench 5: перцентили не по порядку: p50 $($b.p50Ms), p99 $($b.p99Ms), макс $($b.maxMs)" }
                    elseif ($bench.report.simTicks -lt 600) { $problems += "--bench 5: тиков $($bench.report.simTicks), замер кончился раньше 5 с" }
                    elseif (-not (Test-Path $benchShot)) { $problems += '--bench 5: нет снимка последнего кадра замера' }
                    elseif ($bench.arena.enemies -ne 32 -or $bench.arena.skinned -ne 33) { $problems += "--bench 5 --units 32: врагов $($bench.arena.enemies), отдано скиннингу $($bench.arena.skinned); ждали 32 и 33" }
                    elseif (-not $bench.report.machine -or -not $bench.report.machine.text) { $problems += '--bench 5: нет report.machine (машина за замер, точка Пб)' }
                    # Точка Пг: GPU-время кадра замера (среднее, p99) и проходы — мс на кадр (как BenchRecorder SWORD).
                    elseif ($b.gpuFrames -lt 1 -or -not ($b.gpuMs -gt 0) -or $b.gpuP99Ms -lt $b.gpuMs * 0.5 -or @($b.passes | Where-Object { $_.name -eq 'scene.opaque' -and $_.ms -gt 0 }).Count -ne 1) {
                        $problems += "--bench 5: GPU — кадров $($b.gpuFrames), ср $($b.gpuMs) мс, p99 $($b.gpuP99Ms), проходы $($b.passes | ConvertTo-Json -Compress)"
                    }
                    else { $script:benchNote = "команды арены (PageUp, R) в записи Sword.exe — Headless повторил её хеши; бенч с 32 врагами (33 бойца в кадре): $($b.frames) кадров, ср $($b.averageMs) мс, p99 $($b.p99Ms) мс, GPU ср $($b.gpuMs) мс, p99 $($b.gpuP99Ms) мс; $($bench.report.machine.text)" }
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
            # Точка Пг: GPU-время проходов под GPU-валидацией — у каждого прохода последнего кадра, того же кадра (конец прогона ждёт
            # простоя GPU: отставание 0), сумма проходов не больше кадра; число кадра HUD — GPU-время (кадр N − 3).
            elseif (-not $result.report.gpu -or $result.report.gpu.lag -ne 0 -or -not ($result.report.gpu.frameMs -gt 0) -or
                $result.report.gpu.passesMs -gt $result.report.gpu.frameMs + 1e-6 -or $result.report.gpu.dropped -ne 0) {
                $problems += "report.gpu — $($result.report.gpu | ConvertTo-Json -Compress)"
            }
            elseif (@($result.report.passes | Where-Object { $null -eq $_.gpuMs -or $_.gpuMs -lt 0 }).Count -gt 0) {
                $problems += "проходы без gpuMs: $(@($result.report.passes | Where-Object { $null -eq $_.gpuMs } | ForEach-Object { $_.name }) -join ', ')"
            }
            elseif (-not ($result.report.stats.gpuMs -gt 0)) { $problems += "числа кадра: GPU-время кадра $($result.report.stats.gpuMs) мс" }
            else { $script:gpuNote = "GPU (точка Пг, GPU-валидация): кадр $([math]::Round($result.report.gpu.frameMs, 3)) мс, проходы — $(@($result.report.passes | ForEach-Object { "$($_.name) $([math]::Round($_.gpuMs, 3))" }) -join ', ') мс" }
            # Кадр одного цвета сжимается в PNG до пары килобайт; сцена — на порядки больше.
            if (-not (Test-Path $shot)) { $problems += 'нет снимка кадра' }
            elseif ((Get-Item $shot).Length -lt 50KB) { $problems += "снимок подозрительно мал: $((Get-Item $shot).Length) Б — пустой кадр?" }
        }
        if ($problems.Count -eq 0) {
            @{ ok = $true; detail = "$($result.report.adapter): $($result.report.frames) кадров, изменений размера $($result.report.resizes), $($result.report.simTicks) тиков, игрок в x = $([math]::Round($result.player.x, 3)) как в Headless; с удержанием move_forward бежит от камеры; при --fps 45 — 242 тика, доля 2/3, интерполяция; $($script:miniNote); HUD $($result.hud.quads) квадов; F4 — снимок; $($script:gpuNote); $($script:benchNote); $($script:profileNote); кадр $($result.report.averageFrameMs) мс, снимок $shot" }
        }
        else { @{ ok = $false; detail = $problems -join '; ' } }
    }
}

Add-Step 'replay-smoke' {
    # Этап 3б: запись живой игры воспроизводится симуляцией без окна бит в бит; порча называет тик и участника.
    $ErrorActionPreference = 'Continue'
    $headless = Get-ChildItem (Join-Path $root "game\Sword.Game.Headless\bin\$Configuration") -Recurse -Filter 'Sword.Headless.exe' | Select-Object -First 1
    if (-not $headless) { return @{ ok = $false; detail = 'не найден Sword.Headless.exe' } }
    $dir = Join-Path $root 'artifacts\verify\replay-smoke'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir 'record.swreplay'
    if (Test-Path $file) { Remove-Item $file }
    $problems = @()
    if ($SkipGpu) {
        $by = 'Sword.Headless'
        $rec = (& $headless.FullName --ticks 360 --move 1,1 --walk --record $file --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        if (-not $rec.ok) { return @{ ok = $false; detail = "запись Headless: $($rec.error)" } }
    }
    else {
        $by = 'Sword.exe'
        $exe = Get-ChildItem (Join-Path $root "game\Sword.Game.App\bin\$Configuration") -Recurse -Filter 'Sword.exe' | Select-Object -First 1
        $stdout = Join-Path $dir 'app.json'
        $process = Start-Sword $exe.FullName @('--windowed', '640x480', '--no-activate', '--frames', '135', '--fps', '45', '--hold', 'move_forward,walk',
            '--record', "`"$file`"", '--json') $stdout (Join-Path $dir 'app.log')
        if ($process.TimedOut) { return @{ ok = $false; detail = "запись Sword.exe: $($process.Error)" } }
        $rec = [System.IO.File]::ReadAllText($stdout).Trim() | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or -not $rec.ok -or -not $rec.recorded) { return @{ ok = $false; detail = "запись Sword.exe: код $($process.ExitCode), $($rec.error)" } }
    }
    $recorded = if ($rec.recorded) { $rec.recorded } else { $null }
    $play = (& $headless.FullName --replay $file --json 2>$null | Out-String).Trim() | ConvertFrom-Json
    $code = $LASTEXITCODE
    if ($code -ne 0 -or -not $play.ok -or -not $play.replay.match) { $problems += "воспроизведение: код $code, $($play.replay.divergence | ConvertTo-Json -Compress -Depth 5) $($play.error)" }
    elseif ($play.replay.hashesChecked -ne $recorded.hashes -or $play.replay.hashesChecked -lt 300) { $problems += "сверено хешей $($play.replay.hashesChecked), записано $($recorded.hashes)" }
    elseif ($play.invariants.violations -ne 0) { $problems += "воспроизведение: нарушения инвариантов $(@($play.invariants.first) -join '; ')" }
    # Порча: команда одного тика в середине отрезка — отрезок [0, N, cmd] делится, в тике 150 другое направление.
    $text = [System.IO.File]::ReadAllText($file)
    $m = [regex]::Match($text, '\[0, (\d+), (\[0, [^\]]+\])\]')
    if (-not $m.Success -or [int]$m.Groups[1].Value -lt 200) { $problems += "в записи нет отрезка команд с тика 0 длиной ≥ 200: $($m.Value)" }
    else {
        $n = [int]$m.Groups[1].Value
        $cmd = $m.Groups[2].Value
        $bad = [regex]::Replace($cmd, '^\[0, [^,]+,', '[0, 0.25,')
        $split = "[0, 150, $cmd],`r`n    [150, 1, $bad],`r`n    [151, $($n - 151), $cmd]"
        $corrupt = Join-Path $dir 'corrupt.swreplay'
        [System.IO.File]::WriteAllText($corrupt, $text.Replace($m.Value, $split), (New-Object System.Text.UTF8Encoding $false))
        $broken = (& $headless.FullName --replay $corrupt --json 2>$null | Out-String).Trim() | ConvertFrom-Json
        $bcode = $LASTEXITCODE
        $parts = @($broken.replay.divergence.parts | ForEach-Object { $_.part })
        if ($bcode -ne 3 -or $broken.replay.match) { $problems += "испорченный реплей: код $bcode (ждали 3), совпал = $($broken.replay.match)" }
        elseif ($broken.replay.divergence.tick -ne 151 -or $broken.replay.divergence.lastMatch -ne 150 -or $parts -notcontains 'ecs') {
            $problems += "испорченный реплей: расхождение $($broken.replay.divergence | ConvertTo-Json -Compress -Depth 5), ждали после 151 тика (последнее совпадение 150), участник ecs"
        }
    }
    $refNote = 'эталоны — пропущено (-SkipReference)'
    if (-not $SkipReference) {
        $refs = @(Get-ChildItem (Join-Path $root 'tests\reference\replays') -Filter '*.swreplay')
        foreach ($ref in $refs) {
            $r = (& $headless.FullName --replay $ref.FullName --json 2>$null | Out-String).Trim() | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or -not $r.replay.match) { $problems += "эталон $($ref.Name): $($r.replay.divergence | ConvertTo-Json -Compress -Depth 5) $($r.error)" }
            elseif ($r.invariants.violations -ne 0) { $problems += "эталон $($ref.Name): нарушения инвариантов $(@($r.invariants.first) -join '; ')" }
        }
        $refNote = "эталоны ($(($refs | ForEach-Object { $_.BaseName }) -join ', ')) совпали"
    }
    if ($problems.Count -eq 0) { @{ ok = $true; detail = "запись $by — $($recorded.ticks) тиков, Headless повторил все $($play.replay.hashesChecked) хешей без нарушений инвариантов; порча в тике 150 — расхождение после 151 тика, участники $($parts -join ', '), код 3; $refNote" } }
    else { @{ ok = $false; detail = $problems -join '; ' } }
}

if ($SkipReference) {
    $steps.Add([ordered]@{ name = 'features'; ok = $true; seconds = 0; detail = 'пропущено (-SkipReference): сценарии фич — на эталонном паке' })
    if (-not $Json) { Write-Host 'SKIP features                     пропущено (-SkipReference)' }
}
else {
    Add-Step 'features' {
        # Этап 3д: приёмка каждой фичи по её манифесту одним вызовом; вердикт — по манифесту и эталонам.
        $ErrorActionPreference = 'Continue'
        $headless = Get-ChildItem (Join-Path $root "game\Sword.Game.Headless\bin\$Configuration") -Recurse -Filter 'Sword.Headless.exe' | Select-Object -First 1
        if (-not $headless) { return @{ ok = $false; detail = 'не найден Sword.Headless.exe' } }
        $arguments = @('--feature', 'all', '--json')
        if ($SkipGpu) { $arguments += '--no-views' }
        $r = (& $headless.FullName @arguments 2>$null | Out-String).Trim() | ConvertFrom-Json
        $code = $LASTEXITCODE
        if (-not $r -or -not $r.features) { return @{ ok = $false; detail = "Sword.Headless --feature all: код $code, $($r.error)" } }
        $failed = @($r.features | Where-Object { -not $_.ok })
        if ($code -ne 0 -or $failed.Count -gt 0) {
            return @{ ok = $false; detail = ($failed | ForEach-Object { "$($_.feature): $(@($_.failures | Select-Object -First 3) -join '; ')" }) -join ' | ' }
        }
        $summary = $r.features | ForEach-Object {
            $views = if ($_.views -is [array]) { @($_.views).Count } else { 0 }
            "$($_.feature) (сценариев $(@($_.scenarios).Count), метрик $(@($_.metrics).Count), видов $views, бюджетов $(if ($_.budgets -is [array]) { @($_.budgets).Count } else { 0 }))"
        }
        $time = if ($Configuration -eq 'Release') { 'бюджеты времени и кадра проверены' } else { 'время и кадр бюджетов только замерены (Debug)' }
        $viewNote = if ($SkipGpu) { 'виды пропущены (-SkipGpu)' } else { 'хеши App во всех видах совпали с Headless' }
        @{ ok = $true; detail = "зелёные: $($summary -join ', '); $time; $viewNote" }
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
    Add-ToJob $proc
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
        foreach ($t in @('session.status', 'time.step', 'world.query', 'entity.get', 'command.send', 'log.tail', 'ecs.explainSystem', 'architecture.projects', 'anim.pose', 'anim.clips', 'physics.bodies', 'combat.weapons', 'state.hash', 'state.describe',
                'replay.status', 'replay.save', 'replay.check', 'replay.diff', 'session.start', 'time.seek',
                'time.break', 'watch.add', 'watch.list', 'watch.remove', 'entity.history', 'events.history', 'world.snapshot', 'world.diff', 'replay.observe',
                'invariant.list', 'invariant.enable', 'invariant.run', 'invariant.define', 'time.breakOnInvariant',
                'feature.list', 'feature.verify', 'experiment.fork', 'experiment.compare', 'experiment.sweep', 'profile.stages')) {
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
        # Этап 2: бегущий игрок играет клип бега (номер клипа в состоянии локомоции → имя через anim.clips), поза — живая.
        $loco = Call-Stdio 'entity.get' @{ entity = 'player'; components = @('LegacyLocomotionState') }
        $clip = Call-Stdio 'anim.clips' @{ index = [int]$loco.components.LegacyLocomotionState.Clip }
        $clipName = "$(@($clip.clips)[0].name)"
        if ($clipName -notmatch 'Run') { $problems += "Headless: после бега играет клип «$clipName», ждали бег" }
        $pose = Call-Stdio 'anim.pose' @{ entity = 'player'; joints = @('pelvis', 'head') }
        # Этап 3: хеш состояния по участникам и описание участника animation по суставу.
        $hash = Call-Stdio 'state.hash' @{}
        if ("$($hash.total)" -notmatch '^[0-9A-F]{16}$' -or -not $hash.parts.animation) { $problems += "Headless: state.hash — $($hash | ConvertTo-Json -Compress)" }
        $described = Call-Stdio 'state.describe' @{ part = 'animation'; filter = 'pelvis'; take = 1 }
        if ([int]$described.matched -lt 3 -or -not @($described.items)[0].pelvis) { $problems += "Headless: state.describe animation — поз $($described.matched)" }
        $pelvisY = @($pose.joints)[0].model[1]
        if (-not ($pelvisY -gt 0.5) -or $pose.pool.poses -lt 3) { $problems += "Headless: anim.pose — таз на $pelvisY м, поз в пуле $($pose.pool.poses)" }
        # Этап 5б: капсулы тела бегущего игрока — на позе (таз над землёй), 13 штук, самая быстрая — не стоит.
        $bodies = Call-Stdio 'physics.bodies' @{ entity = 'player'; segments = @('pelvis') }
        $capsuleY = @($bodies.capsules)[0].position[1]
        if ($bodies.bodyCount -ne 13 -or -not ($capsuleY -gt 0.5) -or -not ($bodies.fastest.speed -gt 1)) {
            $problems += "Headless: physics.bodies — тел $($bodies.bodyCount), таз на $capsuleY м, быстрее всех $($bodies.fastest | ConvertTo-Json -Compress)"
        }
        # Точка 6б: меч игрока — sword_1h (combat.playerWeapon по умолчанию), клинок им, кончик бегущего движется; удары slash1–4, stab1–3.
        $combat = Call-Stdio 'combat.weapons' @{ entity = 'player' }
        $mine = @($combat.fighters)[0]
        if ($mine.weapon -ne 'sword_1h' -or $mine.bladeWeapon -ne 'sword_1h' -or -not ($mine.tipTravel -gt 0) -or @($combat.weapons).Count -lt 1 -or @($combat.attacks).Count -ne 7) {
            $problems += "Headless: combat.weapons — $($combat | ConvertTo-Json -Compress -Depth 4)"
        }
        # Этап 3б: запись сеанса, перемотка назад и вперёд к тому же хешу, новая симуляция из реплея, сверка реплея.
        $saved = Call-Stdio 'replay.save' @{ name = 'smoke' }
        if ($saved.ticks -ne 120 -or $saved.hashes -ne 121) { $problems += "Headless: replay.save — тиков $($saved.ticks), хешей $($saved.hashes)" }
        $back = Call-Stdio 'time.seek' @{ tick = 60 }
        $null = Call-Stdio 'time.step' @{ ticks = 60 }
        $again = Call-Stdio 'state.hash' @{}
        if (-not $back.rebuilt -or $again.total -ne $hash.total) { $problems += "Headless: time.seek 60 и 60 тиков вперёд — хеш $($again.total), был $($hash.total)" }
        $null = Call-Stdio 'session.start' @{ replay = 'smoke.swreplay' }
        $null = Call-Stdio 'time.step' @{ ticks = 120 }
        $status = Call-Stdio 'replay.status' @{}
        if (-not $status.playback.match -or $status.playback.hashesChecked -ne 121) { $problems += "Headless: session.start с реплеем — $($status.playback | ConvertTo-Json -Compress -Depth 5)" }
        $check = Call-Stdio 'replay.check' @{ path = 'smoke.swreplay' }
        if (-not $check.match) { $problems += "Headless: replay.check — $($check.divergence | ConvertTo-Json -Compress -Depth 5)" }
        # Этап 3в: условие в runUntil, наблюдение и история, останов посреди тика, события, снимки.
        $null = Call-Stdio 'session.start' @{ replay = 'smoke.swreplay' }
        $null = Call-Stdio 'watch.add' @{ entities = @('player'); fields = @('Transform.Position.X') }
        $until = Call-Stdio 'time.runUntil' @{ condition = @{ all = @(@{ entity = 'player' }, @{ field = 'Transform.Position.X'; greaterThan = 1 }) } }
        $at = [int]$until.tick
        $rows = @((Call-Stdio 'entity.history' @{ entity = 'player'; from = $at - 1; to = $at }).rows)
        $before = if ($rows.Count -eq 2) { [double]$rows[0].values.'Transform.Position.X' } else { $null }
        $after = if ($rows.Count -eq 2) { [double]$rows[1].values.'Transform.Position.X' } else { $null }
        if (-not $until.met -or $at -le 0 -or $at -ge 120 -or $rows.Count -ne 2 -or -not ($before -le 1) -or -not ($after -gt 1)) {
            $problems += "Headless: time.runUntil {condition} — $($until | ConvertTo-Json -Compress -Depth 5), история $($rows | ConvertTo-Json -Compress -Depth 5)"
        }
        $break = Call-Stdio 'time.break' @{ stage = 'Animation' }
        $pose = Call-Stdio 'entity.get' @{ entity = 'player' }
        if (-not $break.met -or $break.receipt.stage -ne 'Animation' -or $pose.receipt.stage -ne 'Animation' -or [int]$pose.receipt.tickId -ne $at) {
            $problems += "Headless: time.break Animation — $($break | ConvertTo-Json -Compress -Depth 5)"
        }
        $snap = Call-Stdio 'world.snapshot' @{}
        $null = Call-Stdio 'time.step' @{ ticks = 2 }
        $diff = Call-Stdio 'world.diff' @{ a = $snap.id; b = 'now'; filter = 'Transform/Position.X' }
        $events = Call-Stdio 'events.history' @{ types = @('PlayerCommand') }
        if ([int]$diff.changed -lt 1 -or [int]$events.total -lt 1) { $problems += "Headless: world.diff — изменено $($diff.changed), events.history — $($events.total)" }
        # Этап 3г: базовые инварианты движка, анимации и модуля не нарушены; свой инвариант агента — остановка на нарушении.
        $list = Call-Stdio 'invariant.list' @{}
        $ids = @($list.invariants | ForEach-Object { $_.id })
        foreach ($id in @('ecs.finite', 'animation.finite', 'animation.settled', 'animation.pool', 'animation.owners', 'locomotion.poses')) {
            if ($ids -notcontains $id) { $problems += "Headless: invariant.list — нет $id" }
        }
        if ([int]$list.violationsTotal -ne 0) { $problems += "Headless: нарушения инвариантов: $($list.recent | ConvertTo-Json -Compress -Depth 4)" }
        # Исправления после Пд: Headless (и агент в нём) — все кодовые инварианты каждый тик (точная остановка), периоды — только в живой игре.
        $notEvery = @($list.invariants | Where-Object { $_.enabled -and [int]$_.every -ne 1 } | ForEach-Object { "$($_.id)=$($_.every)" })
        if ($list.livePeriods -ne $false -or $notEvery.Count -gt 0) { $problems += "Headless: инварианты не каждый тик — livePeriods $($list.livePeriods), $($notEvery -join ', ')" }
        $x0 = [double](Call-Stdio 'entity.get' @{ entity = 'player' }).components.Transform.Position.X
        $null = Call-Stdio 'invariant.define' @{ id = 'agent.playerNear'; must = @{ field = 'Transform.Position.X'; lessThan = $x0 + 1 }; where = @{ entity = 'player' } }
        $null = Call-Stdio 'command.send' @{ command = 'PlayerCommand'; fields = @{ Move = @{ X = 1; Y = 0 } }; repeat = 240 }
        $broke = Call-Stdio 'time.breakOnInvariant' @{ ids = @('agent.playerNear'); maxTicks = 600 }
        $v = @($broke.violations)[0]
        $recorded3g = Call-Stdio 'events.history' @{ types = @('InvariantViolation') }
        if (-not $broke.met -or $broke.receipt.stage -ne 'runtime.export' -or $v.id -ne 'agent.playerNear' -or -not $v.entity -or [int]$recorded3g.total -lt 1) {
            $problems += "Headless: time.breakOnInvariant — $($broke | ConvertTo-Json -Compress -Depth 5), история $($recorded3g.total)"
        }
        # Этап 3д: манифесты фич, приёмка одной фичи, развилка записи сеанса и сравнение веток.
        $features = @((Call-Stdio 'feature.list' @{}).features | ForEach-Object { $_.id })
        foreach ($id in @('animation', 'control', 'determinism', 'movement', 'tools')) {
            if ($features -notcontains $id) { $problems += "Headless: feature.list — нет $id" }
        }
        $verified = Call-Stdio 'feature.verify' @{ id = 'movement'; views = $false; budgets = $false }
        if (-not $verified.ok) { $problems += "Headless: feature.verify movement — $(@($verified.failures) -join '; ')" }
        $fork = Call-Stdio 'experiment.fork' @{ replay = 'smoke.swreplay'; at = 60; variants = @(@{ name = 'back'; commands = @(@{ command = 'PlayerCommand'; fields = @{ Move = @{ X = -1; Y = 0 } }; from = 60; to = 61 }) }); measure = @(@{ entity = 'player'; field = 'Transform.Position.X' }) }
        $branches = @($fork.branches)
        if ($branches.Count -ne 2 -or [int]$branches[1].vsBase.firstHashTick -ne 61 -or [int]$branches[1].vsBase.lastCommon -ne 60) {
            $problems += "Headless: experiment.fork — $($fork | ConvertTo-Json -Compress -Depth 6)"
        }
        else {
            $compare = Call-Stdio 'experiment.compare' @{ a = $branches[0].file; b = $branches[1].file }
            if ([int]$compare.describedAt -ne 61 -or @($compare.describe).Count -lt 1) { $problems += "Headless: experiment.compare — $($compare | ConvertTo-Json -Compress -Depth 4)" }
        }
        # Этап П, точка Па: profile.stages — включить, 30 тиков, зоны тика, стадий, систем, инвариантов; выключить. Сеанс
        # может стоять посреди тика (time.breakOnInvariant выше): включение — с начала следующего тика, поэтому тик
        # доигрывается, затем итоги сбрасываются между тиками.
        $profOn = Call-Stdio 'profile.stages' @{ enable = $true }
        $null = Call-Stdio 'time.step' @{ ticks = 1 }
        $null = Call-Stdio 'profile.stages' @{ reset = $true }
        $null = Call-Stdio 'time.step' @{ ticks = 30 }
        $prof = Call-Stdio 'profile.stages' @{ top = 300 }
        $profZones = @($prof.zones | ForEach-Object { $_.name })
        $profMissing = @(@('tick', 'stage.Animation', 'core.animation-tick@Animation', 'barrier.Animation', 'invariant.ecs.finite', 'sim.record') | Where-Object { $profZones -notcontains $_ })
        $profOff = Call-Stdio 'profile.stages' @{ enable = $false }
        if (-not $profOn.requested -or -not $prof.enabled -or [int]$prof.periods -ne 30 -or $prof.per -ne 'tick' -or $profMissing.Count -gt 0 -or [int]$prof.mismatches -ne 0 -or $profOff.enabled) {
            $problems += "Headless: profile.stages — заказан $($profOn.requested), включён $($prof.enabled), тиков $($prof.periods), нет зон [$($profMissing -join ', ')], выключен $(-not $profOff.enabled)"
        }
        # Точка Пб: среда за тик и машина с вердиктом в ответе profile.stages.
        elseif (-not $prof.runtime -or -not ($prof.runtime.cpuMs -gt 0) -or -not $prof.machine -or $prof.machine.verdict -notin @('код', 'среда', 'машина')) {
            $problems += "Headless: profile.stages без runtime или machine: $($prof.runtime | ConvertTo-Json -Compress) $($prof.machine.verdict)"
        }
        # Точка Пв: захват аномалий сеанса настроен exe (порог по медиане), трасса кольца — Chrome Trace.
        elseif (-not $prof.anomalies -or $prof.anomalies -is [string] -or $prof.anomalies.threshold.mode -ne 'median') {
            $problems += "Headless: profile.stages без anomalies (захват не настроен exe?): $($prof.anomalies | ConvertTo-Json -Compress -Depth 3)"
        }
        else {
            $profTrace = Call-Stdio 'profile.stages' @{ trace = $true; anomalies = 0 }
            $traceJson = if ($profTrace.trace -and (Test-Path $profTrace.trace)) { Get-Content $profTrace.trace -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
            if (-not $traceJson -or @($traceJson.traceEvents | Where-Object { $_.ph -eq 'X' -and $_.name -like 'tick *' }).Count -lt 30) { $problems += "Headless: profile.stages {trace} — $($profTrace.trace)" }
        }
        $null = Call-Stdio 'session.stop' @{}
        if (-not $proc.WaitForExit(10000)) { $problems += 'Headless: не вышел после session.stop'; $proc.Kill() }
        elseif ($proc.ExitCode -ne 0) { $problems += "Headless: код выхода $($proc.ExitCode)" }
    }
    catch { $problems += "Headless: $($_.Exception.Message) $(if ($stderr.IsCompleted) { $stderr.Result })" }
    finally { if (-not $proc.HasExited) { $proc.Kill() } }

    # Точка 6а: сервер выходит сам. Три сервера сразу: (1) конец stdin посреди долгого time.runUntil — за секунды;
    # (2) клиент снял обёртку cmd, а вход держит открытым — сервер видит конец родителя; (3) --idle-exit 0.05 (3 с) —
    # выход после простоя без вызовов.
    $lifeNote = ''
    $life = @()
    try {
        function Start-Agent([string]$Extra, [switch]$ViaCmd) {
            $p = New-Object System.Diagnostics.ProcessStartInfo
            if ($ViaCmd) { $p.FileName = 'cmd.exe'; $p.Arguments = "/c `"`"$($headless.FullName)`" --agent $Extra`"" }
            else { $p.FileName = $headless.FullName; $p.Arguments = "--agent $Extra" }
            $p.UseShellExecute = $false
            $p.RedirectStandardInput = $true
            $p.RedirectStandardOutput = $true
            $p.RedirectStandardError = $true
            $p.StandardOutputEncoding = $utf8
            $p.StandardErrorEncoding = $utf8
            $started = [System.Diagnostics.Process]::Start($p)
            Add-ToJob $started
            $null = $started.StandardError.ReadToEndAsync()
            return $started
        }
        function Send-Line($p, [string]$line) {
            $bytes = $utf8.GetBytes($line + "`n")
            $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
            $p.StandardInput.BaseStream.Flush()
        }
        function Read-Line($p) {
            $t = $p.StandardOutput.ReadLineAsync()
            if (-not $t.Wait(60000)) { throw 'сервер не ответил за 60 с' }
            return $t.Result
        }
        $eof = Start-Agent ''
        $wrapped = Start-Agent '' -ViaCmd
        $idle = Start-Agent '--idle-exit 0.05'
        $life = @($eof, $wrapped, $idle)
        foreach ($p in $life) {
            Send-Line $p '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify-lifetime"}}}'
            if (-not (Read-Line $p)) { throw "сервер $($p.Id) закрыл вывод на initialize" }
        }
        $inner = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($wrapped.Id)" | Where-Object { $_.Name -eq 'Sword.Headless.exe' } | Select-Object -First 1
        if (-not $inner) { throw 'под cmd нет Sword.Headless.exe' }
        $innerProc = Get-Process -Id $inner.ProcessId
        $life += $innerProc
        Send-Line $idle '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"session.status","arguments":{}}}'
        $null = Read-Line $idle
        $idleWatch = [System.Diagnostics.Stopwatch]::StartNew()
        # Долгий вызов без нагрузки: runUntil к далёкому тику, затем пауза — вызов ждёт (до 600 с), тики не идут.
        Send-Line $eof '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"time.runUntil","arguments":{"tick":10000000,"timeoutSeconds":600}}}'
        Start-Sleep -Milliseconds 200
        Send-Line $eof '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"time.pause","arguments":{}}}'
        if ((Read-Line $eof) -notmatch '"id":3') { throw 'time.pause: ответ не на тот запрос (runUntil закончился раньше?)' }
        $eofWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $eof.StandardInput.Close()
        $wrapWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Stop-Process -Id $wrapped.Id -Force                   # только обёртка: наш конец входа сервера открыт
        $eofOut = $eof.WaitForExit(8000); $eofSec = [math]::Round($eofWatch.Elapsed.TotalSeconds, 1)
        $wrapOut = $innerProc.WaitForExit(8000); $wrapSec = [math]::Round($wrapWatch.Elapsed.TotalSeconds, 1)
        $idleOut = $idle.WaitForExit(20000); $idleSec = [math]::Round($idleWatch.Elapsed.TotalSeconds, 1)
        if (-not $eofOut) { $problems += 'Headless: конец stdin посреди time.runUntil — не вышел за 8 с' }
        if (-not $wrapOut) { $problems += 'Headless: обёртка cmd снята, вход открыт — не вышел за 8 с (слежение за родителем)' }
        if (-not $idleOut) { $problems += 'Headless: --idle-exit 0.05 — не вышел за 20 с простоя' }
        $lifeNote = "; время жизни (6а): конец stdin посреди runUntil — выход за $eofSec с, снятая обёртка — за $wrapSec с, простой 3 с — выход через $idleSec с"
    }
    catch { $problems += "Headless (время жизни): $($_.Exception.Message)" }
    finally { foreach ($p in $life) { if ($p -and -not $p.HasExited) { $p.Kill() } } }

    $headlessNote = "Headless: игрок в x = $([math]::Round([double]$x, 3)) после 120 тиков команды агента, клип $clipName; реплей сеанса, time.seek, session.start с реплеем — совпали; runUntil по условию, time.break, история, снимки — работают; инварианты не нарушены, свой инвариант агента встал на нарушении посреди тика; feature.verify movement зелёный, ветка experiment.fork разошлась после 61 тика, experiment.compare назвал участника; profile.stages — зоны 30 тиков$lifeNote"

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
        Add-ToJob $app
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
            # Точка Пг: кадр снимка ждёт простоя GPU — его GPU-время разобрано сразу (отставание 0), проходы с gpuMs.
            elseif (-not $bundle.gpu -or $bundle.gpu.lagFrames -ne 0 -or $bundle.gpu.frameId -ne $bundle.frameId -or -not ($bundle.gpu.frameMs -gt 0) -or
                @($bundle.gpu.passes | Where-Object { $_.name -eq 'scene.opaque' -and $_.gpuMs -gt 0 }).Count -ne 1) {
                $problems += "App: capture.bundle gpu — $($bundle.gpu | ConvertTo-Json -Compress -Depth 3)"
            }
            # Точка 5в: ID-буфер и оверлей капсул по заказу — пиксели игрока несут его сущность и сегменты тела (легенда
            # словами), середина рамки каждого кода — внутри рамки игрока; оверлей капсул — отдельный PNG.
            $ids = Call-Http 'capture.bundle' @{ name = 'verify-ids'; ids = $true; overlays = @('capsules') }
            $idPlayer = @($ids.entities | Where-Object { $_.name -eq 'player' })
            $mine = @($ids.ids.legend | Where-Object { $_.name -eq 'player' })
            $segments = @($mine | ForEach-Object { $_.segment })
            if (-not $ids.ids -or -not (Test-Path $ids.ids.png) -or -not (Test-Path $ids.ids.raw) -or -not $ids.overlays -or -not (Test-Path $ids.overlays.capsules)) {
                $problems += "App: capture.bundle ids/overlays — нет файлов: $($ids | ConvertTo-Json -Compress -Depth 3)"
            }
            elseif ($idPlayer.Count -ne 1 -or $segments -notcontains 'head' -or $segments -notcontains 'torso' -or @($mine | Where-Object { $_.title -notmatch 'игрока$' }).Count -gt 0) {
                $problems += "App: ID-буфер — у игрока сегменты [$($segments -join ', ')], названия [$(@($mine | ForEach-Object { $_.title }) -join ', ')]"
            }
            else {
                $r = $idPlayer[0].rect
                $outside = @($mine | Where-Object { ($_.rect[0] + $_.rect[2]) / 2 -lt $r[0] -or ($_.rect[0] + $_.rect[2]) / 2 -gt $r[2] -or ($_.rect[1] + $_.rect[3]) / 2 -lt $r[1] -or ($_.rect[1] + $_.rect[3]) / 2 -gt $r[3] })
                if ($outside.Count -gt 0) { $problems += "App: ID-буфер — середины кодов игрока вне его рамки [$($r -join ', ')]: $($outside | ConvertTo-Json -Compress -Depth 3)" }
                if (@($ids.ids.legend | Where-Object { -not $_.segment -or -not $_.entity }).Count -gt 0) { $problems += 'App: ID-буфер — код без сущности или сегмента в легенде' }
            }
            $recording = (Call-Http 'replay.status' @{}).recording
            if (-not $recording -or $recording.ticks -lt 60) { $problems += "App: replay.status — запись $($recording | ConvertTo-Json -Compress)" }
            # Этап 3в: условие встаёт ровно на своём тике, даже посреди кадра (8 тиков на кадр на паузе).
            $goal = [int]$recording.ticks + 37
            $until = Call-Http 'time.runUntil' @{ condition = @{ sim = 'ticks'; greaterOrEqual = $goal } }
            $after = Call-Http 'session.status' @{}
            if (-not $until.met -or [int]$until.tick -ne $goal -or [int]$after.tick -ne $goal -or -not $after.paused) {
                $problems += "App: time.runUntil {condition} — встали на $($after.tick), ждали $goal ($($until | ConvertTo-Json -Compress -Depth 4))"
            }
            # Этап 3г: инварианты в живой игре проверяются и не нарушены.
            $inv = Call-Http 'invariant.list' @{}
            $checked = @($inv.invariants | Where-Object { $_.enabled -and [int]$_.evaluations -gt 0 }).Count
            if ($checked -lt 6 -or [int]$inv.violationsTotal -ne 0) { $problems += "App: инварианты — проверялось $checked, нарушений $($inv.violationsTotal): $($inv.recent | ConvertTo-Json -Compress -Depth 4)" }
            # Исправления после Пд (решение пользователя): живая игра — тяжёлые инварианты раз в 4 тика, остальные — каждый тик.
            $heavyIds = @('ecs.finite', 'animation.finite', 'physics.finite', 'physics.kinematicPose', 'physics.kinematicSpeed')
            $badEvery = @($inv.invariants | Where-Object { $_.enabled -and [int]$_.every -ne $(if ($heavyIds -contains $_.id) { 4 } else { 1 }) } | ForEach-Object { "$($_.id)=$($_.every)" })
            if ($inv.livePeriods -ne $true -or $badEvery.Count -gt 0) { $problems += "App: инварианты живой игры — livePeriods $($inv.livePeriods), период не тот: $($badEvery -join ', ')" }
            # Этап П, точка Па: profile.stages в живой игре — включение с начала следующего кадра, итоги по кадрам: фазы кадра,
            # тики, проходы графа.
            $null = Call-Http 'profile.stages' @{ enable = $true }
            $null = Call-Http 'time.step' @{ ticks = 16 }
            $null = Call-Http 'capture.bundle' @{ name = 'verify-profile' }   # ещё кадр-другой после шагов
            $appProf = Call-Http 'profile.stages' @{ top = 300; per = 'frame' }
            $appZones = @($appProf.zones | ForEach-Object { $_.name })
            $appMissing = @(@('frame', 'frame.simulate', 'tick', 'frame.tools', 'frame.extract', 'frame.render', 'frame.record', 'render.scene.opaque', 'stage.Animation') | Where-Object { $appZones -notcontains $_ })
            if (-not $appProf.enabled -or [int]$appProf.frames -lt 2 -or [int]$appProf.ticks -lt 16 -or $appMissing.Count -gt 0 -or [int]$appProf.mismatches -ne 0) {
                $problems += "App: profile.stages — включён $($appProf.enabled), кадров $($appProf.frames), тиков $($appProf.ticks), нет зон [$($appMissing -join ', ')], несогласованных $($appProf.mismatches)"
            }
            elseif (-not $appProf.runtime -or -not $appProf.machine) { $problems += 'App: profile.stages без runtime или machine (точка Пб)' }
            elseif (-not $appProf.anomalies -or $appProf.anomalies -is [string]) { $problems += 'App: profile.stages без anomalies (точка Пв: захват настраивает Sword.exe)' }
            # После Пв: кадры с запросами агента (time.step, этот profile.stages) — кадры инструментов: служебные, помечены; заметность —
            # по частоте монитора окна (или 60 Гц по умолчанию).
            elseif ([int]$appProf.toolFrames -lt 1 -or [int]$appProf.serviceFrames -lt [int]$appProf.toolFrames) {
                $problems += "App: profile.stages — кадров инструментов $($appProf.toolFrames), служебных $($appProf.serviceFrames): запросы агента не помечают кадр"
            }
            elseif (-not ($appProf.anomalies.display.noticeableMs -gt 0) -or $appProf.anomalies.display.source -notin @('monitor', 'default')) {
                $problems += "App: profile.stages — нет порога заметности: $($appProf.anomalies.display | ConvertTo-Json -Compress)"
            }
            # Точка Пг: GPU-время кадра и проходов за окно — секция gpu (данные кадра N − 3).
            elseif (-not $appProf.gpu -or $appProf.gpu.lagFrames -lt 0 -or -not ($appProf.gpu.frame.meanMs -gt 0) -or @($appProf.gpu.passes).Count -lt 1) {
                $problems += "App: profile.stages без gpu (точка Пг): $($appProf.gpu | ConvertTo-Json -Compress -Depth 3)"
            }
            $graph = Call-Http 'render.graph' @{}
            $passes = @($graph.passes | ForEach-Object { $_.name })
            if ($passes -notcontains 'scene.opaque' -or $passes -notcontains 'hud') { $problems += "App: render.graph — проходы $($passes -join ', ')" }
            # Точка Пг: у проходов — gpuMs кадра gpu.frameId (показанный минус отставание: на паузе снимки и шаги, 0…3 кадра).
            elseif (-not $graph.gpu -or $graph.gpu.lagFrames -lt 0 -or $graph.gpu.lagFrames -gt 3 -or -not ($graph.gpu.frameMs -gt 0) -or
                @($graph.passes | Where-Object { $_.name -eq 'scene.opaque' -and $_.gpuMs -gt 0 }).Count -ne 1) {
                $problems += "App: render.graph gpu — $($graph.gpu | ConvertTo-Json -Compress) / $(@($graph.passes | ForEach-Object { "$($_.name)=$($_.gpuMs)" }) -join ', ')"
            }
            $null = Call-Http 'session.stop' @{}
            if (-not $app.WaitForExit(15000)) { $problems += 'App: не вышла после session.stop' }
            else {
                $report = ([System.IO.File]::ReadAllText($out).Trim()) | ConvertFrom-Json
                if ($report.report.exitReason -ne 'closed-by-tool') { $problems += "App: выход $($report.report.exitReason)" }
            }
            # Снимок глубины и цвета — под GPU-валидацией: сообщения слоя и предупреждения шейдеров — отказ.
            $d3d12 = @(Get-Content $err -Encoding UTF8 | Where-Object { ($_ -match '^\[d3d12\]' -and $_ -notmatch '^\[d3d12\] Message:') -or $_ -match '^\[shader\]' })
            if ($d3d12.Count -gt 0) { $problems += "App: сообщения D3D12 или шейдеров: $($d3d12 | Select-Object -First 3)" }
            $appNote = "живая игра (GPU-валидация): пауза, 60 тиков команды, снимок кадра $($bundle.width)×$($bundle.height) на тике $($bundle.tickId) с рамкой игрока [$($player[0].rect -join ', ')]; ID-буфер — кодов $(@($ids.ids.legend).Count), у игрока [$($segments -join ', ')], оверлей капсул снят; условие runUntil встало на своём тике; инварианты проверяются, нарушений нет; profile.stages — кадров $($appProf.frames) (кадров инструментов — $($appProf.toolFrames)), зон $($appZones.Count), порог заметности $($appProf.anomalies.display.noticeableMs) мс ($($appProf.anomalies.display.source)), GPU кадра $($appProf.gpu.frame.meanMs) мс; render.graph — GPU-время проходов кадра на $($graph.gpu.lagFrames) раньше показанного; снимок — GPU кадра $($bundle.gpu.frameMs) мс (отставание 0)"
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
        $profilerOut = Join-Path $root 'artifacts\bench\profiler.json'
        if (Test-Path $profilerOut) { Remove-Item $profilerOut }
        $log = & dotnet run -c Release --project (Join-Path $root 'tests\Sword.Benchmarks') 2>&1 | Out-String
        if (-not (Test-Path $out)) { return @{ ok = $false; detail = "нет итога бенчмарков: $(Get-Lines $log 'error|Exception' 3)" } }
        $r = Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
        $ratios = ($r.iteration | ForEach-Object { "чанк $($_.chunkBytes / 1024) КБ: ×$($_.ratio)" }) -join ', '
        $poses = (@($r.poseSampling) | ForEach-Object { "$($_.fighters) бойц. — $([math]::Round($_.tickNs / 1000, 1)) мкс, $($_.bytesPerTick) Б" }) -join ', '
        $profilerFile = Join-Path $root 'artifacts\bench\profiler.json'
        $p = if (Test-Path $profilerFile) { Get-Content $profilerFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        $profilerNote = if ($p) {
            "пара Begin/End профайлера: выключенного $($p.disabledPairNs) нс (не больше $($p.maxDisabledPairNs)), включённого $($p.enabledPairNs) нс, $($p.disabledBytes) и $($p.enabledBytes) Б" +
            "; период включённого (два опроса счётчиков) $($p.counters.PeriodEnabled.ns) нс, выключенного $($p.counters.PeriodDisabled.ns) нс; снимок машины $($p.counters.MachineSampleNoProbe.ns) нс, с пробой частоты $($p.counters.MachineSampleWithProbe.ns) нс; строка HUD $($p.counters.HudMeterFrame.ns) нс на кадр"
        } else { 'нет итога профайлера' }
        $detail = "обход ECS / сырой цикл — $ratios (не больше ×$($r.maxRatio)); тик команд и журналов $([math]::Round($r.commandsAndJournals.tickNs / 1000, 1)) мкс, $($r.commandsAndJournals.bytesPerTick) Б; сэмплинг поз (кроссфейд): $poses; $profilerNote"
        @{ ok = [bool]$r.ok -and $p -and [bool]$p.ok; detail = $detail }
    }
}
else {
    $steps.Add([ordered]@{ name = 'ecs-bench'; ok = $true; seconds = 0; detail = 'пропущено (без -Bench)' })
    if (-not $Json) { Write-Host 'SKIP ecs-bench                    пропущено (без -Bench)' }
}

Add-Step 'line-endings' {
    $extensions = @('.cs', '.csproj', '.props', '.targets', '.slnx', '.md', '.json', '.ps1', '.xml', '.hlsl', '.hlsli', '.swreplay',
        '.editorconfig', '.gitattributes', '.gitignore', '.txt', '.yml', '.yaml', '.cmd')
    # Путь — от корня репозитория: .claude\worktrees\ — рабочие деревья git других агентов (у каждого свой verify.ps1),
    # остальной .claude\ (settings.local.json) проверяется; verify.ps1 внутри рабочего дерева проверяет его файлы.
    $skip = '\\(bin|obj|\.git|\.vs|artifacts|TestResults)\\|^\\\.claude\\worktrees\\'
    $bad = @(Get-ChildItem $root -Recurse -File -Force | Where-Object {
            ($extensions -contains $_.Extension.ToLowerInvariant()) -and ($_.FullName.Substring($root.Length) -notmatch $skip) -and
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

Add-Step 'processes' {
    # Точка 6а: после verify не остаётся процессов SWORD — ни в его задании (фоновые запуски; их дети — там же), ни среди
    # потомков. Снятые шагами процессы исчезают не мгновенно — ждём до 5 с; оставшиеся — провал, задание их завершает.
    function Get-SwordLeft {
        $all = @(Get-CimInstance Win32_Process)
        $children = @{}
        $created = @{}
        foreach ($p in $all) { $children[[int]$p.ParentProcessId] += @($p); $created[[int]$p.ProcessId] = $p.CreationDate }
        $tree = New-Object System.Collections.Generic.List[object]
        $queue = New-Object System.Collections.Generic.Queue[int]
        $queue.Enqueue($PID)
        while ($queue.Count -gt 0) {
            $parent = $queue.Dequeue()
            foreach ($c in @($children[$parent])) {
                # ParentProcessId сироты указывает на умершего родителя, чей номер мог достаться нашему процессу:
                # потомок — только запущенный не раньше родителя (чужие серверы других сеансов не трогаем).
                if (-not $c -or $c.ProcessId -eq $PID -or $c.CreationDate -lt $created[$parent]) { continue }
                $tree.Add($c)
                $queue.Enqueue([int]$c.ProcessId)
            }
        }
        $inJob = @([SwordVerifyJob]::Processes($script:job))
        $all | Where-Object { $_.ProcessId -ne $PID -and ($inJob -contains [int]$_.ProcessId -or $tree.ProcessId -contains $_.ProcessId) -and
            $_.Name -match '^Sword(\.Headless)?\.exe$' }
    }
    $left = @()
    for ($i = 0; $i -lt 50; $i++) {
        $left = @(Get-SwordLeft)
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    }
    $jobNote = if ($script:job -eq [IntPtr]::Zero) { 'задания Windows нет (Add-Type или CreateJobObject не удались) — только потомки' }
               else { "в задании побывало фоновых запусков: $($script:jobAdded)" }
    if ($left.Count -eq 0) { @{ ok = $true; detail = "процессов SWORD после verify не осталось; $jobNote" } }
    else {
        $names = ($left | ForEach-Object { "$($_.Name) $($_.ProcessId) ($($_.CommandLine))" }) -join '; '
        [void][SwordVerifyJob]::Terminate($script:job)
        foreach ($p in $left) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
        @{ ok = $false; detail = "остались процессы SWORD (завершены): $names" }
    }
}

$allOk = @($steps | Where-Object { -not $_.ok }).Count -eq 0
# -Only: итог — только выбранных шагов (остальные — «пропущено (-Only)»); это видно и в JSON (only), и в последней строке.
$onlySteps = if ($Only.Count -gt 0) { @('build') + @($Only | Where-Object { $_ -ne 'build' -and $_ -ne 'processes' }) + @('processes') } else { @() }
$onlyNote = if ($onlySteps.Count -gt 0) { " (только шаги -Only: $($onlySteps -join ', '))" } else { '' }
if ($Json) {
    $summary = [ordered]@{ ok = $allOk; configuration = $Configuration }
    if ($onlySteps.Count -gt 0) { $summary.only = $onlySteps }
    $summary.steps = $steps
    $summary | ConvertTo-Json -Depth 4 -Compress
}
else {
    Write-Host ''
    Write-Host $(if ($allOk) { "Проверка пройдена$onlyNote." } else { "Проверка НЕ пройдена$onlyNote." })
}
if ($allOk) { exit 0 } else { exit 1 }
