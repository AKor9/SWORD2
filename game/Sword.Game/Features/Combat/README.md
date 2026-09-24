# Бой (этап 6) — точка 6б (подготовка и оружие)

Фича «Бой» принимается целиком на 6ж (раздел 13 плана, «Точки этапа 6»); этот документ растёт по точкам. 6а (зависание
серверов агентов) — инструменты агента, описана в плане («Решено на 6а»). **6б готовит бой, не меняя поведения:** команды
арены перенесены в стадию Begin (подпорки 4а и 4б сняты), слой действий аниматора есть у всех бойцов (у игрока пока пустой),
у каждого бойца меч в руке (враг — случайный меч пака, игрок — `combat.playerWeapon`, T — следующий), клинок каждого тика
(основание, кончик и их положение прошлого тика — отрезок, который клинок заметает за тик: его возьмут проходы клинка 6г),
меч на экране в кисти, ID-буфер «клинок» и оверлей `blades`, таблица ударов (окна урона по кончику клинка) — неизменяемый
ассет. Физического тела у клинка нет (этап 7). Удары, рывок, урон — 6в–6г.

Манифест — [feature.json](feature.json) (сценарий `weapons`, вид `blades`; полный — на 6ж).

## Откуда логика (SWORD 2eab13b)

| SWORD | SWORD2 |
|---|---|
| `Program.cs`: PageUp, PageDown, R, T — `SetEnemyCount`, `Reset`, `NextPlayerWeapon` из цикла кадра до `arena.Tick` | `ArenaCommandSystem` — стадия Begin после `core.prev-transform` (Intent того же тика видит арену после команд); клавиша T — действие `next_weapon`, команда арены `NextWeapon` |
| `CharacterRig.TryLoad`: `content.List("weapons", ".swmesh")` (список отсортирован) → `MakeWeapon`; `DefaultWeaponName = "sword_1h"`, нет его — первый | `WeaponTable.Load` (рядом с `CharacterAssets`, неизменяема, наружу `ReadOnlySpan<WeaponInfo>`), клинок по мешу — `Game.Data/WeaponMesh.Read` |
| `MakeWeapon`: кончик — самая дальняя от кисти вершина меша, основание — `Normalize(tip) · 0,22` | `WeaponMesh.Read` — те же выражения (побитово, `Strikes_MatchSword`) |
| `WeaponInventory` (`Owned` в порядке получения, `Equip`, `EquipNext`), `AttachRig`: всё оружие пака сразу, в руке — `SWORD_WEAPON` или меч пака | ресурс-значение `PlayerInventory` (биты мечей, `Equipped`; `EquipNext` — тот же порядок), конфиг `combat.playerWeapon` |
| `EquipPlayerWeapon`, `NextPlayerWeapon` | `ArenaAction.EquipWeapon` (`ArenaCommand.Weapon`), `ArenaAction.NextWeapon` |
| `Spawn`: `f.WeaponKind = _rng.Next(Rig.Weapons.Length)`; `Reset`: `Player.WeaponKind = Inventory.Equipped` | `ArenaRules.Enemy(…, weapons)` — своя выборка потока `arena.spawn`; `ArenaRules.Player(…, weapon)`; компонент `WeaponSlot` |
| `FighterAnimator.Blade(world, …)`: `hand = Component[Hand] · world`, основание и кончик — в его системе | `BladePlacement.Of` (мир — `BodyPlacement.World`, как у капсул) |
| `PlaceBodies`: `KinPrevBase = first ? bladeBase : KinBase`, `KinBase = bladeBase` (и кончик); `PlaceBuilt` — `first` | `Blade` (Game.Contracts); тик — `BladePlacementSystem` (PrePhysics после `core.kinematic-placement`), постройка — `BladeSetupSystem` (Gameplay) и сборка мира |
| `SkinnedCharacters.Submit`: меч — `renderer.Add(_weapons[kind], …, FlagAttachment)`, палитра из одной матрицы `SkinCutPose.WeaponWorld` (кисть из `DrawnJoints`) | `SkinnedMeshes.AddRigid`; мир меча — `ArenaView` (`FighterView.WeaponWorld` = модельная кисть позы тика · показанный мир бойца) |
| `CharacterRig.ApplySlots` → `Analyse(S(n), thrust, Upper(n))`, `Tips`, `AttackOf`, `AttackInfo` | `StrikeTable.Analyse`, `StrikeTable.Tips`, `StrikeTable.Of`, `AttackInfo` (Game.Contracts) |
| `StrikeWindows.Find` (чистая функция) | `Combat/StrikeWindows.Find` — константы, порядок перебора и вычислений те же (побитово) |
| `FighterAnimator` у каждого бойца (`Kind = None` до удара) | слой действий (`AnimatorAction`, `ActionPoses`) у всех бойцов; у игрока — `None` до 6в |

