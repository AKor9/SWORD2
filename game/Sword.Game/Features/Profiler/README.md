# Диагностический профайлер

Профайлер движка: где время кадра App и тика симуляции и сколько байтов выделяет каждый участок; сколько GPU-времени у кадра и
проходов. Симуляцию не меняет. Панель для человека — этап 14 (раздел 13 плана). История — решения, замеры и разборы —
[docs/history/profiler.md](../../../../docs/history/profiler.md).

Код: `engine/Sword.Engine.Diagnostics/` — профайлер (`CpuProfiler*.cs`, отчёт `CpuProfilerReport.cs`), `ChromeTrace.cs`,
`ProfileDigest.cs` (сводка F2), правила — [AGENTS.md](../../../../engine/Sword.Engine.Diagnostics/AGENTS.md); места замера —
`World.RunTick`, `Simulation.Step`, `SimulationInvariants`, `ClientHost.Run`, `Renderer.Render`, `RenderGraph.Execute`,
`ArenaView.Extract`, `ArenaClient.BuildFrame`; GPU — `Sword.Engine.Rhi/GpuTimer.cs`, `GraphicsDevice`, `Sword.Engine.Render/GpuTimes.cs`;
числа кадра и бенч — `Sword.Engine.Client/FrameTools.cs`; HUD и F2 — `game/Sword.Game.Client/Hud/Hud.cs`, `ArenaInput.HudProfiler`;
ключи — `Program.cs` обоих exe.

## Откуда логика

Профайлера зон CPU в SWORD нет — это устройство SWORD2 (разделы 3, 5, 7, 8 плана). Идеи SWORD 2eab13b: время кадра CPU
(`Program.cs` → `FrameStatsMeter`); секция F2 «ПРОФАЙЛЕР, мс» (`Render/Hud.cs`) — та же раскладка, но зоны CPU по своему времени и
проходы GPU под ними; `GpuProfiler` (куча таймстемпов, разбор сразу после ожидания слота — кадр N − 3 без стойла) — `GpuTimer`.
Отличия от `GpuProfiler`: замер ставит рендер-граф вокруг каждого прохода сам (у SWORD — `Scope` руками) и ещё один — вокруг кадра
целиком; очередь одна — «compute» и «графика» стали «кадр» и «проходы»; слот сверяется по номеру кадра.

## Устройство

### Зоны, периоды, итоги

- **Зона** — номер, заведённый заранее, и стабильное имя. Номера заводятся при подключении профайлера, а не в тике: мир —
  `World.AttachProfiler`, симуляция — `Simulation.AttachProfiler`, рендер — `Renderer.Profiler` (проход — по имени при первой
  встрече), клиент игры — в `Initialize`; в тике — только номер (`Registration.Zone`).

  | зона | что |
  |---|---|
  | `frame` | кадр App (период): от прокачки сообщений до конца кадра |
  | `frame.messages`, `frame.resize`, `frame.input`, `frame.simulate`, `frame.extract`, `frame.render`, `frame.wait` | фазы кадра `ClientHost.Run`: сообщения окна, смена размера, ввод и камера, тики кадра, мир представления, кадр рендера, ограничитель кадров |
  | `frame.fenceWait`, `frame.build`, `frame.record`, `frame.present`, `frame.capture` | внутри `frame.render`: ожидание забора кадра в полёте, построение кадра игрой, запись графа в список команд, показ (отправка и Present), снимок |
  | `render.<проход>` | проход графа при записи (`render.scene.opaque`, `render.hud`, `render.debug.bodies`…) |
  | `arena.extract.poses`, `arena.extract.bodies` | копия поз и чтение с переносом капсул тел — по вызову на бойца |
  | `arena.build.skin`, `arena.build.hud`, `arena.build.bodies` | скиннинг бойцов, HUD, оверлеи (F7) в построении кадра |
  | `tick` | тик симуляции (период): `Simulation.Step` целиком |
  | `stage.<стадия>` | стадия: системы, барьер и проверки после барьера |
  | `<метка>@<стадия>` | регистрация системы (имя `SystemInfo.Name`) |
  | `barrier.<стадия>` | барьер: команды, журналы, хуки, события |
  | `invariant.<id>` | одна проверка инварианта |
  | `sim.record` | запись и сверка реплея с хешем |
  | `gpu.frame`, `gpu.<проход>` | GPU-время кадра и прохода (вид `Gpu`, вне дерева CPU — ниже) |

