using System.Globalization;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Game.Client;

/// <summary>
/// Снимок кадра для HUD: числа кадра клиента и рендера (<c>HudData</c> SWORD 2eab13b); <paramref name="Profile"/> — сводка
/// профайлера для F2 (точка Пв: главные зоны кадра, последняя аномалия), null — профайлера нет.
/// </summary>
public readonly record struct HudData(FrameStats Frame, int DrawCalls, int UiQuads, string Adapter, ProfileDigest? Profile = null);

/// <summary>Строка на стеке: форматирование без выделений в кадре (<c>Line</c> SWORD).</summary>
public ref struct Line
{
    private readonly Span<char> _buf;
    private int _n;

    public Line(Span<char> buf)
    {
        _buf = buf;
        _n = 0;
    }

    public Line S(ReadOnlySpan<char> s)
    {
        s.CopyTo(_buf[_n..]);
        _n += s.Length;
        return this;
    }

    public Line F(double v, string fmt = "F1")
    {
        v.TryFormat(_buf[_n..], out int w, fmt, CultureInfo.InvariantCulture);
        _n += w;
        return this;
    }

    public Line I(long v)
    {
        v.TryFormat(_buf[_n..], out int w, default, CultureInfo.InvariantCulture);
        _n += w;
        return this;
    }

    public readonly ReadOnlySpan<char> Text => _buf[.._n];
}

/// <summary>
/// HUD арены (<c>Render/Hud.cs</c> SWORD 2eab13b): слева вверху — панель производительности (FPS цветом по порогам 59/45,
/// шаг кадра, худший за окно, полоса бюджета 60 Гц, секции «КАДР» и «GPU», видеокарта), справа вверху — справка по
/// клавишам. Размеры — логические пиксели SWORD, масштаб — по высоте окна (<see cref="Scale"/>). Рисует списком квадов,
/// без выделений в кадре.
///
/// Точка Пг: полоса бюджета — CPU + GPU (как SWORD), секция «GPU» — время кадра на GPU и сумма его проходов (одна очередь — вместо
/// «compute» и «графика» SWORD), вызовы рисования и квады UI; GPU-время — последнего разобранного кадра (на 3 кадра раньше —
/// кадры в полёте). Отличия от SWORD: нет секции «ФИЗИКА И БОЙ», полосы здоровья, экрана «ПАЛ» и ручки времени — со своими
/// фичами. F2 (точка Пв) — как «ПРОФАЙЛЕР, мс» SWORD внизу панели: зоны CPU (своё время, мс на кадр) и под ними (Пг) проходы GPU
/// (мс на кадр, как в SWORD); ниже — строка среды (Пб) и последняя аномалия. В бенче F2 по умолчанию скрыт (в SWORD — показан):
/// F2 включает профайлер, а бенч без <c>--profile</c> меряется без него.
/// </summary>
public sealed class Hud
{
    // Цвета — только из общей темы (UiTheme.Default), здесь лишь короткие имена.
    private static UiTheme T => UiTheme.Default;
    private static uint Panel => T.Overlay;
    private static uint TextC => T.Text;
    private static uint Dim => T.TextDim;
    private static uint Accent => T.Accent;
    private static uint Good => T.Good;
    private static uint Bad => T.Bad;
    private static uint CpuC => T.Cpu;
    private static uint GpuC => T.Gpu;
    private static uint RuleC => T.Rule;

    /// <summary>Пользовательский множитель размера интерфейса SWORD (<c>SWORD_UI_SCALE</c> по умолчанию 0,7).</summary>
    public const float UserScale = 0.7f;

    private const float PerfW = 340, PerfPad = 14, RowH = 19, TitleH = 22, CellW = (PerfW - 2 * PerfPad - 16) / 2;

    /// <summary>Справка (F1).</summary>
    public bool ShowHelp { get; set; } = true;

    /// <summary>Профайлер (F2, точка Пв): главные зоны кадра, строка среды, последняя аномалия — секция внизу панели.</summary>
    public bool ShowProfiler { get; set; }

    /// <summary>Масштаб интерфейса по высоте окна: <c>clamp(h / 1080, 0,75, 2) · UserScale</c>.</summary>
    public static float Scale(int h) => Math.Clamp(h / 1080f, 0.75f, 2f) * UserScale;

    /// <summary>FPS цветом: от 59 — хорошо, от 45 — акцент, ниже — плохо.</summary>
    public static uint FpsColour(double fps) => fps >= 59 ? Good : fps >= 45 ? Accent : Bad;

