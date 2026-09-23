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
  в точке обслуживания; включение — диагностика, не команда симуляции (в реплей не пишется).

Проверка: `tests/Sword.Engine.Agent.Tests`, `verify.ps1` шаг `agent-smoke`.
