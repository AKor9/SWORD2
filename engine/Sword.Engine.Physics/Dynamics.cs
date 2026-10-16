using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Профиль динамики тела на физике: гравитация, демпфирование, трение о землю, потолки скоростей. Мир держит таблицу
/// профилей, тело — номер (0 — по умолчанию). Демпфирование и трение — доли скорости в секунду: за подшаг множитель
/// <c>max(0, 1 − k·h)</c>. <c>default(BodyDynamics)</c> — не профиль (нулевые потолки).
/// </summary>
public readonly record struct BodyDynamics()
{
    /// <summary>Ускорение свободного падения вниз по мировой оси Y, м/с².</summary>
    public float Gravity { get; init; } = 9.81f;

    /// <summary>Сопротивление воздуха: линейная скорость, 1/с.</summary>
    public float LinearDamping { get; init; } = 0.15f;

    /// <summary>Сопротивление воздуха: угловая скорость, 1/с.</summary>
    public float AngularDamping { get; init; } = 0.8f;

    /// <summary>Трение о землю (<see cref="BodyState.Grounded"/>): горизонтальная скорость (X, Z мира), 1/с.</summary>
    public float GroundFriction { get; init; } = 9f;

    /// <summary>Трение о землю: угловая скорость, 1/с.</summary>
    public float GroundAngularFriction { get; init; } = 3f;

    /// <summary>Потолок скорости, м/с (больше 0; <see cref="float.PositiveInfinity"/> — без потолка): страховка от взрыва решателя.</summary>
    public float MaxSpeed { get; init; } = 40f;

    /// <summary>Потолок угловой скорости, рад/с (больше 0; <see cref="float.PositiveInfinity"/> — без потолка).</summary>
    public float MaxAngularSpeed { get; init; } = 60f;

    /// <summary>Что не так с профилем (null — годен): конечные гравитация, демпфирование и трение ≥ 0, потолки больше 0.</summary>
    public string? Problem()
    {
        if (!float.IsFinite(Gravity)) return "гравитация — конечная";
        if (!(float.IsFinite(LinearDamping) && LinearDamping >= 0 && float.IsFinite(AngularDamping) && AngularDamping >= 0))
            return "демпфирование — конечное, не меньше 0";
        if (!(float.IsFinite(GroundFriction) && GroundFriction >= 0 && float.IsFinite(GroundAngularFriction) && GroundAngularFriction >= 0))
            return "трение о землю — конечное, не меньше 0";
        if (!(MaxSpeed > 0 && MaxAngularSpeed > 0)) return "потолки скоростей — больше 0";
        return null;
    }
}

/// <summary>
/// Обратная инерция в мире <c>R · diag(I⁻¹) · Rᵀ</c> для позы, которая дальше не меняется (скоростной проход, расслабление):
/// одна матрица на тело вместо двух поворотов на вызов.
/// </summary>
internal readonly struct InvInertiaMatrix
{
    public readonly Vector3 X, Y, Z;
    public readonly float InvMass;

    public InvInertiaMatrix(in BodyState s)
    {
        var m = Matrix4x4.CreateFromQuaternion(s.Rotation);
        // строки — образы осей тела (столбцы поворота): W = Σ dᵢ·rᵢ·rᵢᵀ, столбец k — Σ dᵢ·rᵢ·(rᵢ)ₖ
        Vector3 r0 = new(m.M11, m.M12, m.M13), r1 = new(m.M21, m.M22, m.M23), r2 = new(m.M31, m.M32, m.M33);
        var d = s.InvInertia;
        X = r0 * (d.X * r0.X) + r1 * (d.Y * r1.X) + r2 * (d.Z * r2.X);
        Y = r0 * (d.X * r0.Y) + r1 * (d.Y * r1.Y) + r2 * (d.Z * r2.Y);
        Z = r0 * (d.X * r0.Z) + r1 * (d.Y * r1.Z) + r2 * (d.Z * r2.Z);
        InvMass = s.InvMass;
    }

    /// <summary>Обратная инерция в мире, применённая к вектору (как <see cref="BodyState.InvInertiaWorld"/>).</summary>
    public Vector3 Apply(Vector3 v) => X * v.X + Y * v.Y + Z * v.Z;

    /// <summary>Обратная масса тела в точке с плечом <paramref name="r"/> вдоль единичного <paramref name="n"/> (как <see cref="BodyState.InvMassAt"/>).</summary>
    public float At(Vector3 r, Vector3 n)
    {
        var rn = Vector3.Cross(r, n);
        return InvMass + Vector3.Dot(rn, Apply(rn));
    }

    /// <summary>Импульс в скорость в точке с плечом <paramref name="r"/> (как <see cref="Dynamics.ApplyImpulse"/>).</summary>
    public void Impulse(ref BodyState b, Vector3 impulse, Vector3 r)
    {
        b.Velocity += impulse * InvMass;
        b.AngularVelocity += Apply(Vector3.Cross(r, impulse));
    }
}

/// <summary>
/// Динамика тела-капсулы — функции над <c>ref BodyState</c>: форма, коррекция импульсом, поворот, интеграция и обновление
/// скоростей подшага. Аргументы не проверяют (горячий путь; h &gt; 0, направления единичные); памяти не выделяют.
/// </summary>
public static class Dynamics
{
    /// <summary>
    /// Наибольший поворот за вызов <see cref="Rotate"/>, рад: линеаризованный поворот верен для малых углов, большой шаг —
    /// признак взрыва решателя.
    /// </summary>
    public const float MaxRotationPerCall = 0.5f;

