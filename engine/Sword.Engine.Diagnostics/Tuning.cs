using System.Globalization;

namespace Sword.Engine.Diagnostics;

/// <summary>Элемент группы ручек: заголовок раздела, число, флаг, кнопка или строка состояния.</summary>
public abstract class TuningItem
{
    public required string Label { get; init; }

    /// <summary>Подсказка: панель показывает её в строке состояния, пока курсор над строкой элемента.</summary>
    public string? Hint { get; init; }
}

/// <summary>Заголовок раздела группы.</summary>
public sealed class TuningHeader : TuningItem;

/// <summary>
/// Число с диапазоном [<see cref="Min"/>, <see cref="Max"/>]. <see cref="Key"/> — имя значения в словаре группы
/// (<see cref="TuningGroup.Values"/>, <see cref="TuningGroup.Apply"/>); пусто — значение не сохраняется.
/// </summary>
public sealed class TuningFloat : TuningItem
{
    public required string Key { get; init; }
    public required Func<float> Get { get; init; }
    public required Action<float> Set { get; init; }
    public float Min { get; init; }
    public float Max { get; init; } = 1;

    /// <summary>Логарифмическая шкала ползунка (при <see cref="Min"/> ≤ 0 ползунок сам берёт линейную).</summary>
    public bool Log { get; init; }

    /// <summary>Целое (<see cref="TuningGroup.Int"/>): значение округляется до целого.</summary>
    public bool Integer { get; init; }

    /// <summary>Формат числа в поле панели (<see cref="float.ToString(string, IFormatProvider)"/>).</summary>
    public string Format { get; init; } = "0.###";

    /// <summary>Значение по умолчанию — к нему возвращает сброс (ПКМ по подписи, «сброс» группы).</summary>
    public float Default { get; init; }

    /// <summary>Значение в пределах ручки: прижато к диапазону, у целой — округлено; не число — <see cref="Default"/>.</summary>
    public float Clamp(float value)
    {
        if (!float.IsFinite(value)) return Default;
        value = Math.Clamp(value, MathF.Min(Min, Max), MathF.Max(Min, Max));
        return Integer ? MathF.Round(value) : value;
    }
}

/// <summary>Флаг. <see cref="Key"/> — имя значения в словаре группы (1 — да, 0 — нет); пусто — не сохраняется.</summary>
public sealed class TuningBool : TuningItem
{
    public required string Key { get; init; }
    public required Func<bool> Get { get; init; }
    public required Action<bool> Set { get; init; }

    /// <summary>Значение по умолчанию — к нему возвращает «сброс» группы.</summary>
    public bool Default { get; init; }
}

/// <summary>Кнопка группы: действие модуля (пересобрать, выбросить, записать трассу…).</summary>
public sealed class TuningButton : TuningItem
{
    public required Action Click { get; init; }
}

/// <summary>
/// Текст строки состояния: пишет его в <paramref name="destination"/> и возвращает длину (не влезло — обрезает сам).
/// Число форматируется на стеке (<c>value.TryFormat(destination, out int n)</c>) — кадр панели без выделений.
/// </summary>
public delegate int TuningText(Span<char> destination);

/// <summary>Строка состояния: счётчики модуля; пересчитывается каждый кадр, пока группа видна.</summary>
public sealed class TuningInfo : TuningItem
{
    public required TuningText Text { get; init; }
}

