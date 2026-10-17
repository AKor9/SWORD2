using System.Numerics;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Client;

/// <summary>
/// Рельеф арены для клиентских модулей (<see cref="ITerrainView"/>): функции — <see cref="ArenaTerrain"/> (формулы не копируются), буфер
/// высот — тот, по которому клиент арены рисует рельеф (<c>Arena.Heights</c>); его SRV задаёт клиент, когда рендер готов.
/// </summary>
public sealed class ArenaTerrainView : ITerrainView
{
    public float Height(float x, float z) => ArenaTerrain.Height(x, z);

    public float SmoothHeight(float x, float z, out Vector2 gradient) => ArenaTerrain.SmoothHeightAt(x, z, out gradient);

    public float StepHeight(float x, float z) => ArenaTerrain.StepHeightAt(x, z);

    public Vector3 Normal(float x, float z) => ArenaTerrain.Normal(x, z);

    public TerrainGrid Grid { get; } = new(ArenaTerrain.Cells, ArenaTerrain.Half);

    public int HeightsSrv { get; internal set; } = -1;
}
