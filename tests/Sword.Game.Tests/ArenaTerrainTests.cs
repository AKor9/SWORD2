using Sword.Game.Arena;

namespace Sword.Game.Tests;

public class ArenaTerrainTests
{
    [Fact]
    public void FieldCoversSwordRenderGrid()
    {
        // Сетка отрисовки SWORD: 256 ячеек на ±32 м, шаг 0,25 м — поле высот в тех же узлах.
        var f = ArenaTerrain.BuildField();
        Assert.Equal(257, f.Nodes);
        Assert.Equal(0.25f, f.Step);
        Assert.Equal(-32f, f.NodeCoord(0));
        Assert.Equal(32f, f.NodeCoord(256));
    }

    [Fact]
    public void FieldNodesAreFunctionValues()
    {
        var f = ArenaTerrain.BuildField();
        for (int k = 0; k < f.Nodes; k += 3)
            for (int i = 0; i < f.Nodes; i += 3)
                Assert.Equal(ArenaTerrain.Height(f.NodeCoord(i), f.NodeCoord(k)), f.Node(i, k));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]             // центр ровный
    [InlineData(-16f, 0f, 0.6f)]         // вершина пирамиды: 5 ярусов × 12 см
    [InlineData(-16f, 5.5f, 0.12f)]      // первый ярус
    [InlineData(0f, 18f, 1.6f)]          // вершина склона
    public void KnownHeights(float x, float z, float expected) =>
        Assert.Equal(expected, ArenaTerrain.Height(x, z), 4);

    [Fact]
    public void ZonesAreFoundAtTheirCentres()
    {
        for (int i = 0; i < ArenaTerrain.Zones.Length; i++)
        {
            var c = ArenaTerrain.Zones[i].Centre;
            Assert.Equal(i, ArenaTerrain.ZoneAt(c.X, c.Y));
        }
        Assert.Equal(-1, ArenaTerrain.ZoneAt(0, 0));
    }

    [Fact]
    public void PillarsMatchSwordLayout()
    {
        Assert.Equal(8, ArenaLayout.Pillars.Length);
        var p0 = ArenaLayout.Pillars[0];
        Assert.Equal(MathF.Cos(0.39f) * 30, p0.X, 5);
        Assert.Equal(MathF.Sin(0.39f) * 30, p0.Y, 5);
        Assert.Equal(1.2f, p0.Z);
        Assert.Equal(6.5f, p0.W);
        Assert.Equal(44f, new System.Numerics.Vector2(ArenaLayout.Pillars[1].X, ArenaLayout.Pillars[1].Y).Length(), 4);
    }
}
