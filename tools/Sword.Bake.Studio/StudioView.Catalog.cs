using System.Numerics;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Виды левой панели и карточки центра, которых нет у единицы сборки: каталог контента (пути пака — то, что видят
/// игра и человек), единицы сборки (манифесты), импорты (происхождение) и обработчики. Все деревья — по сегментам
/// пути, с настоящей вложенностью. Выбор — пара «вид + ID» (<see cref="SelectKey"/>): u: — единица, c: — ассет
/// контента, i: — импорт, p: — обработчик.
/// </summary>
internal sealed partial class StudioView
{
    private enum LeftView { Content, Units, Imports, Processors }

    private enum Centre { Project, Unit, Content, Import, Processor }

    private static readonly string[] ViewNames = ["Контент", "Сборка", "Импорты", "Обработчики"];
    private static readonly string[] PakGroups = ["файлы", "по контенту", "по импорту"];
    private static readonly string[] ContentTabs = ["Описание", "Просмотр"];
    private const string NoImport = "(без импорта)";

    private LeftView _view = LeftView.Content;
    private string? _contentSel, _importSel, _processorSel;
    private int _pakGroup, _contentTab;
    private readonly Dictionary<string, float> _pageHeight = [];

    // строки дерева текущего кадра
    private UiRect _rowArea;
    private float _rowHeight;
    private int _rowCount;

    private UiRect Row() => new(_rowArea.X0, _rowArea.Y0 + _rowHeight * _rowCount, _rowArea.X1, _rowArea.Y0 + _rowHeight * ++_rowCount);

    private string FilterHint => _view switch
    {
        LeftView.Content => "фильтр: путь в паке или производитель",
        LeftView.Units => "фильтр: ID или обработчик",
        LeftView.Imports => "фильтр: импорт, коллекция, лицензия, единица",
        _ => "фильтр: обработчик или единица",
    };

    private bool Matches(params string?[] texts) =>
        _filter.Length == 0 || texts.Any(s => s is not null && s.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    // ── выбор ──────────────────────────────────────────────────────────

    private static string UnitKey(string id) => "u:" + id;

    private string SelectedKey => _centre switch
    {
        Centre.Unit => UnitKey(app.Selected ?? ""),
        Centre.Content => "c:" + _contentSel,
        Centre.Import => "i:" + _importSel,
        Centre.Processor => "p:" + _processorSel,
        _ => "",
    };

    private void SelectKey(string key)
    {
        string id = key[2..];
        switch (key[0])
        {
            case 'u':
                app.Selected = id;
                _selectedSeen = id;
                _centre = Centre.Unit;
                break;
            case 'c':
                _contentSel = id;
                _centre = Centre.Content;
                break;
            case 'i':
                _importSel = id;
                _centre = Centre.Import;
                break;
            case 'p':
                _processorSel = id;
                _centre = Centre.Processor;
                break;
        }
    }

    /// <summary>Выбор из командной строки: «content:ПУТЬ», «import:ИМЯ», «processor:ID» или ID единицы — вместе с видом слева.</summary>
    private void ApplySelect(string select)
    {
        (string Prefix, LeftView View, char Key)[] kinds =
            [("content:", LeftView.Content, 'c'), ("import:", LeftView.Imports, 'i'), ("processor:", LeftView.Processors, 'p')];
        foreach (var (prefix, view, key) in kinds)
            if (select.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _view = view;
                SelectKey($"{key}:{select[prefix.Length..]}");
                if (key == 'c' && tab == 3) _contentTab = 1;   // --tab 3 — «Просмотр», как у единицы
                _scrollTreeTo = SelectedKey;
                return;
            }
        if (app.Project.Find(select) is { } unit)
        {
            _view = LeftView.Units;
            app.Selected = unit.Id;
            if (_centre != Centre.Project) _centre = Centre.Unit;
            _scrollTreeTo = UnitKey(unit.Id);
        }
    }

    // ── статусы и подписи ──────────────────────────────────────────────

    /// <summary>Худший статус производителей ассета контента: его и показывает точка.</summary>
    private string? WorstProducer(ContentAsset c) =>
        c.Producers.OrderByDescending(p => app.StatusOf(p).Status switch
        {
            AssetStatus.UpToDate => 0,
            AssetStatus.Dirty => 1,
            AssetStatus.NoSource => 2,
            _ => 3,
        }).FirstOrDefault();

    private (uint Colour, string Label) ContentLook(UiContext ui, ContentAsset c) =>
        WorstProducer(c) is { } p ? StatusLook(ui, p) : (ui.Theme.TextFaint, "нет производителя");

    private static string KindText(ContentAsset c) =>
        c.IsSet ? $"набор · {c.Files.Count}" : string.Join(" ", c.Files.Select(f => Path.GetExtension(f).TrimStart('.')));

    private string ImportLabel(AssetManifest a) =>
        a.Import.Length == 0 ? "не указан"
        : app.Project.Imports.TryGetValue(a.Import, out var r) ? $"{a.Import} — {r.Collection}"
        : $"{a.Import} (нет в {ImportRegistry.FileName})";

    private string ImportOf(AssetManifest a) => a.Import.Length > 0 && app.Project.Imports.ContainsKey(a.Import) ? a.Import : NoImport;

    // ── деревья ────────────────────────────────────────────────────────

    /// <summary>
    /// Дерево по сегментам путей: папки — узлы со счётчиком (и числом проблем), элементы — листья, которые рисует
    /// <paramref name="leaf"/> (элемент, глубина, имя без папки). Свёрнутая папка прячет всё под собой.
    /// </summary>
    private void PathTree<T>(UiContext ui, string prefix, List<(string Path, T Item)> items, Func<T, bool> problem, Action<T, int, string> leaf)
    {
        var t = ui.Theme;
        var counts = new Dictionary<string, (int All, int Bad)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, item) in items)
        {
            var parts = path.Split('/');
            bool bad = problem(item);
            for (int k = 1; k < parts.Length; k++)
            {
                string folder = string.Join('/', parts[..k]);
                counts.TryGetValue(folder, out var c);
                counts[folder] = (c.All + 1, c.Bad + (bad ? 1 : 0));
            }
        }
        string[] open = [];
        string? closed = null;
        foreach (var (path, item) in items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (closed is not null && path.StartsWith(closed, StringComparison.OrdinalIgnoreCase)) continue;
            closed = null;
            var parts = path.Split('/');
            var folders = parts[..^1];
            int common = 0;
            while (common < open.Length && common < folders.Length && open[common].Equals(folders[common], StringComparison.OrdinalIgnoreCase)) common++;
            open = open[..common];
            bool hidden = false;
            for (int level = common; level < folders.Length; level++)
            {
                string folder = string.Join('/', folders[..(level + 1)]);
                var (all, bad) = counts[folder];
                string text = bad > 0 && app.StatusesKnown ? $"{folders[level]}   {all} · проблем {bad}" : $"{folders[level]}   {all}";
                if (!ui.TreeNode(Row(), prefix + "dir:" + folder, text, level, leaf: false, selected: false, out _, defaultOpen: true, textColour: t.Accent))
                {
                    closed = folder + "/";
                    hidden = true;
                    break;
                }
                open = [.. open, folders[level]];
            }
            if (!hidden) leaf(item, folders.Length, parts[^1]);
        }
    }

    /// <summary>Лист дерева: точка статуса, имя, подпись справа (если влезает); щелчок — выбор, двойной — <paramref name="doubleClick"/>.</summary>
    private void Leaf(UiContext ui, string key, string name, int depth, uint dot, string tail, string tooltip, Action? doubleClick = null)
    {
        var t = ui.Theme;
        _treeRowOf[key] = _rowCount;
        var row = Row();
        ui.TreeNode(row, key, name, depth, leaf: true, selected: SelectedKey == key, out bool clicked, dot: dot);
        Tail(ui, row, name, depth, tail);
        ui.Tooltip(row, tooltip);
        if (clicked)
        {
            SelectKey(key);
            if (ui.DoubleClicked) doubleClick?.Invoke();
        }
        _treeOrder.Add(key);
    }

    /// <summary>Подпись справа от имени — только если не наезжает на него (отступ — как у TreeNode: уровень, стрелка, точка).</summary>
    private static void Tail(UiContext ui, UiRect row, string name, int depth, string tail)
    {
        if (tail.Length == 0) return;
        var t = ui.Theme;
        float nameEnd = row.X0 + ui.S(14 * depth + 16 + 2 + 14) + ui.Draw.Measure(UiFace.Body, ui.S(t.FontSize), name);
        float width = ui.Draw.Measure(UiFace.Num, ui.S(t.SmallFontSize), tail) + ui.S(10);
        if (nameEnd + ui.S(10) <= row.X1 - width)
            ui.Label(row with { X0 = row.X1 - width, X1 = row.X1 - ui.S(4) }, tail, t.TextFaint, UiAlign.Right, UiFace.Num, t.SmallFontSize);
    }

    private void DrawContentTree(UiContext ui)
    {
        var catalog = app.Builder.Catalog();
        var items = catalog.Assets.Where(c => Matches([c.Id, .. c.Producers])).Select(c => (c.Id, c)).ToList();
        PathTree(ui, "c:", items, c => WorstProducer(c) is { } p && app.StatusOf(p).Status != AssetStatus.UpToDate, (c, depth, name) =>
        {
            var (colour, label) = ContentLook(ui, c);
            Leaf(ui, "c:" + c.Id, name, depth, colour, KindText(c), $"{label} · делает {string.Join(", ", c.Producers)}");
        });
    }

    private void UnitLeaf(UiContext ui, AssetManifest a, string name, int depth, string tail)
    {
        var (colour, label) = StatusLook(ui, a.Id);
        Leaf(ui, UnitKey(a.Id), name, depth, colour, tail, $"{label}: {app.StatusOf(a.Id).Reason}", () =>
        {
            if (!app.Busy) app.StartBuild([a], force: false, "сборка " + a.Id);
        });
    }

    private void DrawUnitTree(UiContext ui)
    {
        var items = app.Project.Assets.Where(a => Matches(a.Id, a.Processor)).Select(a => (a.Id, a)).ToList();
        PathTree(ui, "u:", items, a => app.StatusOf(a.Id).Status != AssetStatus.UpToDate, (a, depth, name) => UnitLeaf(ui, a, name, depth, a.Processor));
    }

    /// <summary>Узел-группа (импорт, обработчик): выбирается щелчком, раскрывается стрелкой; под ним — его единицы.</summary>
    private void GroupWithUnits(UiContext ui, string key, string text, uint dot, List<AssetManifest> units, Func<AssetManifest, string> tail)
    {
        _treeRowOf[key] = _rowCount;
        bool open = ui.TreeNode(Row(), key, text, 0, leaf: units.Count == 0, selected: SelectedKey == key, out bool clicked, dot: dot);
        if (clicked) SelectKey(key);
        _treeOrder.Add(key);
        if (!open) return;
        foreach (var a in units) UnitLeaf(ui, a, a.Id, 1, tail(a));
    }

    private void DrawImportTree(UiContext ui)
    {
        var t = ui.Theme;
        var problems = app.Project.ProvenanceIssues();
        var ids = app.Project.Imports.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (app.Project.Assets.Any(a => ImportOf(a) == NoImport)) ids.Add(NoImport);
        foreach (var id in ids)
        {
            var record = app.Project.Imports.GetValueOrDefault(id);
            var units = app.Project.Assets.Where(a => ImportOf(a) == id).ToList();
            if (!Matches([id, record?.Collection, record?.License, .. units.Select(u => u.Id)])) continue;
            bool bad = id == NoImport || problems.Any(p => p.AssetId is null && p.Text.StartsWith($"импорт {id}:", StringComparison.Ordinal));
            GroupWithUnits(ui, "i:" + id, $"{id}   {units.Count}", bad ? t.Warn : t.Good, units, a => a.Processor);
        }
    }

    private void DrawProcessorTree(UiContext ui)
    {
        var t = ui.Theme;
        foreach (var p in app.Project.Processors.Processors)
        {
            var units = app.Project.Assets.Where(a => a.Processor == p.Id).ToList();
            if (!Matches([p.Id, p.Title, .. units.Select(u => u.Id)])) continue;
            bool bad = units.Any(a => app.StatusOf(a.Id).Status != AssetStatus.UpToDate);
            GroupWithUnits(ui, "p:" + p.Id, $"{p.Id}   {units.Count}", units.Count == 0 ? t.TextFaint : bad ? t.Warn : t.Good, units, a => ImportOf(a));
        }
        var unknown = app.Project.Assets.Where(a => app.Project.Processors.Find(a.Processor) is null && Matches(a.Id, a.Processor)).ToList();
        if (unknown.Count > 0) GroupWithUnits(ui, "p:?", $"(неизвестные)   {unknown.Count}", t.Bad, unknown, a => a.Processor);
    }

    // ── карточки центра ────────────────────────────────────────────────

    private void Title(UiContext ui, ref UiRect r, string title, string subtitle)
    {
        var t = ui.Theme;
        var row = r.SplitTop(ui.S(32), out r);
        float w = ui.Label(row, title, t.Text, UiAlign.Left, UiFace.Head, 19);
        ui.Label(row with { X0 = row.X0 + w + ui.S(14) }, subtitle, t.TextDim);
    }

    /// <summary>Страница с прокруткой: высота содержимого известна после отрисовки и запоминается по ключу.</summary>
    private void Page(UiContext ui, UiRect view, string key, Action<Func<float, UiRect>> body)
    {
        float height = _pageHeight.GetValueOrDefault(key, 1000);
        var content = ui.BeginScroll(view, "page:" + key, height);
        var r = content with { Y1 = content.Y0 + 1e5f };
        float top = r.Y0;
        body(h => r.SplitTop(h, out r));
        _pageHeight[key] = r.Y0 - top;
        ui.EndScroll();
    }

    /// <summary>Строка-ссылка: точка, текст, щелчок — выбрать <paramref name="key"/>.</summary>
    private void Link(UiContext ui, UiRect row, string key, uint dot, string text, uint? colour = null)
    {
        var t = ui.Theme;
        if (ui.Selectable(row, ui.Id("link:" + key + "|" + text), false)) SelectKey(key);
        ui.StatusDot(new Vector2(row.X0 + ui.S(12), row.Centre.Y), dot);
        ui.Label(row with { X0 = row.X0 + ui.S(24) }, text, colour ?? t.TextDim);
        ui.Tooltip(row, text);
    }

    private void Text(UiContext ui, UiRect row, string text, uint? colour = null)
    {
        var t = ui.Theme;
        ui.Label(row.Shrink(ui.S(8), 0), text, colour ?? t.TextDim, UiAlign.Left, UiFace.Body, t.SmallFontSize);
        ui.Tooltip(row, text);
    }

    private void DrawContentDetails(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        var catalog = app.Builder.Catalog();
        var c = catalog.Assets.FirstOrDefault(x => x.Id.Equals(_contentSel, StringComparison.OrdinalIgnoreCase));
        if (c is null)
        {
            Title(ui, ref r, _contentSel ?? "", "ассет контента");
            ui.Label(r.SplitTop(ui.S(28), out _), "Такого ассета контента больше нет — выберите другой в дереве.", t.TextFaint);
            return;
        }
        var sizes = app.PakContents.ToDictionary(e => e.Path, e => e.Size, StringComparer.OrdinalIgnoreCase);
        long total = c.Files.Sum(f => sizes.GetValueOrDefault(f));
        Title(ui, ref r, c.Id, $"{(c.IsSet ? "набор" : "ассет контента")} · файлов {c.Files.Count} · {Size(total)}");
        var statusRow = r.SplitTop(ui.S(24), out r);
        var (colour, label) = ContentLook(ui, c);
        ui.StatusDot(new Vector2(statusRow.X0 + ui.S(5), statusRow.Centre.Y), colour);
        ui.Label(statusRow with { X0 = statusRow.X0 + ui.S(16) }, WorstProducer(c) is { } worst && app.StatusOf(worst).Reason != label ? $"{label}: {app.StatusOf(worst).Reason}" : label, t.TextDim);
        r.SplitTop(ui.S(4), out r);
        ui.Tabs(r.SplitTop(ui.S(30), out r), "content-tabs", ref _contentTab, ContentTabs);
        r.SplitTop(ui.S(8), out r);
        if (_contentTab == 1)
        {
            _preview.DrawContent(ui, r, app, c);
            return;
        }

        float rowH = ui.S(t.RowHeight);
        var imports = c.Producers.Select(app.Project.Find).OfType<AssetManifest>().Select(ImportOf).Distinct().ToList();
        float topH = MathF.Min(r.Height * 0.55f, ui.S(26) * 2 + rowH * (c.Producers.Count + imports.Count) + ui.S(20));
        var top = r.SplitTop(topH, out var files);
        Page(ui, top, "content-top:" + c.Id, take =>
        {
            ui.SectionHeader(take(ui.S(26)), $"ПРОИЗВОДИТЕЛИ   {c.Producers.Count}");
            foreach (var p in c.Producers)
            {
                var unit = app.Project.Find(p);
                var (dot, status) = StatusLook(ui, p);
                Link(ui, take(rowH), UnitKey(p), dot, $"{p}   ·   {unit?.Processor ?? "удалён"}   ·   {status}");
            }
            ui.SectionHeader(take(ui.S(26)), "ПРОИСХОЖДЕНИЕ");
            foreach (var id in imports)
            {
                if (id == NoImport)
                {
                    Text(ui, take(rowH), $"не указано — поле import у {string.Join(", ", c.Producers)}", t.Warn);
                    continue;
                }
                var rec = app.Project.Imports[id];
                Link(ui, take(rowH), "i:" + id, t.Good, $"{id} — {rec.Collection}{(rec.Source.Length > 0 ? ", " + rec.Source : "")}, {(rec.License.Length > 0 ? rec.License : "лицензия не указана")}");
            }
        });
        files.SplitTop(ui.S(6), out files);
        var lines = new List<Line> { new(0, $"ФАЙЛЫ   {c.Files.Count}") };
        foreach (var f in c.Files)
            lines.Add(sizes.TryGetValue(f, out long size) ? new Line(1, f, size) : new Line(3, f, -1, "ещё не в паке"));
        DrawLines(ui, files, "content-files:" + c.Id, lines);
    }

    private void DrawImportDetails(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        string id = _importSel ?? "";
        var record = app.Project.Imports.GetValueOrDefault(id);
        var units = app.Project.Assets.Where(a => ImportOf(a) == id).ToList();
        Title(ui, ref r, id, record is null ? (id == NoImport ? "единицы без известного происхождения" : "нет в реестре") : $"импорт · {record.Collection}");
        float rowH = ui.S(t.RowHeight);
        var catalog = app.Builder.Catalog();
        Page(ui, r, "import:" + id, take =>
        {
            if (record is not null)
            {
                ui.SectionHeader(take(ui.S(26)), $"ПРОИСХОЖДЕНИЕ   ({ImportRegistry.FileName})");
                void Field(string name, string value) => Text(ui, take(rowH), $"{name}: {(value.Length > 0 ? value : "—")}", value.Length > 0 ? null : t.TextFaint);
                Field("коллекция", record.Collection);
                Field("откуда", record.Source);
                Field("ссылка", record.Url);
                Field("лицензия", record.License);
                Field("конвейер", string.Join(", ", record.Pipeline));
                if (record.Notes.Length > 0) Field("заметки", record.Notes);
                foreach (var f in record.LicenseFiles)
                {
                    string? full = null;
                    try { full = app.Project.Roots.Expand(f); }
                    catch (ArgumentException) { }
                    bool ok = full is not null && File.Exists(full);
                    Text(ui, take(rowH), $"файл лицензии: {f}{(ok ? "" : "  — нет файла")}", ok ? null : t.Bad);
                }
                if (record.LicenseFiles.Count == 0) Text(ui, take(rowH), "файлов лицензии нет", t.TextFaint);
            }
            else if (id == NoImport)
                Text(ui, take(rowH), $"Этим единицам нужно поле import со ссылкой на запись в {ImportRegistry.FileName}.", t.Warn);
            take(ui.S(8));
            ui.SectionHeader(take(ui.S(26)), $"ЕДИНИЦЫ СБОРКИ   {units.Count}");
            foreach (var a in units)
            {
                var (dot, status) = StatusLook(ui, a.Id);
                Link(ui, take(rowH), UnitKey(a.Id), dot, $"{a.Id}   ·   {a.Processor}   ·   {status}");
            }
            var content = units.SelectMany(a => catalog.ProducedBy(a.Id)).DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
            take(ui.S(8));
            ui.SectionHeader(take(ui.S(26)), $"АССЕТЫ КОНТЕНТА   {content.Count}");
            foreach (var c in content)
            {
                var (dot, _) = ContentLook(ui, c);
                Link(ui, take(rowH), "c:" + c.Id, dot, $"{c.Id}   ·   {KindText(c)}");
            }
        });
    }

    private void DrawProcessorDetails(UiContext ui, UiRect r)
    {
        var t = ui.Theme;
        string id = _processorSel ?? "";
        var processor = app.Project.Processors.Find(id);
        var units = app.Project.Assets.Where(a => processor is null ? app.Project.Processors.Find(a.Processor) is null : a.Processor == id).ToList();
        Title(ui, ref r, processor?.Id ?? "неизвестные обработчики", processor is null ? "обработчиков этих единиц нет среди встроенных и в плагинах" : $"обработчик · {processor.Title}");
        float rowH = ui.S(t.RowHeight);
        Page(ui, r, "processor:" + id, take =>
        {
            if (processor is not null)
            {
                Text(ui, take(rowH), $"версия {processor.Version} · {(processor.Parallel ? "собирается параллельно" : "по одному ассету")}" +
                                     $"{(processor.Incremental ? " · инкрементальный" : "")} · предпросмотр: {(processor is IAssetPreview ? "есть" : "нет")}");
                Text(ui, take(rowH), $"сборка: {processor.GetType().Assembly.GetName().Name}");
                take(ui.S(8));
            }
            ui.SectionHeader(take(ui.S(26)), $"ЕДИНИЦЫ СБОРКИ   {units.Count}");
            foreach (var a in units)
            {
                var (dot, status) = StatusLook(ui, a.Id);
                Link(ui, take(rowH), UnitKey(a.Id), dot, $"{a.Id}   ·   импорт: {ImportLabel(a)}   ·   {status}");
            }
            if (units.Count == 0) Text(ui, take(rowH), "в проекте нет единиц с этим обработчиком", t.TextFaint);
        });
    }

    // ── содержимое пака ────────────────────────────────────────────────

    /// <summary>Строки содержимого пака: файлами, по ассету контента или по импорту производителя.</summary>
    private List<Line> PakLines(List<PakContentEntry> shown)
    {
        if (_pakGroup == 0) return shown.Select(c => new Line(c.Owner is null ? 3 : 1, c.Path, c.Size, c.Owner ?? "без ассета")).ToList();
        var catalog = app.Builder.Catalog();
        string GroupOf(PakContentEntry e) => _pakGroup == 1
            ? catalog.Find(e.Path)?.Id ?? "(вне каталога)"
            : e.Owner is { } owner && app.Project.Find(owner) is { } unit ? ImportOf(unit) : "(без единицы)";
        var lines = new List<Line>();
        foreach (var group in shown.GroupBy(GroupOf).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(new Line(0, $"{group.Key}   ·   файлов {group.Count()}, {Size(group.Sum(e => e.Size))}"));
            foreach (var e in group) lines.Add(new Line(e.Owner is null ? 3 : 1, e.Path, e.Size, e.Owner ?? "без ассета"));
        }
        return lines;
    }
}
