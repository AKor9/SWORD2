# Sword.Engine.DevTools — правила

Инструменты разработчика в клиенте (раздел 3 плана: «панели: ручки (F3), инспектор сущностей, профайлер»), сторона
Client. Сейчас — панель ручек `TuningPanel` (точка 6е: вкладка «Бой» над ручками симуляции, F3, сохранение в `sword.user.json`). Логика —
SWORD 2eab13b (`Modules/Tools/Tuning/TuningPanel.cs`).

- Зависит от `Engine.Client` (контракт панели `IClientPanel`, `FrameInput`, `InputAction`), `Engine.Ui` (виджеты
  `UiContext`) и `Engine.Diagnostics` (`TuningGroup`); матрица разрешает ещё `Ecs` (инспектор — позже).
- Только из exe (правило `devtools-exe-only` архитектурных тестов; `Game.DevTools` — тоже): ни движок, ни игра на DevTools не
  ссылаются, иначе сборка без них невозможна. Сборка без DevTools — `-p:WithoutDevTools=true`: в
  `Sword.Game.App.csproj` ссылка под условием и `DevToolsMode.cs` убирается; проверка — тест
  `DevToolsReferences_AreRemovedWithoutDevTools` (ссылки exe под условием) и шаг `verify.ps1` `without-devtools` (собирается,
  DLL и записи в `Sword.deps.json` нет; в обычной сборке есть).
- Панель — `IClientPanel` (Engine.Client): её ведёт `ClientHost` (`AddPanel` до `Run`) через слой `ClientPanels`. Кадр:
  шаг ввода кадра, **до** ввода игры — `Update(FrameInput)` у всех панелей (клавиша открытия), затем, если хоть одна видна,
  ввод интерфейса из окна (`UiInputSource`), `BeginFrame`, `Draw` видимых, `EndFrame`; квады — UI-проходом `panels` после
  проходов игры. Изменение ручки, поставленное панелью, попадает в тики этого же кадра.
- Мышь у панели (`Draw` вернул истину: курсор над ней или тянут её виджет) — игре в этом кадре не достаются действия на
  кнопках мыши и колесо (`FrameInput.UiMouse`: «мышь над панелью не бьёт»), поле ввода с фокусом — действия на клавишах
  (`FrameInput.UiKeyboard`), Escape окно не закрывает. Отпускания доходят всегда. Панель закрылась — фокус и захват сняты.
- Бюджет: все панели закрыты — только опрос `Update` (без ввода интерфейса, кадра, шрифтов и прохода: UI-проход заводится,
  когда панель впервые видна); открытая панель — 0 Б на кадр (тест). Кадр без выделений: ID строк — стек ID (`PushId`),
  не склейка строк; строка состояния группы пишет текст на стек (`TuningText`); `Info` со строкой (`Func<string>`) выделяет
  строку в кадре — только для редких групп.

## Панель ручек (`TuningPanel`)

Вкладка на каждую `TuningGroup` (`Groups`) — строка меню слева; длинная группа прокручивается. Строка числа — подпись (ярче, пока значение не по
умолчанию; ПКМ — к значению по умолчанию), ползунок (`log` — логарифмическая шкала), поле числа; флаг; кнопка модуля; строка
состояния (`InfoRow`: длинное значение теснит подпись); заголовок раздела. Подсказка строки под курсором — над подвалом, переносом по словам в три строки (`UiContext.Paragraph`). Подвал: «сохранить» и
«загрузить» — делегаты хоста `Save`/`Load` (`Func<TuningGroup, string>`: итог — строка состояния; нет делегата — кнопка
выключена; исключение — «не сохранено: …» в строке и журнале), «сброс» — `TuningGroup.ResetAll`. Раскладка:
слева, ширина 510, от `Top` (пиксели экрана, под HUD); высота — по содержимому вкладки или по меню, если оно длиннее, не ниже окна (дальше прокрутка), прямоугольник кадра — `Bounds`. Слева — меню вкладок столбцом (`UiContext.TabList`, `MenuWidth` 104), справа — ручки выбранной, подсказка и подвал. Столбец подписей — по самой широкой подписи числа и сведения вкладки (кегль `FontSize`, как у флагов), 30 %…`MaxLabelShare` (55 %) строки. Текст панели — шрифтами без греческих букв: в подписях и значениях их нет (□). Открытие — действие `Toggle` (конструктор) или `Open`. Поле числа —
`ValueWidth` 58 px, текст — минус два `UiContext.FieldPadding` (44 px): формат ручки должен вмещать её значения (7з-4: «0.000002» резалось до
«0.0000» — малые значения с порядком, `0.#e-0`; тест `CorpseTuningDrawTests.KnobValues_FitNumberField_…` — все ручки «Рэгдолл» и «Бой»).

