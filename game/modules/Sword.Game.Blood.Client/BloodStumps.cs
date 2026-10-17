using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>Сторона среза: культя (у тела бойца) или отделённый кусок.</summary>
public enum StumpSide : byte
{
    Stump = 0,
    Piece = 1,
}

/// <summary>
/// Источник крови на срезе: боец, сегмент и вид отделения, сторона, тело, к которому привязан (культя — тело сегмента в позах тика,
/// у отрыва — тело родителя; кусок — номер куска в <see cref="IPiecePoses"/>, у отрыва — тело самого сегмента), центр крышки и нормаль
/// наружу в системе этого тела, радиус среза; последняя точка и нормаль в мире (шаг), состояние струи.
/// </summary>
public struct StumpSource
{
    public bool Alive;
    public ulong Fighter;
    public int Segment;
    public SeverMode Mode;
    public StumpSide Side;

    /// <summary>Культя — номер тела в <see cref="IFighterPoses.Segments"/>; кусок — номер куска (у отрыва — −1: тело сегмента в позах).</summary>
    public int Body;
    public int Piece;
    public Vector3 LocalPoint, LocalNormal;
    public float Radius;
    public ulong Born;
    public uint Seed;

    /// <summary>Доля объёмов по виду отделения.</summary>
    public float Share;

    /// <summary>Огибающая струи (1 при отрубе), время от отрубa по шагам, с, шагов.</summary>
    public float Envelope, Time;
    public int Steps;

    /// <summary>Накоплено к выпуску (струя, капание, потёки по коже), мл; выпущено всего, мл.</summary>
    public float Jet, Drip, Run;
    public double Out;

    /// <summary>Выпуск последнего шага (струя и капание), мл.</summary>
    public float Released;

    /// <summary>Точка и нормаль крышки в мире на последнем шаге (точка — для скорости тела).</summary>
    public Vector3 Point, Normal;
    public bool Posed;

    /// <summary>Вершины кожи у края среза этой стороны (в пуле <see cref="BloodStumps"/>) и треугольники для потёков вниз.</summary>
    public int RimCount, RunCount;
}

/// <summary>
/// Кровь из обрубков (по событиям отделения <see cref="LimbDetached"/>, сервис <see cref="ISeverFeed"/>): на каждой стороне среза —
/// источник на крышке, привязанный к телу своей стороны (культя — тело сегмента, кусок — тело куска <see cref="IPiecePoses"/>), каждый шаг
/// 60 Гц — по позам тика. Культя: артериальная струя по нормали среза с пульсом (<see cref="Pulse"/>), огибающая e^(−t/τ) (у трупа τ
/// короче), капли нескольких размеров — струя и мелкие брызги; потом капание с нижнего края крышки. Кусок: короткий выплеск и капание.
/// Кожа у края среза в крови (вершины основы в полосе у плоскости), потёки вниз по культе (<see cref="BloodDrips.Spawn"/>). Капли —
/// липкие (<see cref="BloodFloor.DetachSticky"/>): к телам и на пол. ГСЧ — хеш (боец, тик отделения, сторона, сегмент; шаг; капля).
/// Пулы — при создании, буфер капель растёт удвоением.
/// </summary>
public sealed class BloodStumps
{
    /// <summary>Источников одновременно не больше (новый вытесняет самый старый).</summary>
    public const int Capacity = 32;

    /// <summary>Вершин края на источник, треугольников потёков на источник не больше.</summary>
    public const int MaxRim = 768, MaxRuns = 48;

    /// <summary>Выпуск по шагам меньше этого, мл, копится до следующего шага.</summary>
    public const float MinRelease = 0.02f;

    /// <summary>Капля из струи — у крышки на столько наружу, м.</summary>
    public const float Lift = 0.005f;

    /// <summary>Скорость тела, добавляемая каплям, не больше, м/с (рывок позы).</summary>
    public const float MaxCarry = 8;

    /// <summary>Огибающая ниже этой — струи нет; источник живёт не дольше, с.</summary>
    public const float JetEnd = 0.004f, MaxLife = 40;

