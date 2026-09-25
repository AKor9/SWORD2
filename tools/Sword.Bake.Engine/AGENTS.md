# Пекарня движка (`Sword.Bake.Engine`) — правила

Плагин пекарни: анимация (`anim.skeleton`, `skin.mesh`, `anim.pack`, `inspect`), текстуры (`texture.array`), шрифты
(`font.vector`), сравнитель `.swclip` для `diff`. Логика выпечки перенесена из SWORD 2eab13b как есть и выпекает эталонный
пак по содержимому — менять её только по решению. Устройство — README.md.

- **Правка выпечки клипа — поднять `AnimImport.ToolVersion`.** Код конвертера в `SourceHash` не входит (решение
  2026-09-25: смена Debug ↔ Release не перепекает клипы), поэтому без подъёма версии прежние клипы останутся в паке.
- После правки выпечки — тест Source (`dotnet test tests/Sword.Bake.Engine.Tests --filter Category=Source`): расхождение
  с эталоном — в `artifacts/bake/source-check.json`; ожидаемое расхождение записать в README и `docs/history/baker.md`.
- Оси и единицы UE — только `UeSpace`; соглашения скелета Manny (кисти, пальцы, `_Mirrored`) — только `UeRig`.
- Форматы пака пишут писатели `Engine.Formats` (`ClipFile`, `SkeletonFile`, `SkinnedMeshData`, `TextureData`, `UiFontFile`)
  через `ContentFiles.Write` (файл `*.tmp` и подмена): раскладку формата здесь не повторять. Роли шрифта — только
  `UiVectorFontFormat.RoleNames`/`TryParseRole`.
- Сравнитель `.swclip` пропускает только `SourceHash`; ослаблять его ради «зелёного» `diff` нельзя (README ядра).
- Всё прочитанное и записанное — через `BakeContext` (`InputFile`, `InputDir`, `Output`, `OutputDir`), иначе актуальность
  о нём не узнает; `anim.pack` пишет только в `ctx.OutputRoot` и удаляет там свои устаревшие клипы.
- Открытый API — для `Sword.Bake.Game` (список — README): его изменение ломает плагин игры. Остальное — `internal`.
- Ссылка на ядро — `Private=false`, `ExcludeAssets=runtime`; `Sword.Bake.Game` ссылается на этот плагин так же и стоит
  в `plugins` после него. Сторона `Neutral`: без `Engine.Ui` и клиентских сборок (матрица — `Bake.Engine` → Bake, Content,
  Formats, Animation).

Проверка: `dotnet test tests/Sword.Bake.Engine.Tests` (синтетика и плагин в `sword-bake`), с исходниками — категория Source.

Типичные ошибки выпечки:
- «кости экспорта … не совпадают со скелетом …» — экспорт на другом скелете, чем эталон рига (`anim.skeleton` → `clips`).
- «клип … не попал ни в один набор» / «попал в несколько наборов» — маски `sets`.
- «fps X ≠ Y» — клипы экспорта разной частоты (передискретизации нет — переэкспорт в Unreal).
- «оси glTF не подобрать» — меш и `skeleton.json` от разных скелетов или меш без скина.
- «в позе покоя пальцев нет кости …» — `refPose` рига не от целевого скелета (нужен при `fingerSrc`).
