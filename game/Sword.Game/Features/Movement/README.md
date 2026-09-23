# Движение игрока (точка 1в; столкновения бойцов — 4в)

Корень бойца игрока идёт по команде: разгон к желаемой скорости, поворот курса к точке взгляда, колонны и край арены.
С точки 4в корни всех бойцов сталкиваются и между собой (`ResolveRoots` SWORD целиком — фича [«Враги»](../Enemies/README.md)).
Часть ядра игры (`GameCore`): по принципу 4 плана ядро — не модуль и не выключается. Код — `game/Sword.Game/Movement/`,
`game/Sword.Game/GameCore.cs`.

## Откуда логика

SWORD 2eab13b, путь `DuelArena.KinematicSteer` — без контроллера Motion Matching модуля Locomotion (в SWORD он по
умолчанию берёт корень игрока на себя через `Fighter.Mover`; сравнивать с SWORD — с выключенным MM). Формулы, константы
и порядок вычислений те же:

| SWORD | SWORD2 |
|---|---|
| `PlayerController.Think`: желаемая скорость — направление WASD на `RunSpeed` 4,2 / `WalkSpeed` 1,6 м/с; курсор запоминается (`_cursor`), курс-цель каждый тик — на него от текущего корня, если дальше 0,3 м | `PlayerCommandSystem` (Intent) из `Event<PlayerCommand>`; точка — `GazeTarget` |
| `DuelArena.KinematicSteer`: курс ≤ `KinTurnRate` 12 рад/с, разгон `KinAccel` 14 1/с | `RootSteerSystem` (Gameplay — до аниматора, этап 2) |
| `KinematicSteer` не-`Agile` (4г): стоя — `KinIdleTurnRate` 2,5 рад/с, отстал больше `KinTurnAnimMin` 45° — поворот на месте клипом, курс ведёт его yaw | `RootSteerSystem.SteerSteady` — бойцы без `PlayerTag` (враги); фича [«Враги»](../Enemies/README.md) |
| `KinematicBegin`: `Facing += anim.YawDelta`, сдвиг корня клипом действия `RootDelta` по курсу (4г) | `RootControlSystem` — у бойцов со слоем действий |
| `KinematicBegin`: `Root += RootVel·dt`, `Root.Y = 0`; `KinPrevRoot/KinPrevFacing` | `RootControlSystem` (Movement), `PrevTransformSystem` (Begin) |
| `ResolveRoots`: два прохода; колонны — корень на радиус колонны + 0,35 м, скорость внутрь гасится; край арены `ArenaRadius − 0,6`, скорость наружу гасится | `RootCollisionSystem` (Movement, после RootControl) |
| `ResolveRoots` (4в): в каждом проходе бойцы по порядку, у бойца — сначала пары со следующими (круги 0,35 м, каждому половина перекрытия, скорость внутрь соседа гасится), затем его колонны и край | `RootCollision.Resolve` по местам `FighterSlot` (игрок 0, враги 1…32); подробно — фича «Враги» |
| `DuelArena.Reset`: игрок в (0, 0), курс 0 | `GameCore.SpawnPlayer`; сброс на ходу (R) — команда арены, фича [«Враги»](../Enemies/README.md) |
| `DuelArena.Tick`: `Time += dt` (float) первым делом; тик 120 Гц (`Fighter.FastTick`), не больше 8 тиков за кадр | `SimTime.Time`; `sim.tickRate`, `sim.maxTicksPerFrame` |

## Устройство

Компоненты и команда — `Game.Contracts` (`Transform`, `PrevTransform`, `RootVelocity`, `FighterCommand` — намерение
бойца, `GazeTarget`, `Fighter`, `PlayerTag`, `PlayerCommand` — готовая команда тика в мировых координатах, её и хранит
реплей); стадии и метки — `GameStages`, `GameLabels`. Регистрации — `GameCore.Configure`, ядро в
`Simulation.Build`; композиция — `Sword.Game.Bootstrap.SwordSimulation`, одна у Sword.exe и Sword.Headless.

## Настройки

Своих ключей конфига нет: константы SWORD — `MovementRules` (тест `Constants_AreSwordLiterals` сверяет их с
литералами SWORD). Шаг тика — `sim.tickRate` (120), потолок тиков за кадр — `sim.maxTicksPerFrame` (8).

## Зависимости

`Game.Contracts`, `Sword.Engine.Ecs` (системы), `Sword.Engine.Runtime` (`SimTime`, регистрация), `Sword.Game.Arena`
(`ArenaLayout`: радиус и колонны).

## Ограничения (намеренные, до своих этапов)

