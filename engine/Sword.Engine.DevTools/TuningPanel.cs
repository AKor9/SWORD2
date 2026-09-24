using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Engine.DevTools;

/// <summary>
/// Панель ручек (F3): вкладка на каждую <see cref="TuningGroup"/>. Строка числа — подпись, ползунок и поле числа (ПКМ по
/// подписи — к значению по умолчанию, подпись ярче, пока значение не по умолчанию), флаг, кнопка модуля, строка состояния;
/// длинная группа прокручивается; подсказка строки под курсором — в строке состояния; внизу — «сохранить», «загрузить»,
/// «сброс». О модулях не знает ничего, кроме их групп: откуда значение и куда уходит запись — дело делегатов группы, как
/// сохранить и загрузить — делегатов хоста (<see cref="Save"/>, <see cref="Load"/>).
///
/// Панель клиента (<see cref="IClientPanel"/>): открывается действием <see cref="Toggle"/> (или <see cref="Open"/>), рисуется в
/// общем интерфейсе панелей до ввода игры; курсор над панелью или протяжка её ползунка — мышь в бой не уходит.
///
/// Перенос <c>Modules/Tools/Tuning/TuningPanel.cs</c> SWORD 2eab13b: раскладка и размеры те же. Отличия: запись и чтение
/// файла <c>Tuning/&lt;группа&gt;.json</c> — не здесь, а в делегатах хоста (без них кнопки выключены); ошибка записи — в строке
/// состояния и журнале, а не падение кадра; кадр без выделений (ID строк — стек ID, а не склейка строк; строка состояния —
/// текст на стеке); ПКМ по подписи — только над видимой частью строки (<see cref="UiContext.IsHovered"/>).
/// </summary>
public sealed class TuningPanel : IClientPanel
{
    /// <summary>Ширина панели, логические пиксели (SWORD).</summary>
    public const float Width = 340;

    private const float Margin = 8, RowHeight = 24, HeaderHeight = 22, TabsHeight = 24, FooterHeight = 26, StatusHeight = 18;

    /// <summary>Длина текста строки состояния (<see cref="TuningText"/>), символов.</summary>
    public const int InfoCapacity = 128;

    private readonly InputAction[] _actions;
    private int _tab;
    private string _status = "";
    private string[] _titles = [];

    /// <param name="toggle">Действие «открыть/закрыть» (композиция: F3 — точка 6е); null — только <see cref="Open"/>.</param>
    public TuningPanel(InputAction? toggle = null)
    {
        Toggle = toggle;
        _actions = toggle is null ? [] : [toggle];
    }

    /// <summary>Действие открытия панели; null — нет.</summary>
    public InputAction? Toggle { get; }

    IReadOnlyList<InputAction> IClientPanel.Actions => _actions;

    /// <summary>Группы — вкладки по порядку.</summary>
    public List<TuningGroup> Groups { get; } = [];

    public bool Open { get; set; }

    /// <summary>Номер выбранной вкладки.</summary>
    public int Tab
    {
        get => _tab;
        set => _tab = value;
    }

    /// <summary>Строка состояния: итог последнего сохранения, загрузки или сброса.</summary>
    public string Status => _status;

    /// <summary>
    /// Сохранить группу (кнопка «сохранить»): хост пишет значения (<see cref="TuningGroup.Values"/>) и возвращает строку
    /// состояния («сохранено: …»). null — кнопка выключена. Исключение — «не сохранено: …» в строке состояния и журнале.
    /// </summary>
    public Func<TuningGroup, string>? Save { get; set; }

    /// <summary>Загрузить группу (кнопка «загрузить»): хост читает и применяет (<see cref="TuningGroup.Apply"/>), возвращает строку состояния.</summary>
    public Func<TuningGroup, string>? Load { get; set; }

    /// <summary>Верхний край панели, пиксели экрана (под HUD игры); null — от верха окна.</summary>
    public Func<float>? Top { get; set; }

    public bool Update(in FrameInput input)
    {
        if (Toggle is not null && input.WasPressed(Toggle)) Open = !Open;
        return Open && Groups.Count > 0;
    }

    bool IClientPanel.Draw(UiContext ui) => Draw(ui, Top?.Invoke() ?? 0);

