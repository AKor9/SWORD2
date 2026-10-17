using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>Точка следа потёка — на коже, в привязке: треугольник и барицентрики.</summary>
public readonly record struct DripPoint(int Triangle, Vector3 Bary);

/// <summary>
/// Штамп следа за шаг (для крови на коже, К3): боец, точка на коже (треугольник, барицентрики — веса трём вершинам и их копиям на шве,
/// <see cref="SkinSurface.WeldCopies"/>), объём, оставленный на этом отрезке следа, мл, тик шага.
/// </summary>
public readonly record struct DripStamp(ulong Fighter, int Triangle, Vector3 Bary, float Volume, ulong Tick);

/// <summary>Заявка «капля»: оторвалась от кожи бойца — мир, скорость, м/с, объём, мл, тик шага (полёт и пол — К6).</summary>
public readonly record struct BloodDrop(ulong Fighter, ulong Tick, Vector3 Position, Vector3 Velocity, float Volume);

/// <summary>
/// Потёки крови по коже (WR §4.3, П4): частица на треугольнике сваренной сетки (<see cref="SkinSurface"/>) — барицентрики и скорость
/// относительно кожи (скорости барицентрик: переживает любую позу), объём, след — ломаная точек в привязке. Шаг 60 Гц по <c>TickId</c> по
/// позе тика: три вершины треугольника скинируются суставами <see cref="IFighterPoses"/>; ускорение g − a_кожи (a — по положению точки на
/// двух прошлых шагах) в плоскость треугольника; вязкое гашение e^(−k·dt); при слабом касательном ускорении потёк стоит; через ребро — к
/// соседу по сварке, а если тянет назад — вдоль ребра. Объём тратится на след. С поверхности, смотрящей вниз, при объёме выше порога или
/// рывке — отрыв: заявка <see cref="BloodDrop"/>. Пределы — на бойца и на мир, вытесняется самый старый (по порядку рождения). Пулы — при
/// создании: шаг без выделений.
/// </summary>
public sealed class BloodDrips
{
    /// <summary>Точек следа на потёк (кольцо: старые уходят).</summary>
    public const int TrailCap = 32;

    /// <summary>Свежих отрывов для оверлея.</summary>
    public const int RecentDrops = 64;

    private static readonly Vector3 Gravity = new(0, -9.81f, 0);

    private struct Drip
    {
        public bool Alive;
        public ulong Fighter;
        public int Wound;
        public ulong Born;
        public int Triangle;
        public Vector3 Bary;
        public float R1, R2;          // скорость относительно кожи: d(бари 1, 2)/dt
        public float Volume;
        public int Age;
        public Vector3 LastPos, LastSurfVel;
        public int History;
        public float SinceTrail;
        public int TrailCount;
        public bool Standing;
        public int Slide;
        public bool Pinned;           // упёрся в ямку ложбины: стоит до проверки             // угол, против которого потёк скользит по ребру (ложбина), −1 — нет
    }

    private readonly SkinSurface _surface;
    private readonly BloodTuning _tuning;
    private readonly Drip[] _drips;
    private readonly DripPoint[] _trail;
    private readonly int _perFighter;
    private DripStamp[] _stamps = new DripStamp[1024];
    private BloodDrop[] _drops = new BloodDrop[128];
    private readonly Vector3[] _recent = new Vector3[RecentDrops];
    private int[] _rowPose = new int[64];
    private ulong[] _rowKey = new ulong[64];
    private ulong _born;
    private float _damp, _cosDown;

    public BloodDrips(SkinSurface surface, BloodTuning tuning, int perFighter = 16, int world = 512)
    {
        _surface = surface;
        _tuning = tuning;
        _perFighter = perFighter;
        _drips = new Drip[world];
        _trail = new DripPoint[world * TrailCap];
    }

    public int Capacity => _drips.Length;

    public int PerFighter => _perFighter;

    public int Alive { get; private set; }

    public long Spawned { get; private set; }

    /// <summary>Кончились: объём ушёл в след, срок жизни, снятие бойца.</summary>
    public long Ended { get; private set; }

    /// <summary>Вытеснены пределами.</summary>
    public long Evicted { get; private set; }

