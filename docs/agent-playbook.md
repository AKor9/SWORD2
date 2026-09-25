# Рецепты для агентов

Как ИИ-агенту (Claude Code, Codex, любой MCP-клиент) запускать, наблюдать и отлаживать SWORD. Устройство агентского
доступа — [engine/Sword.Engine.Agent/README.md](../engine/Sword.Engine.Agent/README.md), правила — раздел 8
[плана](../ARCHITECTURE_PLAN.md). Рецепты — в текущем виде; прежняя редакция с пометками точек и замерами —
[docs/history/playbook.md](history/playbook.md).

## Подключение

- **Claude Code** читает `.mcp.json` в корне: `sword-headless` (stdio: `tools/scripts/agent-headless.cmd` собирает Headless в
  `artifacts/agent-bin/headless/<запуск>` и стартует `Sword.Headless.exe --agent`; сборка не удалась — исправить и переподключить в `/mcp`)
  и `sword-app` (HTTP, `http://127.0.0.1:47813/mcp` — сначала `Sword.exe --agent`, иначе переподключить в `/mcp`). `sword-headless` выходит
  сам после 15 мин без вызовов (`--idle-exit`): следующий вызов идёт на новый сервер (сеанс с тика 0). Виды `feature.verify` сервер снимает
  `Sword.exe` из `game/Sword.Game.App/bin/Debug` (собирает `dotnet build SWORD.slnx`; не собран — «Sword.exe не собран»).
- **Окна `Sword.exe` агентов — без фокуса**: пользователь работает на той же машине. В `.claude/settings.local.json` (вне git)
  задано `"env": {"SWORD_NO_ACTIVATE": "1"}` — запуски `Sword.exe` из Claude Code идут как с `--no-activate` (окно внизу порядка
  перекрытия; журнал — `окно без фокуса: SWORD_NO_ACTIVATE=1`, итог `--json` — `window.activate: false`). Вне Claude Code — запускать
  с `--windowed WxH --no-activate` (замеры — `--windowed 640x480`); `--activate` возвращает фокус.
- **Codex** (`~/.codex/config.toml`):

  ```toml
  [mcp_servers.sword]
  command = "cmd"
  args = ["/c", "D:\\Work\\Visual Studio\\SWORD2\\tools\\scripts\\agent-headless.cmd"]
  ```

- **Любой клиент**: stdio — строка JSON-RPC на сообщение; HTTP — `POST /mcp` с сообщением, ответ `application/json`.

## Порядок

`session.status` → устройство (`ecs.plan`, `architecture.projects`, `module.graph`) → состояние (`world.query`,
`entity.get`, `state.hash`, `state.describe`) → время (`time.step`, `time.runUntil`, `time.seek`) → воздействие (`command.send`) →
проверка состояния → `log.tail`; реплеи — `replay.*`, `session.start`. Каждый ответ несёт квитанцию: `tickId` — сыграно тиков к
моменту ответа, `frameId` — кадр живой игры.

## Рецепты

**Проверить движение игрока (Headless).**
1. `world.types {"name":"PlayerCommand"}` — поля; `command.send {"command":"PlayerCommand","fields":{"Move":{"X":1,"Y":0}},"repeat":120}`
   (команда игрока действует один тик, поэтому `repeat`).
2. `time.step {"ticks":120}`; `entity.get {"entity":"player","components":["Transform","RootVelocity"]}` — x ≈ 3,94, скорость 4,2 м/с.

**Проверить анимацию бойца.**
1. Клипами (враги; игрок — при `locomotion.player=clips`: тесты, сценарии фич, эталоны) —
   `entity.get {"entity":"enemy 1","components":["LegacyLocomotionState","AnimatorMotion"]}`: какой клип играет (номер), время клипа, доля
   кроссфейда; сглаженная скорость и «идёт». Игрок в игре (`sword.json`: `locomotion.player=mm`) — на Motion Matching:
   `entity.get {"entity":"player","components":["MotionMatchState","SteerModel"]}` — клип (номер), кадр в клипе, доля, темп, поисков и смен,
   итог последнего поиска (цена, цена продолжения, 12 групп), модель контроллера тика; база и проигрыватель —
   `state.describe {"part":"locomotion","filter":"base"}` (сводка, отпечаток) и `{"filter":"0v1:foot"}`; ручки —
   `command.send {"command":"TuningCommand","fields":{"Knob":"locomotion.switchGain","Value":1}}`; MM против клипов —
   `experiment.fork` с веткой `{"set":["locomotion.player=clips"]}`; подробно — раздел «Motion Matching игрока».
2. `anim.clips {"index":N}` — имя клипа по номеру (или `{"filter":"Run"}`): длительность, цикл, скорость корневого движения; страницы по 50
   (`skip` из `continuationToken`).
3. `anim.pose {"entity":"player"}` — суставы (таз, голова, кисти, стопы) в пространстве персонажа и в мире; `pool` — живые позы и задания
   последнего тика (`staleLastTick` должен быть 0); у врага — ещё `action` (слой действий).
4. Регрессия — эталоны SWORD2 (`--filter ReferenceReplays`: хеш `animation` каждого тика) и сценарий `locomotion` манифеста `animation`.

**Враги и команды арены.**
1. Headless со `sword.json` стартует с 10 врагами (`arena.enemies`; `--units N` или `--set arena.enemies=N`, 0…32; тесты и сценарии фич
   без `sword.json` — 0). Сколько задано — `ArenaState.Enemies` (`state.describe {"part":"ecs","filter":"resource:"}`); враги —
   `world.query {"with":["EnemyTag"],"fields":["FighterSlot","Transform"]}`; враг по месту — `entity.get {"entity":"enemy 3"}`.
2. PageUp/PageDown/R: `command.send {"command":"ArenaCommand","fields":{"Action":"ChangeEnemies","Enemies":1}}` (`-1` — PageDown),
   `{"Action":"SetEnemies","Enemies":32}`, `{"Action":"Reset"}`; изменения видны после `time.step {"ticks":1}`; команда — в реплее.
3. Места без дыр и число бойцов — инвариант `arena.slots` (`time.breakOnInvariant {"ids":["arena.slots"]}`).
4. Сброс строит бойцов заново на тех же сущностях (`player`, `enemy N` в условиях и `watch.add` его переживают); после PageDown и PageUp
   враг — новая сущность, поэтому место — полем: `{"exists":{"all":[{"component":"EnemyTag"},{"field":"FighterSlot.Index","equals":5},{"field":"Transform.Position.X","greaterThan":0}]}}`.
5. Без агента: `Sword.Headless --ticks N --units 32 --json` — `arena` в итоге (враги по местам, расстояние до игрока, скорость,
   составляющие ИИ); приёмка — `feature.verify {"id":"enemies"}` / `Sword.Headless --feature enemies`.

**ИИ врагов: почему враг идёт туда, куда идёт.**
1. `entity.get {"entity":"enemy 3","components":["EnemyAiTerms","FighterCommand","GazeTarget"]}` — составляющие намерения тика:
   `Distance`, `ApproachSpeed` (> 0 — подходит; ноль на 1,8 м, предел −2…3,2), `SwaySpeed` (±0,7), `Separation` и `Neighbours` (соседи
   ближе 1,4 м); `FighterCommand` — итог, `GazeTarget` — корень игрока.
2. Как менялось: `watch.add {"entities":["enemy 3"],"fields":["EnemyAiTerms","FighterCommand","Transform"]}` → `time.step` →
   `entity.history {"entity":"enemy 3","changes":true}`.
3. Когда подошли все: `time.runUntil {"condition":{"none":{"all":[{"component":"EnemyTag"},{"field":"EnemyAiTerms.Distance","greaterThan":2.5}]}},"maxTicks":1200}`;
   сводка — `world.query {"with":["EnemyTag"],"aggregate":["EnemyAiTerms.Distance","EnemyAiTerms.Neighbours"],"take":0}`.
4. Намерение считается по корням до движения тика: посреди тика — `time.break {"stage":"Intent"}` и `entity.get`. Время арены —
   `{"resource":"SimTime.Time"}`. Свойства ИИ — `dotnet test tests/Sword.Game.Tests --filter EnemyAi`.