    /// <summary>Кадр панели (закрытая или без групп — ничего).</summary>
    /// <param name="top">Верхний край, пиксели экрана.</param>
    /// <returns>Курсор над панелью или тянут её виджет — мышь не должна уходить в бой.</returns>
    public bool Draw(UiContext ui, float top)
    {
        if (!Open || Groups.Count == 0) return false;
        var t = ui.Theme;
        float s = ui.Scale;
        var panel = new UiRect(Margin * s, top + 6 * s, (Margin + Width) * s, ui.Height - Margin * s);
        if (panel.Height < 120 * s) panel = panel with { Y0 = panel.Y1 - 120 * s };
        ui.Panel(panel, t.OverlayStrong, 8 * s);
        uint activeBefore = ui.ActiveId;
        var inner = panel.Shrink(10 * s);

        _tab = Math.Clamp(_tab, 0, Groups.Count - 1);
        UpdateTitles();
        ui.Tabs(inner.SplitTop(TabsHeight * s, out inner), "diag.tabs", ref _tab, _titles);
        var group = Groups[_tab];
        inner.SplitTop(6 * s, out inner);

        var footer = inner.SplitBottom(FooterHeight * s, out inner);
        var status = inner.SplitBottom(StatusHeight * s, out inner);

        var items = group.Items;
        float rowH = RowHeight * s, headH = HeaderHeight * s;
        float content = 0;
        for (int i = 0; i < items.Count; i++) content += items[i] is TuningHeader ? headH : rowH;

        string? hoverHint = null;
        Span<char> text = stackalloc char[InfoCapacity];
        ui.PushId(group.Id);
        var area = ui.BeginScroll(inner, "diag.scroll", content);
        float y = area.Y0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float h = item is TuningHeader ? headH : rowH;
            var row = new UiRect(area.X0, y, area.X1, y + h);
            y += h;
            if (row.Y1 < inner.Y0 || row.Y0 > inner.Y1) continue;   // вне окна: не рисовать и не ловить мышь
            DrawItem(ui, row, item, text);
            if (item.Hint is { } hint && ui.IsHovered(row)) hoverHint = hint;
        }
        ui.EndScroll();

        ui.Label(status, hoverHint ?? _status, t.TextFaint, UiAlign.Left, UiFace.Label, t.SmallFontSize);

        float bw = (footer.Width - 8 * s) / 3;
        if (ui.Button(footer.SplitLeft(bw, out footer), "сохранить", enabled: Save is not null, id: "diag.save"))
            _status = Run(group, Save!, "не сохранено");
        footer.SplitLeft(4 * s, out footer);
        if (ui.Button(footer.SplitLeft(bw, out footer), "загрузить", enabled: Load is not null, id: "diag.load"))
            _status = Run(group, Load!, "не загружено");
        footer.SplitLeft(4 * s, out footer);
        if (ui.Button(footer, "сброс", id: "diag.reset"))
        {
            group.ResetAll();
            _status = "значения по умолчанию";
        }
        ui.PopId();

        bool inside = ui.Input.MouseInside && panel.Contains(ui.Input.Mouse);
        bool dragging = ui.ActiveId != 0 && (activeBefore != 0 || inside);
        return inside || dragging;
    }

    /// <summary>Заголовки вкладок — массив, пересобирается только когда группы сменились (кадр без выделений).</summary>
    private void UpdateTitles()
    {
        bool same = _titles.Length == Groups.Count;
        for (int i = 0; same && i < Groups.Count; i++) same = ReferenceEquals(_titles[i], Groups[i].Title);
        if (same) return;
        _titles = new string[Groups.Count];
        for (int i = 0; i < Groups.Count; i++) _titles[i] = Groups[i].Title;
    }

    /// <summary>Сохранение или загрузка хостом: итог — строка состояния; исключение — в строку и журнал, кадр идёт дальше.</summary>
    private static string Run(TuningGroup group, Func<TuningGroup, string> action, string failed)
    {
        try
        {
            string status = action(group);
            Log.Info("tuning", $"{group.Id}: {status} ({group.Describe()})");
            return status;
        }
        catch (Exception e)   // файл занят, конфиг испорчен, ошибка хоста — панель разработчика не роняет игру
        {
            Log.Error("tuning", $"{group.Id}: {failed}: {e.Message}");
            return $"{failed}: {e.Message}";
        }
    }

    private static void DrawItem(UiContext ui, UiRect row, TuningItem item, Span<char> text)
    {
        var t = ui.Theme;
        float s = ui.Scale;
        switch (item)
        {
            case TuningHeader:
                ui.SectionHeader(row.Shrink(0, 2 * s), item.Label);
                break;

            case TuningFloat f:
            {
                var label = row.SplitLeft(row.Width * 0.42f, out var rest);
                var value = rest.SplitRight(58 * s, out var slider);
                float current = f.Get();
                ui.Label(label, f.Label, current != f.Default ? t.Text : t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
                ui.PushId(f.Key.Length > 0 ? f.Key : f.Label);
                float edit = current;
                if (ui.Slider(slider.Shrink(4 * s, 3 * s), "slider", ref edit, f.Min, f.Max, f.Log)) f.Set(f.Clamp(edit));
                edit = f.Get();
                if (ui.NumberField(value.Shrink(0, 1 * s), "num", ref edit, f.Format, MathF.Min(f.Min, f.Max), MathF.Max(f.Min, f.Max)))
                    f.Set(f.Clamp(edit));
                ui.PopId();
                // щелчок правой по подписи — к значению по умолчанию
                if (ui.IsHovered(label) && ui.Input.Pressed(1)) f.Set(f.Default);
                break;
            }

            case TuningBool b:
            {
                bool v = b.Get();
                if (ui.Toggle(row.Shrink(0, 2 * s), b.Label, ref v, b.Key.Length > 0 ? b.Key : null)) b.Set(v);
                break;
            }

            case TuningButton button:
                ui.PushId("btn");
                if (ui.Button(row.Shrink(0, 2 * s), button.Label)) button.Click();
                ui.PopId();
                break;

            case TuningInfo info:
            {
                int n = Math.Clamp(info.Text(text), 0, text.Length);
                ui.InfoRow(row, info.Label, text[..n]);
                break;
            }
        }
    }
}
