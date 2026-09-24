# Рецепты для агентов

Как ИИ-агенту (Claude Code, Codex, любой MCP-клиент) запускать, наблюдать и отлаживать SWORD. Устройство агентского
доступа — [engine/Sword.Engine.Agent/README.md](../engine/Sword.Engine.Agent/README.md), правила — раздел 8
[плана](../ARCHITECTURE_PLAN.md).

## Подключение

- **Claude Code** читает `.mcp.json` в корне: `sword-headless` (stdio, запускается сам: `tools/scripts/agent-headless.cmd`
  собирает Headless в новый каталог `artifacts/agent-bin/headless/<запуск>` и стартует `Sword.Headless.exe --agent`;
  сборка не удалась — сервер не стартует: исправить сборку и переподключить в `/mcp`) и `sword-app` (HTTP,
  `http://127.0.0.1:47813/mcp` — сначала запустить `Sword.exe --agent`, иначе после запуска переподключить в `/mcp`).
  Проверка — `/mcp`. `sword-headless` выходит сам после 15 мин без вызовов (`--idle-exit`, точка 6а): следующий вызов
  Claude Code исполняет на новом сервере (сборка ~5–8 с, сеанс с тика 0).
- **Окна `Sword.exe` агентов — без фокуса.** Пользователь работает на той же машине: окно прогона не должно забирать
  фокус и вставать поверх его приложений. В настройках Claude Code проекта (`.claude/settings.local.json`, вне git) задано
  `"env": {"SWORD_NO_ACTIVATE": "1"}` — все запуски `Sword.exe` из сеансов Claude Code идут как с `--no-activate` (окно
  внизу порядка перекрытия, в том числе с `--fullscreen`/`--borderless`); в журнале — строка
  `окно без фокуса: SWORD_NO_ACTIVATE=1`, в итоге `--json` — `window.activate: false`, `window.activateBy`. Вне Claude Code
  (Codex, свой терминал) переменной нет — запускать с `--windowed WxH --no-activate` (прогоны замеров —
  `--windowed 640x480`). `--activate` возвращает фокус и при заданной переменной; ручной запуск пользователя — как раньше.
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
   живые позы и задания последнего тика (`staleLastTick` должен быть 0); у врага — ещё `action` (слой действий, 4г).
4. Побитовая сверка с SWORD — тест `ReferenceTraceTests` (`dotnet test tests/Sword.Game.Tests --filter ReferenceTrace`).

**Враги и команды арены (этап 4, точка 4а).**
1. Headless со `sword.json` стартует с 10 врагами (`arena.enemies`; `--units N` или `--set arena.enemies=N` — другое
   число, 0…32; тесты и сценарии фич без `sword.json` — 0). Сколько задано — ресурс `ArenaState.Enemies`
   (`state.describe {"part":"ecs","filter":"resource:"}`); враги —
   `world.query {"with":["EnemyTag"],"fields":["FighterSlot","Transform"]}`; враг по месту — `entity.get {"entity":"enemy 3"}`.
2. PageUp/PageDown/R агентом: `command.send {"command":"ArenaCommand","fields":{"Action":"ChangeEnemies","Enemies":1}}`
   (`-1` — PageDown), `{"Action":"SetEnemies","Enemies":32}`, `{"Action":"Reset"}`; затем `time.step {"ticks":1}` —
   изменения видны после тика (барьер Intent). Команда арены записывается в реплей.
3. Места без дыр и число бойцов держит инвариант `arena.slots` (`invariant.list`, `time.breakOnInvariant
   {"ids":["arena.slots"]}`).
4. Сброс строит бойцов заново на тех же сущностях: `player` и `enemy N` в условиях и `watch.add` его переживают. После
   PageDown и PageUp того же места враг — новая сущность: имя в условии разрешается один раз, поэтому место — полем:
   `{"exists":{"all":[{"component":"EnemyTag"},{"field":"FighterSlot.Index","equals":5},{"field":"Transform.Position.X","greaterThan":0}]}}`.
5. Без агента: `Sword.Headless --ticks N --units 32 --json` — `arena` в итоге (задано, бойцов, враги по местам с
   расстоянием до игрока, скоростью и составляющими ИИ); приёмка — `Sword.Headless --feature enemies` /
   `feature.verify {"id":"enemies"}`.

**ИИ врагов: почему враг идёт туда, куда идёт (точка 4б).**
1. `entity.get {"entity":"enemy 3","components":["EnemyAiTerms","FighterCommand","GazeTarget"]}` — составляющие
   намерения этого тика: `Distance` (до игрока, м), `ApproachSpeed` (к игроку, м/с: > 0 — подходит, < 0 — отступает;
   ноль на 1,8 м, предел −2…3,2), `SwaySpeed` (вбок, м/с, ±0,7), `Separation` (толчок соседей ближе 1,4 м) и
   `Neighbours` (сколько их); `FighterCommand` — итог (желаемая скорость, курс-цель), `GazeTarget` — корень игрока.
2. Как менялось: `watch.add {"entities":["enemy 3"],"fields":["EnemyAiTerms","FighterCommand","Transform"]}` →
   `time.step` → `entity.history {"entity":"enemy 3","changes":true}`.
