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

## Б3. `bake diff` (2026-09-25)

**Что сделано.** Новая команда `sword-bake diff A B [--expect ФАЙЛ] [--json]` — в SWORD её нет; прототип — `inspect compare`
плагина `Sword.Bake.Sword` (`Inspect.cs:102-208`: распаковка во временный каталог, `.swclip` по полям без `SourceHash`,
остальное побайтно, коды 0/2). Ядро: `Core/PakDiff.cs` (сравнение), `Core/DiffExpectations.cs` (файл ожиданий),
`Core/ContentComparer.cs` (`IContentComparer`, `ContentComparison`); `ProcessorCatalog` находит сравнители в плагинах;
CLI — `diff`, `CliJson.Diff`, `list` показывает сравнители. Тестовый сравнитель `.skel` — `tests/Sword.Bake.Tests.PluginA`
(`SkeletonComparer`), тесты — `DiffTests` (15).

**Решения и почему.**
- **По оглавлению, без распаковки.** Путь, размер и хеш уже в оглавлении пака: одинаковые файлы не читаются вовсе, читаются
  только файлы с разными хешами — и только если у расширения есть сравнитель. Эталон с самим собой — ~0,1 с (1082 файла
  `same`), против распаковки 422 МБ у `inspect compare`.
- **Сравнитель формата — в плагине, выбор по расширению.** Ядро форматов игры не знает (раздел 11): `.swclip` читает
  `Formats`, до которого ядру нет ссылки. Сравнитель находится рефлексией, как обработчики и команды, — плагину не нужно
  ничего регистрировать. Сигнатура — байты A и B целиком (`byte[]`): так проще всего и форматам с потоком
  (`ClipFile.Read(new MemoryStream(a), path)`), и побайтовым; самый большой файл пака — текстура ~20 МБ, память не
  ограничение. Вызовы по одному: сравнителю не нужна потокобезопасность, 1048 клипов — секунды.
- **Сбой сравнителя — расхождение, не отказ.** Сравнитель — код плагина: исключение становится расхождением файла с
  текстом ошибки, остальные файлы сравниваются дальше.
- **Плагины — только через проект.** Сравнители — из плагинов проекта (`--project` или проект от текущего каталога); вне
  проекта `diff` работает по оглавлению и байтам (`project: null` в JSON), чтобы сравнить два пака можно было и без проекта.
- **`.bake/build.json` — отдельный раздел, не файл.** Когда он есть в обоих паках, его хеш различается всегда (время), поэтому
  он сравнивается только семантически (разбор пекарни, таблица 3.3) и в списки файлов не входит — иначе одно и то же
  расхождение считалось бы дважды (файлом и полями). Каждое расхождение сведений (ассет, поле, путь) делится на ожидаемое
  и неожиданное само. `fingerprint` — префикс `ID|версия`: сборки в отпечатке после деления `Bake.Sword` другие по
  устройству (проверено на сведениях эталона: замена MVID, `Sword.Bake.Sword` → `Sword.Bake.Engine`, времени и `mtime` всех
  3399 входов — «равны семантически»). `paramsHash` сравнивается с пометкой о корнях: на той же машине с теми же корнями он
  совпадает, иначе — различие с объяснением, а не молчание. Выход с другим хешем — по итогу сравнения файла: равны по
  содержимому (клип с другим `SourceHash`) — не расхождение сведений.
- **Ожидания — файл, а не ключи.** Список «ожидаемо отсутствует/отличается» с обязательной причиной живёт рядом с
  проектом и переживает прогоны; правило — путь файла или ID ассета (маски — как шаблоны проекта); незнакомое поле —
  ошибка (как параметры манифеста). Правила, не подошедшие ни к чему, называются: список не копит устаревших исключений.
- **Коды выхода** — как у `status` и `inspect compare`: 0 — неожиданных расхождений нет, 2 — есть, 1 — ошибка.
- **Режима допуска для float нет.** По разбору 3.4 на той же машине и рантайме выпечка детерминирована; допуск понадобится
  только при смене рантайма — тогда он дело сравнителя формата, не ядра.

