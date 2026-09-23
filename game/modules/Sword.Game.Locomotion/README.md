# Модуль Locomotion (`locomotion`)

Ходьба и бег бойцов клипами. Часть 1 (этап 2) — локомоция клипами `LegacyLocomotion` у всех бойцов; часть 2 (этап 8) —
Motion Matching игрока.

## Назначение

Каждый тик по движению бойца (`AnimatorMotion`: сглаженная скорость в системе бойца и «идёт») выбирает клип — стойку,
ходьбу или бег в одну из 8 сторон — и ставит пулу поз задания: сэмпл клипа в слой локомоции, кроссфейд от снимка при
смене клипа, слой — в позу бойца (`PoseRef`). Итоговую позу (прямую кинематику) считает ядро.

## Откуда логика

SWORD 2eab13b, `Sword.Combat/LegacyLocomotion.cs` — без изменений логики и констант:

- бег выше 1,9 м/с, шаг ниже 1,4 м/с (гистерезис походки); сторона — по углу скорости, меняется, только если ушла
  дальше ±0,7 сектора от текущей (0,5 + гистерезис 0,2);
- клип меняется не чаще 0,35 с; кроссфейд 0,25 с (вес снимка — сглаженная ступень `k²(3 − 2k)`, `k = 1 − доля`);
  фаза цикла сохраняется при смене клипа;
- темп — скорость бойца / скорость корневого движения клипа, в пределах 0,5…2,2 (стоя и у клипов медленнее 0,2 м/с — 1).

Что изменилось при переписывании:

| SWORD | SWORD2 |
|---|---|
| `LegacyLocomotion` — объект у каждого `FighterAnimator`, позы в своих массивах | состояние — компонент модуля `LegacyLocomotionState`, позы — в пуле (`Layer` — слой, `From` — снимок) |
| при смене клипа снимок — пересэмплирование текущей позы (`Sample` → `_fromRot`) | задание `Copy(From ← Layer)`: слой ещё держит итог прошлого тика — это та же поза побитово |
| `ILocomotionSource`, `FighterAnimator.SetLocomotion` | нет: модуль пишет позу системой в стадии Animation (раздел 7 плана — без стратегий) |
| `FighterAnimator.Reset` → `Tick(0)` | `LegacyLocomotion.Start`: стойка с нулевого момента без кроссфейда — то же состояние |
| `FootDown` (касание стоп по разметке клипа) | не перенесено: нужно фиксации стоп и следам крови (этапы 9, 12) |

Сверка — `tests/Sword.Game.Tests/ReferenceTraceTests`: 900 тиков сценария игрока против трассы настоящего кода SWORD на
эталонном паке — клип, корень, курс и поза совпадают побитово.

## Устройство

| стадия | система | вид |
|---|---|---|
| Gameplay | `locomotion.setup` (`LegacyLocomotionSetupSystem`) | реактивная `Added<Fighter>`: состояние (командой, без поз); боец построен заново на той же сущности (сброс арены, точка 4а) — состояние с нуля (`Set`, как `FighterAnimator.Reset` SWORD), позы слоя и снимка прежние |
| Animation | `locomotion.legacy` (`LegacyLocomotionSystem`), до `core.animator-pose` | непрерывная: на первом тике бойца — две позы из пула (слой и снимок), дальше выбор клипа, задания пулу |
| End | `locomotion.release` (`LegacyLocomotionReleaseSystem`) | реактивная `Removed<LegacyLocomotionState>`: позы — в пул |

Инвариант модуля `locomotion.poses` (`LocomotionPosesInvariant`, после End, каждый тик): позы слоя и снимка живые,
свои у каждого бойца и не совпадают с его итоговой позой. Пул модуль видит только видом `IAnimationView` (живая поза —
непустые модельные матрицы). Утечку позы ядро не видит — позы модуля лежат в его компоненте, поэтому держит их модуль.

Состояние добавляется на барьере Gameplay, поэтому новый боец ходит с первого же тика (Animation идёт после). Позы
берёт `locomotion.legacy`, а не `locomotion.setup`: к этому моменту состояние уже в мире, и `Removed` при снятии вернёт
их в пул. Если бы позы брала реакция, а команда добавления не сыграла (боец снят на том же барьере, тик прерван), они
утекли бы (тест `AnimationTests.FighterRemovedOnSpawnBarrier_LeaksNoPoses`). Побитово это ничего не меняет: слой
и снимок — поза привязки, пока не сменится клип, а смена — не раньше `MinHold`.

## Настройки

`modules.locomotion.enabled` (по умолчанию `true`). Своих ручек нет: константы SWORD — в `LegacyLocomotion`.
Сборка без модуля — `-p:WithoutLocomotion=true` (`Sword.Game.Bootstrap`).

## Зависимости

`Game.Contracts` (`Fighter`, `PoseRef`, `AnimatorMotion`, метки), `Game.Data` (`ActionTable`: клипы слотов idle,
walk_*, run_*), `Sword.Engine.Animation` (пул поз, клипы), `Sword.Engine.Runtime`, `Sword.Engine.Ecs`. Ядро игры модуль
не видит.

## Без модуля

Ходьбы нет ни у кого: корень движется как обычно, поза бойца — поза привязки меша. Проверки:
- тест `WithoutLocomotion_PoseIsBindPose_RootStillMoves` — модуль выключен конфигом;
- `verify.ps1` `headless-smoke` — `modules.locomotion.enabled=false`: 10 с без ошибок, поза — поза привязки; с модулем —
  не поза привязки (итог Headless `pose.bindPose`, `pose.maxBindDeviation`);
- `verify.ps1` `without-modules` — сильнее выключения: Headless собран с `-p:WithoutLocomotion=true` (ссылки на проект
  нет, `Sword.Game.Locomotion.dll` в выходе нет), 10 с без ошибок, корень ушёл по +X, поза — поза привязки. Скрытая
  зависимость ядра от модуля не соберётся или проявится здесь.

## Ограничения

- `locomotion.setup` реагирует на `Added<Fighter>` в Gameplay: бойцы, созданные в стадиях позже Gameplay (возрождение в
  Aftermath — этап 7), потребуют `NextTickOk<Added<Fighter>>` или переноса настройки — сборка мира на это укажет.
- Поиск кадра (`AnimatorMotion.Search`, с 4г — false, пока поворот на месте ведёт всё тело врага) локомоция клипами не
  использует и в SWORD: под действием она идёт как шла, слой действий смешивается поверх (`core.animator-pose`).
- Motion Matching игрока — этап 8.

## Агентский доступ

Инвариант — `invariant.list {"filter":"locomotion"}`, остановка на нарушении — `time.breakOnInvariant
{"ids":["locomotion.poses"]}` (тест `GameInvariantTests` — общая поза слоя и снимка ловится на своём тике).
Состояние видно `entity.get {"entity":"player","components":["LegacyLocomotionState"]}` (клип — номер; имя —
`anim.clips {"index":N}`), поза — `anim.pose`. Итог анализа по чек-листу раздела 8 — в
[фиче «Анимация»](../../Sword.Game/Features/Animation/README.md).

## Известные ошибки

Нет.
