using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;

namespace Sword.Engine.Introspection;

/// <summary>
/// Значение чистых данных (компонент, событие, команда, ресурс) по полям — в JSON и обратно, по стабильным именам полей
/// (раздел 8 плана: «компоненты по стабильным именам и полям»). Поля — экземплярные поля структуры в порядке
/// объявления (у автосвойства — имя свойства); вложенные структуры — объекты, <c>[InlineArray]</c> и <c>fixed</c>-буферы —
/// массивы, перечисления — именем (необъявленное значение разбор показывает числом, а сборка его не принимает: записать
/// можно только объявленное), <see cref="Entity"/> — строкой <c>12v3</c>, ключ ручки (<see cref="Runtime.TuningKey"/>) — именем
/// ручки <c>combat.playerDamage</c> (незаведённая — ошибка с перечнем; число — как есть), нечисловые float — строками <c>NaN</c>,
/// <c>Infinity</c>, <c>-Infinity</c> (в JSON их нет). Байты разбираются через упакованную копию значения — раскладку
/// считает сама среда, смещения полей не вычисляются.
/// </summary>
public static class FieldCodec
{
    /// <summary>Байты значения типа <paramref name="type"/> → JSON (объект для структуры с полями).</summary>
    public static JsonNode? Decode(Type type, ReadOnlySpan<byte> bytes)
    {
        CheckPlain(type);
        if (bytes.IsEmpty && !InstanceFields(type).Any()) return new JsonObject();   // пустая структура (тег) — 0 байт в мире
        if (bytes.Length != SizeOf(type))
            throw new ArgumentException($"{type.Name}: {bytes.Length} Б при размере {SizeOf(type)} Б", nameof(bytes));
        return Value(type, Box(type, bytes));
    }

    /// <summary>
    /// JSON → байты значения типа <paramref name="type"/>: заданные поля — из <paramref name="fields"/>, остальные — из
    /// <paramref name="baseline"/> (null — нули). Неизвестное поле, значение не того вида или вне диапазона — ошибка с
    /// путём поля.
    /// </summary>
    public static byte[] Encode(Type type, JsonNode? fields, ReadOnlySpan<byte> baseline = default)
    {
        CheckPlain(type);
        int size = SizeOf(type);
        object box = baseline.IsEmpty ? RuntimeHelpers.GetUninitializedObject(type) : Box(type, baseline);
        if (baseline.IsEmpty) RawData(box, size).Clear();
        // Узлы, собранные в памяти (например, из Decode), хранят значение своего типа и не переводятся в другой
        // (float → double); узлы из текста — переводятся. Поэтому — через текст.
        if (fields is not null) fields = JsonNode.Parse(fields.ToJsonString());
        box = Assign(type, box, fields, type.Name);
        return RawData(box, size).ToArray();
    }

    /// <summary>Поля типа — имена и типы (для схем инструментов и подсказок).</summary>
    public static IReadOnlyList<(string Name, Type Type)> Fields(Type type) =>
        [.. InstanceFields(type).Select(f => (FieldName(f), f.FieldType))];

    /// <summary>Описание типа для агента: поля с видами (вложенные — объектом), без значений.</summary>
    public static JsonNode Schema(Type type)
    {
        if (Leaf(type) is { } leaf) return leaf;
        if (InlineElement(type) is { } element) return new JsonArray(Schema(element));
        var o = new JsonObject();
        foreach (var f in InstanceFields(type)) o[FieldName(f)] = Schema(f.FieldType);
        return o;
    }

