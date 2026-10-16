using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>Тип данных мира: Id — индекс в <see cref="WorldReader.Components"/> (или <see cref="WorldReader.Events"/>);
/// значения читаются байтами (<see cref="WorldReader.TryRead"/>), у тега Size 0.</summary>
public sealed record DataTypeInfo(int Id, string Name, Type Type, int Size, bool IsTag, bool Reactive);

/// <summary>Ресурс мира наружу: номер, тип; <see cref="Size"/> 0 — ресурс со ссылками, байтами не читается.</summary>
public sealed record ResourceInfo(int Id, string Name, Type Type, int Size);

/// <summary>Журнал мира: Id — место в порядке по <see cref="Name"/>; EntrySize 0 — запись со ссылками, байтами не читается.</summary>
public sealed record JournalInfo(int Id, string Name, Type EntryType, int EntrySize);

public readonly partial struct WorldReader
{
    /// <summary>Компоненты мира по номерам.</summary>
    public IReadOnlyList<DataTypeInfo> Components => _world.ComponentInfos;

    /// <summary>События мира (в том числе команды тика) по номерам.</summary>
    public IReadOnlyList<DataTypeInfo> Events => _world.EventInfos;

    /// <summary>Ресурсы мира по номерам.</summary>
    public IReadOnlyList<ResourceInfo> Resources => _world.ResourceInfos;

    /// <summary>Слотов индексов сущностей: живые сущности — среди <see cref="EntityAt"/>(0 … <see cref="EntitySlots"/> − 1).</summary>
    public int EntitySlots => _world.EntitySlots;

    /// <summary>Живая сущность в слоте <paramref name="index"/> или <see cref="Entity.Null"/>; обход по возрастанию —
    /// стабильный порядок сущностей (хеш состояния).</summary>
    public Entity EntityAt(int index) => _world.EntityAt(index);

    /// <summary>Байты компонента без копии (у тега пусто); false — сущность мертва или компонента нет. Годны до
    /// следующего изменения мира.</summary>
    public bool TryRead(Entity e, int component, out ReadOnlySpan<byte> bytes) => _world.TryRead(e, component, out bytes);

    /// <summary>Байты ресурса-значения без копии; false — ресурс со ссылками (байтами не отдаётся).</summary>
    public bool TryReadResource(int resource, out ReadOnlySpan<byte> bytes) => _world.TryReadResource(resource, out bytes);

    /// <summary>Свободные номера сущностей: следующая сущность берёт последний. Порядок — часть состояния мира.</summary>
    public ReadOnlySpan<int> FreeIndices => _world.FreeIndices;

    /// <summary>Поколение слота: у живой сущности — её, у свободного слота — то, что получит следующая.</summary>
    public int GenerationAt(int index) => _world.GenerationAt(index);

    /// <summary>Журналы мира в порядке имён; номер журнала — индекс здесь.</summary>
    public IReadOnlyList<JournalInfo> Journals => _world.JournalInfos;

    /// <summary>Записей в журнале сейчас (между тиками — записи последнего оборота тика: их ещё прочтут системы).</summary>
    public int JournalLength(int journal) => _world.JournalAt(journal).Count;

    /// <summary>Запись <paramref name="i"/> журнала (0 — старейшая) байтами на месте; <paramref name="age"/> — сколько
    /// барьеров назад (возраст, а не номер: состояние не зависит от числа сыгранных тиков).</summary>
    public ReadOnlySpan<byte> JournalEntry(int journal, int i, out long age)
    {
        var bytes = _world.JournalAt(journal).EntryBytes(i, out long barrier);
        age = _world.Barrier - barrier;
        return bytes;
    }

    /// <summary>Включена ли регистрация <paramref name="system"/> (номер — место в <see cref="Systems"/>).</summary>
    public bool SystemActive(int system) => _world.ExecutionOrder[system].Active;

    /// <summary>Курсоров журналов у регистрации (срабатывание реактивной — первым).</summary>
    public int CursorCount(int system) => _world.ExecutionOrder[system].Cursors.Count;

    /// <summary>Сколько записей журнала <paramref name="journal"/> курсор <paramref name="i"/> регистрации ещё не прочёл
    /// (число, а не номер записи: состояние не зависит от истории журнала).</summary>
    public int CursorUnread(int system, int i, out int journal)
    {
        journal = _world.CursorJournals[system][i];
        var c = _world.ExecutionOrder[system].Cursors[i];
        return (int)(c.Journal.End - Math.Max(c.Cursor, c.Journal.First));
    }

}

