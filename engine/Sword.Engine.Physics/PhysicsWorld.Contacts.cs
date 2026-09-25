using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Engine.Physics;

// Статический мир (земля, стена, колонны), контакты подшага — с миром, пары сочленений, скоростной проход — и границы
// сочленений. Земля по граням — Ground.cs; шаг — PhysicsWorld.Step.cs. Идеи — SolveContacts, PushOut, SolvedDepth, Collide,
// PrevOverlap, SolveContactVelocities, UpdateBounds SWORD 2eab13b (research-arena-tick §1.3); свои решения — docs/history/engine-physics.md.
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

    /// <summary>Пары сочленений, прошедшие отсев подшага (<c>i &lt; j</c> по слотам), — заранее выделенный список (растёт со слотами).</summary>
    private (int I, int J)[] _contactsArtPairs = [];
    private int _contactsArtPairCount;

    /// <summary>Тела сочленения у сферы чужого (отсев тел пары) — буфер на пару (растёт со слотами тел).</summary>
    private int[] _contactsNear = [];

    /// <summary>
    /// Щуп тела для пар подшага: центр, полуось капсулы в мире (ось · полудлина), полуразмер коробки по осям мира (|полуось| +
    /// радиус), радиус, на физике ли — плотно и без поворота на каждую пару. У тел на физике — с отсева подшага и после каждой поправки пары
    /// (толчка), у кинематических — с начала шага.
    /// </summary>
    private struct ContactsProbe
    {
        public Vector3 Center, Axis, Extent;
        public float Radius;

        /// <summary>Тело на физике неспящего сочленения (спящее тело для пар — не «на физике», а неподвижная опора — <see cref="Asleep"/>).</summary>
        public bool Simulated;

        /// <summary>Тело спящего сочленения: неспящему телу на физике — неподвижная опора, кинематическому — ничего.</summary>
        public bool Asleep;
    }

    /// <summary>Коробка сочленения по осям мира (центр, полуразмер) — отсев пар подшага.</summary>
    private readonly record struct ContactsBox(Vector3 Center, Vector3 Half);

    /// <summary>Щупы по слотам тел (<see cref="ContactsProbe"/>).</summary>
    private ContactsProbe[] _contactsProbe = [];

    /// <summary>Коробки по слотам сочленений: с телами на физике — отсева подшага, без них — начала шага.</summary>
    private ContactsBox[] _contactsBox = [];

    /// <summary>Контакты подшага для скоростного прохода (по порядку решения), заранее выделенные.</summary>
    private ContactsVelocityEntry[] _contactsVelocity = [];
    private int _contactsVelocityCount;

    /// <summary>Касания торца гранями земли — буфер для <see cref="IGround.Touches"/> (без stackalloc и без выделения).</summary>
    private readonly GroundTouch[] _groundTouches = new GroundTouch[IGround.MaxTouches];

    // Настройки контактов — читаются из _settings раз на шаг (настройки меняются только между шагами).
    private float _contactsPopSpeed, _contactsFriction, _contactsPairFriction, _contactsCompliance;

    /// <summary>Пар сочленений, прошедших отсев по свежим границам, за последний шаг (все подшаги) — тестам и диагностике.</summary>
    internal int ContactsPairChecks { get; private set; }

    /// <summary>Проверок коробки тела против коробки чужого сочленения (отсев тел пары) за последний шаг — тестам и диагностике.</summary>
    internal int ContactsBodyChecks { get; private set; }

    /// <summary>Контактов пар с трением покоя и сколько из них держало (сдвиг погашен) за последний шаг — тестам и диагностике.</summary>
    internal int ContactsPairFrictionChecks { get; private set; }

    internal int ContactsPairFrictionHeld { get; private set; }

    /// <summary>
    /// Тела, которых в подшаге касались пары (другое тело на физике, толчок живого), — для земли после пар: номер подшага пометки по
    /// слотам тел (без очистки массива) и список помеченных по порядку пометки.
    /// </summary>
    private int[] _contactsPressedStamp = [], _contactsPressed = [];
    private int _contactsPressedCount, _contactsSubstepStamp;

    /// <summary>
    /// Поза тела до первой поправки пары в подшаге (после мира) — по слотам тел, с номером подшага пометки: мир после пар решает
    /// только то, что сделали пары, — глубину и сдвиг по земле от этой позы (<see cref="ContactsPressedWorld"/>).
    /// </summary>
    private int[] _contactsPoseStamp = [];
    private Vector3[] _contactsPosePosition = [];
    private Quaternion[] _contactsPoseRotation = [];

    /// <summary>Тел, прижатых парами к земле, которых земля выталкивала второй раз (после пар), за последний шаг — тестам и диагностике.</summary>
    internal int ContactsPressedChecks { get; private set; }

    /// <summary>Пар тел, дошедших до ближайших точек осей (<c>SegSeg</c>), за последний шаг — тестам и диагностике.</summary>
    internal int ContactsNarrowChecks { get; private set; }

    /// <summary>
    /// Контакт подшага для скоростного прохода: точки — в системах тел (до выталкивания), нормаль — от второго к первому; второго
    /// нет (−1) — мир. <see cref="Sliding"/> — предел импульса трения скольжения, Н·с: <c>μ · λn / h</c> (0 — без трения);
    /// <see cref="Lambda"/> — нормальная λ контакта, кг·м (бюджет трения покоя пар).
    /// </summary>
    private struct ContactsVelocityEntry
    {
        public int Body, Other;
        public Vector3 LocalA, LocalB, Normal;
        public float Sliding, Lambda;
        public PhysicsSource Source;
    }

    /// <summary>Бюджет трения покоя пар подшага по слотам тел, кг·м: <c>μ · Σλn</c> пар тела; вне прохода трения — 0.</summary>
    private float[] _contactsBudget = [];

    public void SetGround(IGround? ground)
    {
        if (ReferenceEquals(_ground, ground)) return;
        _ground = ground;
        SleepWakeAll(WakeReason.Settings);   // опора спящих могла уйти
    }

    public void SetWall(float radius)
    {
        if (!(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "радиус стены — больше 0 (∞ — стены нет)");
        if (radius == _wallRadius) return;
        _wallRadius = radius;
        SleepWakeAll(WakeReason.Settings);
    }

    public void SetPillars(ReadOnlySpan<Pillar> pillars)
    {
        for (int i = 0; i < pillars.Length; i++)
        {
            var p = pillars[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Z) || !float.IsFinite(p.Radius) || !(p.Radius > 0))
                throw new ArgumentOutOfRangeException(nameof(pillars), p, $"колонна {i}: ось — конечная, радиус — конечный и больше 0");
        }
        if (pillars.SequenceEqual(_pillars.AsSpan(0, _pillarCount))) return;
        if (_pillars.Length < pillars.Length) _pillars = new Pillar[pillars.Length];
        pillars.CopyTo(_pillars);
        _pillarCount = pillars.Length;
        SleepWakeAll(WakeReason.Settings);
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
            Array.Resize(ref _contactsBox, articulations);
            _contactsArtPairs = new (int, int)[articulations * (articulations - 1) / 2];
        }
        if (_contactsNear.Length < bodies) _contactsNear = new int[bodies];
        if (_contactsPressed.Length < bodies)
        {
            Array.Resize(ref _contactsPressed, bodies);
            Array.Resize(ref _contactsPressedStamp, bodies);
            Array.Resize(ref _contactsPoseStamp, bodies);
            Array.Resize(ref _contactsPosePosition, bodies);
            Array.Resize(ref _contactsPoseRotation, bodies);
            Array.Resize(ref _groundNormalSum, bodies);
            Array.Resize(ref _groundFrictionSpent, bodies);
        }
        if (_contactsProbe.Length < bodies) Array.Resize(ref _contactsProbe, bodies);
        if (_contactsBudget.Length < bodies) Array.Resize(ref _contactsBudget, bodies);
        int velocity = bodies * ContactsPerBody;
        if (_contactsVelocity.Length < velocity) Array.Resize(ref _contactsVelocity, velocity);
        StepGrow(articulations, bodies);
        SleepGrow(articulations);
        SelfGrow(articulations);
    }

    partial void ContactsReset(int articulation)
    {
        _contactsBounds[articulation] = default;
        _contactsSimulated[articulation] = 0;
        StepReset(articulation);
        SleepReset(articulation);
        SelfReset(articulation);
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
        if (lambda > 0) ContactsAddVelocity(body, local, -1, default, n, mu * lambda / h, lambda, source);
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
        // Отсев по центру — торцы не дальше полудлины от центра: центр ближе <c>limit − полудлина</c> к оси арены — торцам не до стены
        float inner = limit - half;
        if (inner > 0 && s.Position.X * s.Position.X + s.Position.Z * s.Position.Z < inner * inner) return;
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
    /// Отсев сочленений подшага (по коробкам): щупы тел (<see cref="ContactsProbe"/>) и коробки сочленений с телами на физике — по
    /// позе этого подшага (у сочленений без тел на физике — на весь шаг, <see cref="ContactsKinematicBounds"/>); пары <c>i &lt; j</c> по
    /// слотам, чьи коробки пересеклись, — в список подшага; пары без тела на физике — мимо. Коробка (по осям мира) у лежащего трупа
    /// теснее сферы: в куче сфер пересекалась бо́льшая часть пар.
    /// </summary>
    private void ContactsBroadphase()
    {
        _contactsArtPairCount = 0;
        if (_contactsSimulatedArticulations == 0) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_contactsSimulated[a] > 0) ContactsRefresh(a);
        for (int i = 0; i < _artCapacity; i++)
            if (_artGeneration[i] > 0) ContactsPairsOf(i);
    }

    /// <summary>Щупы тел сочленения и его коробка — по нынешней позе.</summary>
    private void ContactsRefresh(int a)
    {
        var members = _artBodies[a]!;
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int k = 0, n = _artCount[a]; k < n; k++)
        {
            ref readonly var p = ref ContactsProbeOf(members[k]);
            min = Vector3.Min(min, p.Center - p.Extent);
            max = Vector3.Max(max, p.Center + p.Extent);
        }
        _contactsBox[a] = new ContactsBox((min + max) * 0.5f, (max - min) * 0.5f);
    }

    /// <summary>Щуп тела — по нынешней позе (<see cref="_contactsProbe"/>); вызывают отсев и каждая поправка пары (толчок).</summary>
    private ref ContactsProbe ContactsProbeOf(int body)
    {
        ref readonly var s = ref _bodies[body];
        ref var p = ref _contactsProbe[body];
        p.Center = s.Position;
        p.Axis = Vector3.Transform(Vector3.UnitY, s.Rotation) * s.HalfLength;
        p.Extent = Vector3.Abs(p.Axis) + new Vector3(s.Radius);
        p.Radius = s.Radius;
        bool asleep = _sleepAsleep[_bodyOwner[body]];
        p.Simulated = s.Mode == BodyMode.Simulated && !asleep;
        p.Asleep = asleep;
        return ref p;
    }

    /// <summary>
    /// Щупы и коробки сочленений без тел на физике — раз на шаг, в его начале (их тела шаг не двигает). Спящих — нет (сняты при засыпании,
    /// поза та же); остальных — нет, если их уже сняла проверка пробуждения этого шага.
    /// </summary>
    private void ContactsKinematicBounds()
    {
        if (_sleepProbed) return;
        for (int a = 0; a < _artCapacity; a++)
            if (_artGeneration[a] > 0 && _contactsSimulated[a] == 0 && !_sleepAsleep[a]) ContactsRefresh(a);
    }

    private void ContactsPairsOf(int i)
    {
        var bi = _contactsBox[i];
        bool si = _contactsSimulated[i] > 0;
        for (int j = i + 1; j < _artCapacity; j++)
        {
            if (_artGeneration[j] <= 0 || !(si || _contactsSimulated[j] > 0)) continue;
            var bj = _contactsBox[j];
            if (!ContactsOverlap(bi.Center, bi.Half, bj.Center, bj.Half)) continue;   // NaN — тоже мимо
            ContactsPairChecks++;
            _contactsArtPairs[_contactsArtPairCount++] = (i, j);
        }
    }

    /// <summary>Коробки (центр, полуразмер по осям мира) пересекаются; NaN — нет.</summary>
    private static bool ContactsOverlap(Vector3 ca, Vector3 ha, Vector3 cb, Vector3 hb)
    {
        var gap = Vector3.Abs(ca - cb) - ha - hb;
        return gap.X <= 0 && gap.Y <= 0 && gap.Z <= 0;
    }

    /// <summary>
    /// Пары сочленений из отсева подшага (<c>i &lt; j</c> по слотам): внутри пары — тела по порядку, пары тел без тела на физике — пропуск до
    /// <c>SegSeg</c>. Затем самоколлизия (пары своих тел сочленения из его таблицы, <see cref="SelfPairs"/>). После нормалей всех пар — трение
    /// покоя пар (и у самоколлизии — бюджет тела общий).
    /// </summary>
    private void ContactsPairs(float h)
    {
        _contactsSubstepStamp++;
        _contactsPressedCount = 0;
        int first = _contactsVelocityCount;
        for (int k = 0; k < _contactsArtPairCount; k++)
        {
            var (i, j) = _contactsArtPairs[k];
            ContactsArticulationPair(i, j, h);
        }
        _profiler.Begin(_zoneSelf);
        SelfPairs(h);
        _profiler.End(_zoneSelf);
        if (_contactsPairFriction > 0 && _contactsVelocityCount > first) ContactsPairFriction(first, h);
    }

    /// <summary>Пара сейчас поправит тело: первая в подшаге — запомнить его позу (после мира) для мира после пар.</summary>
    private void ContactsPoseBeforePair(int body)
    {
        if (_contactsPoseStamp[body] == _contactsSubstepStamp) return;
        _contactsPoseStamp[body] = _contactsSubstepStamp;
        ref readonly var s = ref _bodies[body];
        _contactsPosePosition[body] = s.Position;
        _contactsPoseRotation[body] = s.Rotation;
    }

    /// <summary>Тело на физике поправила пара (другое тело на физике, толчок живого) — в список прижатых подшага (раз на подшаг).</summary>
    private void ContactsPress(int body)
    {
        if (_contactsPressedStamp[body] == _contactsSubstepStamp) return;
        _contactsPressedStamp[body] = _contactsSubstepStamp;
        _contactsPressed[_contactsPressedCount++] = body;
    }

    /// <summary>
    /// Мир после пар — для тел, которых поправили пары (другое тело на физике, толчок живого): пара (тяжёлое тело на лёгком, клинок
    /// живого, бьющий вниз) вдавливает тело в землю после её прохода, и к концу подшага оно оставалось бы в земле — в следующем подшаге эта
    /// глубина уже «застарелая» и выходит не быстрее <see cref="PhysicsSettings.PopSpeed"/>, а пара вдавливает снова (клинок под кучей тонул до
    /// 7,8 см). И тело, не касавшееся земли в первом проходе: толчок мог загнать его в землю.
    /// <para>
    /// Второй проход решает <b>только сделанное парами</b>: старая поза для него — поза тела до первой пары подшага (после мира,
    /// <see cref="ContactsPoseBeforePair"/>), выход старой глубины — 0: вдавливание парами решается целиком, а что земля оставила после
    /// первого прохода (один проход Гаусса — Зейделя по двум торцам недотягивает ~20 % глубины), остаётся как было; трение о землю держит
    /// сдвиг, сделанный парами, бюджетом всей нагрузки тела (вместе с первым проходом, <see cref="GroundFriction"/>). Опора тела, лежащего на
    /// ней, поднимается только на то, на что его же нормаль её вдавила, — вдоль нормали пары, без касательного сдвига под лежащим. Решай второй
    /// проход всю глубину от начала подшага — он поднимал бы опору на недотянутое первым проходом уже после трения пар, и тело на наклонном
    /// касании сползало бы на <c>подъём · sin</c> за подшаг (замер: стержень на двух опорах, 18°, — 1 мм/с). Первый проход мира — до пар:
    /// мир только после пар оставлял опору осевшей под тяжестью на время пар — сползание на <c>g·h²·sin</c> за подшаг (6 мм/с).
    /// </para>
    /// </summary>
    private void ContactsPressedWorld(float h)
    {
        if (_contactsPressedCount == 0) return;
        float pop = _contactsPopSpeed;
        _contactsPopSpeed = 0;
        _groundPressedPass = true;
        for (int i = 0; i < _contactsPressedCount; i++) ContactsPressedBody(_contactsPressed[i], h);
        _groundPressedPass = false;
        _contactsPopSpeed = pop;
    }

    /// <summary>
    /// Прижатое тело — мир от позы до пар: прошлая поза подменяется на время прохода и возвращается. Касались земли оба торца — ещё раз
    /// нормали земли без трения: поправка второго торца поворачивает тело и вдавливает первый (качели: живой давит на конец клинка, земля
    /// поднимает этот конец — другой уходит в землю), а к следующему подшагу это застарелая глубина, выходящая не быстрее
    /// <see cref="PhysicsSettings.PopSpeed"/> (замер, толпа сценария «Бой»: клинок под стопой живого тонул до 4 см).
    /// </summary>
    private void ContactsPressedBody(int b, float h)
    {
        ContactsPressedChecks++;
        ref var s = ref _bodies[b];
        var (prevPosition, prevRotation) = (s.PrevPosition, s.PrevRotation);
        s.PrevPosition = _contactsPosePosition[b];
        s.PrevRotation = _contactsPoseRotation[b];
        ContactsWorldBody(b, h);
        if (_ground is not null && _groundContactCount > 1) GroundNormals(b, h);
        s.PrevPosition = prevPosition;
        s.PrevRotation = prevRotation;
    }

    /// <summary>
    /// Трение покоя пар тел на физике (по образцу земли) — после нормалей всех пар подшага: «всё или ничего»
    /// (<see cref="Xpbd.Friction(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float, float)"/>) по контактам
    /// подшага в материальных точках контакта, с <b>общим бюджетом тела</b> <c>μ · Σλn</c> его пар: контакт держит, если его λt не больше
    /// остатка бюджета обоих тел, и тратит его у обоих. Одно касание гасит сдвиг тела целиком, а его λn — лишь доля веса (тело на двух
    /// трупах): по своей λn оно держало бы до доли μ. Без трения тело, лежащее на другом трупе, съезжало с него под тяжестью.
    /// </summary>
    private void ContactsPairFriction(int first, float h)
    {
        var entries = _contactsVelocity;
        float mu = _contactsPairFriction;
        int end = _contactsVelocityCount;
        for (int i = first; i < end; i++)
        {
            ref readonly var e = ref entries[i];
            if (!ContactsPairEntry(in e)) continue;
            _contactsBudget[e.Body] += mu * e.Lambda;
            if (e.Other >= 0) _contactsBudget[e.Other] += mu * e.Lambda;
        }
        for (int i = first; i < end; i++)
        {
            ref readonly var e = ref entries[i];
            if (e.Other >= 0) ContactsPairStatic(in e, h);
            else if (e.Source == PhysicsSource.Pair) ContactsSleeperStatic(in e, h);
        }
        for (int i = first; i < end; i++)
        {
            ref readonly var e = ref entries[i];
            if (!ContactsPairEntry(in e)) continue;
            _contactsBudget[e.Body] = 0;
            if (e.Other >= 0) _contactsBudget[e.Other] = 0;
        }
    }

    /// <summary>Контакт подшага из прохода пар: пара тел на физике (и самоколлизия) или тело на спящем (мир с источником «пара»); земля и стена — нет.</summary>
    private static bool ContactsPairEntry(in ContactsVelocityEntry e) => e.Other >= 0 || e.Source == PhysicsSource.Pair;

    /// <summary>Трение покоя тела о спящее — как о землю: точка спящего неподвижна; бюджет — общий с парами тела.</summary>
    private void ContactsSleeperStatic(in ContactsVelocityEntry e, float h)
    {
        float budget = _contactsBudget[e.Body];
        if (!(budget > 0)) return;
        ref var a = ref _bodies[e.Body];
        if (_trace is not null) TraceBegin(e.Body);
        float lambdaT = Xpbd.Friction(ref a, a.ToWorld(e.LocalA), e.Normal, budget, 1, 0, h);
        ContactsPairFrictionChecks++;
        if (lambdaT > 0)
        {
            ContactsPairFrictionHeld++;
            _contactsBudget[e.Body] -= lambdaT;
        }
        if (_trace is not null) TraceEnd(e.Body, PhysicsSource.Pair, h);
    }

    private void ContactsPairStatic(in ContactsVelocityEntry e, float h)
    {
        float budget = MathF.Min(_contactsBudget[e.Body], _contactsBudget[e.Other]);
        if (!(budget > 0)) return;
        ref var a = ref _bodies[e.Body];
        ref var b = ref _bodies[e.Other];
        if (_trace is not null)
        {
            TraceBegin(e.Body);
            TraceBegin(e.Other);
        }
        float lambdaT = Xpbd.Friction(ref a, a.ToWorld(e.LocalA), ref b, b.ToWorld(e.LocalB), e.Normal, budget, 1, 0, h);
        ContactsPairFrictionChecks++;
        if (lambdaT > 0)
        {
            ContactsPairFrictionHeld++;
            _contactsBudget[e.Body] -= lambdaT;
            _contactsBudget[e.Other] -= lambdaT;
        }
        if (_trace is null) return;
        TraceEnd(e.Body, PhysicsSource.Pair, h);
        TraceEnd(e.Other, PhysicsSource.Pair, h);
    }

    /// <summary>
    /// Пара сочленений (отсев тел по коробкам): тело, чья коробка не задевает коробки чужого сочленения, не касается ни одного его
    /// тела (все его капсулы — внутри неё), — поэтому сначала тела <paramref name="j"/> у коробки <paramref name="i"/> (список по порядку),
    /// затем тела <paramref name="i"/> у коробки <paramref name="j"/> против этого списка. Порядок пар тел — прежний.
    /// </summary>
    private void ContactsArticulationPair(int i, int j, float h)
    {
        var near = _contactsNear;
        int m = ContactsNearOf(j, _contactsBox[i], near);
        if (m == 0) return;
        var bj = _contactsBox[j];
        var mi = _artBodies[i]!;
        for (int x = 0, n = _artCount[i]; x < n; x++)
        {
            int a = mi[x];
            if (ContactsNear(a, in bj)) ContactsBodyAgainst(a, near, m, h);
        }
    }

    /// <summary>Тела сочленения <paramref name="j"/>, чьи коробки задевают коробку <paramref name="b"/>, — по порядку в <paramref name="near"/>; сколько.</summary>
    private int ContactsNearOf(int j, in ContactsBox b, int[] near)
    {
        var mj = _artBodies[j]!;
        int m = 0;
        for (int y = 0, n = _artCount[j]; y < n; y++)
            if (ContactsNear(mj[y], in b)) near[m++] = mj[y];
        return m;
    }

    /// <summary>Коробка тела (щуп) задевает коробку <paramref name="b"/>.</summary>
    private bool ContactsNear(int body, in ContactsBox b)
    {
        ContactsBodyChecks++;
        ref readonly var p = ref _contactsProbe[body];
        return ContactsOverlap(p.Center, p.Extent, b.Center, b.Half);
    }

    private void ContactsBodyAgainst(int a, int[] near, int m, float h)
    {
        ref readonly var pa = ref _contactsProbe[a];
        bool sa = pa.Simulated;
        for (int y = 0; y < m; y++)
        {
            int b = near[y];
            ref readonly var pb = ref _contactsProbe[b];
            if ((sa || pb.Simulated) && ContactsOverlap(pa.Center, pa.Extent, pb.Center, pb.Extent)) ContactsBodyPair(a, b, h);
        }
    }

    /// <summary>
    /// Пара тел разных сочленений, хотя бы одно — на физике, чьи коробки пересеклись (отсев по коробкам капсул — щупам подшага
    /// <see cref="_contactsProbe"/>, они же дают отрезки осей без поворота на пару): ближайшие точки осей; пересеклись — тело на физике ↔
    /// тело на физике: <see cref="Xpbd.Contact(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float, float)"/> с
    /// <see cref="PhysicsSettings.ContactCompliance"/> и старой глубиной пары в прошлых позах, в скоростной проход; кинематическое
    /// ↔ на физике — толчок (<see cref="PushKinematic"/>, полная глубина, нормаль — от кинематического).
    /// </summary>
    private void ContactsBodyPair(int ia, int ib, float h)
    {
        ref readonly var qa = ref _contactsProbe[ia];
        ref readonly var qb = ref _contactsProbe[ib];
        ContactsNarrowChecks++;
        float rs = qa.Radius + qb.Radius;
        float d2 = ContactsClosest(qa.Center, qa.Axis, _bodies[ia].HalfLength, qb.Center, qb.Axis, _bodies[ib].HalfLength, out var c1, out var c2);
        if (!(d2 < rs * rs)) return;
        ref var a = ref _bodies[ia];
        ref var b = ref _bodies[ib];
        float d = MathF.Sqrt(d2);
        var n = d > ContactsDirectionEpsilon ? (c1 - c2) / d : ContactsFallbackNormal(in a, in b);
        float pen = rs - d;
        var pa = c1 - n * a.Radius;
        var pb = c2 + n * b.Radius;
        bool sa = qa.Simulated, sb = qb.Simulated;
        if (sa && sb)
        {
            ContactsSimulatedPair(ia, ib, pa, pb, n, pen, h, PhysicsSource.Pair, 1);
            return;
        }
        // Спящее тело — неподвижная опора неспящему телу на физике (кинематическое ↔ спящее сюда не доходит: отсев)
        if (qa.Asleep)
        {
            ContactsSleepingPair(ib, ia, pb, -n, pen, h);
            return;
        }
        if (qb.Asleep)
        {
            ContactsSleepingPair(ia, ib, pa, n, pen, h);
            return;
        }
        // Толчок живого пишет трассу сам — вокруг вызова её нет.
        if (sa) PushKinematic(ib, ia, pb, pa, n, pen, h);
        else PushKinematic(ia, ib, pa, pb, -n, pen, h);
    }

    /// <summary>
    /// Два тела на физике, легшие друг в друга, расходятся не быстрее скорости выталкивания (сверх встречного движения). Источник — пара
    /// сочленений (<see cref="PhysicsSource.Pair"/>, радиусы как есть) или самоколлизия (<see cref="PhysicsSource.Self"/>, радиусы ×
    /// <paramref name="scale"/> — и в старой глубине).
    /// </summary>
    private void ContactsSimulatedPair(int ia, int ib, Vector3 pa, Vector3 pb, Vector3 n, float pen, float h, PhysicsSource source, float scale)
    {
        ref var a = ref _bodies[ia];
        ref var b = ref _bodies[ib];
        float pen0 = ContactsPrevOverlap(in a, in b, scale);
        float solved = ContactsSolvedDepth(pen, pen0, _contactsPopSpeed, h);
        Vector3 la = a.ToLocal(pa), lb = b.ToLocal(pb);   // материальные точки — до выталкивания
        ContactsPoseBeforePair(ia);
        ContactsPoseBeforePair(ib);
        if (_trace is not null)
        {
            TraceBegin(ia);
            TraceBegin(ib);
        }
        float lambda = Xpbd.Contact(ref a, pa, ref b, pb, n, -solved, _contactsCompliance, h);
        if (lambda > 0)
        {
            ContactsAddVelocity(ia, la, ib, lb, n, _contactsPairFriction * lambda / h, lambda, source);
            ContactsProbeOf(ia);   // поправка сдвинула и повернула тела — щупы пар дальше по подшагу — нынешние
            ContactsProbeOf(ib);
            ContactsPress(ia);
            ContactsPress(ib);
        }
        if (_trace is null) return;
        TraceContact(source, ia, ib, pa, n, pen, solved);
        TraceEnd(ia, source, h);
        TraceEnd(ib, source, h);
    }

    /// <summary>
    /// Тело на физике <paramref name="body"/> касается тела спящего сочленения <paramref name="sleeper"/>: спящее — неподвижная опора, как
    /// земля (<see cref="Xpbd.Contact(ref BodyState, Vector3, Vector3, float, float, float, float)"/> с миром и
    /// <see cref="PhysicsSettings.ContactCompliance"/>; старая глубина — пары в прошлых позах, у спящего прошлая поза — та же; застарелая выходит
    /// не быстрее <see cref="PhysicsSettings.PopSpeed"/>), в скоростной проход — с трением скольжения пар, трение покоя — с бюджетом тела вместе с
    /// парами (<see cref="ContactsPairFriction"/>). Касание спящее не будит: его будит проверка до подшагов следующего шага, если тело у него
    /// движется быстрее порога сна или вошло глубже <see cref="SleepSlop"/>. Честно: лежащее на спящем медленнее порога сна не сдвинуло бы и
    /// неспящее (нагрузка — та же, под которой оно уснуло), а быстрое его будит. <paramref name="point"/> — точка на теле, <paramref name="n"/> —
    /// нормаль от спящего к телу.
    /// </summary>
    private void ContactsSleepingPair(int body, int sleeper, Vector3 point, Vector3 n, float pen, float h)
    {
        ref var s = ref _bodies[body];
        float pen0 = ContactsPrevOverlap(in s, in _bodies[sleeper]);
        float solved = ContactsSolvedDepth(pen, pen0, _contactsPopSpeed, h);
        var local = s.ToLocal(point);   // материальная точка — до выталкивания
        ContactsPoseBeforePair(body);
        if (_trace is not null) TraceBegin(body);
        float lambda = Xpbd.Contact(ref s, point, n, -solved, _contactsCompliance, h);
        SleepContacts++;
        if (lambda > 0)
        {
            ContactsAddVelocity(body, local, -1, default, n, _contactsPairFriction * lambda / h, lambda, PhysicsSource.Pair);
            ContactsProbeOf(body);
            ContactsPress(body);
        }
        if (_trace is not { } trace) return;
        // источник — «пара», но не «в контакте с трупом» (Paired): спящее энергии не меняет, рост тела — как от земли
        trace.RecordContact(PhysicsSource.Pair, TraceBody(body), TraceBody(sleeper), point, n, pen, solved);
        trace.SleepContact();
        TraceEnd(body, PhysicsSource.Pair, h);
    }

    /// <summary>
    /// Ближайшие точки осей двух капсул (<see cref="Segments.SegSeg"/>): первым идёт более короткий отрезок — у
    /// <c>SegSeg</c> вырожденный второй отрезок даёт начало первого, а не ближайшую точку (шар против капсулы).
    /// </summary>
    private static float ContactsClosest(Vector3 pa, Quaternion qa, float ha, Vector3 pb, Quaternion qb, float hb, out Vector3 ca, out Vector3 cb) =>
        ContactsClosest(pa, Vector3.Transform(Vector3.UnitY, qa) * ha, ha, pb, Vector3.Transform(Vector3.UnitY, qb) * hb, hb, out ca, out cb);

    /// <summary>То же по полуосям в мире <paramref name="ea"/>, <paramref name="eb"/> (ось · полудлина).</summary>
    private static float ContactsClosest(Vector3 pa, Vector3 ea, float ha, Vector3 pb, Vector3 eb, float hb, out Vector3 ca, out Vector3 cb)
    {
        if (hb < ha) return Segments.SegSeg(pb - eb, pb + eb, pa - ea, pa + ea, out cb, out ca);
        return Segments.SegSeg(pa - ea, pa + ea, pb - eb, pb + eb, out ca, out cb);
    }

    /// <summary>Глубина пары капсул в прошлых позах (начало подшага), м (радиусы × <paramref name="scale"/> — самоколлизия); нет пересечения — 0.</summary>
    private static float ContactsPrevOverlap(in BodyState a, in BodyState b, float scale = 1)
    {
        float d2 = ContactsClosest(a.PrevPosition, a.PrevRotation, a.HalfLength, b.PrevPosition, b.PrevRotation, b.HalfLength, out _, out _);
        return MathF.Max(0, (a.Radius + b.Radius) * scale - MathF.Sqrt(d2));
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

    private void ContactsAddVelocity(int body, Vector3 localA, int other, Vector3 localB, Vector3 n, float sliding, float lambda, PhysicsSource source)
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
        c.Lambda = lambda;
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

    /// <summary>
    /// Контакт скоростного прохода: обратные инерции тел — матрицы подшага (<see cref="_stepInertia"/>: поза после обновления скоростей
    /// не меняется). Пара тел на физике — после нормали трение скольжения по Кулону, как у земли: относительная касательная скорость
    /// точек гасится импульсом не больше <c>μ · λn / h</c>.
    /// </summary>
    private void ContactsSolveVelocity(in ContactsVelocityEntry c)
    {
        ref var a = ref _bodies[c.Body];
        ref readonly var wa = ref _stepInertia[c.Body];
        var pa = a.ToWorld(c.LocalA);
        var ra = pa - a.Position;
        var n = c.Normal;
        float vn;
        if (c.Other >= 0)
        {
            ref var b = ref _bodies[c.Other];
            ref readonly var wb = ref _stepInertia[c.Other];
            var pb = b.ToWorld(c.LocalB);
            var rb = pb - b.Position;
            vn = Vector3.Dot(a.PointVelocity(pa) - b.PointVelocity(pb), n);
            float w = wa.At(ra, n) + wb.At(rb, n);
            if (MathF.Abs(vn) >= ContactsVelocityEpsilon && w > 0)
            {
                var p = n * (-vn / w);
                wa.Impulse(ref a, p, ra);
                wb.Impulse(ref b, -p, rb);
            }
            else vn = 0;
            if (c.Sliding > 0) ContactsPairSliding(ref a, in wa, pa, ra, ref b, in wb, pb, rb, n, c.Sliding);
        }
        else
        {
            vn = Vector3.Dot(a.PointVelocity(pa), n);
            float w = wa.At(ra, n);
            if (MathF.Abs(vn) >= ContactsVelocityEpsilon && w > 0) wa.Impulse(ref a, n * (-vn / w), ra);
            else vn = 0;
            if (c.Sliding > 0) ContactsSliding(ref a, in wa, pa, ra, n, c.Sliding);
        }
        if (vn > 0 && _trace is not null) TraceVelocity(c.Source, c.Body, vn);   // погашена скорость расхождения (выталкивания)
    }

    /// <summary>
    /// Трение скольжения тела о мир в точке <paramref name="pa"/> (Müller и др. 2020 §3.6, в импульсах): касательная скорость
    /// точки гасится импульсом против неё, не больше <paramref name="limit"/>, Н·с, и не больше, чем нужно до нуля. Трение покоя
    /// позиционного прохода держит только почти неподвижную точку (сдвиг за подшаг ≲ μ·g·h²) — сорвавшееся тело без трения
    /// скольжения не остановилось бы.
    /// </summary>
    private static void ContactsSliding(ref BodyState a, in InvInertiaMatrix wa, Vector3 pa, Vector3 ra, Vector3 n, float limit)
    {
        var v = a.PointVelocity(pa);
        var vt = v - n * Vector3.Dot(v, n);
        float speed = vt.Length();
        if (!(speed >= ContactsVelocityEpsilon)) return;
        var t = vt / speed;
        float w = wa.At(ra, t);
        if (!(w > 0)) return;
        wa.Impulse(ref a, t * -MathF.Min(limit, speed / w), ra);
    }

    /// <summary>
    /// Трение скольжения пары тел на физике: относительная касательная скорость точек контакта гасится импульсом против неё (тело
    /// a — минус, b — плюс), не больше <paramref name="limit"/>, Н·с, и не больше, чем нужно до нуля; импульс пары сохраняется.
    /// </summary>
    private static void ContactsPairSliding(ref BodyState a, in InvInertiaMatrix wa, Vector3 pa, Vector3 ra, ref BodyState b, in InvInertiaMatrix wb,
        Vector3 pb, Vector3 rb, Vector3 n, float limit)
    {
        var v = a.PointVelocity(pa) - b.PointVelocity(pb);
        var vt = v - n * Vector3.Dot(v, n);
        float speed = vt.Length();
        if (!(speed >= ContactsVelocityEpsilon)) return;
        var t = vt / speed;
        float w = wa.At(ra, t) + wb.At(rb, t);
        if (!(w > 0)) return;
        var p = t * -MathF.Min(limit, speed / w);
        wa.Impulse(ref a, p, ra);
        wb.Impulse(ref b, -p, rb);
    }

    // ——— Границы ———

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
