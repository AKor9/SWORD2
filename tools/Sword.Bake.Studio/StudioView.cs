using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Окно Studio: панель инструментов, слева узел проекта и четыре вида — каталог контента, единицы сборки, импорты,
/// обработчики (<see cref="LeftView"/>), в центре — выбранное: проект и пак (корни, плагины, актуальность, целостность,
/// содержимое), ассет контента, импорт, обработчик или единица сборки с вкладками (свойства, исходники и выходы,
/// зависимости, просмотр); лог, строка состояния.
/// Клавиши: F7 — собрать пак, F5 — проверить статусы, Ctrl+S — сохранить параметры, ↑↓ — выбор в дереве,
/// двойной щелчок по ассету — собрать пак с ним.
/// </summary>
internal sealed partial class StudioView(StudioApp app, string? select, int tab, bool confirmDelete = false, bool addAsset = false) : IDisposable
{
    private readonly ParamsEditor _editor = new(app.Project.Processors);
    private static readonly string[] TabNames = ["Свойства", "Исходники и выходы", "Зависимости", "Просмотр"];
    private readonly PreviewPanel _preview = new();

    private float _left = 340, _log = 250;
    private string _filter = "", _pakFilter = "";
    private int _tab = Math.Clamp(tab, 0, TabNames.Length - 1);
    /// <summary>
    /// Что в центре. Пак — результат всего проекта, поэтому он не вкладка ассета, а отдельный узел над деревом;
    /// выбор единицы сборки (в дереве, логе, зависимостях) возвращает к ней.
    /// </summary>
    private Centre _centre = tab >= 4 ? Centre.Project : Centre.Unit;
    private bool _projectView => _centre == Centre.Project;
    private string? _selectedSeen;
    private bool _selectApplied;

    private readonly List<string> _treeOrder = [];
    private int _treeRows = 1;
    /// <summary>Ассет, к которому прокрутить дерево; строки дерева прошлого кадра — с заголовками папок.</summary>
    private string? _scrollTreeTo;
    private readonly Dictionary<string, int> _treeRowOf = [];

    private bool _logProblemsOnly, _logSelectedOnly, _logFollow = true;
    private readonly List<int> _logRows = [];
    private int _logVersionSeen = -1, _logFilterKey = -1, _selectedLog = -1;

    /// <summary>Ресурсы видеокарты вкладки «Просмотр»; окно заменяется при открытии другого проекта.</summary>
    public void Dispose() => _preview.Dispose();

    public void Draw(UiContext ui)
    {
        if (!_selectApplied)
        {
            _selectApplied = true;
            if (select is not null) ApplySelect(select);
            _selectedSeen = app.Selected;
            if (confirmDelete) _confirmDelete = app.Selected;   // для снимков: вопрос открыт сразу
            if (addAsset) OpenAdd();
        }
        // Клавиши разбираются ДО Sync: ↑↓ меняют выбранный ассет, и форма параметров должна показать уже новый —
        // иначе кадр рисовался бы с чужим заголовком над старыми полями.
        HandleKeys(ui);
        // выбор ассета откуда угодно (дерево, стрелки, лог, зависимости, добавление) переводит центр на ассет
        if (!string.Equals(app.Selected, _selectedSeen, StringComparison.Ordinal))
        {
            if (_selectedSeen is not null && app.Selected is not null) _centre = Centre.Unit;
            _selectedSeen = app.Selected;
        }
        _editor.Sync(app.SelectedAsset, app);

        float pad = ui.S(8);
        var area = ui.Screen;
        var toolbar = area.SplitTop(ui.S(46), out area);
        var status = area.SplitBottom(ui.S(28), out area);
        DrawToolbar(ui, toolbar.Shrink(pad, ui.S(7)));
        DrawStatus(ui, status);

        area = area.Shrink(pad, 0);
        _left = Math.Clamp(_left, ui.S(220), MathF.Max(ui.S(220), area.Width - ui.S(420)));
        var left = area.SplitLeft(_left, out var right);
        var vsplit = right.SplitLeft(pad, out right);
        float leftEdge = area.X0 + _left;
        if (ui.Splitter(vsplit, "split-left", ref leftEdge, vertical: true, area.X0 + ui.S(220), area.X1 - ui.S(420)))
            _left = leftEdge - area.X0;

        _log = Math.Clamp(_log, ui.S(90), MathF.Max(ui.S(90), right.Height - ui.S(220)));
        var log = right.SplitBottom(_log, out var centre);
        var hsplit = centre.SplitBottom(pad, out centre);
        float logTop = right.Y1 - _log;
        if (ui.Splitter(hsplit, "split-log", ref logTop, vertical: false, right.Y0 + ui.S(220), right.Y1 - ui.S(90)))
            _log = right.Y1 - logTop;

        DrawTree(ui, left);
        DrawDetails(ui, centre);
        DrawLog(ui, log);
        DrawConfirmDelete(ui);
        DrawAddAsset(ui);
    }

    private void HandleKeys(UiContext ui)
    {
        var input = ui.Input;
        if (_confirmDelete is not null || _add is not null) return;   // открыт диалог: Enter и Escape разбирает он
        if (input.Ctrl && input.TakeKey(UiKey.S)) _editor.Save(app);
        if (ui.WantsKeyboard) return;
        if (input.TakeKey(UiKey.F7) && !app.Busy) app.BuildAll();
        if (input.TakeKey(UiKey.F5)) app.RefreshStatuses();
        bool previewVisible = _centre == Centre.Unit && _tab == 3 || _centre == Centre.Content && _contentTab == 1;
        if (previewVisible && _preview.TakesVerticalKeys) return;
        int step = input.TakeKey(UiKey.Down) ? 1 : input.TakeKey(UiKey.Up) ? -1 : 0;
        if (step != 0 && _treeOrder.Count > 0)
        {
            int at = _treeOrder.IndexOf(SelectedKey);
            at = Math.Clamp(at < 0 ? 0 : at + step, 0, _treeOrder.Count - 1);
            SelectKey(_treeOrder[at]);
            _scrollTreeTo = _treeOrder[at];
        }
    }

    // ── статусы ────────────────────────────────────────────────────────

