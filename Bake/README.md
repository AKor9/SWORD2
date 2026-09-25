# Проект пекарни `Bake/`

Всё, из чего пекарня собирает пак контента SWORD2: файл проекта, 29 манифестов ассетов и реестр импортов. Исходники
(экспорт Unreal, картинки, шрифты) — вне репозитория, на машине; результат — пак игры `Content.swpak` в корне репозитория
(вне git). Ядро и
консоль `sword-bake` — [tools/Sword.Bake/README.md](../tools/Sword.Bake/README.md), обработчики — плагины
[tools/Sword.Bake.Engine](../tools/Sword.Bake.Engine/README.md) и [tools/Sword.Bake.Game](../tools/Sword.Bake.Game/README.md).

Главное назначение сейчас — **перепечь анимацию**, если с Motion Matching будут проблемы. Игра читает пак пекарни
`Content.swpak`; он сверен с эталонным паком `Content.reference.swpak` (ниже).

## Что здесь

| файл | что | в git |
|---|---|---|
| `bake.project.json` | имя, каталог манифестов (`.`), пак `../Content.swpak`, плагины по порядку (`Sword.Bake.Engine`, затем `Sword.Bake.Game`; `${config}` — конфигурация `sword-bake`), `strictLayout` | да |
| `**/*.bake.json` | манифест ассета: обработчик, `params`, `import`, `notes`; папки манифестов = папки пака | да |
| `bake.imports.json` | происхождение и лицензии импортов (`sword-bake licenses`); скрипты Unreal — [tools/ue](../tools/ue/README.md) | да |
| `reference.expect.json` | ожидаемые расхождения с эталонным паком для `diff` (сейчас правил нет) | да |
| `bake.user.json` | корни исходников этой машины | нет |
| `.bake/` | кэш хешей исходников и ошибок, копии удалённых манифестов | нет |

Ассеты (ID = путь манифеста без `.bake.json`):

| папка | ассеты | обработчик (плагин) |
|---|---|---|
| `skeletons/` | `ue5_manny` — риг | `anim.skeleton` (Engine) |
| `characters/manny/` | `mesh` — меш Manny; `anatomy/skeleton`, `organs`, `brain` — анатомия; `anatomy/albedo`, `normals` — её текстуры | `skin.mesh`, `anatomy.mesh` (Game), `texture.array` |
| `anim/` | `gasp` (набор `locomotion`), `1hm_sword` и `combat_master` (`sword_1h`), `combat_master_ext` (`katana`, `sword_shield`, `twin_swords`, `unarmed`) | `anim.pack` (Engine) |
| `weapons/` | `combat_sword`, `katana`, `katana_v2`, `powerful_sword`, `sword_1h`, `sword_dss` — меш и зоны крови | `weapon.static` (Game) |
| `effects/` | `blood/directional`, `noise`, `omni`, `puddles`, `spots`; `innards/cut_albedo` | `texture.array` (Engine) |
| `ui/fonts/` | `body`, `head`, `label`, `num`, `num_light` (+ лицензии гарнитур) | `font.vector` (Engine) |
| `combat/` | `actions` — `combat/actions.json` (действия боя) | `file.copy` (ядро) |

Кровь и анатомия пекутся, хотя фичи отложены (решения 15, 17): пак полон и сверяется с эталоном целиком.

## Корни исходников: `bake.user.json`

Пути в манифестах — через корни: `${src}` (экспорт Unreal `_Export/*`, свои исходники `_Sword/*`) и `${assets}` (атлас
Gore-пака для `effects/innards/cut_albedo`). Корни машины — в `bake.user.json` рядом с проектом (вне git):

```json
{
  "roots": {
    "src": "D:/Temp/GamedevAssets",
    "assets": "D:/Temp/AssetsDatabase"
  }
}
```

Файл пишет `sword-bake root src D:/Temp/GamedevAssets` (так же `assets`); переменная `BAKE_ROOT_SRC` (`BAKE_ROOT_ASSETS`)
перекрывает его. Корни входят в хеш параметров ассета: другой корень — ассеты устаревают, а `diff` с эталоном покажет
`paramsHash` с пометкой о корнях (эталон собран с корнями выше).

## Как печь

Консоль — `tools/Sword.Bake.Cli/bin/<конфигурация>/net10.0-windows10.0.22000.0/sword-bake.exe`; плагины той же конфигурации
собирает `dotnet build SWORD.slnx` (или `-c Release`). В каталоге `Bake/` (или с `--project Bake`):

```
sword-bake status [--json]          что устарело и почему; код 2 — пак не актуален
sword-bake build [шаблоны] [--json] собрать устаревшее с зависимостями и записать пак
sword-bake pak --verify             целостность пака
```

Полная выпечка с нуля — 29 ассетов, 1082 файла, пак 421,9 МБ; по `ядра/2` заданий: Debug ~31 с (дольше всех — текстуры
анатомии, по 25–27 с), Release ~9 с. Ошибка любого ассета — пак не пишется, прежний цел; ничего не пересобрано — пак не
переписывается. Отпечаток кода обработчика — MVID его сборок; номера коммита у библиотек нет ни в версии, ни в PDB
(`Directory.Build.targets`), поэтому коммит без правки кода ничего не устаревает. MVID зависит от конфигурации и от пути
дерева: смена Debug ↔ Release или пак, испечённый в другом дереве, устаревают все ассеты («изменился код обработчика»), но
клипы не перепекаются (`SourceHash` без сборок): пересобираются остальные, файлы контента выходят те же, другой — только
`.bake/build.json` (в хеш контента он не входит).

