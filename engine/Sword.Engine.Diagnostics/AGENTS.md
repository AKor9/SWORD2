# Sword.Engine.Diagnostics — правила

Основа диагностики (раздел 3 ARCHITECTURE_PLAN.md: «Log, профайлер CPU, ручки (Tuning), каналы трасс»), Neutral, зависит только от
Core. Сейчас: журнал (`Log`), профайлер CPU (`CpuProfiler`), Chrome Trace (`ChromeTrace`), сводка F2 (`ProfileDigest`), GPU-время
проходов в профайлере кадра (`CpuProfilerGpu.cs`), прогрев JIT (`JitWarmup`), ручки (`TuningGroup`, `Tuning.cs`). Фича профайлера —
[game/Sword.Game/Features/Profiler/README.md](../../game/Sword.Game/Features/Profiler/README.md).

- `Line` — строка на стеке (HUD, строки состояния `TuningText` модулей): не влезло — обрезается, числа — инвариантной
  культурой; своих помощников форматирования в модулях не заводить.
- Ручки — `TuningGroup`: группа (вкладка панели F3) из заголовков, чисел, флагов, кнопок и строк состояния; значение — живые делегаты
  `Get`/`Set`, привязку к симуляции делает клиент (`Engine.Client/TuningBinding`). Сохранение — `Values`/`Apply` (файл пишет хост).
  Проверка — `tests/Sword.Engine.Runtime.Tests/TuningGroupTests`.
- `Log` не без выделений: из горячего пути (тик, кадр) в него не писать — только события и ошибки.
- `JitWarmup.Run(шаг)` — шаги, пока число скомпилированных методов процесса не стоит 250 мс подряд, не дольше 1 с; греет Headless (0 Б/тик
  судится только после затишья JIT) и прогрев физики.
- Профайлер — однопоточный, у владельца потока (главный поток App — `ClientHost.Profiler`; Headless — свой у `--profile`). Общего
  статического нет; общий `CpuProfiler.Off` не пишет ничего (`if (_off) return` в периодах).
- Зона — номер, заведённый заранее (`Zone(имя)` при сборке мира, графа, клиента), и стабильное имя; в тике и кадре имя не строится.
- `Begin/End` — `AggressiveInlining`, выключенный стоит проверку поля `bool`; работа — в невстраиваемых `Push/Pop` (время и выделенные
  байты потока, 0 Б). Поле обычное, не `static readonly` — иначе профайлер не включить на ходу.
- Время — через `Now()`: тесты подменяют часы внутренним `ProfileClock` (конструктор `CpuProfiler(history, clock)`,
  `InternalsVisibleTo` — `Sword.Engine.Runtime.Tests`).
- Периоды: кадр и тик; тик внутри кадра — в итогах обоих. Служебные кадры — в кольце, не в средних. Включение (`SetEnabled`) — только на
  границе верхнего периода. Несогласованный `End` — исключение в Debug, в Release — `Mismatches` и раскрутка; участок, который ловит
  исключение и продолжает период, раскручивает стек сам (`Depth`/`UnwindTo`).
- Итоги — `Stats`, `Report`/`Write` (`profile.json`, формат `sword-profile`), кратко — `Summary`; кольцо — `WriteTrace` (Chrome Trace:
  кадры и тики с настоящим началом, зоны подряд из итогов периода; вложенность «X» должна оставаться верной).
- GPU-время — зоны вида `ProfileZoneKind.Gpu` (`gpu.frame`, `gpu.<проход>`): время задаёт рендер (`RecordGpu`), отставание —
  `NoteGpuFrame`; вне дерева CPU, в отчёте — `frames.gpu`, в F2 — свои строки.
- Симуляцию профайлер не трогает: в хеш и реплей не входит.

Проверка: `dotnet test tests/Sword.Engine.Runtime.Tests --filter "FullyQualifiedName~Profiler"` (`ProfilerTests`, `ProfilerGpuTests`),
`tests/Sword.Game.Tests/ArenaTests` (толпа с профайлером — 0 Б/тик, те же хеши), `tests/Sword.Benchmarks` (`ProfilerZone`,
`ProfilerPeriod`), `verify.ps1` (`headless-smoke`, `app-smoke`, `-Bench`).
