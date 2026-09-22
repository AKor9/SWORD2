using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>
/// Зарегистрированный тип компонента или события. Номер (<see cref="Id"/>) назначается при сборке мира по полному
/// имени типа — не зависит от порядка регистрации и первого обращения; нигде не сохраняется (сейвы, реплеи и ассеты
/// ссылаются на <see cref="StableName"/>).
/// </summary>
internal sealed class DataType
{
    public required Type Type { get; init; }
    public required string StableName { get; init; }
    /// <summary>Номер CLR-типа в процессе (<see cref="ProcessTypeId{T}"/>) — ключ поиска типа в мире без словаря.</summary>
    public required int ProcessId { get; init; }
    public int Id { get; set; }
    /// <summary>Байт в колонке; у тега — 0 (колонки нет).</summary>
    public required int Size { get; init; }
    public required bool IsTag { get; init; }
    public required bool Reactive { get; init; }
    /// <summary>Смещения полей <see cref="Entity"/> (включая вложенные структуры) — для разрешителя черновиков.</summary>
    public required int[] EntityOffsets { get; init; }
    /// <summary>Типизированные журналы этого типа (создаются при сборке, только для тех, на что подписаны).</summary>
    public IJournalWriter? Writer { get; set; }

    public bool HasEntityFields => EntityOffsets.Length > 0;

    public static DataType Create<T>() where T : unmanaged
    {
        var type = typeof(T);
        bool tag = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
        return new DataType
        {
            Type = type,
            StableName = type.FullName ?? type.Name,
            ProcessId = ProcessTypeId<T>.Value,
            Size = tag ? 0 : Unsafe.SizeOf<T>(),
            IsTag = tag,
            Reactive = type.GetCustomAttribute<ReactiveAttribute>() is not null,
            EntityOffsets = ManagedEntityOffsets<T>(),
        };
    }

    /// <summary>
    /// Смещения всех полей <see cref="Entity"/> в управляемой раскладке <typeparamref name="T"/>, включая вложенные
    /// структуры. <c>Marshal.OffsetOf</c> не годится: он даёт раскладку маршалинга (bool — 4 байта, char — 1), и
    /// разрешитель писал бы мимо поля. Поэтому в каждое поле пишется свой маркер и ищется в байтах значения.
    /// </summary>
    private static int[] ManagedEntityOffsets<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(Entity)) return [0];   // сама сущность как компонент или событие
        var paths = new List<FieldInfo[]>();
        CollectEntityPaths(typeof(T), [], paths, typeof(T));
        if (paths.Count == 0) return [];

        object boxed = default(T);
        for (int k = 0; k < paths.Count; k++)
        {
            var path = paths[k];
            if (path.Length == 1) path[0].SetValue(boxed, Marker(k));
            else path[^1].SetValueDirect(TypedReference.MakeTypedReference(boxed, path[..^1]), Marker(k));
        }
        T value = (T)boxed;
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));

        var offsets = new int[paths.Count];
        for (int k = 0; k < paths.Count; k++)
        {
            var marker = Marker(k);
            var pattern = MemoryMarshal.AsBytes(new ReadOnlySpan<Entity>(in marker));
            int at = bytes.IndexOf(pattern);
            if (at < 0 || bytes[(at + 1)..].IndexOf(pattern) >= 0)
                throw new InvalidOperationException($"{typeof(T).FullName}.{string.Join('.', paths[k].Select(f => f.Name))}: " +
                    "поле Entity не найдено однозначно (перекрытие полей?) — ссылку на сущность не разрешить");
            offsets[k] = at;
        }
        Array.Sort(offsets);
        return offsets;
    }

    /// <summary>Маркер поля: живой сущностью и черновиком быть не может (поколение вне обычных значений).</summary>
    private static Entity Marker(int k) => new(0x5EED0000 + k, 0x7E57AB1E);

    /// <summary>
    /// Пути ко всем полям <see cref="Entity"/>, включая вложенные структуры. Иная форма хранения ссылки (фиксированный
    /// буфер — в нём сущность не распознать) — ошибка регистрации.
    /// </summary>
    private static void CollectEntityPaths(Type type, FieldInfo[] prefix, List<FieldInfo[]> paths, Type root)
    {
        // Встроенный массив ([InlineArray]) виден отражению одним полем: остальные элементы разрешитель бы не увидел.
        if (type.GetCustomAttribute<InlineArrayAttribute>() is not null && ContainsEntity(type))
            throw new InvalidOperationException($"{root.FullName}: встроенный массив ({type.Name}) с Entity — ссылки в нём не разрешить; " +
                "нужны отдельные поля Entity");
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (f.GetCustomAttribute<FixedBufferAttribute>() is not null)
                throw new InvalidOperationException($"{root.FullName}.{f.Name}: фиксированный буфер — ссылки на сущности в нём не распознать");
            FieldInfo[] path = [.. prefix, f];
            if (f.FieldType == typeof(Entity)) paths.Add(path);
            else if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                CollectEntityPaths(f.FieldType, path, paths, root);
        }
    }

    private static bool ContainsEntity(Type type) =>
        type == typeof(Entity) || type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(f => f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && ContainsEntity(f.FieldType));

    public override string ToString() => StableName;
}

