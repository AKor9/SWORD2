# Пекарня движка: плагин `Sword.Bake.Engine`

Плагин проекта пекарни (`bake.project.json → plugins`, первым): обработчики ассетов движка — скелет и риг
(`anim.skeleton`), меш персонажа (`skin.mesh`), клипы raw-экспорта Unreal → наборы библиотеки анимаций (`anim.pack`),
массивы текстур (`texture.array`), шрифты интерфейса (`font.vector`); команда `sword-bake inspect`; сравнитель `.swclip` для
`sword-bake diff`. Конвертеры — `Anim/`, `Texture/`, `Font/`, обработчики — `Processors/`. Оружие, кровь оружия и анатомия —
плагин игры [`Sword.Bake.Game`](../Sword.Bake.Game/README.md), он ссылается на этот плагин так же, как на ядро. Ядро
пекарни и консоль — [tools/Sword.Bake/README.md](../Sword.Bake/README.md), проект — [Bake/](../../Bake/README.md).

Главное назначение сейчас — **перепечь анимацию**, если с Motion Matching будут проблемы: выпечка настоящих исходников
этим плагином даёт те же клипы, скелет, меш, текстуры и шрифты, что эталонный пак (тест категории Source, «Проверки»;
полная выпечка `Bake/` — `diff` без расхождений).

## Откуда

SWORD 2eab13b, `tools/Sword.Bake.Sword` — движковая часть; логика выпечки не менялась. Журнал переноса —
[docs/history/baker.md](../../docs/history/baker.md), «Б4», «Б5».

| SWORD | SWORD2 | что изменилось |
|---|---|---|
| `Anim/RawAnim.cs`, `UeSpace.cs`, `UeRig.cs`, `ClipSource.cs`, `FingerTransfer.cs`, `GltfMesh.cs` | `Anim/` | пространство имён `Sword.Bake.Engine.Anim`; типы, которые зовёт `Bake.Game`, — открытые |
| `Anim/SkinBake.cs` | `Anim/SkinBake.cs` | клипы — в `ClipLibrary` движка вместо `SkeletalClips`, прямая кинематика — `Skeleton.ToModel`; `Run` → `Mesh` (только меш: ветки `.swskel` и статического меша с креплением — мёртвые, их звала старая команда `skin`) |
| `Anim/AnimImport.cs` | `Anim/AnimImport.cs` | `SourceHash` без MVID сборок, `ToolVersion` 3 → 4; запись — `ClipFile` Formats; прежний клип читается целиком (чтения только заголовка в Formats нет) |
| `Anim/PoseReport.cs` | `Anim/PoseReport.cs` | только `Raw`; `SkelCheck`, `Fingers` читали снятый формат `.swskel` — не перенесены |
| `Processors/SkeletonProcessor.cs` (+ `SkinMeshProcessor`), `AnimPackProcessor.cs` | `Processors/` | форматы и константы путей — `Engine.Formats`, `ClipLibrary`; без предпросмотров Studio (Б7) |
| `Inspect.cs` | `Inspect.cs` | живые `meshinfo`, `rawpose` (вывод — в поток команды); `compare` не перенесён — сравнение паков `bake diff` (Б3), сравнение клипов — `Anim/ClipContent.cs`; `skelcheck`, `fingers` — мёртвые |
| — | `Anim/ContentFiles.cs`, `Anim/ClipContent.cs` | запись файла контента через `*.tmp` (как `ClipAsset.Save`); клипы по содержимому без `SourceHash` |
| `Anim/MeshDecimate.cs`, `Anim/SpineChain.cs` | `Anim/` | открыты: их зовёт анатомия `Bake.Game` |
| `Texture/*`, `Processors/TextureArrayProcessor.cs` | `Texture/`, `Processors/` | формат `.swtex` (`TextureData`) — в `Engine.Formats`, файл пишет его писатель; без предпросмотра (Б7) |
| `Processors/FontVectorProcessor.cs`, `UiFontBaker` (в SWORD — сборка интерфейса) | `Processors/`, `Font/UiFontBaker.cs` | пекарь — в плагине (сторона `Neutral`, без `Engine.Ui`); список ролей шрифта — `UiVectorFontFormat.RoleNames`/`TryParseRole` в Formats; файл пишет `UiFontFile`; без `FontPreview` (Б7) |
| — (прототип — `inspect compare`) | `Anim/ClipComparer.cs` | сравнитель `.swclip` для `sword-bake diff` |

