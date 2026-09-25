# Sword.Engine.Agent — правила

Агентский хост (раздел 8 плана): сеанс, реестр инструментов, квитанции, артефакты, MCP (свой минимальный JSON-RPC),
транспорты stdio и HTTP, цикл Headless под агентом (`SimulationLoop`). Устройство и инструменты — `README.md`,
рецепты — `docs/agent-playbook.md`.

- Инструмент — имя `группа.действие`, схема аргументов, аннотации (только чтение / меняет, идемпотентность), класс
  стоимости. Исполнитель — на потоке агента; к миру — только `host.Serve(...)` (точка обслуживания), изменения — только
  командами на будущий тик.
- Ответ — `ToolOutput` с данными и тем, что идёт в квитанцию (тик, кадр, полнота, продолжение, артефакт). Большое —
  страницами и файлами в `artifacts/agent/<сеанс>/`, не в ответ.
- Ошибка вызова (аргументы, имя, таймаут) — `ArgumentException`/`TimeoutException`… → результат с `isError`; ошибка
  протокола — ошибка JSON-RPC.
- Новый инструмент — только если существующие не покрывают задачу (раздел 8); у него тест и строка в
  `docs/agent-playbook.md` и `README.md`.
- stdout Headless под агентом — только протокол; журнал — в stderr и кольцо `log.tail`.
- Время жизни сервера (точка 6а). Клиент MCP держит stdio-сервер, пока открыт его сеанс (настольные Claude Code и Codex —
  часами, по серверу на сеанс или поток), поэтому Headless `--agent` выходит сам: конец stdin (транспорт не ждёт вызов,
  не ответивший на отмену, дольше `EofGrace + CancelGrace`), конец родителя (`ProcessLifetime.WhenParentExited` — клиент
  мог снять только обёртку `cmd`), простой без вызовов инструментов (`McpServer.IdleFor`, `--idle-exit`, 15 мин),
  `session.stop`; не вышел за 20 с — сторож. Не добавлять кода, от которого процесс не выходит: передних потоков,
  ожиданий без отмены на потоке симуляции. Инструмент, запускающий процесс, кладёт его в `KillOnCloseJob.Shared`
  (иначе снятый Headless оставит сироту); фоновый запуск SWORD в `verify.ps1` — через `Add-ToJob`, шаг `processes`
  проверяет, что после прогона никого не осталось. Тесты — `LifetimeTests`, `agent-smoke` (конец stdin, снятая обёртка,
  простой).
- Симуляция сеанса Headless — `AgentSimulation` (этап 3б): запись с тика 0, смену симуляции (`session.start`,
  `time.seek` назад) исполняет только поток симуляции в `SimulationLoop` между точками обслуживания (`Post`); после
  смены цикл обновляет контекст обслуживания, `AgentHost.Catalog` и время (`TimeControl.Reset`). Живая игра
  (`host.Simulation` = null) пересборки не умеет — такие инструменты отвечают ошибкой. Транспорт Headless запускается
  в `started` у `SimulationLoop.Run`, когда сеанс уже подключён: иначе первый вызов после `initialize` мог застать
  `host.Simulation` = null и получить ошибку «живая игра» (тест `FeatureToolsTests.SessionAttachedBeforeTransportStarts`).
- Наблюдение (этап 3в) — `AgentHost.Observation` (`ObservationHub`): подключает хозяин цикла (`host.Observe`) —
  `SimulationLoop` к каждой симуляции сеанса до её первого тика (`AgentSimulation.Preparing`: и повтор перемотки назад
  попадает в историю), живая игра — exe при старте. Останов посреди тика (`time.break`) — точка обслуживания внутри
  тика в `SimulationLoop`: ждёт, пока время разрешит тики или сеанс поставит операцию (тик доигрывается). Инструменты —
  `ObservationTools`, `replay.observe` — в `ReplayTools`.
- Инварианты (этап 3г) — `InvariantTools`: реестр и проверка — в Runtime (`ServiceContext.Invariants` в точке
  обслуживания), свои инварианты агента, включения и остановка на нарушении — через `AgentHost.Observation` (переносит
  их в каждую новую симуляцию сеанса). Ожидание остановки — общее с `runUntil`/`break` (`ObservationTools.Await`).
