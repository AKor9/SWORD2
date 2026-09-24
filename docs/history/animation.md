# История: Анимация и локомоция клипами (этап 2) — перенесено из README 2026-09-25

Разделы README до чистки документов (решение пользователя 2026-09-25: README — только текущее состояние, история — в `docs/history/`), дословно; относительные ссылки пересчитаны от этого файла. Текущее состояние — [README](../../game/Sword.Game/Features/Animation/README.md).

Здесь — таблица точек 2а–2в, сверки со SWORD до 7а (трассы, метрика трассы локомоции), «не перенесено до своих этапов» с отметками о сделанном, ограничения и агентский доступ этапа 2 (кандидаты в инварианты до этапа 3), проверки с отметками «до 7а».

## Точки этапа

| точка | что | видно |
|---|---|---|
| 2а. Подсистема | `Formats`: `.swskeleton`, `.swclip` (чтение и запись, байт в байт SWORD; с 7а — своя спецификация раскладки, сверка с писателем SWORD снята); `Engine.Animation`: скелет, библиотека клипов, сэмплинг, смешивание, прямая кинематика, `AnimMath`, пул поз с заданиями | тестами и бенчмарком |
| 2б. Аниматор и модуль | `Game.Data` (таблица действий), ассеты бойца, ядро аниматора (`PoseRef`, `AnimatorMotion`), порядок `KinematicBegin` по стадиям, модуль Locomotion (`LegacyLocomotion`) | `Headless`: игрок играет клипы |
| 2в. Отрисовка и приёмка | вид `IAnimationView`, палитры по позе в рендере, инструменты агента `anim.pose` / `anim.clips`, сверка с SWORD на эталонном паке (снята на 7а) | Manny ходит и бегает клипами |

## Откуда логика — порядок, семантика, сравнения со SWORD (до чистки)

**Порядок вычислений.** В SWORD `KinematicBegin` делает курс и разгон, затем `anim.Tick` по новой скорости, затем
перенос корня. Стадия Animation в плане стоит до Movement, поэтому курс и разгон вынесены в Gameplay, до аниматора, —
порядок тот же, аниматор видит скорость этого тика. Побитовая сверка с SWORD (трасса, снятая программой трассы SWORD:
настоящие `FighterAnimator`, `LegacyLocomotion` и клипы SWORD, курс, разгон и перенос корня — копия `DuelArena`/`PlayerController`) снята на 7а
(ниже); порядок вычислений держат эталон SWORD2 `animation.swreplay` (хеши каждого тика) и тесты.

**Намеренных изменений семантики нет.** Не перенесено до своих этапов: слой действий, маски верха, вздрагивание, рука
и IK кисти (этап 6), фиксация и постановка стоп, `FootDown` (этап 9), Motion Matching и визуальное смещение (этап 8),
капсулы тела по позе (этап 5), оружие в руке (**сделано на 6б**: меч — жёсткое вложение в кисть hand_r позы, фича
[«Бой»](../../game/Sword.Game/Features/Combat/README.md)). Слой действий с 6б есть и у игрока; удары, вид рывка, маски верха и снимок позы — **сделаны на
6в** (фича «Бой», раздел «6в: удар и рывок»; вздрагивание, рука и IK кисти — 6г и этап 10). Поворот на месте клипом у врагов — **сделан на точке 4г** (фича
[«Враги»](../../game/Sword.Game/Features/Enemies/README.md)): первый кусок слоя действий — компонент `AnimatorAction` (действие `Turn`), поза слоя —
`ActionPoses`, логика — `ActionLayer` (`game/Sword.Game/Animation/AnimatorAction.cs`), шаг действия — в
`AnimatorInputSystem`, смешивание всем телом — в `AnimatorPoseSystem`; снимок позы и прерывание действия — **6в**: снимок —
запрос в `AnimatorAction.Snapshots`, копия итоговой позы прошлого тика — `ActionSnapshotSystem` первой в Animation (поза
`ActionPoses.Snap`), слой верха — задание пула `BlendMasked` по маске `ActionRules.UpperMask` (побитово против SWORD —
`anim-actions.trace.json`, тест трассы слоя действий; снято на 7а). Сверка поворота с SWORD — трасса
`anim-turn.trace.json` (тест трассы поворота; снята на 7а).

**Сравнения со SWORD сняты на 7а** (решение 14, свой путь: SWORD — только источник идей). Было: побитовые трассы SWORD из программы
трассы SWORD — локомоция игрока (`anim-locomotion.trace.json`, 900 тиков), поворот врагов на месте
(`anim-turn`), толпа (`anim-crowd`), рельеф (`anim-terrain`), слой действий (`anim-actions`) — тесты трасс, и метрика
трассы локомоции в манифесте (корень и курс против трассы с допуском 0). Всё удалено; поведение не менялось (хеши эталонов SWORD2 те же).
Регрессию держат эталон SWORD2 `animation.swreplay` (сценарий `reference` манифеста — хеши каждого тика; толпа и рельеф — эталоны
`enemies`, `bodies`, поворот — сценарии `turn` фич «Враги» и «Физика и тела»), проверки сценария `locomotion` (до 7а — сценарий трассы SWORD),
тесты свойств — `AnimationTests`, `PoseTests`, `AnimationFilesTests`, `EnemyTurnTests`, `ActionTests`; инварианты `animation.*`,
`locomotion.poses`.

## Ограничения

- Приёмка — манифест и `feature.verify` (с этапа 3д, задним числом); сверки со SWORD нет — тест и метрика трассы
  локомоции SWORD сняты на 7а (решение 14): сценарий `locomotion` и эталон SWORD2 проверяют своё поведение, не схожесть со SWORD.
