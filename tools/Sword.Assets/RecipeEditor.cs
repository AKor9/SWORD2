using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Sword.Bake;
using Sword.Engine.Ui;

namespace Sword.Assets;

/// <summary>
/// Форма рецепта, построенная по классу параметров конвертера: строка — поле ввода (пусто у необязательной — поле
/// убирается из рецепта), bool — флажок, число — числовое поле, список строк — поле на элемент, остальное (наборы
/// анимации, зоны крови) — JSON одной строкой. Путь к сырому файлу — с кнопкой выбора, пишется через корни
/// (<c>${src}/…</c>). Правки живут в форме; «Испечь» пишет рецепт вместе с выпечкой.
/// </summary>
internal sealed class RecipeEditor
{
    private static readonly NullabilityInfoContext Nullability = new();
    private static readonly JsonSerializerOptions OneLine = new(Recipe.Json) { WriteIndented = false };

    private IConverter? _converter;
    private object? _edited;
    private int _version = -1;
    /// <summary>Набранный текст числовых и JSON-полей: неразобранный остаётся на экране, а в параметры не попадает.</summary>
    private readonly Dictionary<string, string> _typed = [];
    private string? _error;

    /// <summary>Параметры формы (тесты: форма без окна).</summary>
    internal object? Params => _edited;

    internal string? Error => _error;

    /// <summary>Перечитать параметры, если рецепт окна сменился (другой файл или конвертер).</summary>
    public void Sync(AssetsApp app)
    {
        if (_version == app.RecipeVersion) return;
        _version = app.RecipeVersion;
        _typed.Clear();
        _error = null;
        _edited = null;
        _converter = app.Recipe is { } r ? Baker.Find(r.Converter) : null;
        if (_converter is null || app.Recipe is null) return;
        try
        {
            _edited = app.Recipe.ReadParams(_converter.ParamsType);
        }
        catch (InvalidDataException e)
        {
            _error = e.Message;
        }
    }

    public void Draw(UiContext ui, UiRect area, AssetsApp app)
    {
        var t = ui.Theme;
        if (_error is not null || _edited is null || _converter is null)
        {
            ui.Label(area.SplitTop(ui.S(28), out _), _error ?? "нет параметров", t.Bad);
            return;
        }

        var props = _converter.ParamsType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).ToList();
        int rows = props.Sum(p => p.PropertyType == typeof(List<string>) ? 1 + ((p.GetValue(_edited) as List<string>)?.Count ?? 0) : 1);
        float rowH = ui.S(32), labelW = ui.S(170);
        // прокрутка у каждого файла своя: позиция одного не переносится на другой
        ui.PushId(app.Source ?? "");
        var content = ui.BeginScroll(area, "params", rows * rowH + ui.S(4));
        int n = 0;
        UiRect Row(out UiRect label)
        {
            var row = new UiRect(content.X0, content.Y0 + rowH * n, content.X1, content.Y0 + rowH * ++n).Shrink(0, ui.S(3));
            label = row.SplitLeft(labelW, out row);
            label = label with { X1 = label.X1 - ui.S(10) };
            return row.SplitLeft(MathF.Min(row.Width, ui.S(680)), out _);
        }

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
                if (source is not null)
                {
                    var browse = field.SplitRight(ui.S(34), out field);
                    field = field with { X1 = field.X1 - ui.S(4) };
                    if (ui.Button(browse, "…", enabled: !app.DialogOpen, id: name + "-browse")) Browse(app, p, source, text, append: false);
                    ui.Tooltip(browse, source.Directory ? "Выбрать каталог" : "Выбрать файл");
                }
                if (ui.TextField(field, name, ref text, optional ? "не задано" : ""))
                {
                    p.SetValue(_edited, optional && text.Length == 0 ? null : text);
                }
                if (description is not null) ui.Tooltip(field, description);
            }
            else if (type == typeof(bool))
            {
                bool b = (bool)value!;
                if (ui.Toggle(field, description ?? name, ref b, id: name))
                {
                    p.SetValue(_edited, b);
                }
            }
            else if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(float?))
            {
                // Число правится текстом и применяется на каждом вводе, а не при выходе из поля: иначе Ctrl+S
                // или выбор другого ассета, пока поле в фокусе, молча теряли бы набранное.
                var numberField = field.SplitLeft(ui.S(140), out var numberRest);
                string format = type == typeof(int) ? "0" : "0.####";
                if (!_typed.TryGetValue(name, out var text)) text = value is null ? "" : Convert.ToDouble(value).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                if (ui.TextField(numberField, name, ref text))
                {
                    _typed[name] = text;
                    if (type == typeof(float?) && text.Trim().Length == 0)
                    {
                        p.SetValue(_edited, null);
                    }
                    bool ok = double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                              && double.IsFinite(parsed) && (type != typeof(int) || Math.Abs(parsed) <= int.MaxValue);
                    if (ok)
                    {
                        p.SetValue(_edited, type == typeof(int) ? (int)Math.Round(parsed) : type == typeof(double) ? parsed : (float)parsed);
                    }
                }
                if (_typed.TryGetValue(name, out var typed) && !(type == typeof(float?) && typed.Trim().Length == 0) && !double.TryParse(typed.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    ui.Label(numberRest.Shrink(ui.S(8), 0), "не число — не применено", t.Bad, UiAlign.Left, UiFace.Body, t.SmallFontSize);
            }
            else if (type == typeof(List<string>))
            {
                if (value is not List<string> list)
                {
                    list = [];
                    p.SetValue(_edited, list);   // список не задан в рецепте — правим пустой, пока в нём ничего нет
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
                    }
                    if (ui.Button(del, "×", id: $"{name}-{i}-del")) remove = i;
                }
                if (remove >= 0)
                {
                    list.RemoveAt(remove);
                }
            }
            else
            {
                // наборы анимации, зоны крови — JSON одной строкой; применяется, когда разобрался
                if (!_typed.TryGetValue(name, out var json)) json = JsonSerializer.Serialize(value, type, OneLine);
                if (ui.TextField(field, name, ref json))
                {
                    _typed[name] = json;
                    try
                    {
                        p.SetValue(_edited, JsonSerializer.Deserialize(json, type, Recipe.Json));
                    }
                    catch (JsonException) { }
                }
                if (description is not null) ui.Tooltip(field, description);
            }
        }
        ui.EndScroll();
        ui.PopId();

    }

    /// <summary>
    /// Выбрать сырой файл системным окном: путь записывается через корни (<see cref="SourceRoots.Contract"/>). Выбор приходит
    /// позже, на потоке окна; за это время мог смениться рецепт — тогда он не применяется.
    /// </summary>
    private void Browse(AssetsApp app, PropertyInfo p, SourceAttribute source, string current, bool append)
    {
        int version = _version;
        string? near = app.Source;
        if (current.Length > 0)
            try { near = app.Baker.Roots.Expand(current); }
            catch (ArgumentException) { }
        app.Pick(JsonNamingPolicy.CamelCase.ConvertName(p.Name), source.Directory, source.Filter, near, paths =>
        {
            if (version != _version || _edited is null) return;
            var contracted = paths.Select(app.Baker.Roots.Contract).ToList();
            if (append && p.GetValue(_edited) is List<string> list) list.AddRange(contracted);
            else if (append) p.SetValue(_edited, contracted);
            else p.SetValue(_edited, contracted[0]);
        }, multiple: append);
    }
}