3. Когда подошли все: `time.runUntil {"condition":{"none":{"all":[{"component":"EnemyTag"},{"field":"EnemyAiTerms.Distance","greaterThan":2.5}]}},"maxTicks":1200}`;
   сводка по толпе — `world.query {"with":["EnemyTag"],"aggregate":["EnemyAiTerms.Distance","EnemyAiTerms.Neighbours"],"take":0}`.
4. Намерение считается по корням до движения тика: посреди тика — `time.break {"stage":"Intent"}` и `entity.get` врагов
   (корни ещё прошлого тика, намерение — уже этого). В тике команды арены враги думают по арене после команды (новые
   враги, центр после R). Время арены — `{"resource":"SimTime.Time"}`.
5. Поворот врага — не как у игрока (точка 4г, рецепт ниже). Сверка ИИ с SWORD побитово — тест `EnemyAiTests`
   (`dotnet test tests/Sword.Game.Tests --filter EnemyAi`).

**Столкновения бойцов: кто кого толкнул и где перекрытие (точка 4в).**
1. Толчки тика у врага — `entity.get {"entity":"enemy 3","components":["RootContacts","Transform","RootVelocity"]}`:
   `Contacts` (сколько раз за оба прохода разрешалась пара с ним), `Push` (сдвиг корня парами, м), `FromPlayer` (часть
   сдвига от пары с игроком), `Deepest` (наибольшее перекрытие пары до разрешения, м) и `DeepestWith` (с кем: место,
   0 — игрок, −1 — касаний не было), `Obstacle` (4д: сдвиг корня колоннами и краем арены; `Push + Obstacle` — весь сдвиг
   столкновениями; «во что упёрся» — `{"field":"RootContacts.Obstacle.X","notEquals":0}`). У игрока вида нет: его толчок парами —
   `−Σ FromPlayer`: `world.query {"with":["EnemyTag"],"aggregate":["RootContacts.FromPlayer.X","RootContacts.FromPlayer.Z"],"take":0}`
   (`sum`); упёрся ли игрок — `RootVelocity` против `FighterCommand.DesiredVelocity` (`entity.get player`).
2. «Когда игрок толкнул врага»: `time.runUntil {"condition":{"exists":{"all":[{"component":"EnemyTag"},{"field":"RootContacts.DeepestWith","equals":0}]}},"maxTicks":600}`;
   кого — `world.query {"with":["EnemyTag"],"where":{"field":"RootContacts.DeepestWith","equals":0},"fields":["RootContacts","FighterSlot"]}`.
   Как менялось — `watch.add {"entities":["enemy 3"],"fields":["RootContacts","Transform"]}` → `entity.history`.
3. Перекрытие корней держит инвариант `movement.rootOverlap` (после Movement): ближе 0,35 м — дольше 4 тиков подряд,
   перекрытие больше 0,02 м — дольше 24 тиков подряд (после спауна, особенно у края арены, кучка врагов разводится
   несколько тиков — это не ошибка). Встать на нарушении — `time.breakOnInvariant {"ids":["movement.rootOverlap"]}` и
   `time.runUntil`; подробности (обе сущности, расстояние, перекрытие, какое правило) — `events.history
   {"types":["InvariantViolation"]}` и `invariant.list`; проверить сейчас — `invariant.run {"ids":["movement.rootOverlap"]}`
   (счёт тиков подряд не сдвигает). Почему такой допуск — замер в `game/Sword.Game/Features/Enemies/README.md`.
4. Посреди тика: `time.break {"stage":"Movement"}` — корни уже разведены; `{"stage":"Animation"}` — ещё до переноса и
   столкновений. Побитово с SWORD — тест `RootCollisionTests` (`dotnet test tests/Sword.Game.Tests --filter RootCollision`).
5. Без агента: `Sword.Headless --ticks N --units 32 --move 1,0 --json` — `arena.closestPair` (ближайшая пара корней:
   расстояние и места; ближе 0,7 м — перекрытие) и у мест `contacts`, `push`, `fromPlayer`, `deepest`, `deepestWith`,
   `obstacle`; приёмка — сценарий `push` манифеста `enemies` (толпа у колонны — `sword-crowd`, проверка `pillar-pushes`).

**Толпа против SWORD: где разошлась (точка 4д).**
1. Трасса толпы SWORD — `tests/reference/anim-crowd.trace.json` (настоящий `FighterAnimator`, копии `EnemyAi.Think` и
   `ResolveRoots`, команды арены по ходу, до 33 бойцов). Вся толпа — `dotnet test tests/Sword.Game.Tests --filter
   Crowd_MatchesSwordTickByTick`: сообщение называет тик, место и что разошлось (клип, действие, корень, курс, поза).
   Через агента — `feature.verify {"id":"enemies","views":false,"budgets":false}`: метрики `sword-crowd-player`,
   `sword-crowd-enemy-1`, `sword-crowd-enemy-12` — `firstMismatch` (тик, ключ `x[место]`/`z[…]`/`facing[…]`, SWORD и SWORD2).
