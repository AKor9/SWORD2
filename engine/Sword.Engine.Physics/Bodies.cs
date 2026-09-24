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
/// Режим тела. <see cref="Kinematic"/> — тело ставят снаружи (анимация: <see cref="IPhysicsWorld.SetKinematicTarget"/>),
/// шаг физики его не двигает, для решателя его масса бесконечна. <see cref="Simulated"/> — тело на физике (труп, позже
/// обломок): его двигает шаг (<see cref="IPhysicsWorld.Step"/>) — интеграция, суставы, контакты; нужна масса
/// (<see cref="IPhysicsWorld.SetShape(BodyHandle, float, float, float)"/>).
/// </summary>
public enum BodyMode : byte
{
    Kinematic = 1,
    Simulated = 2,
}

/// <summary>
/// Чем кинематическое тело толкает тела на физике (этап 7, точка 7д). Смысл для физики — какие бюджеты тратит толчок и даёт
/// ли он отдачу; что это за тело в игре (корпус, клинок в ударе, часть за отрубом), знает игра. Трасса физики считает
/// толчки по этим видам. Телу на физике не значит ничего.
/// </summary>
public enum KinematicSource : byte
{
    /// <summary>Обычный толчок: бюджет пары за подшаг (<see cref="PhysicsSettings.PushForce"/>), отдача толкающему.</summary>
    Body = 0,

    /// <summary>
    /// Толчок прохода (клинок в ударе): сверх бюджета пары — из бюджета прохода пары сочленений
    /// (<see cref="IPhysicsWorld.PreparePass"/>), отдача толкающему.
    /// </summary>
    Pass = 1,

    /// <summary>Толчок без отдачи: тело ведут вслед чужому движению (часть за отрубом живого, этап 11); бюджет пары — тот же.</summary>
    NoReaction = 2,
}

/// <summary>
/// Состояние тела-капсулы (идеи — <c>RigidBody</c> SWORD 2eab13b): капсула вдоль локальной оси Y радиусом
/// <see cref="Radius"/>, отрезок оси — ±<see cref="HalfLength"/> от центра. Положение и поворот — центр масс и система тела;
/// прошлая поза (<see cref="PrevPosition"/>, <see cref="PrevRotation"/>) — поза начала подшага у тела на физике и прошлая
/// постановка у кинематического; поза начала тика (<see cref="StartPosition"/>, <see cref="StartRotation"/>) — для
/// интерполяции клиента. Динамика (этап 7): масса, обратная масса, обратная инерция (диагональ в системе тела), касание земли
/// в подшаге, номер профиля динамики мира. Поля — открытые: примитивы <see cref="Dynamics"/> и <see cref="Xpbd"/> работают
/// над <c>ref BodyState</c>; мир отдаёт копию (<see cref="IPhysicsWorld.Get"/>) и принимает целиком
/// (<see cref="IPhysicsWorld.Set"/>).
/// </summary>
public struct BodyState
{
    public Vector3 Position, PrevPosition, Velocity, AngularVelocity;
    public Quaternion Rotation, PrevRotation;
    public float Radius, HalfLength;
    public BodyMode Mode;

    /// <summary>
    /// Поза начала тика — для интерполяции клиента (поза начала тика → поза тика). У тела на физике <see cref="PrevPosition"/>
    /// перезаписывает каждый подшаг, поэтому поле отдельное: его пишут <see cref="IPhysicsWorld.Place"/> (та же поза),
    /// <see cref="IPhysicsWorld.SetKinematicTarget"/> (прошлая постановка) и <see cref="IPhysicsWorld.Step"/> (у тел на физике
    /// — перед первым подшагом).
    /// </summary>
    public Vector3 StartPosition;

    /// <summary>Поворот начала тика — как <see cref="StartPosition"/>.</summary>
    public Quaternion StartRotation;

    /// <summary>
    /// Масса, кг (0 — формы ещё нет: новое тело, <see cref="New"/>); <see cref="InvMass"/> — 1 / масса (0 у тела без массы).
    /// Форма задаётся только с массой (<see cref="IPhysicsWorld.SetShape"/>).
    /// </summary>
    public float Mass, InvMass;

    /// <summary>Обратная инерция — диагональ в системе тела (X, Y — вдоль оси капсулы, Z), 1/(кг·м²).</summary>
    public Vector3 InvInertia;

    /// <summary>Касается земли в этом подшаге: трение о землю профиля гасит скольжение (сбрасывает <see cref="Dynamics.Integrate"/>).</summary>
    public bool Grounded;

    /// <summary>Номер профиля динамики мира (<see cref="IPhysicsWorld.RegisterDynamics"/>); 0 — профиль по умолчанию.</summary>
    public byte Profile;

