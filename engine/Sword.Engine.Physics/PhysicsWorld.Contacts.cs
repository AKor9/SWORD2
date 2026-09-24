using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Engine.Physics;

// 7г: статический мир (земля, стена, колонны), контакты подшага — с миром, пары сочленений, скоростной проход — и границы
// сочленений. Земля по граням — Ground.cs; шаг — PhysicsWorld.Step.cs. Идеи — SolveContacts, PushOut, SolvedDepth, Collide,
// PrevOverlap, SolveContactVelocities, UpdateBounds SWORD 2eab13b (research-arena-tick §1.3); свои решения — README «7г».
public sealed partial class PhysicsWorld
{
    /// <summary>Запас буфера скоростного прохода — контактов на слот тела (растёт вместе со слотами тел; переполнение — удвоение, разово).</summary>
    private const int ContactsPerBody = 4;

    /// <summary>Порог нормальной относительной скорости скоростного прохода, м/с: меньше — гасить нечего.</summary>
    private const float ContactsVelocityEpsilon = 1e-6f;

    /// <summary>Порог расстояния, м: ближе — направление (от оси колонны, между осями капсул) не определено, берётся запасное.</summary>
    private const float ContactsDirectionEpsilon = 1e-5f;

    private IGround? _ground;
    private float _wallRadius = float.PositiveInfinity;
    private Pillar[] _pillars = [];
    private int _pillarCount;

    /// <summary>Границы сочленений по слотам: в шаге — свежие (пересчёт перед проходом пар каждого подшага), между шагами — конца шага.</summary>
    private ArticulationBounds[] _contactsBounds = [];

    /// <summary>Тел на физике в сочленении — на начало шага (режим внутри шага не меняется); 0 — сочленение шаг не двигает.</summary>
    private int[] _contactsSimulated = [];

    /// <summary>Сочленений с телами на физике — на начало шага.</summary>
    private int _contactsSimulatedArticulations;

    /// <summary>Контакты подшага для скоростного прохода (по порядку решения), заранее выделенные.</summary>
    private ContactsVelocityEntry[] _contactsVelocity = [];
    private int _contactsVelocityCount;

    /// <summary>Касания торца гранями земли — буфер для <see cref="IGround.Touches"/> (без stackalloc и без выделения).</summary>
    private readonly GroundTouch[] _groundTouches = new GroundTouch[IGround.MaxTouches];

    // Настройки контактов — читаются из _settings раз на шаг (настройки меняются только между шагами).
    private float _contactsPopSpeed, _contactsFriction, _contactsCompliance;

    /// <summary>Пар сочленений, прошедших отсев по свежим границам, за последний шаг (все подшаги) — тестам и диагностике.</summary>
    internal int ContactsPairChecks { get; private set; }

    /// <summary>Пар тел, дошедших до ближайших точек осей (<c>SegSeg</c>), за последний шаг — тестам и диагностике.</summary>
    internal int ContactsNarrowChecks { get; private set; }

    /// <summary>
    /// Контакт подшага для скоростного прохода: точки — в системах тел (до выталкивания), нормаль — от второго к первому; второго
    /// нет (−1) — мир. <see cref="Sliding"/> — предел импульса трения скольжения, Н·с: <c>μ · λn / h</c> (0 — без трения).
    /// </summary>
    private struct ContactsVelocityEntry
    {
        public int Body, Other;
        public Vector3 LocalA, LocalB, Normal;
        public float Sliding;
        public PhysicsSource Source;
    }

    public void SetGround(IGround? ground) => _ground = ground;

    public void SetWall(float radius)
    {
        if (!(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "радиус стены — больше 0 (∞ — стены нет)");
        _wallRadius = radius;
    }

    public void SetPillars(ReadOnlySpan<Pillar> pillars)
    {
        for (int i = 0; i < pillars.Length; i++)
        {
            var p = pillars[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Z) || !float.IsFinite(p.Radius) || !(p.Radius > 0))
                throw new ArgumentOutOfRangeException(nameof(pillars), p, $"колонна {i}: ось — конечная, радиус — конечный и больше 0");
        }
        if (_pillars.Length < pillars.Length) _pillars = new Pillar[pillars.Length];
        pillars.CopyTo(_pillars);
        _pillarCount = pillars.Length;
    }

