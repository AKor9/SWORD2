using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Профиль динамики тела на физике (идея — <c>BodyDynamics</c> SWORD 2eab13b): гравитация, демпфирование, трение о землю,
/// потолки скоростей. Мир держит таблицу профилей (<see cref="IPhysicsWorld.RegisterDynamics"/>), тело — номер профиля
/// (<see cref="BodyState.Profile"/>); профиль 0 — по умолчанию (<c>new BodyDynamics()</c>), игра регистрирует свои
/// (профиль трупа). Демпфирование и трение — доли скорости в секунду: за подшаг h скорость умножается на <c>max(0, 1 − k·h)</c>.
/// Отправные значения — SWORD: 9,81 / 0,15 / 0,8 / 9 / 3 / 40 / 60. <c>default(BodyDynamics)</c> — не профиль (нулевые
/// потолки): мир его не примет.
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
/// Динамика тела-капсулы — функции над <c>ref BodyState</c> (идеи — <c>RigidBody</c> SWORD 2eab13b): форма с массой,
/// позиционная коррекция импульсом и поворот (решатель XPBD — <see cref="Xpbd"/>), импульс в скорость, интеграция подшага и
/// обновление скоростей подшага по профилю. Запросы (точки, скорость точки, обратная масса и инерция) — члены
/// <see cref="BodyState"/>. Функции не проверяют аргументы (горячий путь): <c>h</c> — конечный и больше 0, направления —
/// единичные; проверяет мир. Памяти не выделяют.
/// </summary>
public static class Dynamics
{
    /// <summary>
    /// Наибольший поворот за один вызов <see cref="Rotate"/>, рад (SWORD: 0,5): линеаризованный поворот кватерниона верен для
    /// малых углов, большой шаг решателя — признак взрыва; обрезка действует и на <see cref="Correct"/>, и на интеграцию.
    /// </summary>
    public const float MaxRotationPerCall = 0.5f;

    /// <summary>
    /// Обратная инерция сплошной капсулы (цилиндр высотой 2·<paramref name="halfLength"/> и две полусферы радиуса
    /// <paramref name="radius"/>, плотность однородная) — диагональ в системе тела, ось капсулы — Y. Масса больше 0, радиус
    /// больше 0, полудлина ≥ 0. Вдоль оси инерция наименьшая (у шара — равная).
    /// <para>
    /// Не формула SWORD (<c>len = 2·halfLen + r</c>, цилиндр длиной len): у короткой капсулы она даёт осевую инерцию больше
    /// поперечной — шар (<c>halfLen = 0</c>) у неё анизотропен (<c>m r²/3</c> поперёк против <c>m r²/2</c> вдоль), а капсулы
    /// короче <c>0,37·r</c> «катятся» не той осью. Точная формула не дороже и верна при любых пропорциях (решение 7б).
    /// </para>
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

    /// <summary>
    /// Форма с массой: радиус, полудлина, масса, обратная масса и обратная инерция капсулы (<see cref="CapsuleInvInertia"/>).
    /// Масса — конечная и больше 0, радиус — больше 0, полудлина ≥ 0 (проверяет мир).
    /// </summary>
    public static void SetShape(ref BodyState b, float mass, float radius, float halfLength)
    {
        b.Radius = radius;
        b.HalfLength = halfLength;
        b.Mass = mass;
        b.InvMass = 1f / mass;
        b.InvInertia = CapsuleInvInertia(mass, radius, halfLength);
    }

    /// <summary>
    /// Повернуть тело на вектор поворота <paramref name="dTheta"/> (мир, рад) линеаризованно:
    /// <c>q ← normalize(q + ½ (dθ, 0) · q)</c>; длина шага обрезается до <see cref="MaxRotationPerCall"/>.
    /// </summary>
    public static void Rotate(ref BodyState b, Vector3 dTheta)
    {
        float l = dTheta.Length();
        if (l > MaxRotationPerCall) dTheta *= MaxRotationPerCall / l;
        b.Rotation = Quaternion.Normalize(b.Rotation + new Quaternion(dTheta * 0.5f, 0) * b.Rotation);
    }

    /// <summary>
    /// Позиционная коррекция импульсом <paramref name="impulse"/> (кг·м — λ·n решателя) в точке с плечом
    /// <paramref name="r"/> (от центра, мир): центр сдвигается на <c>impulse / m</c>, тело поворачивается на
    /// <c>I⁻¹(r × impulse)</c> (<see cref="Rotate"/>, с обрезкой угла).
    /// </summary>
    public static void Correct(ref BodyState b, Vector3 impulse, Vector3 r)
    {
        b.Position += impulse * b.InvMass;
        Rotate(ref b, b.InvInertiaWorld(Vector3.Cross(r, impulse)));
    }

    /// <summary>
    /// Импульс <paramref name="impulse"/> (Н·с) в скорость в точке с плечом <paramref name="r"/> (от центра, мир):
    /// <c>v += J / m</c>, <c>ω += I⁻¹(r × J)</c>. Скоростной проход контактов, расслабление суставов, импульс удара.
    /// </summary>
    public static void ApplyImpulse(ref BodyState b, Vector3 impulse, Vector3 r)
    {
        b.Velocity += impulse * b.InvMass;
        b.AngularVelocity += b.InvInertiaWorld(Vector3.Cross(r, impulse));
    }

    /// <summary>
    /// Начало подшага <paramref name="h"/>, с: прошлая поза ← нынешняя; скорость — плюс гравитация профиля (вниз по Y);
    /// положение — плюс скорость · h; поворот — на угловую скорость · h (<see cref="Rotate"/>); касание земли сброшено
    /// (его ставят контакты подшага). Явно-неявный Эйлер: ошибка смещения при падении — <c>g·h·t / 2</c>.
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
    /// Конец подшага <paramref name="h"/>, с (после проекций): скорость — сдвиг за подшаг / h; угловая —
    /// <c>2 · (q · q̄_prev).xyz / h</c> (кратчайший путь; стандартное приближение XPBD, при ω·h ≪ 1 неотличимо от
    /// <see cref="PhysicsMath.RotationVector"/>); затем демпфирование профиля <c>max(0, 1 − k·h)</c>; при
    /// <see cref="BodyState.Grounded"/> — трение о землю: горизонтальная скорость (X, Z) и угловая; наконец потолки
    /// скоростей — по длине, направление сохраняется. Множители не бывают отрицательными (у SWORD демпфирование при
    /// <c>k·h &gt; 1</c> меняло бы знак скорости).
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