- Действий, кроме поворота врага на месте (4г), нет (этап 6): масштаб хода и поворота — 1, корневое движение и поворот
  курса клипом — только от поворота на месте. Порядок `KinematicBegin` SWORD (курс и разгон → `anim.Tick` по новой скорости → перенос корня) сохранён
  с этапа 2: курс и разгон — `RootSteerSystem` в Gameplay, до аниматора, перенос — `RootControlSystem` в Movement
  (фича [«Анимация»](../Animation/README.md), побитовая сверка с SWORD — `ReferenceTraceTests`).
- `Agile` (SWORD: `Agile = f.IsPlayer`) — боец с `PlayerTag`: курс сразу контроллером, 12 рад/с. Враги (не-`Agile`) с
  точки 4г — стоя 2,5 рад/с и поворот на месте клипом (фича «Враги»).
- Столкновения бойцов между собой — с точки 4в: `RootCollisionSystem` собирает бойцов по местам и проходит их двумя
  проходами наружу, как SWORD (пары, колонны, край — у каждого бойца до следующего). Одиночный боец даёт ровно прежние
  биты (эталон прототипа не перезаписан); два прохода не разводят толпу до конца — инвариант `movement.rootOverlap`
  с допуском по замеру (фича «Враги», «Инвариант перекрытия: замер»).
- Тик без команды — стоять; взгляд — на прежнюю точку (как `_cursor` SWORD).
- Ввод, камера и интерполяция кадра — фича «Управление» (`Features/Control`, точка 1г).

## Что проверить в игре

Те же пункты — `checklist` в [feature.json](feature.json) (тест `FeatureManifestsTests` сверяет).

Управление с клавиатуры — в `Features/Control`; здесь — заданной командой: `--move`/`--walk`/`--gaze` заменяют ввод
игрока, камера управляется как обычно.

1. `Sword.exe --move 1,0` — Manny бежит вправо (по +X), камера за ним, кольцо и тень под ним; секунд через 15 упирается
   в край арены и дальше не выходит.
2. `Sword.exe --move 27.7,11.4` — секунд через 7 добегает до ближайшей колонны, упирается и уходит вдоль неё, сквозь
   неё не проходит.
3. `Sword.exe --move 1,1 --gaze 30,0` — бежит по диагонали, лицом доворачивает к точке (30, 0) и следит за ней, пока бежит.
4. `Sword.exe --move 0,1 --walk` — заметно медленнее: шаг, а не бег.
5. Без ключей — Manny стоит в центре, как в точке 1б.

## Агентский доступ

Приёмка (этап 3д) — [feature.json](feature.json): сценарии командами (бег до края, шаг, на колонну, со взглядом,
стоит) и эталон прототипа; инварианты `ecs.*` и (с 4в) `movement.rootOverlap`; метрики — скорости бега и шага и край арены против констант SWORD, корень у колонны и курс
взгляда — эталон SWORD2; виды — бег и колонна; бюджет бега. `Sword.Headless --feature movement` или `feature.verify
{"id":"movement"}`.

- `Sword.Headless --ticks N --move X,Z [--gaze X,Z] [--walk] [--set …] --json` — итог: где игрок, курс, скорость,
  траектория (`--sample`), мс на тик, выделенные байты на тик после прогрева (`allocatedBytesPerTick`, ждём 0), список
  систем (`метка@стадия`, в порядке исполнения), модули, итоговый конфиг и его источники.
- `Sword.exe --move … --ticks-per-frame N --frames M --shot F.png --json` — картинка ровно после N·M тиков; в итоге
  `simTicks` и корень игрока последнего кадра (`player`).
- `verify.ps1`: `headless-smoke` — 600 тиков бега, скорость 4,2 м/с, игрок ушёл по +X, 0 байт на тик; `app-smoke` —
  Sword.exe с `--move 1,0` по 2 тика на кадр: тиков ровно 180, корень игрока совпадает с Headless на 180 тиках.
- Клиент видит мир только через `WorldReader` (`SimulationLink.World`), владелец симуляции — тоже: изменить симуляцию
  мимо команд тика нельзя (`SimulationLink_ReachesOnlyReadOnlyWorld`, `OwnerSeesWorldReadOnly`).
- Тесты `tests/Sword.Game.Tests/MovementTests`: константы — литералы SWORD, первый тик по формуле, скорости бега и
  шага, поворот, запоминание точки взгляда, край арены, колонна (не входит, упирается; бег вскользь — обходит и бежит
  дальше), нечисловая и огромная команда, ядро не выключается, побитовая повторяемость, 0 байт на тик. Столкновения
  бойцов (4в) — `RootCollisionTests` (побитово против копии `ResolveRoots` SWORD, одиночный боец — как до 4в).

Не хватает (точка 1е): чтения состояния через агента на ходу (`state.*`) и объяснения, почему игрок оказался здесь
(`state.why`), — их даёт агентский доступ.

## Известные ошибки

Нет.