    /// <summary>
    /// Обратная инерция сплошной капсулы (цилиндр 2·<paramref name="halfLength"/> и две полусферы, плотность однородна) —
    /// диагональ в системе тела, ось — Y. Точная формула: приближение цилиндром длины <c>2·halfLen + r</c> делает шар
    /// анизотропным, и короткие капсулы катятся не той осью.
    /// </summary>
    public static Vector3 CapsuleInvInertia(float mass, float radius, float halfLength)
    {
        float r2 = radius * radius, h = halfLength;
        // доли массы — по объёмам: цилиндр π r² · 2h, шар из двух полусфер 4/3 π r³ → отношение 3h / (2r)
        float cylinder = 2 * h, sphere = 4f / 3f * radius;
        float mc = mass * cylinder / (cylinder + sphere), ms = mass - mc;
        float iy = mc * r2 * 0.5f + ms * 0.4f * r2;
        // поперёк: цилиндр m(H²/12 + r²/4), полусферы — своя инерция 83/320 m r² и перенос центров на h + 3r/8
        float ixz = mc * (h * h / 3f + r2 * 0.25f) + ms * (0.4f * r2 + h * h + 0.75f * h * radius);
        return new Vector3(1f / ixz, 1f / iy, 1f / ixz);
    }

    /// <summary>Форма с массой: радиус, полудлина, масса и обратные масса и инерция (проверяет мир).</summary>
    public static void SetShape(ref BodyState b, float mass, float radius, float halfLength)
    {
        b.Radius = radius;
        b.HalfLength = halfLength;
        b.Mass = mass;
        b.InvMass = 1f / mass;
        b.InvInertia = CapsuleInvInertia(mass, radius, halfLength);
    }

    /// <summary>Повернуть на <paramref name="dTheta"/> (мир, рад) линеаризованно: <c>q ← normalize(q + ½ (dθ, 0) · q)</c>, шаг ≤ <see cref="MaxRotationPerCall"/>.</summary>
    public static void Rotate(ref BodyState b, Vector3 dTheta)
    {
        float l = dTheta.Length();
        if (l > MaxRotationPerCall) dTheta *= MaxRotationPerCall / l;
        b.Rotation = Quaternion.Normalize(b.Rotation + new Quaternion(dTheta * 0.5f, 0) * b.Rotation);
    }

    /// <summary>
    /// Позиционная коррекция импульсом (кг·м) в точке с плечом <paramref name="r"/>: центр — на <c>impulse / m</c>, поворот —
    /// на <c>I⁻¹(r × impulse)</c>.
    /// </summary>
    public static void Correct(ref BodyState b, Vector3 impulse, Vector3 r)
    {
        b.Position += impulse * b.InvMass;
        Rotate(ref b, b.InvInertiaWorld(Vector3.Cross(r, impulse)));
    }

    /// <summary>Импульс (Н·с) в скорость в точке с плечом <paramref name="r"/>: <c>v += J / m</c>, <c>ω += I⁻¹(r × J)</c>.</summary>
    public static void ApplyImpulse(ref BodyState b, Vector3 impulse, Vector3 r)
    {
        b.Velocity += impulse * b.InvMass;
        b.AngularVelocity += b.InvInertiaWorld(Vector3.Cross(r, impulse));
    }

    /// <summary>
    /// Начало подшага: прошлая поза ← нынешняя, скорость + гравитация, положение и поворот — на скорости · h, касание земли
    /// сброшено. Явно-неявный Эйлер: ошибка смещения при падении — <c>g·h·t / 2</c>.
    /// </summary>
    public static void Integrate(ref BodyState b, in BodyDynamics d, float h)
    {
        b.PrevPosition = b.Position;
        b.PrevRotation = b.Rotation;
        b.Velocity.Y -= d.Gravity * h;
        b.Position += b.Velocity * h;
        Rotate(ref b, b.AngularVelocity * h);
        b.Grounded = false;
    }

    /// <summary>
    /// Конец подшага: скорость — сдвиг / h, угловая — <c>2 · (q · q̄_prev).xyz / h</c>; затем демпфирование
    /// <c>max(0, 1 − k·h)</c> (не меняет знак при k·h &gt; 1), трение о землю при <see cref="BodyState.Grounded"/> и потолки по длине.
    /// </summary>
    public static void UpdateVelocity(ref BodyState b, in BodyDynamics d, float h)
    {
        b.Velocity = (b.Position - b.PrevPosition) / h;
        var dq = b.Rotation * Quaternion.Conjugate(b.PrevRotation);
        if (dq.W < 0) dq = Quaternion.Negate(dq);
        b.AngularVelocity = new Vector3(dq.X, dq.Y, dq.Z) * (2f / h);

        b.Velocity *= MathF.Max(0, 1 - d.LinearDamping * h);
        b.AngularVelocity *= MathF.Max(0, 1 - d.AngularDamping * h);
        if (b.Grounded)
        {
            float f = MathF.Max(0, 1 - d.GroundFriction * h);
            b.Velocity.X *= f;
            b.Velocity.Z *= f;
            b.AngularVelocity *= MathF.Max(0, 1 - d.GroundAngularFriction * h);
        }
        float v = b.Velocity.Length();
        if (v > d.MaxSpeed) b.Velocity *= d.MaxSpeed / v;
        float w = b.AngularVelocity.Length();
        if (w > d.MaxAngularSpeed) b.AngularVelocity *= d.MaxAngularSpeed / w;
    }
}