    /// <summary>Оторвались каплей.</summary>
    public long Detached { get; private set; }

    public double DetachedVolume { get; private set; }

    /// <summary>Отрывов, которые ещё никто не забрал (очередь К6 — пока только счёт).</summary>
    public long QueuedDrops { get; private set; }

    public long Steps { get; private set; }

    /// <summary>Переходов через ребро к соседу и скольжений вдоль ребра — с начала.</summary>
    public long Crossings { get; private set; }
    public long Slides { get; private set; }

    /// <summary>Штампов следа последнего шага (К3 кладёт их в кровь по вершинам).</summary>
    public ReadOnlySpan<DripStamp> Stamps => _stamps.AsSpan(0, _stampCount);

    /// <summary>Отрывы последнего шага (К6 — полёт и пол).</summary>
    public ReadOnlySpan<BloodDrop> Drops => _drops.AsSpan(0, _dropCount);

    /// <summary>Точки последних отрывов (мир) для оверлея, кольцо.</summary>
    public ReadOnlySpan<Vector3> RecentDropPoints => _recent.AsSpan(0, (int)Math.Min(Detached, RecentDrops));

    private int _stampCount, _dropCount;

    /// <summary>Суммарный объём живых потёков, мл.</summary>
    public float AliveVolume
    {
        get
        {
            float v = 0;
            for (int i = 0; i < _drips.Length; i++) if (_drips[i].Alive) v += _drips[i].Volume;
            return v;
        }
    }

    public bool IsAlive(int i) => _drips[i].Alive;

    public ulong FighterOf(int i) => _drips[i].Fighter;

    /// <summary>Головка потёка <paramref name="i"/>.</summary>
    public DripPoint Head(int i) => new(_drips[i].Triangle, _drips[i].Bary);

    public float Volume(int i) => _drips[i].Volume;

    /// <summary>Рана, из которой потёк (номер в кольце бойца; −1 — не из раны).</summary>
    public int WoundOf(int i) => _drips[i].Wound;

    /// <summary>Скорость потёка относительно кожи в мире, м/с, по вершинам его треугольника в мире.</summary>
    public Vector3 Velocity(int i, Vector3 w0, Vector3 w1, Vector3 w2) => _drips[i].R1 * (w1 - w0) + _drips[i].R2 * (w2 - w0);

    /// <summary>След потёка (по порядку, от старой точки к головке; не больше <see cref="TrailCap"/>) — в <paramref name="into"/>.</summary>
    public int Trail(int i, Span<DripPoint> into)
    {
        ref readonly var d = ref _drips[i];
        int n = Math.Min(d.TrailCount, TrailCap);
        int first = d.TrailCount - n;
        for (int k = 0; k < n && k < into.Length; k++) into[k] = _trail[i * TrailCap + (first + k) % TrailCap];
        return Math.Min(n, into.Length);
    }

    /// <summary>Отрывы забраны (К6): очередь пуста.</summary>
    public void TakeDrops() => QueuedDrops = 0;

    /// <summary>Снять все потёки бойца (постройка заново, снятие).</summary>
    public void Clear(ulong fighter)
    {
        for (int i = 0; i < _drips.Length; i++)
            if (_drips[i].Alive && _drips[i].Fighter == fighter) Kill(i, ended: true);
    }

    public void ClearAll()
    {
        for (int i = 0; i < _drips.Length; i++) if (_drips[i].Alive) Kill(i, ended: true);
        _stampCount = _dropCount = 0;
        QueuedDrops = 0;
    }

    /// <summary>
    /// Выброс крови при ударе: 2–8 потёков вдоль линии раны (число и доли — из хеша зерна раны), объём ∝ урону
    /// (<see cref="BloodTuning.VolumePerDamage"/>).
    /// </summary>
    public int SpawnWound(in BloodWound wound, int woundIndex)
    {
        float volume = wound.Damage * _tuning.VolumePerDamage;
        if (!(volume > 0)) return 0;
        int count = Math.Clamp((int)(2 + wound.Damage / 10 + 2 * BloodHash.Unit(wound.Seed, 0)), 2, 8);
        float half = Math.Clamp(0.01f + wound.Damage * 0.0006f, 0.01f, 0.05f);
        int spawned = 0;
        for (int k = 0; k < count; k++)
        {
            float s = ((k + 0.25f + 0.5f * BloodHash.Unit(wound.Seed, (uint)(1 + 2 * k))) / count * 2 - 1) * half;
            float share = volume / count * (0.75f + 0.5f * BloodHash.Unit(wound.Seed, (uint)(2 + 2 * k)));
            if (SpawnAt(wound, woundIndex, wound.BindPoint + wound.BindLine * s, share, wound.Tick) >= 0) spawned++;
        }
        return spawned;
    }