**Столкновения бойцов: кто кого толкнул и где перекрытие.**
1. `entity.get {"entity":"enemy 3","components":["RootContacts","Transform","RootVelocity"]}`: `Contacts`, `Push` (сдвиг корня парами),
   `FromPlayer`, `Deepest` и `DeepestWith` (место; 0 — игрок, −1 — касаний нет), `Obstacle` (сдвиг колоннами и краем арены). Толчок игрока
   парами — `−Σ FromPlayer`: `world.query {"with":["EnemyTag"],"aggregate":["RootContacts.FromPlayer.X","RootContacts.FromPlayer.Z"],"take":0}`.
2. «Когда игрок толкнул врага»: `time.runUntil {"condition":{"exists":{"all":[{"component":"EnemyTag"},{"field":"RootContacts.DeepestWith","equals":0}]}},"maxTicks":600}`;
   кого — `world.query {"with":["EnemyTag"],"where":{"field":"RootContacts.DeepestWith","equals":0},"fields":["RootContacts","FighterSlot"]}`.
3. Перекрытие корней — инвариант `movement.rootOverlap` (после Movement: ближе 0,35 м — дольше 4 тиков подряд, перекрытие больше 0,02 м —
   дольше 24 тиков; кучка после спауна разводится несколько тиков — не ошибка): `time.breakOnInvariant {"ids":["movement.rootOverlap"]}`,
   подробности — `events.history {"types":["InvariantViolation"]}`, проверить сейчас — `invariant.run`.
4. Посреди тика: `time.break {"stage":"Movement"}` — корни уже разведены, `{"stage":"Animation"}` — ещё нет. Свойства — `dotnet test
   tests/Sword.Game.Tests --filter RootCollision`. Без агента: `Sword.Headless --ticks N --units 32 --move 1,0 --json` —
   `arena.closestPair` и у мест `contacts`, `push`, `deepest`, `obstacle`; приёмка — сценарии `push`, `crowd-commands` манифеста `enemies`.

**Толпа разошлась с эталоном: где.**
1. Толпа с командами арены (PageUp, PageDown, 16, R, 32), колонна, до 33 бойцов — сценарий `crowd-commands` манифеста `enemies`, его
   запись — эталон SWORD2 `tests/reference/replays/enemies.swreplay`. Разошлось — `feature.verify {"id":"enemies","views":false,"budgets":false}`
   (сценарий `reference`: `replay.divergence` — тик и участники) или `replay.check {"path":"tests/reference/replays/enemies.swreplay"}`.
2. Боец на тике расхождения: `session.start` с эталоном, `time.runUntil {"tick":T-1}`, `watch.add {"entities":["enemy 5"],"fields":
   ["Transform","EnemyAiTerms","RootContacts","LegacyLocomotionState"]}`, `time.step`, `entity.history`; с новым прогоном — `replay.diff`.

**Поворот врага на месте клипом.**
1. Враг (не-`Agile`) поворачивает на ходу 12 рад/с, стоя — 2,5 рад/с, а отстал стоя больше 45° — клипом (90° влево, вправо, разворот).
   Вызвать командой: `command.send {"command":"EnemyCommand","fields":{"Slot":1,"FacingGoal":3.9},"repeat":150}` (намерение врага на тик
   вместо ИИ: `Velocity` — желаемая скорость x, z; `FacingGoal` — курс-цель, рад); команда — в реплее.
2. Идёт ли поворот — `entity.get {"entity":"enemy 1","components":["AnimatorAction","AnimatorMotion"]}`: `Kind` (`None`/`Turn`), `Clip`,
   `Time`/`Duration`, `Weight`, `TurnScale`, `YawDelta`, `RootDelta`; имя клипа — `anim.pose {"entity":"enemy 1","joints":["pelvis"]}` →
   `action`. Встать на начале: `time.runUntil {"condition":{"all":[{"entity":"enemy 1"},{"field":"AnimatorAction.Kind","equals":"Turn"}]},"maxTicks":120}`.
3. Слой действий — инвариант `animation.actions`; поза слоя — `state.describe {"part":"animation"}`. Порядок: `core.root-steer` →
   `core.animator-input` → `core.animator-pose` → `core.root-control` (посреди тика — `time.break {"stage":"Gameplay"}`). Свойства —
   `dotnet test tests/Sword.Game.Tests --filter EnemyTurn`; приёмка — сценарий `turn` манифеста `enemies`; глазами —
   `Sword.exe --replay artifacts/features/enemies/scenario-turn.swreplay`.

**Тела бойцов.** Тело бойца — сочленение из 14 тел-капсул подсистемы Physics (тело i = сегмент `BodySegs`: 0…10 — pelvis…shin.r, 11 —
`blade`, клинок по мечу в руке, 12, 13 — foot.l, foot.r); у бойца в ECS — хэндл `BodyRef`. Живой — кинематический: капсулы плоти стоят на
суставах позы тика (PrePhysics), тело клинка — на мече; мёртвый — труп на физике («Рэгдолл» ниже).
1. `physics.bodies {"entity":"enemy 3"}` — тела с именами (`forearm.l` — «левое предплечье»): центр, концы, поворот, скорости, прошлая
   постановка, форма, масса, режим, профиль, касание земли; `fastest`; `"segments":["foot.l","foot.r"]` — только эти. Слоты и поколения —
   `state.describe {"part":"physics","filter":"3v1"}` (сочленение `Index`v`Generation` из `BodyRef`), свободные — `{"filter":"slots"}`;
   хеш — участник `physics` в `state.hash`.
2. Кто заводит и освобождает тело — `ecs.componentUsage {"name":"BodyRef"}` → `hooks`; кто ставит — `ecs.explainSystem` для
   `core.body-setup` (Gameplay, по `Added<Fighter>`: форма и постановка без скорости по позе постройки — ресурс `StartPose`),
   `core.kinematic-placement` и `core.blade-placement` (PrePhysics).
3. Инварианты: `physics.kinematicPose`, `physics.kinematicSpeed` (после PrePhysics), `physics.bodyRefs` (после End), `physics.finite`
   (после Physics): `time.breakOnInvariant {"ids":["physics.kinematicSpeed"]}`, затем `physics.bodies`. `invariant.run` посреди тика до
   PrePhysics `physics.kinematicPose` не сверяет; до первого тика — сверяет с позой постройки.
4. Числа тела во времени — вычисляемые поля `Body.<сегмент>.<поле>[.X|.Y|.Z|.W]` (`Position`, `Rotation`, `PrevPosition`, `Velocity`,
   `AngularVelocity`, `EndA`, `EndB`, `Radius`, `HalfLength`, `Mass`) и `Body.MaxSpeed`, `Body.MaxAngularSpeed`: метрики манифеста,
   `watch.add {"entities":["player"],"fields":["Body.MaxSpeed","Body.foot.l.Position.Y"]}` → `entity.history`, по записи сценария —
   `replay.observe {"path":"<артефакты фичи>/scenario-terrain.swreplay","watch":{"entities":["player"],"fields":["Body.MaxSpeed"]}}` → `.jsonl`.
5. Регрессия — эталоны SWORD2 (хеш `physics` каждого тика): толпа — `enemies.swreplay` (сценарий `reference` манифеста `bodies`), рельеф —
   `bodies.swreplay` (`reference-terrain`); разошлось — `replay.divergence`, тела на тике — `session.start` с эталоном, `time.runUntil`,
   `physics.bodies`. Приёмка — `feature.verify {"id":"bodies"}` (сценарии, метрики, бюджеты с капсулами — `budgets[].frame.overlays`).

**Оружие и клинки.** У бойца меч в руке (`WeaponSlot` — номер в таблице мечей пака) и клинок (`Blade`: основание и кончик тика и прошлого
тика в мире, `Weapon`). Враг — случайный меч при постройке, игрок — `combat.playerWeapon` и инвентарь (`PlayerInventory`); T — `NextWeapon`.
1. `combat.weapons` — таблица мечей, инвентарь, меч и клинок каждого бойца (`tipTravel` — путь кончика за тик), таблица ударов (`attacks`;
   `{"entity":"enemy 3","attacks":false}` — один боец). Инварианты — `combat.blade` (после PrePhysics), `combat.weapons` (после End).
2. Сменить меч: `command.send {"command":"ArenaCommand","fields":{"Action":"NextWeapon"}}` (или `"EquipWeapon"`, `"Weapon":N`) → `time.step`.
3. Глазами: `capture.bundle {"overlays":["blades"],"ids":true}` — слой клинков (игрок жёлтый, враги белые, прошлый тик серый, пройденное —
   оранжевое) и ID-буфер (меч — сегмент 11 `blade`, «клинок врага 3»); в видах — `views[].overlays: ["blades"]`, в игре — F7 или
   `Sword.exe --overlay blades`. Приёмка — `feature.verify {"id":"combat"}` (сценарий `weapons`, вид `blades`).

