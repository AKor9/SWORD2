using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Хеш состояния (раздел 7 плана): 64 бита, зависит от порядка добавления. Числа добавляются значениями, а не сырыми
/// байтами структур: float и double — с приведением <c>-0</c> к <c>+0</c> и любого NaN к одному, bool — к 0/1. Раунд —
/// как у xxHash64 (умножение, поворот, умножение), итог — его же перемешивание; структура-значение, память не выделяет.
/// </summary>
public struct StateHasher
{
    private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
        P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    private ulong _acc;
    private ulong _count;

    /// <param name="seed">Начальное значение: разные участники с одинаковыми данными дают разный хеш.</param>
    public StateHasher(ulong seed) => _acc = seed + P5;

    public void Add(ulong value)
    {
        _acc ^= Round(value);
        _acc = BitOperations.RotateLeft(_acc, 27) * P1 + P4;
        _count++;
    }

    public void Add(long value) => Add(unchecked((ulong)value));
    public void Add(int value) => Add(unchecked((ulong)(long)value));
    public void Add(uint value) => Add((ulong)value);
    public void Add(bool value) => Add(value ? 1UL : 0UL);

    /// <summary>float: <c>-0</c> → <c>+0</c>, любой NaN → один NaN.</summary>
    public void Add(float value) => Add((ulong)(uint)BitConverter.SingleToInt32Bits(Canonical(value)));

    /// <summary>double: <c>-0</c> → <c>+0</c>, любой NaN → один NaN.</summary>
    public void Add(double value) => Add(unchecked((ulong)BitConverter.DoubleToInt64Bits(Canonical(value))));

    /// <summary>Строка — длиной и кодами символов (имена участников и типов).</summary>
    public void Add(string value)
    {
        Add(value.Length);
        foreach (char c in value) Add((uint)c);
    }

    public static float Canonical(float value) => float.IsNaN(value) ? float.NaN : value == 0 ? 0f : value;
    public static double Canonical(double value) => double.IsNaN(value) ? double.NaN : value == 0 ? 0d : value;

    /// <summary>Итог (состояние хешера не меняется — можно добавлять дальше).</summary>
    public readonly ulong Value
    {
        get
        {
            ulong h = _acc ^ (_count * P5);
            h ^= h >> 33;
            h *= P2;
            h ^= h >> 29;
            h *= P3;
            h ^= h >> 32;
            return h;
        }
    }

    private static ulong Round(ulong input) => BitOperations.RotateLeft(input * P2, 31) * P1;

    /// <summary>Хеш строки (имени) — для заранее посчитанных «подписей» типов.</summary>
    public static ulong Of(string value)
    {
        var h = new StateHasher(0);
        h.Add(value);
        return h.Value;
    }
}

/// <summary>
/// Участник хеша состояния (раздел 7 плана): подсистема, чьё авторитетное состояние лежит вне ECS (в ECS — только хэндлы),
/// добавляет его в хеш и описывает для людей и агента. <see cref="Describe"/> описывает ровно то, что хешируется: при
/// расхождении хешей сравниваются два описания. Регистрирует его игра или модуль (<see cref="SimulationBuilder.StateContributor"/>);
/// участник <c>ecs</c> (сущности, компоненты, ресурсы-значения) Runtime заводит сам.
/// </summary>
public interface IStateContributor
{
    /// <summary>Стабильный Id — строчная латиница, цифры и <c>_</c>: <c>animation</c>, <c>physics</c>…</summary>
    string Id { get; }

    /// <summary>Полное авторитетное состояние в стабильном порядке хэндлов. Вызывается между тиками; память не выделять.</summary>
    void AppendHash(ref StateHasher hasher);

    /// <summary>То же состояние по частям с отбором и страницами (<see cref="DescribeQuery"/>).</summary>
    void Describe(StateWriter writer, in DescribeQuery query);
}