**Интерфейс для Б5.** Сравнитель `.swclip` — класс в `Bake.Engine`, реализующий `IContentComparer`
(`Extensions = [".swclip"]`, `Compare(path, a, b)`: оба клипа через `ClipFile.Read`, все поля, кроме `SourceHash`,
`Same("без SourceHash")` или `Differs("<поле>: …")`). Контракт — README пекарни, «Сравнитель формата — контракт для плагинов».

**Проверки.** `tests/Sword.Bake.Tests` — 43 (было 28; `DiffTests` 15: одинаковые паки; только в одном и другой размер;
другой хеш — без сравнителя «байтами», сравнитель плагина A: равны по содержимому, отличаются, сбой; два сравнителя на
расширение; сведения о сборке — время, MVID и время входов не видны, параметры, версия обработчика, входы, каталоги, входы
из контента, ассет только в одном — видны; выходы по итогу сравнителя; файл ожиданий — путь, папка, маска, ассет, выход по
правилу пути, непригодившееся правило; 6 ошибок разбора; `--json`; процесс — коды 0/2/1, проект по `--project` и от
текущего каталога); `CliJsonTests` — текст ошибки `--json` у других команд. Руками: `diff Content.reference.swpak
Content.reference.swpak` — 1082 `same`, сведения равны (29 ассетов), код 0; паки из одного `.bake/build.json` эталона —
исходный против изменённого (время, MVID, имя сборки, `mtime`) — равны, код 0; с правкой `paramsHash` `anim/gasp`, хеша
выхода `weapons/katana` и без ассета `effects/blood/spots` — три расхождения, код 2; с ожиданиями на два ассета — одно
неожиданное.

**Агентские инструменты (итог анализа).** Сравнение паков — процесс командной строки: агентский вид — `diff --json` (сводка,
файлы по видам, сведения о сборке по полям, ожидаемые и неожиданные расхождения, непригодившиеся ожидания, сравнители и
плагины проекта); инструмент MCP не нужен. Сравнитель формата от плагина агент видит в `comparers[]` и у каждого файла
(`comparer`, `detail`), в `list`; проверяет — тестом на синтетических паках (`DiffTests`, тестовый плагин). Строка рецепта —
`docs/agent-playbook.md`, «Пекарня», п. 6.

## Б4. `Bake.Engine`: анимация (2026-09-25)

**Что перенесено.** Новый плагин `tools/Sword.Bake.Engine` (сторона `Neutral`, `EnableDynamicLoading`, выход
`bin/<конф>/` без TFM; ссылки — ядро `Private=false`/`ExcludeAssets=runtime`, Content, Formats, Animation) — анимационная
часть `tools/Sword.Bake.Sword` SWORD 2eab13b: `Anim/RawAnim.cs`, `UeSpace.cs`, `UeRig.cs`, `ClipSource.cs`,
`FingerTransfer.cs`, `GltfMesh.cs`, `SkinBake.cs`, `AnimImport.cs`, `PoseReport.cs`, `Processors/SkeletonProcessor.cs`
(+ `SkinMeshProcessor`), `Processors/AnimPackProcessor.cs`, `Inspect.cs`. Тесты — `tests/Sword.Bake.Engine.Tests`.
Не перенесены (Б5, Б7): текстуры, шрифты, предпросмотры, `MeshDecimate`, `SpineChain`, оружие, кровь, анатомия.

**Что поменялось против SWORD и почему.**
- Пространства имён — `Sword.Bake.Engine.Anim`, `Sword.Bake.Engine.Processors`, `Sword.Bake.Engine` (в SWORD —
  `Sword.Bake.Anim`, `Sword.Bake.Processors`: плагин игры жил бы в тех же пространствах, что и движковый).
- Контейнер клипов импорта — `ClipLibrary` движка (вместо `SkeletalClips`): `SkinBake.BuildClips` собирает те же таблицы
  списками и отдаёт их конструктору `ClipLibrary` (копия — один раз, из `CollectionsMarshal.AsSpan`); `Sample`, `MotionMatrix`
  — те же операции; прямая кинематика проверки — `Skeleton.ToModel` (скелет экспорта без ID). Своего контейнера не нужно.
- Форматы — писатели `Engine.Formats` (`ClipFile`, `SkeletonFile`, `SkinnedMeshData`, байт в байт эталон — Б1) через
  `ContentFiles.Write`: файл `*.tmp` и подмена, как `ClipAsset.Save` SWORD. Прежний клип при инкрементальной сборке читается
  целиком (`ClipFile.Read`): чтения одного заголовка в Formats нет; заодно клип с испорченным телом перепекается, а не
  принимается за актуальный.
