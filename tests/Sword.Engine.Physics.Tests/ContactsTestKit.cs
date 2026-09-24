using System.Numerics;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Помощник тестов контактов и шага (7г): тестовые земли (<see cref="IGround"/> без состояния и без выделения памяти), постройка
/// тел на физике, замеры глубины и перекрытия. Общий помощник 7б — <see cref="TestBodies"/>.
/// </summary>
internal static class ContactsKit
{
    /// <summary>Тик 120 Гц, с.</summary>
    public const float Dt = 1f / 120;

    /// <summary>Только гравитация: без демпфирования и трения о землю профиля — импульс и трение покоя видны в чистом виде.</summary>
    public static readonly BodyDynamics GravityOnly = new() { LinearDamping = 0, AngularDamping = 0, GroundFriction = 0, GroundAngularFriction = 0 };

    /// <summary>Мир с профилями: 0 — по умолчанию, 1 — <see cref="GravityOnly"/>, 2 — <see cref="TestBodies.Vacuum"/>.</summary>
    public static PhysicsWorld World(PhysicsSettings? settings = null, IGround? ground = null)
    {
        var w = new PhysicsWorld(settings: settings);
        w.RegisterDynamics(GravityOnly);
        w.RegisterDynamics(TestBodies.Vacuum);
        w.SetGround(ground);
        return w;
    }

    public const int ProfileGravityOnly = 1;
    public const int ProfileVacuum = 2;

    /// <summary>Сочленение из одного тела на физике: форма с массой, поза без скорости, профиль.</summary>
    public static BodyHandle Simulated(PhysicsWorld w, float mass, float radius, float halfLength, Vector3 position, Quaternion? rotation = null,
        int profile = 0)
    {
        var b = w.Body(w.CreateArticulation(1), 0);
        Setup(w, b, mass, radius, halfLength, position, rotation ?? Quaternion.Identity, profile);
        return b;
    }

    /// <summary>Тело сочленения — на физику: форма с массой, поза без скорости, профиль.</summary>
    public static void Setup(PhysicsWorld w, BodyHandle b, float mass, float radius, float halfLength, Vector3 position, Quaternion rotation, int profile)
    {
        w.SetShape(b, mass, radius, halfLength);
        w.Place(b, position, rotation);
        w.SetMode(b, BodyMode.Simulated);
        w.SetDynamics(b, profile);
    }

    /// <summary>Поворот, кладущий ось капсулы (локальная Y) вдоль <paramref name="axis"/>.</summary>
    public static Quaternion AxisAlong(Vector3 axis)
    {
        axis = Vector3.Normalize(axis);
        var c = Vector3.Cross(Vector3.UnitY, axis);
        float s = c.Length(), d = Vector3.Dot(Vector3.UnitY, axis);
        if (s < 1e-6f) return d > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.CreateFromAxisAngle(c / s, MathF.Atan2(s, d));
    }

    /// <summary>Наибольшая глубина торцов тела в земле по всем граням, м (0 — не касается).</summary>
    public static float Depth(IGround ground, in BodyState s)
    {
        Span<GroundTouch> touches = stackalloc GroundTouch[IGround.MaxTouches];
        float max = 0;
        foreach (var end in new[] { s.EndA, s.EndB })
        {
            int n = ground.Touches(end, s.Radius, touches);
            for (int i = 0; i < n; i++) max = MathF.Max(max, touches[i].Depth);
        }
        return max;
    }

    /// <summary>Глубина пары капсул сейчас, м (0 — не пересекаются). Первым — короткий отрезок (вырожденный второй <c>SegSeg</c> не ищет ближайшую точку).</summary>
    public static float Overlap(in BodyState a, in BodyState b)
    {
        float d2 = a.HalfLength <= b.HalfLength
            ? Sword.Engine.Geometry.Segments.SegSeg(a.EndA, a.EndB, b.EndA, b.EndB, out _, out _)
            : Sword.Engine.Geometry.Segments.SegSeg(b.EndA, b.EndB, a.EndA, a.EndB, out _, out _);
        return MathF.Max(0, a.Radius + b.Radius - MathF.Sqrt(d2));
    }

