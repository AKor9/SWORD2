# Анимация и локомоция клипами (этап 2)

Персонаж ходит и бегает клипами: стойка, ходьба и бег в 8 сторон с кроссфейдом и темпом по скорости — как игрок SWORD
без Motion Matching. Так ходят враги всегда и игрок при `locomotion.player = clips` (значение в коде: тесты, сценарии фич, эталоны); в игре
(`sword.json`) игрок — на Motion Matching (этап 8, модуль Locomotion). Позы считает подсистема `Sword.Engine.Animation` (пул поз, задания пачкой), выбор клипа — модуль
Locomotion, аниматор бойца (вход слоя движения, слой действий, итоговая поза) — ядро игры, отрисовка — клиент по позе тика.
Этап сделан (точки 2а–2в); история (точки, сверки со SWORD до 7а) — [docs/history/animation.md](../../../../docs/history/animation.md).

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
порядок тот же, аниматор видит скорость этого тика. Порядок держат эталон SWORD2 `animation.swreplay` (хеши каждого тика)
и тесты.

**Слой действий** (намеренных изменений семантики локомоции нет): поворот врага на месте клипом — точка 4г (фича
[«Враги»](../Enemies/README.md)): компонент `AnimatorAction` (действие `Turn`), поза слоя — `ActionPoses`, логика —
`ActionLayer` (`game/Sword.Game/Animation/AnimatorAction.cs`), шаг действия — в `AnimatorInputSystem`, смешивание всем
телом — в `AnimatorPoseSystem`. С 6б слой действий есть и у игрока, меч — жёсткое вложение в кисть hand_r позы; удары, вид
рывка, маски верха, снимок позы и прерывание действия — 6в, вздрагивание — 6г (фича [«Бой»](../Combat/README.md)): снимок —
запрос в `AnimatorAction.Snapshots`, копия итоговой позы прошлого тика — `ActionSnapshotSystem` первой в Animation (поза
`ActionPoses.Snap`), слой верха — задание пула `BlendMasked` по маске `ActionRules.UpperMask`.

Не перенесено: фиксация и постановка стоп, `FootDown` (этап 9); визуальное смещение — не переносится (решение этапа 8); рука и IK
кисти — с блоком (Guard отложен — решение 16). Motion Matching игрока — модуль Locomotion (этап 8,
[README модуля](../../../modules/Sword.Game.Locomotion/README.md), «Motion Matching игрока в тике (8е)»).

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

- Приёмка — манифест и `feature.verify`: сценарий `locomotion` и эталон SWORD2 проверяют своё поведение, не схожесть со SWORD.
- Сэмплинг последовательный (7,9 мкс на бойца за тик при кроссфейде, 33 бойца — 0,26 мс; бенчмарк `PoseSampling`).
- Отладочной отрисовки скелета (линии суставов поверх меша) нет.

## Что проверить в игре

Те же пункты — `checklist` в [feature.json](feature.json) (тест `FeatureManifestsTests` сверяет). В игре (`sword.json`) игрок — на Motion
Matching (этап 8): пункты про игрока — с `--set locomotion.player=clips`; враги — клипами всегда.

1. `Sword.exe` — Manny стоит в боевой стойке одноручного хвата (клип `1HM_Idle_Standing`; с 6б — с мечом в руке), слегка
   покачивается, а не стоит в T-позе.
2. W (курсор перед Manny) — разгоняется: пара шагов ходьбой, затем бег вперёд; ноги не скользят по полу.
3. С зажатым Shift — шагом, заметно медленнее, шаги соответствуют скорости.
4. Курсор держать перед Manny и бежать A или D — бег боком (стрейф), S — бег спиной вперёд; смена направления — плавная,
   без рывков позы.
5. Отпустить клавиши — за полсекунды возвращается в стойку, переход мягкий.
6. Бег по кругу вокруг курсора — ноги переключаются между направлениями без дёрганья (гистерезис сторон).

## Агентский доступ

Приёмка — [feature.json](feature.json): сценарий `locomotion` (900 тиков командами) — проверки клипов (стойка, бег,
стороны); эталон SWORD2 анимации (сценарий `reference` — хеши каждого тика); инварианты `animation.*`, `locomotion.poses`;
виды сценария `locomotion` — стойка, бег, бег назад; бюджет стадии Animation. `Sword.Headless --feature animation` или
`feature.verify {"id":"animation"}`; запись сценария — `artifacts/features/animation/scenario-locomotion.swreplay`.

