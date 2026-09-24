using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Ключ ручки (раздел 7 плана, «Конфигурация»; точка 6е): стабильный хеш полного имени ручки «секция.ключ» — того же ключа
/// конфига (<c>combat.playerDamage</c>), без учёта регистра. В команде и реплее — число (от порядка регистрации ручек и от
/// набора модулей не зависит); имя по числу знают ручки, заведённые в процессе (<see cref="Name"/>) — агент пишет и читает ручку
/// именем (<c>command.send</c>, <c>events.history</c>).
/// </summary>
public struct TuningKey : IEquatable<TuningKey>
{
    public ulong Hash;

    public TuningKey(ulong hash) => Hash = hash;

    // Имена заведённых ручек по ключу — общие для процесса (ветки experiment.fork — потоки одного процесса): отображение
    // «имя → хеш» у всех симуляций одно, поэтому общий словарь только растёт и не расходится.
    private static readonly ConcurrentDictionary<ulong, string> Names = new();

    /// <summary>Ключ имени ручки «секция.ключ» (регистр не важен); ручку он не заводит.</summary>
    public static TuningKey Of(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new TuningKey(StateHasher.Of(name.ToLowerInvariant()));
    }

    /// <summary>Имя ручки этого ключа (как её завели); ключ, который в процессе не заводился, — null.</summary>
    public readonly string? Name => Names.TryGetValue(Hash, out var name) ? name : null;

    /// <summary>Заведённая ручка по имени (регистр не важен); нет такой — false.</summary>
    public static bool TryParse(string name, out TuningKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        key = Of(name);
        return Names.ContainsKey(key.Hash);
    }

    /// <summary>Имена ручек, заведённых в процессе, — по алфавиту (подсказка к ошибке).</summary>
    public static IReadOnlyList<string> Known => [.. Names.Values.Order(StringComparer.Ordinal)];

    /// <summary>Завести имя (регистрация ручки): другое имя с тем же хешем — ошибка регистрации.</summary>
    internal static TuningKey Register(string name)
    {
        var key = Of(name);
        string known = Names.GetOrAdd(key.Hash, name);
        if (!string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ручки «{name}» и «{known}» дают один ключ {key.Hash:X16} — переименуйте одну");
        return key;
    }

    public readonly bool Equals(TuningKey other) => Hash == other.Hash;
    public override readonly bool Equals(object? obj) => obj is TuningKey k && Equals(k);
    public override readonly int GetHashCode() => Hash.GetHashCode();
    public static bool operator ==(TuningKey a, TuningKey b) => a.Hash == b.Hash;
    public static bool operator !=(TuningKey a, TuningKey b) => a.Hash != b.Hash;
    public override readonly string ToString() => Name ?? $"0x{Hash:X16}";
}

/// <summary>
/// Команда ручки (раздел 7 плана): новое значение ручки <see cref="Knob"/> — число; флаг — не ноль. Изменение ручки на ходу —
/// команда симуляции, как любая другая: ставится на ближайший тик, в служебной стадии <c>runtime.commands</c> становится
/// событием, система ручек своего раздела (<c>tuning.&lt;секция&gt;</c>, первая стадия игры) применяет её в том же тике —
/// ресурс заменяется командой (<see cref="ResourceChanged{T}"/> для реактивных систем, видно со следующей стадии), — и она
/// пишется в реплей. Значение прижимается к диапазону ручки; нечисло и незнакомая ручка — ничего.
/// </summary>
public struct TuningCommand
{
    public TuningKey Knob;
    public float Value;
}

/// <summary>Вид ручки: число или флаг (1 — да, 0 — нет).</summary>
public enum TuningKind : byte
{
    Float = 0,
    Bool = 1,
}

/// <summary>
/// Ручка симуляции — описание для панели F3, агента и записи в конфиг: полное имя (ключ конфига «секция.ключ»), подпись,
/// подсказка, диапазон, значение по умолчанию (значение в коде), ресурс мира и его поле. Значение живёт в ресурсе мира
/// (<see cref="Read"/>), меняется только командой <see cref="TuningCommand"/>. Заводит её ядро или модуль при сборке
/// симуляции (<see cref="SimulationBuilder.Tuning{T}"/>).
/// </summary>
public sealed class TuningKnob
{
    private readonly Func<WorldReader, float> _read;