    /// <summary>HUD кадра: панель производительности и справка (<paramref name="help"/> — строки справа сверху).</summary>
    public void Draw(UiDrawList d, int w, int h, in HudData data, IReadOnlyList<string> help)
    {
        DrawPerf(d, h, data, ShowProfiler);
        if (ShowHelp) DrawHelp(d, w, h, help);
    }

    /// <summary>Заголовок секции: мелкая подпись и тонкая линия до края панели.</summary>
    private static float Section(UiDrawList d, float s, float x, float y, string title)
    {
        y += 6 * s;
        float tw = d.Text(UiFace.Label, 11 * s, new(x, y), Dim, title);
        d.Rect(x + tw + 8 * s, y + 7 * s, (PerfW - PerfPad) * s, y + 8 * s, RuleC);
        return y + (TitleH - 6) * s;
    }

    /// <summary>Ячейка сетки: метка слева, значение выровнено по правому краю ячейки. col = 0 или 1.</summary>
    private static void Cell(UiDrawList d, float s, float x, float y, int col, string label, ReadOnlySpan<char> value, uint colour)
    {
        float cx = x + col * (CellW + 16) * s;
        d.Text(UiFace.Body, 13 * s, new(cx, y + 1 * s), Dim, label);
        d.TextRight(UiFace.Num, 14 * s, new(cx + CellW * s, y), colour, value);
    }

    /// <summary>Строк секции профайлера сверх строк зон: строка среды и две строки последней аномалии.</summary>
    private const int ProfilerExtraRows = 3;

    /// <summary>Строка секции профайлера — ниже обычной (<c>lh · 0,9</c> SWORD).</summary>
    private const float ProfilerRowScale = 0.9f;

    /// <summary>Панель производительности (<c>Hud.DrawPerf</c> SWORD). Возвращает нижний край.</summary>
    private static float DrawPerf(UiDrawList d, int h, in HudData data, bool showProfiler)
    {
        Span<char> buf = stackalloc char[96];
        var f = data.Frame;
        float s = Scale(h);
        float x = (8 + PerfPad) * s, lh = RowH * s;
        var digest = data.Profile;
        int zoneRows = showProfiler ? digest is { Ready: true } ? digest.Count + GpuRows(digest) : 1 : 0;
        // SWORD: PerfPad + 30 + 16 + 3·TitleH + (4 + строк секции арены)·RowH + 20 + (F2: TitleH + n·RowH·0,9) + PerfPad — у нас две
        // секции и четыре строки («КАДР» и «GPU» — по две, как в SWORD); F2 — зоны CPU, проходы GPU (Пг), строка среды и последняя аномалия.
        float height = PerfPad + 30 + 16 + 2 * TitleH + 4 * RowH + 20 + PerfPad
            + (showProfiler ? TitleH + (zoneRows + ProfilerExtraRows) * RowH * ProfilerRowScale : 0);
        d.Rect(8 * s, 8 * s, (8 + PerfW) * s, (8 + height) * s, Panel, 8 * s);
        float y = (8 + PerfPad) * s;
        float right = (PerfW - PerfPad + 8) * s;

        // FPS и время кадра
        float fw = d.Text(UiFace.Head, 26 * s, new(x, y - 3 * s), FpsColour(f.Fps), new Line(buf).F(f.Fps, "F0").Text);
        d.Text(UiFace.Label, 12 * s, new(x + fw + 5 * s, y + 9 * s), Dim, "FPS");
        d.TextRight(UiFace.Num, 15 * s, new(right, y + 1 * s), TextC, new Line(buf).F(f.FrameMs, "F2").S(" мс").Text);
        d.TextRight(UiFace.Body, 12 * s, new(right, y + 17 * s), Dim, new Line(buf).S("макс ").F(f.FrameMsMax, "F1").S(" мс").Text);
        y += 30 * s;

        // полоса бюджета 60 Гц: CPU + GPU (SWORD; GPU — последний разобранный кадр, точка Пг)
        const double budget = 1000.0 / 60.0;
        float bx0 = x, bx1 = right, bw = bx1 - bx0, bh = 6 * s;
        d.Rect(bx0, y, bx1, y + bh, T.Track, 3 * s);
        float cpu = (float)Math.Min(f.CpuMs / budget, 1) * bw;
        float gpu = (float)Math.Min(f.GpuMs / budget, 1) * bw;
        d.Rect(bx0, y, bx0 + cpu, y + bh, CpuC, 3 * s);
        d.Rect(bx0 + cpu, y, MathF.Min(bx0 + cpu + gpu, bx1), y + bh, GpuC, 3 * s);
        y += 16 * s;

        y = Section(d, s, x, y, "КАДР");
        Cell(d, s, x, y, 0, "CPU", new Line(buf).F(f.CpuMs, "F2").S(" мс").Text, CpuC);
        Cell(d, s, x, y, 1, "ожидание", new Line(buf).F(f.WaitMs, "F2").S(" мс").Text, TextC);
        y += lh;
        Cell(d, s, x, y, 0, "тиков сим.", new Line(buf).I(f.Ticks).Text, TextC);
        Cell(d, s, x, y, 1, "vsync", f.Vsync ? "вкл" : "выкл", f.Vsync ? Accent : TextC);
        y += lh;

        y = Section(d, s, x, y, "GPU");
        // Кадр целиком (проходы, барьеры между ними) и сумма проходов — одна очередь (в SWORD — compute и графика).
        Cell(d, s, x, y, 0, "кадр", new Line(buf).F(f.GpuMs, "F2").S(" мс").Text, GpuC);
        Cell(d, s, x, y, 1, "проходы", new Line(buf).F(f.GpuPassesMs, "F2").S(" мс").Text, GpuC);
        y += lh;
        Cell(d, s, x, y, 0, "draw calls", new Line(buf).I(data.DrawCalls).Text, TextC);
        Cell(d, s, x, y, 1, "UI квадов", new Line(buf).I(data.UiQuads).Text, TextC);
        y += lh;

        d.Text(UiFace.Body, 11 * s, new(x, y + 4 * s), UiDrawList.WithAlpha(Dim, 0.7f), data.Adapter);
        y += 20 * s;
        if (showProfiler) y = DrawProfiler(d, s, x, y, right, buf, f, digest);
        return y;
    }