    private (uint Colour, string Label) StatusLook(UiContext ui, string id)
    {
        var t = ui.Theme;
        if (app.Busy && app.Live.TryGetValue(id, out var live))
            switch (live)
            {
                case LiveKind.Queued: return (t.TextFaint, "в очереди");
                case LiveKind.Building:
                    float pulse = 0.55f + 0.45f * MathF.Sin((float)ui.Input.Time * 7);
                    ui.RedrawAfter(1 / 30.0);
                    return (UiDrawList.WithAlpha(t.Accent, pulse), "запекается");
                case LiveKind.Failed: return (t.Bad, "ошибка");
                case LiveKind.Built or LiveKind.UpToDate: return (t.Good, live == LiveKind.Built ? "запечён" : "актуален");
                default: return (t.TextFaint, "пропущен");
            }
        var c = app.StatusOf(id);
        return c.Status switch
        {
            AssetStatus.UpToDate => (t.Good, "актуален"),
            AssetStatus.Dirty => app.StatusesKnown ? (t.Warn, "устарел") : (t.TextFaint, "проверяется"),
            AssetStatus.NoSource => (t.TextFaint, "нет исходника"),
            _ => (t.Bad, "ошибка"),
        };
    }

    private (uint Colour, string Label) PakLook(UiContext ui)
    {
        var t = ui.Theme;
        return app.PakStatus?.Status switch
        {
            null => (t.TextFaint, "пак проверяется"),
            PakStatus.UpToDate => (t.Good, "пак актуален"),
            PakStatus.Stale => (t.Warn, "пак устарел"),
            PakStatus.Missing => (t.TextFaint, "пак не собран"),
            _ => (t.Bad, "пак не открывается"),
        };
    }

    // ── панель инструментов и строка состояния ─────────────────────────

    private void DrawToolbar(UiContext ui, UiRect r)
    {
        float gap = ui.S(6), group = ui.S(14);
        bool busy = app.Busy;
        // кнопки ассета работают только с видимым ассетом: в режиме проекта выбранного в дереве нет
        var selected = _centre == Centre.Unit ? app.SelectedAsset : null;

        // Кнопки по порядку важности: не влезающая в узкое или крупно масштабированное окно не рисуется,
        // а не наезжает на соседей. Состояние пака на панели не дублируется — оно в узле проекта и строке состояния.
        bool Slot(float width, float spaceBefore, out UiRect slot)
        {
            slot = default;
            if (r.Width < spaceBefore + width) return false;
            r.SplitLeft(spaceBefore, out r);
            slot = r.SplitLeft(width, out r);
            return true;
        }

        if (Slot(ui.S(150), 0, out var build))
        {
            if (ui.Button(build, "Собрать пак  F7", primary: true, enabled: !busy)) app.BuildAll();
            ui.Tooltip(build, $"Запечь устаревшие ассеты и записать пак {app.Project.PakPath}; при ошибке прежний пак остаётся");
        }
        if (Slot(ui.S(96), group, out var one))
        {
            if (ui.Button(one, "Собрать", enabled: !busy && selected is not null))
                app.StartBuild([selected!], force: false, "сборка " + selected!.Id);
            ui.Tooltip(one, "Запечь выбранный ассет, если устарел, и обновить пак");
        }
        if (Slot(ui.S(110), gap, out var rebuild) && ui.Button(rebuild, "Пересобрать", enabled: !busy && selected is not null))
            app.StartBuild([selected!], force: true, "пересборка " + selected!.Id);
        if (Slot(ui.S(90), gap, out var cancel) && ui.Button(cancel, "Отмена", enabled: busy)) app.Cancel();
        if (Slot(ui.S(96), group, out var add))
        {
            if (ui.Button(add, "+ Ассет", enabled: !busy)) OpenAdd();
            ui.Tooltip(add, "Добавить ассет из файлов или каталога");
        }
        if (Slot(ui.S(96), gap, out var delete))
        {
            if (ui.Button(delete, "Удалить", enabled: !busy && selected is not null)) _confirmDelete = selected!.Id;
            ui.Tooltip(delete, "Удалить манифест ассета; из пака его уберёт следующая сборка");
        }
        if (Slot(ui.S(130), group, out var verify))
        {
            if (ui.Button(verify, "Проверить пак", enabled: !busy && app.Builder.PakExists)) app.VerifyPak();
            ui.Tooltip(verify, "Целостность: хеши всех файлов пака и согласие оглавления со сведениями о сборке");
        }
        if (Slot(ui.S(110), group, out var watch))
        {
            ui.Toggle(watch, "Следить", ref app.Watch);
            ui.Tooltip(watch, "Изменились исходники или манифесты — проверить статусы и собрать устаревшее");
        }
        if (Slot(ui.S(100), gap, out var open))
        {
            if (ui.Button(open, "Проект…", enabled: !busy && !app.DialogOpen)) app.OpenOtherProject();
            ui.Tooltip(open, "Открыть другой проект или создать новый");
        }

        var info = r.Shrink(ui.S(12), 0);
        if (busy && info.Width >= ui.S(160))
        {
            var progress = info.SplitRight(MathF.Min(info.Width, ui.S(420)), out _);
            float fraction = app.TaskTotal > 0 ? (float)app.TaskDone / app.TaskTotal : -1;
            string text = app.TaskTotal > 0
                ? $"{app.TaskTitle}: {app.TaskDone} из {app.TaskTotal}{(app.TaskCurrent is { } cur ? " · " + cur : "")}"
                : app.TaskTitle;
            ui.ProgressBar(progress.Shrink(0, ui.S(6)), fraction, text);
        }
    }

    /// <summary>ID ассета, про который открыт вопрос об удалении; null — вопроса нет.</summary>
    private string? _confirmDelete;