## Обработчики

| обработчик | параметры | выход |
|---|---|---|
| `anim.skeleton` (`SkeletonProcessor`) | `clips` — эталонный raw-экспорт рига (читается только `manifest.json`: кости, родители), `refPose` — `skeleton.json` с позой покоя, `fingerSrc` — raw-экспорт исходного скелета (пальцы для хвата оружия) | `skeletons/<id>.swskeleton`; ID скелета — последний сегмент ID единицы, единица — в `skeletons/` |
| `skin.mesh` (`SkinMeshProcessor`) | `skeleton` — ссылка на риг, `mesh` — glTF (.glb) Manny, `out` | `<ID>.swmesh`: суставы — кости экспорта по имени, bind-поза — поза покоя рига, оси glTF → UE подбираются по ней (знаковая перестановка с наименьшим отклонением центров вершин от суставов) |
| `anim.pack` (`AnimPackProcessor`, инкрементальный) | `skeleton` — ссылка на риг, `anims` — raw-экспорт, `fingerSrc`, `sets` — набор → маски папок пути UE | `anim/<набор>/<клип>.swclip`; кости экспорта обязаны совпасть с эталоном рига; каждый клип — ровно в один набор |
| `texture.array` (`TextureArrayProcessor`) | `layers` — PNG по порядку (маски `*`/`?` — по алфавиту, каталог — вход; хвост `@x,y,сторона` — участок, `#имя` — имя слоя), `channel` (`luma`, `r`, `g`, `b`, `a`, `normal`, `rgb`), `size` (степень двойки), `compress`, `tile` (бесшовная плитка), `flipU` (слои, отражённые по U), `out` | `<ID>.swtex`: массив слоёв одного размера, полная цепочка мипов, BC4 / BC5 (нормаль xy) / BC1 sRGB (`rgb`), без сжатия — R8 / RG8 / RGBA8 sRGB; у слоя — имя и исходное соотношение сторон |
| `font.vector` (`FontVectorProcessor`) | `font` — TrueType (квадратичные контуры; CFF не подходит), `roles` — роли интерфейса (`Body`, `Head`, `Label`, `NumLight`, `Num`; роль — ровно у одного шрифта), `extra` — файлы рядом (лицензия), `out` | `ui/fonts/<имя>.swfont`: контуры для GPU-векторного текста, роли и сам ttf (для HarfBuzz); игра склеивает все шрифты `ui/fonts/` при загрузке |

Остальные ассеты ссылаются на риг (`[AssetRef("anim.skeleton")]`, `ctx.Ref<SkeletonParams>`), а не повторяют позу покоя и
эталонные клипы. В один набор могут писать несколько единиц (1HM и Combat Master → `sword_1h`): импорт трогает только свои
клипы.

## Путь клипа: от raw-экспорта до `.swclip`

1. **Unreal** ([tools/ue/README.md](../ue/README.md)): ретаргет на UE5 Manny, `export_gasp.py` с конфигом → `manifest.json`
   (`sword-rawanim` v1: кости, родители, `units cm`, `axes ue`, `rootForward`, клипы: имя, путь UE, fps, кадры, цикл) и
   `clips/<имя>.bin` (кадры × кости × 7 float32, component space, root motion — в кости `root`, 30 кадров/с).
2. **`anim.pack`**: сверка костей с эталоном рига → набор каждого клипа (`AnimImport.Route`: маски `*`, `?` по папкам пути UE,
   без учёта регистра) → хеш исходника клипа (`SourceHash`); клип с тем же хешем, скелетом и хешем скелета в прежнем паке
   не перепекается → `SkinBake.BuildClips`:
   - оси и единицы — `UeSpace`: UE (X, Y, Z-вверх, левая, см) → персонаж (+Z вперёд, +X влево, Y вверх, м), `rootForward` → +Z;
   - пальцы (`fingerSrc`) — `FingerTransfer`: фаланги цели доворачиваются по направлениям фаланг исходного клипа в системе кисти,
     пясть — в позе покоя Manny (IK-ретаргет UE4→UE5 ломает кисть);
   - **корень**: источник — кость `root`, если она поворачивается больше 0,5° или уезжает больше 1 см, иначе таз; горизонтальное
     смещение и курс от первого кадра — в `RootMotion[f]` (Y = 0) и `RootYaw[f]`; система первого кадра — там, где источник,
     лицом по курсу root; у цикла с источником-тазом — равномерный дрейф (раскачка остаётся в позе), у root — покадрово
     (`Circle_Strafe`); корневые суставы — в системе корневого движения кадра, остальные — относительно родителя;
   - проверка прямой кинематики против экспорта — строка лога «проверка FK: макс. расхождение суставов».