    private readonly SkinSurface? _surface;
    private readonly StumpSource[] _sources = new StumpSource[Capacity];
    private readonly int[] _rim = new int[Capacity * MaxRim];
    private readonly float[] _rimWeight = new float[Capacity * MaxRim];
    private readonly int[] _runs = new int[Capacity * MaxRuns];
    private readonly int[] _mark = [];
    private readonly int[] _candidates = [];
    private readonly float[] _distance = [];
    private int _generation;
    private BloodDrop[] _drops = new BloodDrop[64];
    private int _dropCount;

    public BloodStumps(SkinSurface? surface)
    {
        _surface = surface;
        if (surface is null) return;
        _mark = new int[surface.WeldCount];
        _distance = new float[surface.WeldCount];
        _candidates = new int[surface.TriangleCount];
    }

    /// <summary>Капли последнего шага (мир, скорость, объём, тик) — в полёт.</summary>
    public ReadOnlySpan<BloodDrop> Drops => _drops.AsSpan(0, _dropCount);

    /// <summary>Источник <paramref name="i"/> (0 … <see cref="Capacity"/> − 1).</summary>
    public ref readonly StumpSource Source(int i) => ref _sources[i];

    /// <summary>Живых источников; родилось с начала прогона.</summary>
    public int Alive { get; private set; }
    public long Born { get; private set; }

    /// <summary>Выпущено с начала прогона, мл: каплями в полёт, на кожу у края, потёками по культе; капель.</summary>
    public double DropsMl { get; private set; }
    public double RimMl { get; private set; }
    public double RunMl { get; private set; }
    public long DropCount { get; private set; }

    /// <summary>Всего выпущено, мл.</summary>
    public double OutMl => DropsMl + RimMl + RunMl;

    /// <summary>Пульс струи: 0.25 + 3·max(0, sin 2πft)² — среднее за период 1, систола — пик 3.25, диастола — 0.25.</summary>
    public static float Pulse(float t, float hz)
    {
        float s = MathF.Max(0, MathF.Sin(2 * MathF.PI * hz * t));
        return 0.25f + 3 * s * s;
    }

    /// <summary>Доля систолы 0…1 (для скорости струи).</summary>
    private static float Systole(float t, float hz)
    {
        float s = MathF.Max(0, MathF.Sin(2 * MathF.PI * hz * t));
        return s * s;
    }

    /// <summary>Доля объёмов по виду отделения.</summary>
    public static float ShareOf(SeverMode mode, BloodTuning t) => mode switch
    {
        SeverMode.Cut => 1,
        SeverMode.Fracture => t.FractureShare,
        SeverMode.Tear => t.TearShare,
        _ => t.ShatterShare,
    };

    public void Clear(ulong fighter)
    {
        for (int i = 0; i < Capacity; i++)
            if (_sources[i].Alive && _sources[i].Fighter == fighter) Kill(i);
    }

    public void ClearAll()
    {
        for (int i = 0; i < Capacity; i++)
            if (_sources[i].Alive) Kill(i);
        _dropCount = 0;
    }

    private void Kill(int i)
    {
        _sources[i].Alive = false;
        Alive--;
    }

    /// <summary>
    /// Прогрев один раз в первом тике: путь отруба и шага (поиск края по коже сегмента, струя, капание) собирается JIT здесь, а не в тике
    /// первого отруба (было ≈ 25 мс в нём). Источник — по предплечью первого бойца позы, без кожи и потёков; после — пусто, счёт — нули.
    /// </summary>
    public void Warm(IFighterPoses poses, BloodTuning t)
    {
        if (poses.Count == 0 || poses.Segments(0).Length < BodySegs.Bodies) return;
        var e = new LimbDetached
        {
            Segment = BodySegs.FArmL, Mode = SeverMode.Cut, Piece = -1, Normal = Vector3.UnitY, StumpShift = -0.05f, StumpHalf = 0.05f, StumpRadius = 0.04f,
            PieceCenter = 0.08f, PieceRadius = 0.04f, PieceHalf = 0.04f,
        };
        Sever(poses.Fighter(0).Fighter, e, 0, poses, null, null, 0, t);
        for (int k = 1; k <= 3; k++) Step((ulong)k, 1f / 60, poses, null, null, null, null, 0, t);
        ClearAll();
        Born = DropCount = 0;
        DropsMl = RimMl = RunMl = 0;
    }

