# Sword.Engine.Ui — правила

Интерфейс без GPU (раздел 6 плана, «Ui»): список квадов за кадр (`UiDrawList`), нарезка прямоугольников (`UiRect`),
палитра (`UiTheme`), шрифты (`UiFonts`: склейка ролей `UiFontTables`, shaping HarfBuzz `UiTextShaper`, метрики).
Отрисовка — UI-проход рендера (`Sword.Engine.Render/UiPass.cs`, `Shaders/common/ui.hlsl`); файл шрифта —
`Sword.Engine.Formats/UiVectorFontFormat.cs`. Логика — SWORD 2eab13b (`Sword.Engine.Ui`).

- Ui не знает GPU, контент и платформу: файлы шрифтов читает вызывающий (`UiFontFile.Read` — рендер из пака, тест из
  пака или памяти), ввод придёт с виджетами.
- `UiQuad` — запись для шейдера (68 Б): поля, их порядок и `UiMode` меняются только вместе с `ui.hlsl`; размер сверяет
  `_Static_assert(sizeof(UiQuad) == UI_QUAD_SIZE)`, номера режимов приходят в шейдер из контракта (`UiPass.Contribute`).
- Горячий путь без выделений: строки — `ReadOnlySpan<char>` (форматирование на стеке), буфер shaping и массив квадов
  переиспользуются. Проверка — тесты «кадр без выделений» (Ui и HUD игры).
- Цвета — только из `UiTheme`; размеры — логические пиксели, масштаб даёт вызывающий.
- Список без шрифтов (`new UiDrawList()`) рисует фигуры; текст в нём — `InvalidOperationException`. `Append` списка с
  другими шрифтами — `ArgumentException`: номера glyph'ов в квадах — таблиц своих шрифтов.
- Шрифт из памяти (`UiFontFile`) склейка проверяет так же, как чтение (`UiFontFile.Validate`): в видеопамять не уходят
  ссылки за границы таблиц. Роль — точное имя `UiFace` (список «A,B» — не роль).
- Публичный API Ui несёт типы Formats (`UiFontFile`, записи шрифта в `UiFontTables`): Ui зависит от Formats по матрице.
  Игра их не использует — ссылку собранной игры на Formats поймал бы `CompiledAssemblies_FollowArchitecture`.
- Вызовы рисования — `UiDrawList.Split`: подряд квады одного конвейера под одной обрезкой, scissor в пределах цели.
- Текст режется и мерится по символам, а не по половинкам пар UTF-16: `TextFit` не отрезает половину эмодзи, каретка
  `CaretX` внутри пары стоит перед ней (в SWORD половинка давала .notdef).

Не перенесено (с первыми панелями DevTools): виджеты `UiContext` (кнопки, ползунки, списки, поля ввода, дерево,
вкладки), `UiInput`, `UiTextEdit`; растровые шрифты `UiFontRaster` и запекание `UiFontBaker` (пекарня, этап 3).

Проверка: `tests/Sword.Engine.Ui.Tests` (без видеокарты; на шрифтах пака — категория Content).