2. Посмотреть бойца на тике расхождения: `session.start {"replay":"<артефакты>/scenario-sword-crowd.swreplay"}`,
   `time.runUntil {"tick":T-1}`, `watch.add {"entities":["enemy 5"],"fields":["Transform","EnemyAiTerms","RootContacts","LegacyLocomotionState"]}`,
   `time.step`, `entity.history`; число в трассе — `ticks[T-1]` (массивы по местам 0…EnemyCount). Пересобрать трассу — README
   `tests/reference/sword-trace` (вход — `artifacts/reference/crowd.input.json`, его пишет тот же тест).
3. Сценарий и расстановка трассы — те же, что у теста: изменили сценарий (`ReferenceTraceTests.CrowdSegments`) или спаун —
   тест скажет «снять трассу заново», и сценарий `sword-crowd` манифеста надо поправить теми же командами.

**Поворот врага на месте клипом (точка 4г).**
1. Враг (не-`Agile`) поворачивает на ходу 12 рад/с, стоя — 2,5 рад/с, а отстал стоя больше 45° — клипом (90° влево,
   вправо, разворот). ИИ почти никогда не даёт стоячему врагу отстать на 45° — поворот клипом вызывают командой врагу:
   `command.send {"command":"EnemyCommand","fields":{"Slot":1,"FacingGoal":3.9},"repeat":150}` (намерение врага места на
   тик вместо ИИ: `Velocity` — желаемая скорость x, z, м/с, без неё — стоять; `FacingGoal` — курс-цель, рад; курс врага
   сейчас — `entity.get {"entity":"enemy 1","components":["Transform"]}`). Команда пишется в реплей.
2. Идёт ли поворот — `entity.get {"entity":"enemy 1","components":["AnimatorAction","AnimatorMotion"]}`: `Kind`
   (`None`/`Turn`), `Clip`, `Time`/`Duration`, `Weight` (вход 0,15 с, выход 0,2 с), `TurnScale` (растяжение yaw клипа),
   `YawDelta` (поворот курса клипом за тик — пока `AnimatorMotion.Moving` false), `RootDelta`; с именем клипа —
   `anim.pose {"entity":"enemy 1","joints":["pelvis"]}` → `action`. Встать на начале:
   `time.runUntil {"condition":{"all":[{"entity":"enemy 1"},{"field":"AnimatorAction.Kind","equals":"Turn"}]},"maxTicks":120}`;
   по тикам — `watch.add {"entities":["enemy 1"],"fields":["AnimatorAction","Transform"]}` → `entity.history`.
3. Слой действий держит инвариант `animation.actions` (после End): у врага слой есть, вес 0…1, клип ⇔ действие, клип
   поворота с растяжением 0,5…1,6, поза слоя своя. Поза слоя — `ActionPoses.Layer` → `state.describe {"part":"animation"}`.
4. Порядок `KinematicSteer` → `anim.Tick` → `Facing += YawDelta` → перенос: `core.root-steer` (начало поворота) →
   `core.animator-input` (шаг действия, `YawDelta`) → `core.animator-pose` (клип поверх локомоции) → `core.root-control`
   (курс и корень); посреди тика — `time.break {"stage":"Gameplay"}`. Побитово с SWORD — `dotnet test tests/Sword.Game.Tests
   --filter EnemyTurn` (трасса `anim-turn.trace.json`) и метрика `sword-turn-trace` сценария `turn` манифеста `enemies`.
5. Без агента: `Sword.Headless --units N --json` — у мест `action`, `actionWeight`, `yawDelta`; посмотреть глазами —
   `Sword.exe --replay artifacts/features/enemies/scenario-turn.swreplay` (запись сценария `turn` после `feature.verify`).

**Тела бойцов (этап 5: 5а — тела, 5б — капсулы по позе, 5в — на экране, 5г — приёмка).** Тело бойца — сочленение из 13
кинематических капсул в подсистеме Physics; у бойца в ECS — хэндл `BodyRef`. Капсулы стоят на суставах позы тика (PrePhysics)
с формой постройки; на экране — F7 и оверлей `capture.bundle` (рецепт «ID-буфер и оверлей капсул»). Тела бойцов сборки мира
стоят уже до первого тика — по позе постройки (5в: их ставит сборка мира, тик 1 ставит их ещё раз теми же значениями).
1. Тело бойца по сегментам: `physics.bodies {"entity":"enemy 3"}` — 13 капсул с именами (`forearm.l` — «левое предплечье»),
   центр, концы оси A и B, поворот, скорость и её модуль, угловая скорость, прошлая постановка, радиус, полудлина; `fastest` —
   самая быстрая капсула; `"segments":["foot.l","foot.r"]` — только эти. Слоты, поколения и хэндлы тел — `state.describe
   {"part":"physics","filter":"3v1"}` (сочленение `Index`v`Generation` из `BodyRef`; тело `i` — сегмент `BodySegs.Segment(i)`:
   0…10 — pelvis…shin.r, 11 — foot.l, 12 — foot.r); свободные слоты — `{"part":"physics","filter":"slots"}`. Хеш тел — участник
   `physics` в `state.hash`.
2. Кто заводит и освобождает тело: `ecs.componentUsage {"name":"BodyRef"}` — `hooks` (OnAdd и OnRemove ядра). Кто ставит:
   `ecs.explainSystem {"name":"core.body-setup"}` (Gameplay, по `Added<Fighter>`: форма и постановка без скорости по позе
   постройки — ресурс `StartPose` в `state.describe {"part":"ecs"}`), `core.kinematic-placement` (PrePhysics: капсулы по позе
   тика, скорость — разность за тик).
