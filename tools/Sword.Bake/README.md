# Пекарня: ядро `Sword.Bake` и консоль `sword-bake`

Пекарня превращает исходники ассетов в пак `.swpak` (раздел 11 плана). Ядро (`tools/Sword.Bake`) держит проект
(`bake.project.json`), манифесты ассетов (`*.bake.json`), обработчики из плагинов, проверку актуальности и сборку пака со
сведениями о сборке (`.bake/build.json`). Об ассетах игры ядро не знает: встроенный обработчик — только `file.copy`,
обработчики движка и игры приходят плагинами (`Sword.Bake.Engine`, `Sword.Bake.Game` — точки Б4, Б5). Консоль —
`tools/Sword.Bake.Cli` (exe `sword-bake`); Studio — позже (Б7).

## Откуда

SWORD 2eab13b, перенесено как есть (решение раздела 11): логика та же, пространства имён ядра прежние (`Sword.Bake`,
`Sword.Bake.Processors`, `Sword.Bake.Cli`). Журнал переноса — [docs/history/baker.md](../../docs/history/baker.md).

| SWORD | SWORD2 | что изменилось |
|---|---|---|
| `tools/Sword.Bake/Core`, `Processors/FileCopyProcessor.cs` | `tools/Sword.Bake` | `using Sword.Content` → `Sword.Engine.Content`; `SwordSide=Neutral`; `InternalsVisibleTo Sword.Anim.Tests` снят (проекта нет); пустой каталог `Font/` не перенесён |
| `tools/Sword.Bake.Cli/Program.cs` | `tools/Sword.Bake.Cli` | то же; новое — `--json` у `status` и `build` (`CliJson.cs`); ссылка на `Engine.Content` — явная (`pak --extract` читает `PakSource`) |
| `src/Sword.Content/Pak/PakBuildInfo.cs` | `engine/Sword.Engine.Content/Pak/PakBuildInfo.cs` | только пространство имён |
| `tests/Sword.Bake.Tests` (Build, Layout, Provenance) | `tests/Sword.Bake.Tests` | только `using` |
| `tests/Sword.Anim.Tests/ManifestWriterTests.cs` | `tests/Sword.Bake.Tests` | третий случай — на своём тестовом обработчике вместо плагинного `anim.pack` |

`TreatWarningsAsErrors` и `Nullable` SWORD2 правок кода не потребовали.

## Устройство ядра

- **Проект** (`BakeProject`) — каталог с `bake.project.json`: `name`, `assets` (каталог манифестов), `pak` (путь пака от
  файла проекта), `roots` (корни исходников `${имя}`), `plugins`, `strictLayout`. Ищется от текущего каталога вверх, в том
  числе в подкаталоге `Bake/`. Корни задаются ещё локально — `bake.user.json` (вне git) и переменные `BAKE_ROOT_ИМЯ`.
- **Манифест** (`AssetManifest`) `<id>.bake.json`: `processor`, `params` (разбор без незнакомых полей — опечатка в имени
  поля — ошибка), `import` (происхождение из `bake.imports.json`), `notes`, `dependsOn`. `ManifestWriter` меняет только
  правленые поля `params` и сохраняет окончания строк файла.
- **Обработчик** (`IAssetProcessor`, база `AssetProcessor<TParams>`): `Id`, `Version`, `ParamsType`, `Parallel`,
  `Incremental`, `CodeAssemblies`, `Process`, `DeclareOutputs`. Ссылка на другую единицу — поле с `[AssetRef]`, чтение —
  `BakeContext.Ref<T>` (тип `T` сверяется с `ParamsType` обработчика цели).
- **Контекст** (`BakeContext`): всё прочитанное — через `InputFile`/`InputDir`/`ContentFile`, всё записанное — через
  `Output`/`OutputDir` во временный каталог сборки; по этим спискам — актуальность и выходы. Необъявленный выход — ошибка.
- **Актуальность** (`Builder.Check`, `CheckPak`) — по `.bake/build.json` текущего пака: отпечаток кода обработчика (ID,
  версия, MVID сборок `CodeAssemblies`), хеш параметров (с корнями и параметрами целей ссылок), входы (размер и время,
  иначе хеш), каталоги-входы (набор файлов), выходы (хеш в оглавлении), зависимости.
- **Сборка** (`Builder.Build`): топологический порядок, по `ядра/2` заданий; актуальные ассеты берутся из прежнего пака;
  пак пишется во временный файл и подменяет прежний только после успешной записи. **Любая ошибка — прежний пак
  нетронут**; ничего не пересобрано — пак не переписывается (игра, держащая его открытым, не получает замену).
