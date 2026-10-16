using System.Numerics;

namespace Sword.Engine.Physics;

// Внутренние типы и математика суставов. Открытые типы — Contract.cs, члены мира и крючки — PhysicsWorld.Joints.cs.

/// <summary>Сустав сочленения в мире: описание, привязка и включён ли. Массив записей — за слотом сочленения, переиспользуется.</summary>
internal struct JointRecord
{
    public JointDesc Desc;
    public Vector3 ParentAnchor;
    public Quaternion Rest;
    public bool Enabled;

    /// <summary>
    /// Пороги быстрого «в пределах» по описанию: tg² люфта оси, tg половины скручивания, tg² половины наименьшей полуоси
    /// эллипса. ∞ — предел не достижим.
    /// </summary>
    public float SlackTan2, TwistTanHalf, SwingTanHalf2;

    public static JointRecord Of(in JointDesc d)
    {
        float slack = d.AxisSlack, twist = d.Twist;
        float swing = MathF.Min(MathF.Min(MathF.Max(d.SwingXMax, Joints.EllipseFloor), MathF.Max(-d.SwingXMin, Joints.EllipseFloor)),
            MathF.Max(d.SwingZ, Joints.EllipseFloor));
        return new JointRecord
        {
            Desc = d,
            Enabled = true,
            SlackTan2 = slack < MathF.PI / 2 ? MathF.Tan(slack) * MathF.Tan(slack) : float.PositiveInfinity,
            TwistTanHalf = twist < MathF.PI ? MathF.Tan(twist / 2) : float.PositiveInfinity,
            SwingTanHalf2 = swing < MathF.PI ? MathF.Tan(swing / 2) * MathF.Tan(swing / 2) : float.PositiveInfinity,
        };
    }
}

/// <summary>Математика суставов — чистые функции над позами тел. Памяти не выделяют; аргументы не проверяют (проверяет <see cref="PhysicsWorld.SetJoints"/>).</summary>
internal static class Joints
{
    /// <summary>Нижняя граница полуосей эллипса наклона, рад: предел 0 по одной стороне X не делит на ноль.</summary>
    public const float EllipseFloor = 1e-3f;

    /// <summary>Порог длины части (Y, W) отклонения и допустимого наклона: меньше — наклон на π (скручивание 0) или наклона нет.</summary>
    public const float TwistEpsilon = 1e-6f;

    /// <summary>Порог длины <c>ac × ap</c> шарнира: меньше — оси совпадают или противоположны, направления поправки оси нет.</summary>
    public const float AxisEpsilon = 1e-7f;

    /// <summary>Порог относительной скорости точек сустава (квадрат, м²/с²): меньше — расслаблять нечего.</summary>
    public const float RelaxEpsilonSquared = 1e-12f;

    /// <summary>Порог дополнения Шура «точка + ось предела» (доля от <c>w</c>): меньше — случай вырожден, отскок не гасится.</summary>
    public const float SchurEpsilon = 1e-6f;

    /// <summary>Порог |b⊥|² шарнира: меньше — ось капсулы ребёнка вдоль оси шарнира (сгиб не определён), градиент — ось шарнира.</summary>
    public const float GradientEpsilon = 1e-8f;

    /// <summary>Нижняя граница sin φ у наклона к π (направление наклона вырождается) в градиенте меры эллипса.</summary>
    public const float SinFloor = 1e-3f;