    /// <summary>
    /// Проверить путь полей внутри типа (<c>Position.X</c>, без учёта регистра) — по тем же полям, что даёт
    /// <see cref="Decode"/>. Неизвестный сегмент — ошибка с перечнем полей: опечатка в проекции не выглядит пустыми данными.
    /// Ответ — путь с точными именами полей.
    /// </summary>
    public static string CheckPath(Type type, string path, string prefix)
    {
        var exact = new List<string>();
        Type t = type;
        string at = prefix;
        foreach (string part in path.Split('.'))
        {
            IReadOnlyList<(string Name, Type Type)> fields = Leaf(t) is null && InlineElement(t) is null ? Fields(t) : [];
            var f = fields.FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));
            if (f.Name is null)
                throw new ArgumentException(fields.Count == 0
                    ? $"{at}: вложенных полей нет — «{part}» лишнее"
                    : $"{at}: нет поля «{part}»; есть: {string.Join(", ", fields.Select(x => x.Name))}");
            t = f.Type;
            at += "." + f.Name;
            exact.Add(f.Name);
        }
        return string.Join('.', exact);
    }

    public static int SizeOf(Type type) => RuntimeHelpers.SizeOf(type.TypeHandle);

    /// <summary>
    /// Только чистые данные: структура без ссылок и без <c>Nullable</c> (у упакованного <c>Nullable</c> другая
    /// раскладка — запись байтами испортила бы память). Другое — <see cref="ArgumentException"/>.
    /// </summary>
    public static void CheckPlain(Type type)
    {
        if (!IsPlain(type, 0)) throw new ArgumentException($"{type.Name}: не чистые данные (ссылки или Nullable) — побайтно не разбирается");
    }

    private static bool IsPlain(Type t, int depth) =>
        depth < 16 && t.IsValueType && Nullable.GetUnderlyingType(t) is null && !t.IsPointer
        && (t.IsPrimitive || t.IsEnum || InstanceFields(t).All(f => IsPlain(f.FieldType, depth + 1)));

    // ── разбор ─────────────────────────────────────────────────────────────

    private static JsonNode? Value(Type type, object value)
    {
        switch (value)
        {
            case bool b: return JsonValue.Create(b);
            case float f: return float.IsFinite(f) ? JsonValue.Create(f) : Number(f);
            case double d: return Number(d);
            case Entity e: return JsonValue.Create(e.ToString());
            // Ручка — именем «секция.ключ»; ключ, которого в процессе не заводили, — числом.
            case Runtime.TuningKey k: return k.Name is { } knob ? JsonValue.Create(knob) : JsonValue.Create((decimal)k.Hash);
            case char c: return JsonValue.Create(c.ToString());
            // Необъявленное значение (у [Flags] — с необъявленными битами) ToString печатает числом: оно и уходит числом, а
            // не строкой-числом, похожей на имя.
            case Enum en: return en.ToString() is [(>= '0' and <= '9') or '-', ..] ? JsonValue.Create(EnumNumber(en)) : JsonValue.Create(en.ToString());
            case nint n: return JsonValue.Create((long)n);
            case nuint u: return JsonValue.Create((ulong)u);
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return JsonValue.Create(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }
        if (InlineElement(type) is { } element)
        {
            int size = SizeOf(element), total = SizeOf(type);
            var raw = RawData(value, total);
            var array = new JsonArray();
            for (int i = 0; i + size <= total; i += size) array.Add(Value(element, Box(element, raw.Slice(i, size))));
            return array;
        }
        var o = new JsonObject();
        foreach (var f in InstanceFields(type)) o[FieldName(f)] = f.GetValue(value) is { } v ? Value(f.FieldType, v) : null;
        return o;
    }

    private static JsonNode Number(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");

    private static JsonNode? Leaf(Type t) =>
        t == typeof(bool) ? "bool" :
        t == typeof(float) || t == typeof(double) ? "number" :
        t == typeof(Entity) ? "entity (\"12v3\")" :
        t == typeof(Runtime.TuningKey) ? $"knob: {string.Join(" | ", Runtime.TuningKey.Known)}" :
        t == typeof(char) ? "char" :
        t.IsEnum ? $"enum: {string.Join(" | ", Enum.GetNames(t))}" :
        t.IsPrimitive ? $"integer ({t.Name})" : null;

    // ── сборка ─────────────────────────────────────────────────────────────

    private static object Assign(Type type, object box, JsonNode? node, string path)
    {
        if (node is null) return box;
        if (node is not JsonObject obj)
            throw new ArgumentException($"{path}: ожидается объект с полями {string.Join(", ", InstanceFields(type).Select(FieldName))}");
        var fields = InstanceFields(type).ToDictionary(FieldName, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in obj)
        {
            if (!fields.TryGetValue(name, out var f))
                throw new ArgumentException($"{path}: нет поля «{name}»; есть: {string.Join(", ", fields.Keys)}");
            string at = $"{path}.{FieldName(f)}";
            object current = f.GetValue(box)!;
            f.SetValue(box, Convert(f.FieldType, current, value, at));
        }
        return box;
    }

    private static object Convert(Type t, object current, JsonNode? node, string path)
    {
        try
        {
            if (t == typeof(bool)) return node!.GetValue<bool>();
            if (t == typeof(float))
            {
                double d = Double(node, path);
                float f = (float)d;
                if (double.IsFinite(d) && !float.IsFinite(f)) throw new ArgumentException($"{path}: {d} вне диапазона float");
                return f;
            }
            if (t == typeof(double)) return Double(node, path);
            if (t == typeof(Entity)) return ParseEntity(node!.GetValue<string>(), path);
            if (t == typeof(Runtime.TuningKey))
            {
                if (node!.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    return new Runtime.TuningKey((ulong)Integral(node, path, 0, ulong.MaxValue));
                string name = node.GetValue<string>();
                return Runtime.TuningKey.TryParse(name, out var key) ? key
                    : throw new ArgumentException($"{path}: нет ручки «{name}»; есть: {string.Join(", ", Runtime.TuningKey.Known)}");
            }
            if (t == typeof(char)) return node!.GetValue<string>() is [var c] ? c : throw new ArgumentException($"{path}: ожидается один символ");
            if (t.IsEnum)
            {
                string s = node!.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    ? node.GetValue<long>().ToString(CultureInfo.InvariantCulture) : node.GetValue<string>();
                // Список «A, C» — только у [Flags] (так его и печатает Decode); у обычного перечисления Enum.TryParse
                // сложил бы значения в третье.
                bool flags = t.IsDefined(typeof(FlagsAttribute), false);
                // Число Enum.TryParse принимает любое: у обычного перечисления — только объявленные значения, у [Flags] —
                // только объявленные биты.
                if (!Enum.TryParse(t, s, ignoreCase: true, out var e) || (!flags && s.Contains(','))
                    || (flags ? (EnumBits(e!) & ~Enum.GetValues(t).Cast<object>().Aggregate(0ul, (m, v) => m | EnumBits(v))) != 0
                              : !Enum.IsDefined(t, e!)))
                    throw new ArgumentException($"{path}: нет значения «{s}»; есть: {string.Join(", ", Enum.GetNames(t))}");
                return e!;
            }
            if (t == typeof(nint)) return (nint)Integral(node, path, long.MinValue, long.MaxValue);
            if (t == typeof(nuint)) return (nuint)(ulong)Integral(node, path, 0, ulong.MaxValue);
            if (t.IsPrimitive)
            {
                decimal value = Integral(node, path, decimal.MinValue, decimal.MaxValue);
                return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            }
            if (InlineElement(t) is { } element)
            {
                if (node is not JsonArray array) throw new ArgumentException($"{path}: ожидается массив");
                int size = SizeOf(element), total = SizeOf(t);
                if (array.Count * size > total) throw new ArgumentException($"{path}: элементов не больше {total / size}");
                var raw = RawData(current, total);
                for (int i = 0; i < array.Count; i++)
                {
                    object item = Box(element, raw.Slice(i * size, size));
                    item = Convert(element, item, array[i], $"{path}[{i}]");
                    RawData(item, size).CopyTo(raw.Slice(i * size, size));
                }
                return current;
            }
            return Assign(t, current, node, path);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or NullReferenceException
                                      or InvalidCastException)
        {
            throw new ArgumentException($"{path}: значение {node?.ToJsonString() ?? "null"} не подходит к {t.Name} ({e.Message})");
        }
    }

    private static decimal EnumNumber(Enum value) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    /// <summary>Биты значения перечисления любого базового типа (знаковое — дополнением до двух).</summary>
    private static ulong EnumBits(object value) =>
        Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) == TypeCode.UInt64
            ? (ulong)System.Convert.ChangeType(value, typeof(ulong), CultureInfo.InvariantCulture)
            : unchecked((ulong)System.Convert.ToInt64(value, CultureInfo.InvariantCulture));

    /// <summary>Целое из JSON: дробное — ошибка (молчаливое округление дало бы не то значение).</summary>
    private static decimal Integral(JsonNode? node, string path, decimal min, decimal max)
    {
        decimal value = node!.GetValue<decimal>();
        if (decimal.Truncate(value) != value) throw new ArgumentException($"{path}: {value} — не целое");
        if (value < min || value > max) throw new ArgumentException($"{path}: {value} вне диапазона");
        return value;
    }

    private static double Double(JsonNode? node, string path) =>
        node!.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                var s => throw new ArgumentException($"{path}: «{s}» — не число"),
            }
            : node.GetValue<double>();

    /// <summary>Сущность из строки <c>12v3</c> (как <see cref="Entity.ToString"/>) или <c>null</c>.</summary>
    public static Entity ParseEntity(string text, string what = "сущность")
    {
        if (text is "null" or "Entity.Null") return Entity.Null;
        int v = text.IndexOf('v');
        if (v > 0 && int.TryParse(text.AsSpan(0, v), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            && int.TryParse(text.AsSpan(v + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int generation))
            return new Entity(index, generation);
        throw new ArgumentException($"{what}: «{text}» — ожидается номер и поколение, например 12v3");
    }

    // ── упаковка и поля ────────────────────────────────────────────────────

    private sealed class RawBox
    {
        public byte Data;
    }

    /// <summary>Байты упакованного значения: данные начинаются там, где у объекта первое поле.</summary>
    private static Span<byte> RawData(object box, int size) => MemoryMarshal.CreateSpan(ref Unsafe.As<RawBox>(box).Data, size);

    private static object Box(Type type, ReadOnlySpan<byte> bytes)
    {
        object box = RuntimeHelpers.GetUninitializedObject(type);
        bytes.CopyTo(RawData(box, SizeOf(type)));
        return box;
    }

    private static IEnumerable<FieldInfo> InstanceFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken);

    private static string FieldName(FieldInfo f) =>
        f.Name.StartsWith('<') && f.Name.IndexOf('>') is > 1 and var end ? f.Name[1..end] : f.Name;

    /// <summary>
    /// Элемент массива в структуре: <c>[InlineArray]</c> или <c>fixed</c>-буфер (компилятор делает из него структуру с
    /// одним полем <c>FixedElementField</c> и размером всего буфера) — иначе null.
    /// </summary>
    private static Type? InlineElement(Type type)
    {
        if (type.GetCustomAttribute<InlineArrayAttribute>() is not null) return InstanceFields(type).First().FieldType;
        if (type.IsDefined(typeof(UnsafeValueTypeAttribute), false) && InstanceFields(type).ToList() is [{ Name: "FixedElementField" } only])
            return only.FieldType;
        return null;
    }
}