/// <summary>
/// Группа ручек (вкладка панели F3): модуль описывает свои параметры — живые делегаты чтения и записи, — панель рисует их и
/// ничего о модуле не знает. Откуда значение и куда уходит запись, решает тот, кто собирает группу: при композиции ручки
/// привязываются к объектам настроек, изменение на ходу — команда симуляции (раздел 7 плана, «Конфигурация»).
///
/// Значения по умолчанию и сброс — здесь (<see cref="ResetAll"/>, <see cref="Changed"/>); сохранение — словарём
/// ключ → число (<see cref="Values"/>, <see cref="Apply"/>): файл пишет и читает хост (<c>sword.user.json</c>).
///
/// Перенос <c>Diagnostics/Tuning.cs</c> SWORD 2eab13b. Отличия: <see cref="Save"/>/<see cref="Load"/> файла
/// <c>Tuning/&lt;Id&gt;.json</c> нет (решение пользователя: ручки сохраняются в <c>sword.user.json</c>) — вместо них словарь;
/// ключи в группе уникальны (в SWORD второй ключ молча перезаписывал первый в файле); значение по умолчанию можно задать
/// (в SWORD — всегда значение при создании группы); строка состояния пишет текст на стек (<see cref="TuningText"/>), а не
/// строкой каждый кадр; элементы — только через методы группы.
/// </summary>
public sealed class TuningGroup
{
    private readonly List<TuningItem> _items = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <param name="id">Стабильный Id группы (латиницей): ключ сохранения у хоста, ID виджетов панели.</param>
    /// <param name="title">Заголовок вкладки.</param>
    public TuningGroup(string id, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }

    /// <summary>Элементы по порядку добавления.</summary>
    public IReadOnlyList<TuningItem> Items => _items;

    public TuningGroup Header(string label)
    {
        _items.Add(new TuningHeader { Label = label });
        return this;
    }

    /// <param name="defaultValue">Значение сброса; null — текущее (<paramref name="get"/> при создании), как в SWORD.</param>
    public TuningGroup Float(string key, string label, Func<float> get, Action<float> set, float min, float max, bool log = false,
        string format = "0.###", string? hint = null, float? defaultValue = null)
    {
        CheckRange(key, min, max);
        AddKey(key);
        _items.Add(new TuningFloat
        {
            Key = key, Label = label, Get = get, Set = set, Min = min, Max = max, Log = log, Format = format, Hint = hint,
            Default = defaultValue ?? get(),
        });
        return this;
    }

    /// <summary>Целое: ползунок и поле работают числом, запись — округлённым до целого.</summary>
    public TuningGroup Int(string key, string label, Func<int> get, Action<int> set, int min, int max, string? hint = null,
        int? defaultValue = null)
    {
        CheckRange(key, min, max);
        AddKey(key);
        _items.Add(new TuningFloat
        {
            Key = key, Label = label, Get = () => get(), Set = v => set((int)MathF.Round(v)), Min = min, Max = max, Integer = true,
            Format = "0", Hint = hint, Default = defaultValue ?? get(),
        });
        return this;
    }

    /// <param name="defaultValue">Значение сброса; null — текущее.</param>
    public TuningGroup Bool(string key, string label, Func<bool> get, Action<bool> set, string? hint = null, bool? defaultValue = null)
    {
        AddKey(key);
        _items.Add(new TuningBool { Key = key, Label = label, Get = get, Set = set, Hint = hint, Default = defaultValue ?? get() });
        return this;
    }

    public TuningGroup Button(string label, Action click, string? hint = null)
    {
        _items.Add(new TuningButton { Label = label, Click = click, Hint = hint });
        return this;
    }

    /// <summary>Строка состояния, текст — на стек (кадр панели без выделений).</summary>
    public TuningGroup Info(string label, TuningText text, string? hint = null)
    {
        _items.Add(new TuningInfo { Label = label, Text = text, Hint = hint });
        return this;
    }

    /// <summary>
    /// Строка состояния строкой, как в SWORD: удобно, но <paramref name="text"/> выделяет строку каждый кадр, пока группа
    /// видна; для кадра без выделений — <see cref="Info(string, TuningText, string?)"/>.
    /// </summary>
    public TuningGroup Info(string label, Func<string> text, string? hint = null) => Info(label, destination =>
    {
        var s = text().AsSpan();
        if (s.Length > destination.Length) s = s[..destination.Length];
        s.CopyTo(destination);
        return s.Length;
    }, hint);

    /// <summary>Все значения — к значениям по умолчанию.</summary>
    public void ResetAll()
    {
        foreach (var item in _items)
            switch (item)
            {
                case TuningFloat f: f.Set(f.Default); break;
                case TuningBool b: b.Set(b.Default); break;
            }
    }

