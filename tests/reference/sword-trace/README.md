# Трассы SWORD для сверки анимации, толпы и тел (этап 2, точки 4г, 4д, 5б и 5г)

`../anim-locomotion.trace.json` снят этой программой с **настоящего кода SWORD 2eab13b** на эталонном паке
`Content.reference.swpak`: игрок (`Agile`) без Motion Matching — встроенный `KinematicSteer`, `FighterAnimator`
с `LegacyLocomotion`, перенос корня `KinematicBegin`, команды — как `PlayerController.Think`. На каждый тик — корень,
курс, играющий клип и хеш позы (FNV-1a по битам всех 16 чисел модельных матриц суставов после тика).

Сверяет `tests/Sword.Game.Tests/ReferenceTraceTests` — тот же сценарий в симуляции SWORD2, тик за тиком, побитово.

`../anim-turn.trace.json` (точка 4г, сценарий `turn`) — боец **не-`Agile`** (враг) с настоящим `FighterAnimator`
(слой действий): его намерение (желаемая скорость, курс-цель) задаёт сценарий `TurnTrace.Segments` — курс-цель скачет стоя
на +1,59 рад (поворот клипом влево), −1,3 (вправо), +3,1 (разворот), +0,5 (меньше 45° — 2,5 рад/с), +1,7 и сразу ходьба
(поворот прерван: клип гаснет без yaw), поворот на ходу (12 рад/с), остановка с отставшим на 2,6 рад курсом, −0,8 рад (на
грани 45°), медленный дрейф с +2,3 рад. На тик — корень, курс, клип локомоции, действие и его клип, время, вес, растяжение
yaw, `YawDelta`, `RootDelta` (x, z), переход от снимка и хеш позы. Итог прогона (`summary`): поворотов клипом 42 (из них
34 — начались и кончились в том же тике: встал по скорости, а «идёт» ещё держится гистерезисом), все три клипа поворота,
591 тик 2,5 рад/с, 605 тиков курс ведёт клип, снимков позы 0. Начало — корень и курс врага места 1 в SWORD2 при сборке мира
(`arena.enemies = 1`, `sim.seed = 1`: расстановка SWORD2 — не `Random(7)` SWORD); другая расстановка — тест назовёт
её, трассу снять заново с новым `TurnTrace.StartRoot`/`StartFacing`. В SWORD2 намерение дают команды врагу
(`EnemyCommand`) — тест `ReferenceTraceTests.EnemyTurn_MatchesSwordTickByTick` и сценарий `turn` манифеста `enemies`
(метрика `sword-turn-trace`).

`../anim-crowd.trace.json` (точка 4д, сценарий `crowd`) — **толпа**: игрок (`Agile`) и враги (не-`Agile`), у каждого места
свой настоящий `FighterAnimator` (33 аниматора заводятся сразу, как `DuelArena.AttachRig`; постройка бойца — `Reset`, как
`Fighter.Build` → `ResetAnimator`: аниматор места переживает PageDown/PageUp и сброс, вместе с тем, что `Reset` не
сбрасывает). Тик — порядок `DuelArena.Tick`: `Time += dt` → `PlayerController.Think` → `EnemyAi.Think` врагов по местам
1…`EnemyCount` → `KinematicBegin` всех бойцов 0…`ActiveCount − 1` → `ResolveRoots`; команды арены (`SetEnemyCount`,
`Reset`) — до тика, как из цикла кадра SWORD. Сценарий и расстановку программа читает из входа (`crowd.input.json`, его пишет
тест SWORD2 — ниже): команды игрока по сегментам, команды арены по тикам и постройки (тик, место, корень, курс, зерно) —
`Random(7)` SWORD не воспроизводится, поэтому бойцы строятся там же, где SWORD2 (игрок при сборке и сбросе — в центре,
программа это проверяет). На тик — по местам 0…`EnemyCount` массивами: `x`, `z` (корень), `facing`, `clip` (номер в
`clipNames`), `action` и `actionClip` (слой действий), `poseHash`; итог (`summary`) — бойцов до 33, команд арены 11,
построек 53, пар корней 3164, толчков колонной 2307, края 0, поворотов клипом 0. Копия трассы хранит вход (`input`), и тест
сверяет его с SWORD2. Сверяет `tests/Sword.Game.Tests/ReferenceTraceTests.Crowd_MatchesSwordTickByTick` (каждый боец
каждого тика) и метрики `sword-crowd-*` сценария `sword-crowd` манифеста `enemies`. Файл — одна строка JSON без перевода
строки в конце, как и прежние трассы (3,4 МБ).