3. Что держится всегда: `physics.kinematicPose` (после PrePhysics: центр капсулы — середина её суставов в мире бойца, ось —
   вдоль них; ловит пропущенную постановку и тело, сдвинутое после неё), `physics.kinematicSpeed` (после PrePhysics: скорость
   капсулы относительно бойца — до 30 м/с, угловая — до 150 рад/с; ловит постройку без постановки и обменявшиеся тела),
   `physics.bodyRefs` (после End: у каждого бойца тело из 13 капсул, ровно один владелец, ничьих нет), `physics.finite`
   (после Physics). Встать на нарушении — `time.breakOnInvariant {"ids":["physics.kinematicSpeed"]}`, затем `physics.bodies`.
   `invariant.run` посреди тика до PrePhysics (`time.break` на Begin…Movement) `physics.kinematicPose` не сверяет: постановки
   этого тика ещё не было; до первого тика — сверяет с позой постройки (5в).
4. Числа тела в метриках манифеста (`feature.verify`) — вычисляемые поля `Body.<сегмент>.<поле>[.X|.Y|.Z|.W]` (`Position`,
   `Rotation`, `PrevPosition`, `Velocity`, `AngularVelocity`, `EndA`, `EndB`, `Radius`, `HalfLength`) и `Body.MaxSpeed`,
   `Body.MaxAngularSpeed` (Game.Agent `BodyFields`; компонента — только `X`, `Y`, `Z`, `W`, опечатка — ошибка). С 5г те же поля —
   в наблюдениях: история капсул по тикам — `watch.add {"entities":["player"],"fields":["Body.MaxSpeed","Body.foot.l.Position.Y"]}`,
   `time.step`, `entity.history` (лист F64, нет тела — null); по записи сценария глубже кольца —
   `replay.observe {"path":"<артефакты фичи>/scenario-terrain.swreplay","watch":{"entities":["player"],"fields":["Body.MaxSpeed"]}}`
   → `.jsonl` (так найдены окна метрик 5г: скачки на кромках ступеней, наибольшая угловая скорость на развороте). В условиях
   проверок (`time.runUntil`, проверки манифеста) их нет — там только компоненты; остановиться на скачке капсулы — инвариант
   `physics.kinematicSpeed` или `invariant.define` по компонентам.
5. Сверка с SWORD: трасса толпы (`tests/reference/anim-crowd.trace.json`, README трассы) — хеш 13 капсул каждого бойца
   каждого тика; при расхождении `ReferenceTraceTests.Crowd_MatchesSwordTickByTick` называет тик и место, число капсулы —
   по `samples` (тики выборки). Метрики `sword-crowd-pelvis-*` манифеста `bodies` — таз игрока и врагов 1 и 12. Рельеф (5г) —
   трасса `tests/reference/anim-terrain.trace.json` (склон, ступени вверх и вниз, ухабы; та же программа, свой вход),
   `ReferenceTraceTests.Terrain_MatchesSwordTickByTick` и метрика `sword-terrain-pelvis` сценария `terrain`.
6. Команды арены: PageDown — тело ушедшего освобождено, PageUp — новое (тот же слот, новое поколение), R — те же тела на тех
   же сущностях, форма и постановка — заново. Приёмка — `feature.verify {"id":"bodies"}` (`Sword.Headless --feature bodies`):
   сценарии `arena`, `terrain`, `crowd`, `turn`, `reference`; бюджеты — тик и PrePhysics с 32 врагами, кадр App без капсул и с
   капсулами (`budgets[].frame.overlays`).

**Проверить детерминизм и найти расхождение (этап 3).**
1. `state.hash` — итог и хеш каждого участника (`ecs` — сущности, компоненты, ресурсы-значения, слоты, журналы, системы;
   `animation` — пул поз; `physics` — тела и сочленения, с 5а).
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
5. **Живая игра — тяжёлые раз в 4 тика** (исправления после Пд, решение пользователя): в Sword.exe (кроме `--replay`)
   `animation.finite`, `ecs.finite`, `physics.finite`, `physics.kinematicPose`, `physics.kinematicSpeed` проверяются на тиках,
   кратных 4 (`invariant.list`: `livePeriods: true`, у них `every: 4`, у остальных — 1); нарушение, которое держится, видно не
   позже чем через 3 тика (тик в записи — тик проверки), нарушение короче 4 тиков может не попасть в проверку. Каждый тик: в
   живой игре — `time.breakOnInvariant` (на время ожидания периоды сняты: встаёт на тике нарушения, потом — снова раз в 4);
   точно и всегда — Headless (`sword-headless`, `Sword.Headless --replay` записи живой игры: `replay.save` → `replay.observe`
   или `session.start` с реплеем и `time.breakOnInvariant`): там `livePeriods: false`, все `every: 1`, как в тестах и `feature.verify`.
Новый инвариант в коде — `IInvariant` у подсистемы или модуля и `SimulationBuilder.Invariant` (`AGENTS.md` Runtime); тяжёлый (медиана
в тике толпы от ~2 % тика, без счёта тиков подряд) — `InvariantCost.Heavy`.

