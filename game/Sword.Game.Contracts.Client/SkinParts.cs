namespace Sword.Game.Contracts;

/// <summary>
/// Отделения последнего сыгранного тика для клиентских модулей (сервис клиента): события <see cref="LimbDetached"/> по порядку. В наблюдателе
/// тиков — события ровно этого тика, как позы <see cref="IFighterPoses"/>.
/// </summary>
public interface ISeverFeed
{
    ulong Tick { get; }

    ReadOnlySpan<SeverRecord> Detached { get; }
}

/// <summary>Отделение тика для клиента: ключ бойца (<see cref="FighterKey"/>) и событие.</summary>
public readonly record struct SeverRecord(ulong Fighter, LimbDetached Event);

/// <summary>
/// Часть меша бойца после разрезов: номер меша рендера и кусок, чьей позой она рисуется (−1 — поза бойца). Изгиб у перелома
/// (<see cref="BendPiece"/> ≥ 0): у меша есть суставы-призраки — суставы сегмента <see cref="BendSegment"/> по порядку номеров, после
/// суставов скелета; они рисуются позой куска перелома <see cref="BendPiece"/>, а кожа у места перелома тянется между ними.
/// </summary>
public readonly record struct SkinPart(int Mesh, int Piece, int BendPiece = -1, int BendSegment = -1);

/// <summary>
/// Меши бойцов после разрезов (сервис клиента): клиентский модуль Cutting режет меш и кладёт сюда части бойца, клиент арены рисует их
/// вместо целого меша. Части бойца без отделений (построен заново) арена снимает (<see cref="Drop"/>), модуль освобождает их меши
/// (<see cref="TakeDropped"/>). Только главный поток клиента.
/// </summary>
public sealed class FighterSkinParts
{
    private readonly Dictionary<ulong, SkinPart[]> _parts = [];
    private readonly List<int> _dropped = [];

    /// <summary>Меш персонажа у рендера (основа производных) и путь к нему в контенте; −1 — ещё не загружен.</summary>
    public int BaseMesh { get; set; } = -1;

    public string MeshPath { get; set; } = "";

    /// <summary>Бойцов с частями.</summary>
    public int Count => _parts.Count;

    /// <summary>Части бойца <paramref name="fighter"/> (<see cref="FighterKey"/>) — заменяют прежние; прежние меши освобождает модуль сам.</summary>
    public void Set(ulong fighter, SkinPart[] parts) => _parts[fighter] = parts;

    public bool TryGet(ulong fighter, out ReadOnlySpan<SkinPart> parts)
    {
        bool found = _parts.TryGetValue(fighter, out var array);
        parts = found ? array : default;
        return found;
    }

    /// <summary>Боец без отделений: его части больше не рисуются; меши — в список освобождения.</summary>
    public void Drop(ulong fighter)
    {
        if (!_parts.Remove(fighter, out var parts)) return;
        foreach (var p in parts)
            if (p.Mesh != BaseMesh) _dropped.Add(p.Mesh);
    }

    /// <summary>Забрать меши, которые больше не рисуются (освобождает их модуль).</summary>
    public void TakeDropped(List<int> into)
    {
        into.AddRange(_dropped);
        _dropped.Clear();
    }
}