    /// <summary>
    /// Подпитка раны (сочение, К4): новый потёк объёма <paramref name="volume"/>, мл, у раны — на её линии, место из хеша (зерно раны,
    /// <paramref name="salt"/>); вернёт номер потёка, −1 — не встал на кожу.
    /// </summary>
    public int Feed(in BloodWound wound, int woundIndex, float volume, ulong tick, uint salt)
    {
        if (!(volume > 0)) return -1;
        float half = Math.Clamp(0.01f + wound.Damage * 0.0006f, 0.01f, 0.05f);
        float s = (2 * BloodHash.Unit(BloodHash.Combine(wound.Seed, salt), 7) - 1) * half;
        return SpawnAt(wound, woundIndex, wound.BindPoint + wound.BindLine * s, volume, tick);
    }

    private int SpawnAt(in BloodWound wound, int woundIndex, Vector3 bind, float volume, ulong tick)
    {
        int triangle = _surface.Nearest(wound.Segment, bind, out var bary, out _);
        if (triangle < 0) return -1;
        return Spawn(wound.Fighter, woundIndex, triangle, bary, volume);
    }

    /// <summary>Новый потёк на коже бойца: треугольник, барицентрики, объём, мл. Пределы — вытеснением самого старого.</summary>
    public int Spawn(ulong fighter, int wound, int triangle, Vector3 bary, float volume)
    {
        int mine = 0, oldestMine = -1, oldest = -1, free = -1;
        for (int i = 0; i < _drips.Length; i++)
        {
            ref readonly var d = ref _drips[i];
            if (!d.Alive)
            {
                if (free < 0) free = i;
                continue;
            }
            if (oldest < 0 || d.Born < _drips[oldest].Born) oldest = i;
            if (d.Fighter != fighter) continue;
            mine++;
            if (oldestMine < 0 || d.Born < _drips[oldestMine].Born) oldestMine = i;
        }
        int slot;
        if (mine >= _perFighter) slot = oldestMine;
        else if (free >= 0) slot = free;
        else slot = oldest;
        if (_drips[slot].Alive)
        {
            Kill(slot, ended: false);
            Evicted++;
        }
        _drips[slot] = new Drip
        {
            Alive = true, Fighter = fighter, Wound = wound, Born = ++_born, Triangle = triangle, Bary = bary, Volume = volume, TrailCount = 1, Slide = -1,
        };
        _trail[slot * TrailCap] = new DripPoint(triangle, bary);
        Alive++;
        Spawned++;
        return slot;
    }

    private void Kill(int i, bool ended)
    {
        _drips[i].Alive = false;
        Alive--;
        if (ended) Ended++;
    }

    /// <summary>
    /// Шаг потёков по позам тика <paramref name="tick"/>: бойцы без позы стоят. Штампы и отрывы шага — в <see cref="Stamps"/> и
    /// <see cref="Drops"/> (прошлые сбрасываются).
    /// </summary>
    public void Step(ulong tick, float dt, IFighterPoses poses)
    {
        _stampCount = _dropCount = 0;
        Steps++;
        if (Alive == 0) return;
        _damp = MathF.Exp(-_tuning.Viscosity * dt);
        _cosDown = MathF.Cos(_tuning.DetachAngle * (MathF.PI / 180));
        int rows = 0, row = -1;
        ulong last = ulong.MaxValue;
        for (int i = 0; i < _drips.Length; i++)
        {
            if (!_drips[i].Alive) continue;
            ulong fighter = _drips[i].Fighter;
            if (fighter != last || row < 0) (row, last) = (RowOf(fighter, ref rows, poses), fighter);   // потёки бойца обычно подряд
            int pose = _rowPose[row];
            if (pose < 0) continue;
            StepOne(i, poses.Joints(pose), dt, tick);
        }
    }