3. **Пак**: `.swclip` v2 (`ClipFile`): скелет и его хеш, частота, кадры, суставы, цикл, путь UE, `SourceHash`, позы,
   `RootMotion`, `RootYaw`. Библиотеку клипов и базу Motion Matching игра строит при загрузке.

## Что можно менять без Unreal, а что — только в Unreal

- **Без Unreal** (raw-экспорт лежит в `${src}/_Export`): маршрутизация и наборы (`sets` манифеста — без кода); правило
  источника корня и пороги, равномерный дрейф цикла, система первого кадра, перенос пальцев (код `Anim/SkinBake.cs`,
  `FingerTransfer.cs` — затем **поднять `AnimImport.ToolVersion`**, иначе прежние клипы останутся: хеш кода не видит).
- **Нет в выпечке** (добавлять кодом): обрезка краёв, передискретизация (все клипы экспорта — одна частота, иначе ошибка),
  зеркалирование, отбор и пропуск клипа (каждый клип экспорта — ровно в одном наборе), разметка контактов и тегов.
- **Только в Unreal**: частота кадров, отбор клипов на экспорте, флаг цикла (`loopRegex`), ретаргет, кривые — скрипты
  `tools/ue`, [tools/ue/README.md](../ue/README.md).

## `SourceHash` клипа

SHA-256 (первые 32 hex) от: `.bin` клипа, параметров клипа (fps, кадры, цикл), пространства экспорта (единицы, оси,
`rootForward`), источника пальцев (его `.bin`, `skeleton.json` позы покоя, fps, кадры, пространство, кости и родители) и
`AnimImport.ToolVersion` («sword-animbake import 4»). Кости и родители цели покрывает хеш скелета в том же заголовке.

**Сборок конвертера в хеше нет** (решение 2026-09-25): смена Debug ↔ Release или пересборка без правок не перепекает
клипы — ассеты `anim.pack` устаревают по отпечатку кода обработчика и пересобираются, но клипы берутся из прежнего пака
(«записано 0»). Цена — правку выпечки клипа хеш не замечает: поднять `ToolVersion`. В эталонном паке SWORD в хеш входил
MVID сборок — клипы с эталоном сверяются по содержимому без `SourceHash` (`ClipContent.Diff`).

## Открытый API для `Sword.Bake.Game`

Открыто то, что зовут оружие и анатомия: `RawManifest`, `RawClipInfo`, `RawClip`, `RawAnimIO`, `UeSpace`, `UeRig`,
`ClipSource`, `FingerSource`, `FingerStats`, `GltfMesh`, `SkinBake.GripAttachment`, `MeshDecimate`, `SpineChain`,
`ContentFiles` (запись выхода), `SkeletonParams`, `SkinMeshParams`, `SkeletonProcessor` (`ProcessorId`, `SkeletonId`,
`ManifestOf`), `SkinMeshProcessor` (`ProcessorId`); `UiFontBaker` — ещё и окнам инструментов (системные шрифты на лету).
Остальное — `internal` (`AnimImport`, `FingerTransfer`, `SkinBake.Mesh`/`BuildClips`, `PoseReport`, `ClipContent`,
`ClipComparer`, `TextureBake`, обработчики, `Inspect`; тестам — `InternalsVisibleTo Sword.Bake.Engine.Tests`).

## Сравнитель `.swclip`

`ClipComparer` (`IContentComparer`, контракт — README ядра) — для `sword-bake diff` в проекте с этим плагином: оба клипа
читает `ClipFile`, сравнивает `ClipContent.Diff` — заголовок и все массивы побайтно, кроме `SourceHash`. Равны — «без
SourceHash»; отличаются — первое отличие (поле, число отличных значений, наибольшая разница, кадр); не клип —
`InvalidDataException` (расхождение с текстом ошибки). Остальные форматы движка сравниваются байтами: выпечка на той же
машине и рантайме детерминирована.

## Команда `inspect`

`sword-bake inspect КОМАНДА` (в каталоге проекта):
- `meshinfo GLB` — вершины, треугольники, суставы, границы, bind-точки ключевых костей;
- `rawpose DIR --clip ИМЯ [--frame N]` — курс root и точки костей кадра raw-экспорта в пространстве персонажа.

