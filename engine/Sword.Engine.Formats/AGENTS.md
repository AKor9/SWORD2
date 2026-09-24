# Sword.Engine.Formats — правила

Форматы движка: чтение и запись без ввода-вывода файлов — только потоки и байты (откуда поток, решает вызывающий:
пак, пекарня, тест). Сейчас: меш `.swmesh` (`SkinnedMeshData`: вершины, индексы, суставы, секции), шрифт интерфейса
`.swfont` (`UiFontFile`: контуры для GPU и ttf для HarfBuzz; роли — строки, их смысл знает Ui), PNG (`Png`, для
снимков кадра), скелет `.swskeleton` (`SkeletonFile`) и клип `.swclip` (`ClipFile`, версии 1 и 2) библиотеки анимаций.

- Формат байт в байт — SWORD 2eab13b: его читает эталонный пак. Меняя формат, поднимать версию и читать прежние.
- Чтение — точным числом байт (`Stream.ReadExactly`); обрезанный или противоречивый файл — `InvalidDataException`
  с именем файла, а не `EndOfStreamException` или «успех» (тест `EveryTruncationIsInvalidData`).
- Формат не выходит за Render: игра грузит ассеты по пути (`Skinned.LoadMesh`), типы форматов в публичном API Render
  запрещены (`PublicSurfaceTests`). В симуляции меш читают `Engine.Animation` (поза привязки,
  `Skeleton.WithMeshBindPose`) и `Game.Data` (`WeaponMesh`, 6б: клинок меча по вершинам) — `SkinnedMeshData` наружу не отдают.

`SkinnedMeshData` переписан при точке 1б (чтение и запись потоком, точное чтение), проверки те же, что в SWORD.
`UiFontFile` — точка 1д: чтение SWORD (`UiVectorFontData`) плюс запись, проверка шрифта из памяти (`Validate`: её
проходят чтение, запись и склейка Ui), конечность рамок glyph'ов; испорченная длина строки — тоже `InvalidDataException`
(строка читается сама, не `BinaryReader.ReadString`: ошибка чтения потока — диск, пак — уходит как `IOException`, а не
«испорчена строка»). Числа таблиц сверяются до выделения памяти: полос — ровно `glyphs × 2 × 16`, таблицы — не длиннее
остатка потока (с поиском). `MaxBands` = `MaxGlyphs × 2 × 16` — предел 65 536 glyph'ов достижим (в SWORD `MaxBands`
2 000 000 ограничивал шрифт 62 500 glyph'ами).
`Png` перенесён без изменений логики.
`SkeletonFile`, `ClipFile` — этап 2 (`SkeletonAsset`, `ClipAsset` SWORD): байт в байт (тесты повторяют запись SWORD
поле за полем), строки — `FormatStrings.Read` (испорченная длина — `InvalidDataException`, общий с `UiFontFile`), поза
клипа читается одним блоком, счётчик кадров сверяется с длиной потока до выделения памяти. Хеш скелета — FNV-1a SWORD
(`SkeletonFile.ComputeHash`): по нему клипы пака находят свой скелет. Формат `.swskel` не перенесён — в паке его нет.

Проверка: `tests/Sword.Engine.Formats.Tests`.
