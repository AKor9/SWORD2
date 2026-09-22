# Рецепты для агентов

Как ИИ-агенту (Claude Code, Codex, любой MCP-клиент) запускать, наблюдать и отлаживать SWORD. Устройство агентского
доступа — [engine/Sword.Engine.Agent/README.md](../engine/Sword.Engine.Agent/README.md), правила — раздел 8
[плана](../ARCHITECTURE_PLAN.md).

## Подключение

- **Claude Code** читает `.mcp.json` в корне: `sword-headless` (stdio, запускается сам: `tools/scripts/agent-headless.cmd`
  собирает Headless в новый каталог `artifacts/agent-bin/headless/<запуск>` и стартует `Sword.Headless.exe --agent`;
  сборка не удалась — сервер не стартует: исправить сборку и переподключить в `/mcp`) и `sword-app` (HTTP,
  `http://127.0.0.1:47813/mcp` — сначала запустить `Sword.exe --agent`, иначе после запуска переподключить в `/mcp`).
  Проверка — `/mcp`.
- **Codex** (`~/.codex/config.toml`):

  ```toml
  [mcp_servers.sword]
  command = "cmd"
  args = ["/c", "D:\\Work\\Visual Studio\\SWORD2\\tools\\scripts\\agent-headless.cmd"]
  ```

- **Любой клиент**: stdio — строка JSON-RPC на сообщение; HTTP — `POST /mcp` с сообщением, ответ `application/json`.

## Порядок

`session.status` → устройство (`ecs.plan`, `architecture.projects`, `module.graph`) → состояние (`world.query`,
`entity.get`, `state.hash`, `state.describe`) → время (`time.step`, `time.runUntil`, `time.seek`) → воздействие (`command.send`) → проверка состояния → `log.tail`; реплеи — `replay.*`, `session.start`.
Каждый ответ несёт квитанцию: `tickId` — сыграно тиков к моменту ответа, `frameId` — кадр живой игры.

## Рецепты

**Проверить движение игрока (Headless).**
1. `world.types {"name":"PlayerCommand"}` — поля команды.
2. `command.send {"command":"PlayerCommand","fields":{"Move":{"X":1,"Y":0}},"repeat":120}` — команда игрока действует
   один тик, поэтому `repeat`.
3. `time.step {"ticks":120}`; `entity.get {"entity":"player","components":["Transform","RootVelocity"]}` — ждём
   x ≈ 3,94, скорость 4,2 м/с (разгон `MovementRules`).

**Проверить анимацию бойца (этап 2).**
1. `entity.get {"entity":"player","components":["LegacyLocomotionState","AnimatorMotion"]}` — какой клип играет
   (номер), время клипа, доля кроссфейда; сглаженная скорость и «идёт».
2. `anim.clips {"index":N}` — имя клипа по номеру (или `{"filter":"Run"}` — клипы по подстроке ID): длительность, цикл,
   скорость корневого движения. Список — страницами по 50: дальше — `skip` из `continuationToken` квитанции.
3. `anim.pose {"entity":"player"}` — суставы (таз, голова, кисти, стопы) в пространстве персонажа и в мире; `pool` —
   живые позы и задания последнего тика (`staleLastTick` должен быть 0).
4. Побитовая сверка с SWORD — тест `ReferenceTraceTests` (`dotnet test tests/Sword.Game.Tests --filter ReferenceTrace`).

**Проверить детерминизм и найти расхождение (этап 3).**
1. `state.hash` — итог и хеш каждого участника (`ecs` — сущности, компоненты, ресурсы-значения, слоты, журналы, системы;
   `animation` — пул поз).
   Два прогона с одними командами обязаны совпасть тик в тик; разный хеш участника называет подсистему.
2. `state.describe {"part":"ecs","filter":"0v1"}` — компоненты сущности полями ровно так, как они хешируются
   (`filter` — сущность или часть имени компонента); `"filter":"slots"` — свободные номера, `"journal:"` — непрочитанные
   записи журналов, `"system:"` — включена ли система и что не прочла; `{"part":"animation","filter":"0v1:foot"}` —
   поза по суставам.
   Описания двух прогонов сравниваются поле в поле.
3. Без агента: `Sword.Headless --ticks N --move X,Y --json` — `stateHash` в итоге; `verify.ps1` гоняет его дважды.

**Воспроизвести реплей и найти расхождение (этап 3б).**
1. Сеанс пишется с тика 0 сам: `replay.save {"name":"my"}` — файл `.swreplay` в артефактах; `replay.status` — сколько
   записано. Живая игра: `Sword.exe --record FILE` (или `replay.save` при `--agent`).
2. `replay.check {"path":"my.swreplay"}` — реплей целиком в отдельной симуляции: `match`, `divergence.tick` (после
   скольких тиков), `lastMatch` (расхождение — в тиках lastMatch…tick−1), `parts` — участники. Без агента —
   `Sword.Headless --replay FILE --json` (код 3 при расхождении).