/// <summary>
/// Отбор описания участника: <see cref="Filter"/> — смысл задаёт участник (у <c>ecs</c> — сущность <c>12v3</c> или часть
/// имени компонента; у <c>animation</c> — поза <c>2v1</c>); части по порядку, страницей <see cref="Skip"/>/<see cref="Take"/>.
/// </summary>
public readonly record struct DescribeQuery(string? Filter = null, int Skip = 0, int Take = 50);

/// <summary>
/// Описание состояния участника — JSON: части (<see cref="BeginItem"/>) с полями и вложенными объектами. Сколько частей
/// подошло под отбор всего, участник пишет в <see cref="Matched"/> (для страниц).
/// </summary>
public sealed class StateWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();
    private readonly Utf8JsonWriter _json;
    private bool _started, _items;

    public StateWriter() => _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { Indented = false });

    /// <summary>Частей, подошедших под отбор (не только на этой странице).</summary>
    public int Matched { get; set; }

    /// <summary>Частей на странице.</summary>
    public int Written { get; private set; }

    /// <summary>Сведения участника вне частей (счётчики и т. п.) — до первой части.</summary>
    public void Summary(string name, double value)
    {
        if (_items) throw new InvalidOperationException("сводка — до частей");
        Start();
        Number(name, value);
    }

    /// <summary>Часть: объект с именем <paramref name="id"/> (сущность, поза…) — до <see cref="EndItem"/>.</summary>
    public void BeginItem(string id)
    {
        Start();
        if (!_items)
        {
            _json.WriteStartArray("items");
            _items = true;
        }
        _json.WriteStartObject();
        _json.WriteString("id", id);
        Written++;
    }

    public void EndItem() => _json.WriteEndObject();

    public void BeginObject(string name) => _json.WriteStartObject(name);
    public void EndObject() => _json.WriteEndObject();

    /// <summary>Массив объектов: <see cref="BeginArray"/>, в нём <see cref="BeginElement"/> … <see cref="EndObject"/>.</summary>
    public void BeginArray(string name) => _json.WriteStartArray(name);
    public void EndArray() => _json.WriteEndArray();
    public void BeginElement() => _json.WriteStartObject();

    /// <summary>Массив чисел (номера по порядку).</summary>
    public void Values(string name, ReadOnlySpan<int> values)
    {
        _json.WriteStartArray(name);
        foreach (int v in values) _json.WriteNumberValue(v);
        _json.WriteEndArray();
    }

    public void Value(string name, string value) => _json.WriteString(name, value);
    public void Value(string name, bool value) => _json.WriteBoolean(name, value);
    public void Value(string name, long value) => _json.WriteNumber(name, value);
    public void Value(string name, ulong value) => _json.WriteNumber(name, value);

    /// <summary>Число с точностью до бита (float — 9 значащих цифр); NaN и бесконечности — строками.</summary>
    public void Value(string name, float value)
    {
        if (float.IsFinite(value)) _json.WriteNumber(name, double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        else _json.WriteString(name, value.ToString(CultureInfo.InvariantCulture));
    }

    public void Value(string name, double value) => Number(name, value);

    private void Number(string name, double value)
    {
        if (double.IsFinite(value)) _json.WriteNumber(name, value);
        else _json.WriteString(name, value.ToString(CultureInfo.InvariantCulture));
    }

    private void Start()
    {
        if (_started) return;
        _json.WriteStartObject();
        _started = true;
    }

    /// <summary>Готовый JSON: <c>{сводка…, "items": [...], "matched": N}</c>.</summary>
    public string ToJson()
    {
        Start();
        if (!_items) _json.WriteStartArray("items");
        _json.WriteEndArray();
        _json.WriteNumber("matched", Matched);
        _json.WriteEndObject();
        _json.Flush();
        return Encoding.UTF8.GetString(_buffer.WrittenSpan);
    }
}