**Принять фичу (этап 3д, раздел 12 плана).** Приёмка описана манифестом `feature.json` рядом с md фичи: сценарии
(реплей или команды по тикам) с проверками-условиями, инварианты, метрики с допуском и источником (`sword` — SWORD
2eab13b, `sword2` — эталон SWORD2), виды в App, бюджеты, чек-лист «что проверить в игре».
1. `feature.list` — фичи; `feature.list {"id":"animation"}` — манифест целиком.
2. `feature.verify {"id":"animation"}` — вердикт `ok` и `failures` словами; по разделам — сценарии (проверки, хеши
   реплея, нарушения), инварианты (проверок, нарушений), метрики (`value`, `expected`, `detail`; ряд против трассы SWORD —
   `firstMismatch`), бюджеты (медиана мс на тик, стадии, байты; кадр App — `frame`: средний и p99 на записи сценария,
   хеши App сверены; `frame.overlays` — оверлеи игры на весь замер, например кадр с капсулами тел, 5г; `detail` называет
   проходы последнего кадра; время и кадр проверяются только в Release, без видов кадр не меряется), виды (PNG кадров
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
   растёт резко быстрее значения ключа. Время — в Release (`tools/scripts/agent-headless.cmd` собирает Debug). Толпа:
   `{"params":{"arena.enemies":[0,8,16,32]},"replay":"<артефакты enemies>/scenario-crowd.swreplay","repeats":7}` (таблица
   этапа 4 — README фичи `enemies`; с телами и стадией PrePhysics, с инвариантами и без —
   `{"params":{"arena.enemies":[0,8,16,32],"sim.invariants":[true,false]},"replay":"<артефакты bodies>/scenario-crowd.swreplay","repeats":7}`,
   таблица 5г — README фичи `bodies`). Перебор сначала прогоняет каждую комбинацию по разу без замера, затем повторы по
   кругу комбинаций: без этого первые комбинации свежего процесса мерились на неоптимизированном JIT (в 3–15 раз
   медленнее, 4д); `msPerTick.max` (первый круг повторов) ещё бывает выше — смотреть медиану. Сеанс агента меряет с
   инвариантами (`sim.invariants=false` в `params` — как бюджет манифеста). `rows[].systems` — мс систем на тик из
   отдельного прогона с профайлером CPU после повторов (этап П; `systems: 0` — без него, `systems: N` — сколько в строке).
   `rows[].machine` — машина за замеры строки и вердикт (`код`/`среда`/`машина`, рецепт «Код или машина» ниже).

**Где время кадра или тика (профайлер CPU, этап П).** Профайлер выключен по умолчанию; включённый не меняет симуляцию
(хеши те же) и не выделяет памяти.
1. `profile.stages {"enable":true,"reset":true}` — включить и забыть прошлое (App — со следующего кадра: пока не
   вступило, в ответе `note`; Headless на паузе — сразу).
2. Дать поработать: Headless — `time.step {"ticks":600}` (или сценарий `session.start {"replay":…}` и `time.step`); App —
   секунды игры (или `time.step` на паузе). Первые сотни тиков свежего процесса — неоптимизированный JIT: после них ещё
   раз `reset` (или смотреть медиану, а не среднее).
3. `profile.stages {"top":30}` — зоны за кадр (App) или тик (Headless; `per` — выбрать): `meanMs`/`medianMs`/`p99Ms`/
   `maxMs`, `selfMs` — без дочерних, `bytes` — выделено потоком за период, `calls`, `presence` — доля периодов с зоной,
   `parent` — под какой зоной. Имена: `frame.*` — фазы кадра (`frame.simulate` — тики кадра, `frame.render` —
   `frame.fenceWait` ожидание GPU, `frame.build` игра строит кадр, `frame.record` запись графа, `frame.present`), `tick`,
   `stage.<стадия>`, `<метка>@<стадия>` — система (как в `ecs.plan`), `barrier.<стадия>`, `invariant.<id>`, `sim.record`
   (реплей и хеш), `sim.observers` (агент), `render.<проход>`, `arena.*` — игра. Узко — `prefix` (`stage.`, `invariant.`,
   `render.`); по кадрам — `history` (кадр: `ticks: [первый, сколько]`, `alpha`, `ms`, `bytes`, `service`).
4. Всё в файл — `profile.stages {"write":true}` → `file` (`profile.json`, формат `sword-profile`). Выключить — `enable:false`.
5. Без агента: `Sword.exe --profile` / `Sword.Headless --ticks 600 --units 32 --profile` → `artifacts/profile/<прогон>/profile.json`,
   в `--json` — секция `profile`. Бюджеты фич: `feature.verify` → `budgets[].systems` (мс систем, прогон с профайлером),
   `budgets[].profiled` (его мс и байты; медиана бюджета — без профайлера).
6. Байты: `bytes` зоны — выделения потока владельца между её началом и концом (байты дочерних входят в родителя). Где кадр
   выделяет — спускаться от `frame` по зонам с `bytes` > 0, пока байты не перестанут делиться между дочерними. Чужие потоки
   зоны не видят (выделения процесса — `runtime.processBytes`, типы — диагностический режим ниже); то, что между кадрами
   (после `frame` и до следующего), — тоже: время кадра по часам (`report.bench`) больше `frame` — ищите там (пример —
   компиляция OSR главного цикла, README фичи `profiler`, «Первый взгляд»).
7. Среда (точка Пб): `runtime` ответа — за период: `cpuMs` против `wallMs` (`onCpuPct` — главный поток на CPU; у кадра App
   ниже 100 % из-за ожидания GPU, у тика — нет), `gc` (сборки по поколениям, `periods` — в скольких периодах, пауза),
   `jit` (`mainThread` — компиляции на главном потоке: пауза кадра; `methods` — процесса, фоновый tier1), `processBytes`.
   По периодам — `history` (`cpuMs`, `gc`, `pauseMs`, `jit`, `jitProcess`, `jitMs`, `processBytes`).
8. Что именно — диагностический режим: `profile.stages {"events":true}` (включает и профайлер; JIT дороже — только для
   расследования), шаги, затем `profile.stages {}` → `events`: `alloc.types` (тип, байты по выборке ~100 КБ, на главном
   потоке, зоны), `jit` (методов, уровни `tier0`/`tier1`/`osr`/`tier0-instrumented`…), `gc` (сборки, паузы), `recent` —
   последние события с `frame`/`afterFrame` (между кадрами), `tick`, `zone`, `path`. `late` — событие старше ленты зон
   (~0,25 с кадров App). Выключить — `{"events":false}`. Без агента — `--profile runtime` у обоих exe (секция `events` в
   `profile.json`). Стеки вызовов — не здесь: `dotnet-trace collect -p <pid>` / PerfView.

**Код или машина (точка Пб).** Бюджет или бенч упал — сначала вердикт, потом код. Пользователь иногда нагружает машину.
1. Где смотреть: `feature.verify` → `budgets[].machine` (сумма повторов, `runs` — по повтору) и `budgets[].frame.machine`;
   `experiment.sweep` → `rows[].machine`; `Sword.Headless --json` → `machine` (тики после прогрева); `Sword.exe --json` →
   `report.machine` (бенч — кадры замера); `profile.stages` → `machine` (с включения или `reset`); журнал — `[bench]
   вердикт: …`. Провал времени бюджета сам дописывает вердикт: `… мс/тик (медиана), предел … — машина: …`.
2. `verdict`: `машина` — другие процессы заняли ≥ 25 % всех логических процессоров (`othersBusyPct`; окно ≥ 100 мс), или счётный поток
   был на CPU < 90 % своего времени (`threadOnCpuPct`; у профайлера — `ticksOnCpuPct`, тики), или частота ядра (`mhz.probe`,
   только Release) ниже 90 % базовой (`mhz.base`), или ОС ограничила частоту процессора ниже его собственной базы (`mhz.limit` <
   `mhz.limitBase`: у гибридных CPU базы P- и E-ядер разные); `среда` — JIT на главном потоке (`jit.mainThread`) или GC
   (`gc.collections`, `gc.pauseMs`) во время замера; `код` — ничего из этого: время — за код. Фоновый JIT (`jit.methods` −
   `jit.mainThread`) — причина «фоновый JIT … вердикт не меняет» (с Пв). `reasons` — причины с числами, `text` — одной строкой.
3. `машина` → перемерить (один раз), сообщить пользователю числа; предел бюджета не трогать. `среда` с JIT на главном
   потоке → разовая компиляция в замере (прогрев мал или метод впервые вызван посреди замера) — какой, покажет аномалия тика
   (рецепт ниже); фоновый tier1 — только причина (медиана повторов его отбрасывает). `код` → профайлер: `profile.stages`, время
   систем бюджета.
4. `note` в `machine` — окно короче 0,5 с: загрузка системы по счётчикам ОС (шаг ~15,6 мс) грубая, короче 0,1 с — в вердикт не идёт (замер бюджета `movement` — ~5 мс); доля CPU потока — точна.

**Почему был медленный кадр или тик (захват аномалий, точка Пв).** Среднее и p99 говорят «бывает медленно», аномалия — какой
именно период, где и почему.
1. Включённый профайлер ловит сам: кадр дольше `медиана + max(2 мс, 0.75·медиана)`, тик — `медиана + max(1 мс, 0.75·медиана)`
   (медиана — 128 последних; первые 32 и служебные кадры не судятся). Свой порог — `profile.stages {"threshold": 5}` (мс; `0` —
   снова по медиане) или `--profile-threshold 5` у exe. Без агента: `Sword.exe --profile` / `Sword.Headless … --profile`; у
   Sword.exe — ещё F2 (профайлер включается им же).
2. Где видно: журнал — `log.tail {"category":"profile"}` (строка `аномалия: кадр N: X мс (медиана, порог) — класс; главная
   зона Z …; файл …`); `profile.stages {"anomalies": 20}` → `anomalies.list` (период, номер, `ms`, `class`, `causes`, `zone`,
   `file`, `noticeable`; `state`: `written`, или без файла — `rate`/`limit`/`classLimit`/`busy`: файлов за прогон — код, JIT, GC
   не больше 20, «ожидание GPU» и «машина» — по 5, заметные — ещё до 10 сверх лимитов (`limits`), 5 сразу и дальше 1 в секунду,
   но в списке — все), `byClass`, `detected`/`sinceReset`; `profile.json` → `anomalies`; итог `--json` → `profile.anomalies`.
   **Видел ли это человек** — `noticeable` (строка журнала — `…, заметная (кадр X мс)`, F2 — `· заметная`): кадр App не короче
   интервала показа (`anomalies.display`: `noticeableMs` = 1000 / частота монитора окна, `source` — `monitor` или `default` —
   60 Гц). Жалоба пользователя «дёрнулось» — сначала заметные; незаметные (кадр 3 мс при 144 Гц) — рывки для Пд, глазу не видны.
   **Почему кадр не судился** — `profile.stages {"history": 200}` → `history[]`: `service` — служебный, `tools` — кадр
   инструментов (в нём точка обслуживания выполняла запросы агента — и ваши: их время и выделения — не игры; счёт — `toolFrames`).
   Такой кадр как кадр не аномалия, его тик — судится; средние профайлера и итог прогона — без него. Мерить игру — между
   вызовами агента (или без агента: `Sword.exe --profile`), а не по кадрам, где агент спрашивал.
3. Класс (`class`, из `causes` — разбор лишнего времени сверх медианы): `jit` — компиляция на главном потоке (OSR долгого цикла,
   первый вызов; метод — в `events` файла в диагностическом режиме, `tier: osr`; OSR метода тика или кадра — вынести тело долгого
   цикла в свой метод, `AGENTS.md` Runtime; все компиляции процесса — `DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=<файл>`,
   строки `Tier1-OSR`); `gc` — пауза сборки (кто выделял — события
   выделений окна); `gpu` — `frame.fenceWait`/`frame.present` дольше обычного (GPU занят — в т. ч. чужой программой; по проходам
   — секция `gpu` файла, точка Пг: кадр N − 3, чьего забора ждали, и проходы по лишнему времени); `machine` — поток сняли с процессора (`offCpuMs`; `machine` файла — загрузка другими процессами) — перемерить,
   сообщить пользователю, код не трогать; `code` — время на CPU в коде: смотреть `mainZone`. «Главной зоны нет» (`zone: null`,
   `zoneNote`; в файле `mainZone.name: null`) — сработал абсолютный порог (`threshold`) на периоде не дольше своего среднего
   (лишнее время зон в сумме не больше 0; класс такой аномалии — по шуму в микросекунды) — искать нечего, порог ниже обычного периода.
4. Файл `anomaly-<frame|tick>-N.json`: `mainZone.path` (например `tick > stage.Movement > core.root-collision@Movement`) — наибольшее
   **своё** лишнее время; `zones` — по нему же (первая — главная), `tree` — дерево периода со средним каждой зоны; `header` —
   CPU потока, JIT и GC периода; `recent` — 32 периода до (был ли он один или серия); `tick` — у кадра App, сработавшего по тику;
   `events` — в диагностическом режиме (`profile.stages {"events":true}` / `--profile runtime`) — JIT, сборки и выделения по
   типам периода и окна с зонами, без него — `mode: counters`; `machine` — машина за последнюю секунду.
5. Картинкой — `trace` файла (`anomaly-…trace.json`) или кольцо целиком `profile.stages {"trace":true}` / `--profile-trace`:
   открыть в https://ui.perfetto.dev («Open trace file») или `chrome://tracing`. pid 1 — кадры и тики с настоящим временем (зоны
   в них — суммой вызовов, место условное), pid 2 — лента зон точно (диагностический режим), pid 3 — события среды.