- **Начало и конец зоны** — `Stopwatch.GetTimestamp` и `GC.GetAllocatedBytesForCurrentThread` (байты потока владельца).
  Иерархия — стек; своё время — время зоны минус дочерние.
- **Периоды** — кадр и тик (тик внутри кадра — в обоих). Конец периода — строка кольца (время, байты, вызовы каждой зоны) и
  заголовок: номер, тики кадра, доля тика, время, байты, служебный ли. Кольцо — 1024 кадра и 1024 тика (выделяется при первом
  включении). Служебные кадры (первый и все до первого сыгранного тика включительно, смена размера, снимок) — в кольце, но не в итогах.
- **Итоги** на зону: среднее, максимум, байты и вызовы на период, доля периодов с зоной — с последнего сброса; медиана и p99
  — по окну кольца. Отчёт — `profile.json` (формат `sword-profile`): `frames` и `ticks` (`count`, `service`, `window`, `zones`,
  у кадров — `gpu`), `history`, `mismatches`, `abandoned`, `overflows`.
- **Выключен по умолчанию**: выключенный — проверка поля на `Begin`/`End`; включение и выключение — на границе верхнего
  периода (следующий кадр App, следующий тик Headless). Экземпляр на поток-владелец; «профайлера нет» — общий
  `CpuProfiler.Off`. Несогласованный `End` — исключение в Debug, счётчик `mismatches` в Release.
- **Симуляцию не трогает**: только читает часы и счётчик выделений; в хеш и реплей не входит; включение — ключ запуска или F2, а не
  команда симуляции.

### Chrome Trace, F2

- **Chrome Trace Event JSON** (`ChromeTrace`; Perfetto — ui.perfetto.dev → «Open trace file», `chrome://tracing`): `--profile-trace`
  (`trace.json`) — кольцо: кадры, тики, зоны из итогов периода (место внутри периода условное, вложенность верная).
- **F2** (действие `hud_profiler`; справка F1 — «F2 — профайлер»): секция «ПРОФАЙЛЕР, мс» внизу панели HUD — главные зоны кадра
  по своему времени (мс на кадр за 0,25 с, до 8 строк, `frame (вне зон)`), «GPU кадр» и до 6 проходов GPU. F2 включает профайлер,
  если он выключен, и выключает — только если включил сам; в бенче скрыт по умолчанию. Сводка — `ProfileDigest` (раз в 0,25 с,
  без выделений). Итог `--json` Sword.exe — `hud.profiler`, `profiler`.

### GPU-время проходов

- **Таймстемпы** — `GpuTimer` (у `GraphicsDevice.Timer`): куча на 256 замеров × 2 × 3 кадра в полёте (1536 таймстемпов),
  readback 12 КБ. Замер 0 — кадр целиком (от открытия списка команд до его закрытия); проходы — ставит рендер-граф вокруг
  каждого прохода (`RenderGraph.Execute`); частота — `GetTimestampFrequency` очереди, один раз.
- **Разбор без стойла** — в `GraphicsDevice.BeginFrame` после ожидания забора слота: в кадре N видны времена кадра N − 3.
  `WaitForIdle` (смена размера, снимок, `ReadStorage`, конец прогона) разбирает все отправленные — отставание 0.
- **Сверка по номеру**: слот помнит номер своего кадра; публикуется только отправленный кадр (брошенный — `AbandonFrame` —
  нет), номер опубликованного только растёт.
- **Опубликованное** — `Renderer.Gpu` (`GpuFrameTimes`): `Frame`, `Lag`, `FrameMs`, `PassesMs`, проходы (`NameOf`,
  `MillisecondsOf`), `Sequence`, `Dropped`; обновляется на месте — 0 Б на кадр. GPU-время к проходам — при выдаче
  (`GpuFrameTimes.Join` → `RenderPassSummary` с `GpuMs`).
- **Всегда включено**; `Renderer.Gpu.Enabled` — выключатель только для замера цены (тест), ключа нет.
- **Числа кадра и бенч**: `FrameStats.GpuMs`/`GpuPassesMs` (HUD); `FrameBench` на каждый новый разобранный кадр — `gpuMs`,
  `gpuP99Ms`, `gpuFrames`, `passes` (мс на кадр), строки `[bench]   <проход>  X мс/кадр GPU`.
