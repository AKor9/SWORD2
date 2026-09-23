# Враги и ИИ (этап 4) — точка 4а: спаун и команды арены

Враги на арене: при старте — `arena.enemies` врагов кольцом вокруг игрока, лицом к нему; клавиши PageUp/PageDown —
врагов на одного больше или меньше, R — сброс арены. Враг — такой же боец, как игрок (корень, намерение, поза, локомоция
клипами), со своим местом 1…32 и зерном. Врагам пока никто не пишет намерение — они стоят в стойке (ИИ — точка 4б).
Часть ядра игры (`GameCore`): арена без врагов — тоже игра, но врагов не бывает без ядра, поэтому это не модуль. Код —
`game/Sword.Game/Arena/` (`ArenaSpawn.cs`, `ArenaCommandSystem.cs`, `ArenaInvariants.cs`), `game/Sword.Game/GameCore.cs`,
контракты — `game/Sword.Game.Contracts/Components.cs`, клавиши — `game/Sword.Game.Client/ArenaInput.cs`.

Точки этапа 4 — таблица раздела 13 [плана](../../../../ARCHITECTURE_PLAN.md); фича `enemies` принимается целиком на 4д.
Сделано: 4а.

## Откуда логика

SWORD 2eab13b, `Gameplay/Ragdoll/DuelArena.cs`, `Fighter.cs`, `Program.cs`. Формулы и константы те же:

| SWORD | SWORD2 |
|---|---|
| `DuelArena.MaxFighters = 33`; `EnemyCount` (0…32) | `ArenaRules.MaxFighters`, `ArenaState.MaxEnemies`; ресурс `ArenaState.Enemies` (хешируется) |
| `_fighters[i]`, `Fighter.Index`: игрок 0, враги 1…32 — порядок обхода | компонент `FighterSlot` (игрок 0, враги 1…32), метки `PlayerTag`/`EnemyTag` |
| `Reset(enemies)`: `Build(Player, 0, 0)`, затем `Spawn` всех мест | сборка мира: `GameCore.SpawnArena`; команда `ArenaAction.Reset` — игрок и все враги строятся заново |
| `SetEnemyCount(n)`: `Math.Clamp(n, 0, MaxFighters − 1)`; места old+1…n — `Spawn`; меньше — последние места неактивны | `ArenaAction.ChangeEnemies`/`SetEnemies` → `ArenaCommandSystem` (Intent): новые места — новые сущности, ушедшие — сущности снимаются |
| `Spawn`: a = rng·τ, r = 5 + rng·4 вокруг таза игрока; дальше `ArenaRadius − 3` — на этот радиус; facing = `atan2(p.X − pos.X, p.Z − pos.Z)` | `ArenaRules.Enemy` — вокруг корня игрока (решено при разбивке этапа 4: тел до этапа 5 нет) |
| `Fighter.Build`: `Seed = rng.NextSingle()`, `Facing = FacingGoal = facing`, скорость 0, `ResetAnimator` | `FighterSeed`, `Fighters.Create`/`Rebuild`: намерение — стоять курсом постройки; локомоция — со стойки |
| `Random(7)` — один поток на угол, радиус, зерно, таймер удара, оружие | счётчиковый ГСЧ SWORD2: поток `arena.spawn`, выборка — по месту и «партии» постройки (0 — сборка мира, дальше — номер команды тика) |
| `Program.cs`: `Units = 10` (`--units`, clamp 0…32); PageUp → +1, PageDown → −1, R → `Reset(EnemyCount)` — прямо из цикла кадра | `sword.json` `arena.enemies = 10`, `--units N` (App и Headless); клавиши — `ArenaCommand` из фронтов ввода (ровно одним тиком), в реплее — готовой командой |
| `SkinnedCharacters.EnemyColour(i)` | `ArenaClient.EnemyColour(slot)` |

Намеренные отличия:
- **Расстановка не та же, что в SWORD** — другой ГСЧ (решено при разбивке этапа 4); сверка толпы с SWORD — от одной
  расстановки (4д).
- **Центр спауна — корень игрока, а не таз его тела** (сантиметры; тел до этапа 5 нет — вернуть таз или записать
  почему нет на этапе 5).