**Удар, связка, рывок.** Удар — действие слоя действий `AnimatorAction` (`Kind` = `Attack`, `Attack` — номер удара: 0–3 slash1–4, 4–6
stab1–3), рывок — `PlayerControl` игрока (`Kind` = `Dash`).
1. `command.send {"command":"PlayerCommand","fields":{"Attack":true}}` (`"Stab":true`, `"Dash":true`; жест — `"Gesture":{"X":1,"Y":-0.2}`
   в системе бойца; ход — `Move`: без него рывок назад от курса). Нажатие ждёт в буфере (удар 0,3 с, рывок 0,2 с). Врагу —
   `{"command":"EnemyCommand","fields":{"Slot":1,"Attack":5}}` (номер + 1). Мирные враги — `session.start {"set":["combat.enemiesAttack=false"]}`.
2. `entity.get {"entity":"player","components":["AnimatorAction","PlayerControl","AttackCombo"]}` — действие, удар, время и вес,
   `Upper`/`Stand`, `CutRecovery`, снимок позы при прерывании, `AttackSerial`, что прервано (`From*`); буферы, перезарядка, рывок,
   неуязвимость; связка. У врага — `EnemyAttack.Timer`; словами — `anim.pose {"entity":"enemy 1","joints":["hand_r"]}` (`action.attack`).
3. Кончик клинка по тикам: `watch.add {"entities":["player"],"fields":["Blade.Tip.X","Blade.Tip.Y","Blade.Tip.Z","AnimatorAction.Kind","AnimatorAction.Time"]}`
   → удар → `time.step {"ticks":60}` → `entity.history {"entity":"player"}`; дольше кольца — `replay.observe` с тем же `watch`.
4. Инварианты `animation.actions`, `combat.control`. Приёмка — `feature.verify {"id":"combat"}` (сценарии `strike-combo`, `stab`,
   `run-strike`, `dash`, `strike-from-dash`, `enemies-swing`, `peaceful`; вид `strike`); свойства — `--filter Action`.

**Урон, смерть, реакции, возрождение. Почему боец умер.** Протокол боя — событиями: `StrikeContact` (Combat) → `Hit`, `Dead`,
`SeverRequested` (Resolve) → реакция (React) → кровопотеря, возрождение (Aftermath) → `ArenaStats` (Settle). Компоненты: `Health`, `Dead`
(время и причина смерти), `StrikePairs` врага, `Flinch` игрока.
1. `events.history {"types":["Hit","Added<Dead>"],"entity":"enemy 1"}` — попадания (`Attacker`, `Part` — сегмент, урон, вид, точка,
   `Killed`, `LethalThrust`, `Corpse`, `Probe`) и тик смерти; касание без попадания — `{"types":["StrikeContact"]}`; старше кольца (1200
   тиков) — `replay.observe {"path":"session","events":["Event<Hit>","Added<Dead>"]}`; цепочкой — `state.why` («Понять, почему поле такое»).
2. Здоровье: `watch.add {"entities":["player"],"fields":["Health.Hp","Health.Bleed"]}` → `entity.history`; сейчас — `entity.get
   {"entity":"player","components":["Health","Dead","Flinch"]}`; статистика — `state.describe ecs` (`ArenaStats`).
3. Проба без клинка: `command.send {"command":"StrikeProbe","fields":{"Attacker":0,"Victim":1,"Part":1,"Damage":30,"Kind":"Thrust"}}` (путь
   Resolve); неуязвимый игрок — `session.start {"set":["combat.playerInvulnerable=true"]}`. Инварианты `combat.health`, `combat.dead`,
   `combat.hits`, `combat.respawn`; приёмка — сценарии `duel`, `player-dies`, `bleed`, `respawn`, `dash-through` манифеста `combat`.

**Изменить ручку и увидеть эффект.** Ручка — поле ресурса мира (`CombatRules`, `RagdollRules`), имя — ключ конфига (`combat.playerDamage`);
изменение на ходу — команда `TuningCommand` (в реплее).
1. Какие ручки: `world.types {"name":"CombatRules"}` → `knobs` (имя, поле, вид, подпись, подсказка, `min`, `max`, `default`, `log`);
   значения — `state.describe {"part":"ecs","filter":"CombatRules"}`.
2. Изменить: `command.send {"command":"TuningCommand","fields":{"Knob":"combat.playerDamage","Value":4}}` (флаг — `1`/`0`; вне диапазона —
   прижимается; незнакомое имя — ошибка); действует в тике команды; история — `events.history {"types":["TuningCommand"]}`.
3. Эффект: `events.history {"types":["Hit"]}` — `Damage`; сравнить с ручкой и без — `experiment.fork` (ветки с `TuningCommand` на одном
   тике) → `experiment.compare`. С другого значения без команды — `session.start {"set":["combat.playerDamage=4"]}`. В живой игре —
   `Sword.exe --json` → `combat`, `panels`; снимок панели — `--hold tuning`.

**Проверить детерминизм и найти расхождение.**
1. `state.hash` — итог и хеш каждого участника (`ecs`, `animation`, `physics`, `skin`); два прогона с одними командами обязаны совпасть
   тик в тик; разный хеш участника называет подсистему.
2. `state.describe {"part":"ecs","filter":"0v1"}` — компоненты сущности ровно так, как хешируются (`"filter":"slots"`, `"journal:"`,
   `"system:"`; `{"part":"animation","filter":"0v1:foot"}`); без агента — `Sword.Headless --ticks N --move X,Y --json` → `stateHash`.

**Воспроизвести реплей и найти расхождение.**
1. Сеанс пишется с тика 0: `replay.save {"name":"my"}` — `.swreplay` в артефактах; `replay.status`. Живая игра: `Sword.exe --record FILE`.
2. `replay.check {"path":"my.swreplay"}` — `match`, `divergence.tick`, `lastMatch`, `parts`; сведения реплея (`facts` заголовка — отпечаток
   базы MM) не те, что у симуляции, — `warnings` и `divergence.cause`; без агента — `Sword.Headless --replay FILE --json` (код 3 при
   расхождении).
3. Встать перед расхождением: `session.start {"replay":"my.swreplay"}` → `time.runUntil {"tick":lastMatch}` → `state.describe` участника;
   `time.step {"ticks":1}` → снова `state.describe`. `time.seek {"tick":N}` — назад и вперёд (после перемотки назад прежнее будущее
   повторяется, команды на несыгранные тики сброшены). Таймаут — ошибка с достигнутым тиком: повторить или дать больше `timeoutSeconds`.
4. «Что если»: после `session.start` с реплеем `command.send` на тик T ветвит; `replay.diff {"a":"my.swreplay","b":"session"}` — первый тик
   с разными командами и первый разный хеш; другой конфиг — `replay.check {"path":…,"set":["sim.seed=2"]}`.
5. Эталоны SWORD2 — `tests/reference/replays/*.swreplay` (`ReferenceReplaysTests`); намеренное изменение поведения — перезапись
   `Sword.Headless --replay F --record F` с доказательством (раздел 12 плана), причина — в md фичи.

**Наблюдать и остановиться по условию.** Условия — один JSON-язык (узлы — в описании `time.runUntil`):
`{"all":[{"entity":"player"},{"field":"Transform.Position.Y","lessThan":-10}]}`, `{"event":"PlayerCommand","where":{"field":"Walk","equals":true}}`,
`{"count":{"component":"Fighter"},"greaterThan":2}`, `{"for":30,"that":…}` (30 тиков подряд); поля — компоненты и вычисляемые (`Body.*`).
1. «Когда»: `time.runUntil {"condition":…, "maxTicks":N}` — встаёт на тике выполнения (и в живой игре); `met: false` — не выполнилось.
2. «Как менялось»: `watch.add {"entities":["player"],"fields":["Transform.Position","RootVelocity"]}` → `entity.history
   {"entity":"player","changes":true}`; без `entities` — все сущности под `where`.
