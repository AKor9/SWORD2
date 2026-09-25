using System.Numerics;

namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Плоская система (x, z, курс) — корень персонажа на земле. Соглашение — как у <see cref="ClipLibrary.MotionMatrix"/> и
/// всего SWORD2: пространство персонажа +Z вперёд, +X влево, Y вверх; курс <see cref="Yaw"/> — поворот вокруг Y, рад,
/// «+ — от +Z к +X» (<see cref="Matrix4x4.CreateRotationY"/>, <c>Transform.Facing</c> игры): вперёд — (sin курса, 0, cos курса).
///
/// Вектор слева (v·M): точка системы в родителе — <c>p·R(курс) + (X, 0, Z)</c>, матрица — <see cref="ToMatrix"/>.
/// <see cref="Compose"/>(a, b) — система b, заданная в системе a, в родителе a: матрица <c>M_b · M_a</c>.
/// <see cref="Relative"/>(a, b) — система b (в родителе) в системе a: матрица <c>M_b · M_a⁻¹</c>; обратный порядок
/// (<c>M_a⁻¹ · M_b</c>) — ошибка, дающая ложный сдвиг на системах с поворотом (тем больше, чем дальше a от начала).
/// </summary>
public readonly record struct Pose2(float X, float Z, float Yaw)
{
    /// <summary>Тождество: начало координат, курс 0.</summary>
    public static Pose2 Identity => default;

    /// <summary>Система в точке <paramref name="position"/> (Y не учитывается) с курсом <paramref name="yaw"/>.</summary>
    public Pose2(Vector3 position, float yaw) : this(position.X, position.Z, yaw)
    {
    }

    /// <summary>Начало системы в родителе (Y = 0).</summary>
    public Vector3 Position => new(X, 0, Z);

    /// <summary>Направление «вперёд» системы в родителе: (sin курса, 0, cos курса).</summary>
    public Vector3 Forward => new(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));

    /// <summary>Все три числа конечны.</summary>
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Z) && float.IsFinite(Yaw);

    /// <summary>
    /// Вектор системы с курсом <paramref name="yaw"/> — в родителе (поворот вокруг Y, как <c>Vector3.TransformNormal</c> с
    /// <see cref="Matrix4x4.CreateRotationY"/>): <c>x' = x·cos + z·sin</c>, <c>z' = −x·sin + z·cos</c>, Y без изменений.
    /// Обратный поворот — с <c>−yaw</c>.
    /// </summary>
    public static Vector3 Rotate(Vector3 v, float yaw)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    /// <summary>Угол в (−π, π] (по модулю <see cref="MathF.Tau"/>); ровно −π — это π.</summary>
    public static float Wrap(float angle)
    {
        // IEEERemainder даёт [−π, π] точно (остаток и вычитание делителя — без округления)
        float r = MathF.IEEERemainder(angle, MathF.Tau);
        return r <= -MathF.PI ? r + MathF.Tau : r;
    }

    /// <summary>
    /// Система <paramref name="local"/>, заданная в системе <paramref name="a"/>, — в родителе <paramref name="a"/>
    /// (матрица <c>M_local · M_a</c>). Курс складывается без заворота: подряд идущие курсы остаются непрерывными.
    /// </summary>
    public static Pose2 Compose(Pose2 a, Pose2 local)
    {
        float c = MathF.Cos(a.Yaw), s = MathF.Sin(a.Yaw);
        return new Pose2(a.X + local.X * c + local.Z * s, a.Z - local.X * s + local.Z * c, a.Yaw + local.Yaw);
    }

    /// <summary>
    /// Система <paramref name="b"/> (в том же родителе, что <paramref name="a"/>) — в системе <paramref name="a"/>
    /// (матрица <c>M_b · M_a⁻¹</c>); курс — в (−π, π]. <c>Compose(a, Relative(a, b))</c> = b (курс — по модулю 2π).
    /// Перенос корня за интервал кадров — <c>Relative(кадр f, кадр f + 1)</c>.
    /// </summary>
    public static Pose2 Relative(Pose2 a, Pose2 b)
    {
        float c = MathF.Cos(a.Yaw), s = MathF.Sin(a.Yaw);
        float dx = b.X - a.X, dz = b.Z - a.Z;
        return new Pose2(dx * c - dz * s, dx * s + dz * c, Wrap(b.Yaw - a.Yaw));
    }

    /// <summary>Родитель в этой системе: <c>Relative(this, Identity)</c>.</summary>
    public Pose2 Inverse() => Relative(this, Identity);

    /// <summary>
    /// Доля <paramref name="t"/> системы от тождества: смещение и курс, умноженные на t (0 — тождество, 1 — сама система,
    /// побитово). Для интервала кадра (поворот за кадр — градусы) отличие от дуги окружности — доли миллиметра.
    /// </summary>
    public static Pose2 Lerp(Pose2 a, float t) => new(a.X * t, a.Z * t, a.Yaw * t);

    /// <summary>Точка этой системы — в родителе.</summary>
    public Vector3 ToParent(Vector3 local) => Rotate(local, Yaw) + Position;

    /// <summary>Точка родителя — в этой системе.</summary>
    public Vector3 ToLocal(Vector3 parent) => Rotate(parent - Position, -Yaw);

    /// <summary>Матрица системы: <see cref="ClipLibrary.MotionMatrix"/> — поворот на курс, затем смещение (вектор слева).</summary>
    public Matrix4x4 ToMatrix() => ClipLibrary.MotionMatrix(Position, Yaw);

    /// <summary>
    /// Плоская система матрицы (вектор слева): смещение — (M41, M43), курс — направление её оси +Z, в (−π, π]. Обратна
    /// <see cref="ToMatrix"/> и <see cref="ClipLibrary.MotionMatrix"/>; наклон и высота отбрасываются.
    /// </summary>
    public static Pose2 From(in Matrix4x4 m) => new(m.M41, m.M43, Wrap(MathF.Atan2(m.M31, m.M33)));

    public override string ToString() => $"({X:0.###}, {Z:0.###}; {Yaw * 180 / MathF.PI:0.#}°)";
}