/// <summary>
/// Номер CLR-типа в процессе: статическое поле обобщённого класса читается дешевле словаря и потоковой памяти, поэтому
/// обход чанков (<c>Read/Write</c> на каждый чанк) находит тип мира по массиву. Номер нигде не сохраняется.
/// </summary>
internal static class ProcessTypeId
{
    private static int s_last = -1;

    public static int Next() => Interlocked.Increment(ref s_last);
}

internal static class ProcessTypeId<T>
{
    public static readonly int Value = ProcessTypeId.Next();
}

/// <summary>Тип мира по номеру в процессе: массив, дыры — null.</summary>
internal static class ProcessTypeTable
{
    public static DataType?[] Build(DataType[] types)
    {
        var table = new DataType?[types.Length == 0 ? 0 : types.Max(t => t.ProcessId) + 1];
        foreach (var t in types) table[t.ProcessId] = t;
        return table;
    }
}

/// <summary>Типизированная запись срабатываний типа из сырых байтов барьера.</summary>
internal interface IJournalWriter
{
    void Attach(TriggerKind kind, JournalBase journal);
    void Added(Entity e, CauseId cause, long barrier);
    void Removed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier);
    void Changed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier);
    void Event(ReadOnlySpan<byte> value, CauseId cause, long barrier);
}

internal sealed class ComponentJournals<T> : IJournalWriter where T : unmanaged
{
    public Journal<Added<T>>? AddedJournal;
    public Journal<Removed<T>>? RemovedJournal;
    public Journal<Changed<T>>? ChangedJournal;

    public void Attach(TriggerKind kind, JournalBase journal)
    {
        switch (kind)
        {
            case TriggerKind.Added: AddedJournal = (Journal<Added<T>>)journal; break;
            case TriggerKind.Removed: RemovedJournal = (Journal<Removed<T>>)journal; break;
            case TriggerKind.Changed: ChangedJournal = (Journal<Changed<T>>)journal; break;
            default: throw new ArgumentException($"{kind} у компонента");
        }
    }

    public void Added(Entity e, CauseId cause, long barrier) => AddedJournal?.Append(new Added<T>(e, cause), barrier);

    public void Removed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier) =>
        RemovedJournal?.Append(new Removed<T>(e, Read(value), cause), barrier);

    public void Changed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier) =>
        ChangedJournal?.Append(new Changed<T>(e, Read(value), cause), barrier);

    public void Event(ReadOnlySpan<byte> value, CauseId cause, long barrier) => throw new InvalidOperationException();

    private static T Read(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? default : MemoryMarshal.Read<T>(bytes);
}

internal sealed class EventJournal<E> : IJournalWriter where E : unmanaged
{
    public Journal<Event<E>>? Journal;

    public void Attach(TriggerKind kind, JournalBase journal) =>
        Journal = kind == TriggerKind.Event ? (Journal<Event<E>>)journal : throw new ArgumentException($"{kind} у события");

    // Событие без полей (тег) хранится без байтов: значение — default.
    public void Event(ReadOnlySpan<byte> value, CauseId cause, long barrier) =>
        Journal?.Append(new Event<E>(value.Length == 0 ? default : MemoryMarshal.Read<E>(value), cause), barrier);

    public void Added(Entity e, CauseId cause, long barrier) => throw new InvalidOperationException();
    public void Removed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier) => throw new InvalidOperationException();
    public void Changed(Entity e, ReadOnlySpan<byte> value, CauseId cause, long barrier) => throw new InvalidOperationException();
}

/// <summary>Ресурс мира: синглтон. Замена — командой или сразу вне систем; попадает в <see cref="ResourceChanged{T}"/>.</summary>
internal abstract partial class ResourceSlot
{
    public abstract Type Type { get; }
    /// <summary>Применить значение, отложенное командой (номер — из <see cref="ResourceSlot{T}.Stage"/>).</summary>
    public abstract void ApplyStaged(int staged, CauseId cause, long barrier);
    /// <summary>Отложенные значения применены на барьере — место освобождается.</summary>
    public abstract void ClearStaged();
    public abstract void Attach(JournalBase journal);
}

internal sealed partial class ResourceSlot<T> : ResourceSlot
{
    public T Value = default!;
    public bool Set;
    public Journal<ResourceChanged<T>>? Journal;
    // Значения команд до барьера — типизированно: упаковка в object выделяла бы память на каждую команду.
    private T[] _staged = new T[2];
    private int _stagedCount;

    public override Type Type => typeof(T);

    public override void Attach(JournalBase journal) => Journal = (Journal<ResourceChanged<T>>)journal;

    public int Stage(T value)
    {
        if (_stagedCount == _staged.Length) Array.Resize(ref _staged, _staged.Length * 2);
        _staged[_stagedCount] = value;
        return _stagedCount++;
    }

    public override void ApplyStaged(int staged, CauseId cause, long barrier)
    {
        Value = _staged[staged];
        Set = true;
        Journal?.Append(new ResourceChanged<T>(Value, cause), barrier);
    }

    public override void ClearStaged()
    {
        if (_stagedCount == 0) return;
        // Ссылки ссылочного ресурса не держатся до следующей команды.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_staged, 0, _stagedCount);
        _stagedCount = 0;
    }
}
