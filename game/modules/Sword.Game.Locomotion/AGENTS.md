# Sword.Game.Locomotion — правила

Модуль Locomotion (`locomotion`): локомоция клипами у всех бойцов, позже — Motion Matching игрока. Описание —
[README.md](README.md).

- Модуль не видит ядро игры (`Sword.Game`) и другие модули: вход — компоненты и метки `Game.Contracts`, клипы слотов —
  `Game.Data.ActionTable` (ресурс), позы — `PosePool` (ресурс) и `PoseRef` бойца.
- Позу не считает сам: только задания пулу поз, до `core.animator-pose`. Порядок заданий и формулы — как в
  `LegacyLocomotion` SWORD: от них зависит побитовая сверка `ReferenceTraceTests`.
- Свои позы (слой и снимок) модуль заводит сам и сам возвращает (`locomotion.release`).
- Без модуля игра стартует и ходит без клипов (решение 9): кода «на случай отсутствия» нет ни в ядре, ни здесь.

Проверка: `tests/Sword.Game.Tests/AnimationTests`, `ReferenceTraceTests`; `verify.ps1` — `headless-smoke`,
`without-modules`, `agent-smoke`.
