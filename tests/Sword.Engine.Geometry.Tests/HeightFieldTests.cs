using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Engine.Geometry.Tests;

public class HeightFieldTests
{
    private static float Wave(float x, float z) => MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.4f) + 0.1f * x;

    [Fact]
    public void NodesHoldFunctionValues()
    {
        var f = HeightField.FromFunction(64, 8, Wave);
        Assert.Equal(65, f.Nodes);
        Assert.Equal(0.25f, f.Step);
        for (int k = 0; k < f.Nodes; k += 7)
            for (int i = 0; i < f.Nodes; i += 5)
            {
                Assert.Equal(Wave(f.NodeCoord(i), f.NodeCoord(k)), f.Node(i, k));
                Assert.Equal(f.Node(i, k), f.Sample(f.NodeCoord(i), f.NodeCoord(k)), 5);
            }
    }

    [Fact]
    public void PlaneIsExactEverywhere()
    {
        static float Plane(float x, float z) => 0.3f * x - 0.2f * z + 1;
        var f = HeightField.FromFunction(16, 4, Plane);
        var rng = new Random(7);
        for (int n = 0; n < 200; n++)
        {
            float x = (float)(rng.NextDouble() * 8 - 4), z = (float)(rng.NextDouble() * 8 - 4);
            Assert.Equal(Plane(x, z), f.Sample(x, z), 4);
            var normal = f.Normal(x, z);
            var expected = Vector3.Normalize(new Vector3(-0.3f, 1, 0.2f));
            Assert.True(Vector3.Distance(expected, normal) < 1e-4f, $"{normal} ≠ {expected}");
        }
    }

    [Fact]
    public void SampleSplitsCellAlongMainDiagonal()
    {
        // Одна ячейка 1×1: только угол (1, 0) поднят. Ниже диагонали (u ≥ v) он тянет высоту, выше — нет.
        var f = new HeightField(1, 0.5f, [0, 1, 0, 0]);
        Assert.Equal(0.5f, f.Sample(0, -0.5f), 5);   // середина нижнего ребра
        Assert.Equal(0f, f.Sample(-0.25f, 0.25f), 5); // треугольник над диагональю
        Assert.Equal(0.25f, f.Sample(0.25f, 0), 5);   // u = .75, v = .5: .75 − .5
    }

    [Fact]
    public void OutsideClampsToEdge()
    {
        var f = HeightField.FromFunction(8, 2, Wave);
        Assert.Equal(f.Sample(2, 1), f.Sample(50, 1));
        Assert.Equal(f.Sample(-2, -2), f.Sample(-9, -30));
    }

    [Fact]
    public void WrongHeightCountIsRejected() =>
        Assert.Throws<ArgumentException>(() => new HeightField(4, 1, new float[24]));
}