- `SkinBake.Run` → `SkinBake.Mesh`: только меш. Ветки `.swskel` (`outClips` — формат снят) и статического меша с креплением
  (`--static`, `--attach`) звала лишь старая команда `skin`; обработчик `skin.mesh` передавал в них `null`. С ними ушли
  `GripAlongBlade` и сообщение про `--attach`; оружие (Б5) крепит `WeaponBake` через `SkinBake.GripAttachment`.
- `PoseReport` — только `Raw`: `SkelCheck`, `Fingers` читали `.swskel`. `Inspect`: `meshinfo`, `rawpose` пишут в поток команды
  (в SWORD — в `Console`); **`compare` не перенесён** — сравнение паков — `bake diff` ядра (Б3), клипы по содержимому без
  `SourceHash` — `Anim/ClipContent.cs` (заголовок и массивы побайтно, у отличия — поле, число значений, наибольшая разница,
  кадр), на нём сравнитель `.swclip` для `bake diff` (Б5); режим `--by-name` (сверка после переименования наборов) не нужен.
- `CodeAssemblies` обработчиков: `anim.skeleton`, `skin.mesh` — плагин и Formats; `anim.pack` — плагин, Animation (система
  корневого движения), Formats (запись клипа) — в SWORD Sword.Anim и Sword.Content.
- **`SourceHash` без MVID** (решение 2026-09-25): из хеша убран отпечаток сборок конвертера (MVID `Sword.Bake.Sword` и
  `Sword.Anim`), `ToolVersion` «sword-animbake import 3» → «4». Остальное в хеше — как в SWORD.
- Открытый API для `Bake.Game` вместо `InternalsVisibleTo`: явная граница основы и надстройки (надстройка не достаёт до
  `AnimImport` и внутренностей переноса пальцев). Открыты `RawManifest`, `RawClipInfo`, `RawClip`, `RawAnimIO`, `UeSpace`,
  `UeRig`, `ClipSource`, `FingerSource`, `FingerStats`, `GltfMesh`, `SkinBake.GripAttachment`, `ContentFiles`, `SkeletonParams`,
  `SkinMeshParams`, `SkeletonProcessor`, `SkinMeshProcessor`; `MeshDecimate` и `SpineChain` откроются с анатомией (Б5).
- `TreatWarningsAsErrors` и `Nullable` SWORD2 правок сверх перечисленного не потребовали.
- Архитектурный тест `CompiledAssemblies_FollowArchitecture` искал сборку только в `bin/<конф>/<TFM>/` — плагин без TFM
  (`bin/<конф>/`) теперь тоже находится (`SolutionModel.BuiltAssembly`). `verify.ps1 -SkipReference` исключает и категорию
  Source (ей нужен эталонный пак).

**Тесты.** `tests/Sword.Bake.Engine.Tests` — 33 синтетических и 3 категории Source. Перенесены из `tests/Sword.Anim.Tests`
SWORD (синтетика — спецификация пекарни): `GltfTests` (2), `RouteTests` (4), `RootMotionTests` (вынос поворота из root, дрейф
цикла, смещение начала ×2; «формат с yaw» — теперь импорт пишет клип с поворотом корня), `MathTests` (оси UE, `rootForward`
×3, окно пальцев), `LibraryTests.ImportId` (7), `SynthSkeleton` (помощник: синтетический raw-экспорт полного скелета). Свои:
`SourceHash` меняется от исходника, параметров клипа и пространства и **не зависит от сборки** (копия сборки плагина с
подменённым MVID в своём контексте загрузки считает тот же хеш); инкрементальный импорт (тот же экспорт — «записано 0»,
изменённый клип — перепечён один, испорченный прежний — перепечён, ушедший в другой набор и пропавший — удалены); меш на
синтетическом риге (оси `ue=(x,z,y)` с отклонением 0, вершины и bind-поза в пространстве персонажа, поза привязки движка);
плагин в настоящем хосте (`sword-bake` процессом: скелет, меш, клипы читаются `Skeleton`/`ClipLibrary`, FK · корень =
экспорт, повторная сборка — всё актуально и пак не переписан, `--force` — «записано 0», изменённый клип — один; `inspect`).