- **Каталог контента** (`ContentCatalog`): ассет контента — файлы с общей основой имени или объявленный каталог-набор;
  раскладка (выход в папке своего манифеста, файл — у одной единицы) при `strictLayout` — ошибка ассета.
- **Целостность** (`Builder.Verify`): хеши всех файлов с оглавлением, оглавление против сведений о сборке, ничьих файлов нет.

## Плагины и их порядок

- `bake.project.json → plugins` — пути сборок от файла проекта; `${config}` — конфигурация хоста (`Debug`/`Release` exe,
  `AssemblyConfiguration`). Плагины грузятся **по порядку списка** в контекст по умолчанию процесса из теневой копии своего
  каталога (`%TEMP%/sword-bake/plugins/<имя>-<хеш>`: исходный каталог свободен, плагин можно пересобрать); обработчики и
  команды (`IBakeCommand`) находятся рефлексией. Сборку в процессе не выгрузить: новый список или пересобранный плагин —
  при следующем открытии проекта.
- Проект плагина: `EnableDynamicLoading`, выход `bin/<конфигурация>/` без TFM (`AppendTargetFrameworkToOutputPath=false`),
  ссылка на ядро — `Private=false` и `ExcludeAssets=runtime`: типы ядра даёт хост, своей копии у плагина нет.
- **Плагин на другом плагине** (`Sword.Bake.Game` на `Sword.Bake.Engine`) ссылается на него так же, как на ядро, и стоит в
  `plugins` **после** него. Основа к этому времени уже загружена, и ссылка надстройки по имени берёт её из контекста по
  умолчанию: второй копии нет, `ctx.Ref<T>` видит один тип `T`, отпечаток обработчика надстройки включает MVID основы
  (правка основы пересобирает ассеты надстройки). Ядро SWORD так уже работало — загрузчик не менялся; проверка —
  `PluginTests` на тестовых плагинах `tests/Sword.Bake.Tests.PluginA` (основа) и `PluginB` (надстройка: обработчик выведен
  из типа A и читает параметры единицы A).
- Надстройка раньше основы: её тип, выведенный из типа основы, не загружается — в проблемах проекта «плагин …B.dll: Could
  not load file or assembly '…A…'», обработчика надстройки нет. Если надстройка зовёт основу только из тел методов, она
  загрузится и в обратном порядке — на это не полагаться.

## Консоль `sword-bake`

`sword-bake [--project ПУТЬ] [--json] КОМАНДА …` — проект ищется от `--project` (или текущего каталога) вверх.

| команда | что делает | код выхода |
|---|---|---|
| `init [КАТАЛОГ] [--name ИМЯ]` | создать проект | 0 |
| `build [шаблоны…] [--force] [-j N]` | собрать устаревшие ассеты с зависимостями и записать пак | 0 — пак записан, 1 — нет |
| `status [шаблоны…]` | актуальность ассетов и пака | 0 — пак актуален, 2 — нет |
| `add ID ОБРАБОТЧИК [поле=путь…] [--notes ТЕКСТ]` | манифест нового ассета (исходники не копируются) | 0 |
| `remove ID` | удалить манифест (копия — `.bake/removed/`) | 0 |
| `root ИМЯ ПУТЬ` | локальный корень исходников (`bake.user.json`) | 0 |
| `list [--content]` | проект, корни, плагины, обработчики, ассеты; `--content` — каталог контента | 0 |
| `which ПУТЬ…` | ассет контента по пути в паке: файлы, кто пишет, актуален ли | 0, 1 — не найден |
| `licenses` | импорты по лицензиям | 0 |
| `pak [--verify] [--extract КАТАЛОГ]` | содержимое пака по ассетам; целостность; распаковка | 0, 2 — целостность нарушена |
| `КОМАНДА …` | команды плагинов проекта | как у команды |

Шаблон — ID (`anim/1hm_sword`), папка (`anim`), маска (`anim/*_locomotion`), `import:ИМЯ`, `processor:ID` или путь ассета
контента. Ошибка разбора, нет проекта — код 1 и строка `sword-bake: …` в stderr.

**`--json`** (только у `status` и `build`; у остальных — ошибка): итог — один JSON-объект одной строкой в stdout, больше в
stdout ничего; коды выхода те же. Общие поля — `command`, `ok`, `exitCode`, `project` (`name`, `file`, `assetsDir`, `pak`,
`strictLayout`, `plugins[]` с `loaded`, `processors[]`, `problems[]` — плагины, корни, импорты, `broken[]` — неразобранные
манифесты), `pak` (`path`, `status`: `upToDate`/`stale`/`missing`/`error`, `reason`, `details[]`, `exists`, `bytes`, `builtUtc`).
- `status`: `assets[]` — `id`, `processor`, `status` (`upToDate`, `dirty`, `noSource`, `error`), `reason`; `counts` по статусам;
  `provenance[]` и `layout[]` — замечания (`asset`, `text`).