    internal TuningKnob(string section, string key, TuningKind kind, string label, string? hint, float min, float max, float defaultValue,
        bool log, string format, Type resource, string field, string? module, Func<WorldReader, float> read)
    {
        Section = section;
        Key = key;
        Name = section + "." + key;
        Kind = kind;
        Label = label;
        Hint = hint;
        Min = min;
        Max = max;
        Default = defaultValue;
        Log = log;
        Format = format;
        Resource = resource;
        Field = field;
        Module = module;
        _read = read;
        Id = TuningKey.Register(Name);
    }

    /// <summary>Полное имя — ключ конфига: <c>combat.playerDamage</c> (<c>--set combat.playerDamage=3</c>).</summary>
    public string Name { get; }

    /// <summary>Секция конфига (<c>combat</c>) и ключ в ней (<c>playerDamage</c>).</summary>
    public string Section { get; }
    public string Key { get; }

    /// <summary>Ключ ручки в команде (<see cref="TuningCommand.Knob"/>).</summary>
    public TuningKey Id { get; }

    public TuningKind Kind { get; }

    /// <summary>Подпись для людей (панель F3) и подсказка.</summary>
    public string Label { get; }
    public string? Hint { get; }

    /// <summary>Диапазон ручки (флаг — 0…1): команда прижимается к нему.</summary>
    public float Min { get; }
    public float Max { get; }

    /// <summary>Значение по умолчанию — значение в коде: к нему возвращает «сброс».</summary>
    public float Default { get; }

    /// <summary>Логарифмическая шкала ползунка.</summary>
    public bool Log { get; }

    /// <summary>Формат числа в поле панели.</summary>
    public string Format { get; }

    /// <summary>Ресурс мира, в котором живёт значение, и его поле.</summary>
    public Type Resource { get; }
    public string Field { get; }

    /// <summary>Владелец — Id модуля; null — ядро.</summary>
    public string? Module { get; }

    /// <summary>Значение ручки в мире (из ресурса; флаг — 1 или 0).</summary>
    public float Read(WorldReader world) => _read(world);

    /// <summary>Значение в пределах ручки: число — прижато к диапазону, флаг — 1 или 0; нечисло — нечисло (команда его не применит).</summary>
    public float Clamp(float value) =>
        !float.IsFinite(value) ? float.NaN : Kind == TuningKind.Bool ? value != 0 ? 1 : 0 : Math.Clamp(value, Min, Max);

    /// <summary>Значение для людей: флаг — да/нет, число — по формату ручки.</summary>
    public string Text(float value) =>
        Kind == TuningKind.Bool ? value != 0 ? "да" : "нет" : value.ToString(Format, CultureInfo.InvariantCulture);

    public override string ToString() => Name;
}

/// <summary>Чтение поля ресурса для ручки.</summary>
public delegate float TuningGet<T>(in T resource);

/// <summary>Запись поля ресурса ручкой (значение уже в пределах ручки).</summary>
public delegate void TuningSet<T>(ref T resource, float value);

/// <summary>Чтение флага ресурса для ручки.</summary>
public delegate bool TuningGetBool<T>(in T resource);

/// <summary>Запись флага ресурса ручкой.</summary>
public delegate void TuningSetBool<T>(ref T resource, bool value);

/// <summary>
/// Ручки одной секции конфига над ресурсом <typeparamref name="T"/> (<see cref="SimulationBuilder.Tuning{T}"/>): каждая ручка —
/// поле ресурса. Их команды применяет система секции <c>tuning.&lt;секция&gt;</c> в первой стадии игры.
/// </summary>
public sealed class TuningSection<T> where T : unmanaged
{
    private readonly SimulationBuilder _builder;
    private readonly TuningSystem<T> _system;

    internal TuningSection(SimulationBuilder builder, string section, TuningSystem<T> system)
    {
        _builder = builder;
        Section = section;
        _system = system;
    }

    public string Section { get; }