**Сверка с эталоном на исходниках** (`SourceBakeTests`, категория Source; манифесты анимации `Bake/` SWORD —
`skeletons/ue5_manny`, `characters/manny/mesh`, `anim/1hm_sword`, `anim/combat_master`, `anim/combat_master_ext`, `anim/gasp`;
`sword-bake build` обычным прогоном, `-j` = ядра/2 = 4, Debug):
- клипы: в эталоне 1048, выпечено 1048, **совпали по содержимому все 1048** (заголовок, позы, корневое движение и его поворот —
  побайтно; `SourceHash` другой у всех 1048 — ожидаемо), размеры те же; по наборам: katana 182, locomotion 444, sword_1h 188,
  sword_shield 184, twin_swords 25, unarmed 25 — всё из всего. Циклы с поворотом кости root (`katana/Anim_DK2_Run_F_IP`,
  `sword_shield/Anim_SAS_Walk_BL`, `sword_shield/Anim_DSS3_Run_BL_RM`) и четыре `Circle_Strafe` локомоции — в их числе: эталон собран логикой
  a5c9e55 (разбор, п. 3.1), перенос её воспроизводит;
- `skeletons/ue5_manny.swskeleton` (2945 Б, 161 сустав) и `characters/manny/mesh.swmesh` (3 460 779 Б, 48 779 вершин) — байт в байт;
- время: выпечка 6,4 с (sword-bake 6,3 с: gasp 5,7, combat_master_ext 2,6, 1hm_sword 0,8, combat_master 0,8, меш 0,1), сверка
  клипов 0,4 с. Лог: проверка FK — 0,000 см у всех четырёх экспортов; пальцы — отклонение фаланг 47–49° → 0,0°, сгиб пясти
  107–137° → 18°; оси меша `ue=(x,z,y)`, 3,1 см по 61 суставу.

**Смена конфигурации (руками, один прогон).** Тот же временный проект: выпечка Debug (5,7 с, записано 1048), затем Release
`status` — все шесть ассетов «изменился код обработчика», `build` Release — 2,0 с, у всех `anim.pack` «записано 0, без
изменений …» (1048 клипов не перепечены); пак переписан только ради `.bake/build.json` (другие отпечатки кода).

**Агентские инструменты (итог анализа).** Изменение выпечки анимации агент видит и проверяет без новых инструментов MCP:
тест Source с итогом в `artifacts/bake/source-check.json` (числа по наборам, у отличий — поле и величина), лог `sword-bake
build` (сводка корня, пальцев, FK, «записано / без изменений / удалено»), `inspect rawpose`/`meshinfo` для исходников,
загрузчики движка для клипов пака. Добавлено в этой точке: тест Source с файлом итога, `ClipContent` (сравнение клипов без
`SourceHash`), вывод `inspect` в поток команды. Не хватает сравнения двух паков по форматам — `bake diff` (Б3) и сравнитель
`.swclip` (Б5).

## Б5. Текстуры, шрифты, `Bake.Game`, сравнитель `.swclip` (2026-09-25)