- **В профайлере** — зоны вида `Gpu` (`CpuProfiler.RecordGpu`, 0 Б) вне дерева CPU: в своё время кадра и сводку CPU не входят;
  `frames.gpu` (`lagFrames`, `frame`, `passes`), `history[].gpuFrame`.
- **HUD**: полоса бюджета 60 Гц — CPU + GPU; секция «GPU» — «кадр» и «проходы», вызовы рисования и квады UI.

## Настройки

- `Sword.exe --profile` — с первого кадра (с `--bench` итоги — кадры замера); отчёт — `artifacts/profile/<дата-время>-app-<pid>/profile.json`,
  в `--json` — секция `profile` (файл, кадров, тиков, 12 главных зон кадра).
- `Sword.Headless --profile` — итоги — тики после прогрева (120; до них — прогрев JIT отдельной симуляцией, ≤ 1 с, у прогона без
  `--replay` от 240 тиков, `jitWarmup` в `--json`); отчёт — `artifacts/profile/<…>-headless-<pid>/profile.json`.
- `--profile-trace` (оба exe) — то же, что `--profile`, и `trace.json`. F2 — `--set input.hud_profiler=<клавиша>`.
- Без ключей: GPU-время — в HUD и `--json`; частота монитора — строка журнала `[client] …, монитор N Гц`, `report.displayHz`.

## Зависимости

`Sword.Engine.Diagnostics` (Neutral, только Core): его видят Ecs, Runtime, Render, Client и клиент игры — новых ссылок между
проектами нет.

## Ограничения

- Зоны — только поток владельца и где их поставили: подсистемы (анимация, физика) изнутри не размечены; другие потоки не видны.
  Стеков вызовов нет — `dotnet-trace collect -p <pid>` / PerfView.
- Точность: часы — 100 нс (короткие зоны точны в среднем); первое включение на ходу (F2) выделяет кольцо (~5 МБ) в кадре включения.
- «Вне зон» кадра (вершина цикла `ClientHost.Run`) не видно; место зон в трассе кольца — условное.
- GPU: запаздывание N − 3 (`Lag`, `lagFrames`, `gpuFrame`); частота GPU плавает — сравнивать прогоны на одной машине в одном
  состоянии (`SetStablePowerState` не делаем); время прохода — интервал между его метками (сумма проходов не больше кадра); часы
  CPU и GPU не сопоставлены (этап 14) — GPU-времени нет в Chrome Trace; одна очередь; до 255 проходов на кадр (`dropped`);
  одноимённые UI-проходы складываются по имени.

## Цена

Release, .NET 10.0.11, Ryzen 7 7800X3D, RTX 5070, 2026-09-23 (прогоны по точкам — история):
- пара Begin/End — выключенного ~0,2 нс, включённого ~39 нс; 0 Б;
- тик толпы с выключенным профайлером — в шуме; с профайлером (время систем, ~60 зон на тик) — +4…7 %, 0 Б/тик;
- кадр App с `--profile` — те же средний кадр, p99 и байты; секция F2 — +0,04…0,05 мс CPU кадра;
- таймстемпы GPU — в шуме (CPU записи кадра и кадр бенча), 0 Б.

## Решения и почему

Подробно, с причинами и замерами, — [история](../../../../docs/history/profiler.md).
- Профайлер — движковый (`Sword.Engine.Diagnostics`), фича — здесь; экземпляр на поток-владелец.
- Флаг — обычное поле (не `static readonly`): иначе профайлер не включить на ходу; `Begin/End` встраиваются.
- Таймстемпы GPU — всегда (цена в шуме, нужны HUD и бенчу); `RenderPassReport` без поля `GpuMs` (+8 Б на проход в каждом кадре)
  — GPU-время сливается с проходами при выдаче; GPU-времена в профайлере — зонами вида `Gpu`, без второго хранилища.
- Главное средство от компиляций OSR на главном потоке — вынос тела долгого цикла в свой метод (правило — AGENTS.md);
  `AggressiveOptimization` — только тонкому методу из вызовов (иначе теряется PGO).
- Тяжёлые инварианты в живой игре — раз в 4 тика, `animation.finite` без модельных матриц (решение пользователя).