    /// <summary>
    /// Число: поле ресурса <paramref name="field"/> (по умолчанию — ключ с заглавной буквы: <c>playerDamage</c> → <c>PlayerDamage</c>),
    /// диапазон [<paramref name="min"/>, <paramref name="max"/>], значение по умолчанию — значение в коде.
    /// </summary>
    public TuningSection<T> Float(string key, string label, TuningGet<T> get, TuningSet<T> set, float min, float max, float defaultValue,
        bool log = false, string format = "0.###", string? hint = null, string? field = null)
    {
        if (!(float.IsFinite(min) && float.IsFinite(max) && min < max))
            throw new ArgumentException($"ручка {Section}.{key}: диапазон [{min}, {max}] — нужны числа, min < max");
        if (!float.IsFinite(defaultValue)) throw new ArgumentException($"ручка {Section}.{key}: значение по умолчанию {defaultValue} — не число");
        return Add(key, TuningKind.Float, label, hint, min, max, defaultValue, log, format, field, get, set);
    }

    /// <summary>Флаг: поле ресурса <paramref name="field"/> (по умолчанию — ключ с заглавной буквы).</summary>
    public TuningSection<T> Bool(string key, string label, TuningGetBool<T> get, TuningSetBool<T> set, bool defaultValue,
        string? hint = null, string? field = null) =>
        Add(key, TuningKind.Bool, label, hint, 0, 1, defaultValue ? 1 : 0, false, "0", field,
            (in T r) => get(r) ? 1 : 0, (ref T r, float v) => set(ref r, v != 0));

    private TuningSection<T> Add(string key, TuningKind kind, string label, string? hint, float min, float max, float defaultValue,
        bool log, string format, string? field, TuningGet<T> get, TuningSet<T> set)
    {
        if (!IsValidKey(key)) throw new ArgumentException($"ручка {Section}.{key}: ключ — латиница и цифры, первой — буква (ключ конфига)");
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        field ??= char.ToUpperInvariant(key[0]) + key[1..];
        if (typeof(T).GetField(field, BindingFlags.Instance | BindingFlags.Public) is null)
            throw new ArgumentException($"ручка {Section}.{key}: у ресурса {typeof(T).Name} нет поля {field}");
        var knob = new TuningKnob(Section, key, kind, label, hint, min, max, defaultValue, log, format, typeof(T), field, _builder.Module,
            world => get(world.Resource<T>()));
        _builder.AddKnob(knob);
        _system.Add(knob, set);
        return this;
    }

    /// <summary>Ключ ручки — ключ конфига: латиница и цифры, первой — буква (<c>playerDamage</c>).</summary>
    internal static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || !char.IsAsciiLetter(key[0])) return false;
        foreach (char c in key)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }
}

/// <summary>
/// Система ручек секции (<c>tuning.&lt;секция&gt;</c>, первая стадия игры — реактивная по <c>Event&lt;TuningCommand&gt;</c>): команды
/// ручек тика по порядку правят копию ресурса (значение прижато к диапазону ручки; нечисло и чужая ручка — пропуск), итог —
/// одной заменой ресурса командой (с барьера стадии его видят все следующие стадии тика, реактивные — <see cref="ResourceChanged{T}"/>).
/// Внутреннего состояния нет: всё авторитетное — в ресурсе мира.
/// </summary>
internal sealed class TuningSystem<T>(string section) : IReactiveSystem<Event<TuningCommand>> where T : unmanaged
{
    private readonly Dictionary<ulong, (TuningKnob Knob, TuningSet<T> Set)> _knobs = [];

    public static SystemLabel LabelOf(string section) => new("tuning." + section);

    public void Add(TuningKnob knob, TuningSet<T> set) => _knobs.Add(knob.Id.Hash, (knob, set));

    public void Setup(SystemSetup s) => s.Label(LabelOf(section)).SetsResource<T>();

    public void React(in SystemContext ctx, ReadOnlySpan<Event<TuningCommand>> commands)
    {
        T value = ctx.World.Resource<T>();
        bool changed = false;
        for (int i = 0; i < commands.Length; i++)
        {
            var command = commands[i].Value;
            if (!_knobs.TryGetValue(command.Knob.Hash, out var knob)) continue;   // ручка другой секции (или из другой сборки)
            float v = knob.Knob.Clamp(command.Value);
            if (float.IsNaN(v)) continue;
            knob.Set(ref value, v);
            changed = true;
            // Причина замены ресурса (state.why, 6д) — последняя применённая команда ручки (одна команда тика — и так она).
            ctx.Cause = commands[i].Cause;
        }
        if (changed) ctx.Commands.SetResource(value);
    }
}