    /// <summary>Импульс тела, кг·м/с.</summary>
    public static Vector3 Momentum(in BodyState s) => s.Velocity * s.Mass;
}

/// <summary>Ровная земля: плоскость y = <c>height</c>, одна грань 0.</summary>
internal sealed class FlatGround(float height = 0) : IGround
{
    public int Touches(Vector3 center, float radius, Span<GroundTouch> touches)
    {
        float depth = Depth(center, radius, 0, out var n);
        if (!(depth > 0) || touches.Length == 0) return 0;
        touches[0] = new GroundTouch(0, n, depth);
        return 1;
    }

    public float Depth(Vector3 center, float radius, int face, out Vector3 normal)
    {
        normal = Vector3.UnitY;
        return radius - (center.Y - height);
    }
}

/// <summary>Наклонная плоскость через начало координат: нормаль повёрнута от вертикали на угол вокруг Z (вниз по склону — +X), одна грань 0.</summary>
internal sealed class SlopeGround(float angle) : IGround
{
    /// <summary>Нормаль из земли: плоскость y = −x·tg(угла).</summary>
    public Vector3 Normal { get; } = new(MathF.Sin(angle), MathF.Cos(angle), 0);

    /// <summary>Вниз по склону — единичный вектор вдоль плоскости (+X и вниз).</summary>
    public Vector3 Downhill { get; } = new(MathF.Cos(angle), -MathF.Sin(angle), 0);

    public int Touches(Vector3 center, float radius, Span<GroundTouch> touches)
    {
        float depth = Depth(center, radius, 0, out var n);
        if (!(depth > 0) || touches.Length == 0) return 0;
        touches[0] = new GroundTouch(0, n, depth);
        return 1;
    }

    public float Depth(Vector3 center, float radius, int face, out Vector3 normal)
    {
        normal = Normal;
        return radius - Vector3.Dot(center, Normal);
    }
}

/// <summary>
/// Ступень-ящик на ровном полу: пол y = 0 — грань 0, ящик [<c>min</c>, <c>max</c>] — грань 1 с точным расстоянием (у верха нормаль
/// вверх, у подступенка — горизонтальная, за кромкой — от ребра; внутри — к ближайшей стороне).
/// </summary>
internal sealed class StepGround(Vector3 min, Vector3 max) : IGround
{
    public int Touches(Vector3 center, float radius, Span<GroundTouch> touches)
    {
        int count = 0;
        for (int face = 0; face < 2 && count < touches.Length; face++)
        {
            float depth = Depth(center, radius, face, out var n);
            if (depth > 0) touches[count++] = new GroundTouch(face, n, depth);
        }
        return count;
    }

    public float Depth(Vector3 center, float radius, int face, out Vector3 normal)
    {
        if (face == 0)
        {
            normal = Vector3.UnitY;
            return radius - center.Y;
        }
        return radius - BoxDistance(center, out normal);
    }

    private float BoxDistance(Vector3 p, out Vector3 normal)
    {
        var c = (min + max) * 0.5f;
        var e = (max - min) * 0.5f;
        var d = p - c;
        var q = Vector3.Abs(d) - e;
        var outside = Vector3.Max(q, Vector3.Zero);
        float lo = outside.Length();
        if (lo > 0)
        {
            normal = Vector3.Normalize(p - Vector3.Clamp(p, min, max));
            return lo;
        }
        if (q.X >= q.Y && q.X >= q.Z)
        {
            normal = new Vector3(d.X >= 0 ? 1 : -1, 0, 0);
            return q.X;
        }
        if (q.Y >= q.Z)
        {
            normal = new Vector3(0, d.Y >= 0 ? 1 : -1, 0);
            return q.Y;
        }
        normal = new Vector3(0, 0, d.Z >= 0 ? 1 : -1);
        return q.Z;
    }
}
