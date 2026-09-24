using System.Numerics;

namespace Sword.Engine.Physics;

// 7в: внутренние типы и математика суставов. Открытые типы (JointDesc, JointBinding, JointLimitHit, JointMeasure) — Contract.cs,
// члены мира и крючки — PhysicsWorld.Joints.cs. Идеи — Fighter.Joints SWORD 2eab13b (источник, не образец; README «7в»).

/// <summary>
/// Сустав сочленения в мире (7в): описание, привязка (точка на родителе в системе родителя, поза покоя
/// <c>conj(parent) · child</c>) и включён ли. Массив записей — за слотом сочленения, переиспользуется.
/// </summary>
internal struct JointRecord
{
    public JointDesc Desc;
    public Vector3 ParentAnchor;
    public Quaternion Rest;
    public bool Enabled;
}

/// <summary>
/// Математика суставов (7в) — чистые функции над позами тел: ось и сгиб шарнира, отклонение от позы покоя, наклон и
/// скручивание, поправки пределов вдоль градиентов их мер, замер, эффективная масса точки для расслабления. Памяти не выделяют;
/// аргументы не проверяют (горячий путь — проверяет мир в <see cref="PhysicsWorld.SetJoints"/>).
/// </summary>
internal static class Joints
{
    /// <summary>Нижняя граница полуосей эллипса наклона, рад: предел 0 по одной стороне X не делит на ноль.</summary>
    public const float EllipseFloor = 1e-3f;

    /// <summary>
    /// Порог длины части (Y, W) отклонения и длины допустимого наклона: меньше — наклон на π (скручивание не определено,
    /// берётся 0) или наклона нет.
    /// </summary>
    public const float TwistEpsilon = 1e-6f;

    /// <summary>Порог длины <c>ac × ap</c> шарнира: меньше — оси совпадают или противоположны, направления поправки оси нет.</summary>
    public const float AxisEpsilon = 1e-7f;

    /// <summary>Порог относительной скорости точек сустава (квадрат, м²/с²): меньше — расслаблять нечего.</summary>
    public const float RelaxEpsilonSquared = 1e-12f;

    /// <summary>
    /// Порог дополнения Шура системы «точка + ось предела» (доля от <c>w</c>): меньше — угловой импульс неотличим от импульса в
    /// точку (вырожденный случай), отскок не гасится.
    /// </summary>
    public const float SchurEpsilon = 1e-6f;

    /// <summary>Порог |b⊥|² шарнира: меньше — ось капсулы ребёнка вдоль оси шарнира (сгиб не определён), градиент — ось шарнира.</summary>
    public const float GradientEpsilon = 1e-8f;

    /// <summary>Нижняя граница sin φ у наклона к π (направление наклона вырождается) в градиенте меры эллипса.</summary>
    public const float SinFloor = 1e-3f;

    /// <summary>
    /// Угол от <paramref name="a"/> к <paramref name="b"/> вокруг единичной оси <paramref name="n"/>, рад (−π…π) — в
    /// плоскости, перпендикулярной оси: <c>atan2((a × b)·n, a·b − (a·n)(b·n))</c>. У SWORD вторая часть — просто <c>a·b</c>:
    /// составляющие векторов вдоль оси искажали угол, когда кость родителя не перпендикулярна оси шарнира.
    /// </summary>
    public static float SignedAngle(Vector3 a, Vector3 b, Vector3 n) =>
        MathF.Atan2(Vector3.Dot(Vector3.Cross(a, b), n), Vector3.Dot(a, b) - Vector3.Dot(a, n) * Vector3.Dot(b, n));

    /// <summary>Шарнир: ось шарнира, какой её держит родитель (перенесённая позой покоя), мир, единичная.</summary>
    public static Vector3 ParentHingeAxis(in JointDesc d, Quaternion parent, Quaternion rest) =>
        Vector3.Normalize(Vector3.Transform(d.HingeAxis, parent * rest));

    /// <summary>Шарнир: ось шарнира ребёнка, мир, единичная.</summary>
    public static Vector3 ChildHingeAxis(in JointDesc d, Quaternion child) => Vector3.Normalize(Vector3.Transform(d.HingeAxis, child));

    /// <summary>
    /// Шарнир: уход оси ребёнка <paramref name="ac"/> от оси родителя <paramref name="ap"/>, рад (0…π, <c>atan2</c> — верно и
    /// за 90°, где <c>asin |ac × ap|</c> SWORD ошибался); <paramref name="cross"/> = <c>ac × ap</c> — ось поворота ребёнка к оси
    /// родителя.
    /// </summary>
    public static float OffAxis(Vector3 ac, Vector3 ap, out Vector3 cross)
    {
        cross = Vector3.Cross(ac, ap);
        return MathF.Atan2(cross.Length(), Vector3.Dot(ac, ap));
    }

    /// <summary>
    /// Шарнир: сгиб — угол от оси капсулы родителя к оси капсулы ребёнка вокруг оси шарнира <paramref name="ap"/> (от
    /// выпрямленного положения), умноженный на анатомический знак описания.
    /// </summary>
    public static float Bend(in JointDesc d, Quaternion parent, Quaternion child, Vector3 ap) =>
        SignedAngle(Vector3.Transform(Vector3.UnitY, parent), Vector3.Transform(Vector3.UnitY, child), ap) * d.BendSign;