3. Встать перед расхождением: `session.start {"replay":"my.swreplay"}` → `time.runUntil {"tick":lastMatch}` →
   `state.describe` участника; `time.step {"ticks":1}` → снова `state.describe` — что изменилось не так.
   `time.seek {"tick":N}` — назад (пересборка и повтор, то же состояние) и вперёд. После перемотки назад прежнее
   будущее повторяется (свои команды его ветвят), а команды на ещё не сыгранные тики сброшены — поставить заново.
   Таймаут — ошибка с достигнутым тиком: сеанс стоит там, повторить `time.seek` или дать больше `timeoutSeconds`.
4. «Что если»: после `session.start` с реплеем `command.send` на тик T перебивает команду реплея — ветвление;
   `replay.diff {"a":"my.swreplay","b":"session"}` — первый тик с разными командами и первый разный хеш. Другой конфиг —
   `replay.check {"path":…,"set":["sim.seed=2"]}`.
5. Эталоны SWORD2 — `tests/reference/replays/*.swreplay` (тест `ReferenceReplaysTests`). Намеренное изменение поведения —
   перезапись: `Sword.Headless --replay F --record F`, причина — в md фичи.

**Наблюдать и остановиться по условию (этап 3в).** Условия — один JSON-язык (узлы — в описании `time.runUntil`):
`{"all":[{"entity":"player"},{"field":"Transform.Position.Y","lessThan":-10}]}`, `{"event":"PlayerCommand","where":{"field":"Walk","equals":true}}`,
`{"count":{"component":"Fighter"},"greaterThan":2}`, `{"for":30,"that":…}` (держится 30 тиков подряд).
1. «Когда это случилось»: `time.runUntil {"condition":…, "maxTicks":N}` — встаёт на паузу ровно на тике выполнения (и в
   живой игре); `met: false` — не выполнилось за N тиков.
2. «Как менялось»: `watch.add {"entities":["player"],"fields":["Transform.Position","RootVelocity"]}` до прогона →
   `entity.history {"entity":"player","changes":true}` — значения по `tickId`. Наблюдение без `entities` — все сущности
   под `where`.
3. «Что произошло»: `events.history {"types":["PlayerCommand"],"from":T}` — события, команды и `Added/Removed/Changed`
   последних 1200 тиков с тиком и стадией (видны журналы, у которых в мире есть потребитель).
4. «Что внутри тика»: `time.break {"stage":"Animation","condition":…}` (Headless) — стоим после барьера стадии
   (`receipt.stage`); `entity.get`, `state.describe`, `world.snapshot` видят мир на этом месте; `time.step 1` доигрывает тик.
5. «Что изменилось»: `world.snapshot` → шаги → `world.diff {"a":"s1","b":"now","filter":"player-id/Transform"}` — было →
   стало с разницей. Выборка и сводка: `world.query {"where":…, "aggregate":["Transform.Position.X"]}`.
6. Глубже истории — `replay.observe {"path":"my.swreplay","events":["*"],"watch":{…},"condition":…}`: прогон реплея в
   отдельной симуляции, строки — в `.jsonl` артефакта, сводка — первый и последний `tickId` условия.

**Инварианты: что должно держаться всегда (этап 3г).** `invariant.list` — что проверяется (id, чей, когда, стоимость) и
что уже нарушено (`recent`, `violationsTotal`). Нарушение — факт с тиком, стадией, сущностью и подробностями.
1. «Где сломалось»: `time.breakOnInvariant {"ids":["animation.owners"]}` (без `ids` — любой включённый) — Headless
   встаёт посреди тика после нарушившего барьера (`receipt.stage`); `entity.get`, `invariant.run`, `world.snapshot`
   видят мир там; `time.step 1` доигрывает тик.
2. «Своё правило»: `invariant.define {"id":"agent.onArena","must":{"field":"Transform.Position.X","between":[-40,40]},"where":{"component":"Fighter"}}`
   или `{"id":"agent.x","condition":…,"after":"Movement","every":10}` — дальше как с кодовым (`time.breakOnInvariant`,
   `events.history`); `remove: true` — убрать. Переживает `session.start` и перемотку.
3. «Проверить сейчас»: `invariant.run {"ids":["ecs.entityRefs"]}` — в том числе «по запросу» (висячие ссылки) и посреди
   тика; `invariant.enable {"ids":["ecs.entityRefs"],"enabled":true}` — проверять каждый тик.
4. «Было ли раньше»: `events.history {"types":["InvariantViolation"]}` — нарушения последних 1200 тиков; глубже —
   `replay.observe {"path":…, "events":["InvariantViolation"]}` (сводка `violations.first`).
Новый инвариант в коде — `IInvariant` у подсистемы или модуля и `SimulationBuilder.Invariant` (`AGENTS.md` Runtime).

