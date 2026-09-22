using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>
/// Тип данных мира наружу — для интроспекции (агент, инструменты): номер, стабильное имя (полное имя типа), тип CLR,
/// размер (у тега 0), реактивность. Номер — индекс в <see cref="WorldReader.Components"/> (или
/// <see cref="WorldReader.Events"/>); значения читаются байтами (<see cref="WorldReader.TryCopy"/>), разбирает их
/// вызывающий по типу CLR.
/// </summary>
public sealed record DataTypeInfo(int Id, string Name, Type Type, int Size, bool IsTag, bool Reactive);

/// <summary>Ресурс мира наружу: номер, тип; <see cref="Size"/> 0 — ресурс со ссылками, байтами не читается.</summary>
public sealed record ResourceInfo(int Id, string Name, Type Type, int Size);

/// <summary>
/// Журнал мира наружу: номер — место в порядке по имени <see cref="Name"/> (<c>Added&lt;полное имя компонента&gt;</c>,
/// <c>Event&lt;…&gt;</c>…), тип записи и её размер (0 — запись со ссылками, байтами не читается).
/// </summary>
public sealed record JournalInfo(int Id, string Name, Type EntryType, int EntrySize);

public readonly partial struct WorldReader
{
    /// <summary>Компоненты мира по номерам.</summary>
    public IReadOnlyList<DataTypeInfo> Components => _world.ComponentInfos;

    /// <summary>События мира (в том числе команды тика) по номерам.</summary>
    public IReadOnlyList<DataTypeInfo> Events => _world.EventInfos;

    /// <summary>Ресурсы мира по номерам.</summary>
    public IReadOnlyList<ResourceInfo> Resources => _world.ResourceInfos;

    /// <summary>
    /// Копия байтов компонента <paramref name="component"/> сущности (<see cref="DataTypeInfo.Size"/> байт; у тега —
    /// пустой массив); null — сущность мертва или компонента у неё нет. Копия — новый массив: путь инструментов, не
    /// горячий, и в мир через него не пишется.
    /// </summary>
    public byte[]? Copy(Entity e, int component)
    {
        if ((uint)component >= (uint)_world.ComponentInfos.Count) throw new ArgumentOutOfRangeException(nameof(component));
        var bytes = new byte[_world.ComponentInfos[component].Size];
        return _world.TryCopy(e, component, bytes) ? bytes : null;
    }

    /// <summary>Номера компонентов сущности по возрастанию (пусто — мертва).</summary>
    public void ComponentsOf(Entity e, List<int> components) => _world.ComponentsOf(e, components);

    /// <summary>
    /// Сущности со всеми <paramref name="with"/> и без <paramref name="without"/> — по возрастанию индекса; в
    /// <paramref name="page"/> — не больше <paramref name="take"/> после первых <paramref name="skip"/>. Возвращает, сколько
    /// сущностей подходит всего.
    /// </summary>
    public int Select(ReadOnlySpan<int> with, ReadOnlySpan<int> without, int skip, int take, List<Entity> page) =>
        _world.Select(with, without, skip, take, page);

    /// <summary>Слотов индексов сущностей: живые сущности — среди <see cref="EntityAt"/>(0 … <see cref="EntitySlots"/> − 1).</summary>
    public int EntitySlots => _world.EntitySlots;

    /// <summary>
    /// Живая сущность в слоте индекса <paramref name="index"/>; нет — <see cref="Entity.Null"/>. Обход слотов по
    /// возрастанию — стабильный порядок сущностей (хеш состояния, описание), без выделения памяти.
    /// </summary>
    public Entity EntityAt(int index) => _world.EntityAt(index);

    /// <summary>Номера компонентов живой сущности по возрастанию, без копии; мёртвая — пусто.</summary>
    public ReadOnlySpan<int> ComponentIds(Entity e) => _world.ComponentIds(e);

    /// <summary>
    /// Байты компонента <paramref name="component"/> сущности — вид в хранилище без копии (у тега пусто); false —
    /// сущность мертва или компонента нет. Вид годен до следующего изменения мира: только чтение на месте.
    /// </summary>
    public bool TryRead(Entity e, int component, out ReadOnlySpan<byte> bytes) => _world.TryRead(e, component, out bytes);

    /// <summary>Байты ресурса-значения без копии; false — ресурс со ссылками (байтами не отдаётся).</summary>
    public bool TryReadResource(int resource, out ReadOnlySpan<byte> bytes) => _world.TryReadResource(resource, out bytes);

    /// <summary>
    /// Свободные номера сущностей в порядке выдачи с конца: следующая созданная сущность берёт последний. От этого
    /// порядка зависят номера будущих сущностей — он часть состояния мира.
    /// </summary>
    public ReadOnlySpan<int> FreeIndices => _world.FreeIndices;

    /// <summary>
    /// Поколение слота <paramref name="index"/> (0 … <see cref="EntitySlots"/> − 1): у живой сущности — её поколение, у
    /// свободного слота — поколение, которое получит следующая сущность в нём.
    /// </summary>
    public int GenerationAt(int index) => _world.GenerationAt(index);

    /// <summary>Журналы мира в порядке имён; номер журнала — индекс здесь.</summary>
    public IReadOnlyList<JournalInfo> Journals => _world.JournalInfos;

    /// <summary>Записей в журнале сейчас (между тиками — записи последнего оборота тика: их ещё прочтут системы).</summary>
    public int JournalLength(int journal) => _world.JournalAt(journal).Count;

    /// <summary>
    /// Запись <paramref name="i"/> журнала (с 0 — старейшая) байтами на месте, у записи со ссылками — пусто; в
    /// <paramref name="age"/> — сколько барьеров назад она сделана (0 — немедленной операцией после последнего барьера).
    /// Возраст, а не номер барьера: состояние не зависит от того, сколько тиков сыграно. Только чтение, годна до
    /// следующего изменения мира.
    /// </summary>
    public ReadOnlySpan<byte> JournalEntry(int journal, int i, out long age)
    {
        var bytes = _world.JournalAt(journal).EntryBytes(i, out long barrier);
        age = _world.Barrier - barrier;
        return bytes;
    }

    /// <summary>Регистраций систем — в порядке исполнения, как <see cref="Systems"/>.</summary>
    public int SystemCount => _world.ExecutionOrder.Length;

    /// <summary>Включена ли регистрация <paramref name="system"/> (номер — место в <see cref="Systems"/>).</summary>
    public bool SystemActive(int system) => _world.ExecutionOrder[system].Active;

    /// <summary>Курсоров журналов у регистрации (срабатывание реактивной — первым).</summary>
    public int CursorCount(int system) => _world.ExecutionOrder[system].Cursors.Count;

    /// <summary>
    /// Курсор <paramref name="i"/> регистрации: сколько записей журнала (номер — <paramref name="journal"/> в
    /// <see cref="Journals"/>) она ещё не прочла. Число, а не номер записи: состояние не зависит от истории журнала.
    /// </summary>
    public int CursorUnread(int system, int i, out int journal)
    {
        journal = _world.CursorJournals[system][i];
        var c = _world.ExecutionOrder[system].Cursors[i];
        return (int)(c.Journal.End - Math.Max(c.Cursor, c.Journal.First));
    }

    /// <summary>Копия байтов ресурса (<see cref="ResourceInfo.Size"/>); null — ресурс со ссылками.</summary>
    public byte[]? CopyResource(int resource)
    {
        var info = _world.ResourceInfos[resource];
        var bytes = new byte[info.Size];
        return _world.TryCopyResource(resource, bytes) ? bytes : null;
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

    internal bool TryCopy(Entity e, int component, Span<byte> destination)
    {
        if ((uint)component >= (uint)_types.Length) throw new ArgumentOutOfRangeException(nameof(component));
        if (!IsAlive(e)) return false;
        var a = _archetypeOf[e.Index]!;
        if (!a.Has[component]) return false;
        var t = _types[component];
        if (!t.IsTag) a.Cell(_chunkOf[e.Index], _rowOf[e.Index], a.ColumnOf[component]).CopyTo(destination);
        return true;
    }

    internal void ComponentsOf(Entity e, List<int> components)
    {
        components.Clear();
        if (IsAlive(e)) components.AddRange(_archetypeOf[e.Index]!.Components);
    }

    internal int Select(ReadOnlySpan<int> with, ReadOnlySpan<int> without, int skip, int take, List<Entity> page)
    {
        page.Clear();
        var all = new List<Entity>();
        foreach (var a in Archetypes)
        {
            if (a.Count == 0 || !Matches(a, with, without)) continue;
            foreach (var chunk in a.Chunks)
                for (int i = 0; i < chunk.Count; i++) all.Add(chunk.Entities[i]);
        }
        all.Sort((x, y) => x.Index.CompareTo(y.Index));
        for (int i = Math.Max(skip, 0); i < all.Count && page.Count < take; i++) page.Add(all[i]);
        return all.Count;
    }

    private bool Matches(Archetype a, ReadOnlySpan<int> with, ReadOnlySpan<int> without)
    {
        foreach (int c in with)
            if ((uint)c >= (uint)_types.Length || !a.Has[c]) return false;
        foreach (int c in without)
            if ((uint)c < (uint)_types.Length && a.Has[c]) return false;
        return true;
    }

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

    internal ReadOnlySpan<int> ComponentIds(Entity e) => IsAlive(e) ? _archetypeOf[e.Index]!.Components : default;

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

    internal bool TryCopyResource(int resource, Span<byte> destination)
    {
        if ((uint)resource >= (uint)_resources.Count) throw new ArgumentOutOfRangeException(nameof(resource));
        return _resources[resource].TryCopy(destination);
    }
}

internal abstract partial class ResourceSlot
{
    /// <summary>Размер значения в байтах; 0 — тип со ссылками (байтами не отдаётся).</summary>
    public abstract int DataSize { get; }

    public abstract bool TryCopy(Span<byte> destination);

    /// <summary>Байты значения без копии; у типа со ссылками — пусто.</summary>
    public abstract ReadOnlySpan<byte> Bytes { get; }
}

internal sealed partial class ResourceSlot<T>
{
    private static readonly bool Data = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public override int DataSize => Data ? Unsafe.SizeOf<T>() : 0;

    public override ReadOnlySpan<byte> Bytes =>
        Data ? MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Value), Unsafe.SizeOf<T>()) : default;

    public override bool TryCopy(Span<byte> destination)
    {
        if (!Data) return false;
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Value), Unsafe.SizeOf<T>()).CopyTo(destination);
        return true;
    }
}