- **Неактивных мест нет**: SWORD держит 32 бойца всегда и перестраивает все места при сбросе; здесь у неактивного места
  нет сущности. Видимое поведение то же: `SetEnemyCount` строит прибавленные места заново, а неактивное место не видно.
- **Значение в коде `arena.enemies` — 0** (пустая арена), 10 — в `sword.json`: мир без `sword.json` (тесты, сценарии
  фич, реплеи, записанные до 4а, — в их конфиге секции `arena` нет) остаётся прежним; игра и Headless с `sword.json`
  стартуют с 10, как `Units` SWORD.
- **Сброс строит бойца заново на той же сущности** (`Fighters.Rebuild`): как `Fighter.Build` SWORD над тем же объектом
  места — номер сущности и имя у агента не меняются (условия и наблюдения по `player` и `enemy N` переживают R).
  Протокол: метка `Fighter` снимается и ставится на одном барьере — `Removed<Fighter>` и `Added<Fighter>`; модули заводят
  по `Added<Fighter>` своё состояние бойца с нуля (Locomotion — со стойки, как `ResetAnimator`).
- **Число врагов за пределами 0…32 в конфиге — ошибка запуска** (как `sim.tickRate`), а `--units` и команды упираются
  в предел, как в SWORD.

## Устройство

- `ArenaCommand` (`Game.Contracts`): `Action` — `ChangeEnemies` (на `Enemies` больше, отрицательное — меньше),
  `SetEnemies` (ровно `Enemies`), `Reset`; `None` и неизвестное — пропускаются. Зарегистрирована ядром
  (`Command<ArenaCommand>`), поэтому попадает в реплей и в `command.send`.
- `ArenaCommandSystem` (Intent, реактивная по `Event<ArenaCommand>`, до `core.player-command`): команды тика по порядку
  сводятся в итог — число врагов, какие места строятся заново и вокруг какого центра (до сброса — корень игрока, после —
  центр арены), сброшен ли игрок; затем мир меняется один раз: новые места — `Fighters.Create` (черновик), ушедшие —
  `Destroy`, оставшиеся с постройкой — `Fighters.Rebuild`, игрок при сбросе — `Rebuild` в (0, 0) курсом 0 с командой
  игрока этого тика (в SWORD `Reset` — до `PlayerController.Think`) и прежней точкой взгляда (`_cursor` контроллера
  SWORD сброс не трогает). Место, прибавленное и убавленное в одном тике, сущности не получает.
- `FighterSetupSystem` (Gameplay, реактивная по `Added<Fighter>`): новому бойцу — поза в пуле (`PoseRef` с пустым
  хэндлом — команда может не сыграть, а поза из пула не вернуться; поза берётся, когда боец уже в мире, и пишется в
  колонку на месте). Боец, добавленный после Gameplay, получит позу в следующем тике (`NextTickOk`); до того итоговую
  позу ему не считают.
- Ушедший враг: `Removed<Fighter>` в экспорте тика (клиент убирает его из кадра), `Removed<PoseRef>` →
  `PoseReleaseSystem` (End) возвращает итоговую позу, `Removed<LegacyLocomotionState>` → `locomotion.release` — позы
  слоя и снимка.
- Инвариант `arena.slots` (ядро, после End, каждый тик): бойцов 1 + `ArenaState.Enemies`, места 0…n без дыр и
  повторов, место 0 — игрок, 1…n — враги, у каждого бойца место есть.
- Клиент: действия `enemies_more` (PageUp), `enemies_fewer` (PageDown), `arena_reset` (R) — секция `input`;
  `ArenaIntent.Commands` превращает фронты тика в команды в порядке SWORD (PageUp, PageDown, R); при `Sword.exe --replay`
  клиент своих команд не ставит. Цвет врага — по месту (`EnemyColour` SWORD); справка HUD — строка
  «PgUp / PgDn — врагов ±1    R — сброс арены».

## Настройки

| ключ | по умолчанию | что |
|---|---|---|
| `arena.enemies` | 0 в коде, 10 в `sword.json` | врагов при сборке мира, 0…32 |
| `--units N` (Sword.exe, Sword.Headless) | — | то же, что `--set arena.enemies=N`, с пределом 0…32 как `--units` SWORD |
| `input.enemies_more`, `input.enemies_fewer`, `input.arena_reset` | PageUp, PageDown, R | клавиши команд арены |

