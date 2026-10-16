using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>Тело физики: номер слота и поколение. Поколение 0 — «нет тела»; освобождённый слот меняет поколение.</summary>
public readonly record struct BodyHandle(int Index, int Generation)
{
    public static readonly BodyHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>Сочленение — набор тел с одним хэндлом (боец, труп). Поколение 0 — «нет сочленения».</summary>
public readonly record struct ArticulationHandle(int Index, int Generation)
{
    public static readonly ArticulationHandle None = default;
    public bool IsNone => Generation == 0;
    public override string ToString() => IsNone ? "нет" : $"{Index}v{Generation}";
}

/// <summary>
/// Режим тела. <see cref="Kinematic"/> — ставят снаружи, шаг не двигает, масса для решателя бесконечна. <see cref="Simulated"/>
/// — тело на физике: двигает шаг, нужна масса.
/// </summary>
public enum BodyMode : byte
{
    Kinematic = 1,
    Simulated = 2,
}

/// <summary>Чем кинематическое тело толкает тела на физике: какие бюджеты тратит толчок и есть ли отдача. Телу на физике не значит ничего.</summary>
public enum KinematicSource : byte
{
    /// <summary>Обычный толчок: бюджет пары за подшаг (<see cref="PhysicsSettings.PushForce"/>), отдача толкающему.</summary>
    Body = 0,

    /// <summary>Толчок прохода (клинок в ударе): сверх бюджета пары — из бюджета прохода (<see cref="IPhysicsWorld.PreparePass"/>), отдача толкающему.</summary>
    Pass = 1,

    /// <summary>Толчок без отдачи: тело ведут вслед чужому движению; бюджет пары — тот же. Потребителя пока нет.</summary>
    NoReaction = 2,
}

/// <summary>
/// Состояние тела-капсулы: ось — локальная Y, отрезок ±<see cref="HalfLength"/>, радиус <see cref="Radius"/>; положение —
/// центр масс. Прошлая поза — начало подшага (на физике) или прошлая постановка (кинематика); поза начала тика — для
/// интерполяции клиента. Поля открыты: <see cref="Dynamics"/> и <see cref="Xpbd"/> работают над <c>ref BodyState</c>.
/// </summary>
public struct BodyState
{
    public Vector3 Position, PrevPosition, Velocity, AngularVelocity;
    public Quaternion Rotation, PrevRotation;
    public float Radius, HalfLength;
    public BodyMode Mode;

    /// <summary>
    /// Поза начала тика — для интерполяции клиента. Отдельно от <see cref="PrevPosition"/>: ту у тела на физике перезаписывает
    /// каждый подшаг.
    /// </summary>
    public Vector3 StartPosition;

    /// <summary>Поворот начала тика — как <see cref="StartPosition"/>.</summary>
    public Quaternion StartRotation;

    /// <summary>Масса, кг (0 — формы ещё нет); <see cref="InvMass"/> — 1 / масса (0 без массы).</summary>
    public float Mass, InvMass;

    /// <summary>Обратная инерция — диагональ в системе тела (X, Y — вдоль оси капсулы, Z), 1/(кг·м²).</summary>
    public Vector3 InvInertia;

    /// <summary>Касается земли в этом подшаге: трение о землю профиля гасит скольжение (сбрасывает <see cref="Dynamics.Integrate"/>).</summary>
    public bool Grounded;

    /// <summary>Номер профиля динамики мира (<see cref="IPhysicsWorld.RegisterDynamics"/>); 0 — профиль по умолчанию.</summary>
    public byte Profile;

    /// <summary>Чем толкает кинематическое тело (<see cref="KinematicSource"/>); у тела на физике не значит ничего.</summary>
    public KinematicSource Source;

    /// <summary>Новое тело: в начале координат, без поворота, скоростей, формы и массы; профиль 0, толчок <see cref="KinematicSource.Body"/>.</summary>
    public static BodyState New(BodyMode mode) => new()
    {
        Rotation = Quaternion.Identity,
        PrevRotation = Quaternion.Identity,
        StartRotation = Quaternion.Identity,
        Mode = mode,
    };

    /// <summary>Ось капсулы в мире.</summary>
    public readonly Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Нижний конец оси.</summary>
    public readonly Vector3 EndA => Position - Axis * HalfLength;

    /// <summary>Верхний конец оси.</summary>
    public readonly Vector3 EndB => Position + Axis * HalfLength;

    public readonly Vector3 ToWorld(Vector3 local) => Position + Vector3.Transform(local, Rotation);

    public readonly Vector3 ToLocal(Vector3 world) => Vector3.Transform(world - Position, Quaternion.Conjugate(Rotation));

    /// <summary>Скорость материальной точки тела, которая сейчас в <paramref name="world"/>: v + ω × r.</summary>
    public readonly Vector3 PointVelocity(Vector3 world) => Velocity + Vector3.Cross(AngularVelocity, world - Position);

    /// <summary>Где была в прошлой позе (начало подшага) материальная точка, которая сейчас в <paramref name="world"/>.</summary>
    public readonly Vector3 PrevPoint(Vector3 world) => PrevPosition + Vector3.Transform(ToLocal(world), PrevRotation);

    /// <summary>Обратная инерция в мире, применённая к вектору: <c>R · (I⁻¹ ⊙ (R⁻¹ v))</c>.</summary>
    public readonly Vector3 InvInertiaWorld(Vector3 v)
    {
        var local = Vector3.Transform(v, Quaternion.Conjugate(Rotation)) * InvInertia;
        return Vector3.Transform(local, Rotation);
    }

    /// <summary>
    /// Обратная масса в точке вдоль единичного <paramref name="n"/>: <c>1/m + (r × n) · I⁻¹(r × n)</c>, <paramref name="r"/> — от
    /// центра к точке (мир).
    /// </summary>
    public readonly float InvMassAt(Vector3 r, Vector3 n)
    {
        var rn = Vector3.Cross(r, n);
        return InvMass + Vector3.Dot(rn, InvInertiaWorld(rn));
    }

    /// <summary>Поза и скорости конечны (положения, повороты, скорость и угловая скорость); форма и масса проверяются при записи.</summary>
    public readonly bool IsFinite =>
        float.IsFinite(Position.X + Position.Y + Position.Z + PrevPosition.X + PrevPosition.Y + PrevPosition.Z
            + StartPosition.X + StartPosition.Y + StartPosition.Z
            + Velocity.X + Velocity.Y + Velocity.Z + AngularVelocity.X + AngularVelocity.Y + AngularVelocity.Z
            + Rotation.X + Rotation.Y + Rotation.Z + Rotation.W + PrevRotation.X + PrevRotation.Y + PrevRotation.Z + PrevRotation.W
            + StartRotation.X + StartRotation.Y + StartRotation.Z + StartRotation.W);

    /// <summary>Кинетическая энергия, Дж: <c>½ m v² + ½ ω · I ω</c>; у тела без массы — 0.</summary>
    public readonly float KineticEnergy
    {
        get
        {
            float e = 0.5f * Mass * Velocity.LengthSquared();
            var w = Vector3.Transform(AngularVelocity, Quaternion.Conjugate(Rotation));
            if (InvInertia.X > 0) e += 0.5f * w.X * w.X / InvInertia.X;
            if (InvInertia.Y > 0) e += 0.5f * w.Y * w.Y / InvInertia.Y;
            if (InvInertia.Z > 0) e += 0.5f * w.Z * w.Z / InvInertia.Z;
            return e;
        }
    }
}

public static class PhysicsMath
{
    /// <summary>Вектор поворота от <paramref name="from"/> к <paramref name="to"/> (ось · угол, рад) по кратчайшему пути.</summary>
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
