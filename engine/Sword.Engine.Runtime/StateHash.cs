using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Хеш состояния, 64 бита, зависит от порядка. Числа — значениями: <c>-0</c> → <c>+0</c>, любой NaN — один, bool — 0/1.
/// Раунд и перемешивание — как у xxHash64; без выделений.
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

/// <summary>Участник хеша: подсистема, чьё авторитетное состояние лежит вне ECS (в ECS — только хэндлы). Участник
/// <c>ecs</c> Runtime заводит сам.</summary>
public interface IStateContributor
{
    /// <summary>Стабильный Id — строчная латиница, цифры и <c>_</c>: <c>animation</c>, <c>physics</c>…</summary>
    string Id { get; }

    /// <summary>Полное авторитетное состояние в стабильном порядке хэндлов. Вызывается между тиками; память не выделять.</summary>
    void AppendHash(ref StateHasher hasher);
}

/// <summary>
/// План полей типа чистых данных: листья (примитивы, перечисления, <see cref="Entity"/>, <c>имя[i]</c> встроенных
/// массивов) с путём и смещением; padding не участвует. Смещения ищутся меткой в упакованной копии. Указатели и ссылки — ошибка.
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

    /// <summary>Лист значения 64 битами: знаковые — с расширением знака, bool — 0/1, float — биты, <see cref="Entity"/> —
    /// индекс в младших 32, поколение в старших. Обратное — <see cref="SetBits"/>.</summary>
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
/// Участник <c>ecs</c> — всё, что мир переносит из тика в тик: живые сущности по индексу с компонентами по стабильному
/// имени, ресурсы-значения, слоты и порядок свободных номеров, журналы (записи с возрастом), регистрации систем
/// (включена, сколько не прочла). Номера записей и барьеров не входят — хеш не зависит от числа сыгранных тиков.
/// Ресурсы со ссылками хешируют свои участники.
/// </summary>
internal sealed class EcsStateContributor(WorldReader world) : IStateContributor
{
    private (int Id, ulong Name, FieldLayout Layout)[]? _components;
    private (int Id, ulong Name, FieldLayout Layout)[]? _resources;
    private (ulong Name, FieldLayout? Layout)[]? _journals;
    private ulong[]? _systems;

    public string Id => "ecs";

    private void Prepare()
    {
        if (_components is not null) return;
        _components = [.. world.Components.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => (c.Id, StateHasher.Of(c.Name), FieldLayout.Of(c.Type)))];
        _resources = [.. world.Resources.Where(r => r.Size > 0).OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (r.Id, StateHasher.Of(r.Name), FieldLayout.Of(r.Type)))];
        _journals = [.. world.Journals.Select(j => (StateHasher.Of(j.Name), j.EntrySize > 0 ? FieldLayout.Of(j.EntryType) : null))];
        _systems = [.. world.Systems.Select(s => StateHasher.Of(s.Name))];
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
            h.Add(_systems[s]);
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
}
