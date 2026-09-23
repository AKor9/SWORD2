# Sword.Game.Agent — правила

Агентский доступ игры: то, что знает только игра. Имена сущностей (`SwordAgent.ArenaNames`: `player`, `enemy N` — враг
на месте N, 1…32, `fighter N` — боец по номеру сущности);
инструменты анимации (`AnimationTools`, этап 2): `anim.pose` — поза бойца по суставам и счётчики пула поз, у врага — слой
действий (`action`, 4г: действие, клип с именем, вес, поворот курса клипом), `anim.clips` — библиотека клипов (номер ↔ ID). Ядро игры и модули об агенте не знают; сценарии и пробы боя — со своими этапами.

- Имя — подсказка, не идентичность: в ответах сущность всегда и номером.
- Исполняется в точке обслуживания: только чтение мира. Данные вне ECS — только через виды только для чтения
  (`IAnimationView`), не через ресурсы симуляции.
- Имена — подсказки для запросов: `enemy N` ищется по месту (`FighterSlot`), тест —
  `tests/Sword.Game.Tests/ArenaTests.Agent_NamesEnemiesBySlot_SendsArenaCommand`.
- Новый инструмент — с тестом (`tests/Sword.Game.Tests/AnimationTests.AgentTools_ShowPoseAndClips`) и строкой в
  `docs/agent-playbook.md`.

Проверка: `verify.ps1` шаг `agent-smoke` (`entity.get player`, клип бега через `anim.clips`, `anim.pose`).