## Зависимости

`Sword.Bake` (`Private=false`, `ExcludeAssets=runtime` — ядро даёт хост), `Engine.Content` (пути контента), `Engine.Formats`
(писатели `.swclip`, `.swskeleton`, `.swmesh`, `.swtex`, `.swfont`, роли шрифта), `Engine.Animation` (`ClipLibrary`,
`Skeleton`). Сторона `Neutral`. NuGet — `StbTrueTypeSharp` (контуры TrueType пекаря шрифтов).
Выход — `bin/<конфигурация>/Sword.Bake.Engine.dll` (без TFM), `EnableDynamicLoading`.

## Проверки

- `dotnet test tests/Sword.Bake.Engine.Tests` — синтетика (спецификация пекарни): glTF, маршрутизация, корневое движение,
  оси UE и окно пальцев, ID импорта, `SourceHash` (от исходника — да, от сборки — нет: копия сборки с другим MVID считает тот
  же хеш), инкрементальный импорт, меш на синтетическом риге; плагин в настоящем хосте (`sword-bake` процессом: скелет, меш
  и клипы читаются загрузчиками движка, FK повторяет экспорт, повторная и принудительная сборка не переписывают клипы);
  текстуры (BC4 держит края маски и точные нули; массив — мипы, слои, круг записи), `SpineChain`, сравнитель `.swclip`
  (только `SourceHash` — равны, любое другое поле — отличаются, не клип — ошибка; паки через `PakDiff`).
- Категория **Source** (`--filter Category=Source`; нет исходников — пропуск, корни — `BAKE_ROOT_SRC` или
  `D:/Temp/GamedevAssets`, `BAKE_ROOT_ASSETS` или `D:/Temp/AssetsDatabase`; нужен и эталонный пак): временный проект с
  манифестами обработчиков этого плагина (параметры — как в `Bake/`), `sword-bake build`, сверка с `Content.reference.swpak`:
  все 1048 клипов — по содержимому (без `SourceHash`), скелет, меш, 8 текстур, 5 шрифтов и 3 лицензии — байт в байт. Итог —
  вывод теста и `artifacts/bake/source-check.json`; замеры — `docs/history/baker.md`. `verify.ps1` гоняет Source отдельным
  прогоном после остальных тестов: выпечка занимает все ядра и ломала тесты на настоящих часах.
- Полная выпечка проекта `Bake/` и `sword-bake diff` с эталоном — [Bake/README.md](../../Bake/README.md).

## Агентский доступ

Итог анализа (раздел 8 плана): пекарня — процесс командной строки, агентский вид — `--json` ядра (`status`, `build`,
`diff`) и этот README. Агент видит и проверяет изменение выпечки:
- сверку с эталоном на настоящих исходниках — тест Source и `artifacts/bake/source-check.json` (по наборам и форматам; у
  отличий клипа — поле, число отличных значений, наибольшая разница, первый кадр); весь пак — `sword-bake diff --json` в
  `Bake/` (`unexpected[]`, у клипа — `detail` сравнителя);
- ход выпечки — лог `sword-bake build` (текстом; в `--json` — у упавших ассетов): сводка корня (из root/из таза, наибольший
  поворот и сдвиг), перенос пальцев (отклонение фаланг до/после, сгиб пясти), проверка FK, «записано / без изменений / удалено»;
- исходники — `inspect rawpose`, `inspect meshinfo`;
- клип пака — загрузчики движка (`ClipLibrary`, `Skeleton`), в живой игре — `anim.clips`, `anim.pose` агента;
- текстуры и шрифты — лог выпечки (слои, размер, формат, мипы; отчёт пекаря шрифта) и побайтная сверка.

## Ограничения

- Предпросмотры Studio (`AnimPreview`, `MeshPreview`, текстур, `FontPreview`) не перенесены — Б7.
- Правка кода выпечки клипа без подъёма `ToolVersion` не перепекает клипы (см. `SourceHash`).
- Инкрементальная проверка читает каждый прежний клип целиком (≈ 340 МБ на все наборы); выпечка набора держит в памяти все
  его raw-клипы (кэш `ClipSource`) и таблицу `ClipLibrary` — у GASP сотни мегабайт.
- В выходе плагина — `Microsoft.Windows.SDK.NET.dll` (TFM Windows решения): теневая копия плагина у ядра — около 26 МБ.

## Известные ошибки

Нет.
