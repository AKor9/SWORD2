using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Форма параметров ассета, построенная по классу параметров обработчика: строка — поле ввода (пусто у необязательной —
/// поле убирается из манифеста), bool — флажок, число — числовое поле, список строк — поле на элемент. Вложенные
/// объекты правятся в самом манифесте. Правки идут в копию; «Сохранить» пишет отличия (<see cref="ManifestWriter"/>).
/// </summary>
internal sealed class ParamsEditor(ProcessorCatalog processors)
{
    private static readonly NullabilityInfoContext Nullability = new();

    private IAssetProcessor? _processor;
    private object? _original, _edited;
    /// <summary>Набранный текст числовых полей: неразобранный остаётся на экране, а в параметры не попадает.</summary>
    private readonly Dictionary<string, string> _numberText = [];
    private string? _error, _saveError;

    public AssetManifest? Asset { get; private set; }
    public bool Dirty { get; private set; }

    /// <summary>Параметры, которые правит форма (тесты: форма туда и обратно без окна).</summary>
    internal object? EditedParams => _edited;

    /// <summary>Правка мимо виджетов (тесты): <see cref="EditedParams"/> изменены — сохранять.</summary>
    internal void MarkEdited() => Dirty = _edited is not null;

    /// <summary>Ошибка разбора параметров или последней записи; null — нет.</summary>
    internal string? Error => _error ?? _saveError;

    /// <summary>
    /// Показать ассет. Тот же ассет, перечитанный с диска (другой экземпляр манифеста), при несохранённых правках
    /// не сбрасывает их.
    /// </summary>
    public void Sync(AssetManifest? asset, StudioApp app)
    {
        if (ReferenceEquals(asset, Asset)) return;
        if (Dirty && asset is not null && Asset is not null && asset.Id == Asset.Id)
        {
            Asset = asset;
            return;
        }
        if (Dirty)
        {
            // уход на другой ассет не теряет правки: сохраняем (при ошибке записи правки остаются, выбор не меняется)
            string? target = asset?.Id;
            Save(app);
            if (Dirty)
            {
                app.AddLog(BakeEventKind.Failed, Asset?.Id ?? "", "правки не сохранены: " + _saveError);
                if (Asset is not null) app.Selected = Asset.Id;
                return;
            }
            asset = target is null ? null : app.Project.Find(target);
        }
        Load(asset);
    }

    private void Load(AssetManifest? asset)
    {
        Asset = asset;
        _processor = asset is null ? null : processors.Find(asset.Processor);
        _original = _edited = null;
        _numberText.Clear();
        _error = _saveError = null;
        Dirty = false;
        if (asset is null) return;
        if (_processor is null)
        {
            _error = $"неизвестный обработчик «{asset.Processor}»";
            return;
        }
        try
        {
            _original = asset.ReadParams(_processor);
            _edited = asset.ReadParams(_processor);
            // незаданные списки — пустые в обоих: форма правит пустой список, и это не должно считаться правкой
            foreach (var p in _processor.ParamsType.GetProperties().Where(p => p.PropertyType == typeof(List<string>) && p.CanWrite))
            {
                p.SetValue(_original, p.GetValue(_original) ?? new List<string>());
                p.SetValue(_edited, p.GetValue(_edited) ?? new List<string>());
            }
        }
        catch (InvalidDataException e)
        {
            _error = e.Message;
        }
    }

    public void Revert() => Load(Asset);

    public void Save(StudioApp app)
    {
        if (!Dirty || Asset is null || _processor is null || _edited is null) return;
        try
        {
            bool written = ManifestWriter.SaveParams(Asset, _processor, _edited, _original);
            string id = Asset.Id;
            app.AddLog(BakeEventKind.Log, id, written ? $"параметры сохранены в {Asset.File}" : "параметры не изменились");
            Dirty = false;
            if (app.Busy)
            {
                // проект под идущей сборкой не перечитывать: запись манифеста заметит наблюдатель после неё
                _original = JsonSerializer.Deserialize(JsonSerializer.Serialize(_edited, _processor.ParamsType, AssetManifest.ParamsJson),
                    _processor.ParamsType, AssetManifest.ParamsJson);
                return;
            }
            app.ReloadProject($"сохранён {id}");
            Load(app.Project.Find(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _saveError = e.Message;
        }
    }

    public static void OpenInEditor(AssetManifest asset)
    {
        try { Process.Start(new ProcessStartInfo(asset.File) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public void Draw(UiContext ui, UiRect area, StudioApp app)
    {
        var t = ui.Theme;
        if (Asset is null) return;

        var bar = area.SplitBottom(ui.S(40), out area);
        bar = bar.Shrink(0, ui.S(6));
        if (ui.Button(bar.SplitLeft(ui.S(140), out bar), "Сохранить  Ctrl+S", primary: true, enabled: Dirty)) Save(app);
        bar.SplitLeft(ui.S(6), out bar);
        if (ui.Button(bar.SplitLeft(ui.S(100), out bar), "Отменить", enabled: Dirty)) Revert();
        bar.SplitLeft(ui.S(6), out bar);
        if (ui.Button(bar.SplitLeft(ui.S(150), out bar), "Открыть манифест")) OpenInEditor(Asset);
        bar.SplitLeft(ui.S(12), out bar);
        if (_saveError is not null) ui.Label(bar, "не сохранено: " + _saveError, t.Bad);
        else if (Dirty) ui.Label(bar, "есть несохранённые правки", t.Warn);

        if (_error is not null || _edited is null || _processor is null)
        {
            ui.Label(area.SplitTop(ui.S(28), out _), _error ?? "нет параметров", t.Bad);
            return;
        }

        var props = _processor.ParamsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).ToList();
        int rows = props.Sum(p => p.PropertyType == typeof(List<string>) ? 1 + ((p.GetValue(_edited) as List<string>)?.Count ?? 0) : 1);
        float rowH = ui.S(32), labelW = ui.S(170);
        // прокрутка у каждого ассета своя: позиция одного не переносится на другой
        ui.PushId(Asset.Id);
        var content = ui.BeginScroll(area, "params", rows * rowH + ui.S(4));
        int n = 0;
        UiRect Row(out UiRect label)
        {
            var row = new UiRect(content.X0, content.Y0 + rowH * n, content.X1, content.Y0 + rowH * ++n).Shrink(0, ui.S(3));
            label = row.SplitLeft(labelW, out row);
            label = label with { X1 = label.X1 - ui.S(10) };
            return row.SplitLeft(MathF.Min(row.Width, ui.S(680)), out _);
        }

        bool changed = false;
        foreach (var p in props)
        {
            string name = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            string? description = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var field = Row(out var label);
            ui.Label(label, name, t.TextDim, UiAlign.Left, UiFace.Num, t.SmallFontSize);
            if (description is not null) ui.Tooltip(label, description);
            object? value = p.GetValue(_edited);
            var type = p.PropertyType;

            var source = p.GetCustomAttribute<SourceAttribute>();
            if (type == typeof(string))
            {
                bool optional = Nullability.Create(p).WriteState == NullabilityState.Nullable;
                string text = (string?)value ?? "";
                if (p.GetCustomAttribute<AssetRefAttribute>() is { } assetRef)
                {
                    // ссылка на другую единицу: выбор из единиц нужного обработчика, «→» — перейти к ней
                    var candidates = app.Project.Assets
                        .Where(a => a.Id != Asset.Id && (assetRef.Processors.Count == 0 || assetRef.Processors.Contains(a.Processor)))
                        .Select(a => a.Id).ToList();
                    var items = new List<string> { optional ? "(не задано)" : "(не выбрано)" };
                    items.AddRange(candidates);
                    if (text.Length > 0 && !candidates.Contains(text)) items.Add(text + "  — нет такого");
                    int index = text.Length == 0 ? 0 : Math.Max(1, items.FindIndex(1, x => x == text || x.StartsWith(text + "  —", StringComparison.Ordinal)));
                    var go = field.SplitRight(ui.S(34), out field);
                    if (ui.Combo(field with { X1 = field.X1 - ui.S(4) }, name, ref index, items) && index <= candidates.Count)
                    {
                        p.SetValue(_edited, index == 0 ? optional ? null : "" : candidates[index - 1]);
                        changed = true;
                    }
                    bool exists = app.Project.Find(text) is not null;
                    if (ui.Button(go, "→", enabled: exists, id: name + "-go")) app.Selected = text;
                    ui.Tooltip(go, exists ? $"Перейти к {text}" : "цели нет");
                    if (description is not null) ui.Tooltip(field, description);
                    continue;
                }
                if (source is not null)
                {
                    var browse = field.SplitRight(ui.S(34), out field);
                    field = field with { X1 = field.X1 - ui.S(4) };
                    if (ui.Button(browse, "…", enabled: !app.DialogOpen, id: name + "-browse")) Browse(app, p, source, text, append: false);
                    ui.Tooltip(browse, source.Kind == SourceKind.Directory ? "Выбрать каталог" : "Выбрать файл");
                }
                if (ui.TextField(field, name, ref text, optional ? "не задано" : ""))
                {
                    p.SetValue(_edited, optional && text.Length == 0 ? null : text);
                    changed = true;
                }
                if (description is not null) ui.Tooltip(field, description);
            }
            else if (type == typeof(bool))
            {
                bool b = (bool)value!;
                if (ui.Toggle(field, description ?? name, ref b, id: name))
                {
                    p.SetValue(_edited, b);
                    changed = true;
                }
            }
            else if (type == typeof(float) || type == typeof(double) || type == typeof(int))
            {
                // Число правится текстом и применяется на каждом вводе, а не при выходе из поля: иначе Ctrl+S
                // или выбор другого ассета, пока поле в фокусе, молча теряли бы набранное.
                var numberField = field.SplitLeft(ui.S(140), out var numberRest);
                string format = type == typeof(int) ? "0" : "0.####";
                if (!_numberText.TryGetValue(name, out var text)) text = Convert.ToDouble(value).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                if (ui.TextField(numberField, name, ref text))
                {
                    _numberText[name] = text;
                    bool ok = double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                              && double.IsFinite(parsed) && (type != typeof(int) || Math.Abs(parsed) <= int.MaxValue);
                    if (ok)
                    {
                        p.SetValue(_edited, type == typeof(int) ? (int)Math.Round(parsed) : type == typeof(double) ? parsed : (float)parsed);
                        changed = true;
                    }
                }
                if (_numberText.TryGetValue(name, out var typed) && !double.TryParse(typed.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    ui.Label(numberRest.Shrink(ui.S(8), 0), "не число — не применено", t.Bad, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            }
            else if (type == typeof(List<string>))
            {
                if (value is not List<string> list)
                {
                    list = [];
                    p.SetValue(_edited, list);   // список не задан в манифесте — правим пустой, пока в нём ничего нет
                }
                ui.Label(field.SplitLeft(field.Width - ui.S(source is null ? 110 : 234), out var add), $"{list.Count} элем.", t.TextFaint);
                if (source is not null)
                {
                    var browse = add.SplitLeft(ui.S(120), out add);
                    add = add with { X0 = add.X0 + ui.S(4) };
                    if (ui.Button(browse, "+ выбрать…", enabled: !app.DialogOpen, id: name + "-browse"))
                        Browse(app, p, source, list.LastOrDefault() ?? "", append: true);
                }
                if (ui.Button(add, "+ добавить", id: name + "+"))
                {
                    // строка нового элемента рисуется тут же, ниже по циклу: высота области прокрутки
                    // отстанет на одну строку ровно до следующего кадра
                    list.Add("");
                    changed = true;
                }
                int remove = -1;
                for (int i = 0; i < list.Count && remove < 0; i++)
                {
                    var itemRow = Row(out var itemLabel);
                    ui.Label(itemLabel with { X0 = itemLabel.X1 - ui.S(40) }, $"[{i}]", t.TextFaint, UiAlign.Right, UiFace.Num, t.SmallFontSize);
                    var del = itemRow.SplitRight(ui.S(30), out itemRow);
                    string item = list[i];
                    if (ui.TextField(itemRow.Shrink(0, 0) with { X1 = itemRow.X1 - ui.S(4) }, $"{name}[{i}]", ref item))
                    {
                        list[i] = item;
                        changed = true;
                    }
                    if (ui.Button(del, "×", id: $"{name}-{i}-del")) remove = i;
                }
                if (remove >= 0)
                {
                    list.RemoveAt(remove);
                    changed = true;
                }
            }
            else
            {
                string json = JsonSerializer.Serialize(value, type, AssetManifest.ParamsJson);
                var open = field.SplitRight(ui.S(150), out var info);
                ui.Label(info, $"{{…}} {json}", t.TextFaint, UiAlign.Left, UiFace.Num, t.SmallFontSize);
                ui.Tooltip(info, json.Length > 400 ? json[..400] + "…" : json);
                if (ui.Button(open, "править в файле", id: name + "-open")) OpenInEditor(Asset);
            }
        }
        ui.EndScroll();
        ui.PopId();

        if (changed) UpdateDirty();
    }

    private void UpdateDirty()
    {
        if (_edited is null || _processor is null) return;
        Dirty = JsonSerializer.Serialize(_edited, _processor.ParamsType, AssetManifest.ParamsJson)
                != JsonSerializer.Serialize(_original, _processor.ParamsType, AssetManifest.ParamsJson);
    }

    /// <summary>
    /// Выбрать исходник системным окном: путь записывается через корни проекта (<see cref="SourceRoots.Contract"/>).
    /// Выбор приходит позже, на потоке окна; за это время мог быть выбран другой ассет — тогда он не применяется.
    /// </summary>
    private void Browse(StudioApp app, PropertyInfo p, SourceAttribute source, string current, bool append)
    {
        var asset = Asset;
        string? near = null;
        if (current.Length > 0)
            try { near = app.Project.Roots.Expand(current); }
            catch (ArgumentException) { }
        app.Pick(source.Kind, $"{JsonNamingPolicy.CamelCase.ConvertName(p.Name)} — {asset?.Id}", multiple: append, source.Filter, near, paths =>
        {
            if (!ReferenceEquals(asset, Asset) && asset?.Id != Asset?.Id || _edited is null) return;
            var contracted = paths.Select(app.Project.Roots.Contract).ToList();
            if (append && p.GetValue(_edited) is List<string> list) list.AddRange(contracted);
            else if (append) p.SetValue(_edited, contracted);
            else p.SetValue(_edited, contracted[0]);
            UpdateDirty();
        });
    }
}
