using System.Globalization;
using System.Numerics;
using Sword.Assets.Preview;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ui;

namespace Sword.Assets;

/// <summary>
/// Окно менеджера ресурсов. Вкладка «Ресурсы»: слева дерево <c>Content/</c> по темам с флажком «в пак» у файла и каталога,
/// справа предпросмотр выбранного (<see cref="PreviewPanel"/>). Вкладка «Пекарня»: сырой файл → конвертер → форма рецепта
/// (<see cref="RecipeEditor"/>) → «Испечь» → записанные файлы. Внизу лог, строка состояния — что попадёт в пак.
/// Клавиши: F7 — собрать пак, F5 — перечитать Content/.
/// </summary>
internal sealed class AssetsView(AssetsApp app, Func<PreviewImage, PreviewTexture> upload, int tab, int previewMode = 0, bool still = false) : IDisposable
{
    private static readonly string[] TabNames = ["Ресурсы", "Пекарня"];

    private readonly PreviewPanel _preview = new(upload, previewMode, still);
    private readonly RecipeEditor _editor = new();
    private readonly DirectorySource _content = new(app.Pack.ContentDir);
    private int _tab = Math.Clamp(tab, 0, TabNames.Length - 1);
    private float _left = 420, _log = 200;
    private string _sourceText = app.Source ?? "";

    private UiRect _rowArea;
    private float _rowHeight;
    private int _rowCount, _treeRows = 1;

    private readonly List<int> _logRows = [];
    private int _logVersionSeen = -1;

    public void Dispose() => _preview.Dispose();

    public void Draw(UiContext ui)
    {
        HandleKeys(ui);
        _editor.Sync(app);
        float pad = ui.S(8);
        var area = ui.Screen;
        var toolbar = area.SplitTop(ui.S(46), out area);
        var status = area.SplitBottom(ui.S(28), out area);
        DrawToolbar(ui, toolbar.Shrink(pad, ui.S(7)));
        DrawStatus(ui, status);

        area = area.Shrink(pad, 0);
        _log = Math.Clamp(_log, ui.S(90), MathF.Max(ui.S(90), area.Height - ui.S(260)));
        var log = area.SplitBottom(_log, out var main);
        var hsplit = main.SplitBottom(pad, out main);
        float logTop = area.Y1 - _log;
        if (ui.Splitter(hsplit, "split-log", ref logTop, vertical: false, area.Y0 + ui.S(260), area.Y1 - ui.S(90)))
            _log = area.Y1 - logTop;

        if (_tab == 0) DrawResources(ui, main);
        else DrawBaker(ui, main);
        DrawLog(ui, log);
    }

    private void HandleKeys(UiContext ui)
    {
        if (ui.WantsKeyboard) return;
        if (ui.Input.TakeKey(UiKey.F7) && !app.Busy) app.BuildPak();
        if (ui.Input.TakeKey(UiKey.F5) && !app.Busy) app.Rescan();
    }

    // ── панель инструментов и строка состояния ─────────────────────────

    private void DrawToolbar(UiContext ui, UiRect r)
    {
        ui.Tabs(r.SplitLeft(ui.S(260), out r), "main-tabs", ref _tab, TabNames);
        r.SplitLeft(ui.S(16), out r);
        var build = r.SplitLeft(ui.S(160), out r);
        if (ui.Button(build, "Собрать пак  F7", primary: true, enabled: !app.Busy)) app.BuildPak();
        ui.Tooltip(build, $"Сложить включённые файлы Content/ в {app.Pack.PakPath}; ошибка — прежний пак цел");
        r.SplitLeft(ui.S(6), out r);
        var rescan = r.SplitLeft(ui.S(130), out r);
        if (ui.Button(rescan, "Перечитать  F5", enabled: !app.Busy)) app.Rescan();
        ui.Tooltip(rescan, "Перечитать файлы Content/ (после выпечки из консоли)");

        var info = r.Shrink(ui.S(12), 0);
        if (app.Busy && info.Width >= ui.S(160))
        {
            var progress = info.SplitRight(MathF.Min(info.Width, ui.S(420)), out _);
            var (done, total) = app.Progress;
            ui.ProgressBar(progress.Shrink(0, ui.S(6)), total > 0 ? (float)done / total : -1,
                total > 0 ? $"{app.TaskTitle}: {done} из {total}" : app.TaskTitle);
        }
    }

