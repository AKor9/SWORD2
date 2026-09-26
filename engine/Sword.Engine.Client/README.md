# Sword.Engine.Client — клиентские модули

Правила клиента движка целиком — [AGENTS.md](AGENTS.md); здесь — контракт клиентских модулей (раздел 7 плана, «Модули»; раздел 6 —
«GPU-симуляции»). Первый потребитель — модуль огня (`game/modules/Sword.Game.Fire.Client`).

## Модуль

- `IClientModule` — `Id`, `ConfigureClient(ClientBuilder b)`. Сборка `game/modules/Sword.Game.<Модуль>.Client`: видит Contracts, Data,
  Client, Render, Ui, Diagnostics и свою часть симуляции — не Ecs, не ядро игры, не другие модули (`ArchitectureRules.ClientModuleAllowed`).
- Активация — `modules.<id>.enabled`: у модуля с частью симуляции — вместе с ней (`Simulation.ActiveModules`), у чисто клиентского — его Id
  объявлен симуляции (`Simulation.Build(…, clientModules)`, у игры — `SwordSimulation.ClientModuleIds`) и включён
  (`Simulation.ClientOnlyModules`; в состав реплея и хеш не входит). Id, которого симуляция не знает, — ошибка композиции. Выключенный не
  настраивается вовсе: ни прохода, ни ресурса, ни ручки, ни наблюдателя; его секция пропущена (`ConfigSet.Skip`).
- Сборка без модуля — `-p:Without<Модуль>=true`: ссылка exe на модуль — под условием (тест `ModuleReferences_AreRemovedWithoutModule`),
  секция модуля, которого нет в сборке, пропускается композицией exe; проверка вживую — шаг `without-modules` verify.ps1.
- Композиция — exe (`ClientModules.Compose(config, simulation, modules, services)` до `ConfigSet.Complete`), у игры —
  `Sword.Game.App/ClientModuleList.cs`; `ClientHost(…, modules)` ведёт их в своём кадре. Игра (`IClientGame`) модулей не знает.

## ClientBuilder

| вызов | что |
|---|---|
| `Config<T>()` | секция конфига с Id модуля — слои и строгость как у секций симуляции |
| `Service<T>()` | сервис клиентской части игры (`ClientServices`, даёт exe; сервис над симуляцией — `Add<T>(Func<SimulationLink, T>)`, связь создаёт `Compose`); нет — null. Оружие тика — `IWeaponTips`, туман сцены — `ISceneFog`, солнце и небо — `ISceneSky` (`Game.Contracts`) |
| `AfterTick(Action<ulong>)` | наблюдатель: после **каждого** тика, по порядку, с `TickId` (номер сыгранного тика с 0, как `SimTime.Tick`), мир — после своего тика; 0…8 тиков за кадр; пауза — ни одного вызова |
| `Extract(FrameExtract)` | после тиков кадра и извлечения игры, с долей тика (`FrameView.Alpha`, `Ticks`) |
| `Pass(FramePhase, Action<RenderFrame>)` | проходы кадра после проходов игры; проход не своей фазы — ошибка кадра |
| `Lights(Action<PointLights>)` | точечные источники и light functions кадра — до проходов игры: их видят освещение, константы и оверлей `lights` игры |
| `GpuSimulation(IGpuSimulation)` | GPU-симуляция; вернёт её заявки `SimRequests` |
| `Tuning(TuningGroup)` | вкладка панели F3 (ручки клиента — делегаты, в реплей не идут) |
| `Overlay(name)` | оверлей: слой снимка (`capture.bundle`, `ClientHost.Overlays`) и `Sword.exe --overlay`; `Shown` — на экране |
| `Shaders(Action<ShaderContract>)` | числа для шейдеров — до компиляции первого шейдера |
| `Initialize(Action<ClientModuleContext>)` | рендер готов (после игры): `Renderer`, `Camera` (камера кадра игры), `Own(ресурс)` |
| `TickDt` | шаг тика симуляции, с (1/120): такт наблюдателя — например, шаг GPU-симуляции 60 Гц по `TickId` |
| `Report(Func<object?>)` | раздел итога `Sword.exe --json` под Id модуля |

Камера кадра (`CameraView`) несёт `Rotation` — поворот из осей вида (x — вправо, y — вверх, z — к зрителю) в мир, посчитанный игрой по углам и
дистанции камеры, без позиций: при тех же углах тот же бит в бит (площадка огня к камере повторяется по тикам); default — игра его не дала.

Кадр хоста: `BeginFrame(nextTick)` → тики (после каждого — `AfterTick`) → `Extract` → `AddLights` (источники модулей) → проходы игры →
`BuildFrame` (шаги GPU-симуляций, затем проходы модулей). Кадр из перетаскивания рамки — без тиков. Кадр модулей — 0 Б (`ModulesFrame_SteadyState_DoesNotAllocate`).

Итог `Sword.exe --json`: `report.modules` — включённые, затем выключенные модули (проходы, оверлеи, вкладки, GPU-симуляции, наблюдатели,
тиков, проходов последнего кадра); разделы `Report` — рядом, под Id модуля (у огня — `fire`: `fire.metrics`, `fire.light` и др.).

## GPU-симуляция

- `IGpuSimulation` — `Name`, `Create(GpuSimContext)` (один раз: рендер, `TickDt`, `Readback(storage, bytes)`, `Own`),
  `AddPasses(RenderFrame, in SimFrame)` — только фаза Simulation, `OnReadback(ReadOnlySpan<byte>)` — данные кадра, отправленного
  `GpuReadback.Latency` (3) кадра назад; только представление и диагностика.
- `SimFrame` — `FirstTick`, `Ticks` (0 — пауза, перетаскивание рамки), `LastTick`, `Dt`, `Contains(tick)`, `Requests<T>(tick)` — заявки
  тика по порядку, `Requests<T>()` — все. Шаг решает симуляция: 60 Гц — на чётных `TickId` (каждый второй тик при 120 Гц, с тика 0), пара
  тиков может лежать в разных кадрах.
- `SimRequests.Add<T>(tick, in T)` — из наблюдателя тиков, `TickId` не меньше прошлого, тип один на симуляцию; кадр начат — пусто.

Проверка: `tests/Sword.Engine.Client.Tests/ClientModulesTests` (наблюдатель при 0, 1, 2, 3, 8 тиках и паузе; шаг через кадр; выключенный
модуль; ошибки Id; 0 Б), `tests/Sword.Engine.Render.Tests/ReadbackGpuTests`, `tests/Sword.Game.Tests/FireModuleTests`.
