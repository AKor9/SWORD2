using System.Globalization;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Game.Client;

/// <summary>
/// Снимок кадра для HUD: числа кадра клиента и рендера; <paramref name="Profile"/> — сводка профайлера для F2 (null — нет);
/// <paramref name="Combat"/> — бой (null — HUD без полосы здоровья и секции «ФИЗИКА И БОЙ»).
/// </summary>
public readonly record struct HudData(FrameStats Frame, int DrawCalls, int UiQuads, string Adapter, ProfileDigest? Profile = null,
    CombatHud? Combat = null);

/// <summary>
/// Бой для HUD — снимок мира последнего тика кадра. Последний удар: часть — сегмент (−1 — не было), сбрасывается по R;
/// меч — имя таблицы (null — нет); открытый инвентарь рисует панель, подпись в углу — только у закрытого.
/// </summary>
public readonly record struct CombatHud(float PlayerHp, bool PlayerDead, int AliveEnemies, int Enemies, int Kills, int Hits,
    int LastPart, float LastDamage, string? Weapon = null, bool InventoryOpen = false);

/// <summary>Строка на стеке: форматирование без выделений в кадре.</summary>
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
/// HUD арены: слева вверху — панель производительности (FPS цветом по порогам 59/45, полоса бюджета 60 Гц — CPU + GPU,
/// секции «КАДР» и «GPU», с боем — «ФИЗИКА И БОЙ»; F2 — зоны CPU и проходы GPU), справа вверху — справка; полоса здоровья
/// и последний удар внизу, экран «ПАЛ». GPU-время — последнего разобранного кадра (на 3 кадра раньше — кадры в полёте).
/// Масштаб — по высоте окна (<see cref="Scale"/>); рисует списком квадов без выделений в кадре.
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

    /// <summary>Пользовательский множитель размера интерфейса.</summary>
    public const float UserScale = 0.7f;

    private const float PerfW = 340, PerfPad = 14, RowH = 19, TitleH = 22, CellW = (PerfW - 2 * PerfPad - 16) / 2;

    /// <summary>Справка (F1).</summary>
    public bool ShowHelp { get; set; } = true;

    /// <summary>Профайлер (F2): главные зоны кадра и проходы GPU — секция внизу панели.</summary>
    public bool ShowProfiler { get; set; }

    /// <summary>Масштаб интерфейса по высоте окна: <c>clamp(h / 1080, 0,75, 2) · UserScale</c>.</summary>
    public static float Scale(int h) => Math.Clamp(h / 1080f, 0.75f, 2f) * UserScale;

    /// <summary>FPS цветом: от 59 — хорошо, от 45 — акцент, ниже — плохо.</summary>
    public static uint FpsColour(double fps) => fps >= 59 ? Good : fps >= 45 ? Accent : Bad;

    /// <summary>Нижний край панели производительности в последнем кадре, пиксели (под ней — панель ручек F3).</summary>
    public float PerfBottom { get; private set; }

    /// <summary>Клавиша инвентаря в подписи закрытого инвентаря (по привязке).</summary>
    public string InventoryKey { get; set; } = "I";

    /// <summary>Клавиша сброса арены на экране «ПАЛ» (по привязке).</summary>
    public string ResetKey { get; set; } = "R";

    /// <summary>
    /// HUD кадра: панель производительности, полоса здоровья и последний удар, подпись меча, справка
    /// (<paramref name="help"/> — строки справа сверху), экран «ПАЛ».
    /// </summary>
    public void Draw(UiDrawList d, int w, int h, in HudData data, IReadOnlyList<string> help)
    {
        PerfBottom = DrawPerf(d, h, data, ShowProfiler);
        if (data.Combat is { } combat) DrawCombat(d, w, h, combat);
        if (ShowHelp) DrawHelp(d, w, h, help);
        if (data.Combat is { PlayerDead: true } dead) Fallen(d, w, h, dead.Kills, ResetKey);
    }

    /// <summary>Строк секции «ФИЗИКА И БОЙ»: враги и убито, попаданий.</summary>
    private const int CombatRows = 2;

    /// <summary>Название части тела в последнем ударе (без стороны).</summary>
    public static string PartName(int segment) => segment switch
    {
        Contracts.BodySegs.Head => "голова",
        Contracts.BodySegs.Torso => "торс",
        Contracts.BodySegs.Pelvis => "таз",
        Contracts.BodySegs.UArmL or Contracts.BodySegs.UArmR => "плечо",
        Contracts.BodySegs.FArmL or Contracts.BodySegs.FArmR => "предплечье",
        Contracts.BodySegs.ThighL or Contracts.BodySegs.ThighR => "бедро",
        Contracts.BodySegs.ShinL or Contracts.BodySegs.ShinR => "голень",
        Contracts.BodySegs.FootL or Contracts.BodySegs.FootR => "стопа",
        _ => "клинок",
    };

    /// <summary>Последний удар игрока: «голова 61»; удара не было — пусто.</summary>
    public static ReadOnlySpan<char> LastHitText(Line line, in CombatHud c) =>
        c.LastPart < 0 ? default : line.S(PartName(c.LastPart)).S(" ").F(MathF.Max(c.LastDamage, 0), "F0").Text;

    /// <summary>Подпись закрытого инвентаря: «оружие: sword_1h    I — инвентарь».</summary>
    public static ReadOnlySpan<char> WeaponText(Line line, string weapon, string key) =>
        line.S("оружие: ").S(weapon).S("    ").S(key).S(" — инвентарь").Text;

    /// <summary>Строка экрана «ПАЛ»: «убито 3  ·  R — снова в бой».</summary>
    public static ReadOnlySpan<char> FallenText(Line line, int kills, string key) =>
        line.S("убито ").I(kills).S("  ·  ").S(key).S(" — снова в бой").Text;

    /// <summary>Полоса здоровья внизу по центру, над ней — последний удар, справа внизу — меч.</summary>
    private void DrawCombat(UiDrawList d, int w, int h, in CombatHud c)
    {
        Span<char> buf = stackalloc char[128];
        float s = Scale(h);
        float bw = 420 * s, bh = 18 * s;
        float bx = (w - bw) * 0.5f, by = h - 60 * s;
        d.Rect(bx - 10 * s, by - 34 * s, bx + bw + 10 * s, by + bh + 12 * s, Panel, 10 * s);
        d.Rect(bx, by, bx + bw, by + bh, T.HealthTrack, 4 * s);
        d.RectGradient(bx, by, bx + bw * Math.Clamp(c.PlayerHp / Contracts.Health.Full, 0, 1), by + bh, T.HealthHigh, T.HealthLow, 4 * s);
        d.TextCentered(UiFace.Num, 13 * s, new(w * 0.5f, by + 1 * s), TextC, new Line(buf).S("HP ").F(MathF.Max(c.PlayerHp, 0), "F0").Text);
        var last = LastHitText(new Line(buf), c);
        if (!last.IsEmpty) d.TextCentered(UiFace.Label, 18 * s, new(w * 0.5f, by - 64 * s), TextC, last);
        if (c.Weapon is { } weapon && !c.InventoryOpen)
        {
            // Полоса 24 пикселя у низа экрана с отступом 16, подпись — справа, по центру полосы.
            float fs = T.FontSize * s, y0 = h - 16 * s - 24 * s;
            float y = y0 + (24 * s - d.LineHeight(UiFace.Label, fs)) * 0.5f;
            d.TextRight(UiFace.Label, fs, new(w - 16 * s, y), Dim, WeaponText(new Line(buf), weapon, InventoryKey));
        }
    }

    /// <summary>Экран «ПАЛ»: затемнение, «ПАЛ», убито и клавиша сброса.</summary>
    private static void Fallen(UiDrawList d, int w, int h, int kills, string key)
    {
        Span<char> buf = stackalloc char[64];
        float s = Scale(h);
        d.Rect(0, 0, w, h, T.DeathTint);
        d.TextCentered(UiFace.Head, 64 * s, new(w * 0.5f, h * 0.38f), Bad, "ПАЛ");
        d.TextCentered(UiFace.Body, 22 * s, new(w * 0.5f, h * 0.38f + 80 * s), TextC, FallenText(new Line(buf), kills, key));
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

    /// <summary>Строка секции профайлера — ниже обычной.</summary>
    private const float ProfilerRowScale = 0.9f;

    /// <summary>Панель производительности. Возвращает нижний край панели, пиксели.</summary>
    private static float DrawPerf(UiDrawList d, int h, in HudData data, bool showProfiler)
    {
        Span<char> buf = stackalloc char[96];
        var f = data.Frame;
        float s = Scale(h);
        float x = (8 + PerfPad) * s, lh = RowH * s;
        var digest = data.Profile;
        int zoneRows = showProfiler ? digest is { Ready: true } ? digest.Count + GpuRows(digest) : 1 : 0;
        // Секции «КАДР» и «GPU» по две строки, с боем — «ФИЗИКА И БОЙ»; F2 — зоны CPU и проходы GPU.
        bool combat = data.Combat is not null;
        float height = PerfPad + 30 + 16 + (combat ? 3 : 2) * TitleH + (4 + (combat ? CombatRows : 0)) * RowH + 20 + PerfPad
            + (showProfiler ? TitleH + zoneRows * RowH * ProfilerRowScale : 0);
        d.Rect(8 * s, 8 * s, (8 + PerfW) * s, (8 + height) * s, Panel, 8 * s);
        float y = (8 + PerfPad) * s;
        float right = (PerfW - PerfPad + 8) * s;

        // FPS и время кадра
        float fw = d.Text(UiFace.Head, 26 * s, new(x, y - 3 * s), FpsColour(f.Fps), new Line(buf).F(f.Fps, "F0").Text);
        d.Text(UiFace.Label, 12 * s, new(x + fw + 5 * s, y + 9 * s), Dim, "FPS");
        d.TextRight(UiFace.Num, 15 * s, new(right, y + 1 * s), TextC, new Line(buf).F(f.FrameMs, "F2").S(" мс").Text);
        d.TextRight(UiFace.Body, 12 * s, new(right, y + 17 * s), Dim, new Line(buf).S("макс ").F(f.FrameMsMax, "F1").S(" мс").Text);
        y += 30 * s;

        // полоса бюджета 60 Гц: CPU + GPU (GPU — последний разобранный кадр)
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
        // Кадр целиком (проходы, барьеры между ними) и сумма проходов — одна очередь.
        Cell(d, s, x, y, 0, "кадр", new Line(buf).F(f.GpuMs, "F2").S(" мс").Text, GpuC);
        Cell(d, s, x, y, 1, "проходы", new Line(buf).F(f.GpuPassesMs, "F2").S(" мс").Text, GpuC);
        y += lh;
        Cell(d, s, x, y, 0, "draw calls", new Line(buf).I(data.DrawCalls).Text, TextC);
        Cell(d, s, x, y, 1, "UI квадов", new Line(buf).I(data.UiQuads).Text, TextC);
        y += lh;

        if (data.Combat is { } c)
        {
            // Секция арены — враги (живых / всего), убито, попаданий.
            y = Section(d, s, x, y, "ФИЗИКА И БОЙ");
            Cell(d, s, x, y, 0, "враги", new Line(buf).I(c.AliveEnemies).S(" / ").I(c.Enemies).Text, TextC);
            Cell(d, s, x, y, 1, "убито", new Line(buf).I(c.Kills).Text, TextC);
            y += lh;
            Cell(d, s, x, y, 0, "попаданий", new Line(buf).I(c.Hits).Text, TextC);
            y += lh;
        }

        d.Text(UiFace.Body, 11 * s, new(x, y + 4 * s), UiDrawList.WithAlpha(Dim, 0.7f), data.Adapter);
        y += 20 * s;
        if (showProfiler) DrawProfiler(d, s, x, y, right, buf, digest);
        return (8 + height) * s;
    }

    /// <summary>
    /// Секция F2: главные зоны кадра по своему времени за прошлые 0,25 с (мс на кадр, полоса — доля кадра), под ними —
    /// «GPU кадр» и главные проходы GPU за то же окно.
    /// </summary>
    private static float DrawProfiler(UiDrawList d, float s, float x, float y, float right, Span<char> buf, ProfileDigest? digest)
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
                // Проходы GPU (мс на кадр за то же окно; кадры — на 3 раньше: кадры в полёте), полоса — доля GPU-кадра.
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
        return y;
    }

    /// <summary>Строк проходов GPU в F2: строка «GPU кадр» и проходы; времён GPU нет — ни одной.</summary>
    private static int GpuRows(ProfileDigest digest) => digest.GpuFrameMs > 0 ? 1 + digest.GpuCount : 0;

    /// <summary>Своё время корня кадра — вне всех зон (строка F2).</summary>
    public const string FrameSelfName = "frame (вне зон)";

    /// <summary>Справка справа сверху: строки выровнены вправо на общей панели.</summary>
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
    /// Строки справки по привязкам (действие → клавиша, как в конфиге): справка показывает клавиши, которые на самом деле
    /// работают. Кнопки мыши — ЛКМ/ПКМ/СКМ, без привязки — «—».
    /// </summary>
    public static IReadOnlyList<string> HelpLines(IReadOnlyDictionary<string, string> keys)
    {
        string K(InputAction a) => keys.TryGetValue(a.Name, out var k) ? Show(k) : Show(a.DefaultKey);
        // Панель ручек (DevTools) — в справке, только когда её действие привязано (сборки без DevTools его нет); вкладки — «Бой»
        // и «Рэгдолл».
        string tuning = keys.TryGetValue(TuningAction, out var t) ? $"    {Show(t)} — ручки (бой, рэгдолл)" : "";
        return
        [
            $"курсор — поворот    {K(ArenaInput.MoveForward)} {K(ArenaInput.MoveLeft)} {K(ArenaInput.MoveBack)} {K(ArenaInput.MoveRight)} — движение ({K(ArenaInput.Walk)} — шагом)",
            $"колесо, {K(ArenaInput.CameraZoomIn)} {K(ArenaInput.CameraZoomOut)} — зум    {K(ArenaInput.CameraRotateLeft)} {K(ArenaInput.CameraRotateRight)} / {K(ArenaInput.CameraOrbit)} — камера    {K(ArenaInput.CameraFollow)} — свободная",
            // Суставы и контакты физики (отладочные оверлеи, как F7) — в строку клавиш представления: число строк справки прежнее.
            $"{K(ArenaInput.HudHelp)} — справка    {K(ArenaInput.HudProfiler)} — профайлер    {K(ClientHost.Screenshot)} — снимок    {K(ClientHost.VsyncToggle)} — vsync    Esc — выход    " +
            $"{K(ArenaInput.DebugJoints)} — суставы    {K(ArenaInput.DebugContacts)} — контакты физики",
            $"{K(ArenaInput.EnemiesMore)} / {K(ArenaInput.EnemiesFewer)} — врагов ±1    {K(ArenaInput.ArenaReset)} — сброс арены    {K(ArenaInput.NextWeapon)} — следующее оружие    {K(ArenaInput.DebugBodies)} — капсулы и клинки",
            // Траектории Motion Matching (оверлей) — в строку ударов: число строк справки прежнее.
            $"{K(ArenaInput.Attack)} — удар (жест мышью перед кликом — направление)    {K(ArenaInput.Stab)} — укол    {K(ArenaInput.Dash)} — рывок    " +
            $"{K(ArenaInput.DebugMotion)} — траектории MM",
            $"{K(ArenaInput.Inventory)} — инвентарь ({K(ArenaInput.Weapons[0])}–{K(ArenaInput.Weapons[^1])}, щелчок — взять меч){tuning}",
        ];
    }

    /// <summary>Имя действия панели ручек (F3, <c>Engine.DevTools</c>; привязка — секция <c>input</c>, её разрешает exe).</summary>
    public const string TuningAction = "tuning";

    /// <summary>Клавиша для людей (справка, подписи): кнопки мыши — ЛКМ/ПКМ/СКМ, цифры — без «D», без привязки — «—».</summary>
    public static string Show(string key) => key switch
    {
        ['D', >= '0' and <= '9'] => key[1..],
        _ => ShowName(key),
    };

    private static string ShowName(string key) => key switch
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