- `build`: `options`; `assets[]` в порядке очереди — `result` (`built`, `upToDate`, `failed`, `skipped`, `cancelled`), `reason`
  (почему собирался), `error`, `detail`, `seconds`, `warnings[]`, у упавших — `log[]`, у остальных — `logLines`; `summary`;
  `pakWritten`, `pakUnchanged`, `pakFiles`, `pakBytes`; `pak` — актуальность после сборки; `messages[]` — события без ассета
  (`kind`, `text`: запись пака, почему не записан).
- Ошибка до итога — `{"command":…,"ok":false,"exitCode":1,"error":"…"}`.

## `.bake/` и где пак

- В каталоге проекта: `.bake/cache.json` — хеши исходников и ошибки последних сборок (только эта машина; потерян —
  пересчитывается), `.bake/removed/` — копии удалённых манифестов. `.bake/` и `bake.user.json` — вне git (`.gitignore`).
- В паке: `.bake/build.json` — `PakBuildInfo` (`engine/Sword.Engine.Content/Pak`), последним файлом: из чего собран именно
  этот пак. Читают пекарня и (позже) редактор F6 (`SingleSourceOf`).
- **Пак пекарни SWORD2 — `artifacts/bake/Content.swpak`, не корень репозитория.** `ContentLocator` предпочитает
  `Content.swpak` эталонному: пак в корне молча подменил бы контент игре, тестам Content, сценариям фич и эталонным
  реплеям. Проект `Bake/` (Б6) задаёт `"pak": "../artifacts/bake/Content.swpak"`; переключение игры на новый пак —
  отдельным решением. Временные проекты — во временном каталоге.

## Зависимости

`Sword.Bake` → `Engine.Content` (пак, пути контента, `PakBuildInfo`); `Sword.Bake.Cli` → `Sword.Bake`, `Engine.Content`.
Оба — сторона `Neutral` (Content нейтрален). NuGet — нет.

## Ограничения

- Обработчиков движка и игры пока нет (Б4, Б5): ядро и консоль работают с `file.copy` и плагинами проекта.
- Сравнения паков (`bake diff`) нет — Б3.
- У `file.copy` путь в паке по умолчанию — ID ассета без расширения (как в SWORD); другой — полем `to`.
- В выходе плагина лежат копии сборок движка, до которых он достаёт транзитивно (Content, Core, Diagnostics): ядро их
  не берёт — те же сборки хоста загружены раньше.

## Проверки

- `dotnet test tests/Sword.Bake.Tests` — 28 тестов: сборка, актуальность, отказ без порчи пака, раскладка, происхождение и
  ссылки (перенесены как есть — спецификация инструмента), запись манифестов, два плагина по порядку (`PluginTests`, в
  том числе процессом `sword-bake`), `--json` (`CliJsonTests`, в том числе процессом).
- `tests/Sword.Engine.Content.Tests`: `PakBuildInfoTests` — запись и чтение; `ReferenceBuildInfoTests` (категория Content) —
  сведения эталонного пака: 29 ассетов, выходы — ровно файлы оглавления, запись — те же байты.
- `tests/Sword.Engine.Formats.Tests`: `ReferencePakWritersTests` (категория Content) — писатели SWORD2 = байты эталона:
  1048 `.swclip`, 1 `.swskeleton`, 10 `.swmesh`, 5 `.swfont` — чтение и запись дают те же байты. Расхождение пересобранного
  пака с эталоном (Б6) тогда — в логике выпечки, не в записи формата.
- Архитектура: строки `Sword.Bake*` в `ArchitectureRules.Matrix` — как таблица раздела 4.

## Агентский доступ

Итог анализа (раздел 8 плана): пекарня — процесс командной строки, не живой мир, поэтому агентский вид — ключ `--json`,
как у `Sword.Headless --json`; инструмент MCP не нужен. Агент видит и проверяет:
- актуальность — `sword-bake status --json`: что устарело и почему, проблемы проекта и плагинов (загружен ли каждый),
  неразобранные манифесты, происхождение, раскладка, состояние пака;
- сборку — `sword-bake build --json`: что собрано и почему, ошибки ассетов с логом, записан ли пак, актуален ли после;
- пак — `sword-bake pak --verify` (целостность, код 2), `which ПУТЬ` (кто пишет файл), `list --content`;
- писатели форматов — `ReferencePakWritersTests`; сведения о сборке — `PakBuildInfo.Read` на любом паке.

Рецепт — [docs/agent-playbook.md](../../docs/agent-playbook.md), «Пекарня». Не хватает — сравнения двух паков (Б3, `bake diff`
с `--json`).

## Известные ошибки

Нет.
