<#
.SYNOPSIS
    Полная проверка SWORD2 — раздел 12 ARCHITECTURE_PLAN.md.

.DESCRIPTION
    Шаги:
      build                 сборка SWORD.slnx (предупреждения — ошибки);
      test                  тесты решения, в том числе архитектурные;
      forbidden-references  заготовки tests/fixtures/forbidden-references/SWORD00N-*: каждая обязана упасть со своим
                            кодом — иначе проверка ссылок в сборке сломана;
      without-agent         App и Headless с -p:WithoutAgent=true собираются и не содержат сборок агента;
      app-smoke             Sword.exe без фокуса: 90 кадров, смена размера по кругу, отладочный слой D3D12 —
                            итог JSON в порядке, сообщений D3D12 нет (нужна видеокарта; -SkipGpu — пропустить);
      line-endings          все текстовые файлы — CRLF (соглашение AGENTS.md);
      reference             SHA-256 эталонного пака совпадает с tests/reference/reference.json.

.PARAMETER Json
    Итог одним JSON-объектом в stdout, UTF-8 (для агентов). Код выхода: 0 — всё прошло, 1 — нет.

.PARAMETER SkipReference
    Не проверять эталонный пак (например, на машине без него).

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
    [switch]$SkipGpu
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
    $detail = if ($ok) { Get-Lines $out 'Warning\(s\)|Error\(s\)' 2 } else { Get-Lines $out ': error ' 5 }
    @{ ok = $ok; detail = $detail }
}

Add-Step 'test' {
    $out = & dotnet test $solution -c $Configuration --no-build -nologo | Out-String
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

if ($SkipGpu) {
    $steps.Add([ordered]@{ name = 'app-smoke'; ok = $true; seconds = 0; detail = 'пропущено (-SkipGpu)' })
    if (-not $Json) { Write-Host 'SKIP app-smoke                    пропущено (-SkipGpu)' }
}
else {
    Add-Step 'app-smoke' {
        # Точка 1а: окно, устройство, очистка кадра, изменение размера — без фокуса и с отладочным слоем D3D12.
        $exe = Get-ChildItem (Join-Path $root "game\Sword.Game.App\bin\$Configuration") -Recurse -Filter 'Sword.exe' | Select-Object -First 1
        if (-not $exe) { return @{ ok = $false; detail = 'не найден Sword.exe — сборка не прошла?' } }
        $dir = Join-Path $root 'artifacts\verify\app-smoke'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $stdout = Join-Path $dir 'stdout.json'
        $stderr = Join-Path $dir 'stderr.log'
        $frames = 90
        $process = Start-Process -FilePath $exe.FullName -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
            -ArgumentList @('--windowed', '800x600', '--frames', "$frames", '--resize-cycle', '--no-activate', '--debug', '--json')
        $text = [System.IO.File]::ReadAllText($stdout).Trim()
        if ($process.ExitCode -ne 0 -or -not $text) { return @{ ok = $false; detail = "код выхода $($process.ExitCode); $([System.IO.File]::ReadAllText($stderr).Trim())" } }
        $result = $text | ConvertFrom-Json
        $d3d12 = @(Get-Content $stderr | Where-Object { $_ -match '^\[d3d12\]' })
        $problems = @()
        if (-not $result.ok) { $problems += "итог: $($result.error)" }
        elseif ($result.report.frames -ne $frames) { $problems += "кадров $($result.report.frames) из $frames" }
        elseif ($result.report.resizes -lt 3) { $problems += "изменений размера $($result.report.resizes), ожидалось не меньше 3" }
        if ($d3d12.Count -gt 0) { $problems += "сообщения D3D12: $($d3d12 | Select-Object -First 3)" }
        if ($problems.Count -eq 0) {
            @{ ok = $true; detail = "$($result.report.adapter): $($result.report.frames) кадров, изменений размера $($result.report.resizes), кадр $($result.report.averageFrameMs) мс" }
        }
        else { @{ ok = $false; detail = $problems -join '; ' } }
    }
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
