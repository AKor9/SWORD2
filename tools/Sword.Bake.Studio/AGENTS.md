# Sword.Bake.Studio — правила

Окно пекарни на движке SWORD2: `Win32Window`, рендер-граф (очистка + `Renderer.Ui`), виджеты `Sword.Engine.Ui`. Модель —
`StudioApp` (без окна: проект, статусы, фоновые задачи, лог, слежение), виды — `StudioView*`, форма — `ParamsEditor`.

- Логику выпечки не дублировать: всё через ядро (`BakeProject`, `Builder`, `ManifestWriter`); обработчики — только из плагинов
  проекта (`PluginLoader` ядра). Сборки `Sword.Bake.Engine`/`Sword.Bake.Game` в выход Studio не класть: одноимённая копия в
  контексте по умолчанию ломает загрузку плагина. Пекарь шрифтов — исходником (`Shared\UiFontBaker.cs`, ссылка в csproj).
- Шрифты окна — системные (`StudioFonts`, кэш `%LOCALAPPDATA%\SWORD Bake Studio\fonts`), в рендер — `RendererOptions.UiFonts`;
  пак контента Studio не открывает.
- Фоновые потоки только кладут события в очередь и будят окно; состояние меняет `StudioApp.Pump` на потоке окна.
- Окно агента — без фокуса: `--shot`/`--gallery` всегда, иначе `--no-activate` или `SWORD_NO_ACTIVATE=1`.
- Предпросмотр (`PreviewPanel`) пока пуст — «нет предпросмотра»; предпросмотры `IAssetPreview` — следующая точка.

Проверка: `dotnet test tests/Sword.Bake.Studio.Tests` (модель на копии `Bake/`, форма туда-обратно, сборка против
`sword-bake`; с окном — категория Gpu). Глазами: `Sword.Bake.Studio --project Bake --gallery КАТАЛОГ` — снимки всех видов
`NN-вид.png` и лог модели в консоль; снимок по таймауту печатает, чем модель занята.