## Устройство

- **Стадии.** Begin: `core.prev-transform` → `core.arena-command` (команды арены, меч игрока); Intent: `core.player-command`,
  `core.enemy-ai` — уже по арене после команд; Gameplay: `core.fighter-setup`, `core.body-setup`, `core.blade-setup` (клинок
  построенного бойца по позе постройки без прошлого), …; PrePhysics: `core.kinematic-placement` → `core.blade-placement` (клинок
  тика). Постройка места — одна функция (`Fighters.Create`/`Fighters.Rebuild`: новое место, сброс, прибавленное и убавленное в
  одном тике место, возрождение 6г; прошлый корень = новому — телепорт без интерполяции).
- **Компоненты и ресурсы** (Game.Contracts, `Combat.cs`): `WeaponSlot` (номер меча в таблице), `Blade` (основание, кончик,
  прошлые; `Weapon` — каким мечом посчитан, −1 — не поставлен), ресурс `PlayerInventory` (хешируется), `ArenaAction.NextWeapon`,
  `EquipWeapon` и поле `ArenaCommand.Weapon`, `BodySegs.Weapon` = 11 («blade», «клинок»). Вид `ICombatView`: мечи
  (`WeaponInfo`: имя, меш, клинок), меч по умолчанию, сустав кисти, удары (`AttackInfo`: слот, клип, `_Blocked`, длительность,
  укол, слой, окна `StrikeWindow`).
- **Ассеты** (`CharacterAssets`): `Weapons` (`WeaponTable`), `Strikes` (`StrikeTable`), `Hand` (hand_r). Без мечей или кисти
  игра не стартует. В журнал (`combat`) — мечи и разметка ударов.
- **Клинок.** Постройка: клинок по позе постройки в корне постройки, прошлое = нынешнему; первый тик заметает его от позы
  постройки к позе тика (как `PlaceBuilt` SWORD). Тик: прошлое — клинок прошлого тика; сменился меч (`Blade.Weapon` ≠
  `WeaponSlot`) — прошлое = нынешнему. Мир — «как показан боец»: корень и курс после Movement, высота рельефа под корнем (тот
  же мир, что у капсул 5б/5в).
- **Клиент.** Меши мечей грузятся по путям таблицы (`Skinned.LoadMesh`), часть ID-буфера — сегмент 11. Меч каждого бойца —
  `AddRigid(меш, WeaponWorld)` после всех тел (место кончилось — пропадут мечи, а не бойцы); до первого тика — кисть позы
  привязки. Оверлей клинков — F7 (вместе с капсулами), `--overlay blades`, слой снимка `blades` (свой слой: `DebugDraw.Mark`):
  клинок тика (игрок — жёлтый, враги — белые), прошлого тика (серый), стороны пройденного за тик (оранжевые), в слое снимка —
  имя меча у кончика. Клинок оверлея перенесён из мира бойца на тике в показанный (как капсулы): при доле тика 1 — ровно
  клинок симуляции; прошлый — тем же переносом.
