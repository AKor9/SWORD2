using System.Numerics;

namespace Sword.Engine.Animation;

/// <summary>
/// Общая математика позы и пружин — одна реализация на игру, пекарню и тесты: ориентации капсул, двухзвенный IK, векторы
/// поворота, критически демпфированные пружины.
/// </summary>
public static class AnimMath
{
    /// <summary>Ориентация капсулы: локальная Y — вдоль <paramref name="yDir"/>, X — к <paramref name="xHint"/> (ось шарнира).</summary>
    public static Quaternion FromBasis(Vector3 yDir, Vector3 xHint)
    {
        var y = Vector3.Normalize(yDir);
        var x = xHint - y * Vector3.Dot(xHint, y);
        if (x.LengthSquared() < 1e-6f)
        {
            x = Vector3.Cross(y, Vector3.UnitZ);
            if (x.LengthSquared() < 1e-6f) x = Vector3.Cross(y, Vector3.UnitX);
        }
        x = Vector3.Normalize(x);
        var z = Vector3.Cross(x, y);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
    }

    /// <summary>Двухкостный IK: от <paramref name="s"/> к <paramref name="target"/>, средний сустав — в сторону <paramref name="pole"/>.</summary>
    public static (Vector3 Mid, Vector3 End) Ik2Bone(Vector3 s, Vector3 target, Vector3 pole, float a, float b)
    {
        var d = target - s;
        float len = Math.Clamp(d.Length(), 0.08f, a + b - 0.002f);
        var dir = d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : Vector3.UnitZ;
        var end = s + dir * len;
        float cosA = Math.Clamp((a * a + len * len - b * b) / (2 * a * len), -1, 1);
        float sinA = MathF.Sqrt(1 - cosA * cosA);
        var pv = pole - s;
        var perp = pv - dir * Vector3.Dot(pv, dir);
        perp = perp.LengthSquared() > 1e-6f ? Vector3.Normalize(perp) : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX));
        return (s + dir * a * cosA + perp * a * sinA, end);
    }

    /// <summary>Вектор поворота (мир), переводящий from в to.</summary>
    public static Vector3 RotationVector(Quaternion from, Quaternion to) => ToRotationVector(to * Quaternion.Conjugate(from));

    public static Vector3 ToRotationVector(Quaternion q)
    {
        if (q.W < 0) q = Quaternion.Negate(q);
        var v = new Vector3(q.X, q.Y, q.Z);
        float s = v.Length();
        if (s < 1e-7f) return v * 2;
        return v / s * (2 * MathF.Atan2(s, q.W));
    }

    public static Quaternion FromRotationVector(Vector3 v)
    {
        float angle = v.Length();
        if (angle < 1e-7f) return Quaternion.Normalize(new Quaternion(v * 0.5f, 1));
        return Quaternion.CreateFromAxisAngle(v / angle, angle);
    }

    /// <summary>Коэффициент затухания критически демпфированной пружины по полупериоду, 1/с.</summary>
    public static float HalflifeToDamping(float halflife) => 4f * MathF.Log(2f) / (halflife + 1e-5f);

    /// <summary>Затухание смещения инерциализации к нулю (вектор) — пружина с целью 0.</summary>
    public static void DecaySpring(ref Vector3 x, ref Vector3 v, float halflife, float dt)
    {
        float y = HalflifeToDamping(halflife) * 0.5f;
        var j1 = v + x * y;
        float e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt);
        v = e * (v - j1 * y * dt);
    }

    /// <summary>Затухание смещения-поворота к единичному: пружина на векторе поворота.</summary>
    public static void DecaySpring(ref Quaternion x, ref Vector3 v, float halflife, float dt)
    {
        var r = ToRotationVector(x);
        DecaySpring(ref r, ref v, halflife, dt);
        x = FromRotationVector(r);
    }
}
