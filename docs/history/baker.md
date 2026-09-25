# Пекарня: перенос из SWORD

Перенос пекарни SWORD 2eab13b в SWORD2 (раздел 11 плана, точки Б1–Б7 раздела 13). Разбор пекарни SWORD —
`artifacts/plans/baker/research-baker.md` (вне git). Текущее устройство — [tools/Sword.Bake/README.md](../../tools/Sword.Bake/README.md).

## Б1. Ядро и CLI (2026-09-25)

**Что перенесено.** Ядро `tools/Sword.Bake` (Core — 13 файлов, `Processors/FileCopyProcessor.cs`) и консоль
`tools/Sword.Bake.Cli` (exe `sword-bake`) — как есть, пространства имён прежние; `PakBuildInfo`, `PakAssetRecord`,
`PakFileRecord` — в `engine/Sword.Engine.Content/Pak` (AGENTS Content: «вместе с пекарней»); тесты `tests/Sword.Bake.Tests`
(`BuildTests` 11, `LayoutTests` 4, `ProvenanceTests` 5) и `ManifestWriterTests` (3) из `tests/Sword.Anim.Tests`.

**Что поменялось против SWORD и почему.**
- `using Sword.Content` → `Sword.Engine.Content` (ядро ссылается на `Engine.Content` вместо `Sword.Content`) — и всё: с
  `TreatWarningsAsErrors` и `Nullable` SWORD2 код собрался без правок.
- csproj: `SwordSide=Neutral` у ядра и CLI (Content нейтрален — хватает); `InternalsVisibleTo Sword.Anim.Tests` снят (проекта
  нет); у CLI ссылка на `Engine.Content` явная — `pak --extract` читает `PakSource` сам (матрица: `Bake.Cli` → Bake, Content).
- Пустой каталог `tools/Sword.Bake/Font/` не перенесён.
- `ManifestWriterTests.ClearingOptionalFieldRemovesIt` в SWORD шёл на плагинном `AnimPackProcessor`; в ядре его нет —
  тот же случай на своём тестовом обработчике той же формы параметров (необязательное поле, словарь списков, ссылка).
- Новое: `--json` у `status` и `build` (`CliJson.cs`) — агентский вид; остальные команды и текстовый вывод — как в SWORD.
- Строки `Sword.Bake*` матрицы `ArchitectureRules` приведены к таблице раздела 4: `Bake.Cli` +Content; `Bake.Engine` —
  Content, без Ui; `Bake.Game` +Formats; `Bake.Studio` +Bake.Engine, Content, Formats (строки для Б4, Б5, Б7 — по таблице плана).

**Писатели SWORD2 = байты эталона.** `ReferencePakWritersTests` (категория Content): каждый файл эталонного пака читается
форматом SWORD2 и записывается обратно. Все побайтно: 1048 `.swclip` (`ClipFile`), 1 `.swskeleton` (`SkeletonFile`), 10
`.swmesh` (`SkinnedMeshData`), 5 `.swfont` (`UiFontFile`) — расхождений нет, писатели не правились. Сведения о сборке
эталона (`.bake/build.json`, 1 МБ) тоже записываются обратно теми же байтами (`ReferenceBuildInfoTests`): 29 ассетов, их
выходы — ровно 1082 файла оглавления (плюс сам `build.json`) с теми же размерами и хешами. Непокрытые форматы пака —
`.swtex` (8 файлов) и `.swblood` (6) — в SWORD2 их ещё нет (Б5).

**Несколько плагинов по порядку.** Ядро SWORD уже грузит плагины по порядку списка в контекст по умолчанию; ссылка
надстройки на основу (`Private=false`, `ExcludeAssets=runtime`) по имени берёт уже загруженную основу — второй копии нет.
Загрузчик не менялся. Проверено на тестовых плагинах `tests/Sword.Bake.Tests.PluginA` (основа: обработчик скелета и
базовый класс обработчиков «на скелете») и `PluginB` (надстройка: обработчик выведен из класса A, читает параметры единицы
A через `ctx.Ref<SkeletonParams>`): сборка A в процессе одна, тип параметров A глазами B — тот же, отпечаток B включает MVID
A, сборка через оба плагина проходит (`PluginTests.SecondPluginUsesFirstWithoutSecondCopy`). Обратный порядок (B раньше A) —
отдельным процессом `sword-bake status --json`: в `project.problems` — «плагин …PluginB.dll: Could not load file or assembly
'Sword.Bake.Tests.PluginA…'», обработчика B нет (`PluginTests.CliLoadsPluginsInOrder_WrongOrderIsAProjectProblem`; в
процессе тестов A мог быть загружен другим тестом, поэтому процесс).

**Проверки.** `tests/Sword.Bake.Tests` — 28 (перенесённые 23, `PluginTests` 2, `CliJsonTests` 3); `tests/Sword.Engine.Content.Tests`
+4 (`PakBuildInfoTests` 3, `ReferenceBuildInfoTests` 1); `tests/Sword.Engine.Formats.Tests` +4 (`ReferencePakWritersTests`);
архитектурные — зелёные. Руками — временный проект во временном каталоге: `init`, `root`, `add` (`file.copy`), `build`,
`build --json` (пак не изменился — не переписан), `status --json`, `pak --verify`; правка исходника — `dirty` с причиной;
пропавший исходник — `build --json`: `failed`, `pakWritten: false`, прежний пак цел (`pak --verify` — в порядке).

**Агентские инструменты (итог анализа).** Пекарня — процесс командной строки: вид для агента — `--json` у `status` и
`build` (как `Sword.Headless --json`), инструмент MCP не нужен; строка рецепта — `docs/agent-playbook.md`, «Пекарня».
Не хватает сравнения паков — Б3 (`bake diff --json`).