    /// <summary>
    /// Секция F2 (точка Пв; «ПРОФАЙЛЕР, мс» SWORD — там проходы GPU): главные зоны кадра по своему времени за прошлые 0,25 с
    /// (мс на кадр, полоса — доля кадра), строка среды (Пб: перенесена сюда — в основной панели её нет, как в SWORD), последняя
    /// аномалия — период, длительность, класс; главная зона и её лишнее время. Точка Пг: под зонами CPU — «GPU кадр» и главные
    /// проходы GPU (мс на кадр за то же окно).
    /// </summary>
    private static float DrawProfiler(UiDrawList d, float s, float x, float y, float right, Span<char> buf, in FrameStats f, ProfileDigest? digest)
    {
        float rh = RowH * ProfilerRowScale * s, width = right - x;
        y = Section(d, s, x, y, "ПРОФАЙЛЕР, мс");
        if (digest is not { Ready: true })
        {
            string state = digest is null ? "профайлера нет" : digest.Profiler.Enabled ? "профайлер: собираю кадры…" : "профайлер включается…";
            d.Text(UiFace.Body, 12 * s, new(x, y), Dim, state);
            y += rh;
        }
        else
        {
            double frame = Math.Max(digest.FrameMs, 1e-6);
            for (int i = 0; i < digest.Count; i++)
            {
                double ms = digest.Ms(i);
                float bar = (float)Math.Min(ms / frame, 1) * width;
                d.Rect(x, y + 13 * s, right, y + 14.5f * s, T.TrackFaint, 1 * s);
                d.Rect(x, y + 13 * s, x + bar, y + 14.5f * s, UiDrawList.WithAlpha(Accent, 0.8f), 1 * s);
                float vw = d.TextRight(UiFace.Num, 12 * s, new(right, y), TextC, new Line(buf).F(ms, "F3").Text);
                string name = digest.Zone(i) == CpuProfiler.FrameZone ? FrameSelfName : digest.Name(i);
                d.TextFit(UiFace.Num, 12 * s, new(x, y), Dim, name, width - vw - 8 * s);
                y += rh;
            }
            if (GpuRows(digest) > 0)
            {
                // Точка Пг: проходы GPU (мс на кадр за то же окно; кадры — на 3 раньше: кадры в полёте), полоса — доля GPU-кадра.
                double gpuFrame = Math.Max(digest.GpuFrameMs, 1e-6);
                d.TextRight(UiFace.Num, 12 * s, new(right, y), GpuC, new Line(buf).F(digest.GpuFrameMs, "F3").Text);
                d.Text(UiFace.Body, 12 * s, new(x, y), GpuC, "GPU кадр");
                y += rh;
                for (int i = 0; i < digest.GpuCount; i++)
                {
                    double ms = digest.GpuMs(i);
                    float bar = (float)Math.Min(ms / gpuFrame, 1) * width;
                    d.Rect(x, y + 13 * s, right, y + 14.5f * s, T.TrackFaint, 1 * s);
                    d.Rect(x, y + 13 * s, x + bar, y + 14.5f * s, UiDrawList.WithAlpha(GpuC, 0.8f), 1 * s);
                    float vw = d.TextRight(UiFace.Num, 12 * s, new(right, y), TextC, new Line(buf).F(ms, "F3").Text);
                    d.TextFit(UiFace.Num, 12 * s, new(x + 10 * s, y), Dim, digest.GpuName(i), width - vw - 18 * s);
                    y += rh;
                }
            }
        }
        // Среда за прошлую секунду (точка Пб): методов JIT, сборок GC и их пауза, загрузка CPU другими процессами.
        d.Text(UiFace.Body, 12 * s, new(x, y), RuntimeColour(f.Runtime), RuntimeText(new Line(buf), f.Runtime));
        y += rh;
        if (digest?.Profiler.Anomalies is { } anomalies && anomalies.TryGetLast(out var last))
        {
            d.Text(UiFace.Body, 12 * s, new(x, y), AnomalyColour(last.Class), AnomalyText(new Line(buf), last));
            y += rh;
            // После Пв: справа — заметных за прогон из всех (заметная — кадр не короче интервала показа монитора).
            float cw = d.TextRight(UiFace.Body, 12 * s, new(right, y), Dim, NoticeableText(new Line(buf), anomalies.NoticeableCount, anomalies.Detected));
            // Исправления после Пд: главной зоны нет (абсолютный порог на обычном кадре) — так и написано, без случайной зоны «+0».
            var zone = last.HasZone
                ? new Line(buf).S(last.ZoneName.AsSpan(0, Math.Min(last.ZoneName.Length, 64))).S(" +").F(last.ZoneExcessMs, "F2").S(" мс").Text
                : new Line(buf).S(ProfileAnomaly.NoZoneText).Text;
            d.TextFit(UiFace.Num, 12 * s, new(x, y), Dim, zone, width - cw - 8 * s);
            y += rh;
        }
        else
        {
            d.Text(UiFace.Body, 12 * s, new(x, y), Dim, digest?.Profiler.Anomalies is null ? "аномалии не ловятся" : "аномалий нет");
            y += 2 * rh;
        }
        return y;
    }