    /// <summary>
    /// Угол от <paramref name="a"/> к <paramref name="b"/> вокруг единичной <paramref name="n"/>, рад (−π…π), в плоскости поперёк оси:
    /// <c>atan2((a × b)·n, a·b − (a·n)(b·n))</c> — составляющие вдоль оси не искажают угол.
    /// </summary>
    public static float SignedAngle(Vector3 a, Vector3 b, Vector3 n) =>
        MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), n), Vector3.Dot(a, b) - Vector3.Dot(a, n) * Vector3.Dot(b, n));

    /// <summary>Шарнир: ось шарнира, какой её держит родитель (перенесённая позой покоя), мир, единичная.</summary>
    public static Vector3 ParentHingeAxis(in JointDesc d, Quaternion parent, Quaternion rest) =>
        Vector3.Normalize(Vector3.Transform(d.HingeAxis, parent * rest));

    /// <summary>Шарнир: ось шарнира ребёнка, мир, единичная.</summary>
    public static Vector3 ChildHingeAxis(in JointDesc d, Quaternion child) => Vector3.Normalize(Vector3.Transform(d.HingeAxis, child));

    /// <summary>
    /// Шарнир: уход оси ребёнка <paramref name="ac"/> от оси родителя <paramref name="ap"/>, рад (0…π; <c>atan2</c> верен и за 90°,
    /// в отличие от <c>asin</c>); <paramref name="cross"/> = <c>ac × ap</c> — ось поворота ребёнка к родителю.
    /// </summary>
    public static float OffAxis(Vector3 ac, Vector3 ap, out Vector3 cross)
    {
        cross = Vector3.Cross(ac, ap);
        return MathF.Atan2(cross.Length(), Vector3.Dot(ac, ap));
    }

    /// <summary>Шарнир: сгиб — угол от оси капсулы родителя к оси ребёнка вокруг <paramref name="ap"/>, × анатомический знак.</summary>
    public static float Bend(in JointDesc d, Quaternion parent, Quaternion child, Vector3 ap) =>
        SignedAngle(Vector3.Transform(Vector3.UnitY, parent), Vector3.Transform(Vector3.UnitY, child), ap) * d.BendSign;

    /// <summary>
    /// Шарнир: градиент сгиба по повороту ребёнка, мир: <c>знак · (n − (b·n)·b⊥/|b⊥|²)</c> (b — ось капсулы, b⊥ — поперёк n);
    /// по родителю — с обратным знаком. Поправка предела — вдоль градиента меры.
    /// </summary>
    public static Vector3 BendGradient(in JointDesc d, Quaternion child, Vector3 n)
    {
        var b = Vector3.Transform(Vector3.UnitY, child);
        float bn = Vector3.Dot(b, n);
        var across = b - n * bn;
        float l2 = across.LengthSquared();
        var g = l2 > GradientEpsilon ? n - across * (bn / l2) : n;
        return g * d.BendSign;
    }

    /// <summary>Отклонение от позы покоя <c>q = conj(parent · rest) · child</c> (W ≥ 0); левый δ — поворот ребёнка на <c>(parent · rest)·δ</c> в мире.</summary>
    public static Quaternion Deviation(Quaternion parent, Quaternion rest, Quaternion child)
    {
        var q = Quaternion.Conjugate(parent * rest) * child;
        return q.W < 0 ? Quaternion.Negate(q) : q;
    }

    /// <summary>
    /// Замер наклона-скручивания: q = наклон · скручивание; скручивание — <c>2·atan2(q.Y, q.W)</c>, наклон — вектор поворота без Y.
    /// Выходы, рад: наклона — вдоль луча за эллипс, скручивания — за <c>±Twist</c>.
    /// </summary>
    public static void SwingTwist(in JointDesc d, Quaternion q, out float swing, out float twist, out float swingExcess, out float twistExcess)
    {
        var sv = SwingVector(q);
        swing = sv.Length();
        twist = 2 * MathF.Atan2(q.Y, q.W);
        float e = Ellipse(d, sv);
        swingExcess = e > 1 ? swing * (1 - 1 / MathF.Sqrt(e)) : 0;
        twistExcess = MathF.Abs(twist - Math.Clamp(twist, -d.Twist, d.Twist));
    }

    /// <summary>
    /// Поправка наклона: вне эллипса — true и левый поворот <paramref name="delta"/> вдоль градиента меры <c>C = √E − 1</c>:
    /// <c>δ = −C·∇C/|∇C|²</c>, <c>∇C = (G·u)·u + (G·v)·φ·ctg φ·v − (G·v)·φ·Y</c> (u — ось наклона, φ — угол, G = ∇_sv C).
    /// Сжатие к центру вдоль луча у неокружного эллипса — не нормаль: предел совершал бы работу.
    /// </summary>
    public static bool SwingCorrection(in JointDesc d, Quaternion q, out Vector3 delta, float tanHalf2 = 0)
    {
        delta = default;
        // Быстрое «внутри» — наклон не больше наименьшей полуоси m (круг внутри эллипса): 2·atan2(ρ, l) ≤ m ⇔ ρ² ≤ l²·tg²(m/2)
        if (q.X * q.X + q.Z * q.Z <= (q.Y * q.Y + q.W * q.W) * tanHalf2) return false;
        var sv = SwingVector(q);
        float phi = sv.Length();
        if (!(phi > TwistEpsilon)) return false;
        var u = sv / phi;
        float lx = MathF.Max(sv.X >= 0 ? d.SwingXMax : -d.SwingXMin, EllipseFloor), lz = MathF.Max(d.SwingZ, EllipseFloor);
        float e = sv.X / lx * (sv.X / lx) + sv.Z / lz * (sv.Z / lz);
        if (!(e > 1)) return false;
        float root = MathF.Sqrt(e);
        var g = new Vector3(sv.X / (lx * lx), 0, sv.Z / (lz * lz)) / root;
        var v = new Vector3(-u.Z, 0, u.X);
        float gu = Vector3.Dot(g, u), gv = Vector3.Dot(g, v);
        float sin = MathF.Sin(phi);
        // φ·ctg φ: у малого наклона → 1; у наклона к π направление наклона вырождается — синус ограничен снизу.
        float k = sin > SinFloor ? phi * MathF.Cos(phi) / sin : phi < 1 ? 1 : phi * MathF.Cos(phi) / SinFloor;
        var grad = u * gu + v * (gv * k) - Vector3.UnitY * (gv * phi);
        delta = grad * ((1 - root) / grad.LengthSquared());
        return true;
    }

    /// <summary>
    /// Поправка скручивания: за <c>±Twist</c> — true и δ вдоль градиента τ = 2·atan2(q.Y, q.W):
    /// <c>g = (xy − wz, w² + y², wx + yz)/(w² + y²)</c>, <c>δ = g·(τ_c − τ)/|g|²</c>. Поворот вокруг Y ребёнка при большом
    /// наклоне отклонён от градиента (плечо 2,6 рад — на 75°): предел совершал работу по замкнутому пути.
    /// </summary>
    public static bool TwistCorrection(in JointDesc d, Quaternion q, out Vector3 delta, float tanHalf = 0)
    {
        delta = default;
        float yw = q.Y * q.Y + q.W * q.W;
        if (!(yw > TwistEpsilon * TwistEpsilon)) return false;
        // Быстрое «в пределе» (W ≥ 0 у отклонения): |2·atan2(y, w)| ≤ T ⇔ |y| ≤ w·tg(T/2)
        if (MathF.Abs(q.Y) <= q.W * tanHalf) return false;
        float twist = 2 * MathF.Atan2(q.Y, q.W);
        float tc = Math.Clamp(twist, -d.Twist, d.Twist);
        if (tc == twist) return false;
        var g = new Vector3(q.X * q.Y - q.W * q.Z, yw, q.W * q.X + q.Y * q.Z) / yw;
        delta = g * ((tc - twist) / g.LengthSquared());
        return true;
    }

    /// <summary>Вектор поворота наклона отклонения q (без Y): угол <c>2·atan2(√(x²+z²), √(y²+w²))</c>, ось — в плоскости XZ.</summary>
    private static Vector3 SwingVector(Quaternion q)
    {
        float rho = MathF.Sqrt(q.X * q.X + q.Z * q.Z), l = MathF.Sqrt(q.Y * q.Y + q.W * q.W);
        if (!(rho > 1e-15f)) return Vector3.Zero;
        // ось наклона — (w·x + z·y, 0, w·z − x·y)/(l·ρ): единичная (её длина — ρ·l)
        var u = l > TwistEpsilon ? new Vector3(q.W * q.X + q.Z * q.Y, 0, q.W * q.Z - q.X * q.Y) / (l * rho) : new Vector3(q.X, 0, q.Z) / rho;
        return u * (2 * MathF.Atan2(rho, l));
    }

    /// <summary>Мера эллипса наклона E (1 — на границе): полуось X — по знаку наклона, не меньше <see cref="EllipseFloor"/>.</summary>
    private static float Ellipse(in JointDesc d, Vector3 sv)
    {
        float lx = MathF.Max(sv.X >= 0 ? d.SwingXMax : -d.SwingXMin, EllipseFloor), lz = MathF.Max(d.SwingZ, EllipseFloor);
        return sv.X / lx * (sv.X / lx) + sv.Z / lz * (sv.Z / lz);
    }

    /// <summary>
    /// Замер сустава: шарнир — сгиб, наклон = уход оси, выход — из сгиба и люфта; наклон-скручивание — наклон, скручивание и
    /// наибольший из их выходов.
    /// </summary>
    public static JointMeasure Measure(in JointRecord r, in BodyState parent, in BodyState child)
    {
        float pointError = Vector3.Distance(child.ToWorld(r.Desc.ChildAnchor), parent.ToWorld(r.ParentAnchor));
        if (r.Desc.Kind == JointKind.Hinge)
        {
            var ap = ParentHingeAxis(r.Desc, parent.Rotation, r.Rest);
            float off = OffAxis(ChildHingeAxis(r.Desc, child.Rotation), ap, out _);
            float bend = Bend(r.Desc, parent.Rotation, child.Rotation, ap);
            float excess = MathF.Max(MathF.Max(r.Desc.BendMin - bend, bend - r.Desc.BendMax), off - r.Desc.AxisSlack);
            return new JointMeasure(pointError, bend, off, 0, MathF.Max(excess, 0));
        }
        SwingTwist(r.Desc, Deviation(parent.Rotation, r.Rest, child.Rotation), out float swing, out float twist, out float se, out float te);
        return new JointMeasure(pointError, 0, swing, twist, MathF.Max(se, te));
    }

    /// <summary>Прибавить к столбцам K отклик скорости точки на плече <paramref name="r"/> на единичный импульс: <c>e/m + (I⁻¹(r × e)) × r</c>.</summary>
    public static void AddPointMass(in BodyState b, Vector3 r, ref Vector3 kx, ref Vector3 ky, ref Vector3 kz)
    {
        kx += Vector3.UnitX * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitX)), r);
        ky += Vector3.UnitY * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitY)), r);
        kz += Vector3.UnitZ * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitZ)), r);
    }

    /// <summary>То же по матрице обратной инерции в мире (поза подшага уже не меняется) — без поворотов на столбец.</summary>
    public static void AddPointMass(in InvInertiaMatrix w, Vector3 r, ref Vector3 kx, ref Vector3 ky, ref Vector3 kz)
    {
        kx += Vector3.UnitX * w.InvMass + Vector3.Cross(w.Apply(new Vector3(0, r.Z, -r.Y)), r);
        ky += Vector3.UnitY * w.InvMass + Vector3.Cross(w.Apply(new Vector3(-r.Z, 0, r.X)), r);
        kz += Vector3.UnitZ * w.InvMass + Vector3.Cross(w.Apply(new Vector3(r.Y, -r.X, 0)), r);
    }

    /// <summary>Решить <c>K x = rhs</c> по Крамеру; определитель ≤ 0 или не конечен — false.</summary>
    public static bool Solve(Vector3 c0, Vector3 c1, Vector3 c2, Vector3 rhs, out Vector3 x)
    {
        var c12 = Vector3.Cross(c1, c2);
        float det = Vector3.Dot(c0, c12);
        if (!(det > 0) || !float.IsFinite(det))
        {
            x = default;
            return false;
        }
        x = new Vector3(Vector3.Dot(rhs, c12), Vector3.Dot(c0, Vector3.Cross(rhs, c2)), Vector3.Dot(c0, Vector3.Cross(c1, rhs))) / det;
        return true;
    }
}