    /// <summary>Чем толкает кинематическое тело (<see cref="KinematicSource"/>); у тела на физике не значит ничего.</summary>
    public KinematicSource Source;

    /// <summary>
    /// Новое тело — в начале координат, без поворота, без скоростей, без формы и массы (радиус, полудлина, масса 0), профиль
    /// по умолчанию, толчок <see cref="KinematicSource.Body"/>.
    /// </summary>
    public static BodyState New(BodyMode mode) => new()
    {
        Rotation = Quaternion.Identity,
        PrevRotation = Quaternion.Identity,
        StartRotation = Quaternion.Identity,
        Mode = mode,
    };

    /// <summary>Ось капсулы в мире (<c>RigidBody.Axis</c>).</summary>
    public readonly Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Нижний конец оси (<c>RigidBody.EndA</c>).</summary>
    public readonly Vector3 EndA => Position - Axis * HalfLength;

    /// <summary>Верхний конец оси (<c>RigidBody.EndB</c>).</summary>
    public readonly Vector3 EndB => Position + Axis * HalfLength;

    /// <summary>Точка системы тела — в мир.</summary>
    public readonly Vector3 ToWorld(Vector3 local) => Position + Vector3.Transform(local, Rotation);

    /// <summary>Точка мира — в систему тела.</summary>
    public readonly Vector3 ToLocal(Vector3 world) => Vector3.Transform(world - Position, Quaternion.Conjugate(Rotation));

    /// <summary>Скорость материальной точки тела, которая сейчас в <paramref name="world"/>: v + ω × r.</summary>
    public readonly Vector3 PointVelocity(Vector3 world) => Velocity + Vector3.Cross(AngularVelocity, world - Position);

    /// <summary>
    /// Где была в прошлой позе (<see cref="PrevPosition"/>, <see cref="PrevRotation"/> — начало подшага) материальная точка
    /// тела, которая сейчас в <paramref name="world"/>. Сдвиг точки за подшаг — <c>world − PrevPoint(world)</c>.
    /// </summary>
    public readonly Vector3 PrevPoint(Vector3 world) => PrevPosition + Vector3.Transform(ToLocal(world), PrevRotation);

    /// <summary>Обратная инерция в мире, применённая к вектору: <c>R · (I⁻¹ ⊙ (R⁻¹ v))</c>.</summary>
    public readonly Vector3 InvInertiaWorld(Vector3 v)
    {
        var local = Vector3.Transform(v, Quaternion.Conjugate(Rotation)) * InvInertia;
        return Vector3.Transform(local, Rotation);
    }

    /// <summary>
    /// Обратная масса тела в точке вдоль направления: <c>1/m + (r × n) · I⁻¹(r × n)</c>; <paramref name="r"/> — от центра
    /// тела к точке (мир), <paramref name="n"/> — единичное направление. Сколько метров сдвига точки вдоль n даёт единица
    /// позиционного импульса (кг·м) — или м/с скорости от импульса в Н·с.
    /// </summary>
    public readonly float InvMassAt(Vector3 r, Vector3 n)
    {
        var rn = Vector3.Cross(r, n);
        return InvMass + Vector3.Dot(rn, InvInertiaWorld(rn));
    }

    /// <summary>
    /// Поза и скорости конечны: положения (нынешнее, прошлое, начала тика), повороты, скорость и угловая скорость (у SWORD
    /// проверялись только положение, <c>W</c> поворота и скорость). Форма и масса проверяются при записи.
    /// </summary>
    public readonly bool IsFinite =>
        float.IsFinite(Position.X + Position.Y + Position.Z + PrevPosition.X + PrevPosition.Y + PrevPosition.Z
            + StartPosition.X + StartPosition.Y + StartPosition.Z
            + Velocity.X + Velocity.Y + Velocity.Z + AngularVelocity.X + AngularVelocity.Y + AngularVelocity.Z
            + Rotation.X + Rotation.Y + Rotation.Z + Rotation.W + PrevRotation.X + PrevRotation.Y + PrevRotation.Z + PrevRotation.W
            + StartRotation.X + StartRotation.Y + StartRotation.Z + StartRotation.W);

    /// <summary>
    /// Кинетическая энергия, Дж: <c>½ m v² + ½ ω · I ω</c> (инерция — по диагонали системы тела; ось с нулевой обратной
    /// инерцией не вращается и энергии не несёт). У тела без массы — 0.
    /// </summary>
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

/// <summary>Математика тел.</summary>
public static class PhysicsMath
{
    /// <summary>
    /// Вектор поворота от <paramref name="from"/> к <paramref name="to"/> (ось · угол, рад) по кратчайшему пути
    /// (идея — <c>Xpbd.RotationVector</c> SWORD 2eab13b): из него — угловая скорость кинематического тела и поправки пределов.
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