3. «Что произошло»: `events.history {"types":["PlayerCommand"],"from":T}` — события, команды, `Added/Removed/Changed` последних 1200 тиков.
4. «Что внутри тика»: `time.break {"stage":"Animation","condition":…}` (Headless) — после барьера стадии; `time.step 1` доигрывает тик.
5. «Что изменилось»: `world.snapshot` → шаги → `world.diff {"a":"s1","b":"now","filter":"player-id/Transform"}`; выборка и сводка —
   `world.query {"where":…, "aggregate":["Transform.Position.X"]}`.
6. Глубже истории — `replay.observe {"path":"my.swreplay","events":["*"],"watch":{…},"condition":…}` → `.jsonl`, сводка — первый и
   последний `tickId` условия.

**Инварианты: что должно держаться всегда.** `invariant.list` — что проверяется (id, чей, когда, стоимость) и что нарушено (`recent`,
`violationsTotal`); нарушение — тик, стадия, сущность, подробности.
1. «Где сломалось»: `time.breakOnInvariant {"ids":["animation.owners"]}` (без `ids` — любой) — встаёт после нарушившего барьера.
2. «Своё правило»: `invariant.define {"id":"agent.onArena","must":{"field":"Transform.Position.X","between":[-40,40]},"where":{"component":"Fighter"}}`
   или `{"id":"agent.x","condition":…,"after":"Movement","every":10}`; `remove: true` — убрать; переживает `session.start` и перемотку.
3. «Проверить сейчас»: `invariant.run {"ids":["ecs.entityRefs"]}` (и по запросу, и посреди тика); `invariant.enable {"ids":[…],"enabled":true}`.
4. «Было ли раньше»: `events.history {"types":["InvariantViolation"]}`; глубже — `replay.observe {"path":…, "events":["InvariantViolation"]}`.
5. **Живая игра — тяжёлые раз в 4 тика**: в Sword.exe (кроме `--replay`) `animation.finite`, `ecs.finite`, `physics.finite`,
   `physics.kinematicPose`, `physics.kinematicSpeed` и тяжёлые инварианты трупа — на тиках, кратных 4 (`invariant.list`: `livePeriods:
   true`); нарушение короче 4 тиков может не попасть. Каждый тик — `time.breakOnInvariant` в живой игре (на время ожидания) или Headless
   (`replay.save` → `replay.observe` / `session.start` с реплеем): там `every: 1`, как в тестах и `feature.verify`.
Новый инвариант в коде — `IInvariant` и `SimulationBuilder.Invariant` (`AGENTS.md` Runtime); тяжёлый — `InvariantCost.Heavy`.

**Принять фичу (раздел 12 плана).** Приёмка — манифест `feature.json` рядом с md фичи: сценарии (реплей или команды по тикам) с проверками,
инварианты, метрики с допуском (замер, константа или формула; откуда — `reference`), виды в App, бюджеты, чек-лист «что проверить в игре».
1. `feature.list` — фичи; `feature.list {"id":"animation"}` — манифест целиком.
2. `feature.verify {"id":"animation"}` — вердикт `ok` и `failures` словами; сценарии (проверки, хеши реплея, нарушения), инварианты,
   метрики (`value`, `expected`, `delta`), бюджеты (медиана мс на тик, стадии, байты; кадр App — `frame`: средний и p99, `frame.overlays`;
   время и кадр — только в Release), виды (PNG `Sword.exe --replay`, хеши App сверены). Артефакты — `report.json`, `scenario-*.swreplay`,
   `view-*.png`; `views:false` — без App. Без агента — `Sword.Headless --feature ID|all [--json]` (код 4 — провал), `verify.ps1` шаг `features`.
3. Провал называет тик и значение; запись сценария — `session.start {"replay":"…/scenario-X.swreplay"}`. Ослабить манифест — только
   правкой файла с сообщением пользователю.
4. Новая фича — `feature.json` рядом с md (тест `FeatureManifestsTests`: манифест есть, чек-лист = пункты «Что проверить в игре»).
Метрика по ресурсу мира: `{"id": …, "resource": "ArenaStats.LastDamage", "expected": 60.8, "tolerance": 0.001}` вместо `entity`/`field`.
Статистика окна метрики (`stat` с `from`/`to`) — `min`, `max`, `mean`, `p95`, `p99` (95-й и 99-й перцентили ближайшим рангом — распределение
без одиночных выбросов `max`); потолок «не больше L» — `expected` = `tolerance` = L/2. Величина, определённая не в каждом тике (NaN:
скольжение опорной стопы без стопы на земле), — `"skipUndefined": true`: такие тики не входят в окно (в отчёте — `defined`), окно без
значений — провал; без ключа NaN в окне — провал «значения нет».
Редкая дорогая работа (поиск MM раз в 12 тиков), которую медиана тика прячет, — пик зоны профайлера в бюджете: `"zones": {"locomotion.search":
0.25}` — наибольшее время зоны за тик в прогоне бюджета с профайлером, мс; в отчёте `budgets[].zones` (пик, среднее, вызовы, доля тиков);
судится в Release.

**Эксперимент «что если».**
1. `experiment.fork {"replay":"my.swreplay","at":300,"variants":[{"name":"back","commands":[{"command":"PlayerCommand","fields":{"Move":{"X":-1,"Y":0}},"from":300,"to":301}]},{"name":"nolo","set":["modules.locomotion.enabled=false"]}],"measure":[{"entity":"player","field":"Transform.Position"}]}`
   — база и ветки параллельно; `vsBase.firstHashTick` (команда тика T — это T+1; ключ конфига — с тика 0), `measure`, `file` — запись
   ветки; `cut: true` — команды реплея после развилки не идут. `measure` читает и вычисляемые поля игры (`Body.foot.l.Position.X`,
   `MotionMatch.JointJump`); по тикам — `replay.observe` по `file` веток.
2. `experiment.compare {"a":<file базы>,"b":<file ветки>}` — заголовки и конфиг, первый тик с разными командами, первый хеш с разными
   участниками, листья описаний (`путь`, `a`, `b`).
3. `experiment.sweep {"params":{"arena.enemies":[0,8,16,32]},"replay":"<артефакты enemies>/scenario-crowd.swreplay","repeats":7}` — по
   комбинации мс на тик (медиана, разброс), стадии, байты, `deterministic`, `scaling`; время — в Release; сначала каждая комбинация по разу
   без замера (прогрев JIT), смотреть медиану; сеанс агента меряет с инвариантами (`sim.invariants=false` в `params` — как бюджет);
   `rows[].systems` — мс систем (профайлер), `rows[].machine` — вердикт машины («Код или машина»).

**Где время кадра или тика (профайлер CPU).** Выключен по умолчанию; включённый не меняет симуляцию и не выделяет памяти.
1. `profile.stages {"enable":true,"reset":true}` — включить и забыть прошлое (App — со следующего кадра).
2. Дать поработать (Headless — `time.step {"ticks":600}`; App — секунды игры); первые сотни тиков свежего процесса — неоптимизированный
   JIT: после них ещё раз `reset` или смотреть медиану.
3. `profile.stages {"top":30}` — зоны за кадр (App) или тик (Headless; `per`): `meanMs`/`medianMs`/`p99Ms`/`maxMs`, `selfMs`, `bytes`,
   `calls`, `presence`, `parent`. Имена: `frame.*` (`frame.simulate`, `frame.render`, `frame.fenceWait`, `frame.build`, `frame.record`,
   `frame.present`), `tick`, `stage.<стадия>`, `<метка>@<стадия>` — система, `barrier.<стадия>`, `invariant.<id>`, `sim.record`,
   `sim.observers`, `render.<проход>`, `arena.*`, `physics.*` (внутри шага физики); узко — `prefix`; по кадрам — `history`.
4. В файл — `profile.stages {"write":true}` → `profile.json`; выключить — `enable:false`. Без агента: `Sword.exe --profile` /
   `Sword.Headless --ticks 600 --units 32 --profile` → `artifacts/profile/<прогон>/profile.json`. Бюджеты — `budgets[].systems`, `profiled`.
5. Байты: `bytes` зоны — выделения потока владельца (спускаться от `frame` по зонам с `bytes` > 0); чужие потоки и время между кадрами зоны
   не видят. Среда: `runtime` — `cpuMs`/`wallMs`, `gc`, `jit` (`mainThread` — пауза кадра), `processBytes`; по периодам — `history`.