- Сэмплинг последовательный (7,9 мкс на бойца за тик при кроссфейде, 33 бойца — 0,26 мс; бенчмарк `PoseSampling`).
- Отладочной отрисовки скелета (линии суставов поверх меша) нет — появится с debug draw (этап 5).

## Агентский доступ

Приёмка (этап 3д) — [feature.json](../../game/Sword.Game/Features/Animation/feature.json): сценарий `locomotion` (900 тиков командами; до 7а — сценарий трассы SWORD с метрикой
корня и курса против трассы SWORD, снята) — проверки клипов (стойка, бег, стороны); эталон SWORD2 анимации (сценарий `reference` —
хеши каждого тика); инварианты `animation.*`, `locomotion.poses`; виды сценария `locomotion` — стойка, бег, бег назад; бюджет стадии
Animation. `Sword.Headless --feature animation` или `feature.verify {"id":"animation"}`; запись сценария —
`artifacts/features/animation/scenario-locomotion.swreplay`.

Анализ по чек-листу раздела 8:

| что появилось | чем агент это видит и проверяет |
|---|---|
| компоненты `PoseRef`, `AnimatorMotion`, `LegacyLocomotionState` | `entity.get` / `world.query` по имени (поля, хэндлы поз — номер и поколение) |
| клип — номер в библиотеке | **добавлен** `anim.clips` (номер ↔ ID, длительность, цикл, скорость корня) |
| слой действий врага (4г) | `anim.pose` — поле `action` (действие, клип с именем, время, вес, `yawDelta`, `rootDelta`); `entity.get` → `AnimatorAction`; инвариант `animation.actions` |
| поза — данные подсистемы, не ECS | **добавлен** `anim.pose` (суставы в пространстве персонажа и в мире, счётчики пула) |
| новое видимое — поза Manny | `capture.bundle` (картинка кадра; оверлея скелета нет — этап 5) |
| порядок систем | `ecs.plan`, `ecs.explainSystem` (`core.root-steer` → `core.animator-input` → `locomotion.legacy` → `core.animator-pose` → `core.animation-tick`) |
| сверка с SWORD | снята на 7а (был тест трассы: `anim-locomotion.trace.json`, программа трассы SWORD); регрессию агент видит `feature.verify` (эталон SWORD2 `animation.swreplay`, сценарий `locomotion`), `replay.check` |
| новая подсистема | участник хеша `animation` (`AnimationState`, этап 3а — [Детерминизм](../../game/Sword.Game/Features/Determinism/README.md)): пул поз в `state.hash`, позы по суставам — `state.describe`; инварианты — `animation.finite`, `animation.settled`, `animation.pool`, `animation.owners` (3г, `AnimationInvariants`), у локомоции — `locomotion.poses`; API пула допускает второй экземпляр (дифференциальный прогон) |

Инструменты агента: добавлены `anim.pose`, `anim.clips` (тест `AnimationTests.AgentTools_ShowPoseAndClips`, строки в
`docs/agent-playbook.md`, проверка в `verify.ps1` `agent-smoke`). Инвариантов нет — реестр инвариантов появится на
этапе 3; кандидаты: «поза конечна», «задания к освобождённым позам — 0» (`LastTickStale`), «живых поз — 3 на бойца с
локомоцией, 1 — без неё» (утечка поз).

7а (сравнения со SWORD сняты, решение 14): вид «метрика против трассы SWORD» снят — нового не требуется: регрессию
агент видит `feature.verify animation` (проверки сценария `locomotion`, эталон SWORD2 — хеши каждого тика), `anim.pose` / `anim.clips`,
инварианты `animation.*`, `locomotion.poses`.

## Проверки

- `tests/Sword.Engine.Formats.Tests/AnimationFilesTests` — раскладка формата пака поле за полем (`Clip_LayoutV2_FieldByField`,
  `Skeleton_LayoutV1_FieldByField_AndRoundTrip`; до 7а — байт в байт против копии писателя SWORD), обрезка, версия 1.
- `tests/Sword.Engine.Animation.Tests` — библиотека (перенесённые `LibraryTests` SWORD), математика (`MathTests`),
  пул поз: задания побитово равны последовательным вызовам, кинематика с родителем после ребёнка, поколения, поза
  привязки из меша, 0 байт на тик.
- `tests/Sword.Game.Tests/AnimationTests` — выбор клипа по движению, поза = сэмпл клипа, модуль выключен, позы
  возвращаются в пул (и при снятии бойца на барьере его появления), общие ассеты без путей записи, 0 байт на тик,
  инструменты агента; тест трассы локомоции SWORD (900 тиков побитово) — снят на 7а. Эталон SWORD2 `animation.swreplay` —
  `ReferenceReplaysTests`, `verify.ps1` `replay-smoke`.
- `tests/Sword.Engine.Ecs.Tests/ReadViewTests`, `tests/Sword.Architecture.Tests/PublicSurfaceTests` — виды только для
  чтения.
- `verify.ps1`: `headless-smoke` (бегущий игрок — не в позе привязки; модуль выключен конфигом — в ней),
  `without-modules` (сборка без модуля: без DLL, 10 с, корень идёт, поза привязки), `app-smoke` (Manny по позе под
  GPU-валидацией), `agent-smoke`. Итог Headless `--json` несёт `pose`: суставов, наибольшее отклонение от позы привязки,
  `bindPose` (пустая поза — освобождённая или не выделенная — не поза привязки: проверки «без модуля» впустую не проходят).
