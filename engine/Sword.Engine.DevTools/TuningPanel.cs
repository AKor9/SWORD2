using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Engine.DevTools;

/// <summary>
/// Панель ручек (F3): слева — меню вкладок (вкладка на каждую <see cref="TuningGroup"/>), справа — её ручки; строка — подпись (ПКМ — к умолчанию), ползунок и поле
/// числа, флаг, кнопка, строка состояния; внизу — «сохранить», «загрузить», «сброс» через делегаты хоста. О модулях знает
/// только их группы. Кадр без выделений; ошибка записи — в строке состояния и журнале, а не падение кадра.
/// </summary>
public sealed class TuningPanel : IClientPanel
{
    /// <summary>Ширина панели, логические пиксели: меню вкладок <see cref="MenuWidth"/> слева, ручки — справа (~374).</summary>
    public const float Width = 510;

    /// <summary>Ширина меню вкладок слева, логические пиксели.</summary>
    public const float MenuWidth = 104;

    /// <summary>Наибольшая доля строки под подпись: столбец подписей — по самой широкой подписи числа вкладки, не шире.</summary>
    public const float MaxLabelShare = 0.55f;

    /// <summary>Ширина поля числа, логические пиксели: текст — в <c>ValueWidth − 2·UiContext.FieldPadding</c> (44 px). Формат
    /// ручки должен вмещать умолчание и пределы (у малых значений — с порядком, <c>0.#e-0</c>).</summary>
    public const float ValueWidth = 58;

    private const float Margin = 8, Padding = 10, Gap = 6, RowHeight = 24, HeaderHeight = 22, MenuRowHeight = 28, FooterHeight = 26;

    /// <summary>Строк подсказки (или итога сохранения) над подвалом: длинная подсказка переносится по словам.</summary>
    private const int StatusLines = 3;

    /// <summary>Длина текста строки состояния (<see cref="TuningText"/>), символов.</summary>
    public const int InfoCapacity = 128;

    private readonly InputAction[] _actions;
    private int _tab;
    private string _status = "";
    private string[] _titles = [];
    private TuningGroup? _labelGroup;
    private float _labelScale, _labelWidest;

    /// <param name="toggle">Действие «открыть/закрыть» (композиция: F3); null — только <see cref="Open"/>.</param>
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

    /// <summary>Сохранить группу: хост пишет <see cref="TuningGroup.Values"/> и возвращает строку состояния. null — кнопка
    /// выключена; исключение — «не сохранено: …» в строке и журнале.</summary>
    public Func<TuningGroup, string>? Save { get; set; }

    /// <summary>Загрузить группу (кнопка «загрузить»): хост читает и применяет (<see cref="TuningGroup.Apply"/>), возвращает строку состояния.</summary>
    public Func<TuningGroup, string>? Load { get; set; }

    /// <summary>Прямоугольник панели в последнем кадре, пиксели экрана: высота — по содержимому вкладки, не ниже окна.</summary>
    public UiRect Bounds { get; private set; }

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
        _tab = Math.Clamp(_tab, 0, Groups.Count - 1);
        UpdateTitles();
        var group = Groups[_tab];
        var items = group.Items;
        float rowH = RowHeight * s, headH = HeaderHeight * s, tabH = MenuRowHeight * s, pad = Padding * s, gap = Gap * s;
        float content = 0;
        for (int i = 0; i < items.Count; i++) content += items[i] is TuningHeader ? headH : rowH;
        float statusH = MathF.Ceiling(StatusLines * ui.Draw.LineHeight(UiFace.Body, ui.S(t.SmallFontSize)));

        // высота — по содержимому вкладки (или по меню, если оно длиннее), но не ниже окна: длинная вкладка прокручивается
        float y0 = top + 6 * s;
        float settingsH = content + 2 * gap + statusH + FooterHeight * s + 1;   // +1: округление — не полоса прокрутки
        float need = 2 * pad + MathF.Max(settingsH, Groups.Count * tabH);
        var panel = new UiRect(Margin * s, y0, (Margin + Width) * s, MathF.Min(y0 + need, ui.Height - Margin * s));
        if (panel.Height < 120 * s) panel = panel with { Y0 = panel.Y1 - 120 * s };
        Bounds = panel;
        ui.Panel(panel, t.OverlayStrong, 8 * s);
        uint activeBefore = ui.ActiveId;
        var inner = panel.Shrink(pad);

        // слева — меню вкладок, справа — ручки выбранной, подсказка и подвал
        var menu = inner.SplitLeft(MenuWidth * s, out inner);
        ui.TabList(menu, "diag.tabs", ref _tab, _titles, tabH);
        ui.Separator(inner.SplitLeft(2 * Gap * s, out inner).Shrink(0, gap), vertical: true);
        var footer = inner.SplitBottom(FooterHeight * s, out inner);
        inner.SplitBottom(gap, out inner);
        var status = inner.SplitBottom(statusH, out inner);
        inner.SplitBottom(gap, out inner);

        string? hoverHint = null;
        Span<char> text = stackalloc char[InfoCapacity];
        ui.PushId(group.Id);
        var area = ui.BeginScroll(inner, "diag.scroll", content);
        float labelW = LabelColumn(ui, group, area.Width);
        float y = area.Y0;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            float h = item is TuningHeader ? headH : rowH;
            var row = new UiRect(area.X0, y, area.X1, y + h);
            y += h;
            if (row.Y1 < inner.Y0 || row.Y0 > inner.Y1) continue;   // вне окна: не рисовать и не ловить мышь
            DrawItem(ui, row, item, text, labelW);
            if (item.Hint is { } hint && ui.IsHovered(row)) hoverHint = hint;
        }
        ui.EndScroll();

        ui.Paragraph(status, hoverHint ?? _status, t.TextDim, UiFace.Body, t.SmallFontSize);

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

    /// <summary>
    /// Ширина столбца подписей вкладки, пиксели экрана: самая широкая подпись числа и строки состояния плюс зазор, в
    /// пределах 30 %…<see cref="MaxLabelShare"/> строки. Замер — один раз на вкладку и масштаб.
    /// </summary>
    private float LabelColumn(UiContext ui, TuningGroup group, float rowWidth)
    {
        if (!ReferenceEquals(_labelGroup, group) || _labelScale != ui.Scale)
        {
            float fs = ui.S(ui.Theme.FontSize), widest = 0;
            var items = group.Items;
            for (int i = 0; i < items.Count; i++)
                if (items[i] is TuningFloat or TuningInfo) widest = MathF.Max(widest, ui.Draw.Measure(UiFace.Body, fs, items[i].Label));
            (_labelGroup, _labelScale, _labelWidest) = (group, ui.Scale, widest + ui.S(10));
        }
        return Math.Clamp(_labelWidest, rowWidth * 0.3f, rowWidth * MaxLabelShare);
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

    private static void DrawItem(UiContext ui, UiRect row, TuningItem item, Span<char> text, float labelW)
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
                var label = row.SplitLeft(labelW, out var rest);
                var value = rest.SplitRight(ValueWidth * s, out var slider);
                float current = f.Get();
                ui.Label(label, f.Label, current != f.Default ? t.Text : t.TextDim, UiAlign.Left, UiFace.Body);
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
                ui.InfoRow(row, info.Label, text[..n], labelW / row.Width, labelSize: t.FontSize);
                break;
            }
        }
    }
}
