# Каталог пекарни `Bake/`

Параметры выпечки и пака SWORD2. Сырые исходники (экспорт Unreal, картинки, шрифты) — вне репозитория, на машине;
результат выпечки — игровые ресурсы `Content/` в корне (вне git), пак игры `Content.swpak` собирает из них менеджер
ресурсов. Пекарня — [tools/Sword.Bake](../tools/Sword.Bake/README.md), менеджер — [tools/Sword.Assets](../tools/Sword.Assets/README.md).

| файл | что | в git |
|---|---|---|
| `recipes/<корень>/<путь сырого файла>.bake.json` | рецепт: конвертер, `import`, `notes`, `params` | да |
| `pak.json` | выключенные из пака файлы и каталоги `Content/` (нет файла — в пак идёт всё) | да |
| `imports.json` | происхождение и лицензии импортов (ключ `import` рецепта); скрипты Unreal — [tools/ue](../tools/ue/README.md) | да |
| `bake.user.json` | корни сырых исходников этой машины | нет |

## Корни: `bake.user.json`

```json
{ "roots": { "src": "D:/Temp/GamedevAssets", "assets": "D:/Temp/AssetsDatabase" } }
```

`${src}` — экспорт Unreal `_Export/*` и свои исходники `_Sword/*`, `${assets}` — атлас Gore-пака текстуры среза.
Переменная `BAKE_ROOT_SRC` (`BAKE_ROOT_ASSETS`) перекрывает файл. Рабочее дерево агента — копия файла из основного дерева.

## Рецепты (34)

| тема `Content/` | сырые файлы | конвертер |
|---|---|---|
| `skeletons/ue5_manny` | `_Export/meshes/skeleton.json` — риг остальных рецептов | `skeleton` |
| `animations/<набор>/` | `_Export/skin_gasp`, `skin_sword`, `skin_combat`, `skin_combat_ext` — `manifest.json` | `anim` |
| `meshes/characters/` | `manny.glb`; анатомия `organs/SK_Skeleton.glb`, `SK_SkeletonWithOrgans.glb`, `BrainHighPoly.glb` | `character`, `anatomy` |
| `meshes/weapons/`, `data/weapons/` | `_Export/weapons/*.glb`, `meshes/sword_1h.glb` — меш и зоны крови | `weapon` |
| `textures/blood/`, `fire/`, `innards/`, `anatomy/` | первый (или свой) PNG набора; слои — `layers` | `texture` |
| `fonts/` | `_Sword/fonts/*.ttf` + лицензии | `font` |
| `data/combat/actions.json` | `_Sword/anim/actions.json` | `copy` |

Кровь и анатомия пекутся; из `.swblood` игра читает зону лезвия «blade» (кровь на оружии, модуль `blood`), остальное — для отложенных фич.

## Как печь и собирать пак

```
sword-bake ФАЙЛ                 испечь сырой файл по рецепту (первый раз — спросит параметры)
sword-bake --all                все рецепты: 1088 файлов, ~46 с Debug
sword-assets pack               пак из включённых файлов Content/ (~1 с)
sword-assets                    окно: дерево Content/ с флажками «в пак», предпросмотр, вкладка «Пекарня»
```

- Частота, отбор клипов, флаг цикла, ретаргет — в Unreal (скрипты [tools/ue](../tools/ue/README.md)), затем выпечка
  выгрузки. Лишний клип набора — выключить флажком в менеджере.
- Выпечка перезаписывает файлы своего рецепта и не удаляет чужие: клип, пропавший из выгрузки, удалить из `Content/`
  руками или выключить.
- Номера клипов библиотеки игры — в порядке путей пака: добавленный или выключенный клип сдвигает номера следующих.

## Что после этого с игрой

- Пак игры `Content.swpak` читают игра, Headless, тесты Content, сценарии фич и эталонные реплеи; собран ли он из текущего
  `Content/` и `pak.json` — шаг `content` в `verify.ps1` (`sword-assets check`).
- Реплеи называют контент хешем оглавления пака: перепечённый или выключенный файл меняет хеш — эталонные реплеи
  перезаписать `Sword.Headless --replay F --record F` (совпадение тик в тик — симуляция та же).
- Перепечённая локомоция меняет то, что игра строит из клипов при загрузке: библиотеку клипов и базу Motion Matching.