## Что проверить в игре

1. `Sword.exe --profile` — игра как обычно (FPS и плавность — как без ключа); через 10–20 с Esc. В журнале строка `[profile] профайлер: …\artifacts\profile\…-app-…\profile.json`; в файле первая зона `frames.zones` — `frame` (рядом `frame.render`, `frame.simulate`, `frame.fenceWait`), в `ticks.zones` — `stage.Animation`, `core.animation-tick@Animation`, `invariant.…`; `mismatches` — 0.
2. `Sword.exe`, F2 — внизу панели HUD секция «ПРОФАЙЛЕР, мс»: зоны кадра с полосами (`frame.fenceWait`, `frame.present`, `arena.build.skin`…), голубая строка «GPU кадр» и проходы GPU (`scene.opaque`, `hud`; F7 — появляется `debug.bodies`). F2 ещё раз — секция скрыта; в справке F1 — «F2 — профайлер».
3. `Sword.exe --units 32 --bench 10 --profile-trace` — в журнале `[profile] трасса (Perfetto, chrome://tracing): …\trace.json`; открыть на https://ui.perfetto.dev («Open trace file») — «SWORD: кольцо профайлера», дорожки «кадры» и «тики», зоны кадра вложены (`frame.render` > `frame.fenceWait`…).
4. `Sword.exe` — в панели HUD секция «GPU»: «кадр» и «проходы» в мс (доли миллисекунды, не нули: 0,2…0,5 — по окну и тактам GPU; с `--units 32` — больше), полоса бюджета под FPS — оранжевая часть (CPU) и за ней голубая (GPU). `Sword.exe --units 32 --bench 10` — строка `[bench] кадров …` кончается на `; GPU X мс, p99 Y`, под ней `[bench]   scene.opaque … мс/кадр GPU` и `hud`.
5. `Sword.Headless --ticks 3600 --units 32 --move 1,0 --profile` — строка `[profile] профайлер: …headless…\profile.json`, в файле `ticks.count` — 3480 (после прогрева 120 тиков), первая зона — `tick`.

## Проверки

- `--json` обоих exe — `profile`; Sword.exe — ещё `report.gpu`, `report.passes[].gpuMs`, `report.stats.gpuMs`,
  `report.bench.gpuMs`/`gpuP99Ms`/`passes`, `report.displayHz`, `profiler`, `hud.profiler`. Весь список компиляций процесса —
  `DOTNET_JitDisasmSummary=1`.
- Свойства «0 Б на главном потоке», «те же хеши», «выключенный стоит ~0» держат тесты и `verify.ps1`, а не инвариант симуляции
  (профайлер вне мира); включение и F2 — не команды симуляции (в реплей не идут).
- `tests/Sword.Engine.Runtime.Tests`: `ProfilerTests` (зоны, периоды, кольцо, итоги, включение на границе, `Off`, 0 Б, те же
  хеши, прогрев JIT), `ProfilerGpuTests` (GPU-зоны вне дерева CPU, `frames.gpu`, F2, 0 Б; поддельные часы `ProfileClock` —
  внутренний конструктор `CpuProfiler(history, clock)`, `InternalsVisibleTo` — Runtime.Tests).
- `tests/Sword.Engine.Render.Tests/GpuTimesGpuTests` (категория Gpu, GPU-валидация: долгий проход, сумма ≤ кадра, `Lag` 3 и 0,
  брошенные кадры и смена размера, 0 Б на кадр, переполнение 255), `WindowRefreshGpuTests` (частота монитора окна).
- `FrameToolsTests` (Client), `HudTests`, `HudDrawTests` (Game); `tests/Sword.Benchmarks` (`ProfilerZone`, `ProfilerPeriod`;
  `verify.ps1 -Bench`) — цена пары и периода.
- `verify.ps1`: `headless-smoke` (толпа с `--profile`: 0 Б/тик, тот же хеш, отчёт; `--profile-trace` — `trace.json`), `app-smoke`
  (те же байты на кадр с профайлером и без, F2, `gpuMs` у каждого прохода под GPU-валидацией, `report.gpu`, `frames.gpu`, GPU бенча).

## Известные ошибки

Профайлера — нет. Остаётся: GPU скиннинга 33 бойцов (`scene.opaque` ~97 % GPU кадра) — этап рендера.