    /// <summary>Строк проходов GPU в F2 (точка Пг): строка «GPU кадр» и проходы; времён GPU нет — ни одной.</summary>
    private static int GpuRows(ProfileDigest digest) => digest.GpuFrameMs > 0 ? 1 + digest.GpuCount : 0;

    /// <summary>Своё время корня кадра — вне всех зон (строка F2).</summary>
    public const string FrameSelfName = "frame (вне зон)";

    /// <summary>
    /// Первая строка аномалии F2: «аномалия: кадр 1234 · 8.61 мс · JIT» (тик в кадре — «тик 383 в кадре 1234»); заметная (после Пв:
    /// кадр не короче интервала показа монитора) — ещё «· заметная».
    /// </summary>
    public static ReadOnlySpan<char> AnomalyText(Line line, in ProfileAnomaly a)
    {
        line = line.S("аномалия: ");
        if (a.TickTrigger && a.Tick is { } tick) line = line.S("тик ").I((long)tick).S(" в кадре ").I((long)a.Number);
        else line = line.S(a.Period == ProfilePeriod.Frame ? "кадр " : "тик ").I((long)a.Number);
        line = line.S(RuntimeSeparator).F(a.Ms, "F2").S(" мс").S(RuntimeSeparator).S(AnomalyCauses.Word(a.Class));
        return a.Noticeable ? line.S(RuntimeSeparator).S(ProfileAnomaly.NoticeableWord).Text : line.Text;
    }

    /// <summary>Счёт заметных F2 (после Пв): «заметных 3 из 12» — за прогон.</summary>
    public static ReadOnlySpan<char> NoticeableText(Line line, long noticeable, long detected) =>
        line.S("заметных ").I(noticeable).S(" из ").I(detected).Text;

    /// <summary>Цвет аномалии: машина — плохо (не код игры), остальное — акцент.</summary>
    public static uint AnomalyColour(AnomalyClass c) => c == AnomalyClass.Machine ? Bad : Accent;

    /// <summary>Разделитель строки среды.</summary>
    public const string RuntimeSeparator = " · ";

