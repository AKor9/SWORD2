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
Ядро игры и модули об агенте не знают; урон и смерть (6г) агент видит без своих инструментов — компоненты (`Health`, `Dead`,
`StrikePairs`, `Flinch`), события (`events.history`: `Hit`, `StrikeContact`, `SeverRequested`, `Added<Dead>`) и проба удара командой
(`command.send StrikeProbe`), тест `DamageTests.Agent_ProbeByCommand_WhyDiedByEventsHistory`.

7и: `physics.bodies` расширен (нового инструмента нет) — `joints` (суставы тела трупа) и `trace` (`on`/`off`/`reset`/`show`,
`events` 0…64: трасса физики — диагностический режим `physics.trace` через `ServiceContext.SetDiagnostic`; сводки `last`/`window`/`run`,
события, контакты по источникам) — `PhysicsDebugJson.cs` по виду `IPhysicsDebugView`; тест — `CorpseAgentTests`. Аннотация
`physics.bodies` — «меняет» (trace переключает режим сеанса, не мир); сначала читается тело, затем действие с трассой: ошибка вызова
трассу не переключает.

7з: вычисляемые поля тела целиком — `CorpseFields` (часть `BodyFields`): `Body.Com[.X|.Y|.Z]`, `Body.ComVelocity[…]` (без компоненты —
длина), `Body.KineticEnergy`, `Body.Energy` — по виду тел (`CorpseMetrics` игры); читают `watch.add`, `entity.history`, метрики и — с 7з —
условия (`time.runUntil`, `replay.observe`, проверки манифеста). Тесты — `RagdollCorpseTests.Agent_CorpseFields_…`,
`Agent_RunUntilCorpseAtRest_ByComputedField`.

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
