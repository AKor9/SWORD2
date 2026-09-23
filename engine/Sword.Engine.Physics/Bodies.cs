using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>Тело физики: номер слота и поколение. Поколение 0 — «нет тела»; освобождённый слот меняет поколение.</summary>
public readonly record struct BodyHandle(int Index, int Generation)
{
    public static readonly BodyHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>
/// Сочленение — набор тел с одним хэндлом (раздел 6 плана): боец, позже труп, отрубленная часть, обломок. Номер слота и
/// поколение; поколение 0 — «нет сочленения».
/// </summary>
public readonly record struct ArticulationHandle(int Index, int Generation)
{
    public static readonly ArticulationHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>
/// Режим тела. С этапа 5 — только <see cref="Kinematic"/>: тело ставят снаружи (анимация), шаг физики его не двигает.
/// Тело на физике (<c>Simulated</c>: трупы, обломки) — с шагом и решателем, этап 7.
/// </summary>
public enum BodyMode : byte
{
    Kinematic = 1,
}

/// <summary>
/// Состояние тела-капсулы — <c>RigidBody</c> SWORD 2eab13b без динамики (масса, инерция, <c>BodyDynamics</c> — этап 7) и
/// без позы отрисовки (<c>RenderPos</c> — клиент): капсула вдоль локальной оси Y радиусом <see cref="Radius"/>, отрезок
/// оси — ±<see cref="HalfLength"/> от центра. Прошлая постановка — для скорости и интерполяции.
/// </summary>
public struct BodyState
{
    public Vector3 Position, PrevPosition, Velocity, AngularVelocity;
    public Quaternion Rotation, PrevRotation;
    public float Radius, HalfLength;
    public BodyMode Mode;

    /// <summary>
    /// Новое тело — как <c>new RigidBody()</c> SWORD до <c>SetShape</c> и <c>Place</c>: в начале координат, без поворота,
    /// без скоростей и без формы (радиус и полудлина 0).
    /// </summary>
    public static BodyState New(BodyMode mode) => new() { Rotation = Quaternion.Identity, PrevRotation = Quaternion.Identity, Mode = mode };

    /// <summary>Ось капсулы в мире (<c>RigidBody.Axis</c>).</summary>
    public readonly Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Нижний конец оси (<c>RigidBody.EndA</c>).</summary>
    public readonly Vector3 EndA => Position - Axis * HalfLength;

    /// <summary>Верхний конец оси (<c>RigidBody.EndB</c>).</summary>
    public readonly Vector3 EndB => Position + Axis * HalfLength;
}

/// <summary>Математика тел.</summary>
public static class PhysicsMath
{
    /// <summary>
    /// Вектор поворота от <paramref name="from"/> к <paramref name="to"/> (ось · угол, рад) по кратчайшему пути —
    /// <c>Xpbd.RotationVector</c> SWORD 2eab13b дословно: из него — угловая скорость кинематического тела.
    /// </summary>
    public static Vector3 RotationVector(Quaternion from, Quaternion to)
    {
        var dq = to * Quaternion.Conjugate(from);
        if (dq.W < 0) dq = Quaternion.Negate(dq);
        var v = new Vector3(dq.X, dq.Y, dq.Z);
        float s = v.Length();
        if (s < 1e-7f) return v * 2;
        return v / s * (2 * MathF.Atan2(s, dq.W));
    }
}