- **Render.** `SkinnedMeshes.AddRigid` (палитра из одной матрицы, только статический меш); `DebugDraw.Mark()` и
  `AddPass(…, since)` — проход с отметки (слоты 21–22 — первая вершина отрезков и первая капсула).

## Отличия от SWORD (намеренные)

- **Смена меча не даёт прохода между клинками.** В SWORD `EquipPlayerWeapon` меняет `WeaponKind`, и следующая постановка
  заметает от клинка старого меча к клинку нового — фантомный проход (урон на 6г). В SWORD2 клинок, посчитанный другим мечом,
  ставится без прошлого (прошлое = нынешнему).
- **Неизвестное имя меча игрока — ошибка конфига** (`combat.playerWeapon`); в SWORD `SWORD_WEAPON` с неизвестным именем —
  сообщение и меч по умолчанию.
- **Меч врага — своя выборка счётчикового ГСЧ** (номер со старшего бита, `ArenaRules.WeaponSampleBase`), а не `_rng.Next` в общем
  потоке `Random(7)` после угла, радиуса, зерна и таймера удара: расстановка SWORD2 (корни, курсы, зёрна) не сдвинулась (тест
  `EnemyWeapon_RandomNewSample_SpawnNotShifted_Deterministic`, эталоны — тик в тик).
- **Меш меча со суставами или без клинка — ошибка загрузки** (в SWORD — скиннинг чужими суставами и NaN основания).
- **Масштаба клинка игрока нет** (`SWORD_PLAYER_BLADE` — тест разреза SWORD).
- **Инвентарь — биты номеров** (до 64 мечей), порядок получения — порядок номеров: в SWORD всё оружие выдаётся сразу по порядку
  таблицы — порядок тот же. Выбранный меч недоступен — `EquipNext` берёт первый (как `IndexOf` −1 в SWORD).
- **Команды арены в Begin** — итог тика тот же, что в SWORD (команды из цикла кадра до тика); отличие от SWORD2 4а–6а, где
  команды были в Intent с подпорками.
- **Клинки на экране по F7** — отладочного вида клинков в SWORD нет.

## Решено на 6б

- Команды арены — стадия Begin; подпорки 4а (команда игрока в тике сброса) и 4б (ИИ всех врагов в тике команды) удалены из
  `ArenaCommandSystem`: Intent сам видит арену после барьера Begin. Постройка места — одна функция.
- Таблица оружия — в симуляции рядом с `CharacterAssets`; чтение меша — в Game.Data (`WeaponMesh`): формат `.swmesh` игре
  не виден (матрица раздела 4 — Data видит Formats, Sword.Game — нет).
- Клинок — отрезок в компоненте бойца; физического тела (сегмент 11 в физике) нет — этап 7.
- Таблица ударов — окна по кончику меча по умолчанию для всех мечей (как SWORD), укол — только самый быстрый удар.
- Эталоны SWORD2 перезаписаны; доказательство — «Эталоны и хеши».

## Настройки

- `combat.playerWeapon` — меч игрока при старте (имя файла пака без расширения, без учёта регистра; по умолчанию `sword_1h`);
  неизвестное имя — ошибка конфига с перечнем мечей.
- Ввод: `next_weapon` (T) — следующий меч (секция `input`).
- `Sword.exe --overlay blades` — клинки поверх кадра с первого кадра.

## Зависимости

Ядро игры: Game.Contracts (`Combat.cs`), Game.Data (`WeaponMesh` → Formats), Animation (клипы, пул поз — поза кисти), модуль
Locomotion (поза бойца; без него — поза привязки, клинок и меч всё равно на кисти). Клиент: Render (`AddRigid`, `DebugDraw`).
Контент: `weapons/*.swmesh`, `combat/actions.json`, клипы ударов.

## Ограничения (до своих точек)

- Ударов, рывка, выбора удара, удара ИИ — нет (6в); проходов клинка, урона, смерти, реакций, возрождения — нет (6г);
  `state.why` — 6д; HUD боя, панель инвентаря, клавиши 1–9 — 6е (`EquipWeapon` уже есть командой); полный манифест и бюджеты
  с оружием — 6ж.
