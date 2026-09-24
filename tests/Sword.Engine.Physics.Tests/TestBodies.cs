using System.Numerics;

namespace Sword.Engine.Physics.Tests;

/// <summary>Заготовки тел для тестов динамики и проекций: капсула на физике с массой, случайные позы и скорости.</summary>
internal static class TestBodies
{
    /// <summary>Подшаг тика 120 Гц при 4 подшагах, с.</summary>
    public const float H = 1f / 480;

    /// <summary>Без гравитации, демпфирования и трения — чтобы скорость меняли только проекции.</summary>
    public static readonly BodyDynamics Vacuum = new() { Gravity = 0, LinearDamping = 0, AngularDamping = 0, GroundFriction = 0, GroundAngularFriction = 0 };

    /// <summary>Капсула на физике в покое: вся поза (нынешняя, прошлая, начала тика) — <paramref name="position"/>, <paramref name="rotation"/>.</summary>
    public static BodyState Capsule(float mass, float radius, float halfLength, Vector3 position = default, Quaternion? rotation = null)
    {
        var b = BodyState.New(BodyMode.Simulated);
        Dynamics.SetShape(ref b, mass, radius, halfLength);
        var q = rotation ?? Quaternion.Identity;
        b.Position = b.PrevPosition = b.StartPosition = position;
        b.Rotation = b.PrevRotation = b.StartRotation = q;
        return b;
    }

    /// <summary>Случайная капсула: масса 0,5…20 кг, радиус 0,03…0,2 м, полудлина 0…0,4 м, случайный поворот.</summary>
    public static BodyState RandomCapsule(Random rng, Vector3 position)
    {
        float mass = 0.5f + rng.NextSingle() * 19.5f;
        float radius = 0.03f + rng.NextSingle() * 0.17f;
        float half = rng.NextSingle() * 0.4f;
        return Capsule(mass, radius, half, position, RandomRotation(rng));
    }

    public static Quaternion RandomRotation(Random rng) =>
        Quaternion.Normalize(new Quaternion(Gauss(rng), Gauss(rng), Gauss(rng), Gauss(rng)));

    public static Vector3 RandomUnit(Random rng) => Vector3.Normalize(new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)));

    /// <summary>Случайный вектор длиной не больше <paramref name="max"/>.</summary>
    public static Vector3 RandomVector(Random rng, float max) => RandomUnit(rng) * (rng.NextSingle() * max);

    /// <summary>Угол между поворотами, рад (0…π).</summary>
    public static float Angle(Quaternion a, Quaternion b) => PhysicsMath.RotationVector(a, b).Length();

    private static float Gauss(Random rng) =>
        MathF.Sqrt(-2 * MathF.Log(1 - rng.NextSingle())) * MathF.Cos(2 * MathF.PI * rng.NextSingle());
}
