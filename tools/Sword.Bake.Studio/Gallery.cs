using System.Numerics;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Витрина виджетов (<c>--widgets</c>, последний снимок <c>--gallery</c>) в раскладке Studio: панель инструментов, дерево
/// ассетов, свойства, лог, строка состояния. Данные — выдуманные: витрина проверяет виджеты, а не проект.
/// </summary>
internal sealed class Gallery(bool showcase)
{
    private float _left = 330, _log = 230;
    private int _frame, _tab, _config, _attachMode = 1, _treeRows = 1, _selectedLog = 12;
    private string _filter = "", _selected = "anim/1hm_sword";
    private string _title = "1HM Sword Attacks & Finishers", _origin = "Fab: 1HM_Sword_Attacks_Finishers, ретаргет UE4 → UE5 Manny (tools/ue/retarget_pack.py)";
    private string _anims = "${src}/_Export/skin_sword", _skeleton = "ue5_manny";
    private float _rate = 30, _scaleCm = 100;
    private bool _watch = true, _mirror, _fingers = true;

    private static readonly string[] Configs = ["Release", "Debug"];
    private static readonly string[] AttachModes = ["нет", "grip — по кулаку", "socket — сокет пака"];
    private static readonly string[] TabNames = ["Свойства", "Входы и выходы", "Зависимости"];

    private static readonly (string Folder, (string Name, int Status)[] Assets)[] Tree =
    [
        ("anim", [("1hm_sword", 0), ("actions", 0), ("gasp", 1), ("gasp_locomotion", 0), ("synth_locomotion", 2)]),
        ("fonts", [("hud", 0)]),
        ("meshes", [.. Enumerable.Range(1, 40).Select(i => ($"prop_{i:00}_очень_длинное_имя_ассета_для_проверки_многоточия", i % 7 == 0 ? 3 : i % 5 == 0 ? 1 : 0))]),
    ];

    /// <summary>Строки лога: уровень (0 — сведения, 1 — начало, 2 — готово, 3 — внимание, 4 — ошибка) и текст.</summary>
    private static readonly (int Level, string Text)[] LogSamples =
    [
        (1, "anim/1hm_sword — изменён D:\\Temp\\GamedevAssets\\_Export\\skin_sword\\clips\\1HM_Slash01.bin"),
        (0, "root motion: из корневой кости root 6 клипов, из таза 96; наибольший поворот 180° (1HM_Turn180)"),
        (0, "пальцы перенесены с исходных клипов: 102 из 102, отклонение фаланг от пака 47.4° → 0.0°"),
        (3, "ВНИМАНИЕ: отклонение велико — проверьте масштаб экспорта (ожидаются сантиметры)"),
        (2, "anim/1hm_sword за 1.1 с: выходов 107, 35463 КБ"),
        (4, "anim/synth_locomotion: нет исходника D:\\Temp\\GamedevAssets\\_Export\\synth\\manifest.json"),
    ];

    private uint StatusColour(UiContext ui, int status) => status switch
    {
        0 => ui.Theme.Good,
        1 => ui.Theme.Warn,
        2 => ui.Theme.Bad,
        _ => ui.Theme.TextFaint,
    };

    public void Draw(UiContext ui)
    {
        var t = ui.Theme;
        float pad = ui.S(8), rowH = ui.S(t.RowHeight);
        var area = ui.Screen;

        var toolbar = area.SplitTop(ui.S(46), out area);
        var status = area.SplitBottom(ui.S(28), out area);
        DrawToolbar(ui, toolbar.Shrink(pad, ui.S(7)));
        DrawStatus(ui, status);

        area = area.Shrink(pad, 0);
        _left = Math.Clamp(_left, ui.S(200), MathF.Max(ui.S(200), area.Width - ui.S(360)));
        var left = area.SplitLeft(_left, out var right);
        var splitter = right.SplitLeft(pad, out right);
        // разделитель двигает X-координату; ширина левой панели — от левого края области
        float leftEdge = area.X0 + _left;
        if (ui.Splitter(splitter, "split-left", ref leftEdge, vertical: true, area.X0 + ui.S(200), area.X1 - ui.S(360)))
            _left = leftEdge - area.X0;
        _log = Math.Clamp(_log, ui.S(80), MathF.Max(ui.S(80), right.Height - ui.S(200)));
        var log = right.SplitBottom(_log, out var centre);
        var hsplit = centre.SplitBottom(pad, out centre);
        float logTop = right.Y1 - _log;
        if (ui.Splitter(hsplit, "split-log", ref logTop, vertical: false, right.Y0 + ui.S(200), right.Y1 - ui.S(80)))
            _log = right.Y1 - logTop;

        DrawTree(ui, left);
        DrawProperties(ui, centre);
        DrawLog(ui, log);
        _frame++;
    }