- Физического тела клинка, выпадения меча у трупа — нет (этап 7); крови на клинке — нет (этап 12).
- Слой действий у игрока пуст; снимка позы и прерываний нет (6в).

## Сверка с SWORD

`ReferenceTraceTests.Strikes_MatchSword` — трасса `tests/reference/combat-strikes.trace.json` (программа трассы, сценарий
`strikes`: ассеты настоящего `CharacterRig` на эталонном паке): клинки всех 6 мечей, меч по умолчанию, 7 ударов (клип,
`_Blocked`, длительность, кадры, укол, слой, хеш кончика по кадрам, все поля всех окон) — побитово, с первого снятия. Прежние
трассы после пересборки программы — побайтно те же; `ReferenceTraceTests` — без изменений ожиданий.

## Эталоны и хеши

6б: все четыре эталона SWORD2 (`prototype`, `animation`, `enemies`, `bodies`) **перезаписаны** теми же командами
(`Sword.Headless --replay F --record G`, Release, эталонный пак через `SWORD_CONTENT`): новые компоненты (`WeaponSlot`, `Blade`,
слой действий у игрока), ресурс `PlayerInventory`, системы (`core.arena-command@Begin`, `core.blade-setup`, `core.blade-placement`)
и журналы меняют участника `ecs` с тика 0; в заголовке у `ArenaCommand` — поле `Weapon`, в конфиге — секция `combat`. Поведение
не изменилось — доказано против сборки `1d4f694` (HEAD до 6б, отдельный git worktree во временной папке):
- сборка `1d4f694` повторила свои эталоны бит в бит (все хеши всех тиков);
- участники `animation` и `physics` новой сборки совпали с ней **на каждом тике**: 571/571, 751/751, 3001/3001, 2801/2801; `ecs` —
  0 (по причине выше); команды и конфиг в перезаписанных файлах те же (плюс поле `Weapon` = 0 и `combat.playerWeapon`);
- траектории и все прежние компоненты **всех бойцов** — `FighterSlot`, `FighterSeed`, `Transform`, `PrevTransform`,
  `RootVelocity`, `FighterCommand`, `GazeTarget`, `AnimatorMotion`, `PoseRef`, `BodyRef`, `LegacyLocomotionState`, `EnemyAiTerms`,
  `RootContacts` каждого бойца каждого тика (`replay.observe` Headless обеих сборок): 570, 750, 53 400 и 2 800 бойце-тиков, у врагов
  ещё `AnimatorAction` и `ActionPoses` (50 400) — файлы наблюдения совпали побайтно.

Реплеи пользователя, записанные до 6б, расходятся по `ecs` с тика 0; поведением — не расходятся (поле `Weapon` — ноль).

## Что проверить в игре

Те же пункты — `checklist` в [feature.json](feature.json) (тест `FeatureManifestsTests` сверяет).

1. `Sword.exe` — у каждого бойца меч в правой руке; меч идёт с кистью без отставания и дрожи: стойка, бег, повороты, враги, обступившие игрока.
2. У врагов разные мечи (катаны, широкие мечи, одноручный); R — враги строятся заново со своими новыми мечами.
3. T — меч игрока меняется на следующий по кругу (6 мечей пака, после последнего — первый); R меч игрока не сбрасывает; справка F1 называет T.
4. F7 — поверх капсул тел отрезки клинков: у врагов белые, у игрока жёлтый, клинок прошлого тика серый, пройденное за тик — оранжевые стороны; клинок лежит вдоль меча.
5. PageUp / PageDown / R работают как раньше: враги прибавляются по одному, убавляются, арена сбрасывается.

## Агентский доступ

