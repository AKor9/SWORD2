# Sword.Game.Locomotion — правила

Модуль Locomotion (`locomotion`): локомоция клипами у всех бойцов, позже — Motion Matching игрока. Описание —
[README.md](README.md).

- Модуль не видит ядро игры (`Sword.Game`) и другие модули: вход — компоненты и метки `Game.Contracts`, клипы слотов —
  `Game.Data.ActionTable` (ресурс), позы — `PosePool` (ресурс) и `PoseRef` бойца.
- Позу не считает сам: только задания пулу поз, до `core.animator-pose`. Порядок заданий и формулы — как в
  `LegacyLocomotion` SWORD: от них зависит побитовая сверка `ReferenceTraceTests`.
- Свои позы (слой и снимок) модуль заводит сам и сам возвращает (`locomotion.release`); держит их инвариант модуля
  `locomotion.poses` (живые, свои, не итоговая поза бойца). Пул в инварианте — только через `IAnimationView`.
- `Added<Fighter>` у бойца, у которого состояние уже есть, — постройка заново (протокол `Game.Contracts`, сброс арены):
  состояние — с нуля, позы слоя и снимка — те же (иначе утечка). Две записи одного бойца за срабатывание — одна команда.
- Без модуля игра стартует и ходит без клипов (решение 9): кода «на случай отсутствия» нет ни в ядре, ни здесь.

Проверка: `tests/Sword.Game.Tests/AnimationTests`, `ReferenceTraceTests`; `verify.ps1` — `headless-smoke`,
`without-modules`, `agent-smoke`.