    /// <summary>Вопрос перед удалением: что именно исчезнет. Enter — удалить, Escape — отмена.</summary>
    private void DrawConfirmDelete(UiContext ui)
    {
        if (_confirmDelete is not { } id) return;
        var asset = app.Project.Find(id);
        if (asset is null || app.Busy)
        {
            _confirmDelete = null;
            return;
        }
        var t = ui.Theme;
        float w = MathF.Min(ui.S(620), ui.Width - ui.S(40)), h = ui.S(200);
        var dialog = UiRect.FromSize(MathF.Round((ui.Width - w) * 0.5f), MathF.Round((ui.Height - h) * 0.38f), w, h);
        ui.BeginModal(dialog);
        var body = dialog.Shrink(ui.S(18), ui.S(16));
        ui.Label(body.SplitTop(ui.S(28), out body), $"Удалить ассет {id}?", t.Text, UiAlign.Left, UiFace.Head, 18);
        body.SplitTop(ui.S(8), out body);
        var rec = app.Builder.RecordOf(id);
        ui.Label(body.SplitTop(ui.S(22), out body), $"Манифест: {asset.File}", t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        ui.Label(body.SplitTop(ui.S(22), out body),
            rec is null
                ? "В паке ассета нет: он не собирался."
                : $"Следующая сборка уберёт из пака его файлы: {rec.Outputs.Count}, {rec.Outputs.Sum(o => o.Size) / 1048576.0:F1} МБ. Исходники не трогаются.",
            t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        ui.Label(body.SplitTop(ui.S(22), out body), $"Копия манифеста останется в {BakeProject.StateDirName}/removed.", t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);

        var buttons = body.SplitBottom(ui.S(30), out _);
        bool confirm = ui.Button(buttons.SplitRight(ui.S(130), out buttons), "Удалить", primary: true, id: "confirm-delete-yes");
        buttons.SplitRight(ui.S(8), out buttons);
        bool cancel = ui.Button(buttons.SplitRight(ui.S(110), out buttons), "Отмена", id: "confirm-delete-no");
        ui.EndModal();

        if (ui.Input.TakeKey(UiKey.Enter)) confirm = true;
        if (ui.Input.TakeKey(UiKey.Escape)) cancel = true;
        if (confirm) app.Delete(asset);
        if (confirm || cancel) _confirmDelete = null;
    }

    private void DrawStatus(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Draw.Rect(r, t.Panel);
        r = r.Shrink(ui.S(10), ui.S(5));
        int up = 0, dirty = 0, errors = 0, noSource = 0;
        foreach (var a in app.Project.Assets)
            switch (app.StatusOf(a.Id).Status)
            {
                case AssetStatus.UpToDate: up++; break;
                case AssetStatus.Dirty: dirty++; break;
                case AssetStatus.NoSource: noSource++; break;
                default: errors++; break;
            }
        if (app.Refreshing || !app.StatusesKnown)
        {
            var bar = r.SplitRight(ui.S(120), out r);
            ui.ProgressBar(bar.Shrink(0, ui.S(4)), -1);
        }
        float x = r.X0;
        void Part(uint colour, string text)
        {
            ui.StatusDot(new Vector2(x + ui.S(5), r.Centre.Y), colour);
            x += ui.S(14);
            x += ui.Label(new UiRect(x, r.Y0, r.X1, r.Y1), text, t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize) + ui.S(18);
        }
        var (pakColour, pakLabel) = PakLook(ui);
        Part(pakColour, pakLabel);
        Part(t.Good, $"актуальны {up}");
        Part(t.Warn, $"устарели {dirty}");
        if (noSource > 0) Part(t.TextFaint, $"без исходников {noSource}");
        Part(t.Bad, $"ошибок {errors}");
        if (app.LastSummary is { } s)
            ui.Label(new UiRect(x, r.Y0, r.X1 - ui.S(12), r.Y1),
                $"последняя задача ({app.TaskTitle}): {s.Elapsed.TotalSeconds:F1} с, запечено {s.Built}, ошибок {s.Failed}{(s.Cancelled ? ", отменена" : "")}",
                s.Failed == 0 && !s.Cancelled ? t.TextFaint : t.Bad, UiAlign.Left, UiFace.Body, t.SmallFontSize);
    }

    // ── дерево ─────────────────────────────────────────────────────────

    private void DrawTree(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));

        // проект и его пак — над ассетами: это результат всего дерева, а не свойство одного ассета
        ui.SectionHeader(r.SplitTop(ui.S(24), out r), "ПРОЕКТ");
        var card = r.SplitTop(ui.S(48), out r);
        if (ui.Selectable(card, ui.Id("project-node"), _projectView)) _centre = Centre.Project;
        {
            var (colour, label) = PakLook(ui);
            var inner = card.Shrink(ui.S(10), ui.S(4));
            var top = inner.SplitTop(inner.Height * 0.5f, out var bottom);
            ui.Label(top, app.Project.Name, t.Text, UiAlign.Left, UiFace.Head, 15);
            ui.StatusDot(new Vector2(bottom.X0 + ui.S(5), bottom.Centre.Y), colour);
            ui.Label(bottom with { X0 = bottom.X0 + ui.S(16) }, $"{Path.GetFileName(app.Project.PakPath)} · {label}", t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            ui.Tooltip(card, (app.PakStatus?.Reason ?? label) + " — корни, плагины, актуальность, целостность и содержимое пака");
        }
        r.SplitTop(ui.S(10), out r);

        int view = (int)_view;
        ui.Tabs(r.SplitTop(ui.S(28), out r), "left-view", ref view, ViewNames);
        _view = (LeftView)view;
        r.SplitTop(ui.S(6), out r);
        ui.TextField(r.SplitTop(ui.S(28), out r), "filter", ref _filter, FilterHint);
        r.SplitTop(ui.S(6), out r);

        float rowH = ui.S(t.RowHeight);
        if (_scrollTreeTo is not null)
        {
            if (_treeRowOf.TryGetValue(_scrollTreeTo, out int rowIndex))
                ui.ScrollIntoView("tree", rowIndex * rowH, (rowIndex + 1) * rowH, r.Height);
            _scrollTreeTo = null;
        }
        _rowArea = ui.BeginScroll(r, "tree", _treeRows * rowH);
        _rowHeight = rowH;
        _rowCount = 0;
        _treeOrder.Clear();
        _treeRowOf.Clear();
        switch (_view)
        {
            case LeftView.Content: DrawContentTree(ui); break;
            case LeftView.Units: DrawUnitTree(ui); break;
            case LeftView.Imports: DrawImportTree(ui); break;
            default: DrawProcessorTree(ui); break;
        }
        if (_treeOrder.Count == 0)
            ui.Label(Row(), _filter.Length > 0 ? $"по «{_filter}» ничего не найдено"
                : app.Project.Assets.Count == 0 ? "ассетов пока нет — «+ Ассет» добавит первый" : "пусто", t.TextFaint);
        ui.EndScroll();
        _treeRows = _rowCount;
    }

    // ── подробности ────────────────────────────────────────────────────

    private void DrawDetails(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(12), ui.S(8));
        var asset = app.SelectedAsset;
        switch (_centre)
        {
            case Centre.Content: DrawContentDetails(ui, r); return;
            case Centre.Import: DrawImportDetails(ui, r); return;
            case Centre.Processor: DrawProcessorDetails(ui, r); return;
        }

        var title = r.SplitTop(ui.S(32), out r);
        if (_projectView || asset is null)
        {
            float w = ui.Label(title, app.Project.Name, t.Text, UiAlign.Left, UiFace.Head, 19);
            ui.Label(title with { X0 = title.X0 + w + ui.S(14) }, $"проект · {app.Project.ProjectFile}", t.TextDim);
            r.SplitTop(ui.S(6), out r);
            DrawProject(ui, r);
            return;
        }
        {
            var processor = app.Project.Processors.Find(asset.Processor);
            float w = ui.Label(title, asset.Id, t.Text, UiAlign.Left, UiFace.Head, 19);
            ui.Label(title with { X0 = title.X0 + w + ui.S(14) },
                $"{asset.Processor} · {processor?.Title ?? "неизвестный обработчик"} · импорт: {ImportLabel(asset)}", t.TextDim);

            var statusRow = r.SplitTop(ui.S(24), out r);
            var (colour, label) = StatusLook(ui, asset.Id);
            var check = app.StatusOf(asset.Id);
            ui.StatusDot(new Vector2(statusRow.X0 + ui.S(5), statusRow.Centre.Y), colour);
            var statusText = statusRow with { X0 = statusRow.X0 + ui.S(16) };
            string statusLine = check.Reason == label || app.Busy && app.Live.ContainsKey(asset.Id) ? label : $"{label}: {check.Reason}";
            ui.Label(statusText, statusLine, check.Status == AssetStatus.Error ? t.Bad : t.TextDim);
            ui.Tooltip(statusText, check.Reason);

            if (asset.Notes.Length > 0)
            {
                var notes = r.SplitTop(ui.S(22), out r);
                ui.Label(notes, asset.Notes, t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
                ui.Tooltip(notes, asset.Notes.Length > 600 ? asset.Notes[..600] + "…" : asset.Notes);
            }
        }
        r.SplitTop(ui.S(4), out r);
        ui.Tabs(r.SplitTop(ui.S(30), out r), "details-tabs", ref _tab, TabNames);
        r.SplitTop(ui.S(8), out r);

        switch (_tab)
        {
            case 0: _editor.Draw(ui, r, app); break;
            case 1: DrawSources(ui, r, asset); break;
            case 2: DrawDependencies(ui, r, asset); break;
            default: _preview.Draw(ui, r, app, asset); break;
        }
    }

    private string ShortPath(string path)
    {
        foreach (var (name, root) in app.Project.Roots.Roots.OrderByDescending(kv => kv.Value.Length))
            if (path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return $"${{{name}}}{path[root.TrimEnd('\\').Length..].Replace('\\', '/')}";
        return path;
    }

    private static string Size(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:F1} МБ" : bytes >= 1024 ? $"{bytes / 1024.0:F0} КБ" : $"{bytes} Б";

    /// <summary>Строка списка: 0 — заголовок, 1 — обычная, 2 — выделенная, 3 — проблема.</summary>
    private readonly record struct Line(int Kind, string Text, long Size = -1, string Tail = "");

    private void DrawLines(UiContext ui, UiRect r, string id, List<Line> lines)
    {
        var t = ui.Theme;
        float rowH = ui.S(20);
        ui.VirtualList(r, id, lines.Count, rowH, (i, row) =>
        {
            var line = lines[i];
            if (line.Kind == 0)
            {
                ui.SectionHeader(row, line.Text);
                return;
            }
            var sizeCell = row.SplitRight(ui.S(90), out var pathCell);
            if (line.Tail.Length > 0)
            {
                var tail = pathCell.SplitRight(MathF.Min(ui.S(220), pathCell.Width * 0.35f), out pathCell);
                ui.Label(tail.Shrink(ui.S(6), 0), line.Tail, t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            }
            uint colour = line.Kind switch { 2 => t.Accent, 3 => t.Bad, _ => t.TextDim };
            ui.Label(pathCell.Shrink(ui.S(8), 0), line.Text, colour, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            ui.Tooltip(pathCell, line.Text);
            if (line.Size >= 0) ui.Label(sizeCell, Size(line.Size), t.TextFaint, UiAlign.Right, UiFace.Num, t.SmallFontSize);
        });
    }

    private void DrawSources(UiContext ui, UiRect r, AssetManifest asset)
    {
        var lines = new List<Line>();
        var sources = app.Project.SourcesOf(asset);
        lines.Add(new(0, $"ИСХОДНИКИ ИЗ ПАРАМЕТРОВ   {sources.Count}"));
        if (sources.Count == 0) lines.Add(new(1, "у обработчика нет полей-исходников или параметры не разбираются"));
        foreach (var s in sources)
            lines.Add(new(s.Problem is null ? 1 : 3,
                $"{s.Field}: {s.Value}{(s.Problem is null ? "" : "  — " + s.Problem)}",
                s.Problem is null && s.Kind == SourceKind.File ? new FileInfo(s.FullPath!).Length : -1,
                s.Kind == SourceKind.Directory ? "каталог" : "файл"));

        if (app.Builder.FailureOf(asset.Id) is { } failure && app.StatusOf(asset.Id).Status == AssetStatus.Error)
        {
            lines.Add(new(0, $"ПОСЛЕДНЯЯ СБОРКА УПАЛА   {failure.Utc.ToLocalTime():dd.MM.yyyy HH:mm:ss}"));
            foreach (var l in failure.Message.Split('\n').Take(20)) lines.Add(new(3, l.TrimEnd('\r')));
        }

        var rec = app.Builder.RecordOf(asset.Id);
        if (rec is null)
        {
            lines.Add(new(0, "В ПАКЕ"));
            lines.Add(new(1, "ассета в паке нет — прочитанные входы и выходы станут известны после сборки"));
        }
        else
        {
            lines.Add(new(0, $"ВХОДЫ СБОРКИ ПАКА   {rec.Inputs.Count} · {Size(rec.Inputs.Sum(i => i.Size))} · собран {rec.BakedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}"));
            foreach (var d in rec.InputDirs) lines.Add(new(2, ShortPath(d) + "/  (каталог целиком)"));
            foreach (var i in rec.Inputs) lines.Add(new(1, ShortPath(i.Path), i.Size));
            foreach (var c in rec.ContentInputs) lines.Add(new(2, $"{c}  (готовый файл другого ассета)"));
            lines.Add(new(0, $"ВЫХОДЫ В ПАКЕ   {rec.Outputs.Count} · {Size(rec.Outputs.Sum(o => o.Size))}"));
            foreach (var o in rec.Outputs) lines.Add(new(1, o.Path, o.Size));
        }
        DrawLines(ui, r, "io:" + asset.Id, lines);
    }

    private void DrawDependencies(UiContext ui, UiRect r, AssetManifest asset)
    {
        var t = ui.Theme;
        List<AssetManifest> deps, dependents;
        try
        {
            deps = [.. app.Builder.Dependencies(asset)];
            dependents = app.Project.Assets.Where(a => a != asset && SafeDeps(a).Contains(asset)).ToList();
        }
        catch (InvalidDataException e)
        {
            ui.Label(r.SplitTop(ui.S(28), out _), e.Message, t.Bad);
            return;
        }

        float rowH = ui.S(t.RowHeight);
        // списки бывают длинными (от библиотеки зависят все) — в прокрутке; высота содержимого известна после отрисовки
        var view = r;
        var content = ui.BeginScroll(view, "deps:" + asset.Id, _depsHeight);
        r = content with { Y1 = content.Y0 + 1e5f };
        float top = r.Y0;
        void Section(string title, List<AssetManifest> list, string empty)
        {
            ui.SectionHeader(r.SplitTop(ui.S(26), out r), $"{title}   {list.Count}");
            if (list.Count == 0) ui.Label(r.SplitTop(rowH, out r).Shrink(ui.S(8), 0), empty, t.TextFaint);
            foreach (var d in list)
            {
                var row = r.SplitTop(rowH, out r);
                if (ui.Selectable(row, ui.Id("dep:" + title + d.Id), false)) SelectKey(UnitKey(d.Id));
                var (colour, label) = StatusLook(ui, d.Id);
                ui.StatusDot(new Vector2(row.X0 + ui.S(12), row.Centre.Y), colour);
                ui.Label(row with { X0 = row.X0 + ui.S(24) }, $"{d.Id}   ·   {d.Processor}   ·   {label}", t.TextDim);
            }
            r.SplitTop(ui.S(10), out r);
        }
        Section("СОБИРАЕТСЯ ПОСЛЕ", deps, "нет: не зависит от других ассетов");
        Section("ОТ НЕГО ЗАВИСЯТ", dependents, "нет");
        var refs = app.Project.ReferencesOf(asset);
        Section("ИСПОЛЬЗУЕТ (ССЫЛКИ)", refs.Select(x => x.Target).OfType<AssetManifest>().Distinct().ToList(), "нет ссылок");
        foreach (var bad in refs.Where(x => x.Problem is not null))
            ui.Label(r.SplitTop(rowH, out r).Shrink(ui.S(8), 0), $"{bad.Field}: {bad.Problem}", t.Bad);
        Section("НА НЕГО ССЫЛАЮТСЯ",
            app.Project.Assets.Where(a => a != asset && app.Project.ReferencesOf(a).Any(x => x.Target?.Id == asset.Id)).ToList(), "нет");
        ui.Label(r.SplitTop(ui.S(40), out r),
            "Зависимость — поле dependsOn манифеста или готовый файл другого ассета, прочитанный при сборке пака. " +
            "Ссылка — поле параметров с ID другой единицы: её параметры входят в хеш, но собираться раньше она не обязана.",
            t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        _depsHeight = r.Y0 - top;
        ui.EndScroll();
    }

    private float _depsHeight = 1000, _projectTopHeight = 400;

    private IReadOnlyList<AssetManifest> SafeDeps(AssetManifest a)
    {
        try { return app.Builder.Dependencies(a); }
        catch (InvalidDataException) { return []; }
    }

    // ── проект и пак ───────────────────────────────────────────────────

    private void DrawProject(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        var project = app.Project;
        float rowH = ui.S(24), small = t.SmallFontSize;
        UiRect Row() => r.SplitTop(rowH, out r);

        // Сведения о проекте и паке — сверху, в своей прокрутке: в низком окне или при крупном масштабе они не
        // выдавливают содержимое пака, которому остаётся не меньше ~150 пикселей. Высота известна после отрисовки.
        var all = r;
        float topHeight = MathF.Min(_projectTopHeight, MathF.Max(ui.S(60), all.Height - ui.S(150)));
        var topView = all.SplitTop(topHeight, out var below);
        var topContent = ui.BeginScroll(topView, "project-top", _projectTopHeight);
        r = topContent with { Y1 = topContent.Y0 + 1e5f };
        float top = r.Y0;

        // проект: каталоги, плагины, проблемы
        ui.SectionHeader(r.SplitTop(ui.S(26), out r), "ПРОЕКТ");
        ui.Label(Row(), $"ассеты: {project.AssetsDir}", t.TextDim, UiAlign.Left, UiFace.Body, small);
        foreach (var plugin in project.PluginPaths)
        {
            bool loaded = project.Processors.Plugins.Any(p => p.Path.Equals(plugin, StringComparison.OrdinalIgnoreCase));
            ui.Label(Row(), $"плагин: {plugin}{(loaded ? "" : "  — не загружен")}", loaded ? t.TextDim : t.Bad, UiAlign.Left, UiFace.Body, small);
        }
        ui.Label(Row(), "обработчики: " + string.Join(", ", project.Processors.Processors.Select(p => p.Id)), t.TextDim, UiAlign.Left, UiFace.Body, small);
        foreach (var problem in project.Problems.Take(4))
        {
            var row = Row();
            ui.Label(row, problem, t.Bad, UiAlign.Left, UiFace.Body, small);
            ui.Tooltip(row, problem);
        }

        // корни исходников
        var missing = project.MissingRoots();
        if (project.Roots.Roots.Count > 0 || missing.Count > 0)
        {
            r.SplitTop(ui.S(6), out r);
            ui.SectionHeader(r.SplitTop(ui.S(26), out r), $"КОРНИ ИСХОДНИКОВ   (локально — {SourceRoots.UserFile})");
            foreach (var (name, path, ok) in project.Roots.Roots.Select(kv => (kv.Key, kv.Value, Directory.Exists(kv.Value)))
                         .Concat(missing.Select(m => (m, "не задан", false))))
            {
                var row = Row();
                var button = row.SplitRight(ui.S(110), out row).Shrink(0, ui.S(1));
                ui.Label(row, $"${{{name}}} = {path}{(ok || path == "не задан" ? "" : "  — нет каталога")}", ok ? t.TextDim : t.Bad, UiAlign.Left, UiFace.Num, small);
                string root = name;
                if (ui.Button(button, ok ? "Изменить…" : "Задать…", enabled: !app.DialogOpen && !app.Busy, id: "root-" + name))
                    app.Pick(SourceKind.Directory, $"Корень исходников ${{{root}}}", false, "", ok ? path : null, paths => app.SetRoot(root, paths[0]));
            }
        }

        // пак
        r.SplitTop(ui.S(6), out r);
        ui.SectionHeader(r.SplitTop(ui.S(26), out r), "ПАК");
        {
            var row = Row();
            var verify = row.SplitRight(ui.S(210), out row).Shrink(0, ui.S(1));
            row.SplitRight(ui.S(6), out row);
            var build = row.SplitRight(ui.S(130), out row).Shrink(0, ui.S(1));
            if (ui.Button(build, "Собрать пак", primary: true, enabled: !app.Busy, id: "pak-build")) app.BuildAll();
            if (ui.Button(verify, "Проверить целостность", enabled: !app.Busy && app.Builder.PakExists, id: "pak-verify")) app.VerifyPak();
            string built = app.Builder.BuildInfo is { } info ? $", собран {info.BuiltUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}" : "";
            ui.Label(row, app.Builder.PakExists ? $"{project.PakPath}   {Size(app.Builder.PakLength)}{built}" : project.PakPath, t.TextDim, UiAlign.Left, UiFace.Num, small);
        }
        {
            var row = Row();
            var (colour, _) = PakLook(ui);
            ui.StatusDot(new Vector2(row.X0 + ui.S(5), row.Centre.Y), colour);
            string text = "актуальность: " + (app.PakStatus?.Reason ?? "проверяется…");
            ui.Label(row with { X0 = row.X0 + ui.S(16) }, text, app.PakStatus?.Status == PakStatus.Error ? t.Bad : t.TextDim);
        }
        foreach (var d in (app.PakStatus?.Details ?? []).Take(5))
        {
            var row = Row();
            ui.Label(row with { X0 = row.X0 + ui.S(16) }, d, t.TextFaint, UiAlign.Left, UiFace.Body, small);
            ui.Tooltip(row, d);
        }
        if (app.PakStatus is { Details.Count: > 5 } many)
            ui.Label(Row() with { X0 = r.X0 + ui.S(16) }, $"… и ещё {many.Details.Count - 5}", t.TextFaint, UiAlign.Left, UiFace.Body, small);
        {
            var row = Row();
            if (app.LastVerify is { } v)
            {
                ui.StatusDot(new Vector2(row.X0 + ui.S(5), row.Centre.Y), v.Report.Ok ? t.Good : t.Bad);
                ui.Label(row with { X0 = row.X0 + ui.S(16) },
                    $"целостность ({v.Time:HH:mm:ss}): " + (v.Report.Ok ? $"в порядке — файлов {v.Report.Files}, хеши совпали" : $"проблем {v.Report.Problems.Count}: {v.Report.Problems[0]}"),
                    v.Report.Ok ? t.TextDim : t.Bad);
            }
            else ui.Label(row with { X0 = row.X0 + ui.S(16) }, "целостность: не проверялась в этом сеансе", t.TextFaint);
        }

        _projectTopHeight = r.Y0 - top;
        ui.EndScroll();
        r = below;

        // содержимое
        r.SplitTop(ui.S(6), out r);
        var head = r.SplitTop(ui.S(28), out r);
        var filter = head.SplitRight(MathF.Min(ui.S(320), head.Width * 0.4f), out head);
        head.SplitRight(ui.S(8), out head);
        if (head.Width > ui.S(420)) ui.Tabs(head.SplitRight(ui.S(300), out head).Shrink(0, ui.S(1)), "pak-group", ref _pakGroup, PakGroups);
        var contents = app.PakContents;
        var shown = contents.Where(c => _pakFilter.Length == 0 || c.Path.Contains(_pakFilter, StringComparison.OrdinalIgnoreCase)
                                        || (c.Owner ?? "").Contains(_pakFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        ui.SectionHeader(head, $"СОДЕРЖИМОЕ   {(shown.Count == contents.Count ? contents.Count : $"{shown.Count} из {contents.Count}")}");
        ui.TextField(filter.Shrink(0, ui.S(1)), "pak-filter", ref _pakFilter, "фильтр: путь или ассет");
        r.SplitTop(ui.S(4), out r);
        if (contents.Count == 0)
        {
            ui.Label(r.SplitTop(rowH, out _), app.Builder.PakExists ? "пак пуст или не прочитан" : "пак ещё не собран — «Собрать пак»", t.TextFaint);
            return;
        }
        DrawLines(ui, r, "pak-contents:" + _pakGroup, PakLines(shown));
    }

    // ── добавление ассета ──────────────────────────────────────────────

    private sealed class AddState
    {
        public string Id = "", Notes = "";
        public int Processor;
        /// <summary>Поле параметров → полные пути через «|».</summary>
        public readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase);
        public string? Error;
        public bool IdTouched;
    }

    private AddState? _add;

    private void OpenAdd()
    {
        if (app.Busy) return;
        var processors = app.Project.Processors.Processors;
        // по умолчанию — обработчик выбранного ассета: следующий ассет чаще всего того же вида
        int index = app.SelectedAsset is { } s ? processors.ToList().FindIndex(p => p.Id == s.Processor) : -1;
        _add = new AddState { Processor = Math.Max(0, index) };
    }

    private static List<(PropertyInfo Prop, SourceAttribute Source)> SourceProps(IAssetProcessor p) =>
        p.ParamsType.GetProperties()
            .Select(prop => (prop, prop.GetCustomAttribute<SourceAttribute>()))
            .Where(x => x.Item2 is not null && (x.prop.PropertyType == typeof(string) || x.prop.PropertyType == typeof(List<string>)))
            .Select(x => (x.prop, x.Item2!))
            .ToList();

    /// <summary>ID из имени выбранного файла или каталога: латиница в нижнем регистре, прочее — «_».</summary>
    private static string SuggestId(string path, string folder)
    {
        string name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')).ToLowerInvariant();
        var chars = name.Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' ? c : '_').ToArray();
        string id = new string(chars).Trim('_', '.');
        if (id.Length == 0) id = "asset";
        return folder.Length > 0 ? $"{folder}/{id}" : id;
    }

    private void DrawAddAsset(UiContext ui)
    {
        if (_add is not { } add) return;
        if (app.Busy)
        {
            _add = null;
            return;
        }
        var t = ui.Theme;
        var processors = app.Project.Processors.Processors;
        add.Processor = Math.Clamp(add.Processor, 0, Math.Max(0, processors.Count - 1));
        var processor = processors.Count > 0 ? processors[add.Processor] : null;
        var sources = processor is null ? [] : SourceProps(processor);

        float rowH = ui.S(34);
        float w = MathF.Min(ui.S(760), ui.Width - ui.S(40));
        float h = ui.S(150) + rowH * (3 + Math.Max(1, sources.Count)) + (add.Error is null ? 0 : ui.S(26));
        var dialog = UiRect.FromSize(MathF.Round((ui.Width - w) * 0.5f), MathF.Round(MathF.Max(ui.S(20), (ui.Height - h) * 0.3f)), w, h);
        ui.BeginModal(dialog);
        var body = dialog.Shrink(ui.S(18), ui.S(16));
        ui.Label(body.SplitTop(ui.S(28), out body), "Добавить ассет", t.Text, UiAlign.Left, UiFace.Head, 18);
        ui.Label(body.SplitTop(ui.S(22), out body),
            "Исходники остаются на месте: в манифест записываются пути (через корни проекта, если файл под корнем).",
            t.TextFaint, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        body.SplitTop(ui.S(8), out body);

        float labelW = ui.S(150);
        UiRect Field(string label, string? tooltip = null)
        {
            var row = body.SplitTop(rowH, out body).Shrink(0, ui.S(3));
            var l = row.SplitLeft(labelW, out row);
            ui.Label(l, label, t.TextDim, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            if (tooltip is not null) ui.Tooltip(l, tooltip);
            return row;
        }

        ui.PushId("add-asset");
        if (processor is null)
        {
            ui.Label(Field("обработчик"), "у проекта нет обработчиков", t.Bad);
        }
        else
        {
            var names = processors.Select(p => $"{p.Id} — {p.Title}").ToList();
            int index = add.Processor;
            if (ui.Combo(Field("обработчик", "Что сделать с исходниками. Обработчики проекта приходят из его плагинов."), "processor", ref index, names))
            {
                add.Processor = index;
                add.Sources.Clear();
            }
        }

        if (sources.Count == 0 && processor is not null)
            ui.Label(Field("исходники"), "у обработчика нет полей-исходников: пути задаются в свойствах после добавления", t.TextFaint);
        foreach (var (prop, source) in sources)
        {
            string name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            bool list = prop.PropertyType == typeof(List<string>);
            var row = Field(name, prop.GetCustomAttribute<DescriptionAttribute>()?.Description);
            var browse = row.SplitRight(ui.S(110), out row);
            row = row with { X1 = row.X1 - ui.S(6) };
            string value = add.Sources.GetValueOrDefault(prop.Name, "");
            string hint = (source.Kind == SourceKind.Directory ? "каталог" : list ? "один или несколько файлов" : "файл") + (source.Filter.Length > 0 ? $" ({source.Filter})" : "");
            if (ui.TextField(row, name, ref value, hint)) add.Sources[prop.Name] = value;
            if (ui.Button(browse, source.Kind == SourceKind.Directory ? "Каталог…" : "Файл…", enabled: !app.DialogOpen, id: name + "-pick"))
            {
                string near = value.Split('|').LastOrDefault(v => v.Length > 0) ?? "";
                var target = add;
                string field = prop.Name;
                app.Pick(source.Kind, $"{name} — новый ассет", list, source.Filter, near.Length > 0 ? near : null, paths =>
                {
                    if (!ReferenceEquals(_add, target)) return;
                    target.Sources[field] = list && target.Sources.GetValueOrDefault(field, "").Length > 0
                        ? target.Sources[field] + "|" + string.Join('|', paths)
                        : string.Join('|', paths);
                    if (!target.IdTouched || target.Id.Length == 0)
                    {
                        string folder = app.SelectedAsset?.Id is { } sel && sel.Contains('/') ? sel[..sel.LastIndexOf('/')] : "";
                        target.Id = SuggestId(paths[0], folder);
                    }
                });
            }
        }

        string id = add.Id;
        if (ui.TextField(Field("ID", "Путь манифеста в каталоге ассетов: сегменты через «/» из a-z, 0-9, _ . -"), "id", ref id, "папка/имя"))
        {
            add.Id = id.Trim();
            add.IdTouched = true;
        }
        string notes = add.Notes;
        if (ui.TextField(Field("пояснение"), "notes", ref notes, "необязательно")) add.Notes = notes;
        ui.PopId();

        if (add.Error is not null)
            ui.Label(body.SplitTop(ui.S(26), out body), add.Error, t.Bad, UiAlign.Left, UiFace.Body, t.SmallFontSize);

        var buttons = body.SplitBottom(ui.S(30), out _);
        bool create = ui.Button(buttons.SplitRight(ui.S(130), out buttons), "Добавить", primary: true,
            enabled: processor is not null && add.Id.Length > 0 && !app.DialogOpen, id: "add-asset-yes");
        buttons.SplitRight(ui.S(8), out buttons);
        bool cancel = ui.Button(buttons.SplitRight(ui.S(110), out buttons), "Отмена", id: "add-asset-no");
        ui.Label(buttons, BakeProject.IsValidAssetId(add.Id) || add.Id.Length == 0 ? "" : "ID: сегменты через «/» из a-z, 0-9, _ . -", t.Warn,
            UiAlign.Left, UiFace.Body, t.SmallFontSize);
        ui.EndModal();

        if (!ui.WantsKeyboard && ui.Input.TakeKey(UiKey.Enter)) create |= processor is not null && add.Id.Length > 0;
        if (ui.Input.TakeKey(UiKey.Escape)) cancel = true;
        if (create && processor is not null)
        {
            var paths = add.Sources.Where(kv => kv.Value.Trim().Length > 0)
                .ToDictionary(kv => kv.Key, kv => string.Join('|', kv.Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            add.Error = app.AddAsset(add.Id, processor.Id, paths, add.Notes);
            if (add.Error is null)
            {
                _add = null;
                _tab = 0;
            }
        }
        else if (cancel) _add = null;
    }

    // ── лог ────────────────────────────────────────────────────────────

    private uint KindColour(UiContext ui, BakeEventKind kind) => kind switch
    {
        BakeEventKind.Started => ui.Theme.Accent,
        BakeEventKind.Finished => ui.Theme.Good,
        BakeEventKind.Warning or BakeEventKind.Skipped or BakeEventKind.Cancelled => ui.Theme.Warn,
        BakeEventKind.Failed => ui.Theme.Bad,
        BakeEventKind.UpToDate => ui.Theme.TextFaint,
        _ => ui.Theme.TextDim,
    };

    private void DrawLog(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        ui.Panel(r);
        r = r.Shrink(ui.S(6));
        var header = r.SplitTop(ui.S(28), out r);
        var controls = header.SplitRight(MathF.Min(header.Width * 0.75f, ui.S(620)), out header);
        ui.SectionHeader(header, $"ЛОГ   {app.Log.Count}");
        if (ui.Button(controls.SplitRight(ui.S(90), out controls).Shrink(0, ui.S(2)), "Очистить", id: "log-clear"))
        {
            app.ClearLog();
            _selectedLog = -1;
        }
        controls.SplitRight(ui.S(16), out controls);
        // переключатели по важности; не влезающий в узкий лог не рисуется, а не ложится на соседа
        if (controls.Width >= ui.S(120)) ui.Toggle(controls.SplitRight(ui.S(120), out controls), "следовать", ref _logFollow, id: "log-follow");
        if (controls.Width >= ui.S(180)) ui.Toggle(controls.SplitRight(ui.S(180), out controls), "только проблемы", ref _logProblemsOnly, id: "log-problems");
        if (controls.Width >= ui.S(185)) ui.Toggle(controls.SplitRight(ui.S(185), out controls), "только выбранный", ref _logSelectedOnly, id: "log-selected");

        int filterKey = HashCode.Combine(_logProblemsOnly, _logSelectedOnly, app.Selected);
        bool grew = false;
        if (_logVersionSeen != app.LogVersion || _logFilterKey != filterKey)
        {
            int before = _logRows.Count;
            _logRows.Clear();
            for (int i = 0; i < app.Log.Count; i++)
            {
                var e = app.Log[i];
                if (_logProblemsOnly && e.Kind is not (BakeEventKind.Failed or BakeEventKind.Warning or BakeEventKind.Skipped or BakeEventKind.Cancelled)) continue;
                if (_logSelectedOnly && e.Asset != app.Selected && e.Asset.Length > 0) continue;
                _logRows.Add(i);
            }
            grew = _logRows.Count > before || _logFilterKey != filterKey;
            _logVersionSeen = app.LogVersion;
            _logFilterKey = filterKey;
            if (_selectedLog >= app.Log.Count) _selectedLog = -1;
        }

        float rowH = ui.S(20);
        if (_logFollow && grew && _logRows.Count > 0)
            ui.ScrollIntoView("log", (_logRows.Count - 1) * rowH, _logRows.Count * rowH, r.Height);
        ui.VirtualList(r, "log", _logRows.Count, rowH, (i, row) =>
        {
            int index = _logRows[i];
            var e = app.Log[index];
            if (ui.Selectable(row, ui.Id(index), index == _selectedLog))
            {
                _selectedLog = index;
                if (e.Asset.Length > 0 && app.Project.Find(e.Asset) is not null) SelectKey(UnitKey(e.Asset));
            }
            uint colour = KindColour(ui, e.Kind);
            var time = row.SplitLeft(ui.S(70), out row);
            ui.Label(time.Shrink(ui.S(6), 0), e.Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture), t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            var dot = row.SplitLeft(ui.S(16), out row);
            if (e.Kind != BakeEventKind.Log) ui.StatusDot(dot.Centre, colour, 3.5f);
            if (e.Asset.Length > 0)
            {
                var assetCell = row.SplitLeft(ui.S(170), out row);
                ui.Label(assetCell.Shrink(ui.S(2), 0), e.Asset, e.Asset == app.Selected ? t.Text : t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            }
            ui.Label(row, e.Text, e.Kind == BakeEventKind.Log ? t.TextDim : colour, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            ui.Tooltip(row, e.Text.Length > 500 ? e.Text[..500] + "…" : e.Text);
        });
    }
}