    /// <summary>Сколько значений отличается от значений по умолчанию.</summary>
    public int Changed()
    {
        int n = 0;
        foreach (var item in _items)
            n += item switch
            {
                TuningFloat f => f.Get() != f.Default ? 1 : 0,
                TuningBool b => b.Get() != b.Default ? 1 : 0,
                _ => 0,
            };
        return n;
    }

    /// <summary>
    /// Значения с ключами — словарём ключ → число (флаг — 1 или 0), для записи в конфиг. Число float — в кратчайшей записи,
    /// которая читается обратно тем же float (0,3f → 0,3, а не 0,30000001192092896). <paramref name="changedOnly"/> —
    /// только отличные от значений по умолчанию.
    /// </summary>
    public Dictionary<string, double> Values(bool changedOnly = false)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in _items)
            switch (item)
            {
                case TuningFloat f when f.Key.Length > 0:
                {
                    float v = f.Get();
                    if (!changedOnly || v != f.Default) values[f.Key] = Widen(v);
                    break;
                }
                case TuningBool b when b.Key.Length > 0:
                {
                    bool v = b.Get();
                    if (!changedOnly || v != b.Default) values[b.Key] = v ? 1 : 0;
                    break;
                }
            }
        return values;
    }

    /// <summary>
    /// Применить значения словаря (из конфига): незнакомые ключи пропускаются (и попадают в <paramref name="unknown"/>),
    /// не числа — тоже, числа прижимаются к диапазону ручки (<see cref="TuningFloat.Clamp"/>), флаг — «не ноль».
    /// </summary>
    /// <returns>Сколько значений применено.</returns>
    public int Apply(IReadOnlyDictionary<string, double> values, ICollection<string>? unknown = null)
    {
        int applied = 0;
        foreach (var (key, value) in values)
        {
            var item = Find(key);
            if (item is null)
            {
                unknown?.Add(key);
                continue;
            }
            if (!double.IsFinite(value)) continue;
            switch (item)
            {
                case TuningFloat f: f.Set(f.Clamp((float)value)); break;
                case TuningBool b: b.Set(value != 0); break;
            }
            applied++;
        }
        return applied;
    }

    /// <summary>Ручка с ключом <paramref name="key"/> (<see cref="TuningFloat"/> или <see cref="TuningBool"/>); нет — null.</summary>
    public TuningItem? Find(string key)
    {
        if (key.Length == 0) return null;
        foreach (var item in _items)
            if (item is TuningFloat f && f.Key == key || item is TuningBool b && b.Key == key) return item;
        return null;
    }

    /// <summary>Отличия от значений по умолчанию — строкой для журнала («key=значение, …»).</summary>
    public string Describe()
    {
        var parts = new List<string>();
        foreach (var item in _items)
            switch (item)
            {
                case TuningFloat f when f.Get() != f.Default:
                    parts.Add($"{KeyOrLabel(f.Key, f.Label)}={f.Get().ToString(f.Format, CultureInfo.InvariantCulture)}");
                    break;
                case TuningBool b when b.Get() != b.Default:
                    parts.Add($"{KeyOrLabel(b.Key, b.Label)}={(b.Get() ? "true" : "false")}");
                    break;
            }
        return parts.Count == 0 ? "как по умолчанию" : string.Join(", ", parts);
    }

    private static string KeyOrLabel(string key, string label) => key.Length > 0 ? key : label;

    /// <summary>float → double кратчайшей записью float: в конфиге число выглядит так, как его задали.</summary>
    private static double Widen(float v) => double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private void AddKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length > 0 && !_keys.Add(key)) throw new ArgumentException($"ручки {Id}: ключ «{key}» уже есть", nameof(key));
    }

    private void CheckRange(string key, float min, float max)
    {
        if (!(float.IsFinite(min) && float.IsFinite(max) && min < max))
            throw new ArgumentException($"ручки {Id}: у «{key}» диапазон [{min}, {max}] — нужны числа, min < max");
    }
}