## Зависимости

`Game.Contracts`, `Sword.Engine.Ecs`, `Sword.Engine.Runtime` (`SimRandom`, `SimTime`, конфиг, инварианты),
`Sword.Engine.Animation` (пул поз), `Sword.Game.Arena.ArenaLayout` (радиус арены), `Sword.Game.Movement`
(`PlayerCommandSystem.Apply` — команда игрока после сброса). Локомоция врагов — модуль Locomotion (без модуля враги
стоят в позе привязки, как и игрок).

## Ограничения (намеренные, до своих точек)

- ИИ нет (4б): врагам никто не пишет `FighterCommand`, они стоят курсом постройки. Столкновений корней бойцов между
  собой нет (4в): игрок проходит сквозь врагов. Поворота на месте клипом нет (4г).
- Удара, блока, таймера удара (`EnemyAi.Built`), оружия врагов, смерти и возрождения нет (этап 6).
- Условие агента, в котором сущность названа именем (`enemy 5`), разрешает имя один раз: после PageDown и PageUp того же
  места враг — новая сущность, и условие смотрит на ушедшую. Сброс (R) сущности не меняет. Надёжно — по месту полем:
  `{"exists":{"all":[{"component":"EnemyTag"},{"field":"FighterSlot.Index","equals":5},…]}}`.

## Эталоны и хеши

Эталоны SWORD2 `tests/reference/replays/prototype.swreplay` и `animation.swreplay` **перезаписаны** теми же командами
(`Sword.Headless --replay F --record F`) — намеренное изменение состава мира, не поведения. До перезаписи сборка 4а
воспроизводила их так: участник `animation` (пул поз) совпал **на каждом тике** (571 и 751 хеш), траектория игрока
(`--sample 1`: корень, курс, скорость каждого тика), поза и итог — те же, что у сборки до 4а; расходится только участник
`ecs` — с тика 0, потому что в мире появились новые регистрации: системы `core.arena-command@Intent` и
`core.fighter-setup@Gameplay` (с курсорами журналов), журнал `Event<ArenaCommand>`, ресурс `ArenaState`, компоненты
`FighterSlot` и `FighterSeed` у игрока. В реплеях до 4а нет секции `arena` — они собираются с 0 врагов (значение в коде),
поэтому поведение не меняется; в перезаписанных эталонах итоговый конфиг несёт `arena.enemies = 0` явно (и
`sim.invariants`, записанный после 3г). Реплей, записанный пользователем до 4а, воспроизводится с тем же поведением, но
сверка хешей `ecs` на нём расходится с тика 0 — по той же причине.

## Что проверить в игре

Те же пункты — `checklist` в [feature.json](feature.json) (тест `FeatureManifestsTests` сверяет).

1. `Sword.exe` — вокруг Manny кольцом 5–9 м стоят 10 врагов разного цвета, лицом к нему, в боевой стойке (слегка
   покачиваются клипом стойки, не T-поза); к игроку не идут (ИИ — точка 4б).
2. PageUp — в кольце вокруг игрока (там, где он сейчас) появляется ещё один враг; PageDown — исчезает последний
   добавленный; больше 32 и меньше 0 не бывает.
3. Отбежать от центра и нажать R — Manny мгновенно в центре арены и стоит, враги заново кольцом вокруг центра, их
   столько же.
4. `Sword.exe --units 32` — 32 врага, кадр плавный (FPS в HUD как без врагов или близко); `Sword.exe --units 0` — Manny
   один.
5. `Sword.exe --record my.swreplay` — побегать, нажать PageUp, PageDown, R, закрыть окно; `Sword.Headless --replay
   my.swreplay` — в журнале «реплей совпал»; `Sword.exe --replay my.swreplay` — враги появляются и исчезают сами, как
   было.

## Агентский доступ

