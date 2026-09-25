# Пекарня игры: плагин `Sword.Bake.Game`

Плагин проекта пекарни (`bake.project.json → plugins`, **после** `Sword.Bake.Engine`): обработчики ассетов игры —
клинковое оружие (`weapon.static`: статический меш в кисти и зоны крови `.swblood`) и анатомия на риге персонажа
(`anatomy.mesh`: скелет, органы, мозг). Конвертеры — `Anim/`, обработчики — `Processors/`. Опирается на открытый API плагина
движка ([tools/Sword.Bake.Engine](../Sword.Bake.Engine/README.md): glTF, raw-экспорт, риг, хват, прореживание,
`ContentFiles`) так же, как на ядро ([tools/Sword.Bake](../Sword.Bake/README.md)). Проект — [Bake/](../../Bake/README.md).

Кровь и анатомия как фичи игры отложены (решения 15, 17), но пекутся: пак полон и сверяется с эталоном целиком.

## Откуда

SWORD 2eab13b, `tools/Sword.Bake.Sword` — игровая часть; логика выпечки не менялась. Журнал переноса —
[docs/history/baker.md](../../docs/history/baker.md), «Б5».

| SWORD | SWORD2 | что изменилось |
|---|---|---|
| `Anim/WeaponBake.cs`, `Processors/WeaponStaticProcessor.cs` | `Anim/`, `Processors/` | пространства имён `Sword.Bake.Game.*`; хват — `SkinBake.GripAttachment` плагина движка; меш пишет `SkinnedMeshData` (Formats) |
| `Anim/WeaponBloodBake.cs`, формат `.swblood` | `Anim/WeaponBloodBake.cs`; формат — `WeaponBloodData` в `game/Sword.Game.Data` | файл пишет писатель формата (байт в байт эталон) |
| `Anim/AnatomyBake.cs`, `Processors/AnatomyMeshProcessor.cs` | `Anim/`, `Processors/` | `MeshDecimate`, `SpineChain` — открытый API плагина движка |
| — | `Anim/MeshEdges.cs` | замкнутость сетки после прореживания — строка лога |

## Обработчики

| обработчик | параметры | выход |
|---|---|---|
| `weapon.static` (`WeaponStaticProcessor`) | `mesh` — glTF статики (`tools/ue/export_weapons.py`), `skeleton` — ссылка на риг (`anim.skeleton`: кулак его эталонных клипов с перенесёнными пальцами), `grip` — середина хвата, см от навершия (пусто — середина рукояти), `unit` — см в единице glTF, `blood` — зоны крови (`shape` `blade`, `handleEnd`, `bladeStart`, `materials` — слот материала → зона или `none`, клетки лезвия и рукояти), `out` | `weapons/<имя>.swmesh` в системе кисти персонажа и рядом `weapons/<имя>.swblood` |
| `anatomy.mesh` (`AnatomyMeshProcessor`) | `mesh` — glTF анатомии пака (`tools/ue/export_organs.py`), `skeleton` — риг, `skin` — меш персонажа (`skin.mesh`: анатомия прячется под кожу на `margin` см), `materials` — имена материалов по номеру glTF, `keep` — оставить только эти, `attachBone`/`attachCenter`/`attachSize` — статика на кость (мозг), `maxTriangles` — бюджет прореживания, `unit`, `out` | `<ID>.swmesh` на риге персонажа (та же палитра и плоскости разрезов, что у кожи) |

Как печётся:
- **Оружие.** Меш приводится к системе меча (клинок по +Z от навершия, кромка +Y, толщина X): ось клинка — главная ось
  разброса вершин, гарда — самое широкое сечение, рукоять — с её стороны, у изогнутого клинка кромка — от обуха. Затем
  крепление по кулаку анимаций рига и отображение UE → персонаж. Зоны крови: рукоять — цилиндр до гарды, гарда — без
  крови, лезвие — плоскость от гарды до острия; слоты материалов уточняют зону.
- **Анатомия.** Меш пака скинирован на свой скелет (UE4 Mannequin); вершины переносятся линейным скиннингом из bind-позы
  источника в позу покоя одноимённых костей рига, позвоночник (3 позвонка UE4 → 5 UE5) — по доле цепочки (`SpineChain`).
  Больше `maxTriangles` — прореживание схлопыванием рёбер (`MeshDecimate`; швы, края и секции не трогаются), замкнутость —
  строка лога.

## Зависимости

`Sword.Bake` и `Sword.Bake.Engine` (`Private=false`, `ExcludeAssets=runtime`: одна копия сборки, один тип `SkeletonParams`
у `ctx.Ref`), `Engine.Formats` (писатель `.swmesh`), `Game.Data` (формат `.swblood`). Сторона `Neutral`. NuGet — нет.
Выход — `bin/<конфигурация>/Sword.Bake.Game.dll` (без TFM), `EnableDynamicLoading`. В `plugins` раньше плагина движка —
ошибка проекта «ссылается на плагин Sword.Bake.Engine, а стоит в plugins раньше него».

## Проверки

- `dotnet test tests/Sword.Bake.Game.Tests` — оба плагина в `sword-bake` процессом (движок, затем игра: анатомия на
  синтетическом риге через `ctx.Ref<SkeletonParams>`; обратный порядок — ошибка проекта), зоны крови (слоты материалов
  перекрывают геометрию, круг записи формата).
- Категория **Source** (исходники — как у плагина движка): временный проект с манифестами рига, меша и шести оружий, трёх
  анатомий, `combat/actions`; сверка с эталонным паком — оружие 6, `.swblood` 6, анатомия 3, `combat/actions.json` — байт в
  байт; итог — `artifacts/bake/source-check.json`, ключи `game*`.
- Писатель `.swblood` против эталона — `ReferencePakWritersTests` (`tests/Sword.Engine.Formats.Tests`, категория Content).
- Полная выпечка `Bake/` и `sword-bake diff` с эталоном — [Bake/README.md](../../Bake/README.md).

## Агентский доступ

Итог анализа (раздел 8 плана): как у плагина движка — `--json` ядра (`status`, `build`, `diff`), тест Source с
`source-check.json`, лог выпечки (оси и хват оружия, зоны крови, перенос анатомии, прореживание и замкнутость сетки).
Инструмент MCP не нужен.

## Ограничения

- Форма оружия для зон крови — только `blade` (меч, катана); топор, молот, посох, щит — не сделаны.
- Предпросмотров Studio нет — Б7.

## Известные ошибки

Нет.