/// <summary>
/// План полей типа чистых данных: листья (примитивы, перечисления, <see cref="Entity"/>) с путём и смещением в значении.
/// Хеш и описание идут по одним и тем же листьям прямо из байтов значения — padding не участвует, описание ровно то,
/// что хешируется. Смещения среда считает сама: поле заполняется меткой в упакованной копии и ищется в её байтах.
/// Встроенные массивы (<c>[InlineArray]</c>, <c>fixed</c>) — листья <c>имя[i]</c>. Указатели и ссылки — ошибка.
/// </summary>
public sealed class FieldLayout
{
    public enum Kind : byte { Bool, I8, U8, I16, U16, Char, I32, U32, I64, U64, F32, F64, Entity }

    public readonly record struct Leaf(string Path, int Offset, Kind Kind);

    private static readonly ConcurrentDictionary<Type, FieldLayout> Cache = new();

    public Type Type { get; }
    public int Size { get; }
    public IReadOnlyList<Leaf> Leaves => _leaves;

    private readonly Leaf[] _leaves;

    private FieldLayout(Type type, int size, Leaf[] leaves)
    {
        Type = type;
        Size = size;
        _leaves = leaves;
    }

    /// <exception cref="ArgumentException">Тип со ссылками, указателями или Nullable — не чистые данные.</exception>
    public static FieldLayout Of(Type type) => Cache.GetOrAdd(type, Build);

    private static FieldLayout Build(Type type)
    {
        var leaves = new List<Leaf>();
        int size = SizeOf(type);
        Walk(type, "", 0, leaves, 0);
        return new FieldLayout(type, size, [.. leaves]);
    }

    /// <summary>Имя вида листа — в заголовке реплея (<c>Move.X:f32</c>).</summary>
    public static string KindName(Kind kind) => kind switch
    {
        Kind.Bool => "bool", Kind.I8 => "i8", Kind.U8 => "u8", Kind.I16 => "i16", Kind.U16 => "u16", Kind.Char => "char",
        Kind.I32 => "i32", Kind.U32 => "u32", Kind.I64 => "i64", Kind.U64 => "u64", Kind.F32 => "f32", Kind.F64 => "f64",
        _ => "entity",
    };

    /// <summary>Вид листа по имени (<see cref="KindName"/>); неизвестное — null.</summary>
    public static Kind? KindOf(string name) =>
        Enum.GetValues<Kind>().Cast<Kind?>().FirstOrDefault(k => KindName(k!.Value) == name);

    /// <summary>
    /// Лист <paramref name="leaf"/> значения — 64 битами: знаковые — с расширением знака, bool — 0/1, float и double — их
    /// биты, <see cref="Entity"/> — индекс в младших 32 битах, поколение в старших. Обратное — <see cref="SetBits"/>.
    /// </summary>
    public ulong Bits(ReadOnlySpan<byte> bytes, int leaf)
    {
        var l = _leaves[leaf];
        var b = bytes[l.Offset..];
        return l.Kind switch
        {
            Kind.Bool => b[0] != 0 ? 1UL : 0UL,
            Kind.I8 => unchecked((ulong)(sbyte)b[0]),
            Kind.U8 => b[0],
            Kind.I16 => unchecked((ulong)MemoryMarshal.Read<short>(b)),
            Kind.U16 or Kind.Char => MemoryMarshal.Read<ushort>(b),
            Kind.I32 => unchecked((ulong)MemoryMarshal.Read<int>(b)),
            Kind.U32 or Kind.F32 => MemoryMarshal.Read<uint>(b),
            _ => MemoryMarshal.Read<ulong>(b),   // I64, U64, F64, Entity (индекс, поколение — два int подряд)
        };
    }

    /// <summary>Записать лист из 64 бит (<see cref="Bits"/>).</summary>
    public void SetBits(Span<byte> bytes, int leaf, ulong bits)
    {
        var l = _leaves[leaf];
        var b = bytes[l.Offset..];
        switch (l.Kind)
        {
            case Kind.Bool: b[0] = bits != 0 ? (byte)1 : (byte)0; break;
            case Kind.I8 or Kind.U8: b[0] = unchecked((byte)bits); break;
            case Kind.I16 or Kind.U16 or Kind.Char: MemoryMarshal.Write(b, unchecked((ushort)bits)); break;
            case Kind.I32 or Kind.U32 or Kind.F32: MemoryMarshal.Write(b, unchecked((uint)bits)); break;
            default: MemoryMarshal.Write(b, bits); break;
        }
    }