Приёмка (начата на 4а, полная — 4д) — [feature.json](feature.json): сценарии `ring` (10 врагов стоят кольцом, клип
стойки), `commands` (PageUp, до 32 — предел, PageDown ×10 — ушли последние места, R после пробежки — игрок в центре,
враги кольцом, 0 — один игрок), `crowd` (32 врага, игрок бежит); инварианты `arena.slots`, `animation.*`,
`locomotion.poses`, `ecs.*`; виды — кольцо и толпа (App по записи сценария, хеши сверяются с Headless); бюджет толпы из
32 врагов — мс на тик, стадия Animation, 0 байт на тик. `Sword.Headless --feature enemies` или
`feature.verify {"id":"enemies"}`.

Анализ по чек-листу раздела 8:

| что появилось | чем агент это видит и проверяет |
|---|---|
| компоненты `EnemyTag`, `FighterSlot`, `FighterSeed`, ресурс `ArenaState` | `world.query {"with":["EnemyTag"],"fields":["FighterSlot","Transform"]}`, `entity.get`, условия (`count`, `max`, `resource`); хеш и описание — участник `ecs` (`state.describe {"part":"ecs","filter":"resource:"}`) |
| враг — сущность | **добавлено** имя `enemy N` (N — место) в `SwordAgent.ArenaNames`; `player` и `enemy N` переживают сброс (та же сущность) |
| свойство «бойцов 1 + заданное, места без дыр» | **добавлен** инвариант `arena.slots` (`invariant.list`, `time.breakOnInvariant`) |
| способ воздействия — PageUp/PageDown/R | команда `ArenaCommand` для `command.send` (`{"Action":"SetEnemies","Enemies":32}`), в реплее — готовой командой |
| видимое — враги в кадре | `capture.bundle`: рамки `enemy N` (вид `enemy`); итог `Sword.exe --json` — **добавлено** `arena` (врагов задано, бойцов в кадре, отдано скиннингу) |
| итог прогона без агента | **добавлено** `arena` в `Sword.Headless --json`: врагов задано, бойцов, враги по местам — корень, курс, расстояние до игрока |
| детерминизм спауна | `state.hash`; два прогона и реплей с командами арены — тест `ArenaTests` |

Новый инструмент MCP не понадобился: существующие видят новое состояние, команды и нарушения. Тест имён и команды —
`ArenaTests.Agent_NamesEnemiesBySlot_SendsArenaCommand`; рецепт — `docs/agent-playbook.md` («Враги и команды арены»).

## Проверки

- `tests/Sword.Game.Tests/ArenaTests`: константы — литералы SWORD; кольцо 5–9 м, курс на игрока (формула SWORD
  побитово), `FacingGoal = facing`, полный набор бойца и живая поза у каждого; спаун у края — не дальше радиуса арены
  − 3 м; `arena.enemies` вне 0…32 — ошибка, по умолчанию 0; команды: +1 (прежние на месте), −1 ×2 в одном тике (ушли
  последние, позы вернулись в пул), 100 → 32, `int.MaxValue`/`int.MinValue` без переполнения, пустая и неизвестная —
  ничего, убавили и прибавили в одном тике — те же сущности; сброс — игрок в центре той же сущностью, враги заново вокруг
  центра, локомоция со стойки, команда игрока тика сброса действует сразу, точка взгляда сохранена; клавиши PageUp и R
  в одном кадре из трёх тиков — две команды ровно в первом тике, в реплее (поля `Action:u8`, `Enemies:i32`),
  воспроизведение — те же хеши; при воспроизведении клиент команд не ставит; два прогона с командами — один хеш каждый
  тик, реплей повторяет, другое `sim.seed` — другая расстановка; 32 врага — 0 байт на тик; инвариант ловит дыру и повтор
  мест на своём тике; имена `enemy N` и `command.send ArenaCommand`.
- `GameInvariantTests.FairGame_NoViolations_AllCoreInvariantsChecked` — `arena.slots` в списке и не нарушается;
  `HudTests` — строка справки по привязкам.
- `verify.ps1`: `headless-smoke` (10 врагов из `sword.json`, 32 — `--units 32`: 0 Б/тик, нарушений нет), `app-smoke`
  (кадр рисует 11 бойцов; PageUp и R удержанием в записи Sword.exe — Headless повторяет запись с теми же хешами;
  бенч кадра — с 32 врагами), `features` (манифест `enemies`, виды в App), `replay-smoke` (эталоны).

## Известные ошибки

Нет.