    public ArticulationBounds Bounds(ArticulationHandle articulation)
    {
        Check(articulation);
        return _contactsBounds[articulation.Index];
    }

    partial void ContactsGrow(int articulations, int bodies)
    {
        if (_contactsBounds.Length < articulations)
        {
            Array.Resize(ref _contactsBounds, articulations);
            Array.Resize(ref _contactsSimulated, articulations);
        }
        int velocity = bodies * ContactsPerBody;
        if (_contactsVelocity.Length < velocity) Array.Resize(ref _contactsVelocity, velocity);
    }

    partial void ContactsReset(int articulation)
    {
        _contactsBounds[articulation] = default;
        _contactsSimulated[articulation] = 0;
    }

    /// <summary>
    /// Сколько глубины решать в этом подшаге (идея — <c>SolvedDepth</c> SWORD): старая глубина <paramref name="pen0"/> — та же
    /// пара в прошлой позе (начало подшага) — уходит не быстрее <paramref name="popSpeed"/>: после подшага останется не больше
    /// <c>pen0 − popSpeed·h</c>; новое проникновение этого подшага (падение, сустав, толчок) решается целиком, без добавки:
    /// <c>clamp(pen − max(0, pen0 − popSpeed·h), 0, pen)</c>. Прошлые позы не двигаются — скрытого сдвига нет. Не «встречное
    /// движение + popSpeed·h»: тогда выход добавлялся бы каждый подшаг, пока сустав вдавливает часть обратно.
    /// </summary>
    internal static float ContactsSolvedDepth(float pen, float pen0, float popSpeed, float h) =>
        pen <= 0 ? 0 : Math.Clamp(pen - MathF.Max(0, pen0 - popSpeed * h), 0, pen);

    // ——— Мир: земля (Ground.cs), стена, колонны ———

    /// <summary>Контакты тел на физике с миром в подшаге — по слотам: сочленения по номеру, тела по порядку.</summary>
    private void ContactsWorld(float h)
    {
        if (_contactsSimulatedArticulations == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_contactsSimulated[a] > 0) ContactsWorldArticulation(a, h);
    }

