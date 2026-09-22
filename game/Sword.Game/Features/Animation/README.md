# Анимация и локомоция клипами (этап 2)

Персонаж ходит и бегает клипами: стойка, ходьба и бег в 8 сторон с кроссфейдом и темпом по скорости — как игрок SWORD
без Motion Matching. Позы считает подсистема `Sword.Engine.Animation` (пул поз, задания пачкой), выбор клипа — модуль
Locomotion, аниматор бойца (вход слоя движения, итоговая поза) — ядро игры, отрисовка — клиент по позе тика.

## Точки этапа

| точка | что | видно |
|---|---|---|
| 2а. Подсистема | `Formats`: `.swskeleton`, `.swclip` (чтение и запись, байт в байт SWORD); `Engine.Animation`: скелет, библиотека клипов, сэмплинг, смешивание, прямая кинематика, `AnimMath`, пул поз с заданиями | тестами и бенчмарком |
| 2б. Аниматор и модуль | `Game.Data` (таблица действий), ассеты бойца, ядро аниматора (`PoseRef`, `AnimatorMotion`), порядок `KinematicBegin` по стадиям, модуль Locomotion (`LegacyLocomotion`) | `Headless`: игрок играет клипы |
| 2в. Отрисовка и приёмка | вид `IAnimationView`, палитры по позе в рендере, инструменты агента `anim.pose` / `anim.clips`, сверка с SWORD на эталонном паке | Manny ходит и бегает клипами |

## Откуда логика

SWORD 2eab13b: `Sword.Anim` (`AnimLibrary`, `SkeletalClips`, `AnimMath`) — в подсистему (её
[README](../../../../engine/Sword.Engine.Animation/README.md)); `Sword.Combat/LegacyLocomotion` — в модуль
([README](../../../modules/Sword.Game.Locomotion/README.md)); `Sword.Combat/FighterAnimator` (слой локомоции),
`CharacterRig` (слоты действий), `DuelArena.KinematicBegin/KinematicSteer`, `SkinnedCharacters` — сюда:

| SWORD | SWORD2 |
|---|---|
| `KinematicSteer`: курс и разгон | `RootSteerSystem` (Gameplay, `core.root-steer`) |
| `FighterAnimator.Tick`: `_vel += (local − _vel)·(1 − e^(−dt·0,693/0,12))`, «идёт» с гистерезисом 0,25/0,5 м/с; `local = DirToLocal(RootVel)` по новому курсу | `AnimatorInputSystem` (Gameplay, после курса), компонент `AnimatorMotion` |
| `Locomotion.Tick` (`LegacyLocomotion`) | модуль Locomotion, стадия Animation |
| `TickActions` без действия: `clips.ToComponent` | `AnimatorPoseSystem` (Animation, `core.animator-pose`) — задание прямой кинематики |
| — | `AnimationTickSystem` (Animation, `core.animation-tick`): пул исполняет задания пачкой |
| `KinematicBegin`: `Root += RootVel·dt` | `RootControlSystem` (Movement), дальше `RootCollisionSystem` |
| `CharacterRig`: слоты, клипы по умолчанию, `combat/actions.json` | `Game.Data.ActionSlots`, `ActionTable` |
| `CharacterRig.TryLoad`: меш, клипы скелета `ue5_manny` | `CharacterAssets.Load` (скелет, поза привязки из меша, клипы, таблица действий) |
| `SkinnedCharacters.AddInstance`: палитра `InverseBind · Component · World`, корень и курс — между тиками, поза — тика | `ArenaView` снимает позу тика в Extract, `SkinnedMeshes.AddPosed` считает палитру |