    public void Hash(ReadOnlySpan<byte> bytes, ref StateHasher h)
    {
        foreach (ref readonly var leaf in _leaves.AsSpan())
        {
            var b = bytes[leaf.Offset..];
            switch (leaf.Kind)
            {
                case Kind.Bool: h.Add(b[0] != 0); break;
                case Kind.I8: h.Add((int)(sbyte)b[0]); break;
                case Kind.U8: h.Add((uint)b[0]); break;
                case Kind.I16: h.Add((int)MemoryMarshal.Read<short>(b)); break;
                case Kind.U16 or Kind.Char: h.Add((uint)MemoryMarshal.Read<ushort>(b)); break;
                case Kind.I32: h.Add(MemoryMarshal.Read<int>(b)); break;
                case Kind.U32: h.Add(MemoryMarshal.Read<uint>(b)); break;
                case Kind.I64: h.Add(MemoryMarshal.Read<long>(b)); break;
                case Kind.U64: h.Add(MemoryMarshal.Read<ulong>(b)); break;
                case Kind.F32: h.Add(MemoryMarshal.Read<float>(b)); break;
                case Kind.F64: h.Add(MemoryMarshal.Read<double>(b)); break;
                case Kind.Entity:
                    var e = MemoryMarshal.Read<Entity>(b);
                    h.Add(e.Index);
                    h.Add(e.Generation);
                    break;
            }
        }
    }

    /// <summary>Листья значения — полями <c>путь: значение</c> (сущность — <c>12v3</c>).</summary>
    public void Describe(ReadOnlySpan<byte> bytes, StateWriter w)
    {
        foreach (var leaf in _leaves)
        {
            var b = bytes[leaf.Offset..];
            string name = leaf.Path.Length == 0 ? "value" : leaf.Path;
            switch (leaf.Kind)
            {
                case Kind.Bool: w.Value(name, b[0] != 0); break;
                case Kind.I8: w.Value(name, (sbyte)b[0]); break;
                case Kind.U8: w.Value(name, b[0]); break;
                case Kind.I16: w.Value(name, MemoryMarshal.Read<short>(b)); break;
                case Kind.U16: w.Value(name, MemoryMarshal.Read<ushort>(b)); break;
                case Kind.Char: w.Value(name, ((char)MemoryMarshal.Read<ushort>(b)).ToString()); break;
                case Kind.I32: w.Value(name, MemoryMarshal.Read<int>(b)); break;
                case Kind.U32: w.Value(name, MemoryMarshal.Read<uint>(b)); break;
                case Kind.I64: w.Value(name, MemoryMarshal.Read<long>(b)); break;
                case Kind.U64: w.Value(name, MemoryMarshal.Read<ulong>(b)); break;
                case Kind.F32: w.Value(name, MemoryMarshal.Read<float>(b)); break;
                case Kind.F64: w.Value(name, MemoryMarshal.Read<double>(b)); break;
                case Kind.Entity: w.Value(name, MemoryMarshal.Read<Entity>(b).ToString()); break;
            }
        }
    }

    // ── построение плана ────────────────────────────────────────────────