    private void ContactsWorldArticulation(int a, float h)
    {
        var members = _artBodies[a]!;
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            int b = members[k];
            if (_bodies[b].Mode == BodyMode.Simulated) ContactsWorldBody(b, h);
        }
    }

    /// <summary>Тело на физике — земля (торцы и трение), затем стена и колонны; трассе — по источнику.</summary>
    private void ContactsWorldBody(int body, float h)
    {
        if (_ground is not null)
        {
            if (_trace is not null) TraceBegin(body);
            GroundBody(body, h);
            if (_trace is not null) TraceEnd(body, PhysicsSource.Ground, h);
        }
        if (_wallRadius < float.PositiveInfinity || _pillarCount > 0)
        {
            if (_trace is not null) TraceBegin(body);
            if (_wallRadius < float.PositiveInfinity) ContactsWall(body, h);
            for (int i = 0; i < _pillarCount; i++) ContactsPillar(body, _pillars[i], h);
            if (_trace is not null) TraceEnd(body, PhysicsSource.Wall, h);
        }
    }

    /// <summary>
    /// Контакт тела с неподвижным миром (жёсткий, α = 0): <paramref name="point"/> — точка на поверхности тела,
    /// <paramref name="n"/> — нормаль из мира к телу, <paramref name="pen"/> — глубина сейчас, <paramref name="pen0"/> — та же
    /// в прошлой позе. Решается <see cref="ContactsSolvedDepth"/>; λ &gt; 0 — контакт идёт в скоростной проход (материальная
    /// точка — до выталкивания) с трением скольжения <paramref name="mu"/> (0 — без трения). Возвращает λ.
    /// </summary>
    private float ContactsWorldPoint(int body, PhysicsSource source, Vector3 point, Vector3 n, float pen, float pen0, float mu, float h)
    {
        ref var s = ref _bodies[body];
        float solved = ContactsSolvedDepth(pen, pen0, _contactsPopSpeed, h);
        var local = s.ToLocal(point);
        float lambda = Xpbd.Contact(ref s, point, n, -solved, 0, h);
        if (lambda > 0) ContactsAddVelocity(body, local, -1, default, n, mu * lambda / h, source);
        if (_trace is not null) TraceContact(source, body, -1, point, n, pen, solved);
        return lambda;
    }

    /// <summary>
    /// Стена — вся капсула: самая дальняя от начала координат точка оси в XZ — один из концов, поэтому проверяются оба торца
    /// (у шара — один). Центр торца не выходит за <c>радиус стены − радиус тела</c>; нормаль — к центру арены.
    /// </summary>
    private void ContactsWall(int body, float h)
    {
        ref var s = ref _bodies[body];
        float limit = _wallRadius - s.Radius;
        float half = s.HalfLength;
        ContactsWallEnd(body, -half, limit, h);
        if (half > 0) ContactsWallEnd(body, half, limit, h);
    }

    private void ContactsWallEnd(int body, float along, float limit, float h)
    {
        ref var s = ref _bodies[body];
        var local = new Vector3(0, along, 0);
        var end = s.ToWorld(local);
        float rr = MathF.Sqrt(end.X * end.X + end.Z * end.Z);
        float pen = rr - limit;
        if (!(pen > 0) || rr < ContactsDirectionEpsilon) return;
        var n = new Vector3(-end.X / rr, 0, -end.Z / rr);
        var prev = s.PrevPosition + Vector3.Transform(local, s.PrevRotation);
        float pen0 = MathF.Sqrt(prev.X * prev.X + prev.Z * prev.Z) - limit;
        ContactsWorldPoint(body, PhysicsSource.Wall, end - n * s.Radius, n, pen, pen0, 0, h);
    }

    /// <summary>
    /// Колонна — вся капсула: ближайшая к оси колонны (в XZ) точка оси капсулы не входит в <c>радиус колонны + радиус тела</c>;
    /// нормаль — горизонтальная, от оси колонны. Старая глубина — ближайшая точка оси в прошлой позе. Ось капсулы на оси колонны —
    /// направление из прошлой позы, иначе +X (детерминированно).
    /// </summary>
    private void ContactsPillar(int body, Pillar pillar, float h)
    {
        ref var s = ref _bodies[body];
        float reach = pillar.Radius + s.Radius;
        float cx = s.Position.X - pillar.X, cz = s.Position.Z - pillar.Z, far = reach + s.HalfLength;
        if (!(cx * cx + cz * cz < far * far)) return;   // отсев по центру: ось капсулы не ближе reach
        var q = ContactsClosestToVertical(s.Position, s.Rotation, s.HalfLength, pillar.X, pillar.Z);
        float dx = q.X - pillar.X, dz = q.Z - pillar.Z;
        float l2 = dx * dx + dz * dz;
        if (!(l2 < reach * reach)) return;
        var q0 = ContactsClosestToVertical(s.PrevPosition, s.PrevRotation, s.HalfLength, pillar.X, pillar.Z);
        float dx0 = q0.X - pillar.X, dz0 = q0.Z - pillar.Z;
        float l = MathF.Sqrt(l2), l0 = MathF.Sqrt(dx0 * dx0 + dz0 * dz0);
        Vector3 n;
        if (l > ContactsDirectionEpsilon) n = new Vector3(dx / l, 0, dz / l);
        else if (l0 > ContactsDirectionEpsilon) n = new Vector3(dx0 / l0, 0, dz0 / l0);
        else n = Vector3.UnitX;
        ContactsWorldPoint(body, PhysicsSource.Wall, q - n * s.Radius, n, reach - l, reach - l0, 0, h);
    }

    /// <summary>Точка оси капсулы (центр, поворот, полудлина), ближайшая в XZ к вертикали (<paramref name="x"/>, <paramref name="z"/>); вертикальная ось — центр.</summary>
    private static Vector3 ContactsClosestToVertical(Vector3 center, Quaternion rotation, float halfLength, float x, float z)
    {
        var e = Vector3.Transform(Vector3.UnitY, rotation) * halfLength;
        var a = center - e;
        var d = e * 2;
        float dd = d.X * d.X + d.Z * d.Z;
        float t = dd > 1e-12f ? Math.Clamp(((x - a.X) * d.X + (z - a.Z) * d.Z) / dd, 0, 1) : 0.5f;
        return a + d * t;
    }

    // ——— Пары сочленений ———

    /// <summary>
    /// Пары сочленений <c>i &lt; j</c> по слотам: отсев по свежим границам (пересчитаны здесь — поза этого подшага), пары без
    /// тела на физике — пропуск до границ; внутри пары — тела по порядку, пары тел без тела на физике — пропуск до <c>SegSeg</c>.
    /// Своих тел сочленения не сталкивает.
    /// </summary>
    private void ContactsPairs(float h)
    {
        if (_contactsSimulatedArticulations == 0) return;
        ContactsUpdateBounds();
        for (int i = 0; i < _artCapacity; i++)
            if (_artGeneration[i] > 0) ContactsPairsOf(i, h);
    }

    private void ContactsPairsOf(int i, float h)
    {
        var bi = _contactsBounds[i];
        bool si = _contactsSimulated[i] > 0;
        for (int j = i + 1; j < _artCapacity; j++)
        {
            if (_artGeneration[j] <= 0 || !(si || _contactsSimulated[j] > 0)) continue;
            var bj = _contactsBounds[j];
            float reach = bi.Radius + bj.Radius;
            if (!(Vector3.DistanceSquared(bi.Center, bj.Center) <= reach * reach)) continue;   // NaN — тоже мимо
            ContactsPairChecks++;
            ContactsArticulationPair(i, j, h);
        }
    }

    private void ContactsArticulationPair(int i, int j, float h)
    {
        var mi = _artBodies[i]!;
        for (int x = 0, n = _artCount[i]; x < n; x++) ContactsBodyAgainst(mi[x], j, h);
    }

    private void ContactsBodyAgainst(int a, int j, float h)
    {
        bool sa = _bodies[a].Mode == BodyMode.Simulated;
        var mj = _artBodies[j]!;
        for (int y = 0, n = _artCount[j]; y < n; y++)
        {
            int b = mj[y];
            if (sa || _bodies[b].Mode == BodyMode.Simulated) ContactsBodyPair(a, b, h);
        }
    }

    /// <summary>
    /// Пара тел разных сочленений, хотя бы одно — на физике: ближайшие точки осей; пересеклись — тело на физике ↔ тело на
    /// физике: <see cref="Xpbd.Contact(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float, float)"/> с
    /// <see cref="PhysicsSettings.ContactCompliance"/> и старой глубиной пары в прошлых позах, в скоростной проход; кинематическое
    /// ↔ на физике — толчок 7д (<see cref="PushKinematic"/>, полная глубина, нормаль — от кинематического).
    /// </summary>
    private void ContactsBodyPair(int ia, int ib, float h)
    {
        ref var a = ref _bodies[ia];
        ref var b = ref _bodies[ib];
        float reach = a.HalfLength + a.Radius + b.HalfLength + b.Radius;
        if (!(Vector3.DistanceSquared(a.Position, b.Position) <= reach * reach)) return;
        ContactsNarrowChecks++;
        float d2 = ContactsClosest(a.Position, a.Rotation, a.HalfLength, b.Position, b.Rotation, b.HalfLength, out var c1, out var c2);
        float rs = a.Radius + b.Radius;
        if (!(d2 < rs * rs)) return;
        float d = MathF.Sqrt(d2);
        var n = d > ContactsDirectionEpsilon ? (c1 - c2) / d : ContactsFallbackNormal(in a, in b);
        float pen = rs - d;
        var pa = c1 - n * a.Radius;
        var pb = c2 + n * b.Radius;
        bool sa = a.Mode == BodyMode.Simulated, sb = b.Mode == BodyMode.Simulated;
        if (sa && sb)
        {
            ContactsSimulatedPair(ia, ib, pa, pb, n, pen, h);
            return;
        }
        // Толчок живого (7д) пишет трассу сам — вокруг вызова её нет.
        if (sa) PushKinematic(ib, ia, pb, pa, n, pen, h);
        else PushKinematic(ia, ib, pa, pb, -n, pen, h);
    }

    /// <summary>Два тела на физике, легшие друг в друга, расходятся не быстрее скорости выталкивания (сверх встречного движения).</summary>
    private void ContactsSimulatedPair(int ia, int ib, Vector3 pa, Vector3 pb, Vector3 n, float pen, float h)
    {
        ref var a = ref _bodies[ia];
        ref var b = ref _bodies[ib];
        float pen0 = ContactsPrevOverlap(in a, in b);
        float solved = ContactsSolvedDepth(pen, pen0, _contactsPopSpeed, h);
        Vector3 la = a.ToLocal(pa), lb = b.ToLocal(pb);   // материальные точки — до выталкивания
        if (_trace is not null)
        {
            TraceBegin(ia);
            TraceBegin(ib);
        }
        float lambda = Xpbd.Contact(ref a, pa, ref b, pb, n, -solved, _contactsCompliance, h);
        if (lambda > 0) ContactsAddVelocity(ia, la, ib, lb, n, 0, PhysicsSource.Pair);
        if (_trace is null) return;
        TraceContact(PhysicsSource.Pair, ia, ib, pa, n, pen, solved);
        TraceEnd(ia, PhysicsSource.Pair, h);
        TraceEnd(ib, PhysicsSource.Pair, h);
    }

    /// <summary>
    /// Ближайшие точки осей двух капсул (<see cref="Segments.SegSeg"/>): первым идёт более короткий отрезок — у
    /// <c>SegSeg</c> вырожденный второй отрезок даёт начало первого, а не ближайшую точку (шар против капсулы).
    /// </summary>
    private static float ContactsClosest(Vector3 pa, Quaternion qa, float ha, Vector3 pb, Quaternion qb, float hb, out Vector3 ca, out Vector3 cb)
    {
        var ea = Vector3.Transform(Vector3.UnitY, qa) * ha;
        var eb = Vector3.Transform(Vector3.UnitY, qb) * hb;
        if (hb < ha) return Segments.SegSeg(pb - eb, pb + eb, pa - ea, pa + ea, out cb, out ca);
        return Segments.SegSeg(pa - ea, pa + ea, pb - eb, pb + eb, out ca, out cb);
    }

    /// <summary>Глубина пары капсул в прошлых позах (начало подшага), м; нет пересечения — 0.</summary>
    private static float ContactsPrevOverlap(in BodyState a, in BodyState b)
    {
        float d2 = ContactsClosest(a.PrevPosition, a.PrevRotation, a.HalfLength, b.PrevPosition, b.PrevRotation, b.HalfLength, out _, out _);
        return MathF.Max(0, a.Radius + b.Radius - MathF.Sqrt(d2));
    }

    /// <summary>
    /// Нормаль пары (от b к a), когда оси пересекаются: перпендикуляр к обеим осям в сторону центра a, иначе — между центрами,
    /// иначе — вверх (детерминированно).
    /// </summary>
    private static Vector3 ContactsFallbackNormal(in BodyState a, in BodyState b)
    {
        var towards = a.Position - b.Position;
        var n = Vector3.Cross(a.Axis, b.Axis);
        float l = n.Length();
        if (l > ContactsDirectionEpsilon) return Vector3.Dot(n, towards) < 0 ? -n / l : n / l;
        l = towards.Length();
        return l > ContactsDirectionEpsilon ? towards / l : Vector3.UnitY;
    }

    // ——— Скоростной проход ———

    private void ContactsAddVelocity(int body, Vector3 localA, int other, Vector3 localB, Vector3 n, float sliding, PhysicsSource source)
    {
        if (_contactsVelocityCount == _contactsVelocity.Length)
            Array.Resize(ref _contactsVelocity, Math.Max(16, _contactsVelocity.Length * 2));   // разово, сверх запаса
        ref var c = ref _contactsVelocity[_contactsVelocityCount++];
        c.Body = body;
        c.Other = other;
        c.LocalA = localA;
        c.LocalB = localB;
        c.Normal = n;
        c.Sliding = sliding;
        c.Source = source;
    }

    /// <summary>
    /// После обновления скоростей подшага (идея — <c>SolveContactVelocities</c> SWORD, Müller и др. 2020 §3.6): у каждого контакта
    /// подшага с λ &gt; 0 нормальная относительная скорость точек гасится в ноль (отскока нет: e = 0 — и сближение, и
    /// расхождение), импульс — с плечом; у контакта с землёй затем — трение скольжения по Кулону: касательная скорость точки
    /// гасится импульсом не больше <c>μ · λn / h</c> (нормальный импульс контакта за подшаг × μ). Контакты по очереди, один проход
    /// (Гаусс — Зейдель). Позиционный контакт XPBD оставляет скоростью всё, чем вытолкнул (<c>сдвиг / h</c>) — без прохода
    /// выталкивание копилось бы скоростью. Буфер очищается.
    /// </summary>
    private void ContactsVelocityPass()
    {
        for (int i = 0; i < _contactsVelocityCount; i++) ContactsSolveVelocity(in _contactsVelocity[i]);
        _contactsVelocityCount = 0;
    }

    private void ContactsSolveVelocity(in ContactsVelocityEntry c)
    {
        ref var a = ref _bodies[c.Body];
        var pa = a.ToWorld(c.LocalA);
        var ra = pa - a.Position;
        var n = c.Normal;
        float vn;
        if (c.Other >= 0)
        {
            ref var b = ref _bodies[c.Other];
            var pb = b.ToWorld(c.LocalB);
            var rb = pb - b.Position;
            vn = Vector3.Dot(a.PointVelocity(pa) - b.PointVelocity(pb), n);
            if (!(MathF.Abs(vn) >= ContactsVelocityEpsilon)) return;
            float w = a.InvMassAt(ra, n) + b.InvMassAt(rb, n);
            if (!(w > 0)) return;
            var p = n * (-vn / w);
            Dynamics.ApplyImpulse(ref a, p, ra);
            Dynamics.ApplyImpulse(ref b, -p, rb);
        }
        else
        {
            vn = Vector3.Dot(a.PointVelocity(pa), n);
            float w = a.InvMassAt(ra, n);
            if (MathF.Abs(vn) >= ContactsVelocityEpsilon && w > 0) Dynamics.ApplyImpulse(ref a, n * (-vn / w), ra);
            else vn = 0;
            if (c.Sliding > 0) ContactsSliding(ref a, pa, ra, n, c.Sliding);
        }
        if (vn > 0 && _trace is not null) TraceVelocity(c.Source, c.Body, vn);   // погашена скорость расхождения (выталкивания)
    }

    /// <summary>
    /// Трение скольжения тела о мир в точке <paramref name="pa"/> (Müller и др. 2020 §3.6, в импульсах): касательная скорость
    /// точки гасится импульсом против неё, не больше <paramref name="limit"/>, Н·с, и не больше, чем нужно до нуля. Трение покоя
    /// позиционного прохода держит только почти неподвижную точку (сдвиг за подшаг ≲ μ·g·h²) — сорвавшееся тело без трения
    /// скольжения не остановилось бы.
    /// </summary>
    private static void ContactsSliding(ref BodyState a, Vector3 pa, Vector3 ra, Vector3 n, float limit)
    {
        var v = a.PointVelocity(pa);
        var vt = v - n * Vector3.Dot(v, n);
        float speed = vt.Length();
        if (!(speed >= ContactsVelocityEpsilon)) return;
        var t = vt / speed;
        float w = a.InvMassAt(ra, t);
        if (!(w > 0)) return;
        Dynamics.ApplyImpulse(ref a, t * -MathF.Min(limit, speed / w), ra);
    }

    // ——— Границы ———

    /// <summary>Границы всех живых сочленений по нынешней позе (отсев пар подшага; после последнего подшага — <see cref="Bounds"/>).</summary>
    private void ContactsUpdateBounds()
    {
        for (int a = 0; a < _artCapacity; a++)
            if (_artGeneration[a] > 0) _contactsBounds[a] = ContactsBoundsOf(a);
    }

    /// <summary>Сфера всех капсул сочленения (идея — <c>UpdateBounds</c> SWORD): центр — среднее центров тел, радиус — наибольшее <c>|центр тела − центр| + полудлина + радиус</c>.</summary>
    private ArticulationBounds ContactsBoundsOf(int a)
    {
        var members = _artBodies[a]!;
        int count = _artCount[a];
        var sum = Vector3.Zero;
        for (int k = 0; k < count; k++) sum += _bodies[members[k]].Position;
        var center = sum / count;
        float r = 0;
        for (int k = 0; k < count; k++)
        {
            ref var s = ref _bodies[members[k]];
            r = MathF.Max(r, Vector3.Distance(s.Position, center) + s.HalfLength + s.Radius);
        }
        return new ArticulationBounds(center, r);
    }
}