Отличия от SWORD: файла `Tuning/<группа>.json` нет — сохранение у хоста (решение пользователя: `sword.user.json`); ошибка
записи не роняет кадр; кадр без выделений (в SWORD — склейка ID `f.Key + ".num"`, `"btn." + label` и строка состояния
строкой каждый кадр); ПКМ по подписи — только над видимой частью строки (`UiContext.IsHovered`).

## Подключение (точка 6е)

`Game.App/DevToolsMode.cs`: `AddDevTools` — панель ручек с действием `tuning` (F3, секция `input`; привязки разрешает Program.cs вместе с
действиями игры; в бенче панелей нет), масштаб панелей — масштаб HUD (`ClientPanels.Scale = Hud.Scale`); `ConnectDevTools` (после создания
`ClientHost`: клиент игры завёл группы в `Initialize`) — вкладки `ArenaClient.TuningGroups` («Бой»), `Save`/`Load` — `TuningBinding.Save`/
`Load` в `sword.user.json` рядом с `sword.json` (`UserConfig.PathFrom`), `Top` — нижний край панели производительности HUD
(`ArenaClient.HudBottom`). Последняя вкладка — «Графика» (`GraphicsTuning`: vsync — `ClientHost.Vsync`, то же, что V; SMAA — `Renderer.Smaa`; тени — `Renderer.Shadow` и `Renderer.Lights.Shadows`; GTAO — `Renderer.Ao`; автоэкспозиция — `Renderer.AutoExposure`; TAA — `Renderer.Taa`; постобработка — `Renderer.Post`; материалы — `ArenaClient.AddGraphicsTuning`; не сохраняются). Строки группы «Бой» — ручки симуляции через `Engine.Client/TuningBinding`: `Get` — ожидающее значение, пока тик
команды не сыгран (решение 6е: на паузе ползунок не отскакивает), иначе мир; `Set` — команда ручки на ближайший тик; «сброс»
(`TuningGroup.ResetAll`) — командами к значениям в коде.

## Как увидеть (раздел 8 плана)

Панель — рисунок поверх кадра, состояние симуляции она не держит: ручки — ресурс мира и команда `TuningCommand` в реплее. Панель на
экране — `Sword.exe --shot`, открыв её удержанием действия прогона (`--hold tuning`: в первом кадре действие ещё и «нажато»); видна
ли — `Sword.exe --json` → `panels`. Логика панели, маска ввода игры и 0 Б — тесты без окна и видеокарты (вкладка
«Бой» над настоящей симуляцией — `Sword.Game.Tests/CombatUiDrawTests`). Нового инструмента не нужно.

Проверка: `tests/Sword.Engine.DevTools.Tests` (`TuningPanelLogicTests` — закрытая и пустая панель, клавиша открытия;
`TuningPanelTests`, категория Content — строки, вкладки, подвал, подсказка, мышь у панели, кадр без выделений),
`tests/Sword.Engine.Client.Tests` (`ClientPanelsTests`, `UiInputSourceTests`), `tests/Sword.Engine.Runtime.Tests/TuningGroupTests`,
`tests/Sword.Architecture.Tests` (`devtools-exe-only`, `DevToolsReferences_AreRemovedWithoutDevTools`), `verify.ps1`
`without-devtools`.