    private static void Walk(Type type, string path, int offset, List<Leaf> leaves, int depth)
    {
        if (depth > 16) throw new ArgumentException($"{type.Name}: вложенность полей глубже 16");
        if (!type.IsValueType || Nullable.GetUnderlyingType(type) is not null || type.IsPointer)
            throw new ArgumentException($"{path}: {type.Name} — не чистые данные (ссылки, указатели или Nullable) — в хеш состояния не идёт");
        if (type == typeof(Entity))
        {
            leaves.Add(new Leaf(path, offset, Kind.Entity));
            return;
        }
        if (type.IsEnum)
        {
            Walk(Enum.GetUnderlyingType(type), path, offset, leaves, depth + 1);
            return;
        }
        if (type.IsPrimitive)
        {
            leaves.Add(new Leaf(path, offset, PrimitiveKind(type, path)));
            return;
        }
        if (InlineElement(type) is { } element)
        {
            int size = SizeOf(element), count = SizeOf(type) / size;
            for (int i = 0; i < count; i++) Walk(element, $"{path}[{i}]", offset + i * size, leaves, depth + 1);
            return;
        }
        foreach (var f in InstanceFields(type))
        {
            if (f.FieldType.IsPointer) throw new ArgumentException($"{Join(path, FieldName(f))}: указатель — в хеш состояния не идёт");
            Walk(f.FieldType, Join(path, FieldName(f)), offset + OffsetOf(type, f), leaves, depth + 1);
        }
    }

    private static Kind PrimitiveKind(Type t, string path) =>
        t == typeof(bool) ? Kind.Bool :
        t == typeof(sbyte) ? Kind.I8 :
        t == typeof(byte) ? Kind.U8 :
        t == typeof(short) ? Kind.I16 :
        t == typeof(ushort) ? Kind.U16 :
        t == typeof(char) ? Kind.Char :
        t == typeof(int) ? Kind.I32 :
        t == typeof(uint) ? Kind.U32 :
        t == typeof(long) || t == typeof(nint) ? Kind.I64 :
        t == typeof(ulong) || t == typeof(nuint) ? Kind.U64 :
        t == typeof(float) ? Kind.F32 :
        t == typeof(double) ? Kind.F64 :
        throw new ArgumentException($"{path}: {t.Name} — не поддержан хешем состояния");

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static IEnumerable<FieldInfo> InstanceFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken);

    private static string FieldName(FieldInfo f) =>
        f.Name.StartsWith('<') && f.Name.IndexOf('>') is > 1 and var end ? f.Name[1..end] : f.Name;

    private static Type? InlineElement(Type type)
    {
        if (type.GetCustomAttribute<InlineArrayAttribute>() is not null) return InstanceFields(type).First().FieldType;
        if (type.IsDefined(typeof(UnsafeValueTypeAttribute), false) && InstanceFields(type).ToList() is [{ Name: "FixedElementField" } only])
            return only.FieldType;
        return null;
    }

    private sealed class RawBox
    {
        public byte Data;
    }

    private static Span<byte> RawData(object box, int size) => MemoryMarshal.CreateSpan(ref Unsafe.As<RawBox>(box).Data, size);

    /// <summary>Смещение поля: в нулевой упакованной копии владельца поле получает значение из одних 0xFF — первый ненулевой байт.</summary>
    private static int OffsetOf(Type owner, FieldInfo field)
    {
        int ownerSize = SizeOf(owner), fieldSize = SizeOf(field.FieldType);
        object box = RuntimeHelpers.GetUninitializedObject(owner);
        RawData(box, ownerSize).Clear();
        object marker = RuntimeHelpers.GetUninitializedObject(field.FieldType);
        RawData(marker, fieldSize).Fill(0xFF);
        field.SetValue(box, marker);
        int at = RawData(box, ownerSize).IndexOfAnyExcept((byte)0);
        if (at < 0 || at + fieldSize > ownerSize) throw new InvalidOperationException($"{owner.Name}.{field.Name}: смещение не найдено");
        return at;
    }

    private static readonly MethodInfo SizeOfMethod = typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!;
    private static readonly ConcurrentDictionary<Type, int> Sizes = new();

    public static int SizeOf(Type type) => Sizes.GetOrAdd(type, t => (int)SizeOfMethod.MakeGenericMethod(t).Invoke(null, null)!);
}

