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
}

internal sealed partial class ResourceSlot<T>
{
    private static readonly bool Data = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public override int DataSize => Data ? Unsafe.SizeOf<T>() : 0;

    public override bool TryCopy(Span<byte> destination)
    {
        if (!Data) return false;
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<T, byte>(ref Value), Unsafe.SizeOf<T>()).CopyTo(destination);
        return true;
    }
}