    /// <summary>
    /// Шарнир: градиент сгиба (со знаком описания) по повороту ребёнка, мир: <c>знак · (n − (b·n)·b⊥/|b⊥|²)</c>, где n — ось
    /// шарнира родителя, b — ось капсулы ребёнка, b⊥ — её часть поперёк n. У ребёнка, чья ось капсулы не перпендикулярна оси
    /// шарнира (уход в пределах люфта), градиент отклонён от n; по родителю — с обратным знаком (сгиб не меняется от общего
    /// поворота). Поправка сгиба — вдоль него (README «7в»: поправка предела — вдоль градиента меры).
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

    /// <summary>
    /// Наклон-скручивание: отклонение ребёнка от позы покоя <c>q = conj(parent · rest) · child</c> (система покоя; W ≥ 0). Левый
    /// поворот <c>δ</c> отклонения в системе покоя — поворот ребёнка на <c>(parent · rest)·δ</c> в мире.
    /// </summary>
    public static Quaternion Deviation(Quaternion parent, Quaternion rest, Quaternion child)
    {
        var q = Quaternion.Conjugate(parent * rest) * child;
        return q.W < 0 ? Quaternion.Negate(q) : q;
    }

    /// <summary>
    /// Замер наклона-скручивания по отклонению <paramref name="q"/> (<see cref="Deviation"/>): q = наклон · скручивание;
    /// скручивание вокруг Y — <c>2·atan2(q.Y, q.W)</c> (−π…π), наклон — вектор поворота без Y (<paramref name="swing"/> — его
    /// длина). Выход наклона — вдоль луча за эллипс (X — <c>[SwingXMin, SwingXMax]</c>, Z — <c>±SwingZ</c>), рад; выход
    /// скручивания за <c>±Twist</c>, рад.
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
    /// Поправка наклона: вне эллипса — true и левый поворот <paramref name="delta"/> (система покоя, рад) вдоль градиента меры
    /// эллипса <c>C = √E − 1</c> (<c>E = (sv.X/lx)² + (sv.Z/lz)²</c>): <c>δ = −C·∇C/|∇C|²</c>. Градиент — аналитический: наклон
    /// зависит только от левого множителя q = s·t; в базисе плоскости наклона (u — ось наклона, v = Y × u… поперёк, φ — угол)
    /// <c>d(sv) = u·(u·δ) + φ·v·(ctg φ·(v·δ) − δ_Y)</c>, откуда <c>∇C = (G·u)·u + (G·v)·φ·ctg φ·v − (G·v)·φ·Y</c>, G = ∇_sv C.
    /// У SWORD наклон сжимался к центру эллипса вдоль луча — у неокружного эллипса это не нормаль: предел совершал работу.
    /// </summary>
    public static bool SwingCorrection(in JointDesc d, Quaternion q, out Vector3 delta)
    {
        delta = default;
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
    /// Поправка скручивания: за <c>±Twist</c> — true и левый поворот <paramref name="delta"/> (система покоя, рад) вдоль градиента
    /// скручивания τ = 2·atan2(q.Y, q.W): <c>g = (xy − wz, w² + y², wx + yz)/(w² + y²)</c>, |g| = 1/cos(наклон/2),
    /// <c>δ = g·(τ_c − τ)/|g|²</c>. У SWORD — поворот вокруг оси ребёнка (Y): при наклоне плеча 2,6 рад градиент отклонён от неё
    /// на 75°, и предел совершал работу по замкнутому пути (коническое движение руки — рост энергии).
    /// </summary>
    public static bool TwistCorrection(in JointDesc d, Quaternion q, out Vector3 delta)
    {
        delta = default;
        float yw = q.Y * q.Y + q.W * q.W;
        if (!(yw > TwistEpsilon * TwistEpsilon)) return false;
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
    /// Замер сустава (<see cref="JointMeasure"/>): ошибка точки; шарнир — сгиб со знаком, наклон = уход оси шарнира,
    /// скручивание 0, выход — наибольший из выхода сгиба за <c>[BendMin, BendMax]</c> и ухода оси сверх люфта; наклон-скручивание
    /// — сгиб 0, длина наклона, скручивание со знаком, выход — наибольший из выходов наклона и скручивания.
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

    /// <summary>
    /// Прибавить к столбцам <paramref name="kx"/>, <paramref name="ky"/>, <paramref name="kz"/> матрицы K отклик скорости точки
    /// тела на плече <paramref name="r"/> на единичный импульс по осям мира: <c>e/m + (I⁻¹(r × e)) × r</c>. K симметрична и
    /// положительно определена (у тела с массой).
    /// </summary>
    public static void AddPointMass(in BodyState b, Vector3 r, ref Vector3 kx, ref Vector3 ky, ref Vector3 kz)
    {
        kx += Vector3.UnitX * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitX)), r);
        ky += Vector3.UnitY * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitY)), r);
        kz += Vector3.UnitZ * b.InvMass + Vector3.Cross(b.InvInertiaWorld(Vector3.Cross(r, Vector3.UnitZ)), r);
    }

    /// <summary>
    /// Решить <c>K x = rhs</c> (столбцы K — <paramref name="c0"/>, <paramref name="c1"/>, <paramref name="c2"/>) по Крамеру;
    /// определитель не больше 0 или не конечен — false. У SWORD — <c>Matrix4x4.Invert</c> с единицей в углу: то же, дороже.
    /// </summary>
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