**Сделано** (SWORD 2eab13b `tools/Sword.Bake.Sword`, логика как есть; предпросмотры — Б7). `Bake.Engine`: `Texture/`, `texture.array`,
`font.vector` + `Font/UiFontBaker`, открытые `MeshDecimate`, `SpineChain`, `Anim/ClipComparer` (`.swclip` без `SourceHash`).
`.swtex` → Formats, `.swblood` → Game.Data. Плагин `tools/Sword.Bake.Game`: `weapon.static`, `anatomy.mesh`, `MeshEdges.Open`.
**Решения.** Роли шрифта — `UiVectorFontFormat.RoleNames`/`TryParseRole` (Formats; `UiFaces` зовёт их, тест: имена `UiFace` = список).
Файлы пишут писатели (`UiFontFile`, `ContentFiles.Write`): раскладка одна. Чтение `.swtex`/`.swblood` — точным числом байт, без
`Load`/`Save`. `CodeAssemblies` — и `Bake.Engine`, Formats, Game.Data, StbTrueTypeSharp: код выхода теперь в разных сборках.
**Проверка Б4 → ядро.** Source Б4 падал (3 из 36, «Failed to parse deps.json»): параллельные `sword-bake` переписывали теневую копию
плагина, которую другой читал — `PluginLoader.Shadow` копирует в каталог процесса и переносит `Directory.Move`. Плагин игры раньше
движкового работал случайно (основа нужна только коду обработчиков) — теперь ошибка проекта «ссылается на плагин X, а стоит раньше».
**Числа (Source, Debug, `-j` 4).** Движок: клипы 1048/1048 по содержимому, скелет, меш, **текстуры 8/8, шрифты 5/5, лицензии 3/3 —
байт в байт**; выпечка 32,3 с (альбедо и нормали анатомии 29,7 и 28,1 с, кровь 0,7–5,6, шрифты 0,1–0,2). Игра: **оружие 6, `.swblood`
6, анатомия 3, `combat/actions.json` — байт в байт**, 1,2 с. Писатели на эталоне: `.swtex` 8 и `.swblood` 6 — те же байты.
**Тесты.** Перенесены `TextureTests`, `SpineChainTests`, `WeaponBloodTests` (новый `tests/Sword.Bake.Game.Tests`); свои — сравнитель
(без `SourceHash` равны, поле — «отличаются», паки через `PakDiff`), оба плагина в `sword-bake` (анатомия на синтетическом риге через
`ctx.Ref<SkeletonParams>`; обратный порядок), `.swtex`/`.swblood` (круг, каждая обрезка).
**Отклонения.** Сверка Source игры — свой тест в `Bake.Game.Tests` (ключи `game*`), запись итога — под именованным мьютексом.
`verify.ps1`: Source — отдельным прогоном после остальных тестов (32 с на всех ядрах ломали 3 из 3 раз `FirstAnomaly_NoJit…`).
**Агентские инструменты.** Хватает: Source и `source-check.json` по всем форматам, `bake diff` со сравнителем `.swclip`, логи.
**На приёмке (Б6).** README `Bake.Engine` и новый `Bake.Game`; AGENTS ядра (ошибка порядка), Formats, Game.Data; playbook; проект
`Bake/` с `plugins` [Engine, Game], полная выпечка и `bake diff` с эталоном; Release-замер Source.

## Б6. Проект `Bake/`, полная выпечка, сверка с эталоном (2026-09-25)

**Проверка Б5.** `tests/Sword.Bake.Engine.Tests` 52/52 и `tests/Sword.Bake.Game.Tests` 10/10 с категорией Source, `tests/Sword.Bake.Tests`
43/43 — зелёные; правок не понадобилось. Разделение прогонов `verify.ps1` (Source — отдельно, после остальных) оставлено: выпечка на
всех ядрах роняла `FirstAnomaly_NoJitOnMainThread_FreshAssembly`; тест писателя `.swblood` остаётся в тестах Formats.
**Сделано.** `Bake/` из SWORD 2eab13b: 29 манифестов (в SWORD один — `effects/blood/directional` — был в LF, теперь CRLF),
`bake.imports.json` как есть (пути `tools/ue/…` относительны — это скрипты SWORD2), `bake.project.json` — плагины `Bake.Engine`, затем
`Bake.Game`, пак `../artifacts/bake/Content.swpak`, `strictLayout`. В `combat/actions` снята заметка о просмотрщике F6 (в SWORD2 его
нет; `notes` в хеш параметров не входит). `bake.user.json` и `.bake/` — вне git. Новые `Bake/README.md`, `reference.expect.json`,
`tools/Sword.Bake.Game/README.md`; README и AGENTS ядра, `Bake.Engine`, Formats, Game.Data, playbook «Пекарня», `tools/ue/README.md`.
**Числа.** Полная выпечка с нуля (`-j` 4): Debug 30,6 с (текстуры анатомии 27,3 и 25,1 с), Release 9,1 с; 29 ассетов, 1082 файла,
пак 442 369 090 Б (эталон — 442 368 354 Б; разница — `build.json`); `pak --verify` — в порядке. `diff` с эталоном: 34 равны байтами,
1048 клипов — по содержимому (другой только `SourceHash`), `build.json` — семантически (29 ассетов; `paramsHash` совпал — корни те
же), неожиданных 0, ожидаемых 0 — правил в `reference.expect.json` нет (0,7 с). Пак Debug против Release — 1082 файла байт в байт
(`SourceHash` без MVID). В `artifacts/bake/` оставлен пак Debug (`status` Debug — всё актуально).
**Находка для решения о переключении.** `PakSource.ContentHash` — хеш оглавления вместе с `.bake/build.json`, а в нём время:
каждая запись пака меняет хеш контента, даже при тех же файлах (Debug и Release — `DC0F…`, `1C7C…`; эталон — `04A5…`). Реплеи
сверяются по этому хешу: после переключения игры любая пересборка пака делает эталонные реплеи «записанными на другом контенте».
**Агентские инструменты.** Хватает: `status`/`build`/`diff --json`, `pak --verify`, тесты Source; новых не нужно.
**Отклонения.** Тесты Source по-прежнему держат свои копии манифестов (параметры те же), а не читают `Bake/`.
**Осталось.** Б7 — Studio и предпросмотры; переключение игры на пак пекарни — решение пользователя.
**Проверка.** `status` после коммита: `combat/actions` устарел — номер коммита в версии сборки меняет MVID (README `Bake/`, «Как печь»).