6. Что именно — `profile.stages {"events":true}` (дороже, для расследования) → `events`: `alloc.types`, `jit` (`tier0`/`tier1`/`osr`…),
   `gc`, `recent`; выключить — `{"events":false}`; без агента — `--profile runtime`. Стеки — `dotnet-trace collect -p <pid>` / PerfView.

**Код или машина.** Бюджет или бенч упал — сначала вердикт, потом код (пользователь иногда нагружает машину).
1. Где: `feature.verify` → `budgets[].machine`, `budgets[].frame.machine`; `experiment.sweep` → `rows[].machine`; `Sword.Headless --json` →
   `machine`; `Sword.exe --json` → `report.machine`; `profile.stages` → `machine`; журнал — `[bench] вердикт: …`.
2. `verdict`: `машина` — другие процессы заняли ≥ 25 % логических процессоров (`othersBusyPct`), или счётный поток был на CPU < 90 %
   (`threadOnCpuPct`), или частота ядра ниже 90 % базовой (`mhz.probe`, Release), или ОС ограничила частоту (`mhz.limit` < `mhz.limitBase`);
   `среда` — JIT на главном потоке или GC во время замера; `код` — время за код. Фоновый JIT — причина, вердикт не меняет.
3. Замер — один прогон: сомнительный результат (`машина`, провал предела, число у предела) не перемерять, а сообщить пользователю числа;
   предел бюджета не трогать. `среда` с JIT на главном потоке — разовая компиляция в замере (какая — аномалия тика); `код` → профайлер.
   `note` в `machine` — окно короче 0,5 с: загрузка по счётчикам ОС грубая (короче 0,1 с — в вердикт не идёт).

**Почему был медленный кадр или тик (захват аномалий).**
1. Включённый профайлер ловит сам: кадр дольше `медиана + max(2 мс, 0.75·медиана)`, тик — `медиана + max(1 мс, 0.75·медиана)` (медиана —
   128 последних); свой порог — `profile.stages {"threshold": 5}` (`0` — снова по медиане) или `--profile-threshold 5`; у Sword.exe — F2.
2. Где: `log.tail {"category":"profile"}`; `profile.stages {"anomalies": 20}` → `anomalies.list` (`ms`, `class`, `causes`, `zone`, `file`,
   `noticeable`), `byClass`. **Видел ли человек** — `noticeable` (кадр не короче интервала показа монитора): жалоба «дёрнулось» — сначала
   заметные. Кадр инструментов (`history[].tools` — агент выполнял запросы) не судится: мерить игру — между вызовами.
3. Класс: `jit` — компиляция на главном потоке (OSR долгого цикла → тело цикла в свой метод, `AGENTS.md` Runtime; все компиляции —
   `DOTNET_JitDisasmSummary=1 DOTNET_JitStdOutFile=<файл>`); `gc` — пауза сборки; `gpu` — `frame.fenceWait`/`frame.present` (секция `gpu`
   файла называет проходы); `machine` — поток сняли с процессора (сообщить пользователю, код не трогать); `code` — смотреть `mainZone`.
   «Главной зоны нет» (`zone: null`) — абсолютный порог ниже обычного периода.
4. Файл `anomaly-<frame|tick>-N.json`: `mainZone.path`, `zones`, `tree`, `header`, `recent` (32 периода до), `tick`, `events`
   (диагностический режим), `machine`. Картинкой — `trace` файла или `profile.stages {"trace":true}` / `--profile-trace` → https://ui.perfetto.dev.
5. Проверить исправление: тот же прогон — аномалии класса и зоны пропали, p99 ниже.

**Упор в CPU или GPU (GPU-время проходов).** GPU-время — всегда, без профайлера.
1. Бенч — `Sword.exe --units 32 --bench 10 --json` → `report.bench`: `averageMs`, `cpuMs`, `waitMs`, `gpuMs`/`gpuP99Ms`, `passes`; живая
   игра — `render.graph` (`passes[].gpuMs`, `gpu.frameMs`) или `profile.stages` → `gpu`.
2. **GPU** — `gpuMs` ≈ кадр, `waitMs` большие: смотреть проходы; **CPU** — `cpuMs` ≈ кадр, `waitMs` ≈ 0: зоны CPU; p99 ≈ `gpuP99Ms` —
   рывки GPU. GPU-время в кадре N — кадра N − 3 (`lagFrames`; у `capture.bundle` и `--json` — 0); сравнивать прогоны одной машины подряд.

**Понять, почему поле такое.** Режим причинности: `session.start {"causality":true}` (или сервер с `--causality`; хеш и реплей не меняет),
воспроизвести → `state.why {"entity":"player","field":"Health.Hp"}`: последнее изменение (тик, `Set`/`Add`/`Remove`, система, `source`,
`before`/`after`) и `chain` до корня (попадание ← касание ← начало удара ← решение ИИ или команда); `summary` — строкой; смерть —
`{"field":"Dead"}`; `changes: N`; `events.history` в режиме даёт `cause`. Непрерывное поле (`Transform`) — `writers`, дальше
`ecs.componentUsage`, `ecs.explainSystem`, `watch.add`, `time.break`. `complete: false` — родитель вытеснен из кольца: смотреть ближе.
Почему случилось событие (переход Motion Matching, попадание) — `state.why {"cause":N}`: `N` — `cause.id` (или `cause.parent`) строки
`events.history` в режиме; цепочка записи до корня, `summary`.

**Посмотреть на кадр живой игры.** `time.pause` (кадр показывает ровно последний тик) → `capture.bundle {"name":"look"}` → `color` и
`depth`; `entities[].rect` — где кто. `render.graph` — проходы кадра и их GPU-время.

**ID-буфер и оверлей капсул.** Чья часть на пикселе, стоят ли капсулы на персонаже: `time.pause` → `capture.bundle
{"name":"ids","ids":true,"overlays":["capsules"]}`.
- `ids.png` — ID-буфер для глаз; `ids.raw` — коды (uint32 на пиксель, строки сверху вниз); `ids.legend` — `entity`, `name`, `segment`,
  `title` («левое предплечье врага 3», «голова трупа врага 1»), `pixels`, `rect`. Код = (номер сущности + 1) << 8 | сегмент; 0 — ничего.
- `overlays.capsules` — снимок с капсулами тел и подписями бойцов; `color` — без них. Пусто в легенде у видимого бойца — ошибка рендера;
  `notes` — что заказано, но не снято. Капсулы оверлея — «как показан боец»: сверять числа с `physics.bodies` — на паузе (`alpha` 1). В
  видах фичи — `views[].overlays: ["capsules"]` (`Sword.exe --overlay capsules`).

**Длинный прогон и страницы.** `time.runUntil {"tick":N,"timeoutSeconds":120}` (в живой игре — не больше 30; таймаут не отменяет прогон,
`time.pause` останавливает). `world.query` — не больше 100 сущностей: `receipt.truncated` и `continuationToken` (`skip=N`); `log.tail`
без `after` — последние записи по фильтру (`level`, `category`), дальше — `after` = `last`.

## Рэгдолл

Фича [«Рэгдолл»](../game/Sword.Game/Features/Ragdoll/README.md), физика — [README](../engine/Sword.Engine.Physics/README.md) подсистемы.

**Команды сцен и привязка скина.** `SceneCommand` (Intent того же тика; в реплее):
1. Поставить бойца места в точку курсом (постройка заново, мёртвый оживает): `command.send {"command":"SceneCommand",
   "fields":{"Action":"Place","Slot":1,"Position":{"X":-13,"Y":0},"Facing":1.5708}}` (`Position.Y` — это z).
2. Убить без удара: `{"Action":"Kill","Slot":1}` → `entity.get {"entity":"enemy 1","components":["Dead"]}` — `Cause: Command`, `Time`.
3. Сдвинуть тела трупа без скорости (тиком позже смерти) — `{"Action":"ShiftBodies","Slot":2,"Offset":{"X":0.2,"Y":0,"Z":0}}`; меч места —
   `{"Action":"Weapon","Slot":3,"Weapon":0}`; намерение игрока — `{"Action":"PlayerIntent","Velocity":{"X":0,"Y":4.2},"Facing":0}`, `"repeat":360`.
4. Привязка скина мёртвого — `physics.bodies {"entity":"enemy 1"}` → `skin`: `captured`, `time`, `joints`, `drift` (насколько скин ушёл от
   позы смерти); матрицы — `state.describe {"part":"skin"}`; нет привязки — нарушение `ragdoll.skin`.