**Принять фичу (этап 3д, раздел 12 плана).** Приёмка описана манифестом `feature.json` рядом с md фичи: сценарии
(реплей или команды по тикам) с проверками-условиями, инварианты, метрики с допуском и источником (`sword` — SWORD
2eab13b, `sword2` — эталон SWORD2), виды в App, бюджеты, чек-лист «что проверить в игре».
1. `feature.list` — фичи; `feature.list {"id":"animation"}` — манифест целиком.
2. `feature.verify {"id":"animation"}` — вердикт `ok` и `failures` словами; по разделам — сценарии (проверки, хеши
   реплея, нарушения), инварианты (проверок, нарушений), метрики (`value`, `expected`, `detail`; ряд против трассы SWORD —
   `firstMismatch`), бюджеты (медиана мс на тик, стадии, байты; время — только в Release), виды (PNG кадров
   `Sword.exe --replay` по записи сценария, хеши App сверены с Headless). Всё — в каталоге артефактов (`report.json`,
   `scenario-*.swreplay`, `view-*.png`). `views:false` — без App. Без агента — `Sword.Headless --feature ID|all [--json]`
   (код 4 — провал), `verify.ps1` шаг `features`.
3. Провал — не мнение: метрика, проверка или инвариант называют тик и значение; запись сценария —
   `session.start {"replay":"…/scenario-X.swreplay"}` и дальше как с любым реплеем. Ослабить манифест (допуск,
   инвариант) — только правкой файла с сообщением пользователю.
4. Новая фича — `feature.json` рядом с её md (тест `FeatureManifestsTests`: у каждой фичи есть манифест, чек-лист = пункты
   «Что проверить в игре» md, на эталонном паке он зелёный).

**Эксперимент «что если» (этап 3д).**
1. `experiment.fork {"replay":"my.swreplay","at":300,"variants":[{"name":"back","commands":[{"command":"PlayerCommand","fields":{"Move":{"X":-1,"Y":0}},"from":300,"to":301}]},{"name":"nolo","set":["modules.locomotion.enabled=false"]}],"measure":[{"entity":"player","field":"Transform.Position"}]}`
   — база и ветки параллельно; у ветки `vsBase.firstHashTick` — после скольких тиков состояние впервые другое (команда
   тика T — это T+1; ключ конфига — с тика 0, симуляцию собирает конфиг), `measure` в конце, `file` — запись ветки.
   `cut: true` — команды реплея после развилки не идут.
2. `experiment.compare {"a":<file базы>,"b":<file ветки>}` — чем разошлись: заголовки и конфиг по ключам, первый тик с
   разными командами, первый хеш с разными участниками, и `describe` — листья описаний участников (`путь`, `a`, `b`) на
   этом тике.
3. `experiment.sweep {"params":{"sim.tickRate":[60,120]},"replay":"my.swreplay","repeats":3}` — по комбинации: мс на тик
   (медиана, разброс), мс стадий, байты на тик, хеш итога и `deterministic` (повторы совпали); `scaling` — где время
   растёт резко быстрее значения ключа. Время — в Release (`tools/scripts/agent-headless.cmd` собирает Debug).

**Понять, почему поле такое.** `ecs.componentUsage {"name":"Transform"}` — кто пишет; `ecs.explainSystem` писателя —
его входы (`upstream`, с пометкой «в этом тике» или «с прошлого тика» и причиной `why`: команды той же стадии —
только в следующем тике) и место регистрации (`source`).

**Посмотреть на кадр живой игры.** `time.pause` (кадр показывает ровно последний тик) → `capture.bundle
{"name":"look"}` → открыть `color` и `depth` из ответа; `entities[].rect` — где на картинке кто. `render.graph` — какие
проходы нарисовали кадр.

**Длинный прогон.** `time.runUntil {"tick":N,"timeoutSeconds":120}` (в живой игре — не больше 30, предел в схеме); таймаут не отменяет прогон — симуляция идёт к
цели дальше, `time.pause` останавливает.

**Страницы.** `world.query` отдаёт не больше 100 сущностей: `receipt.truncated` и `continuationToken` (`skip=N`) —
следующая страница. `log.tail` без `after` — последние записи, прошедшие фильтр (`level`, `category`), по всему
кольцу; дальше — с `after` = `last` из ответа (новые записи, страницами).

## Если что-то не так

- «симуляция не дошла до точки обслуживания» — кадр живой игры завис (или процесс Headless остановлен); запрос при
  этом не исполнен — его можно повторить. Свёрнутое окно и перетаскивание рамки обслуживаются, но в свёрнутом окне тики
  не идут: `time.step` ждёт, пока окно развернут.
- Неизвестный аргумент — ошибка (опечатка не становится значением по умолчанию); список аргументов — в ошибке.
  Незаданный аргумент пропускается, а не передаётся `null`.
- Живая игра перезапущена — прежний сеанс незнаком (404): клиент начинает сеанс заново; если нет — переподключить
  `sword-app` в `/mcp`.
- Живая игра по HTTP: вызов — не дольше 35 с. Долгое ожидание — `time.runUntil` с `timeoutSeconds` до 30 и повтором
  (цель остаётся, симуляция идёт к ней).
- Кадр живой игры на ходу интерполирован (`alpha` < 1 в пакете наблюдения): для точного кадра — `time.pause`.
- «тик уже прошёл» — команды только на будущий тик; без `tick` команда идёт на ближайший.
- Имя компонента неоднозначно — полное имя из `world.types` (`stableName`).
- Ошибки движка — `log.tail {"level":"warning"}`.