| что | чем агент это видит и проверяет |
|---|---|
| компоненты `PoseRef`, `AnimatorMotion`, `LegacyLocomotionState` (враги; игрок — при `locomotion.player = clips`), `MotionMatchState` (игрок на Motion Matching, 8е) | `entity.get` / `world.query` по имени (поля, хэндлы поз — номер и поколение) |
| клип — номер в библиотеке | `anim.clips` (номер ↔ ID, длительность, цикл, скорость корня) |
| слой действий | `anim.pose` — поле `action` (действие, клип с именем, время, вес, `yawDelta`, `rootDelta`); `entity.get` → `AnimatorAction`; инвариант `animation.actions` |
| поза — данные подсистемы, не ECS | `anim.pose` (суставы в пространстве персонажа и в мире, счётчики пула) |
| поза Manny на экране | `capture.bundle` (картинка кадра; оверлея скелета нет) |
| порядок систем | `ecs.plan`, `ecs.explainSystem` (`core.root-steer` → `core.animator-input` → `locomotion.legacy` (и `locomotion.mm` игрока на MM) → `core.animator-pose` → `core.animation-tick`) |
| регрессия | `feature.verify animation` (эталон SWORD2 `animation.swreplay`, сценарий `locomotion`), `replay.check` |
| подсистема-участник | участник хеша `animation` (`AnimationState`, [Детерминизм](../Determinism/README.md)): пул поз в `state.hash`, позы по суставам — `state.describe`; инварианты — `animation.finite`, `animation.settled`, `animation.pool`, `animation.owners` (`AnimationInvariants`), у локомоции — `locomotion.poses`; API пула допускает второй экземпляр (дифференциальный прогон) |

Инструменты агента `anim.pose`, `anim.clips` — тест `AnimationTests.AgentTools_ShowPoseAndClips`, строки в
`docs/agent-playbook.md`, проверка в `verify.ps1` `agent-smoke`.

## Проверки

- `tests/Sword.Engine.Formats.Tests/AnimationFilesTests` — раскладка формата пака поле за полем (`Clip_LayoutV2_FieldByField`,
  `Skeleton_LayoutV1_FieldByField_AndRoundTrip`), обрезка, версия 1.
- `tests/Sword.Engine.Animation.Tests` — библиотека (перенесённые `LibraryTests` SWORD), математика (`MathTests`),
  пул поз: задания побитово равны последовательным вызовам, кинематика с родителем после ребёнка, поколения, поза
  привязки из меша, 0 байт на тик.
- `tests/Sword.Game.Tests/AnimationTests` — выбор клипа по движению, поза = сэмпл клипа, модуль выключен, позы
  возвращаются в пул (и при снятии бойца на барьере его появления), общие ассеты без путей записи, 0 байт на тик,
  инструменты агента; `EnemyTurnTests`, `ActionTests` — слой действий. Эталон SWORD2 `animation.swreplay` —
  `ReferenceReplaysTests`, `verify.ps1` `replay-smoke`.
- `tests/Sword.Engine.Ecs.Tests/ReadViewTests`, `tests/Sword.Architecture.Tests/PublicSurfaceTests` — виды только для
  чтения.
- `verify.ps1`: `headless-smoke` (бегущий игрок — не в позе привязки; модуль выключен конфигом — в ней),
  `without-modules` (сборка без модуля: без DLL, 10 с, корень идёт, поза привязки), `app-smoke` (Manny по позе под
  GPU-валидацией), `agent-smoke`. Итог Headless `--json` несёт `pose`: суставов, наибольшее отклонение от позы привязки,
  `bindPose` (пустая поза — освобождённая или не выделенная — не поза привязки: проверки «без модуля» впустую не проходят).

## Решения и почему

- Курс и разгон — в Gameplay, до аниматора: стадия Animation стоит до Movement, порядок `KinematicBegin` SWORD сохранён.
- Позы — в пуле подсистемы (вместо массивов в `FighterAnimator` SWORD): слои ставят задания, пул исполняет их пачкой в
  `core.animation-tick` (раздел 6 плана); ассеты общие и неизменяемые, пул — у каждой симуляции свой.
- Поза между тиками не интерполируется, как в SWORD; плавно идёт корень.
- Сравнения со SWORD сняты на 7а (решение 14): трассы SWORD и метрика трассы локомоции удалены, поведение не менялось
  (хеши эталонов SWORD2 те же) — [история](../../../../docs/history/animation.md).

## Известные ошибки

Нет.