**Порядок вычислений.** В SWORD `KinematicBegin` делает курс и разгон, затем `anim.Tick` по новой скорости, затем
перенос корня. Стадия Animation в плане стоит до Movement, поэтому курс и разгон вынесены в Gameplay, до аниматора, —
порядок тот же, аниматор видит скорость этого тика. Побитовая сверка с SWORD (ниже) гоняет настоящие
`FighterAnimator`, `LegacyLocomotion` и клипы SWORD, а курс, разгон, `DirToLocal` и перенос корня в программе трассы —
ручная копия `DuelArena`/`PlayerController` (сверена построчно, верна без действий и Motion Matching; подробно —
[sword-trace](../../../../tests/reference/sword-trace/README.md)). На этапе 6 копию пересверить.

**Намеренных изменений семантики нет.** Не перенесено до своих этапов: слой действий, маски верха, вздрагивание, рука
и IK кисти (этап 6), фиксация и постановка стоп, `FootDown` (этап 9), Motion Matching и визуальное смещение (этап 8),
поворот на месте клипом у врагов (этап 4), капсулы тела по позе (этап 5), оружие в руке (этап 6).

## Устройство

- Поза бойца — слот пула поз (`PoseRef` в `Game.Contracts`): ядро заводит её вместе с бойцом (`GameCore.SpawnPlayer`) и
  возвращает, когда компонент снят (`core.pose-release`, End). Новая поза — поза привязки.
- Слои пишут задания до `core.animator-pose`; итоговую позу считает ядро; пул исполняет все задания тика в
  `core.animation-tick`. Задания одной позы исполняются по порядку постановки.
- Пул поз — у каждой симуляции свой (`GameCore.Configure`); ассеты (`CharacterAssets`: скелет, клипы, таблица
  действий) общие и неизменяемые — наружу только `ReadOnlySpan`, без сеттеров и открытых массивов (тест
  `AnimationTests.SharedAnimationAssets_ExposeNoWritableData`); их грузит exe из пака (`CharacterAssets.LoadFromPak`,
  ~0,3 с на эталонном паке: 1048 клипов, 161 сустав).
- Позы слоя локомоции модуль берёт на первом тике бойца в Animation, когда состояние уже в мире: снятое состояние
  всегда их вернёт, даже если боец снят на барьере своего появления.
- Клиент читает позы видом только для чтения `IAnimationView` (`WorldReader.View`): в Extract снимает модельные
  матрицы бойцов кадра, `BuildFrame` рисует Manny по ним (`SkinnedMeshes.AddPosed` — обратные bind-матрицы знает только
  рендер). Поза не интерполируется между тиками, как в SWORD; плавно идёт корень.
- Вид только для чтения — новое в ECS (`WorldBuilder.ReadView`, маркер `IReadView`): интерфейс, члены которого только
  читают; `PublicSurfaceTests.ReadViews_OnlyRead` проверяет каждый такой интерфейс сборок продукта.

## Настройки

`modules.locomotion.enabled` — модуль Locomotion (без него поза — поза привязки, корень движется). Выбор клипов слотов
— `combat/actions.json` в паке. Своих ручек у этапа нет: константы — `AnimatorRules`, `LegacyLocomotion` (литералы SWORD).

## Зависимости

`Sword.Engine.Animation`, `Sword.Engine.Formats` (через подсистему и рендер), `Game.Data`, `Game.Contracts`; модуль
`Sword.Game.Locomotion`; контент — эталонный пак (`skeletons/ue5_manny.swskeleton`, `characters/manny/mesh.swmesh`,
`anim/*/*.swclip`, `combat/actions.json`).

## Ограничения

- Пакет `FeatureManifest` и `feature.verify` — этап 3 (задним числом и для этой фичи); сейчас сценарий и сверка —
  тест `ReferenceTraceTests`, числа — `verify.ps1`.
- Сэмплинг последовательный (7,9 мкс на бойца за тик при кроссфейде, 33 бойца — 0,26 мс; бенчмарк `PoseSampling`).
- Отладочной отрисовки скелета (линии суставов поверх меша) нет — появится с debug draw (этап 5).

## Что проверить в игре

1. `Sword.exe` — Manny стоит в боевой стойке одноручного хвата (клип `1HM_Idle_Standing`; меча в руке ещё нет — этап 6),
   слегка покачивается, а не стоит в T-позе.