Разбор по правилу раздела 8 плана — хватает ли агенту инструментов увидеть, проверить и отладить 6б:
- **Видеть:** `entity.get` — `WeaponSlot`, `Blade`, `AnimatorAction` игрока; `state.describe ecs` — `PlayerInventory`; новый
  инструмент `combat.weapons` (Game.Agent `CombatTools`, тест `CombatTests.Agent_CombatWeapons_NamesWeaponsBladesAndAttacks`,
  строка в `docs/agent-playbook.md`) — имена мечей вместо номеров, клинок и путь кончика за тик, инвентарь, таблица ударов с
  именами клипов: без него номер меча и окна ударов агенту было не прочитать словами.
- **Проверять:** инварианты `combat.blade` (после PrePhysics: клинок на кисти, конечен, посчитан мечом в руке) и
  `combat.weapons` (после End: меч и клинок у каждого бойца, меч из таблицы, у игрока — выбранный в инвентаре), оба пойманы
  неисправностями (`CombatTests.Fault_CaughtOnItsTick`); `animation.actions` — для всех бойцов; манифест `combat` (сценарий
  `weapons`: мечи у всех, разные у врагов, T, R, T по кругу; вид `blades`); побитовая сверка таблицы ударов с SWORD.
- **Смотреть:** `capture.bundle` — слой `blades` (свой, отдельно от `capsules`) и ID-буфер (пиксели меча — «клинок врага 3»);
  `Sword.exe --overlay blades`, F7; итог `Sword.exe --json` — `weapons` (в руках, отдано рендеру, меч игрока), `blades`.
- **Исправлено попутно (нашла проверка 6а):** сервер агента из `tools/scripts/agent-headless.cmd` (выход в
  `artifacts/agent-bin/headless/<n>`) не находил `Sword.exe` для видов `feature.verify` — искал конфигурацию по папке `bin` в
  пути. Теперь `AppLocator.Find`: вне `bin` — конфигурация по оптимизации сборки Headless (тест
  `CombatTests.AppLocator_FindsAppByBinFolder_OrByOptimizationOutsideBin`).
- **Не хватает (на следующие точки):** причинности (`state.why`, 6д); кончика клинка по кадрам клипа удара агенту не видно
  (только окна; в тесте — хеш) — понадобится с ударами 6в, тогда решить: вычисляемые поля или инструмент.

## Проверки

- `tests/Sword.Game.Tests/CombatTests` — таблица оружия (порядок, клинок по дальней вершине, 22 см), ошибки меша и отсутствия
  мечей, конфиг `combat.playerWeapon`, меч врага своей выборкой (расстановка не сдвинута, детерминизм, разные мечи, сброс),
  T/`EquipWeapon`/R, клинок на кисти каждый тик (побитово `BladePlacement.Of`, прошлое — прошлый тик, до первого тика — поза
  постройки), команды арены в Begin, неисправности `combat.*`, инвентарь, `StrikeWindows` (пик, связка, укол), таблица ударов,
  `combat.weapons`, `AppLocator`.
- `ReferenceTraceTests.Strikes_MatchSword` (Content) — побитово против SWORD.
- `IdBufferGpuTests` (Gpu, Content) — пиксели меча — сегмент 11 «клинок …», слои `capsules` и `blades` раздельно, F7 — капсулы и
  клинки, кадр 0 Б.
- Поправлены под 6б: `EnemyTurnTests` (слой у игрока есть, пустой), `ArenaTests` (поле `Weapon` в реплее), `BodiesTests`
  (`core.arena-command@Begin`), `GameInvariantTests` (список инвариантов; NaN корня доходит и до клинка), `HudTests` (T в
  справке), манифест `enemies` (проверка `player-agile` — действий у игрока нет).
- `verify.ps1`: `app-smoke` — мечи в руках у 11 бойцов, F7 — клинки 11, T в записи Sword.exe — меч игрока `sword_dss`;
  `agent-smoke` — `combat.weapons` (меч игрока `sword_1h`, кончик бегущего движется, 7 ударов); `features` — манифест `combat`.

## Известные ошибки

Нет.