/// <summary>
/// Участник <c>ecs</c> — всё, что мир переносит из тика в тик: живые сущности по возрастанию индекса (индекс и поколение),
/// у каждой — компоненты по стабильному имени (подпись имени, затем листья полей; тег — только подпись); ресурсы-значения
/// по имени; слоты сущностей (число, поколения свободных слотов, порядок свободных номеров — от них зависят номера
/// будущих сущностей); журналы по имени (записи последнего оборота тика с возрастом в барьерах — их ещё прочтут
/// системы следующего тика); регистрации систем в порядке исполнения (включена ли, сколько записей каждого журнала не
/// прочла). Номера записей и барьеров не входят: хеш не зависит от того, сколько тиков сыграно. Ресурсы со ссылками
/// (пулы подсистем, ассеты) сюда не входят: их авторитетное состояние — у своих участников; у записи журнала со
/// ссылками хешируется только возраст.
/// </summary>
internal sealed class EcsStateContributor(WorldReader world) : IStateContributor
{
    private (int Id, ulong Name, FieldLayout Layout, string Title)[]? _components;
    private (int Id, ulong Name, FieldLayout Layout, string Title)[]? _resources;
    private (ulong Name, FieldLayout? Layout, string Title)[]? _journals;
    private (ulong Name, string Title)[]? _systems;

    public string Id => "ecs";