## Сверка с эталоном

```
sword-bake diff ..\Content.reference.swpak ..\Content.swpak --expect reference.expect.json [--json]
```

Запускать в `Bake/`: сравнитель `.swclip` приходит из плагина проекта. Итог полной выпечки: 34 файла равны байтами,
1048 клипов равны по содержимому (отличается только `SourceHash`: в эталоне в него входил MVID конвертера SWORD),
сведения о сборке `.bake/build.json` равны семантически (29 ассетов; время и сборки в отпечатке не сравниваются),
неожиданных расхождений 0, ожидаемых 0; код 0. Паки Debug и Release между собой — 1082 файла байт в байт.
`reference.expect.json` — список правил «ожидаемо отличается / отсутствует» с причиной; новое правило — только с понятой
причиной (формат — README ядра, «Файл ожидаемых расхождений»).

## Перепечь только анимацию

- Собрать один набор или все: `sword-bake build anim/gasp`, `sword-bake build anim` (шаблон — ID, папка, маска,
  `processor:anim.pack`). Остальные ассеты берутся из прежнего пака.
- Ассет пересобирается, когда изменились его входы (raw-экспорт в `${src}/_Export/skin_*`), параметры (`sets` манифеста,
  риг `skeletons/ue5_manny`) или код обработчика; `--force` пересобирает и актуальные. Клип перепекается, только если
  изменился его `SourceHash` (исходник, параметры клипа, пальцы, `AnimImport.ToolVersion`): **правка кода выпечки клипа —
  поднять `ToolVersion`**, иначе клипы останутся прежними (README `Bake.Engine`, «`SourceHash` клипа»).
- Частота, отбор клипов, флаг цикла, ретаргет — только в Unreal: скрипты [tools/ue](../tools/ue/README.md), затем выпечка.
- Проверить: `sword-bake diff` с эталоном (выше) покажет, какие клипы и чем отличаются (`hash[]`, `detail` — поле и кадр);
  тест `dotnet test tests/Sword.Bake.Engine.Tests --filter Category=Source` — выпечку без правок.

## Что после этого с игрой

- Пак пекарни — **пак игры**: игра, Headless, тесты Content, сценарии фич и эталонные реплеи читают `Content.swpak` в корне
  (`ContentLocator` берёт его раньше эталонного). Реплеи и квитанции агента называют контент хешем оглавления
  (`PakSource.ContentHash`) без `.bake/`: перевыпечка тех же файлов — тот же хеш; эталоны записаны на паке пекарни.
- Пака нет (новый клон, рабочее дерево агента) — `ContentLocator` берёт эталонный пак с предупреждением в журнале
  (категория `content`: реплеи на нём отвергаются по хешу контента), тесты Content падают с командой выпечки, шаг `content`
  в `verify.ps1` — ошибка с ней же. Получить пак: `sword-bake build` здесь (Debug ~31 с, нужны исходники) или жёсткая
  ссылка на `Content.swpak` основного дерева (игре и тестам годится; `status` в другом дереве считает её устаревшей — MVID
  зависит от пути дерева, — поэтому шагу `content` нужен свой пак).
- Эталонный пак — основа сверки (`diff` выше, `reference.ps1 -Check`); посмотреть его в игре — `SWORD_CONTENT=<путь>`
  у процесса игры (эталоны на нём не сверяются).
- Перепечённая локомоция (`anim/locomotion/*`) меняет то, что игра строит из клипов **при загрузке**: библиотеку клипов и
  базу Motion Matching (`GaspMotionBase`: отбор клипов, признаки, модель скоростей) — отдельной выпечки базы нет. Номера
  клипов библиотеки — в порядке путей пака (`anim/<набор>/<имя>.swclip`): добавленный или убранный клип сдвигает номера
  следующих.

## Studio

Окно пекарни — `tools/Sword.Bake.Studio` ([README](../tools/Sword.Bake.Studio/README.md)): из корня репозитория
`tools\Sword.Bake.Studio\bin\Debug\net10.0-windows10.0.22000.0\Sword.Bake.Studio.exe --project Bake` (или без ключа, запустив
в `Bake/`). Показывает ассеты со статусами, форму параметров (Ctrl+S пишет манифест), исходники и выходы, зависимости,
предпросмотр выходов из пака (`Content.swpak` в корне: до первой сборки — «соберите»), собирает пак в фоне (F7) и
следит за исходниками. Снимки без фокуса — `--shot ФАЙЛ`, `--gallery КАТАЛОГ`.

## Агентский доступ

Пекарня — процесс командной строки: агент видит проект через `status --json`, выпечку — `build --json` (что собрано и
почему, ошибки с логом), пак — `pak --verify`, сверку — `diff --json` (`summary`, `unexpected[]`); рецепт —
[docs/agent-playbook.md](../docs/agent-playbook.md), «Пекарня». Инструмент MCP не нужен.

## Ограничения

- Корни — пути машины: на другой машине — свой `bake.user.json`, и `paramsHash` в `diff` с эталоном будет отличаться.
- Манифесты правятся в Studio, руками или `sword-bake add`/`remove`; Studio пишет их тем же `ManifestWriter`, что и CLI.