    /// <summary>Номер позы бойца — раз на шаг (бойцов немного: линейный поиск по найденным в этом шаге).</summary>
    private int RowOf(ulong fighter, ref int rows, IFighterPoses poses)
    {
        for (int r = 0; r < rows; r++) if (_rowKey[r] == fighter) return r;
        if (rows == _rowKey.Length)
        {
            Array.Resize(ref _rowKey, rows * 2);
            Array.Resize(ref _rowPose, rows * 2);
        }
        _rowKey[rows] = fighter;
        _rowPose[rows] = poses.Find(fighter) is var p && p >= 0 && !poses.Joints(p).IsEmpty ? p : -1;
        return rows++;
    }

    private void StepOne(int i, ReadOnlySpan<Matrix4x4> joints, float dt, ulong tick)
    {
        ref var d = ref _drips[i];
        var s = _surface;
        int t = d.Triangle;
        s.SkinTriangle(t, joints, out var w0, out var w1, out var w2);
        var bary = d.Bary;
        var p = bary.X * w0 + bary.Y * w1 + bary.Z * w2;
        var surfVel = d.History >= 1 ? (p - d.LastPos) / dt : Vector3.Zero;
        var accel = d.History >= 2 ? (surfVel - d.LastSurfVel) / dt : Vector3.Zero;
        float accel2 = accel.LengthSquared();
        float jerkAccel = _tuning.JerkAccel, maxAccel = _tuning.MaxAccel;
        bool jerk = accel2 > jerkAccel * jerkAccel;
        if (accel2 > maxAccel * maxAccel) accel *= maxAccel / MathF.Sqrt(accel2);
        var n = Normal(t, w0, w1, w2);
        if (n == Vector3.Zero)
        {
            Remember(ref d, p, surfVel);
            return;
        }
        var effective = Gravity - accel;
        var tangent = effective - n * Vector3.Dot(n, effective);
        var v = d.R1 * (w1 - w0) + d.R2 * (w2 - w0);
        v -= n * Vector3.Dot(n, v);
        float stick = _tuning.StickAccel;
        d.Standing = tangent.LengthSquared() < stick * stick;
        v = d.Standing ? Vector3.Zero : (v + tangent * dt) * _damp;

        float cosDown = _cosDown;
        if (-n.Y > cosDown && (d.Volume > _tuning.DetachVolume || jerk && d.Volume >= _tuning.MinDropVolume))
        {
            Drop(d.Fighter, tick, p, surfVel + v, d.Volume);
            Stamp(d.Fighter, t, bary, 0, tick);
            Kill(i, ended: false);
            return;
        }

        float travelled = 0;
        // Скольжение по ложбине прошлого шага держится без проверки соседа, застрявший в ямке ложбины стоит; каждый 4-й шаг — проверка заново.
        bool recheck = d.Age % 4 == 0;
        int slide = recheck ? -1 : d.Slide;
        if (d.Pinned && !recheck) v = Vector3.Zero;
        else if (v != Vector3.Zero)
        {
            float want = v.Length() * dt;
            travelled = Move(ref t, ref bary, ref w0, ref w1, ref w2, ref n, v * dt, ref v, effective, joints, ref slide);
            d.Pinned = travelled < 0.05f * want;
            if (d.Pinned) v = Vector3.Zero;
        }
        d.Slide = slide;
        d.Triangle = t;
        d.Bary = bary;
        var e1 = w1 - w0;
        var e2 = w2 - w0;
        Solve(e1, e2, v, out d.R1, out d.R2);
        Remember(ref d, bary.X * w0 + bary.Y * w1 + bary.Z * w2, surfVel);

        d.SinceTrail += travelled;
        d.Volume -= _tuning.TrailCost * travelled;
        bool empty = d.Volume <= 0;
        bool old = ++d.Age > _tuning.MaxLife / dt;
        if (d.SinceTrail >= _tuning.TrailStep || empty || old)
        {
            _trail[i * TrailCap + d.TrailCount % TrailCap] = new DripPoint(t, bary);
            d.TrailCount++;
            float deposit = _tuning.TrailCost * d.SinceTrail + (empty || old ? Math.Max(d.Volume, 0) : 0);
            Stamp(d.Fighter, t, bary, deposit, tick);
            d.SinceTrail = 0;
        }
        if (empty || old) Kill(i, ended: true);
    }

