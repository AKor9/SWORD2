using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>
/// Построитель запроса: <c>Read/Write</c> — компонент нужен и читается/пишется, <c>With/Without</c> — фильтр. Неизменяемый:
/// каждый шаг — новый построитель, поэтому общая основа (<c>var b = Query().Read&lt;A&gt;()</c>) не меняется ветками.
/// </summary>
public readonly struct QueryBuilder
{
    private readonly World _world;
    private readonly int[] _all, _none, _write;

    internal QueryBuilder(World world) : this(world, [], [], []) { }

    private QueryBuilder(World world, int[] all, int[] none, int[] write)
    {
        _world = world;
        _all = all;
        _none = none;
        _write = write;
    }

    public QueryBuilder Read<T>() where T : unmanaged => With<T>();

    public QueryBuilder Write<T>() where T : unmanaged
    {
        int id = _world.TypeOf<T>().Id;
        var next = With<T>();
        return next._write.Contains(id) ? next : new(_world, next._all, next._none, [.. next._write, id]);
    }

    public QueryBuilder With<T>() where T : unmanaged
    {
        int id = _world.TypeOf<T>().Id;
        return _all.Contains(id) ? this : new(_world, [.. _all, id], _none, _write);
    }

    public QueryBuilder Without<T>() where T : unmanaged
    {
        int id = _world.TypeOf<T>().Id;
        return _none.Contains(id) ? this : new(_world, _all, [.. _none, id], _write);
    }

    public Query Build() => new(_world, [.. _all], [.. _none], [.. _write]);

    public static implicit operator Query(QueryBuilder b) => b.Build();
}

/// <summary>Запрос: подходящие архетипы кешируются и досчитываются, когда в мире появляются новые.</summary>
public sealed class Query
{
    private readonly World _world;

    internal World World => _world;
    internal readonly int[] All, None, Write;
    internal readonly List<Archetype> Matched = [];
    private int _seen;

    internal Query(World world, int[] all, int[] none, int[] write)
    {
        _world = world;
        All = all;
        None = none;
        Write = write;
    }

    internal List<Archetype> Archetypes()
    {
        var archetypes = _world.Archetypes;
        for (; _seen < archetypes.Count; _seen++)
            if (Matches(archetypes[_seen])) Matched.Add(archetypes[_seen]);
        return Matched;
    }

    private bool Matches(Archetype a)
    {
        foreach (int c in All)
            if (!a.Has[c]) return false;
        foreach (int c in None)
            if (a.Has[c]) return false;
        return true;
    }

    /// <summary>Сущностей под запросом сейчас.</summary>
    public int Count()
    {
        int n = 0;
        foreach (var a in Archetypes()) n += a.Count;
        return n;
    }
}

/// <summary>Обход чанков запроса без выделений памяти: <c>foreach (var chunk in ctx.Iterate(q))</c>.</summary>
public struct QueryIterator
{
    private readonly World _world;
    private readonly List<Archetype> _archetypes;
    private readonly Registration? _registration;
    private int _archetype, _chunk;

    internal QueryIterator(World world, Query query, Registration? registration)
    {
        _world = world;
        _archetypes = query.Archetypes();
        _registration = registration;
        _archetype = 0;
        _chunk = -1;
    }

    public readonly QueryIterator GetEnumerator() => this;

    public bool MoveNext()
    {
        while (_archetype < _archetypes.Count)
        {
            if (++_chunk < _archetypes[_archetype].Chunks.Count) return true;
            _archetype++;
            _chunk = -1;
        }
        return false;
    }

    public readonly ChunkView Current => new(_world, _archetypes[_archetype], _archetypes[_archetype].Chunks[_chunk], _registration);
}

/// <summary>Чанк в обходе: сущности строк и колонки компонентов.</summary>
public readonly struct ChunkView
{
    private readonly World _world;
    private readonly Archetype _archetype;
    private readonly Chunk _chunk;
    private readonly Registration? _registration;

    internal ChunkView(World world, Archetype archetype, Chunk chunk, Registration? registration)
    {
        _world = world;
        _archetype = archetype;
        _chunk = chunk;
        _registration = registration;
    }

    public int Count => _chunk.Count;

    public ReadOnlySpan<Entity> Entities => _chunk.Entities.AsSpan(0, _chunk.Count);

    public ReadOnlySpan<T> Read<T>() where T : unmanaged => Column<T>(_world.TypeOf<T>());

    /// <summary>
    /// Колонка на запись на месте. Реактивный компонент так не пишется — только командой <c>Set</c> (проверки мира:
    /// исключение). Отложенное значение черновика в поле <see cref="Entity"/> здесь запрещено: проверки мира ловят его
    /// после исполнения системы.
    /// </summary>
    public Span<T> Write<T>() where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (_world.Checks)
        {
            if (t.Reactive)
                throw new InvalidOperationException($"{_registration?.Name ?? "мир"}: {t.StableName} реактивный — меняется только командой Set");
            if (t.HasEntityFields) _world.NoteWritten(_archetype, _chunk, t);
        }
        return Column<T>(t);
    }

    private Span<T> Column<T>(DataType t) where T : unmanaged
    {
        int column = _archetype.ColumnOf[t.Id];
        if (column < 0)
        {
            if (_archetype.Has[t.Id]) throw new InvalidOperationException($"{t.StableName} — тег: колонки нет");
            throw new InvalidOperationException($"в чанке нет {t.StableName}: добавьте его в запрос (Read/Write/With)");
        }
        return MemoryMarshal.Cast<byte, T>(_chunk.Columns[column].AsSpan(0, _chunk.Count * t.Size));
    }
}
