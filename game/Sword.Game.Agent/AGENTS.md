# Sword.Game.Agent — правила

Агентский доступ игры: то, что знает только игра. Имена сущностей (`SwordAgent.ArenaNames`: `player`, `enemy N` — враг
на месте N, 1…32, `fighter N` — боец по номеру сущности);
инструменты анимации (`AnimationTools`, этап 2): `anim.pose` — поза бойца по суставам и счётчики пула поз, слой действий
(`action`, 4г: действие, клип с именем, вес, поворот курса клипом; 6в — у всех бойцов, слот удара словами, номер удара, слой
верха, доля клипа в ногах, снимок, что прервано), `anim.clips` — библиотека клипов (номер ↔ ID);
инструменты тел (`PhysicsTools`, 5б): `physics.bodies` — капсулы тела бойца по сегментам (имена, центр, концы, скорости,
форма) через `IBodyView`, вычисляемые поля тела для метрик манифестов и наблюдений — `watch.add`, `entity.history`, `replay.observe`, 5г
(`BodyFields`: `Body.<сегмент>.<поле>`, `Body.MaxSpeed`); инструмент боя (`CombatTools`, 6б): `combat.weapons` — таблица мечей,
инвентарь игрока, меч и клинок каждого бойца, таблица ударов через `ICombatView`; поиск Sword.exe для видов фич (`AppLocator`, 6б:
конфигурация — по папке за `bin` или, вне `bin` — сервер агента в `artifacts/agent-bin`, — по оптимизации сборки Headless).
Ядро игры и модули об агенте не знают; сценарии и пробы боя — со своими этапами.

- Имя — подсказка, не идентичность: в ответах сущность всегда и номером.
- Исполняется в точке обслуживания: только чтение мира. Данные вне ECS — только через виды только для чтения
  (`IAnimationView`, `IBodyView`, `ICombatView`), не через ресурсы-классы симуляции (ресурсы-значения ECS, например
  `PlayerInventory`, читаются как мир).
- Имена — подсказки для запросов: `enemy N` ищется по месту (`FighterSlot`), тест —
  `tests/Sword.Game.Tests/ArenaTests.Agent_NamesEnemiesBySlot_SendsArenaCommand`.
- Новый инструмент — с тестом (`tests/Sword.Game.Tests/AnimationTests.AgentTools_ShowPoseAndClips`,
  `BodiesTests.AgentTools_BodiesBySegment_AndBodyFieldsForMetrics`, `CombatTests.Agent_CombatWeapons_NamesWeaponsBladesAndAttacks`)
  и строкой в `docs/agent-playbook.md`.

Проверка: `verify.ps1` шаг `agent-smoke` (`entity.get player`, клип бега через `anim.clips`, `anim.pose`, `physics.bodies`,
`combat.weapons`).