    private void DrawToolbar(UiContext ui, UiRect r)
    {
        float gap = ui.S(6);
        if (ui.Button(r.SplitLeft(ui.S(120), out r), "Собрать  F7", primary: true)) _selectedLog = 4;
        r.SplitLeft(gap, out r);
        ui.Button(r.SplitLeft(ui.S(110), out r), "Пересобрать");
        r.SplitLeft(gap, out r);
        ui.Button(r.SplitLeft(ui.S(90), out r), "Очистить");
        r.SplitLeft(gap, out r);
        ui.Button(r.SplitLeft(ui.S(90), out r), "Отмена", enabled: false);
        r.SplitLeft(ui.S(16), out r);
        ui.Toggle(r.SplitLeft(ui.S(110), out r), "Следить", ref _watch);
        r.SplitLeft(gap, out r);
        if (showcase && _frame == 1) ui.OpenCombo("config");
        ui.Combo(r.SplitLeft(ui.S(130), out r), "config", ref _config, Configs);

        var progress = r.SplitRight(ui.S(260), out r);
        ui.ProgressBar(progress.Shrink(0, ui.S(8)), 0.62f, "4 из 6 ассетов");
        ui.Label(r.Shrink(ui.S(12), 0), "контент D:\\Work\\Visual Studio\\SWORD\\Content   ·   ${src} = D:\\Temp\\GamedevAssets",
            ui.Theme.TextDim, UiAlign.Right, UiFace.Body, ui.Theme.SmallFontSize);
    }