    private static void Remember(ref Drip d, Vector3 position, Vector3 surfVel)
    {
        d.LastPos = position;
        d.LastSurfVel = surfVel;
        if (d.History < 2) d.History++;
    }

    /// <summary>Наружная нормаль грани в мире; вырожденная — ноль.</summary>
    private Vector3 Normal(int t, Vector3 w0, Vector3 w1, Vector3 w2)
    {
        var c = Vector3.Cross(w1 - w0, w2 - w0);
        float l = c.Length();
        return l > 1e-12f ? c * (_surface.FaceSign(t) / l) : Vector3.Zero;
    }

    /// <summary>Вектор в плоскости треугольника через его рёбра: v = x·e1 + y·e2 (наименьшие квадраты).</summary>
    private static bool Solve(Vector3 e1, Vector3 e2, Vector3 v, out float x, out float y)
    {
        float g11 = Vector3.Dot(e1, e1), g12 = Vector3.Dot(e1, e2), g22 = Vector3.Dot(e2, e2);
        float det = g11 * g22 - g12 * g12;
        if (!(det > 1e-20f))
        {
            x = y = 0;
            return false;
        }
        float r1 = Vector3.Dot(e1, v), r2 = Vector3.Dot(e2, v);
        x = (g22 * r1 - g12 * r2) / det;
        y = (g11 * r2 - g12 * r1) / det;
        return true;
    }

    /// <summary>
    /// Сдвиг по коже на <paramref name="move"/> (мир, в плоскости треугольника): через рёбра — к соседу по сварке (сдвиг и скорость
    /// поворачиваются в его плоскость), если сосед есть и касательное ускорение в нём не тянет назад; иначе — вдоль ребра. Вернёт пройденное, м.
    /// </summary>
    private float Move(ref int t, ref Vector3 bary, ref Vector3 w0, ref Vector3 w1, ref Vector3 w2, ref Vector3 n, Vector3 move, ref Vector3 v,
        Vector3 effective, ReadOnlySpan<Matrix4x4> joints, ref int slide)
    {
        var s = _surface;
        float travelled = 0;
        // slide — угол, против которого скользим по ребру (его барицентрика держится 0); уходит, если сдвиг ведёт внутрь треугольника.
        if (slide >= 0 && Get(bary, slide) > 1e-6f) slide = -1;
        for (int iter = 0; iter < 8 && move.LengthSquared() > 1e-14f; iter++)
        {
            if (!Solve(w1 - w0, w2 - w0, move, out float x, out float y)) break;
            var delta = new Vector3(-x - y, x, y);
            if (slide >= 0 && Get(delta, slide) > 0) slide = -1;
            if (slide >= 0)
            {
                int a = (slide + 1) % 3, b = (slide + 2) % 3;
                float half = (Get(delta, a) - Get(delta, b)) / 2;
                delta = Set(Set(Set(delta, slide, 0), a, half), b, -half);
            }
            var target = bary + delta;
            if (target.X >= 0 && target.Y >= 0 && target.Z >= 0)
            {
                travelled += move.Length();
                bary = target;
                break;
            }
            float tmin = 1;
            int k = -1;
            for (int c = 0; c < 3; c++)
            {
                float dc = Get(delta, c);
                if (Get(target, c) < 0 && dc < 0)
                {
                    float tc = Math.Max(Get(bary, c), 0) / -dc;
                    if (tc < tmin) (tmin, k) = (tc, c);
                }
            }
            if (k < 0) break;
            bary = Clean(Set(bary + delta * tmin, k, 0));
            travelled += move.Length() * tmin;
            move *= 1 - tmin;
            int e = (k + 1) % 3, f = (k + 2) % 3;   // ребро против угла k: углы e → f
            var we = Corner(e, w0, w1, w2);
            var wf = Corner(f, w0, w1, w2);
            int nb = s.Neighbor(t, e);
            if (nb >= 0)
            {
                int wa = s.WeldVertex(t, e), wb = s.WeldVertex(t, f), ia = -1, ib = -1;
                for (int c = 0; c < 3; c++)
                {
                    int wc = s.WeldVertex(nb, c);
                    if (wc == wa) ia = c;
                    else if (wc == wb) ib = c;
                }
                if (ia >= 0 && ib >= 0)
                {
                    // Общие углы — уже скинированные концы ребра (шов без щели); скинируется только третий угол соседа.
                    int ic = 3 - ia - ib;
                    var third = s.Skin(s.Vertex(nb, ic), joints);
                    var q0 = ia == 0 ? we : ib == 0 ? wf : third;
                    var q1 = ia == 1 ? we : ib == 1 ? wf : third;
                    var q2 = ia == 2 ? we : ib == 2 ? wf : third;
                    var nn = Normal(nb, q0, q1, q2);
                    var qa = Corner(ia, q0, q1, q2);
                    var edge = Corner(ib, q0, q1, q2) - qa;
                    var inward = Corner(ic, q0, q1, q2) - qa;
                    float el = edge.LengthSquared();
                    if (el > 0) inward -= edge * (Vector3.Dot(inward, edge) / el);
                    var pull = effective - nn * Vector3.Dot(nn, effective);
                    bool back = nn == Vector3.Zero || Vector3.Dot(pull, inward) < 0 && pull.Length() >= _tuning.StickAccel;
                    if (!back)
                    {
                        float sum = Get(bary, e) + Get(bary, f);
                        float u = sum > 0 ? Get(bary, f) / sum : 0.5f;
                        t = nb;
                        bary = Set(Set(Set(default, ia, 1 - u), ib, u), ic, 0);
                        (w0, w1, w2) = (q0, q1, q2);
                        move = Turn(move, nn);
                        v = Turn(v, nn);
                        n = nn;
                        slide = -1;
                        Crossings++;
                        continue;
                    }
                }
            }
            // Края нет или тянет назад — вдоль ребра.
            var along = wf - we;
            float al = along.Length();
            if (al < 1e-9f) break;
            along /= al;
            float m = Vector3.Dot(along, move);
            if (MathF.Abs(m) < 1e-9f || slide == k) break;   // уже скользили по этому ребру и упёрлись
            move = along * m;
            v = along * Vector3.Dot(along, v);
            slide = k;
            Slides++;
        }
        return travelled;
    }

