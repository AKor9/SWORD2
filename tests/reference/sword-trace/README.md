# Трасса SWORD для сверки анимации (этап 2)

`../anim-locomotion.trace.json` снят этой программой с **настоящего кода SWORD 2eab13b** на эталонном паке
`Content.reference.swpak`: игрок (`Agile`) без Motion Matching — встроенный `KinematicSteer`, `FighterAnimator`
с `LegacyLocomotion`, перенос корня `KinematicBegin`, команды — как `PlayerController.Think`. На каждый тик — корень,
курс, играющий клип и хеш позы (FNV-1a по битам всех 16 чисел модельных матриц суставов после тика).

Сверяет `tests/Sword.Game.Tests/ReferenceTraceTests` — тот же сценарий в симуляции SWORD2, тик за тиком, побитово.

**Что здесь настоящий SWORD, а что — копия.** Настоящий код SWORD (подключён исходниками): `CharacterRig`,
`FighterAnimator`, `LegacyLocomotion`, `SkeletalClips`, `AnimLibrary`, чтение пака. Переписаны в `Program.cs` вручную,
потому что `Sword.Game` SWORD (`DuelArena`, `PlayerController`, `Fighter`) не подключается: `PlayerController.Think`
(желаемая скорость и цель курса по курсору), `KinematicSteer`, `Fighter.DirToLocal` и перенос корня `KinematicBegin`.
Копия сверена построчно с `DuelArena.Kinematic.cs` (`KinematicSteer`, `KinematicBegin`), `Fighter.cs` (`DirToLocal`),
`PlayerController.cs` и верна для пути **без действий и без Motion Matching**: `TurnScale`, `MoveScale` = 1,
`YawDelta` = 0. Поэтому трасса подтверждает побитово аниматор, локомоцию и клипы, а порядок курс → аниматор → перенос
— только в той мере, в какой верна эта копия.

На этапе 6 (слой действий: `TurnScale`/`MoveScale`/`YawDelta`/`RootDelta` становятся ненулевыми) копию нужно
пересверить с SWORD или заменить на подключение настоящего `DuelArena`.

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
```

Сценарий (`Scenario.Segments`) должен совпадать с `ReferenceTraceTests.Segments`.