    private void DrawTree(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));
        var header = r.SplitTop(ui.S(24), out r);
        ui.SectionHeader(header, "АССЕТЫ");
        var filter = r.SplitTop(ui.S(28), out r);
        ui.TextField(filter, "filter", ref _filter, "фильтр: ID или часть имени");
        r.SplitTop(ui.S(6), out r);

        float rowH = ui.S(t.RowHeight);
        var content = ui.BeginScroll(r, "tree", _treeRows * rowH);
        int n = 0;
        UiRect Row() => new(content.X0, content.Y0 + rowH * n, content.X1, content.Y0 + rowH * ++n);
        foreach (var (folder, assets) in Tree)
        {
            var visible = assets.Where(a => _filter.Length == 0 || $"{folder}/{a.Name}".Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (visible.Count == 0) continue;
            bool open = ui.TreeNode(Row(), folder, $"{folder}   {visible.Count}", 0, leaf: false, selected: false, out _,
                defaultOpen: folder != "meshes" || showcase, textColour: t.Accent);
            if (!open) continue;
            foreach (var (name, st) in visible)
            {
                string id = $"{folder}/{name}";
                ui.TreeNode(Row(), id, name, 1, leaf: true, selected: _selected == id, out bool clicked, dot: StatusColour(ui, st));
                if (clicked) _selected = id;
                if (st == 2) ui.Tooltip(new UiRect(content.X0, content.Y0 + rowH * (n - 1), content.X1, content.Y0 + rowH * n), "нет исходника: _Export/synth/manifest.json");
            }
        }
        ui.EndScroll();
        _treeRows = n;
    }

    private void DrawProperties(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(10), ui.S(6));
        var title = r.SplitTop(ui.S(30), out r);
        float w = ui.Label(title, _selected, t.Text, UiAlign.Left, UiFace.Head, 18);
        ui.Label(title with { X0 = title.X0 + w + ui.S(12) }, "anim.pack · Источник анимаций (меш + клипы)", t.TextDim);
        var tabs = r.SplitTop(ui.S(30), out r);
        ui.Tabs(tabs, "tabs", ref _tab, TabNames);
        r.SplitTop(ui.S(8), out r);

        float rowH = ui.S(32), labelW = ui.S(150);
        var content = ui.BeginScroll(r, "props", rowH * 9);
        int n = 0;
        UiRect Row(out UiRect label)
        {
            var row = new UiRect(content.X0, content.Y0 + rowH * n, content.X1, content.Y0 + rowH * ++n).Shrink(0, ui.S(3));
            label = row.SplitLeft(labelW, out row);
            return row.SplitLeft(MathF.Min(row.Width, ui.S(520)), out _);
        }

        ui.PushId("props");
        var field = Row(out var label);
        ui.Label(label, "title", t.TextDim);
        if (showcase && _frame == 1) ui.Focus(ui.Id("title"));
        ui.TextField(field, "title", ref _title);

        field = Row(out label);
        ui.Label(label, "origin — откуда взято, очень длинная подпись", t.TextDim);
        ui.Tooltip(label, "Откуда взят источник: пак, проект, путь. Попадает в source.json.");
        ui.TextField(field, "origin", ref _origin);

        field = Row(out label);
        ui.Label(label, "anims", t.TextDim);
        ui.TextField(field, "anims", ref _anims, "${src}/…");

        field = Row(out label);
        ui.Label(label, "skeleton", t.TextDim);
        ui.TextField(field, "skeleton", ref _skeleton);

        field = Row(out label);
        ui.Label(label, "sampleRate, Гц", t.TextDim);
        ui.NumberField(field.SplitLeft(ui.S(120), out _), "rate", ref _rate, "0.##", 1, 1000);

        field = Row(out label);
        ui.Label(label, "масштаб, см", t.TextDim);
        ui.NumberField(field.SplitLeft(ui.S(120), out _), "scale", ref _scaleCm, "0.###", 0.001f, 10000);

        field = Row(out label);
        ui.Label(label, "attach", t.TextDim);
        ui.Combo(field.SplitLeft(ui.S(220), out _), "attach", ref _attachMode, AttachModes);

        field = Row(out label);
        ui.Toggle(field, "перенос пальцев из исходного скелета", ref _fingers);

        field = Row(out label);
        ui.Toggle(field, "зеркальные клипы", ref _mirror);
        ui.PopId();
        ui.EndScroll();
    }

    private void DrawLog(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));
        var header = r.SplitTop(ui.S(24), out r);
        ui.SectionHeader(header, "ЛОГ СБОРКИ");
        float rowH = ui.S(20);
        int selected = _selectedLog;
        ui.VirtualList(r, "log", 2000, rowH, (i, row) =>
        {
            var (level, line) = LogSamples[i % LogSamples.Length];
            if (ui.Selectable(row, ui.Id(i), i == selected)) _selectedLog = i;
            uint colour = level switch { 1 => t.Accent, 2 => t.Good, 3 => t.Warn, 4 => t.Bad, _ => t.TextDim };
            var time = row.SplitLeft(ui.S(70), out var text);
            ui.Label(time.Shrink(ui.S(6), 0), $"12:{i / 60 % 60:00}:{i % 60:00}", t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            var dot = text.SplitLeft(ui.S(16), out text);
            if (level > 0) ui.StatusDot(dot.Centre, colour, 3.5f);
            ui.Label(text, line, level == 0 ? t.TextDim : colour, UiAlign.Left, UiFace.Num, t.SmallFontSize);
        });
    }

    private static void DrawStatus(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Draw.Rect(r, t.Panel);
        r = r.Shrink(ui.S(10), ui.S(6));
        var bar = r.SplitRight(ui.S(180), out r);
        ui.ProgressBar(bar.Shrink(0, ui.S(3)), -1);
        ui.StatusDot(new Vector2(r.X0 + ui.S(5), r.Centre.Y), t.Warn);
        ui.Label(r with { X0 = r.X0 + ui.S(16) }, "устарело 2 ассета · anim/gasp: изменён manifest.json", t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
    }
}