**Тела толпы (точка 5б).** В трассу толпы добавлены капсулы тела каждого бойца: форма при постройке и постановка по позе.
Постройка (`Build` программы) после сброса аниматора задаёт форму (`BuildShape` — копия части `Fighter.Build`: радиусы
`Radii`, полудлины — половина расстояния между концами сегмента процедурной позы `ComputeTargets` от корня на высоте земли
после `IdealFoot(leg, 0)`, `SetShape(Masses, Radii, half)`; состояние живого рэгдолла — как сразу после `Build` у
кинематического бойца: наклоны, `_torsoYaw`, `_upperLead`, `PlanVel` — 0) и ставит тела `PlaceBodies(f, 0)` — `PlaceBuilt`
SWORD (первая постановка без скорости по позе после `FighterAnimator.Reset`). Тик — после `ResolveRoots` `PlaceBodies(f, dt)`
у всех бойцов (копия `DuelArena.PlaceBodies`: мир — `PoseWorld` = `KinWorld(PoseRoot, PoseFacing, Terrain.Height)`, визуального
смещения и `BodyY` нет; `PinFeet` по пути `SWORD_FOOTLOCK=off` — аниматоры с `FootPinningEnabled = false`, фиксация стоп —
этап 9; клинок `anim.Blade`, `JerkTrace`, `PoseHash`, `BodiesPlaced` не копируются; тела 13 — без клинка 11; `Detached` и
`Severed` нет). Настоящие — `RigidBody` (`SetShape`, `Place`, поля), `Xpbd.RotationVector`, `Terrain` (`Height`, `Normal`),
`AnimMath` (`FromBasis`, `Ik2Bone`), `SwordBody`, `Pose2.Rotate`, `CharacterRig` (суставы). Постановка тел в процедурную позу
внутри `Build` (`b.Place(…, TargetRot)`) не копируется: `PlaceBuilt` тут же ставит те же тела заново, а `RigidBody.Place`
переписывает все поля постановки и скорости.

Формат (компактный: полные значения всех капсул всех тиков — около 15 млн чисел, ~160 МБ): на тик по местам
`bodyHash` — FNV-1a по битам 13 капсул в порядке тел SWORD2 (`bodyOrder`: сегменты 0…10, 12, 13; у капсулы — `Pos`, `Rot`,
`PrevPos`, `PrevRot`, `Vel`, `AngVel`, `Radius`, `HalfLen`), `pelvis0`, `pelvis1`, `pelvis12` — центр капсулы таза мест 0, 1 и 12
(ряды метрик `sword-crowd-pelvis-*` манифеста `bodies`); `shapes` — форма каждой постройки (тик, место, радиусы и полудлины
13 капсул); `samples` — все 22 числа каждой капсулы каждого бойца на тиках выборки 1, 2, 481, 601, 1241, 1401, 1402, 2701,
3000 (постройки и конец: при расхождении хеша по ним видно поле); `summary.placements` (53 453 = 53 400 постановок тика + 53
при постройке), `summary.maxSpeed` (12,6 м/с). Трасса выросла с 3,4 до 5,4 МБ; прежние поля не изменились (сверено по всем
376 800 значениям тиков и полям верхнего уровня; изменён только текст `purpose`). Сверяет
`ReferenceTraceTests.Crowd_MatchesSwordTickByTick` — хеш тела каждого бойца каждого тика, таз трёх мест, выборку по числам
и формы построек; расхождение называет тик, место и (по выборке) капсулу и число.

**Рельеф (точка 5г).** `../anim-terrain.trace.json` снята той же программой в режиме `crowd` — без изменений кода: вход
(`terrain.input.json`, его пишет `ReferenceTraceTests.Terrain_MatchesSwordTickByTick`, сценарий `ReferenceTraceTests.Terrain`)
— ноль врагов, зерно 1, без команд арены, команды игрока: бег на склон (0, 18) до вершины 1,6 м, бег на ступенчатую пирамиду
(−16, 0) и стойка на верхнем ярусе, спуск шагом по ступеням на запад до земли, бег через угол пирамиды на ухабы (0, −18) и
стойка на них (2800 тиков). Тела игрока — в мире бойца на высоте `Terrain.Height` под корнем (`KinWorld`): склон, кромки
ступеней вверх и вниз (скачок мира бойца на 12 см за тик — так SWORD без модуля постановки стоп, `SWORD_FOOTIK=off`, как и весь путь трассы; по
умолчанию SWORD сглаживает высоту тела модулем FootPlacement — `BodyY`/`BodyPlacer`, этап 9), ухабы.
Итог прогона: бойцов 1, построек 1, постановок тел 2801, скорость капсул до 17,9 м/с (кромки ступеней). Формат — как у трассы
толпы (поле `purpose` — текст трассы толпы: программа одна); `pelvis1`, `pelvis12` — места, которых в сценарии нет (тела без
постройки), тест их не читает; выборка — тики 1, 2, 481, 601, 1241, 1401, 1402, 2701 (тики выборки толпы, не больше длины сценария). 0,76 МБ.
Сверяет `ReferenceTraceTests.Terrain_MatchesSwordTickByTick` (корень, курс, клип, поза, хеш тела каждого тика, таз, выборка,
форма) и метрика `sword-terrain-pelvis` сценария `terrain` манифеста `bodies`. Совпала с SWORD2 побитово с первого снятия.

