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
`entity.get`) → время (`time.step`, `time.runUntil`) → воздействие (`command.send`) → проверка состояния → `log.tail`.
Каждый ответ несёт квитанцию: `tickId` — сыграно тиков к моменту ответа, `frameId` — кадр живой игры.

## Рецепты

**Проверить движение игрока (Headless).**
1. `world.types {"name":"PlayerCommand"}` — поля команды.
2. `command.send {"command":"PlayerCommand","fields":{"Move":{"X":1,"Y":0}},"repeat":120}` — команда игрока действует
   один тик, поэтому `repeat`.
3. `time.step {"ticks":120}`; `entity.get {"entity":"player","components":["Transform","RootVelocity"]}` — ждём
   x ≈ 3,94, скорость 4,2 м/с (разгон `MovementRules`).

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