**Труп на экране и трасса физики.**
1. Скин и тела: `physics.bodies {"entity":"enemy 1"}` — `skin.drift`; картинкой — `capture.bundle {"ids":true,"overlays":["capsules"]}`:
   меш трупа на капсулах, «голова трупа врага 1», рамка трупа — по телам; кресты тел на физике (зелёный — на земле, синий — спит).
2. Суставы: `physics.bodies {"entity":"enemy 1"}` → `joints` (`list`: родитель, ребёнок, `enabled` — у запястья false, `pointError`,
   `excess`; `excessMax`); картинкой — `capture.bundle {"overlays":["joints"]}`. У живых суставов нет (`joints: null`).
3. Трасса — диагностический режим (мир, хеш, реплей не меняет): `physics.bodies {"trace":"on"}` → тики → `physics.bodies
   {"trace":"show","events":16}` — `trace.last`, `trace.window` (с `reset`), `trace.run`: `speed.max` и чьё тело, `speed.p99`,
   `angularSpeed`, `capped`, `nonFinite`, `sources` (`ground`, `wall`, `pair`, `joints`, `push.*`: сдвиг / h, `hidden` — должен быть 0,
   псевдоимпульс, глубины), `energy`, `sleep`, `top` и `recent` (события быстрее 12 м/с). Окно: `{"trace":"reset"}` → тики → `show`;
   выключить — `{"trace":"off"}`. В Headless включённая трасса переживает `session.start` и перемотку.
4. Контакты последнего подшага: трасса включена (п. 3 или K) → `capture.bundle {"overlays":["contacts"]}` (зелёный — земля, синий — стена
   и колонны, пурпурный — пара трупов, жёлтый — части одного трупа, оранжевый/красный — толчок телом/клинком); выключена — слой пуст, в `notes` — почему. Числа —
   `trace.contacts`. `Sword.exe --overlay contacts` включает трассу с первого кадра (и при `--replay`).
5. F3 «Рэгдолл» человеку — агенту те же ручки командой и те же числа: мёртвые — `world.query {"with":["Dead"],"take":0}`, быстрейшее —
   `trace` (`speed.max`, `capped`), покой — `Body.ComVelocity`; вкладки — итог `Sword.exe --json` (`panels.tuning`).

**Труп на физике.**
1. Переход: `command.send {"command":"SceneCommand","fields":{"Action":"Kill","Slot":1}}` → `time.step 1` → `physics.bodies
   {"entity":"enemy 1"}` — у 14 тел `mode: simulated`, `profile: 1`, `joints` — 13, у запястья `enabled: false`; смерть от удара — так же
   (переход — в Settle). Системы — `ecs.explainSystem {"name":"core.death-to-ragdoll"}`.
2. «Играть, пока труп не ляжет»: `time.runUntil {"condition":{"all":[{"entity":"enemy 1"},{"component":"Dead"},{"field":"Body.KineticEnergy","lessThan":0.05}]},"maxTicks":1200}`.
   По тикам — `watch.add {"entities":["enemy 1"],"fields":["Body.ComVelocity","Body.KineticEnergy","Body.Energy","Body.Com.Y"]}` →
   `entity.history` (труп лёг: `Body.ComVelocity` ≤ 0,05 м/с, `Body.KineticEnergy` — сотые доли джоуля и убывает); те же поля — в
   `replay.observe` и манифестах.
3. Инварианты трупа — `invariant.list`: `physics.modes`, `physics.simulatedSpeed`, `physics.joints`, `physics.ground`, `physics.inArena`
   (после Physics). Диагностические `physics.energy`, `physics.pushes` читают трассу: `physics.bodies {"trace":"on"}` и `invariant.enable
   {"ids":["physics.energy","physics.pushes"],"enabled":true}`; встать — `time.breakOnInvariant {"ids":["physics.energy"]}`, затем
   `trace` → `energy`. Нечисловой труп строится заново живым в точке корня (`core.body-rebuild`; `physics.finite` — нарушение на тике).
4. Хеш: `state.describe {"part":"physics","filter":"1v1"}` — `jointCount`, `jointN` (вид, пределы, `enabled`, `measure`), `reaction`,
   `asleep`, `restTime`; `{"part":"physics","filter":"passes"}` — проходы пар.
5. Ручки: `command.send {"command":"TuningCommand","fields":{"Knob":"ragdoll.popSpeed","Value":3}}` — шаг этого тика уже по новым;
   перебор — `experiment.sweep` по `ragdoll.*` на реплее со смертями.
6. Сцены: s8 — `Place` врага на ступени пирамиды (`{"X":-16,"Y":0}`), тиком позже `Kill`; s4 — два `Place` в (±0,36; 1,5), `Kill` обоим,
   тиком позже `ShiftBodies` второго на `{"X":-0.52,"Y":0,"Z":0}`; приёмка — `feature.verify {"id":"ragdoll"}`.

**Толчки живых и удары по трупам.**
1. Проход сквозь труп (s3): `Place` врага в `{"X":0,"Y":4}`, тиком позже `Kill`, через 2 с — `PlayerIntent`
   `{"Velocity":{"X":0,"Y":4.2},"Facing":0}` с `"repeat":360`; `watch.add {"entities":["player"],"fields":["RootVelocity.Value.Z","Recoil.Velocity.Z","Recoil.Tick"]}`
   и `{"entities":["enemy 1"],"fields":["Body.ComVelocity"]}` → `entity.history`: скорость корня у трупа падает на сотые доли.
2. Толчки по трассе: `trace` → `sources` `push.body`/`push.pass` (импульс), доли бюджетов пары и прохода, выталкивание сверх толкающей
   точки (≤ `PopSpeed`), отдача; картинкой — `contacts`.
3. Метки и проходы: `state.describe {"part":"physics","filter":"<сочленение из BodyRef>"}` — у тела `source` (`Pass` — клинок в ударе),
   `reaction`; `{"filter":"passes"}` — проходы (удар, номер, открыт, бюджет, остаток).
4. Удар по трупу (s1): `Kill` врага рядом, `PlayerCommand {"Attack":true,"Gesture":{"X":1,"Y":-1}}` — `events.history {"types":["Hit"]}`
   (`Corpse: true`), `entity.get {"entity":"enemy 1","components":["HitImpulse","Health"]}`, `ArenaStats.CorpseHits`. Почему дёрнулся —
   `state.why {"entity":"enemy 1","field":"HitImpulse"}` (с `causality`); отдача — `state.why {"entity":"player","field":"Recoil"}`.
5. Инварианты `physics.sources`, `ragdoll.hitSource`, `ragdoll.hitImpulse`, диагностический `physics.pushes`. Ручки отдачи —
   `ragdoll.playerRecoil`, `ragdoll.enemyRecoil`, `ragdoll.recoilMaxSpeed`. Без сеанса: `Sword.Headless --replay
   artifacts/features/ragdoll/scenario-s3.swreplay --set ragdoll.playerRecoil=4 --sample 1 --json` — `trajectory[].speed` игрока.

**Скорость и покой физики трупа.**
1. Где время шага: `profile.stages {"enable":true,"reset":true}` → тики с трупами → `profile.stages {"top":40}` — зоны `physics.*` под
   `core.physics-step@Physics` (фазы подшага и сон; README физики, «Порядок шага»); прогрев шага — `log.tail` → `[physics] прогрев …`.
2. Откуда энергия: `trace` → `energy` (`last`, `window`, `run`): `growth` — труп без толчков и без контакта с трупом (накачка — около 0),
   `paired`/`pairedBody` — у трупа в контакте с трупом, `pairedTotal` — сумма касавшихся (допуски `physics.energy` — 12,5 и 25 Дж),
   `phases` — `integrate`, `joints`, `world`, `pairs`, `pressed`, `velocity`, `contactVelocity`, `relax`; нарушение пишет те же фазы.
3. Глубина в земле: `time.breakOnInvariant {"ids":["physics.ground"]}` → `physics.bodies {"entity":"<чьё тело>"}` (`grounded`, концы) и
   `trace.contacts` (`ground`/`pair`/`push.*`: глубина и решённая) — кто держит тело в земле.

**Сон трупа.**
1. Кто спит: `physics.bodies {"entity":"enemy 3"}` → `asleep`, `restTime`; ждать — `time.runUntil {"condition":{"all":[{"entity":"enemy 3"},{"field":"Body.Asleep","equals":1}]}}`;
   по тикам — `watch.add` на `Body.Asleep`, `Body.RestTime` (не в `world.query`); сколько спит — `state.describe {"part":"physics"}`.