public sealed partial class World
{
    private DataTypeInfo[]? _componentInfos, _eventInfos;

    internal IReadOnlyList<DataTypeInfo> ComponentInfos => _componentInfos ??= [.. _types.Select(Info)];

    internal IReadOnlyList<DataTypeInfo> EventInfos => _eventInfos ??= [.. _eventTypes.Select(Info)];

    internal IReadOnlyList<ResourceInfo> ResourceInfos =>
        [.. _resources.Select((r, i) => new ResourceInfo(i, r.Type.FullName ?? r.Type.Name, r.Type, r.DataSize))];

    private static DataTypeInfo Info(DataType t) => new(t.Id, t.StableName, t.Type, t.Size, t.IsTag, t.Reactive);

    internal int EntitySlots => _entityHigh;

    internal ReadOnlySpan<int> FreeIndices => _free.AsSpan(0, _freeCount);

    internal int GenerationAt(int index) =>
        (uint)index < (uint)_entityHigh ? _generations[index] : throw new ArgumentOutOfRangeException(nameof(index));

    // Журналы и регистрации заводит только сборка мира — порядок для обхода строится один раз, при первом запросе.
    private JournalBase[]? _journalOrder;
    private JournalInfo[]? _journalInfos;
    private Registration[]? _executionOrder;
    private int[][]? _cursorJournals;

    internal IReadOnlyList<JournalInfo> JournalInfos
    {
        get
        {
            if (_journalInfos is not null) return _journalInfos;
            var named = _journals.Select(p => (Name: $"{p.Key.Kind}<{p.Key.Data.FullName ?? p.Key.Data.Name}>", Journal: p.Value))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
            _journalOrder = [.. named.Select(p => p.Journal)];
            return _journalInfos = [.. named.Select((p, i) => new JournalInfo(i, p.Name, p.Journal.EntryType, p.Journal.EntrySize))];
        }
    }

    internal JournalBase JournalAt(int journal)
    {
        if (_journalOrder is null) _ = JournalInfos;
        return _journalOrder![journal];
    }

    internal Registration[] ExecutionOrder => _executionOrder ??= [.. _order.SelectMany(o => o)];

    internal int[][] CursorJournals => _cursorJournals ?? BuildCursorJournals();

    // Отдельный метод: объект замыкания лямбды создаётся на входе в метод — в горячем геттере он выделялся бы каждый раз.
    private int[][] BuildCursorJournals()
    {
        if (_journalOrder is null) _ = JournalInfos;
        var ids = _journalOrder!.Select((j, i) => (j, i)).ToDictionary(p => p.j, p => p.i);
        return _cursorJournals = [.. ExecutionOrder.Select(r => r.Cursors.Select(c => ids[c.Journal]).ToArray())];
    }

    internal Entity EntityAt(int index) =>
        (uint)index < (uint)_entityHigh && _archetypeOf[index] is not null ? new Entity(index, _generations[index]) : Entity.Null;

    internal bool TryRead(Entity e, int component, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if ((uint)component >= (uint)_types.Length) throw new ArgumentOutOfRangeException(nameof(component));
        if (!IsAlive(e)) return false;
        var a = _archetypeOf[e.Index]!;
        if (!a.Has[component]) return false;
        if (!_types[component].IsTag) bytes = a.Cell(_chunkOf[e.Index], _rowOf[e.Index], a.ColumnOf[component]);
        return true;
    }

    internal bool TryReadResource(int resource, out ReadOnlySpan<byte> bytes)
    {
        if ((uint)resource >= (uint)_resources.Count) throw new ArgumentOutOfRangeException(nameof(resource));
        bytes = _resources[resource].Bytes;
        return _resources[resource].DataSize > 0;
    }

}

internal abstract partial class ResourceSlot
{
    /// <summary>Размер значения в байтах; 0 — тип со ссылками (байтами не отдаётся).</summary>
    public abstract int DataSize { get; }

    /// <summary>Байты значения без копии; у типа со ссылками — пусто.</summary>
    public abstract ReadOnlySpan<byte> Bytes { get; }
}

internal sealed partial class ResourceSlot<T>
{
    private static readonly bool Data = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public override int DataSize => Data ? Unsafe.SizeOf<T>() : 0;

    public override ReadOnlySpan<byte> Bytes =>
        Data ? MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Value), Unsafe.SizeOf<T>()) : default;

}