**Что здесь настоящий SWORD, а что — копия.** Настоящий код SWORD (подключён исходниками): `CharacterRig`,
`FighterAnimator`, `LegacyLocomotion`, `SkeletalClips`, `AnimLibrary`, чтение пака. Переписаны в `Program.cs` вручную,
потому что `Sword.Game` SWORD (`DuelArena`, `PlayerController`, `Fighter`) не подключается: `PlayerController.Think`
(желаемая скорость и цель курса по курсору), `KinematicSteer`, `Fighter.DirToLocal` и перенос корня `KinematicBegin`.
Копия сверена построчно с `DuelArena.Kinematic.cs` (`KinematicSteer`, `KinematicBegin`), `Fighter.cs` (`DirToLocal`),
`PlayerController.cs` и верна для пути **без действий и без Motion Matching**: `TurnScale`, `MoveScale` = 1,
`YawDelta` = 0. Поэтому трасса подтверждает побитово аниматор, локомоцию и клипы, а порядок курс → аниматор → перенос
— только в той мере, в какой верна эта копия.

В трассе поворота (4г) настоящие — те же (`FighterAnimator`: `StartTurn`, `Begin`, `TickActions`, `TurnDrivesYaw`,
`TurnScale`, `MoveScale`, `MomentumHold`, `EvadeRecovery` — вызываются как есть; закрытые поля действия — `_actionClip`,
`_actionWeight`, `_turnScale`, `_snapFade` — читаются отражением, только для записи в трассу). Копия (`TurnTrace.Run`) —
`KinematicSteer` целиком с веткой не-`Agile` (`standing`, `idleTurn`, `StartTurn`, `TurnDrivesYaw`, `KinIdleTurnRate`,
`KinTurnRate·TurnScale`, `desired·MoveScale`, `MomentumHold`) и `KinematicBegin` после `anim.Tick` (`Facing += YawDelta`,
`control` уклонения, `RootDelta` по курсу), `Fighter.DirToLocal` — сверена построчно с `DuelArena.Kinematic.cs` 2eab13b.
Коллизий, фиксации стоп (`PinFeet`) и ИИ в копии нет: сценарий держит врага вдали от игрока, колонн и края, а стопы —
этап 9 (как в трассе игрока).

В трассе толпы (4д) настоящие — `FighterAnimator` каждого бойца (`Tick`, `Reset`, `StartTurn`, `SetBlock`) и геометрия арены
(`Sword.Game/Gameplay/ArenaLayout.cs` SWORD подключён ссылкой: `ArenaRadius`, `Pillars`). Копии (`CrowdTrace`), сверенные
построчно с 2eab13b: `EnemyAi.Think` — строки движения (подход до `KeepDistance` 1,8, покачивание, расхождение по местам
1…`EnemyCount`, курс-цель на игрока, `SetBlock(false)`); удара по таймеру, `Built`, `p.Dead` и пропуска мёртвых нет (этап
6); `PlayerController.Think` — движение и взгляд (`SetBlock(false)`: блок не держат); `KinematicSteer`/`KinematicBegin` (как
в трассе поворота), `ResolveRoots` целиком (два прохода, пары, колонны, край; мёртвых нет), поля движения `Fighter.Build`
(курс, цель курса, зерно, желаемая скорость, корень, скорость); `DuelArena.Reset`/`SetEnemyCount`/`Spawn` — с расстановкой
из входа (неактивным местам при сбросе — только `Reset` аниматора: их корень SWORD задаёт снова, когда место станет активным).
Точка взгляда (`GazePoint`) не пишется: кинематический боец её не читает (поворот головы — только у живого рэгдолла).

На этапе 6 (удары, блок, реакции, уклонения: `TurnScale`/`MoveScale` становятся не 1, появляются снимок позы и рывок
уклонения) обе копии нужно пересверить с SWORD или заменить на подключение настоящего `DuelArena`.

## Как снять заново

