using System.Numerics;

namespace Sword.Engine.Render.Tests;

/// <summary>Матрица карты теней солнца без устройства: шар — в карте, ближе к солнцу — глубже, центр — к сетке текселей.</summary>
public sealed class SunShadowMathTests
{
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.45f, -1f, -0.3f));

    private static Vector3 Project(in Matrix4x4 m, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), m);
        return new Vector3(c.X, c.Y, c.Z) / c.W;
    }

    [Fact]
    public void Sphere_InsideMap_CloserToSunIsDeeper()
    {
        var center = new Vector3(5, 0, -3);
        var m = SunShadowMath.ViewProj(Sun, center, 12f, 2048, out float texel, out float range);
        Assert.Equal(24f / 2048, texel, 6);
        var rng = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            var dir = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var p = center + dir * 12f * rng.NextSingle();
            var c = Project(m, p);
            Assert.InRange(c.X, -1.001f, 1.001f);
            Assert.InRange(c.Y, -1.001f, 1.001f);
            Assert.InRange(c.Z, 0f, 1f);
            // Шаг к солнцу на 1 м — глубина больше на 1 / глубину карты; поперёк света — та же.
            Assert.Equal(c.Z + 1 / range, Project(m, p - Sun).Z, 4);
            var across = Vector3.Normalize(Vector3.Cross(Sun, Vector3.UnitX));
            Assert.Equal(c.Z, Project(m, p + across).Z, 4);
        }
    }

    [Fact]
    public void MovingCenter_KeepsWorldOnTexelGrid()
    {
        // Точка мира под одной и той же долей текселя, как бы ни ехал шар: тень не дрожит.
        var world = new Vector3(1.234f, 0.5f, -2.71f);
        float? fraction = null;
        for (int i = 0; i < 40; i++)
        {
            var center = new Vector3(i * 0.0137f, 0, -i * 0.021f);
            var m = SunShadowMath.ViewProj(Sun, center, 10f, 1024, out _, out _);
            var c = Project(m, world);
            float tx = (c.X * 0.5f + 0.5f) * 1024;
            float f = tx - MathF.Floor(tx);
            fraction ??= f;
            float d = MathF.Abs(f - fraction.Value);
            Assert.True(MathF.Min(d, 1 - d) < 0.01f, $"доля текселя {f} против {fraction}");
        }
    }
}
