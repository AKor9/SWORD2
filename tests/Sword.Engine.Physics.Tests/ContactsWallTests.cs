using System.Numerics;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Стена арены и колонны (7г): вся капсула остаётся внутри стены и снаружи колонн (торцы у стены, ближайшая точка оси у колонны),
/// скорость внутрь гасится, утопленное тело выходит не быстрее скорости выталкивания, проверки аргументов.
/// </summary>
public class ContactsWallTests
{
    private const float WallRadius = 10, R = 0.08f, Half = 0.25f;

    /// <summary>Горизонтальное расстояние от точки до вертикали (x, z).</summary>
    private static float Horizontal(Vector3 p, float x, float z) => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));

    /// <summary>Наименьшее горизонтальное расстояние от оси капсулы до вертикали (x, z) — перебором точек оси.</summary>
    private static float AxisToVertical(in BodyState s, float x, float z)
    {
        float min = float.MaxValue;
        for (int i = 0; i <= 200; i++) min = MathF.Min(min, Horizontal(Vector3.Lerp(s.EndA, s.EndB, i / 200f), x, z));
        return min;
    }

    public static TheoryData<string, Vector3> WallThrows => new()
    {
        { "по радиусу", Vector3.UnitX },
        { "поперёк", Vector3.UnitZ },
        { "вертикально", Vector3.UnitY },
        { "наискось", new Vector3(1, 1, 1) },
    };

    [Theory]
    [MemberData(nameof(WallThrows))]
    public void Wall_KeepsWholeCapsuleInside_KillsOutwardVelocity(string name, Vector3 axis)
    {
        var w = World();
        w.SetWall(WallRadius);
        var b = Simulated(w, 3, R, Half, new Vector3(WallRadius - 1, 1, 0.5f), AxisAlong(axis), ProfileVacuum);
        var s = w.Get(b);
        s.Velocity = new Vector3(6, 0, 1);
        s.AngularVelocity = new Vector3(0, 2, 3);
        w.Set(b, s);
        float limit = WallRadius - R;
        for (int t = 0; t < 120; t++)
        {
            w.Step(Dt);
            s = w.Get(b);
            // Удар 6 м/с (12 мм за подшаг) решается в том же подшаге; остаток — от торцов по очереди (замер: до 1,6e-4 м).
            float far = MathF.Max(Horizontal(s.EndA, 0, 0), Horizontal(s.EndB, 0, 0));
            Assert.True(far <= limit + 4e-4f, $"{name}, тик {t}: торец на {far - limit:E2} м за стеной");
        }
        var radial = Vector3.Normalize(new Vector3(s.Position.X, 0, s.Position.Z));
        Assert.True(Vector3.Dot(s.Velocity, radial) <= 1e-3f, $"{name}: скорость наружу {Vector3.Dot(s.Velocity, radial)} м/с");
    }

    [Fact]
    public void Wall_None_ByDefault_And_Infinity()
    {
        var w = World();
        var b = Simulated(w, 3, R, Half, new Vector3(1000, 0, 0), null, ProfileVacuum);
        w.Step(Dt);
        Assert.Equal(1000, w.Get(b).Position.X);
        w.SetWall(float.PositiveInfinity);
        w.Step(Dt);
        Assert.Equal(1000, w.Get(b).Position.X);
    }

    public static TheoryData<string, Vector3, Vector3> PillarThrows => new()
    {
        // ось капсулы, скорость: колонна в (3, 0), радиус 1,2
        { "стоя, в лоб", Vector3.UnitY, new Vector3(4, 0, 0) },
        { "лёжа поперёк — серединой", Vector3.UnitZ, new Vector3(4, 0, 0) },
        { "лёжа вдоль — торцом", Vector3.UnitX, new Vector3(4, 0, 0) },
        { "наискось, вскользь", new Vector3(1, 0.3f, 1), new Vector3(4, 0, 1.5f) },
    };

    [Theory]
    [MemberData(nameof(PillarThrows))]
    public void Pillar_WholeCapsuleStaysOutside(string name, Vector3 axis, Vector3 velocity)
    {
        var w = World();
        const float px = 3, pz = 0, pr = 1.2f;
        w.SetPillars([new Pillar(px, pz, pr)]);
        var b = Simulated(w, 3, R, 0.4f, new Vector3(0, 1, 0), AxisAlong(axis), ProfileVacuum);
        var s = w.Get(b);
        s.Velocity = velocity;
        w.Set(b, s);
        bool touched = false;
        for (int t = 0; t < 120; t++)
        {
            w.Step(Dt);
            s = w.Get(b);
            float gap = AxisToVertical(s, px, pz) - (pr + R);
            Assert.True(gap >= -2e-4f, $"{name}, тик {t}: капсула в колонне на {-gap:E2} м");
            touched |= gap < 1e-3f;
        }
        Assert.True(touched, $"{name}: до колонны не долетела");
    }

    [Fact]
    public void Pillar_SunkBody_ExitsNoFasterThanPopSpeed_EvenFromTheAxis()
    {
        var w = World();
        const float pr = 0.5f;
        w.SetPillars([new Pillar(0, 0, pr), new Pillar(20, 0, pr)]);
        // Первое тело — наполовину в колонне, второе — центром на её оси (направление не определено: +X, детерминированно).
        var half = Simulated(w, 3, R, 0.3f, new Vector3(pr, 1, 0), null, ProfileVacuum);
        var axis = Simulated(w, 3, R, 0.3f, new Vector3(20, 1, 0), null, ProfileVacuum);
        float limit = PhysicsSettings.Default.PopSpeed * Dt;
        float gapHalf = AxisToVertical(w.Get(half), 0, 0) - (pr + R), gapAxis = AxisToVertical(w.Get(axis), 20, 0) - (pr + R);
        for (int t = 0; t < 120; t++)
        {
            w.Step(Dt);
            float g1 = AxisToVertical(w.Get(half), 0, 0) - (pr + R), g2 = AxisToVertical(w.Get(axis), 20, 0) - (pr + R);
            Assert.True(g1 - gapHalf <= limit * 1.001f && g2 - gapAxis <= limit * 1.001f, $"тик {t}: выход {g1 - gapHalf:E3} и {g2 - gapAxis:E3} м при пределе {limit:E3}");
            gapHalf = g1;
            gapAxis = g2;
        }
        Assert.True(gapHalf >= -1e-4f && gapAxis >= -1e-4f, $"осталось в колоннах: {gapHalf}, {gapAxis}");
        Assert.True(w.Get(axis).Position.X > 20, "с оси колонны — в сторону +X");
        Assert.True(w.Get(half).Velocity.Length() <= 1e-3f && w.Get(axis).Velocity.Length() <= 1e-3f, "выход не стал скоростью");
    }

    [Fact]
    public void SetWallAndPillars_ValidateArguments_PillarsCopied()
    {
        var w = World();
        foreach (float bad in new[] { 0f, -1f, float.NaN, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetWall(bad));

        var pillars = new[] { new Pillar(3, 0, 1) };
        w.SetPillars(pillars);
        pillars[0] = new Pillar(100, 100, 1);   // мир держит копию
        foreach (var bad in new[] { new Pillar(float.NaN, 0, 1), new Pillar(0, float.PositiveInfinity, 1), new Pillar(0, 0, 0), new Pillar(0, 0, -1),
                     new Pillar(0, 0, float.NaN) })
            Assert.Throws<ArgumentOutOfRangeException>(() => w.SetPillars([new Pillar(5, 5, 1), bad]));

        // Колонна (3, 0, 1) на месте: тело в ней выталкивается.
        var b = Simulated(w, 3, R, 0.2f, new Vector3(2.5f, 1, 0), null, ProfileVacuum);
        for (int t = 0; t < 120; t++) w.Step(Dt);
        Assert.True(AxisToVertical(w.Get(b), 3, 0) >= 1 + R - 1e-4f);

        w.SetPillars([]);
        var inside = Simulated(w, 3, R, 0.2f, new Vector3(3, 1, 0), null, ProfileVacuum);
        w.Step(Dt);
        Assert.Equal(new Vector3(3, 1, 0), w.Get(inside).Position);
    }
}