2. W (курсор перед Manny) — разгоняется: пара шагов ходьбой, затем бег вперёд; ноги не скользят по полу.
3. С зажатым Shift — шагом, заметно медленнее, шаги соответствуют скорости.
4. Курсор держать перед Manny и бежать A или D — бег боком (стрейф), S — бег спиной вперёд; смена направления — плавная,
   без рывков позы.
5. Отпустить клавиши — за полсекунды возвращается в стойку, переход мягкий.
6. Бег по кругу вокруг курсора — ноги переключаются между направлениями без дёрганья (гистерезис сторон).

## Агентский доступ

Анализ по чек-листу раздела 8:

| что появилось | чем агент это видит и проверяет |
|---|---|
| компоненты `PoseRef`, `AnimatorMotion`, `LegacyLocomotionState` | `entity.get` / `world.query` по имени (поля, хэндлы поз — номер и поколение) |
| клип — номер в библиотеке | **добавлен** `anim.clips` (номер ↔ ID, длительность, цикл, скорость корня) |
| поза — данные подсистемы, не ECS | **добавлен** `anim.pose` (суставы в пространстве персонажа и в мире, счётчики пула) |
| новое видимое — поза Manny | `capture.bundle` (картинка кадра; оверлея скелета нет — этап 5) |
| порядок систем | `ecs.plan`, `ecs.explainSystem` (`core.root-steer` → `core.animator-input` → `locomotion.legacy` → `core.animator-pose` → `core.animation-tick`) |
| сверка с SWORD | `ReferenceTraceTests` (трасса `tests/reference/anim-locomotion.trace.json`, программа — `tests/reference/sword-trace`) |
| новая подсистема | `IStateContributor` и инварианты — этап 3 (их ещё нет в движке); API пула допускает второй экземпляр (дифференциальный прогон) |

Инструменты агента: добавлены `anim.pose`, `anim.clips` (тест `AnimationTests.AgentTools_ShowPoseAndClips`, строки в
`docs/agent-playbook.md`, проверка в `verify.ps1` `agent-smoke`). Инвариантов нет — реестр инвариантов появится на
этапе 3; кандидаты: «поза конечна», «задания к освобождённым позам — 0» (`LastTickStale`), «живых поз — 3 на бойца с
локомоцией, 1 — без неё» (утечка поз).

## Проверки

- `tests/Sword.Engine.Formats.Tests/AnimationFilesTests` — раскладка SWORD байт в байт, обрезка, версия 1.
- `tests/Sword.Engine.Animation.Tests` — библиотека (перенесённые `LibraryTests` SWORD), математика (`MathTests`),
  пул поз: задания побитово равны последовательным вызовам, кинематика с родителем после ребёнка, поколения, поза
  привязки из меша, 0 байт на тик.
- `tests/Sword.Game.Tests/AnimationTests` — выбор клипа по движению, поза = сэмпл клипа, модуль выключен, позы
  возвращаются в пул (и при снятии бойца на барьере его появления), общие ассеты без путей записи, 0 байт на тик,
  инструменты агента; `ReferenceTraceTests` — 900 тиков против SWORD побитово.
- `tests/Sword.Engine.Ecs.Tests/ReadViewTests`, `tests/Sword.Architecture.Tests/PublicSurfaceTests` — виды только для
  чтения.
- `verify.ps1`: `headless-smoke` (бегущий игрок — не в позе привязки; модуль выключен конфигом — в ней),
  `without-modules` (сборка без модуля: без DLL, 10 с, корень идёт, поза привязки), `app-smoke` (Manny по позе под
  GPU-валидацией), `agent-smoke`. Итог Headless `--json` несёт `pose`: суставов, наибольшее отклонение от позы привязки,
  `bindPose` (пустая поза — освобождённая или не выделенная — не поза привязки: проверки «без модуля» впустую не проходят).

## Известные ошибки

Нет.