    private void DrawStatus(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Draw.Rect(r, t.Panel);
        r = r.Shrink(ui.S(10), ui.S(5));
        var files = app.Pack.Files;
        int included = 0;
        long bytes = 0;
        foreach (var f in files)
            if (app.Pack.IsIncluded(f.Path))
            {
                included++;
                bytes += f.Size;
            }
        string text = string.Create(CultureInfo.InvariantCulture,
            $"в пак: файлов {included}, {bytes / 1048576.0:F1} МБ · выключено {files.Count - included} · Content/ — {app.Pack.ContentDir}");
        if (app.LastPack is { } p) text += string.Create(CultureInfo.InvariantCulture, $" · последний пак: {p.Files} файлов за {p.Elapsed.TotalSeconds:F1} с");
        ui.Label(r, text, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
    }

    // ── ресурсы ────────────────────────────────────────────────────────

    private void DrawResources(UiContext ui, UiRect area)
    {
        float pad = ui.S(8);
        _left = Math.Clamp(_left, ui.S(260), MathF.Max(ui.S(260), area.Width - ui.S(420)));
        var left = area.SplitLeft(_left, out var right);
        var vsplit = right.SplitLeft(pad, out right);
        float leftEdge = area.X0 + _left;
        if (ui.Splitter(vsplit, "split-left", ref leftEdge, vertical: true, area.X0 + ui.S(260), area.X1 - ui.S(420)))
            _left = leftEdge - area.X0;
        DrawTree(ui, left);
        DrawSelection(ui, right);
    }

    private UiRect Row() => new(_rowArea.X0, _rowArea.Y0 + _rowHeight * _rowCount, _rowArea.X1, _rowArea.Y0 + _rowHeight * ++_rowCount);

    private void DrawTree(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));
        ui.SectionHeader(r.SplitTop(ui.S(24), out r), "CONTENT/   В ПАК");
        _rowHeight = ui.S(t.RowHeight);
        _rowArea = ui.BeginScroll(r, "tree", _treeRows * _rowHeight);
        _rowCount = 0;
        if (app.Pack.Files.Count == 0) ui.Label(Row(), "Content/ пуст — испеките ресурсы на вкладке «Пекарня»", t.TextFaint);
        else Folder(ui, "", 0);
        ui.EndScroll();
        _treeRows = _rowCount;
    }

    /// <summary>Каталог <paramref name="dir"/> (пусто — корень): подкаталоги, затем файлы.</summary>
    private void Folder(UiContext ui, string dir, int depth)
    {
        string prefix = dir.Length == 0 ? "" : dir + "/";
        var files = app.Pack.Under(dir).ToList();
        var subdirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaves = new List<ContentFile>();
        foreach (var f in files)
        {
            string rest = f.Path[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0) leaves.Add(f);
            else subdirs.Add(rest[..slash]);
        }
        foreach (var sub in subdirs)
        {
            string path = prefix + sub;
            var under = app.Pack.Under(path).ToList();
            int on = under.Count(f => app.Pack.IsIncluded(f.Path));
            string tail = on == under.Count ? $"{under.Count}" : $"{on} из {under.Count}";
            if (RowWith(ui, path, sub, tail, depth, leaf: false, on > 0, on > 0 && on < under.Count)) Folder(ui, path, depth + 1);
        }
        foreach (var f in leaves)
            RowWith(ui, f.Path, f.Path[prefix.Length..], Size(f.Size), depth, leaf: true, app.Pack.IsIncluded(f.Path), partial: false);
    }

    /// <returns>Каталог раскрыт.</returns>
    private bool RowWith(UiContext ui, string path, string name, string tail, int depth, bool leaf, bool included, bool partial)
    {
        var t = ui.Theme;
        var row = Row();
        var check = row.SplitLeft(ui.S(26), out row);
        bool value = included;
        if (ui.Toggle(check, "", ref value, id: "in:" + path)) app.ToggleIncluded(path);
        ui.Tooltip(check, partial ? "в пак идёт часть файлов — щелчок выключает все" : included ? "в паке — щелчок выключает" : "выключен — щелчок включает");
        var size = row.SplitRight(ui.S(90), out row);
        ui.Label(size.Shrink(ui.S(6), 0), tail, t.TextFaint, UiAlign.Right, UiFace.Num, t.SmallFontSize);
        bool open = ui.TreeNode(row, path, name, depth, leaf, app.Selected == path, out bool clicked, defaultOpen: depth == 0,
            textColour: included ? null : t.TextFaint);
        if (clicked) app.Selected = path;
        return open;
    }

    private static string Size(long bytes) =>
        bytes >= 1 << 20 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:F1} МБ") : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F1} КБ");

    private void DrawSelection(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(12), ui.S(8));
        var files = app.Pack.Under(app.Selected).ToList();
        var title = r.SplitTop(ui.S(32), out r);
        float w = ui.Label(title, app.Selected.Length == 0 ? "Content/" : app.Selected, t.Text, UiAlign.Left, UiFace.Head, 19);
        int on = files.Count(f => app.Pack.IsIncluded(f.Path));
        string kind = files.Count > 0 ? Previews.KindOf(files[0].Path) ?? "без предпросмотра" : "пусто";
        ui.Label(title with { X0 = title.X0 + w + ui.S(14) },
            $"{kind} · файлов {files.Count}, {Size(files.Sum(f => f.Size))} · в пак {on}", t.TextDim);
        r.SplitTop(ui.S(6), out r);
        _preview.Draw(ui, r, _content, app.Selected, files);
    }

    // ── пекарня ────────────────────────────────────────────────────────

    private void DrawBaker(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(12), ui.S(8));

        var row = r.SplitTop(ui.S(32), out r);
        ui.Label(row.SplitLeft(ui.S(120), out row), "Сырой файл", t.TextDim);
        var pick = row.SplitRight(ui.S(120), out row);
        var apply = row.SplitRight(ui.S(110), out row);
        ui.TextField(row with { X1 = row.X1 - ui.S(6) }, "source", ref _sourceText, "путь к сырому файлу (raw-экспорт, glb, png, ttf, json)");
        if (ui.Button(apply, "Открыть", enabled: _sourceText.Length > 0 && !app.Busy, id: "source-open")) app.SetSource(_sourceText.Trim('"'));
        if (ui.Button(pick, "Выбрать…", enabled: !app.DialogOpen && !app.Busy, id: "source-pick"))
            app.Pick("Сырой файл", directory: false, "", app.Source ?? app.Baker.Roots.Roots.Values.FirstOrDefault(), paths =>
            {
                _sourceText = paths[0];
                app.SetSource(paths[0]);
            });
        r.SplitTop(ui.S(6), out r);

        if (app.Source is null)
        {
            ui.Label(r.SplitTop(ui.S(28), out _), "Выберите сырой файл: пекарня подберёт конвертер по виду файла.", t.TextFaint);
            DrawKinds(ui, r);
            return;
        }
        if (app.SourceError is not null)
        {
            ui.Label(r.SplitTop(ui.S(28), out r), app.SourceError, t.Bad);
            DrawKinds(ui, r);
            return;
        }

        row = r.SplitTop(ui.S(32), out r);
        ui.Label(row.SplitLeft(ui.S(120), out row), "Конвертер", t.TextDim);
        int index = Math.Max(0, app.Candidates.ToList().FindIndex(c => c.Id == app.Recipe?.Converter));
        var items = app.Candidates.Select(c => $"{c.Id} — {c.Title}").ToList();
        if (ui.Combo(row.SplitLeft(MathF.Min(row.Width, ui.S(520)), out _), "converter", ref index, items))
            app.ChooseConverter(app.Candidates[index]);

        row = r.SplitTop(ui.S(24), out r);
        string recipePath = app.Baker.RecipePath(app.Source);
        ui.Label(row, app.RecipeExists ? $"рецепт: {recipePath}" : $"рецепта нет — параметры по умолчанию; «Испечь» запишет {recipePath}",
            app.RecipeExists ? t.TextDim : t.Warn, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        if (app.Recipe?.Notes is { Length: > 0 } notes)
        {
            var n = r.SplitTop(ui.S(22), out r);
            ui.Label(n, notes, t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            ui.Tooltip(n, notes.Length > 600 ? notes[..600] + "…" : notes);
        }
        r.SplitTop(ui.S(6), out r);

        var bar = r.SplitBottom(ui.S(44), out r);
        var written = app.LastWritten.Count > 0 ? r.SplitBottom(MathF.Min(r.Height * 0.35f, ui.S(22) * (app.LastWritten.Count + 1) + ui.S(8)), out r) : default;
        _editor.Draw(ui, r, app);

        bar = bar.Shrink(0, ui.S(6));
        var bake = bar.SplitLeft(ui.S(150), out bar);
        if (ui.Button(bake, "Испечь", primary: true, enabled: !app.Busy && _editor.Params is not null)) app.BakeSource(_editor.Params!);
        ui.Tooltip(bake, $"Испечь в {app.Pack.ContentDir} и записать рецепт");

        if (app.LastWritten.Count > 0)
        {
            ui.SectionHeader(written.SplitTop(ui.S(22), out written), $"ЗАПИСАНО   {app.LastWritten.Count}");
            var rows = ui.BeginList(written, "written", app.LastWritten.Count, ui.S(22));
            Span<char> path = stackalloc char[260];
            for (int i = rows.First; i < rows.Last; i++)
            {
                var line = rows.Row(i);
                if (ui.Selectable(line, ui.Id(i), false))
                {
                    app.Selected = app.LastWritten[i];
                    _tab = 0;
                }
                ui.Label(line.Shrink(ui.S(6), 0), new Line(path).S("Content/").S(app.LastWritten[i]).Text, t.TextDim, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            }
            ui.EndList();
        }
    }

    /// <summary>Таблица видов файлов: какие сырые файлы пекарня умеет печь.</summary>
    private static void DrawKinds(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        r.SplitTop(ui.S(8), out r);
        ui.SectionHeader(r.SplitTop(ui.S(24), out r), "ВИДЫ ФАЙЛОВ");
        foreach (var c in Bake.Baker.Converters)
        {
            var row = r.SplitTop(ui.S(22), out r);
            ui.Label(row.SplitLeft(ui.S(110), out row), c.Id, t.Text, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            ui.Label(row.SplitLeft(ui.S(300), out row), c.Kind, t.TextDim, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            ui.Label(row, c.Title, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        }
    }

    // ── лог ────────────────────────────────────────────────────────────

    private void DrawLog(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));
        ui.SectionHeader(r.SplitTop(ui.S(24), out r), $"ЛОГ   {app.Log.Count}");
        bool grew = _logVersionSeen != app.LogVersion;
        if (grew)
        {
            _logRows.Clear();
            for (int i = 0; i < app.Log.Count; i++) _logRows.Add(i);
            _logVersionSeen = app.LogVersion;
        }
        float rowH = ui.S(20);
        if (grew && _logRows.Count > 0) ui.ScrollIntoView("log", (_logRows.Count - 1) * rowH, _logRows.Count * rowH, r.Height);
        ui.VirtualList(r, "log", _logRows.Count, rowH, (i, row) =>
        {
            var e = app.Log[_logRows[i]];
            uint colour = e.Kind switch { LogKind.Done => t.Good, LogKind.Failed => t.Bad, _ => t.TextDim };
            var time = row.SplitLeft(ui.S(70), out row);
            ui.Label(time.Shrink(ui.S(6), 0), e.Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture), t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            var dot = row.SplitLeft(ui.S(16), out row);
            if (e.Kind != LogKind.Info) ui.StatusDot(dot.Centre, colour, 3.5f);
            ui.Label(row, e.Text, colour, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            ui.Tooltip(row, e.Text.Length > 500 ? e.Text[..500] + "…" : e.Text);
        });
    }
}
