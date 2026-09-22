# Sword.Game.Agent — правила

Агентский доступ игры: то, что знает только игра. Имена сущностей (`SwordAgent.ArenaNames`: `player`, `fighter N`);
инструменты анимации (`AnimationTools`, этап 2): `anim.pose` — поза бойца по суставам и счётчики пула поз, `anim.clips`
— библиотека клипов (номер ↔ ID). Ядро игры и модули об агенте не знают; сценарии и пробы боя — со своими этапами.

- Имя — подсказка, не идентичность: в ответах сущность всегда и номером.
- Исполняется в точке обслуживания: только чтение мира. Данные вне ECS — только через виды только для чтения
  (`IAnimationView`), не через ресурсы симуляции.
- Новый инструмент — с тестом (`tests/Sword.Game.Tests/AnimationTests.AgentTools_ShowPoseAndClips`) и строкой в
  `docs/agent-playbook.md`.

Проверка: `verify.ps1` шаг `agent-smoke` (`entity.get player`, клип бега через `anim.clips`, `anim.pose`).
