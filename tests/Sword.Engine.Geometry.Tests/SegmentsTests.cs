using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Engine.Geometry.Tests;

/// <summary>
/// <see cref="Segments.SegSeg"/> (этап 6г): первые два — тесты <c>XpbdTests</c> SWORD 2eab13b (<c>SegSeg_CrossingSegments</c>,
/// <c>SegSeg_ParallelSegmentsClampToEnds</c>), дальше — края: вырожденные отрезки, параллельные с перекрытием, концы.
/// </summary>
public class SegmentsTests
{
    [Fact]
    public void CrossingSegments()
    {
        float d2 = Segments.SegSeg(new(-1, 0, 0), new(1, 0, 0), new(0, -1, 1), new(0, 1, 1), out var c1, out var c2);
        Assert.Equal(1f, d2, 5);
        Assert.True(c1.Length() < 1e-5f);
        Assert.True(Vector3.Distance(c2, new Vector3(0, 0, 1)) < 1e-5f);
    }

    [Fact]
    public void ParallelSegmentsClampToEnds()
    {
        float d2 = Segments.SegSeg(new(0, 0, 0), new(1, 0, 0), new(2, 1, 0), new(3, 1, 0), out var c1, out var c2);
        Assert.Equal(new Vector3(1, 0, 0), c1);
        Assert.Equal(new Vector3(2, 1, 0), c2);
        Assert.Equal(2f, d2, 5);
    }

    [Fact]
    public void ParallelOverlapping_FirstFromStart()
    {
        // параллельные внахлёст: s = 0 (начало первого), на втором — ближайшая к нему точка
        float d2 = Segments.SegSeg(new(0, 0, 0), new(2, 0, 0), new(-1, 0.5f, 0), new(1, 0.5f, 0), out var c1, out var c2);
        Assert.Equal(new Vector3(0, 0, 0), c1);
        Assert.Equal(new Vector3(0, 0.5f, 0), c2);
        Assert.Equal(0.25f, d2, 6);
    }

    [Fact]
    public void DegenerateSegments_ArePoints()
    {
        // обе — точки
        float d2 = Segments.SegSeg(new(1, 2, 3), new(1, 2, 3), new(1, 2, 5), new(1, 2, 5), out var c1, out var c2);
        Assert.Equal(new Vector3(1, 2, 3), c1);
        Assert.Equal(new Vector3(1, 2, 5), c2);
        Assert.Equal(4f, d2, 6);
        // первая — точка над серединой второго
        d2 = Segments.SegSeg(new(0.5f, 1, 0), new(0.5f, 1, 0), new(0, 0, 0), new(1, 0, 0), out c1, out c2);
        Assert.Equal(new Vector3(0.5f, 1, 0), c1);
        Assert.Equal(new Vector3(0.5f, 0, 0), c2);
        Assert.Equal(1f, d2, 6);
        // Вторая — точка: у SWORD (Ericson без отдельной ветки «второй отрезок — точка») s остаётся 0 — начало первого, а не
        // ближайшая к точке. Так и перенесено (дословно); в игре второй отрезок — ось капсулы тела, она не вырождена.
        d2 = Segments.SegSeg(new(0, 0, 0), new(1, 0, 0), new(3, 0, 0), new(3, 0, 0), out c1, out c2);
        Assert.Equal(new Vector3(0, 0, 0), c1);
        Assert.Equal(new Vector3(3, 0, 0), c2);
        Assert.Equal(9f, d2, 6);
    }

    [Fact]
    public void SkewSegments_ClosestPointsOnEnds()
    {
        // скрещённые: ближайшее на первом — его конец (s обрезано до 1), на втором — середина
        float d2 = Segments.SegSeg(new(0, 0, 0), new(4, 0, 0), new(6, 1, -1), new(6, 1, 1), out var c1, out var c2);
        Assert.Equal(new Vector3(4, 0, 0), c1);
        Assert.Equal(new Vector3(6, 1, 0), c2);
        Assert.Equal(5f, d2, 6);
        // параллельные вдоль одной оси, второй — за началом первого: t > 1 обрезано, s — от конца второго
        d2 = Segments.SegSeg(new(0, 0, 0), new(0, 0, 4), new(1, 0, -3), new(1, 0, -1), out c1, out c2);
        Assert.Equal(new Vector3(0, 0, 0), c1);
        Assert.Equal(new Vector3(1, 0, -1), c2);
        Assert.Equal(2f, d2, 6);
        // пересекающиеся — расстояние 0, точки совпали
        d2 = Segments.SegSeg(new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), out c1, out c2);
        Assert.Equal(0f, d2, 6);
        Assert.True(Vector3.Distance(c1, c2) < 1e-6f);
    }
}