    /// <summary>
    /// Отделение тика <paramref name="tick"/>: источники на крышках обеих сторон (по позам тика — культя по телу сегмента после разреза,
    /// кусок по своему телу), кровь на кожу у края среза (поза тика — меш до разреза). Прежняя культя того же сегмента гаснет (её крышка
    /// ушла с новым куском).
    /// </summary>
    public void Sever(ulong fighter, in LimbDetached e, ulong tick, IFighterPoses poses, IPiecePoses? pieces, BloodSkin? skin, float now, BloodTuning t)
    {
        if (!BodySegs.IsFlesh(e.Segment)) return;
        int index = poses.Find(fighter);
        if (index < 0) return;
        var segments = poses.Segments(index);
        if (segments.Length < BodySegs.Bodies) return;
        for (int i = 0; i < Capacity; i++)
            if (_sources[i].Alive && _sources[i].Fighter == fighter && _sources[i].Side == StumpSide.Stump && _sources[i].Segment == e.Segment) Kill(i);
        float share = ShareOf(e.Mode, t);
        var seg = segments[e.Segment];
        if (e.Mode is SeverMode.Cut or SeverMode.Fracture)
        {
            // Плоскость — в системе кости сегмента (тело культи сдвинуто по оси на StumpShift), кусок — центр PieceCenter по той же оси.
            var n = Vector3.Normalize(e.Normal);
            float y0 = MathF.Abs(n.Y) > 1e-4f ? e.Offset / n.Y : 0;
            float radius = MathF.Max(MathF.Min(e.StumpRadius, e.PieceRadius > 0 ? e.PieceRadius : e.StumpRadius), 0.01f);
            int stump = Add(fighter, e, tick, StumpSide.Stump, e.Segment, -1, new Vector3(0, y0 - e.StumpShift, 0), n, radius, share);
            int piece = -1;
            if (pieces is not null && FindPiece(pieces.Pieces(fighter), e.Piece, out _))
                piece = Add(fighter, e, tick, StumpSide.Piece, -1, e.Piece, new Vector3(0, y0 - e.PieceCenter, 0), -n, radius, share);
            var bone = seg.Position - Vector3.TransformNormal(Vector3.UnitY, seg.World) * e.StumpShift;
            var cap = bone + Vector3.TransformNormal(new Vector3(0, y0, 0), seg.World);
            Rims(fighter, e.Segment, cap, Vector3.Normalize(Vector3.TransformNormal(n, seg.World)), poses.Joints(index), stump, piece, skin, now, t);
        }
        else if (e.Mode == SeverMode.Tear)
        {
            // Отрыв по суставу: культя — на теле родителя у проксимального конца сегмента, кусок — сам сегмент (−Y).
            int parent = BodySegs.Parent(e.Segment);
            if (parent < 0 || !Matrix4x4.Invert(segments[parent].World, out var inverse)) return;
            var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, seg.World));
            var joint = seg.Position - axis * seg.HalfLength;
            float radius = MathF.Max(seg.Radius, 0.01f);
            int stump = Add(fighter, e, tick, StumpSide.Stump, parent, -1, Vector3.Transform(joint, inverse),
                Vector3.Normalize(Vector3.TransformNormal(axis, inverse)), radius, share);
            int piece = Add(fighter, e, tick, StumpSide.Piece, e.Segment, -1, new Vector3(0, -seg.HalfLength, 0), -Vector3.UnitY, radius, share);
            Rims(fighter, e.Segment, joint, axis, poses.Joints(index), stump, piece, skin, now, t);
        }
        else
        {
            // Клочья: культя — дистальный конец укороченного тела сегмента; клочья мелкие — без своих источников.
            var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, seg.World));
            int stump = Add(fighter, e, tick, StumpSide.Stump, e.Segment, -1, new Vector3(0, seg.HalfLength, 0), Vector3.UnitY,
                MathF.Max(seg.Radius, 0.01f), share);
            Rims(fighter, e.Segment, seg.Position + axis * seg.HalfLength, axis, poses.Joints(index), stump, -1, skin, now, t);
        }
    }

    private static bool FindPiece(ReadOnlySpan<PieceBody> pieces, int piece, out PieceBody body)
    {
        for (int i = 0; i < pieces.Length; i++)
            if (pieces[i].Piece == piece)
            {
                body = pieces[i];
                return true;
            }
        body = default;
        return false;
    }

    private int Add(ulong fighter, in LimbDetached e, ulong tick, StumpSide side, int body, int piece, Vector3 local, Vector3 normal, float radius,
        float share)
    {
        int slot = -1;
        for (int i = 0; i < Capacity && slot < 0; i++)
            if (!_sources[i].Alive) slot = i;
        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < Capacity; i++)
                if (_sources[i].Born < _sources[slot].Born) slot = i;
            Kill(slot);
        }
        _sources[slot] = new StumpSource
        {
            Alive = true, Fighter = fighter, Segment = e.Segment, Mode = e.Mode, Side = side, Body = body, Piece = piece, LocalPoint = local,
            LocalNormal = normal, Radius = radius, Born = tick, Seed = BloodHash.Seed(fighter, tick, 0x5700u + (uint)side * 16 + (uint)e.Segment),
            Share = share, Envelope = 1,
        };
        Alive++;
        Born++;
        return slot;
    }

    /// <summary>
    /// Вершины кожи сегмента у плоскости среза (поза тика — меш до разреза): сторона культи и куска, вес (1 − |d|/B)²; на каждую сторону —
    /// <see cref="BloodTuning.StumpRimMl"/> сразу. Треугольники культи в полосе — для потёков вниз (равномерно по списку).
    /// </summary>
    private void Rims(ulong fighter, int segment, Vector3 cap, Vector3 normal, ReadOnlySpan<Matrix4x4> joints, int stump, int piece, BloodSkin? skin,
        float now, BloodTuning t)
    {
        if (_surface is not { } s || joints.IsEmpty) return;
        float band = MathF.Max(t.StumpRimBand, 0.005f);
        var triangles = s.SegmentTriangles(segment);
        int generation = ++_generation;
        int candidates = 0;
        for (int k = 0; k < triangles.Length; k++) RimTriangle(s, triangles[k], cap, normal, band, joints, stump, piece, generation, ref candidates);
        if (stump >= 0)
        {
            ref var src = ref _sources[stump];
            int take = Math.Min(candidates, MaxRuns);
            for (int i = 0; i < take; i++) _runs[stump * MaxRuns + i] = _candidates[(int)((long)i * candidates / take)];
            src.RunCount = take;
        }
        if (skin is null) return;
        if (stump >= 0) DepositRim(stump, skin, t.StumpRimMl * _sources[stump].Share, now);
        if (piece >= 0) DepositRim(piece, skin, t.StumpRimMl * _sources[piece].Share, now);
    }

    private void RimTriangle(SkinSurface s, int triangle, Vector3 cap, Vector3 normal, float band, ReadOnlySpan<Matrix4x4> joints, int stump,
        int piece, int generation, ref int candidates)
    {
        bool run = true;
        for (int c = 0; c < 3; c++)
        {
            int weld = s.WeldVertex(triangle, c);
            float d;
            if (_mark[weld] == generation) d = _distance[weld];
            else
            {
                // вершина — раз: скин и расстояние до плоскости запоминаются для соседних треугольников
                _mark[weld] = generation;
                d = _distance[weld] = Vector3.Dot(normal, s.Skin(s.Vertex(triangle, c), joints) - cap);
                float f = 1 - MathF.Abs(d) / band;
                if (f > 0)
                {
                    if (stump >= 0 && d <= 0.01f) AddRim(stump, weld, f * f);
                    if (piece >= 0 && d >= -0.01f) AddRim(piece, weld, f * f);
                }
            }
            if (!(d < -0.005f && d > -band)) run = false;
        }
        if (run && stump >= 0) _candidates[candidates++] = triangle;
    }

    private void AddRim(int source, int weld, float weight)
    {
        ref var src = ref _sources[source];
        if (src.RimCount == MaxRim) return;
        _rim[source * MaxRim + src.RimCount] = weld;
        _rimWeight[source * MaxRim + src.RimCount] = weight;
        src.RimCount++;
    }

    /// <summary>Объём на вершины края источника по весам (толщина ∝ весу: объём ∝ вес · площадь вершины).</summary>
    private double DepositRim(int source, BloodSkin skin, float volume, float now)
    {
        ref readonly var src = ref _sources[source];
        if (!(volume > 0) || src.RimCount == 0) return 0;
        int slot = skin.Acquire(src.Fighter);
        if (slot < 0) return 0;
        float sum = 0;
        for (int i = 0; i < src.RimCount; i++) sum += _rimWeight[source * MaxRim + i] * skin.Area(_rim[source * MaxRim + i]);
        if (!(sum > 0)) return 0;
        for (int i = 0; i < src.RimCount; i++)
        {
            int w = _rim[source * MaxRim + i];
            skin.Deposit(slot, w, volume * _rimWeight[source * MaxRim + i] * skin.Area(w) / sum, now);
        }
        RimMl += volume;
        return volume;
    }

    /// <summary>
    /// Шаг 60 Гц на тике <paramref name="tick"/> (позы тика): у каждого источника — место крышки в мире, выпуск шага (струя с пульсом и
    /// огибающей, капание), капли — в <see cref="Drops"/>, доля — на кожу у края и потёками по культе. Боец без позы (или кусок без тела)
    /// шаг пропускает, время идёт.
    /// </summary>
    public void Step(ulong tick, float stepDt, IFighterPoses poses, IPiecePoses? pieces, Func<ulong, bool>? dead, BloodSkin? skin, BloodDrips? drips,
        float now, BloodTuning t)
    {
        _dropCount = 0;
        if (Alive == 0) return;
        for (int i = 0; i < Capacity; i++)
            if (_sources[i].Alive) StepOne(i, tick, stepDt, poses, pieces, dead, skin, drips, now, t);
    }

    private void StepOne(int i, ulong tick, float dt, IFighterPoses poses, IPiecePoses? pieces, Func<ulong, bool>? dead, BloodSkin? skin,
        BloodDrips? drips, float now, BloodTuning t)
    {
        ref var src = ref _sources[i];
        bool stump = src.Side == StumpSide.Stump;
        float tau = MathF.Max(stump ? t.StumpSeconds : t.PieceSeconds, 0.01f);
        bool corpse = dead?.Invoke(src.Fighter) ?? false;
        float tauNow = stump && corpse ? tau * Math.Clamp(t.StumpCorpseShare, 0.05f, 1) : tau;
        float t0 = src.Time, mid = t0 + 0.5f * dt, t1 = t0 + dt;
        float total = (stump ? t.StumpMl : t.PieceMl) * src.Share;
        float jet = src.Envelope > JetEnd ? total / tau * src.Envelope * (stump ? Pulse(mid, t.StumpPulseHz) : 1) * dt : 0;
        float dripTau = MathF.Max(t.StumpDripSeconds, 0.1f);
        float drip = (stump ? t.StumpDripMl : t.PieceDripMl) * src.Share * (MathF.Exp(-t0 / dripTau) - MathF.Exp(-t1 / dripTau));
        float envelope = src.Envelope;
        src.Released = jet + drip;
        src.Envelope *= MathF.Exp(-dt / tauNow);
        src.Time = t1;
        int step = src.Steps++;
        if (src.Time > MaxLife || src.Envelope <= JetEnd && src.Time > 5 * dripTau)
        {
            Kill(i);
            return;
        }
        if (!Locate(ref src, poses, pieces, out var point, out var normal))
        {
            src.Posed = false;
            return;
        }
        var carry = src.Posed ? (point - src.Point) / dt : Vector3.Zero;
        if (carry.LengthSquared() > MaxCarry * MaxCarry) carry *= MaxCarry / carry.Length();
        src.Point = point;
        src.Normal = normal;
        src.Posed = true;

        float rim = jet * t.StumpRimShare, run = stump ? jet * t.StumpRunShare : 0;
        src.Jet += jet - rim - run;
        src.Drip += drip;
        src.Run += run;
        double put = rim > 0 && skin is not null ? DepositRim(i, skin, rim, now) : 0;
        if (put > 0) src.Out += put;
        else src.Jet += rim;   // кожи у края нет — в струю
        uint seed = BloodHash.Combine(src.Seed, (uint)step);
        if (src.Run >= t.StumpRunMl && src.RunCount > 0 && drips is not null && Spawn(i, drips, seed))
        {
            RunMl += src.Run;
            src.Out += src.Run;
            src.Run = 0;
        }
        else if (src.Run >= t.StumpRunMl && (src.RunCount == 0 || drips is null))
        {
            src.Jet += src.Run;   // потёков нет — в струю
            src.Run = 0;
        }
        float speed = (stump ? t.StumpSpeed * (0.45f + 0.55f * Systole(mid, t.StumpPulseHz)) : t.PieceSpeed) * MathF.Sqrt(MathF.Max(envelope, 0));
        if (src.Jet >= MinRelease)
        {
            Jet(ref src, tick, dt, point, normal, carry, MathF.Max(speed, 0.2f), seed, t);
        }
        if (src.Drip >= t.StumpDripDropMl) DripDrop(ref src, tick, point, normal, carry, seed);
    }

    /// <summary>Крышка источника <paramref name="i"/> в мире по позам тика (центр, нормаль наружу); тела нет — false.</summary>
    public bool Locate(int i, IFighterPoses poses, IPiecePoses? pieces, out Vector3 point, out Vector3 normal) =>
        Locate(ref _sources[i], poses, pieces, out point, out normal);

    /// <summary>Крышка в мире по телу своей стороны в позах тика; тела нет — false.</summary>
    private static bool Locate(ref StumpSource src, IFighterPoses poses, IPiecePoses? pieces, out Vector3 point, out Vector3 normal)
    {
        point = normal = default;
        Matrix4x4 world;
        if (src.Piece >= 0)
        {
            if (pieces is null || !FindPiece(pieces.Pieces(src.Fighter), src.Piece, out var body)) return false;
            world = body.World;
        }
        else
        {
            int index = poses.Find(src.Fighter);
            if (index < 0) return false;
            var segments = poses.Segments(index);
            if ((uint)src.Body >= (uint)segments.Length) return false;
            world = segments[src.Body].World;
        }
        point = Vector3.Transform(src.LocalPoint, world);
        var n = Vector3.TransformNormal(src.LocalNormal, world);
        float l = n.Length();
        if (!(l > 1e-6f)) return false;
        normal = n / l;
        return true;
    }

    /// <summary>Потёк вниз по культе: треугольник у края из списка (хеш), объём накопленного.</summary>
    private bool Spawn(int i, BloodDrips drips, uint seed)
    {
        ref var src = ref _sources[i];
        int pick = Math.Min((int)(BloodHash.Unit(seed, 90) * src.RunCount), src.RunCount - 1);
        int triangle = _runs[i * MaxRuns + pick];
        float a = 0.2f + 0.6f * BloodHash.Unit(seed, 91), b = (1 - a) * BloodHash.Unit(seed, 92);
        return drips.Spawn(src.Fighter, -1, triangle, new Vector3(a, b, 1 - a - b), src.Run) >= 0;
    }

    /// <summary>
    /// Капли струи шага: поток (до <see cref="BloodTuning.StumpDropsPerStep"/> капель поровну с разбросом объёма, вдоль шага по времени —
    /// струя, а не комки) и мелкие брызги (лог-нормально, шире конус, медленнее).
    /// </summary>
    private void Jet(ref StumpSource src, ulong tick, float dt, Vector3 point, Vector3 normal, Vector3 carry, float speed, uint seed, BloodTuning t)
    {
        float volume = src.Jet;
        src.Jet = 0;
        Basis(normal, out var u, out var w);
        var origin = point + normal * Lift;
        float spatter = 0;
        int spatterCount = Math.Max(t.StumpSpatterPerStep, 0);
        float spatterTarget = volume * Math.Clamp(t.StumpSpatterShare, 0, 1);
        for (int k = 0; k < spatterCount && spatter < spatterTarget; k++)
        {
            float ml = MathF.Min(BloodSpray.DropMl(BloodHash.Combine(seed, 0x51u + (uint)k), 0, t), spatterTarget - spatter);
            spatter += ml;
            var dir = Cone(normal, u, w, 3 * t.StumpSpread, seed, 40u + 4u * (uint)k);
            float v = speed * (0.5f + 0.6f * BloodHash.Unit(seed, 41u + 4u * (uint)k));
            Emit(src.Fighter, tick, origin, dir * v + carry, ml, dt * BloodHash.Unit(seed, 42u + 4u * (uint)k), t.DropDrag);
        }
        float stream = volume - spatter;
        int count = Math.Clamp((int)MathF.Ceiling(stream / 0.15f), 1, Math.Max(t.StumpDropsPerStep, 1));
        Span<float> share = stackalloc float[count];
        float sum = 0;
        for (int k = 0; k < count; k++) sum += share[k] = 0.6f + 0.8f * BloodHash.Unit(seed, 2u + 4u * (uint)k);
        for (int k = 0; k < count; k++)
        {
            var dir = Cone(normal, u, w, t.StumpSpread, seed, 3u + 4u * (uint)k);
            float v = speed * (0.92f + 0.16f * BloodHash.Unit(seed, 4u + 4u * (uint)k));
            float phase = (k + BloodHash.Unit(seed, 5u + 4u * (uint)k)) / count;
            Emit(src.Fighter, tick, origin, dir * v + carry, stream * share[k] / sum, dt * phase, t.DropDrag);
        }
        src.Out += volume;
    }

    /// <summary>Капля с нижнего края крышки: почти без скорости (тело несёт).</summary>
    private void DripDrop(ref StumpSource src, ulong tick, Vector3 point, Vector3 normal, Vector3 carry, uint seed)
    {
        float volume = src.Drip;
        src.Drip = 0;
        var down = -Vector3.UnitY - normal * -normal.Y;
        float l = down.Length();
        var rim = l > 0.2f ? point + down / l * (0.8f * src.Radius) : point;
        var jitter = new Vector3(BloodHash.Unit(seed, 70) - 0.5f, 0, BloodHash.Unit(seed, 71) - 0.5f) * 0.05f;
        Emit(src.Fighter, tick, rim + normal * Lift, carry + normal * 0.05f + jitter, volume, 0, 2);
        src.Out += volume;
    }

    /// <summary>Капля в полёт: вылетела на <paramref name="ahead"/>, с, раньше тика (струя по шагу непрерывна) — место и скорость сейчас.</summary>
    private void Emit(ulong fighter, ulong tick, Vector3 origin, Vector3 velocity, float ml, float ahead, float drag)
    {
        if (!(ml > 0)) return;
        if (_dropCount == _drops.Length) Array.Resize(ref _drops, _drops.Length * 2);
        _drops[_dropCount++] = new BloodDrop(fighter, tick, BloodFlight.Position(origin, velocity, drag, ahead), BloodFlight.Velocity(velocity, drag, ahead), ml);
        DropsMl += ml;
        DropCount++;
    }

    /// <summary>Направление в конусе полуугла <paramref name="spread"/> вокруг <paramref name="n"/> (равномерно по диску тангенсов).</summary>
    private static Vector3 Cone(Vector3 n, Vector3 u, Vector3 w, float spread, uint seed, uint i)
    {
        float r = spread * MathF.Sqrt(BloodHash.Unit(seed, i)), a = 2 * MathF.PI * BloodHash.Unit(seed, i + 100);
        return Vector3.Normalize(n + MathF.Tan(MathF.Min(r, 1.4f)) * (MathF.Cos(a) * u + MathF.Sin(a) * w));
    }

    private static void Basis(Vector3 n, out Vector3 u, out Vector3 w)
    {
        var a = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        u = Vector3.Normalize(Vector3.Cross(n, a));
        w = Vector3.Cross(n, u);
    }
}