2. Кто будил: `trace` → `sleep`: `sleeps`, `asleepMax`, `contacts` (касаний спящего — опора), `wakesBy` — `push` (живой глубже 1 см или
   быстрее 0,5 м/с), `hit`, `touch` (труп быстрее 0,5 м/с или падающий), `chain` (лежащие на разбуженном и на уведённом теле), `command`,
   `settings`. Висит ли кто-то: `Body.Com.Y` лежащего на земле ~0,15…0,2 м (сценарий `sleep-support`).
3. Инварианты `physics.sleep`, `physics.sleepContacts`; цена — зона `physics.sleep`. Ручки: `command.send {"command":"TuningCommand",
   "fields":{"Knob":"ragdoll.sleepSpeed","Value":0}}` — сна нет (`ragdoll.sleepBodySpeed`, `ragdoll.sleepTime`; F3 — «СОН ТРУПА», F7 —
   синий крест). Приёмка — сценарии `sleep-*`, `s3-asleep`, `s1-asleep` манифеста `ragdoll`.

**Приёмка «Рэгдолла» и цена трупов.**
1. `feature.verify {"id":"ragdoll"}` — 23 сценария, 36 метрик (`value` против `expected`), 8 видов (капсулы, суставы, контакты), бюджеты
   `corpses32` (тик, Physics, 0 Б/тик, кадр App) и `corpses32-overlays` (кадр с оверлеями); время и кадр судятся только в Release.
2. Цена по числу трупов: `experiment.sweep {"params":{"arena.enemies":[0,8,16,32],"sim.invariants":[false]},"replay":"<артефакты
   ragdoll>/scenario-crowd32-budget.swreplay","warmup":602,"repeats":5}` (Release; прогрев — за тиком массовой смерти 600) → `rows[].stages.Physics`
   на труп; `rows[].systems` — `core.physics-step@Physics`; вердикт машины — `rows[].machine`.
3. Энергия трупов в бою: `session.start {"replay":"<артефакты combat>/scenario-crowd.swreplay"}` → `physics.bodies {"trace":"on"}` →
   `time.step {"ticks":2400}` → `physics.bodies {"trace":"show"}` → `trace.run.energy` (`growth` — одиночный труп, допуск 12,5 Дж; `pairedTotal`
   — сумма касавшихся, 25), `speed.p99`, `capped`.

**Самоколлизия трупа.**
1. Часть в своём теле: `physics.bodies {"entity":"enemy 1"}` → `self` — `pairs` (76 у трупа), `active`, `scale` (ручка `ragdoll.selfCollision`),
   `touching`, `deepest` (`a`, `b`, `depth`, м); по тикам — `watch.add {"entities":["enemy 1"],"fields":["Body.SelfDepth"]}` → `entity.history`;
   ждать — `time.runUntil {"condition":{"all":[{"entity":"enemy 1"},{"field":"Body.SelfDepth","greaterThan":0.06}]}}`. Таблица пар —
   `state.describe {"part":"physics","filter":"<сочленение>"}` → `selfContactCount`, `selfContacts` («a-b» по номерам тел = сегментам).
2. Инвариант `physics.selfContacts` (часть глубже 6 см — только выходя); встать — `time.breakOnInvariant {"ids":["physics.selfContacts"]}`.
   Трасса: `trace` → `sources` `self` (поправки), `trace.contacts` (`self`: глубина и решённая); картинкой — `contacts` (жёлтый); цена — зона
   `physics.self` (в `physics.pairs`).
3. «До/после»: `command.send {"command":"TuningCommand","fields":{"Knob":"ragdoll.selfCollision","Value":0}}` — самоколлизии нет (труп
   складывается сам в себя); сценарии `self-fold`, `self-fold-off` и виды `fold-before`, `fold-after`, `fold-after-mesh` манифеста `ragdoll`.
   Трение суставов — ручка `ragdoll.jointFriction` (1/с; рост энергии по фазам трассы — `relax`), жёсткость — `ragdoll.jointCompliance`.

## Motion Matching игрока

Модуль Locomotion — [README](../game/modules/Sword.Game.Locomotion/README.md) («Motion Matching игрока в тике», «Агенту и на экран»).
Игрок на MM — при `locomotion.player=mm` (`sword.json` игры и Headless со `sword.json`; тесты, сценарии фич и эталоны — `clips`).

**Проверить MM игрока.**
1. Что играет: `anim.pose {"entity":"player"}` → `motionMatching` — клип с именем и кадр в клипе, доля, темп, `searching` (под рывком —
   false), поисков, смен, переходов, `feetOnGround` (стопы на земле по разметке кадра); `lastSearch` — тик, повод (`Start`, `Schedule`,
   `GoalJump`, `AfterAction`), текущий и найденный кадр, цена и цена продолжения, 12 групп по именам (какая группа дорогая — туда смотреть),
   `microseconds`; `desired`/`matched` — траектории запроса и найденного кадра (`[t, x, z, курс]`, в мире по корню поиска, в единицах
   данных); `history`; отпечаток базы. По тикам — `watch.add {"entities":["player"],"fields":["MotionMatchState.Clip","MotionMatchState.Rate"]}`.
2. Переходы: `events.history {"types":["MotionTransition"]}` — `Kind` (`Search` — сменой кадра поиском, `Edge` — ребро графа), `Reason`,
   `From`/`To` (кадры базы) и клипы, `Cost`, `ContinuationCost`. Почему: режим причинности (`session.start {"causality":true}`) → у строки
   `cause.id` → `state.why {"cause":<id>}` — `MotionTransition ← решение «MM: поиск — скачок цели скорости»` (корень).
3. Картинкой: `time.pause` → `capture.bundle {"overlays":["mm"]}` (живая игра): голубая — запрос последнего поиска, оранжевая — траектория
   найденного кадра, серая — история корня, зелёные шарики — стопы на земле; игрок не на MM — слой пуст, в `notes` — почему. В игре — M;
   в видах фичи — `views[].overlays: ["mm"]` (`Sword.exe --overlay mm`). Числа F3 «Локомоция» — те же, что `anim.pose`.
4. Скольжение и скачок: поля `MotionMatch.FootSlide` (м/с, NaN — стопы не на земле), `MotionMatch.JointJump` (м за тик),
   `MotionMatch.FeetOnGround` — в `watch.add` → `entity.history`, `replay.observe`, условиях; метрика манифеста — `{"entity":"player",
   "field":"MotionMatch.FootSlide","stat":"p95","skipUndefined":true,…}` (`mean`, `p95`, `p99`, `max`).
5. Инварианты: `invariant.list {"filter":"locomotion"}` — `locomotion.mm` (каждый тик), тяжёлые `locomotion.mmDecay` (смещения
   инерциализации не растут без перехода) и `locomotion.mmContinuity` (скачок сустава за тик ≤ 0,25 м); встать на нарушении —
   `time.breakOnInvariant {"ids":["locomotion.mmContinuity"]}` → `anim.pose` и `state.describe {"part":"locomotion","filter":"0v1:—"}`
   (проигрыватель, история, `lastSearch`; `"0v1:hand"` — смещения суставов).
6. Цена поиска: `profile.stages {"enable":true,"reset":true}` → тики → `profile.stages {"prefix":"locomotion"}` — зона `locomotion.search`
   (под `locomotion.mm@Animation`).
7. MM против клипов: `experiment.fork {"replay":"session","at":0,"variants":[{"name":"clips","set":["locomotion.player=clips"]}],
   "measure":[{"entity":"player","field":"Transform.Position"},{"entity":"player","field":"Body.foot.l.Position.X"}]}` (у клипов — другие
   участники хеша `ecs`, `physics`, это ожидаемо; корень тот же, поза — другая); ручки — `command.send {"command":"TuningCommand","fields":{"Knob":"locomotion.switchGain","Value":1}}`
   (`searchPeriod`, `inertialHalflife`, `rateRange`, `weight…`), перебор — `experiment.sweep {"params":{"locomotion.searchPeriod":[0.05,0.1,0.2],
   "sim.invariants":[false]},"replay":"<artifacts/features/locomotion>/scenario-run8.swreplay"}` (время — в Release; качество — `replay.observe`
   записи сценария с `set` и полями `MotionMatch.*`).
