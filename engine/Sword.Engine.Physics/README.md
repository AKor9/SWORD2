# Sword.Engine.Physics

Подсистема физики движка (раздел 6 плана, этап 5). Владеет телами-капсулами и сочленениями (наборами тел); наружу —
хэндлы. Игра держит в ECS только хэндл сочленения (`BodyRef` бойца) и заводит его хуком ECS. Сторона Neutral; ссылки —
Diagnostics, Geometry (матрица раздела 4); ни ECS, ни игры подсистема не знает.

## Назначение (точка 5а)

- `IPhysicsWorld` — API физики одной симуляции: сочленения (`CreateArticulation`, `DestroyArticulation`, `BodyCount`,
  `Body`), тела (`Get`, `SetShape`, `Place`, `SetKinematicTarget`, `ArticulationOf`), слоты для хеша, инвариантов и
  описания (`ArticulationCapacity/GenerationAt/At`, `FreeArticulations`, `BodyCapacity/GenerationAt/At`, `FreeBodies`,
  счётчики живых).
- `PhysicsWorld` — эталонная реализация (логика `RigidBody` SWORD за хэндлами, состояния по слотам — AoS).
- `BodyHandle`, `ArticulationHandle` — индекс слота и поколение; поколение 0 — «нет».
- `BodyState` — состояние капсулы: положение, поворот, прошлая постановка, скорость, угловая скорость, радиус, полудлина
  оси (вдоль локальной Y), режим; `Axis`, `EndA`, `EndB`.
- `BodyMode.Kinematic` — единственный режим этапа 5 (тело ставят снаружи, шаг его не двигает).
- `PhysicsMath.RotationVector` — вектор поворота от одного поворота к другому (из него — угловая скорость).

Потребители (5б, игра — `game/Sword.Game/Bodies`): постройка бойца — `SetShape` (форма `Fighter.Build`) и `Place` (капсулы
без скорости по позе постройки), каждый тик — `SetKinematicTarget` (капсулы по позе тика, `KinematicPlacementSystem` в
PrePhysics); наблюдателям — вид игры `IBodyView` поверх `Get`. Побитово сверено с трассой SWORD толпы (13 капсул каждого
бойца каждого тика). Замер: 33 × 13 капсул — 0,027 мс на тик (Release), 0 байт.

## Откуда логика

SWORD 2eab13b, `Sword.Physics`:

| SWORD | SWORD2 | что изменилось |
|---|---|---|
| `RigidBody` (поля `Pos, PrevPos, Vel, AngVel, Rot, PrevRot, Radius, HalfLen`) | `BodyState` за `BodyHandle` | тело — данные подсистемы, а не объект с наследником `Body` игры; добавлен режим |
| `new RigidBody()` | `BodyState.New` | без изменений: всё 0, повороты — единичные |
| `SetShape(mass, radius, halfLen)` | `SetShape(body, radius, halfLength)` | без массы и инерции — они у динамики (этап 7); радиус и полудлина — конечные, ≥ 0, иначе `ArgumentOutOfRangeException` |
| `Place(pos, rot)` | `Place` | без `RenderPos/RenderRot` (поза отрисовки — у клиента) |
| кинематическая ветка `Place` в `DuelArena.PlaceBodies` (игра SWORD) | `SetKinematicTarget(body, pos, rot, dt)` | выражения и порядок те же; `dt` — конечный и > 0 |
| `Xpbd.RotationVector` | `PhysicsMath.RotationVector` | дословно |
| `Axis`, `EndA`, `EndB` | `BodyState.Axis/EndA/EndB` | без изменений |
| тела `Fighter.Bodies` (массив бойца) | сочленение — набор тел за одним хэндлом | новое: время жизни набора — одним хэндлом |

Не перенесено до первых потребителей: `Integrate`, `UpdateVelocity`, `Correct`, `Rotate`, `InvInertiaWorld`, `InvMassAt`,
`PointVelocity`, `ToWorld/ToLocal`, `BodyDynamics`, решатель и суставы `Xpbd` (этап 7 — трупы); `SegSeg`, `SweepCapsule`
(этап 6 — проходы клинка; `SegSeg` — в Geometry); `Grounded` и контакты (этап 7). Тесты `XpbdTests` — с решателем.

## Устройство

- **Слоты.** Тела и сочленения — слоты с поколением: у живого — поколение, у свободного — со знаком минус (от него
  зависит поколение следующего), у ни разу не выданного — 0. Свободные берутся с конца списка; рост — удвоением, новые
  свободные кладутся так, что первыми выдаются младшие номера. Удалённое сочленение возвращает тела с конца: следующее
  того же размера получает те же слоты тем же порядком. Порядок выдачи — часть договора `IPhysicsWorld`: две реализации
  при одних вызовах выдают одни хэндлы, иначе их состояния не сравнить.
- **Устаревший хэндл** — `InvalidOperationException` в любом вызове, кроме `IsAlive`.
- **Память.** Массивы растут только при росте числа слотов; массив тел сочленения остаётся за его слотом и
  переиспользуется. Постановка, создание и удаление сочленений прежних размеров — 0 байт.
- **Две реализации.** Всё состояние — в экземпляре, статического нет: у каждой симуляции свой `PhysicsWorld`, эталон и
  кандидат (SoA, острова — отдельным шагом с микробенчмарком) живут в одном процессе.

## Ограничения

- Только кинематические тела: шага, режима `Simulated`, импульсов, контактов и запросов нет (этапы 6–7).
- Хеш и инварианты подсистемы — у игры (`Sword.Game/Bodies`: участник `physics`, `physics.finite`, `physics.bodyRefs`):
  контракты `IStateContributor` и `IInvariant` — в Runtime, а физика его не видит.
- Проверок внутри шага нет (шага нет).

## Проверки

`tests/Sword.Engine.Physics.Tests` (`PhysicsWorldTests`): слоты и поколения, устаревший хэндл, тот же размер — те же
слоты; новое тело как в SWORD; форма, постановка, концы, цель тика — выражения SWORD побитово; `RotationVector`; два
экземпляра в одном процессе; 0 байт на 1000 тиков 33 × 13 тел с оборотом сочленений. Хеш и инварианты —
`tests/Sword.Game.Tests` (`BodiesTests`, `GameInvariantTests`). Фича — [«Физика и тела»](../../game/Sword.Game/Features/Bodies/README.md).