    /// <summary>Вектор в плоскость с нормалью <paramref name="n"/> с прежней длиной.</summary>
    private static Vector3 Turn(Vector3 v, Vector3 n)
    {
        float l = v.Length();
        var p = v - n * Vector3.Dot(n, v);
        float pl = p.Length();
        return pl > 1e-12f ? p * (l / pl) : Vector3.Zero;
    }

    private static Vector3 Corner(int c, Vector3 a, Vector3 b, Vector3 d) => c == 0 ? a : c == 1 ? b : d;

    private static float Get(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;

    private static Vector3 Set(Vector3 v, int c, float value)
    {
        if (c == 0) v.X = value;
        else if (c == 1) v.Y = value;
        else v.Z = value;
        return v;
    }

    /// <summary>Барицентрики без отрицательных и с суммой 1.</summary>
    private static Vector3 Clean(Vector3 b)
    {
        b = Vector3.Max(b, Vector3.Zero);
        float sum = b.X + b.Y + b.Z;
        return sum > 0 ? b / sum : new Vector3(1, 0, 0);
    }

    private void Stamp(ulong fighter, int triangle, Vector3 bary, float volume, ulong tick)
    {
        if (_stampCount == _stamps.Length) Array.Resize(ref _stamps, _stamps.Length * 2);
        _stamps[_stampCount++] = new DripStamp(fighter, triangle, bary, volume, tick);
    }

    private void Drop(ulong fighter, ulong tick, Vector3 position, Vector3 velocity, float volume)
    {
        if (_dropCount == _drops.Length) Array.Resize(ref _drops, _drops.Length * 2);
        _drops[_dropCount++] = new BloodDrop(fighter, tick, position, velocity, volume);
        _recent[Detached % RecentDrops] = position;
        Detached++;
        DetachedVolume += volume;
        QueuedDrops++;
    }
}