8. Реплей с другой базой (контент, `locomotion.clips`, процессор без AVX2): `replay.check` — в `warnings` `сведения реплея:
   locomotion.base: в реплее …, в симуляции …`, в `divergence.cause` — «вероятная причина»; отпечаток — `state.describe {"part":"locomotion",
   "filter":"base"}`, строка старта — `log.tail {"category":"locomotion"}`.
9. Рука с мечом: `entity.get {"entity":"player","components":["MotionMatchArm","FighterStartPose"]}` — поза стойки, её клип,
   действующий вес (к ручке не быстрее 4/с), слой позы постройки (`Layer*`); кисть относительно груди — `anim.pose {"entity":"player",
   "joints":["hand_r","spine_05"]}`; вес — ручка `locomotion.armWeight` (`command.send {"command":"TuningCommand","fields":{"Knob":
   "locomotion.armWeight","Value":0}}` — рука клипа); инвариант `locomotion.mmArm`. Замеры `MotionMatch.*` руку не видят (выход MM).
10. Приёмка: `feature.verify {"id":"locomotion"}` (Headless `--feature locomotion`) — 12 сценариев (`set` — `locomotion.player=mm`), 60 метрик
    (скольжение, скачок, переходов и поисков по сценариям), 3 вида с оверлеем `mm`, бюджеты `run` и `crowd` с пиком поиска (`zones`); записи
    сценариев — `artifacts/features/locomotion/scenario-*.swreplay`. В рабочем дереве сервер MCP сеанса — из основного дерева: свою сборку
    Headless — командной строкой или клиентом MCP по stdio (`Sword.Headless.exe --agent --idle-exit 0`, строка JSON-RPC на сообщение).

## Пекарня

Проект — [Bake/](../Bake/README.md), ядро — [tools/Sword.Bake](../tools/Sword.Bake/README.md), плагины — [Engine](../tools/Sword.Bake.Engine/README.md),
[Game](../tools/Sword.Bake.Game/README.md). Пекарня — процесс командной строки, агентский вид — `--json`; инструмента MCP нет.
Консоль — `tools/Sword.Bake.Cli/bin/Debug/net10.0-windows10.0.22000.0/sword-bake.exe` (плагины той же конфигурации собирает
`dotnet build SWORD.slnx`); запускать в `Bake/` (проект ищется от текущего каталога вверх, иначе `--project КАТАЛОГ`).

**Перепечь, сверить с эталоном, отладить.**
1. `sword-bake status --json` — `assets[]` (`status` `upToDate`/`dirty`/`noSource`/`error`, `reason`), `project.problems` (плагины,
   корни, импорты; «ссылается на плагин X, а стоит в plugins раньше него» — порядок), `project.plugins[].loaded`, `project.broken`,
   `pak`; код 0 — актуален, 2 — нет. Корни машины — `Bake/bake.user.json` (вне git) или `BAKE_ROOT_*`.
2. `sword-bake build [шаблоны] --json` (`anim/gasp`, `anim`, `processor:anim.pack`) — `assets[].result`, `reason`, `error` и `log`
   упавших, `pakWritten`, `pak.status`; ошибка любого ассета — пак не записан, прежний цел. Полная выпечка — Debug ~31 с, Release
   ~9 с. Ход выпечки (корень, пальцы, FK, «записано / без изменений», слои текстур) — лог `build` текстом. Правка кода выпечки
   клипа — поднять `AnimImport.ToolVersion`, иначе клипы не перепекутся.
3. `sword-bake pak --verify` (код 2 — нарушена целостность), `which ПУТЬ` (кто пишет файл), `list --content`.
4. Сверка: `sword-bake diff ..\Content.reference.swpak ..\Content.swpak --expect reference.expect.json --json` —
   `summary` (`same`, `sameContent`, `unexpected`), `unexpected[]` (`source` `file`: `path`, `kind` `onlyA`/`onlyB`/`size`/`hash`,
   `detail` сравнителя или «отличаются байтами»; `buildInfo`: ассет и поле `.bake/build.json`), `expectations.unused`; код 0 —
   неожиданных нет, 2 — есть. Сейчас: 22 равны байтами, 1048 клипов — по содержимому (без `SourceHash`), 24 ожидаемых (оружие — новый хват), 0 неожиданных.
   Вне проекта `diff` не видит сравнитель `.swclip` (`project: null`) — клипы «отличаются байтами».
5. Выпечка без проекта — тесты Source: `dotnet test tests/Sword.Bake.Engine.Tests --filter Category=Source` (и
   `tests/Sword.Bake.Game.Tests`), итог — `artifacts/bake/source-check.json`; писатели форматов —
   `dotnet test tests/Sword.Engine.Formats.Tests --filter ReferencePakWriters`; исходники — `sword-bake inspect rawpose DIR --clip ИМЯ`,
   `inspect meshinfo GLB`.
6. Пак пекарни — пак игры `Content.swpak` в корне: его читают игра, Headless, тесты Content, сценарии фич и эталоны; хеш
   контента — без `.bake/`, перевыпечка тех же файлов его не меняет. Нет пака — игра берёт эталонный с предупреждением
   `content` в журнале (`log.tail`), эталоны отвергаются по хешу; шаг `content` в `verify.ps1` — ошибка с командой выпечки.
   Рабочее дерево агента — свой пак (`sword-bake build` здесь; ссылка на пак основного дерева для `status` устарела: MVID
   зависит от пути дерева). Эталонный пак в игре — `SWORD_CONTENT=<путь пака>` (эталоны с ним не сверяются). Перепечённый
   контент изменил хеш — эталоны перезаписать: `Sword.Headless --replay F --record F` (на другом паке — с предупреждением,
   хеши сверяются тик в тик; на эталонном паке вместо пака игры — отказ).
7. Studio глазами (выходы, форма, предпросмотры) — `tools/Sword.Bake.Studio/bin/Debug/net10.0-windows10.0.22000.0/Sword.Bake.Studio.exe
   --project ПРОЕКТ --gallery КАТАЛОГ` (без фокуса; `NN-preview-<обработчик>.png` — предпросмотр каждого обработчика) или
   `--shot ФАЙЛ --select ID --tab 3 --preview-mode N`; в консоль — лог модели. Эталонный пак — только копией (слежение
   собирает устаревшее). Без окна — `dotnet test tests/Sword.Bake.Studio.Tests` ([README](../tools/Sword.Bake.Studio/README.md)).

## Если что-то не так

- «симуляция не дошла до точки обслуживания» — кадр живой игры завис (или Headless остановлен); запрос не исполнен — повторить. В свёрнутом
  окне тики живой игры не идут; проверочный прогон (`--frames`, `--bench`) рисует и свёрнутым, кадров нет 60 с (`--stall N`) — код 5. Без
  захвата окна — `Sword.exe --windowed 640x480 --no-activate --minimized --frames 60 --json`.
- Неизвестный аргумент — ошибка со списком аргументов; незаданный аргумент пропускается, а не передаётся `null`.
- Живая игра перезапущена — прежний сеанс незнаком (404): начать сеанс заново или переподключить `sword-app` в `/mcp`. Вызов по HTTP — не
  дольше 35 с: долгое ожидание — `time.runUntil` с `timeoutSeconds` до 30 и повтором.
- Кадр живой игры на ходу интерполирован (`alpha` < 1): для точного кадра — `time.pause`. «тик уже прошёл» — команды только на будущий тик; без `tick` — на ближайший. Имя компонента неоднозначно — полное имя из `world.types`
  (`stableName`). Ошибки движка — `log.tail {"level":"warning"}`.
- **Процессы агентских серверов.** Каждый сеанс Claude Code (`claude.exe`) и поток Codex держат свой `Sword.Headless --agent` (~850 МБ;
  подагенты — сервер сеанса); сервер выходит сам по концу stdin, концу обёртки `cmd` и после 15 мин без вызовов, Sword.exe видов умирает с
  ним. Посмотреть: `Get-CimInstance Win32_Process -Filter "Name='Sword.Headless.exe'" | Select ProcessId,ParentProcessId,CreationDate,WorkingSetSize`.
  **Чужие не завершать** — только без живого родителя-клиента или свои. Узлы MSBuild и `VBCSCompiler` выходят сами; `dotnet build-server
  shutdown` не звать, пока работают другие сеансы.