6. Проверить исправление: тот же прогон — аномалии этого класса и зоны пропали (`byClass`, `list`), p99 кадра/тика — ниже.

**Упор в CPU или GPU (GPU-время проходов, точка Пг).** Кадр App медленный — чей он: CPU или GPU. GPU-время — всегда, без профайлера.
1. Числа: бенч — `Sword.exe --units 32 --bench 10 --json` → `report.bench`: `averageMs` (кадр), `cpuMs` (работа CPU без ожиданий),
   `waitMs` (ожидание забора кадра в полёте), `gpuMs`/`gpuP99Ms` (GPU-время кадра), `passes` (проходы — мс на кадр); журнал —
   `[bench] … ; GPU X мс, p99 Y` и `[bench]   <проход> … мс/кадр GPU`. Живая игра — `render.graph` (один кадр: `passes[].gpuMs`,
   `gpu.frameMs`) или `profile.stages {"enable":true}` → шаги → `profile.stages {}` → `gpu` (среднее, медиана, p99 за окно) рядом с
   зонами `frame.fenceWait`, `frame.present`, `frame.build`, `frame.record`.
2. Вывод: **GPU** — `gpuMs` ≈ кадр, `cpuMs` заметно меньше, `waitMs` (и `frame.present`) большие — CPU ждёт GPU: смотреть
   проходы (`passes`, главный — наверху по мс). **CPU** — `cpuMs` ≈ кадр, `waitMs` ≈ 0, `gpuMs` меньше кадра — GPU ждёт CPU: зоны
   CPU (`profile.stages`, рецепт «Где время кадра»). Рывки: p99 кадра ≈ `gpuP99Ms` — рывки GPU (аномалии «ожидание GPU», секция
   `gpu` файла называет проход).