    private void Prepare()
    {
        if (_components is not null) return;
        _components = [.. world.Components.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => (c.Id, StateHasher.Of(c.Name), FieldLayout.Of(c.Type), c.Name))];
        _resources = [.. world.Resources.Where(r => r.Size > 0).OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (r.Id, StateHasher.Of(r.Name), FieldLayout.Of(r.Type), r.Name))];
        _journals = [.. world.Journals.Select(j => (StateHasher.Of(j.Name), j.EntrySize > 0 ? FieldLayout.Of(j.EntryType) : null, j.Name))];
        _systems = [.. world.Systems.Select(s => (StateHasher.Of(s.Name), s.Name))];
    }

    public void AppendHash(ref StateHasher h)
    {
        Prepare();
        int slots = world.EntitySlots;
        for (int i = 0; i < slots; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull) continue;
            h.Add(e.Index);
            h.Add(e.Generation);
            foreach (var c in _components!)
            {
                if (!world.TryRead(e, c.Id, out var bytes)) continue;
                h.Add(c.Name);
                c.Layout.Hash(bytes, ref h);
            }
        }
        foreach (var r in _resources!)
        {
            if (!world.TryReadResource(r.Id, out var bytes)) continue;
            h.Add(r.Name);
            r.Layout.Hash(bytes, ref h);
        }

        // Слоты: у свободных — поколение следующей сущности в слоте, затем порядок выдачи свободных номеров.
        h.Add(slots);
        for (int i = 0; i < slots; i++)
            if (world.EntityAt(i).IsNull)
            {
                h.Add(i);
                h.Add(world.GenerationAt(i));
            }
        var free = world.FreeIndices;
        h.Add(free.Length);
        foreach (int index in free) h.Add(index);

        for (int j = 0; j < _journals!.Length; j++)
        {
            int count = world.JournalLength(j);
            h.Add(_journals[j].Name);
            h.Add(count);
            for (int i = 0; i < count; i++)
            {
                var bytes = world.JournalEntry(j, i, out long age);
                h.Add(age);
                _journals[j].Layout?.Hash(bytes, ref h);
            }
        }

        for (int s = 0; s < _systems!.Length; s++)
        {
            h.Add(_systems[s].Name);
            h.Add(world.SystemActive(s));
            int cursors = world.CursorCount(s);
            h.Add(cursors);
            for (int i = 0; i < cursors; i++)
            {
                h.Add(world.CursorUnread(s, i, out int journal));
                h.Add(journal);
            }
        }
    }

    /// <summary>
    /// Части в порядке хеша: сущности (<c>12v3</c>), ресурсы (<c>resource:Имя</c>), <c>slots</c>, журналы
    /// (<c>journal:Added&lt;Имя&gt;</c>), системы (<c>system:метка@стадия</c>). Отбор — сущность (только она) или часть
    /// имени: у сущностей — компонента, у остальных частей — их id.
    /// </summary>
    public void Describe(StateWriter w, in DescribeQuery q)
    {
        Prepare();
        Entity? only = q.Filter is { } f && TryEntity(f, out var parsed) ? parsed : null;
        string? name = only is null ? q.Filter : null;
        int skip = q.Skip, take = q.Take;
        int matched = 0;
        int slots = world.EntitySlots;
        w.Summary("entities", world.EntityCount);
        for (int i = 0; i < slots; i++)
        {
            var e = world.EntityAt(i);
            if (e.IsNull) continue;
            if (only is { } o && o != e) continue;
            if (name is not null && !_components!.Any(c => c.Title.Contains(name, StringComparison.OrdinalIgnoreCase) && world.TryRead(e, c.Id, out _)))
                continue;
            int at = matched++;
            if (at < skip || at >= skip + take) continue;
            w.BeginItem(e.ToString());
            foreach (var c in _components!)
            {
                if (!world.TryRead(e, c.Id, out var bytes)) continue;
                if (name is not null && !c.Title.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                w.BeginObject(c.Title);
                c.Layout.Describe(bytes, w);
                w.EndObject();
            }
            w.EndItem();
        }
        if (only is null)
        {
            // Часть подходит под отбор — считается; на странице ли она.
            bool OnPage(string id)
            {
                if (name is not null && !id.Contains(name, StringComparison.OrdinalIgnoreCase)) return false;
                int at = matched++;
                return at >= skip && at < skip + take;
            }

            foreach (var r in _resources!)
            {
                if (!world.TryReadResource(r.Id, out var bytes) || !OnPage("resource:" + r.Title)) continue;
                w.BeginItem("resource:" + r.Title);
                r.Layout.Describe(bytes, w);
                w.EndItem();
            }

            if (OnPage("slots"))
            {
                w.BeginItem("slots");
                w.Value("count", slots);
                w.BeginObject("freeGenerations");
                for (int i = 0; i < slots; i++)
                    if (world.EntityAt(i).IsNull) w.Value(i.ToString(CultureInfo.InvariantCulture), world.GenerationAt(i));
                w.EndObject();
                w.Values("free", world.FreeIndices);
                w.EndItem();
            }

            for (int j = 0; j < _journals!.Length; j++)
            {
                var journal = _journals[j];
                if (!OnPage("journal:" + journal.Title)) continue;
                int count = world.JournalLength(j);
                w.BeginItem("journal:" + journal.Title);
                if (journal.Layout is null) w.Value("fields", "не хешируются: запись со ссылками");
                w.BeginArray("entries");
                for (int i = 0; i < count; i++)
                {
                    var bytes = world.JournalEntry(j, i, out long age);
                    w.BeginElement();
                    w.Value("age", age);
                    journal.Layout?.Describe(bytes, w);
                    w.EndObject();
                }
                w.EndArray();
                w.EndItem();
            }

            for (int s = 0; s < _systems!.Length; s++)
            {
                if (!OnPage("system:" + _systems[s].Title)) continue;
                w.BeginItem("system:" + _systems[s].Title);
                w.Value("active", world.SystemActive(s));
                w.BeginObject("unread");
                for (int i = 0, n = world.CursorCount(s); i < n; i++)
                {
                    int unread = world.CursorUnread(s, i, out int journal);
                    w.Value(_journals[journal].Title, unread);
                }
                w.EndObject();
                w.EndItem();
            }
        }
        w.Matched = matched;
    }

    private static bool TryEntity(string text, out Entity e)
    {
        e = default;
        int v = text.IndexOf('v');
        if (v <= 0 || !int.TryParse(text.AsSpan(0, v), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || !int.TryParse(text.AsSpan(v + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int generation))
            return false;
        e = new Entity(index, generation);
        return true;
    }
}