Исходники SWORD подключаются ссылками только для чтения (SWORD не меняется, выход сборки — во временной папке).
Во временной папке — `Program.cs` отсюда и `swordtrace.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.22000.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <PlatformTarget>x64</PlatformTarget>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>$(NoWarn);CS1591;CS8618;CS8602;CS8604;CS0162</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Game\Gameplay\ArenaLayout.cs" Link="SWORD\Game\ArenaLayout.cs" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Game\Gameplay\Terrain.cs" Link="SWORD\Game\Terrain.cs" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Physics\**\*.cs" Exclude="D:\Work\Visual Studio\SWORD\src\Sword.Physics\obj\**;D:\Work\Visual Studio\SWORD\src\Sword.Physics\bin\**" LinkBase="SWORD\Physics" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Anim\**\*.cs" Exclude="D:\Work\Visual Studio\SWORD\src\Sword.Anim\obj\**;D:\Work\Visual Studio\SWORD\src\Sword.Anim\bin\**" LinkBase="SWORD\Anim" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Combat\**\*.cs" Exclude="D:\Work\Visual Studio\SWORD\src\Sword.Combat\obj\**;D:\Work\Visual Studio\SWORD\src\Sword.Combat\bin\**" LinkBase="SWORD\Combat" />
    <Compile Include="D:\Work\Visual Studio\SWORD\src\Sword.Content\**\*.cs" Exclude="D:\Work\Visual Studio\SWORD\src\Sword.Content\obj\**;D:\Work\Visual Studio\SWORD\src\Sword.Content\bin\**" LinkBase="SWORD\Content" />
  </ItemGroup>
</Project>
```

Перед снятием — `tools/scripts/reference.ps1 -Check`. Затем:

```
dotnet run -c Release -- "<SWORD2>\Content.reference.swpak" "<SWORD2>\tests\reference\anim-locomotion.trace.json"
dotnet run -c Release -- "<SWORD2>\Content.reference.swpak" "<SWORD2>\tests\reference\anim-turn.trace.json" turn
```

Толпа (4д) и рельеф (5г) — в два шага: сначала тест SWORD2 пишет вход (сценарий `ReferenceTraceTests.CrowdSegments`/`CrowdArenaCommands` и
расстановку SWORD2 по тикам) — он пишет его при каждом прогоне, даже когда трассы ещё нет или она разошлась; затем программа:

```
dotnet test tests/Sword.Game.Tests --filter Crowd_MatchesSwordTickByTick      # пишет artifacts/reference/crowd.input.json
dotnet run -c Release -- "<SWORD2>\Content.reference.swpak" "<SWORD2>\tests\reference\anim-crowd.trace.json" crowd "<SWORD2>\artifacts\reference\crowd.input.json"
dotnet test tests/Sword.Game.Tests --filter Crowd_MatchesSwordTickByTick      # сверка
```

Рельеф — так же, с другим входом и выходом:

```
dotnet test tests/Sword.Game.Tests --filter Terrain_MatchesSwordTickByTick    # пишет artifacts/reference/terrain.input.json
dotnet run -c Release -- "<SWORD2>\Content.reference.swpak" "<SWORD2>\tests\reference\anim-terrain.trace.json" crowd "<SWORD2>\artifacts\reference\terrain.input.json"
dotnet test tests/Sword.Game.Tests --filter Terrain_MatchesSwordTickByTick    # сверка
```

Третий аргумент — сценарий: `player` (по умолчанию), `turn` или `crowd` (четвёртый — вход). Сценарий игрока (`Scenario.Segments`) должен совпадать с
`ReferenceTraceTests.Segments`; сценарий поворота тест читает из самой трассы (`segments`), а манифест `enemies` (сценарий
`turn`) повторяет его командами — правя `TurnTrace.Segments`, поправить и манифест. На 4г программа пересобрана и трасса
игрока снята заново — побайтно та же. На 4д программа пересобрана (с `ArenaLayout.cs` SWORD) и обе прежние трассы сняты заново
— побайтно те же. Правя сценарий толпы в тесте, поправить и сценарий `sword-crowd` манифеста `enemies` (те же команды).
На 5б программа пересобрана (ещё `Terrain.cs` и `Sword.Physics` SWORD ссылками): `anim-locomotion` и `anim-turn` сняты
заново — побайтно те же; `anim-crowd` снята заново с телами. Манифест `bodies` сверяет таз по трассе на эталоне SWORD2
`enemies.swreplay` (запись сценария `sword-crowd`): правя сценарий толпы, эталон `enemies` записать заново. На 5г программа
пересобрана (те же ссылки): `anim-locomotion`, `anim-turn`, `anim-crowd` сняты заново — побайтно те же; снята `anim-terrain`.
Правя сценарий рельефа (`ReferenceTraceTests.TerrainSegments`), поправить и сценарий `terrain` манифеста `bodies` (те же
команды) и записать заново эталон SWORD2 `bodies.swreplay` (запись сценария `terrain`, приёмка этапа 5: на нём метрика
`sword-terrain-pelvis-reference`).