3. Честно про числа: GPU-время в кадре N — кадра N − 3 (`lagFrames`, `gpu.frameId`; у `capture.bundle` и в итоге `--json` — того
   же кадра, отставание 0); частота GPU плавает — лёгкий кадр на простаивающем GPU (низкие такты) и кадр при чужой программе на
   GPU дольше: сравнивать прогоны одной машины подряд (`machine` — загрузка CPU другими процессами; GPU — `nvidia-smi`), машина
   занята — перемерить, сообщить пользователю. Проход без своих замеров внутри (`scene.opaque`) — одно число: делить — проходами.

**Понять, почему поле такое.** `ecs.componentUsage {"name":"Transform"}` — кто пишет; `ecs.explainSystem` писателя —
его входы (`upstream`, с пометкой «в этом тике» или «с прошлого тика» и причиной `why`: команды той же стадии —
только в следующем тике) и место регистрации (`source`).

**Посмотреть на кадр живой игры.** `time.pause` (кадр показывает ровно последний тик) → `capture.bundle
{"name":"look"}` → открыть `color` и `depth` из ответа; `entities[].rect` — где на картинке кто. `render.graph` — какие
проходы нарисовали кадр и сколько каждый стоил GPU (`passes[].gpuMs` — кадра `gpu.frameId`, обычно на 3 раньше показанного); у
`capture.bundle` — `gpu` кадра снимка (отставание 0).