    /// <summary>
    /// Строка среды HUD (точка Пб): <c>JIT n · GC n/Y мс · фон CPU X %</c> — за прошлую секунду: методов JIT процесса,
    /// сборок GC и их пауза, загрузка CPU другими процессами (доля всех логических процессоров). До первой секунды — прочерки.
    /// </summary>
    public static ReadOnlySpan<char> RuntimeText(Line line, in RuntimeLine r)
    {
        if (!r.Ready) return line.S("JIT —").S(RuntimeSeparator).S("GC —").S(RuntimeSeparator).S("фон CPU —").Text;
        return line.S("JIT ").I(r.Jit).S(RuntimeSeparator).S("GC ").I(r.Gc).S("/").F(r.PauseMs, "F1").S(" мс")
            .S(RuntimeSeparator).S("фон CPU ").F(r.OthersBusy * 100, "F0").S(" %").Text;
    }

    /// <summary>Цвет строки среды: машина занята (другие процессы от 25 %) — плохо, были JIT или GC — акцент, иначе — тускло.</summary>
    public static uint RuntimeColour(in RuntimeLine r) =>
        !r.Ready ? Dim : r.OthersBusy >= MachineReport.OthersBusyLimit ? Bad : r.Jit > 0 || r.Gc > 0 ? Accent : Dim;

    /// <summary>Справка справа сверху (<c>Hud.DrawHelp</c> SWORD): строки выровнены вправо на общей панели.</summary>
    private static void DrawHelp(UiDrawList d, int w, int h, IReadOnlyList<string> help)
    {
        if (help.Count == 0) return;
        float s = Scale(h), lh = 20 * s, fs = 14 * s, pad = 8 * s;
        float hx = w - 16 * s, hy = 14 * s;
        float widest = 0;
        for (int i = 0; i < help.Count; i++) widest = MathF.Max(widest, d.Measure(UiFace.Body, fs, help[i]));
        d.Rect(hx - widest - pad, 8 * s, w - 8 * s, hy + help.Count * lh + pad, Panel, 8 * s);
        for (int i = 0; i < help.Count; i++)
        {
            d.TextRight(UiFace.Body, fs, new(hx, hy), Dim, help[i]);
            hy += lh;
        }
    }

    /// <summary>
    /// Строки справки по привязкам (действие → клавиша, как в конфиге): справка показывает клавиши, которые на самом
    /// деле работают. Кнопки мыши — ЛКМ/ПКМ/СКМ, без привязки — «—».
    /// </summary>
    public static IReadOnlyList<string> HelpLines(IReadOnlyDictionary<string, string> keys)
    {
        string K(InputAction a) => keys.TryGetValue(a.Name, out var k) ? Show(k) : Show(a.DefaultKey);
        return
        [
            $"курсор — поворот    {K(ArenaInput.MoveForward)} {K(ArenaInput.MoveLeft)} {K(ArenaInput.MoveBack)} {K(ArenaInput.MoveRight)} — движение ({K(ArenaInput.Walk)} — шагом)",
            $"колесо, {K(ArenaInput.CameraZoomIn)} {K(ArenaInput.CameraZoomOut)} — зум    {K(ArenaInput.CameraRotateLeft)} {K(ArenaInput.CameraRotateRight)} / {K(ArenaInput.CameraOrbit)} — камера    {K(ArenaInput.CameraFollow)} — свободная",
            $"{K(ArenaInput.HudHelp)} — справка    {K(ArenaInput.HudProfiler)} — профайлер    {K(ClientHost.Screenshot)} — снимок    Esc — выход",
            $"{K(ArenaInput.EnemiesMore)} / {K(ArenaInput.EnemiesFewer)} — врагов ±1    {K(ArenaInput.ArenaReset)} — сброс арены    {K(ArenaInput.NextWeapon)} — следующий меч    {K(ArenaInput.DebugBodies)} — капсулы и клинки",
            $"{K(ArenaInput.Attack)} — удар (жест мышью перед кликом — направление)    {K(ArenaInput.Stab)} — укол    {K(ArenaInput.Dash)} — рывок",
        ];
    }

    private static string Show(string key) => key switch
    {
        "MouseLeft" => "ЛКМ",
        "MouseRight" => "ПКМ",
        "MouseMiddle" => "СКМ",
        "Space" => "Пробел",
        "Plus" => "+",
        "Minus" => "−",
        "PageUp" => "PgUp",
        "PageDown" => "PgDn",
        "None" => "—",
        _ => key,
    };
}
