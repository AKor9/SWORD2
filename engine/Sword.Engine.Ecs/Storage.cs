namespace Sword.Engine.Ecs;

/// <summary>Чанк архетипа: строки подряд, колонка на компонент (SoA), сущности строк.</summary>
internal sealed class Chunk
{
    public readonly Entity[] Entities;
    /// <summary>Колонки по порядку <see cref="Archetype.Columns"/>; байты строки i — [i·размер, (i+1)·размер).</summary>
    public readonly byte[][] Columns;
    public int Count;

    public Chunk(Archetype a)
    {
        Entities = new Entity[a.Capacity];
        Columns = new byte[a.Columns.Length][];
        for (int c = 0; c < Columns.Length; c++) Columns[c] = new byte[a.Capacity * a.Columns[c].Size];
    }
}

/// <summary>Архетип: сущности лежат в чанках плотно, удаление строки переносит на её место последнюю. Размер чанка —
/// настройка (<see cref="WorldOptions.ChunkBytes"/>).</summary>
internal sealed class Archetype
{
    public readonly int Id;
    /// <summary>Компоненты архетипа по возрастанию номера (теги тоже).</summary>
    public readonly int[] Components;
    /// <summary>Типы с данными (без тегов) — колонки чанков, по возрастанию номера.</summary>
    public readonly DataType[] Columns;
    /// <summary>Номер компонента → колонка; −1 — нет колонки (нет компонента или тег).</summary>
    public readonly int[] ColumnOf;
    /// <summary>Номер компонента → есть ли он в архетипе.</summary>
    public readonly bool[] Has;
    public readonly int Capacity;
    public readonly List<Chunk> Chunks = [];
    public readonly Dictionary<int, Archetype> AddEdge = [];
    public readonly Dictionary<int, Archetype> RemoveEdge = [];
    public int Count;
    /// <summary>Опустевшие чанки держатся про запас все: число сущностей колеблется, чанки не выделяются заново.</summary>
    private readonly Stack<Chunk> _spares = new();

    public Archetype(int id, int[] components, DataType[] types, int chunkBytes)
    {
        Id = id;
        Components = components;
        Has = new bool[types.Length];
        ColumnOf = new int[types.Length];
        Array.Fill(ColumnOf, -1);
        var columns = new List<DataType>();
        int rowBytes = 8;   // Entity
        foreach (int c in components)
        {
            Has[c] = true;
            if (types[c].IsTag) continue;
            ColumnOf[c] = columns.Count;
            columns.Add(types[c]);
            rowBytes += types[c].Size;
        }
        Columns = [.. columns];
        Capacity = Math.Max(1, chunkBytes / rowBytes);
    }

    /// <summary>Строка в конце архетипа: (чанк, строка).</summary>
    public (int Chunk, int Row) AddRow(Entity e)
    {
        if (Chunks.Count == 0 || Chunks[^1].Count == Capacity)
        {
            Chunks.Add(_spares.Count > 0 ? _spares.Pop() : new Chunk(this));
        }
        int chunk = Chunks.Count - 1;
        var c = Chunks[chunk];
        int row = c.Count++;
        c.Entities[row] = e;
        Count++;
        return (chunk, row);
    }

    public Span<byte> Cell(int chunk, int row, int column)
    {
        int size = Columns[column].Size;
        return Chunks[chunk].Columns[column].AsSpan(row * size, size);
    }

    /// <summary>Убрать строку (на её место — последняя). Вернёт переехавшую сущность или <see cref="Entity.Null"/>.</summary>
    public Entity RemoveRow(int chunk, int row)
    {
        var lastChunk = Chunks[^1];
        int lastRow = lastChunk.Count - 1;
        var target = Chunks[chunk];
        Entity moved = Entity.Null;
        if (!(ReferenceEquals(lastChunk, target) && lastRow == row))
        {
            moved = lastChunk.Entities[lastRow];
            target.Entities[row] = moved;
            for (int c = 0; c < Columns.Length; c++)
            {
                int size = Columns[c].Size;
                lastChunk.Columns[c].AsSpan(lastRow * size, size).CopyTo(target.Columns[c].AsSpan(row * size, size));
            }
        }
        lastChunk.Count--;
        if (lastChunk.Count == 0)
        {
            Chunks.RemoveAt(Chunks.Count - 1);
            _spares.Push(lastChunk);
        }
        Count--;
        return moved;
    }

    public override string ToString() => $"архетип {Id} [{string.Join(", ", Components)}], сущностей {Count}";
}