## Б7а. Studio на движке SWORD2: доработки движка и приложение (2026-09-25)

**Сделано.** Render: `GpuImage` — постоянная картинка из пикселей RGBA8 с мипами (ящичный фильтр 2×2 на CPU), SRV `Texture2DArray`
для `UiDrawList.Image` (`Renderer.CreateImage`); `RendererOptions.UiFonts` — шрифты UI-прохода от вызывающего (без них — пак, как
было; игра не менялась). Platform.Win32: `NativeDialogs` (папка, файлы, вопрос, ошибка) — перенос SWORD. `tools/Sword.Bake.Studio`:
`StudioApp`, виды, `ParamsEditor`, витрина — из SWORD 2eab13b почти без правок (API ядра и виджетов совпал); `Program.cs` — заново на
`Win32Window` + `Renderer` (проход очистки и `Renderer.Ui`), Vortice напрямую нет; `StudioFonts` — Segoe UI/Consolas в `UiFonts`.
`PreviewPanel` — заглушка «нет предпросмотра». Строка матрицы — Bake, Formats, Diagnostics, Platform, Client, Render, Ui, Platform.Win32.
**Решения.** Пекарь шрифтов — исходником (`Compile Link`), не ссылкой на `Bake.Engine`: это плагин проекта, и его копия в выходе Studio
(TPA) не дала бы `PluginLoader` загрузить одноимённую сборку. Исправлена ошибка SWORD: слежение за корнем исходников, совпавшим с
корнем проекта, видело изменение самого каталога `.bake` (проверка статусов пишет кэш) и крутило проверку без конца — снимок ждал
таймаута 120 с; теперь каталог `.bake` пропускается.
**Числа.** `--gallery` на `Bake/`: 12 снимков за 3,4 с вместе с выпечкой шрифтов (кадр 6 у каждого). Тесты: Studio 9/9 (с Gpu),
Render — 9 новых (мипы; картинка на GPU под GPU-валидацией — цвет мипа из снимка; шрифты вызывающего).
**Агентские инструменты.** `--shot ФАЙЛ`, `--gallery КАТАЛОГ` (все виды `NN-вид.png` + витрина), без фокуса всегда; в консоль —
лог модели и, при снимке по таймауту, чем модель занята (`StudioApp.Activity`).
**Отклонения.** SWORD `--gallery` (витрина виджетов) стал `--widgets`; `--preview-mode` вернётся в Б7б вместе с панелью. Байты манифеста
после правки и отката не совпадают (писатель ядра разворачивает однострочные объекты) — тест проверяет значения, порядок и CRLF.
Замечено: пекарь шрифтов между прогонами дал разное число индексов полос у consola (446 634 / 446 602) — не разбиралось.
**Б7б и приёмка.** Предпросмотры `IAssetPreview` и 3D-панель (`GpuImage` для картинок), `--preview-mode`; README Studio, playbook,
AGENTS Render (UI-проход: шрифты от вызывающего, `CreateImage`); руками: правка формы, добавление и удаление, автосборка.
