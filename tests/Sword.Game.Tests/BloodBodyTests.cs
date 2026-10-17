using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Content;
using Sword.Engine.Formats;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Blood;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Кровь на теле без GPU: раны (якорь на коже по попаданию в позе его тика, пак игры) и потёки по коже (синтетические поверхности: отвесная,
/// горизонтальная, шов, поворот тела, сфера; меш пака — для времени шага). Свойства — WR §3.2, §4.3 и план этапа 12 (К1, К2).
/// </summary>
public sealed class BloodBodyTests(ITestOutputHelper output)
{
    // ---------------- синтетика ----------------

    /// <summary>Меш на одном суставе (вес 1, привязка единичная), все треугольники — торс.</summary>
    internal sealed class SyntheticMesh : ISkinMesh
    {
        private readonly Vector3[] _positions, _normals;
        private readonly uint[] _indices;
        private readonly uint[] _joints;
        private readonly Vector4[] _weights;

        public SyntheticMesh(List<Vector3> positions, List<Vector3> normals, List<uint> indices)
        {
            _positions = [.. positions];
            _normals = [.. normals];
            _indices = [.. indices];
            _joints = new uint[_positions.Length];
            _weights = Enumerable.Repeat(new Vector4(1, 0, 0, 0), _positions.Length).ToArray();
        }

        public bool Loaded => true;
        public ReadOnlySpan<Vector3> Positions => _positions;
        public ReadOnlySpan<Vector3> Normals => _normals;
        public ReadOnlySpan<uint> Indices => _indices;
        public ReadOnlySpan<uint> Joints => _joints;
        public ReadOnlySpan<Vector4> Weights => _weights;
        public ReadOnlySpan<Matrix4x4> InverseBind => new[] { Matrix4x4.Identity };
        public ReadOnlySpan<string> JointNames => new[] { "spine_04" };
        public int JointSegment(int joint) => BodySegs.Torso;
    }

    /// <summary>Позы: бойцы с ключами 1…N, суставы — свои массивы (тест меняет их между шагами).</summary>
    internal sealed class FakePoses(params Matrix4x4[][] joints) : IFighterPoses
    {
        public readonly Matrix4x4[][] JointsOf = joints;
        public ulong Tick { get; set; }
        public int Count => JointsOf.Length;
        public PosedFighter Fighter(int index) => new((ulong)index + 1, index, index == 0, false, false, JointsOf[index].Length, 0);
        public int Find(ulong fighter) => fighter >= 1 && fighter <= (ulong)JointsOf.Length ? (int)fighter - 1 : -1;
        public ReadOnlySpan<Matrix4x4> Joints(int index) => JointsOf[index];
        public ReadOnlySpan<SegmentBody> Segments(int index) => [];
    }

    /// <summary>Прямоугольник сеткой: угол, оси (u, v), число клеток; нормаль вершин — <paramref name="normal"/>.</summary>
    internal static void Quad(List<Vector3> p, List<Vector3> n, List<uint> idx, Vector3 origin, Vector3 u, Vector3 v, int cu, int cv, Vector3 normal)
    {
        uint start = (uint)p.Count;
        for (int j = 0; j <= cv; j++)
            for (int i = 0; i <= cu; i++)
            {
                p.Add(origin + u * ((float)i / cu) + v * ((float)j / cv));
                n.Add(normal);
            }
        for (int j = 0; j < cv; j++)
            for (int i = 0; i < cu; i++)
            {
                uint a = start + (uint)(j * (cu + 1) + i), b = a + 1, c = a + (uint)(cu + 1), d = c + 1;
                idx.AddRange([a, b, d, a, d, c]);
            }
    }

    internal static SyntheticMesh Wall(bool seam)
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var idx = new List<uint>();
        if (seam)
        {
            // Две половины с вершинами-копиями на шве y = 0 (как разрез UV у меша скина).
            Quad(p, n, idx, new Vector3(-0.5f, 0, 0), Vector3.UnitX, new Vector3(0, 0.5f, 0), 20, 10, Vector3.UnitZ);
            Quad(p, n, idx, new Vector3(-0.5f, -0.5f, 0), Vector3.UnitX, new Vector3(0, 0.5f, 0), 20, 10, Vector3.UnitZ);
        }
        else Quad(p, n, idx, new Vector3(-0.5f, -0.5f, 0), Vector3.UnitX, Vector3.UnitY, 20, 20, Vector3.UnitZ);
        return new SyntheticMesh(p, n, idx);
    }

    private static SyntheticMesh Floor()
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var idx = new List<uint>();
        Quad(p, n, idx, new Vector3(-0.5f, 0, -0.5f), Vector3.UnitZ, Vector3.UnitX, 20, 20, Vector3.UnitY);
        return new SyntheticMesh(p, n, idx);
    }

    /// <summary>Сфера по широтам и долготам: шов долготы 0/360 — вершины-копии.</summary>
    private static SyntheticMesh Sphere(float radius, int lon = 32, int lat = 16)
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var idx = new List<uint>();
        for (int j = 0; j <= lat; j++)
            for (int i = 0; i <= lon; i++)
            {
                float th = MathF.PI * j / lat, ph = 2 * MathF.PI * i / lon;
                var d = new Vector3(MathF.Sin(th) * MathF.Cos(ph), MathF.Cos(th), MathF.Sin(th) * MathF.Sin(ph));
                p.Add(d * radius);
                n.Add(d);
            }
        for (int j = 0; j < lat; j++)
            for (int i = 0; i < lon; i++)
            {
                uint a = (uint)(j * (lon + 1) + i), b = a + 1, c = a + (uint)(lon + 1), d = c + 1;
                idx.AddRange([a, b, d, a, d, c]);
            }
        return new SyntheticMesh(p, n, idx);
    }

    private static (BloodDrips Drips, SkinSurface Surface, FakePoses Poses, BloodTuning Tuning) Setup(ISkinMesh mesh)
    {
        var surface = new SkinSurface(mesh);
        var tuning = new BloodTuning();
        var poses = new FakePoses(new[] { Matrix4x4.Identity });
        return (new BloodDrips(surface, tuning), surface, poses, tuning);
    }

    private static int SpawnAt(BloodDrips drips, SkinSurface surface, Vector3 bind, float volume)
    {
        int t = surface.Nearest(BodySegs.Torso, bind, out var bary, out _);
        Assert.True(t >= 0);
        return drips.Spawn(1, -1, t, bary, volume);
    }

    private static Vector3 Head(BloodDrips d, SkinSurface s, int i) => s.BindPoint(d.Head(i).Triangle, d.Head(i).Bary);

    private const float Dt = 1f / 60;

    [Fact]
    public void NearestCoarse_WithinOneCell_OfExact()
    {
        var surface = new SkinSurface(Sphere(0.3f, 96, 48));
        var rng = new Random(7);
        float worst = 0;
        for (int k = 0; k < 400; k++)
        {
            var d = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
            var p = d * (0.3f + 0.1f * rng.NextSingle() - 0.02f);
            int exact = surface.Nearest(BodySegs.Torso, p, out _, out float de);
            int coarse = surface.Nearest(BodySegs.Torso, p, out var bary, out float dc, coarse: true);
            Assert.True(exact >= 0 && coarse >= 0);
            Assert.InRange(bary.X + bary.Y + bary.Z, 0.999f, 1.001f);
            Assert.True(dc >= de - 1e-6f);
            worst = MathF.Max(worst, dc - de);
        }
        output.WriteLine($"грубо дальше точного не больше {worst * 100:F2} см");
        Assert.True(worst <= SkinSurface.GridCell * MathF.Sqrt(3), $"{worst}");
    }

    [Fact]
    public void Drip_OnVerticalWall_FlowsDown_AtViscousTerminalSpeed()
    {
        var (drips, surface, poses, tuning) = Setup(Wall(seam: false));
        int i = SpawnAt(drips, surface, new Vector3(0.1f, 0.4f, 0), 10);
        var start = Head(drips, surface, i);
        var positions = new List<Vector3>();
        for (int step = 0; step < 60; step++)
        {
            drips.Step((ulong)step * 2, Dt, poses);
            positions.Add(Head(drips, surface, i));
        }
        var end = positions[^1];
        Assert.True(drips.IsAlive(i));
        Assert.True(MathF.Abs(end.X - start.X) < 1e-4f && MathF.Abs(end.Z) < 1e-6f, $"сошёл с вертикали: {start} → {end}");
        float damp = MathF.Exp(-tuning.Viscosity * Dt);
        float terminal = 9.81f * Dt * damp / (1 - damp);
        float speed = (positions[29].Y - positions[59].Y) / (30 * Dt);
        output.WriteLine($"скорость на отвесной {speed * 100:F3} см/с, по формуле {terminal * 100:F3} см/с");
        Assert.True(MathF.Abs(speed - terminal) <= 0.01f * terminal, $"скорость {speed}, по формуле {terminal}");
    }

    [Fact]
    public void Drip_OnHorizontal_Stands()
    {
        var (drips, surface, poses, _) = Setup(Floor());
        int i = SpawnAt(drips, surface, new Vector3(0.1f, 0, 0.2f), 1);
        var start = drips.Head(i);
        for (int step = 0; step < 120; step++) drips.Step((ulong)step * 2, Dt, poses);
        Assert.True(drips.IsAlive(i));
        Assert.Equal(start, drips.Head(i));
        Assert.Equal(0, drips.Detached);
    }

    [Fact]
    public void Drip_CrossesUvSeam_WithoutBreak()
    {
        var (drips, surface, poses, tuning) = Setup(Wall(seam: true));
        Assert.Equal(surface.VertexCount - 21, surface.WeldCount);   // 21 копия на шве сварена
        int i = SpawnAt(drips, surface, new Vector3(0.05f, 0.15f, 0), 10);
        for (int step = 0; step < 360; step++) drips.Step((ulong)step * 2, Dt, poses);
        var head = Head(drips, surface, i);
        Assert.True(head.Y < -0.05f, $"потёк не перешёл шов: {head}");
        Span<DripPoint> trail = stackalloc DripPoint[BloodDrips.TrailCap];
        int n = drips.Trail(i, trail);
        float worst = 0;
        for (int k = 1; k < n; k++)
            worst = MathF.Max(worst, Vector3.Distance(surface.BindPoint(trail[k].Triangle, trail[k].Bary),
                surface.BindPoint(trail[k - 1].Triangle, trail[k - 1].Bary)));
        Assert.True(worst <= 2 * tuning.TrailStep, $"разрыв следа {worst} м");
    }

    [Fact]
    public void Drip_BodyTurned90_FollowsNewDown()
    {
        var (drips, surface, poses, _) = Setup(Wall(seam: false));
        int i = SpawnAt(drips, surface, new Vector3(0.2f, 0.2f, 0), 10);
        ulong tick = 0;
        for (int step = 0; step < 30; step++) drips.Step(tick += 2, Dt, poses);
        var before = Head(drips, surface, i);
        // Тело поворачивается на 90° вокруг Z за 0.5 с: «вниз» в привязке — уже −X.
        for (int step = 1; step <= 30; step++)
        {
            poses.JointsOf[0][0] = Matrix4x4.CreateRotationZ(MathF.PI / 2 * step / 30);
            drips.Step(tick += 2, Dt, poses);
        }
        var turned = Head(drips, surface, i);
        for (int step = 0; step < 90; step++) drips.Step(tick += 2, Dt, poses);
        var after = Head(drips, surface, i);
        output.WriteLine($"до поворота {before}, после поворота {turned}, через 1.5 с {after}");
        Assert.True(before.Y < 0.2f - 0.01f);
        Assert.True(turned.X - after.X > 0.05f, $"после поворота потёк не идёт к новому низу (−X привязки): {turned} → {after}");
        Assert.True(MathF.Abs(after.Y - turned.Y) < 0.01f, $"после поворота ушёл по Y: {turned} → {after}");
    }

    private static (List<BloodDrop> Drops, List<(int, Vector3)> Trails) SphereRun(out int spawned, out float worstY)
    {
        const float r = 0.15f;
        var (drips, surface, poses, _) = Setup(Sphere(r));
        spawned = 0;
        // Потёки на широте +30° по кругу, один — точно на шве долготы.
        for (int k = 0; k < 8; k++)
        {
            float ph = 2 * MathF.PI * k / 8 + (k == 0 ? 0 : 0.1f);
            var d = new Vector3(MathF.Cos(MathF.PI / 6) * MathF.Cos(ph), MathF.Sin(MathF.PI / 6), MathF.Cos(MathF.PI / 6) * MathF.Sin(ph));
            SpawnAt(drips, surface, d * r, 1);
            spawned++;
        }
        var drops = new List<BloodDrop>();
        var trails = new List<(int, Vector3)>();
        worstY = float.MinValue;
        for (int step = 0; step < 600; step++)
        {
            drips.Step((ulong)step * 2, Dt, poses);
            foreach (var drop in drips.Drops)
            {
                drops.Add(drop);
                worstY = MathF.Max(worstY, drop.Position.Y);
            }
            foreach (var s in drips.Stamps) trails.Add((s.Triangle, s.Bary));
        }
        return (drops, trails);
    }

    [Fact]
    public void Drip_DetachesFromLowerPoints_Deterministic()
    {
        var (drops, trails) = SphereRun(out int spawned, out float worstY);
        output.WriteLine($"отрывов {drops.Count} из {spawned}, выше всех — y {worstY:F4} (низ сферы −0.15)");
        Assert.Equal(spawned, drops.Count);
        // Отрыв — с поверхности, смотрящей вниз больше чем на cos 50°: на сфере y < −R·cos 50° (грань — чуть выше).
        Assert.True(worstY < -0.15f * MathF.Cos(50 * MathF.PI / 180) + 0.01f, $"отрыв выше нижних точек: y {worstY}");
        Assert.All(drops, d => Assert.True(d.Volume > new BloodTuning().DetachVolume && d.Volume < 1));
        // Два прогона — бит в бит.
        var (drops2, trails2) = SphereRun(out _, out _);
        Assert.Equal(drops, drops2);
        Assert.Equal(trails, trails2);
    }

    // ---------------- пак игры ----------------

    /// <summary>Меш персонажа из пака на CPU (как <see cref="ArenaSkinMesh"/> без рендера).</summary>
    internal sealed class PakSkinMesh : ISkinMesh
    {
        private readonly Vector3[] _positions, _normals;
        private readonly uint[] _indices, _joints;
        private readonly Vector4[] _weights;
        private readonly Matrix4x4[] _inverseBind;
        private readonly string[] _names;
        private readonly int[] _segments;

        public PakSkinMesh(IBodyView bodies)
        {
            using var pak = ContentLocator.Open();
            SkinnedMeshData data;
            using (var s = pak.OpenRead(CharacterAssets.MeshFile)) data = SkinnedMeshData.Read(s, pak.Describe(CharacterAssets.MeshFile));
            var v = data.Vertices;
            _positions = v.Select(x => x.Position).ToArray();
            _normals = v.Select(x => x.Normal).ToArray();
            _joints = v.Select(x => x.Joints).ToArray();
            _weights = v.Select(x => new Vector4(x.Weights, Math.Clamp(1 - x.Weights.X - x.Weights.Y - x.Weights.Z, 0, 1))).ToArray();
            _indices = data.Indices;
            _inverseBind = data.InverseBind;
            _names = data.JointNames;
            _segments = Enumerable.Range(0, bodies.JointCount).Select(bodies.JointSegment).ToArray();
        }

        public bool Loaded => true;
        public ReadOnlySpan<Vector3> Positions => _positions;
        public ReadOnlySpan<Vector3> Normals => _normals;
        public ReadOnlySpan<uint> Indices => _indices;
        public ReadOnlySpan<uint> Joints => _joints;
        public ReadOnlySpan<Vector4> Weights => _weights;
        public ReadOnlySpan<Matrix4x4> InverseBind => _inverseBind;
        public ReadOnlySpan<string> JointNames => _names;
        public int JointSegment(int joint) => _segments[joint];
    }

    internal static readonly Lazy<CharacterAssets> Pak = new(CharacterAssets.LoadFromPak);

    internal static readonly Lazy<PakSkinMesh> PakMesh = new(() =>
        new PakSkinMesh(Sword.Game.Bootstrap.SwordSimulation.Create(ConfigSet.Empty("arena.enemies=0"), Pak.Value).World.View<IBodyView>()));

    private static readonly Lazy<SkinSurface> PakSurface = new(() => new SkinSurface(PakMesh.Value));

    /// <summary>Арена с модулем крови на паке: игрок рубит, поворачивая взгляд, враги бьют его; пробы по врагам.</summary>
    internal sealed class Arena
    {
        public readonly Simulation Sim;
        public readonly ClientModules Modules;
        public readonly SimulationClock Clock;
        public readonly BloodModule Blood = new();
        public ArenaBloodFeed Feed = null!;
        public ArenaFighterPoses Poses = null!;
        public readonly List<StrikeProbe> Probes = [];
        public readonly List<ArenaCommand> ArenaCommands = [];
        public Action<ulong>? AfterTick;

        public Arena(params string[] sets)
        {
            var config = ConfigSet.Empty(["arena.enemies=8", "combat.playerInvulnerable=true", .. sets]);
            Sim = Sword.Game.Bootstrap.SwordSimulation.Create(config, Pak.Value);
            var services = new ClientServices()
                .Add<IBloodFeed>(link => Feed = new ArenaBloodFeed(link))
                .Add<IFighterPoses>(link => Poses = new ArenaFighterPoses(link))
                .Add<ISkinMesh>(PakMesh.Value);
            Modules = ClientModules.Compose(config, Sim, [Blood], services);
            Clock = new SimulationClock(Sim, (link, _) =>
            {
                foreach (var p in Probes) link.Enqueue(p);
                foreach (var c in ArenaCommands) link.Enqueue(c);
                Probes.Clear();
                ArenaCommands.Clear();
                float angle = link.Tick / 300 * MathF.PI / 4;
                link.Enqueue(new PlayerCommand { Attack = true, HasGaze = true, Gaze = new Vector3(3 * MathF.Cos(angle), 0, 3 * MathF.Sin(angle)) });
            }, 3, afterTick: tick =>
            {
                Modules.AfterTick(tick);
                AfterTick?.Invoke(tick);
            });
        }

        public void Frame(int f)
        {
            if (f % 10 == 5)
            {
                Probes.Add(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Torso, Damage = 3, Kind = HitKind.Slash });
                Probes.Add(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.Head, Damage = 50, Kind = HitKind.Thrust });
            }
            Modules.BeginFrame(Sim.Tick);
            int ticks = Clock.RunFrame(0);
            Modules.Extract(new FrameView(1, ticks, 0, default));
        }
    }

    [Fact]
    [Trait("Category", "Content")]
    public void Wound_AnchoredOnSkin_AtHitTick_Stable_Replayable_ClearedOnReset()
    {
        var gaps = new List<float>();
        var bySegment = new Dictionary<int, List<float>>();
        int probes = 0;
        float excess = 0;   // якорь дальше ближайшей кожи сегмента в позе тика
        List<BloodWound> Run(bool measure, out Arena arena)
        {
            var a = arena = new Arena();
            var all = new List<BloodWound>();
            a.AfterTick = tick =>
            {
                foreach (var h in a.Feed.Hits)
                {
                    int row = a.Blood.Wounds.Find(h.Fighter);
                    Assert.True(row >= 0, $"нет раны у попадания тика {tick}");
                    var w = a.Blood.Wounds.Of(row).ToArray().Single(x => x.Tick == tick && x.Seed == BloodHash.Seed(h.Fighter, tick, h.ContactIndex));
                    all.Add(w);
                    if (!measure) continue;
                    // Якорь по полному скину вершин треугольника в позе тика удара.
                    var joints = a.Poses.Joints(a.Poses.Find(h.Fighter));
                    var mesh = PakMesh.Value;
                    var s = a.Blood.Surface!;
                    var p = w.Bary.X * SkinMath.Position(mesh, s.Vertex(w.Triangle, 0), joints)
                        + w.Bary.Y * SkinMath.Position(mesh, s.Vertex(w.Triangle, 1), joints)
                        + w.Bary.Z * SkinMath.Position(mesh, s.Vertex(w.Triangle, 2), joints);
                    Assert.True(Vector3.Distance(p, s.SkinPoint(w.Triangle, w.Bary, joints)) < 1e-4f);
                    Assert.Equal(h.Segment, s.Segment(w.Triangle));
                    if (h.Probe)
                    {
                        probes++;
                        continue;
                    }
                    float gap = Vector3.Distance(p, h.Point);
                    gaps.Add(gap);
                    float best = float.MaxValue;
                    foreach (int t in s.SegmentTriangles(h.Segment))
                    {
                        s.SkinTriangle(t, joints, out var q0, out var q1, out var q2);
                        var b = SkinSurface.ClosestBary(h.Point, q0, q1, q2);
                        best = MathF.Min(best, Vector3.Distance(h.Point, b.X * q0 + b.Y * q1 + b.Z * q2));
                    }
                    excess = MathF.Max(excess, gap - best);
                    (bySegment.TryGetValue(h.Segment, out var list) ? list : bySegment[h.Segment] = []).Add(gap);
                }
            };
            for (int f = 0; f < 400; f++) a.Frame(f);
            return all;
        }
        var first = Run(true, out var arena);
        gaps.Sort();
        float median = gaps[gaps.Count / 2], p90 = gaps[gaps.Count * 9 / 10];
        output.WriteLine($"попаданий клинком {gaps.Count}, проб {probes}; якорь − точка попадания: медиана {median * 100:F2} см, p90 {p90 * 100:F2} см, " +
            $"максимум {gaps[^1] * 100:F2} см; дальше ближайшей кожи — {excess * 100:F3} см");
        foreach (var (seg, list) in bySegment.OrderBy(x => x.Key))
        {
            list.Sort();
            output.WriteLine($"  {BodySegs.Name(seg)}: {list.Count}, медиана {list[list.Count / 2] * 100:F2} см, максимум {list[^1] * 100:F2} см");
        }
        Assert.True(gaps.Count >= 10 && probes >= 10, $"клинком {gaps.Count}, проб {probes}");
        // Якорь — ближайшая точка кожи сегмента в позе тика; расстояние до точки попадания — зазор капсулы и кожи (голова — до 11 см).
        Assert.True(excess <= 1e-3f, $"якорь дальше ближайшей кожи на {excess} м");
        Assert.True(median <= 0.03f, $"медиана {median} м");

        // Через 600 тиков анимации — те же записи (треугольник, барицентрики) у оставшихся в кольце.
        var kept = new List<BloodWound>();
        for (int row = 0; row < arena.Blood.Wounds.Capacity; row++) kept.AddRange(arena.Blood.Wounds.Of(row).ToArray());
        arena.AfterTick = null;
        for (int f = 0; f < 200; f++) arena.Frame(1);   // кадры без проб: 600 тиков
        int same = 0;
        foreach (var w in kept)
        {
            int row = arena.Blood.Wounds.Find(w.Fighter);
            if (row < 0) continue;
            var now = arena.Blood.Wounds.Of(row).ToArray().Where(x => x.Seed == w.Seed && x.Tick == w.Tick).ToArray();
            if (now.Length == 0) continue;
            Assert.Equal(w, now.Single());
            same++;
        }
        Assert.True(same >= 5, $"ран через 600 тиков {same}");

        // Реплей — те же записи.
        var second = Run(false, out var again);
        Assert.Equal(first, second);
        Assert.True(again.Blood.Drips!.Spawned > 0);

        // Сброс арены (R) — ран и потёков нет.
        again.ArenaCommands.Add(new ArenaCommand { Action = ArenaAction.Reset });
        again.AfterTick = null;
        again.Frame(0);
        Assert.Equal(0, again.Blood.Wounds.Count);
        Assert.Equal(0, again.Blood.Drips!.Alive);
    }

    [Fact]
    [Trait("Category", "Content")]
    public void Drips_33Fighters_x16_StepTime_NoAllocations()
    {
        var mesh = PakMesh.Value;
        var surface = PakSurface.Value;
        output.WriteLine($"кожа: треугольников {surface.TriangleCount}, вершин {surface.VertexCount}, сварено {surface.WeldCount}, " +
            $"рёбер без пары {surface.OpenEdges}");
        var bind = new Matrix4x4[mesh.InverseBind.Length];
        for (int j = 0; j < bind.Length; j++) bind[j] = surface.Bind(j);
        var fighters = new Matrix4x4[33][];
        for (int f = 0; f < 33; f++)
        {
            var world = Matrix4x4.CreateRotationY(f * 0.3f) * Matrix4x4.CreateTranslation(f * 2, 0, 0);
            fighters[f] = bind.Select(m => m * world).ToArray();
        }
        var poses = new FakePoses(fighters);
        var tuning = new BloodTuning();
        var drips = new BloodDrips(surface, tuning, perFighter: 16, world: 33 * 16);
        var torso = surface.SegmentTriangles(BodySegs.Torso);
        for (int f = 0; f < 33; f++)
            for (int k = 0; k < 16; k++)
            {
                int t = torso[(int)(BloodHash.Combine((uint)f, (uint)k) % (uint)torso.Length)];
                drips.Spawn((ulong)f + 1, -1, t, new Vector3(1 / 3f), 5);
            }
        Assert.Equal(528, drips.Alive);
        ulong tick = 0;
        uint serial = 0;
        // Прогрев (JIT доходит до оптимизированного кода), затем замер; ушедшие потёки возвращаются — живых всегда 528.
        for (int s = 0; s < 300; s++)
        {
            Sway(fighters, bind, s);
            drips.Step(tick += 2, Dt, poses);
            TopUp(drips, torso, ref serial);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        Span<double> blocks = stackalloc double[3];
        long spent = 0;
        long crossings = drips.Crossings, slides = drips.Slides;
        const int steps = 300;
        int alive = int.MaxValue;
        for (int s = 0; s < steps; s++)
        {
            Sway(fighters, bind, 300 + s);
            alive = Math.Min(alive, drips.Alive);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            drips.Step(tick += 2, Dt, poses);
            spent += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            TopUp(drips, torso, ref serial);
            if (s % 100 == 99)
            {
                blocks[s / 100] = spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 100;
                spent = 0;
            }
        }
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        // Три блока по 100 шагов — медиана (одиночный блок ловит чужую нагрузку машины).
        blocks.Sort();
        double ms = blocks[1];
        output.WriteLine($"шаг потёков: {ms:F4} мс (блоки {blocks[0]:F4}…{blocks[2]:F4}) на 33 бойца × 16; за {steps} шагов оторвалось {drips.Detached}, кончилось {drips.Ended}, " +
            $"переходов ребра {drips.Crossings - crossings}, скольжений {drips.Slides - slides}; выделено {bytes} Б");
        Assert.Equal(0, bytes);
        Assert.Equal(528, alive);
#if !DEBUG
        Assert.True(ms <= 0.1, $"шаг {ms} мс");   // время — только в Release
#endif
    }

    /// <summary>Вернуть ушедшие потёки: каждому бойцу — до 16 на торсе (место — из хеша).</summary>
    private static void TopUp(BloodDrips drips, ReadOnlySpan<int> torso, ref uint serial)
    {
        Span<int> mine = stackalloc int[34];
        for (int i = 0; i < drips.Capacity; i++) if (drips.IsAlive(i)) mine[(int)drips.FighterOf(i)]++;
        for (int f = 1; f <= 33; f++)
            for (; mine[f] < 16; mine[f]++)
                drips.Spawn((ulong)f, -1, torso[(int)(BloodHash.Combine((uint)f, ++serial) % (uint)torso.Length)], new Vector3(1 / 3f), 5);
    }

    /// <summary>Покачивание тел: поворот вокруг вертикали ±0.3 рад с периодом 1 с (без выделений).</summary>
    private static void Sway(Matrix4x4[][] fighters, Matrix4x4[] bind, int step)
    {
        for (int f = 0; f < fighters.Length; f++)
        {
            var world = Matrix4x4.CreateRotationY(f * 0.3f + 0.3f * MathF.Sin(step * Dt * 2 * MathF.PI)) * Matrix4x4.CreateTranslation(f * 2, 0, 0);
            var joints = fighters[f];
            for (int j = 0; j < joints.Length; j++) joints[j] = bind[j] * world;
        }
    }
}