- Приёмка и эксперименты (этап 3д) — `FeatureTools` (`feature.list`, `feature.verify`) и `ExperimentTools`
  (`experiment.fork/compare/sweep`): логика — в Introspection (`FeatureVerifier`, `Experiments`), здесь — аргументы,
  артефакты и отдельные симуляции построителем сеанса (`AgentSimulation.Build`, как `replay.check`; сеанс не
  трогается). Где манифесты и чем снимать виды, задаёт exe игры — `AgentHost.Features` (`FeatureSetup`); движок путей
  игры не знает.
- Профайлер CPU (этап П, точка Па) — `ProfileTools` (`profile.stages`) над `AgentHost.Profiler`: у Headless — свой,
  `SimulationLoop` подключает его к каждой симуляции сеанса (и в `Preparing` — до первого тика новой); у живой игры exe
  отдаёт профайлер клиента (`host.Profiler = client.Profiler`). Профайлер однопоточный: включение, сброс и итоги — только
  в точке обслуживания; включение — диагностика, не команда симуляции (в реплей не пишется). Точка Пб: ответ несёт
  `runtime` (среда за период), `machine` (машина с включения или сброса, вердикт); `events: true` — диагностический режим
  (`CpuProfiler.SetEvents`), ответ ждёт хвост событий (до 1 с, в точке обслуживания) и несёт `events`. `experiment.sweep` —
  `rows[].machine`. Точка Пв: ответ несёт `anomalies` (порог, счёт, последние — класс, главная зона, файл; файлы пишет поток
  писателя профайлера, свежие — `state: queued`), аргументы `anomalies` (сколько), `threshold` (абсолютный порог; захват не
  настроен exe — настраивается в артефакты сеанса), `trace` (кольцо в Chrome Trace в артефакты сеанса). Отдельного
  `profile.anomalies` нет — расширен `profile.stages` (раздел 8). Захват сеанса Headless настраивает exe (каталог прогона
  профайлера), App — профайлер клиента, настроенный exe. Останов посреди тика (`time.break`) зовёт `CpuProfiler.Interrupt()`:
  тик с ожиданием агента — служебный, не аномалия и не в итогах. Точка Пг: ответ `profile.stages` несёт `gpu` (секция
  `frames.gpu` отчёта: GPU-время кадра и проходов за окно, отставание) — у App; нового инструмента нет.

- Причинность (6д) — `CauseTools` (`state.why`) над `CausalityQuery` (Introspection); режим — `AgentSimulation.Causality`
  (`--causality`, `session.start {causality}`: новая симуляция сеанса и перемотка назад включают журнал до первого тика),
  `session.status` — `causality`; `events.history` — `cause` записи (номер, родитель, вид, система, источник), только в режиме.
  Лимиты: цепочка — до 64 шагов (по умолчанию 16), изменений — до 20 (по умолчанию 1). Тест — `Sword.Game.Tests/CausalityTests`
  (`Agent_StateWhy_AndEventsHistoryCauses`: нужен бой игры). 8з: `state.why {"cause":N}` (вместо `entity` и `field`) — цепочка записи
  журнала по номеру из `events.history` (`cause.id`, `cause.parent`) до корня — `CausalityQuery.WhyCause`; тест —
  `MotionMatchAgentTests.Agent_AnimPose_EventsWithCauses_Fields_Describe` (переход MM ← решение «MM: …»).
- Диагностические режимы симуляции (7и, `Simulation.Diagnostics`: трасса физики игры) включают инструменты из точки обслуживания
  (`ServiceContext.SetDiagnostic`; у игры — `physics.bodies {trace}`); `AgentSimulation` переносит включённые режимы в новую симуляцию
  сеанса (`session.start`, перемотка назад) до её первого тика — как причинность (`DiagnosticsCarryTests`). Отдельного инструмента
  режимов нет (режим один); инструмент, который переключает режим, — с аннотацией «меняет» (`ReadOnly = false`, как `profile.stages`).

Проверка: `tests/Sword.Engine.Agent.Tests`, `verify.ps1` шаг `agent-smoke`.
