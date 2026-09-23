# Трассы SWORD для сверки анимации (этап 2, точка 4г)

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

Третий аргумент — сценарий: `player` (по умолчанию) или `turn`. Сценарий игрока (`Scenario.Segments`) должен совпадать с
`ReferenceTraceTests.Segments`; сценарий поворота тест читает из самой трассы (`segments`), а манифест `enemies` (сценарий
`turn`) повторяет его командами — правя `TurnTrace.Segments`, поправить и манифест. На 4г программа пересобрана и трасса
игрока снята заново — побайтно та же.