**ID-буфер и оверлей капсул (5в).** Чья рука на пикселе, что кого закрывает, стоят ли капсулы тела на персонаже:
`time.pause` → `capture.bundle {"name":"ids","ids":true,"overlays":["capsules"]}`. В ответе:
- `ids.png` — ID-буфер для глаз (код — свой цвет, чёрное — земля, колонны, небо); `ids.raw` — сырые коды (uint32 на пиксель,
  строки сверху вниз, `width`×`height`); `ids.legend` — коды кадра: `entity`, `name` (`player`, `enemy 3`), `segment`
  (`forearm.l`), `title` («левое предплечье врага 3»), `pixels`, `rect` [x0, y0, x1, y1). Код = (номер сущности + 1) << 8 |
  сегмент (`BodySegs`); 0 — ничего. Сегмент пикселя — по суставу вершины с наибольшим весом (`CharacterRig.JointSegment` SWORD).
- `overlays.capsules` — снимок цвета с капсулами тел (левые конечности голубые, правые красные, таз-торс-голова жёлтые) и
  подписями бойцов («игрок», «враг 3»); `color` — тот же кадр без них.
Пусто в `ids.legend` у бойца, который виден на `color`, — ID-проход не нарисовал его (ошибка рендера); `notes` — что было
заказано, но не снято. Числа капсул — `physics.bodies {"entity":"enemy 3"}` (центр, концы, скорости по сегментам). Капсулы
оверлея — «как показан боец»: на паузе (`alpha` 1) и в видах фичи — ровно постановка последнего тика (`physics.bodies`); на
ходу (`alpha` < 1) — та же постановка, перенесённая вместе с бойцом в его показанное (интерполированное) место, как меш, —
от `physics.bodies` она отличается на сдвиг и поворот бойца за долю тика, поэтому сверять числа — на паузе. Капсулы в
видах фичи — `views[].overlays: ["capsules"]` в `feature.json` (App рисует их поверх кадра, как F7: `Sword.exe --overlay
capsules`).

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
- **Процессы агентских серверов (точка 6а).** Каждый открытый сеанс настольного Claude Code (процесс `claude.exe`) и
  поток Codex держат свой `Sword.Headless --agent` (~850 МБ), пока сеанс открыт, — подагенты своих серверов не заводят,
  они пользуются сервером сеанса. Висящих серверов больше быть не должно: сервер выходит сам по концу stdin, концу
  обёртки `cmd` и после 15 мин без вызовов (`--idle-exit`; следующий вызов клиент исполнит на новом сервере — сеанс с
  тика 0, другой `sessionId`, реплей прежнего — в его артефактах, `session.start` с ним), а Sword.exe видов — в задании
  Windows и умирает с ним. Посмотреть: `Get-CimInstance Win32_Process -Filter "Name='Sword.Headless.exe'" | Select
  ProcessId,ParentProcessId,CreationDate,WorkingSetSize` — родитель `cmd.exe`, его родитель — `claude.exe` сеанса
  (`~/.claude/sessions/<pid>.json`: `sessionId`, `name`) или `codex.exe`. **Чужие не завершать:** сервер живого сеанса
  (родитель жив) нужен ему — при снятии клиент переподключится, но состояние сеанса пропадёт. Завершать можно только
  процесс, у которого нет живого родителя-клиента, или свои. Узлы MSBuild (`dotnet … MSBuild.dll /nodeReuse:true`,
  ~150–200 МБ, до 8 штук) и `VBCSCompiler` — не зависание: общие для всех сборок пользователя, выходят сами через
  10–15 мин простоя; `dotnet build-server shutdown` снимает их сразу, в том числе посреди чужой сборки, — не звать,
  пока работают другие сеансы.
