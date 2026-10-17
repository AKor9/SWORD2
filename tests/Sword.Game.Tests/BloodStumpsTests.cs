using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Runtime;
using Sword.Game.Blood;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Кровь из обрубков: при отрубе — два источника на плоскости среза (культя и кусок) с противоположными нормалями; струя культи пульсирует
/// и гаснет; масса: выпущено = на полу + на коже; источник куска идёт за куском; два прогона бит в бит; брызги из касания при отрубе меньше.
/// </summary>
public sealed class BloodStumpsTests(ITestOutputHelper output)
{
    /// <summary>Позы бойцов с ключами 1…N: суставы и тела сегментов — свои массивы (тест двигает их).</summary>
    private sealed class Poses(Matrix4x4[][] joints, SegmentBody[][] segments) : IFighterPoses
    {
        public readonly SegmentBody[][] Bodies = segments;
        public ulong Tick { get; set; }
        public int Count => joints.Length;
        public PosedFighter Fighter(int index) => new((ulong)index + 1, index, index == 0, false, false, joints[index].Length, Bodies[index].Length);
        public int Find(ulong fighter) => fighter >= 1 && fighter <= (ulong)joints.Length ? (int)fighter - 1 : -1;
        public ReadOnlySpan<Matrix4x4> Joints(int index) => joints[index];
        public ReadOnlySpan<SegmentBody> Segments(int index) => Bodies[index];
    }

    private sealed class FakePieces : IPiecePoses
    {
        public PieceBody[] Bodies = [];
        public ulong Tick { get; set; }
        public ReadOnlySpan<PieceBody> Pieces(ulong fighter) => fighter == 1 ? Bodies : default;
    }

    private static SegmentBody Body(int segment, Vector3 c, float radius, float half) =>
        new(segment, c, Quaternion.Identity, Matrix4x4.CreateTranslation(c), radius, half);

    /// <summary>
    /// Торс синтетики (стена z = 0 в привязке на суставе в 1.2 м) разрезан плоскостью y = 1.2 (нормаль +Y — к куску): культя — капсула ниже
    /// среза (центр по оси −0.15, полудлина 0.15), кусок — выше (центр +0.12).
    /// </summary>
    private static (SkinSurface Surface, Poses Poses, FakePieces Pieces, LimbDetached Cut) Scene()
    {
        var surface = new SkinSurface(BloodBodyTests.Wall(seam: false));
        var bodies = new SegmentBody[BodySegs.Bodies];
        for (int k = 0; k < bodies.Length; k++) bodies[k] = Body(k, new Vector3(0, -50, 0), 0, 0);
        var bone = new Vector3(0, 1.2f, 0);
        bodies[BodySegs.Torso] = Body(BodySegs.Torso, bone + new Vector3(0, -0.15f, 0), 0.12f, 0.15f);
        var poses = new Poses([new[] { Matrix4x4.CreateTranslation(bone) }], [bodies]);
        var pieces = new FakePieces { Bodies = [new PieceBody(0, BodySegs.Torso, SeverMode.Cut, bone + new Vector3(0, 0.12f, 0), Quaternion.Identity,
            Matrix4x4.CreateTranslation(bone + new Vector3(0, 0.12f, 0)), 0.11f, 0.05f)] };
        var cut = new LimbDetached
        {
            Segment = BodySegs.Torso, Mode = SeverMode.Cut, Piece = 0, Normal = Vector3.UnitY, Offset = 0, StumpShift = -0.15f, StumpHalf = 0.15f,
            StumpRadius = 0.12f, PieceCenter = 0.12f, PieceRadius = 0.11f, PieceHalf = 0.05f,
        };
        return (surface, poses, pieces, cut);
    }

    [Fact]
    public void Sever_TwoSources_OnCutPlane_OppositeNormals()
    {
        var (surface, poses, pieces, cut) = Scene();
        var stumps = new BloodStumps(surface);
        var skin = new BloodSkin(surface, 2);
        stumps.Sever(1, cut, 100, poses, pieces, skin, 0, new BloodTuning());
        Assert.Equal(2, stumps.Alive);
        var found = new List<(StumpSide Side, Vector3 Point, Vector3 Normal)>();
        for (int i = 0; i < BloodStumps.Capacity; i++)
            if (stumps.Source(i).Alive && stumps.Locate(i, poses, pieces, out var p, out var n)) found.Add((stumps.Source(i).Side, p, n));
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Side == StumpSide.Stump);
        Assert.Contains(found, f => f.Side == StumpSide.Piece);
        foreach (var f in found) Assert.InRange(f.Point.Y - 1.2f, -0.02f, 0.02f);   // плоскость y = 1.2
        Assert.InRange(Vector3.Dot(found[0].Normal, found[1].Normal), -1.0001f, -0.999f);
        var stump = found.Single(f => f.Side == StumpSide.Stump);
        Assert.True(stump.Normal.Y > 0.999f, "нормаль культи — наружу, к куску");
        // Кровь у края на обе стороны.
        Assert.InRange(stumps.RimMl, 2 * new BloodTuning().StumpRimMl - 1e-3, 2 * new BloodTuning().StumpRimMl + 1e-3);
        Assert.InRange(skin.Deposited, stumps.RimMl * 0.999, stumps.RimMl * 1.001);
    }

    /// <summary>Выпуск источника культи по шагам 60 Гц, мл/шаг, за <paramref name="seconds"/> с.</summary>
    private static List<double> Outflow(bool dead, float seconds, out BloodStumps stumps, out BloodTuning tuning)
    {
        var (surface, poses, pieces, cut) = Scene();
        var t = tuning = new BloodTuning();
        var s = stumps = new BloodStumps(surface);
        s.Sever(1, cut, 0, poses, pieces, null, 0, t);
        int stump = Enumerable.Range(0, BloodStumps.Capacity).Single(i => s.Source(i).Alive && s.Source(i).Side == StumpSide.Stump);
        var flow = new List<double>();
        const float dt = 1f / 60;
        for (int k = 1; k <= seconds * 60; k++)
        {
            s.Step((ulong)(2 * k), dt, poses, pieces, _ => dead, null, null, k * dt, t);
            flow.Add(s.Source(stump).Alive ? s.Source(stump).Released : 0);
        }
        return flow;
    }

    [Fact]
    public void StumpJet_Pulses_AndFades_FasterOnCorpse()
    {
        var alive = Outflow(dead: false, 8, out var stumps, out var t);
        var corpse = Outflow(dead: true, 8, out _, out _);
        // Пики пульса — локальные максимумы выпуска за первые 3 с: период 1 / StumpPulseHz.
        var peaks = new List<int>();
        for (int k = 1; k < 180; k++)
            if (alive[k] > alive[k - 1] && alive[k] >= alive[k + 1] && alive[k] > 2 * alive.Take(180).Average() * 0.5) peaks.Add(k);
        output.WriteLine($"пики (шаги): {string.Join(", ", peaks)}");
        Assert.True(peaks.Count >= 3, $"пиков {peaks.Count}");
        double period = (peaks[^1] - peaks[0]) / (double)(peaks.Count - 1) / 60;
        Assert.InRange(period, 0.95 / t.StumpPulseHz, 1.05 / t.StumpPulseHz);
        // Между пиками — диастола: в разы меньше пика.
        Assert.True(alive.Skip(peaks[0]).Take(peaks[1] - peaks[0]).Min() < 0.25 * alive[peaks[0]]);
        double Second(List<double> f, int s) => f.Skip(60 * s).Take(60).Sum();
        output.WriteLine($"по секундам, живой: {string.Join(" ", Enumerable.Range(0, 8).Select(s => Second(alive, s).ToString("F1")))}; " +
            $"труп: {string.Join(" ", Enumerable.Range(0, 8).Select(s => Second(corpse, s).ToString("F1")))}");
        Assert.True(Second(alive, 4) < 0.1 * Second(alive, 0), "к 4–5 с струя не угасла");
        Assert.True(Second(corpse, 2) < Second(alive, 2), "у трупа струя гаснет быстрее");
        Assert.True(corpse.Sum() < alive.Sum());
        // Струя живого — около заданного объёма (пульс в среднем 1).
        double jet = alive.Sum() - t.StumpDripMl * (1 - Math.Exp(-8 / t.StumpDripSeconds));
        Assert.InRange(jet, 0.9 * t.StumpMl, 1.1 * t.StumpMl);
        Assert.True(stumps.DropCount > 0);
    }

    [Fact]
    public void Mass_Out_EqualsFloorPlusSkin()
    {
        (double Out, double Floor, double Skin, double Stuck, long Landed, int Hash) Run()
        {
            var (surface, poses, pieces, cut) = Scene();
            var t = new BloodTuning();
            var stumps = new BloodStumps(surface);
            var skin = new BloodSkin(surface, 2);
            var drips = new BloodDrips(surface, t, 32, 256);
            var floor = new BloodFloor(t);
            const float tickDt = 1f / 120, stepDt = 1f / 60;
            stumps.Sever(1, cut, 0, poses, pieces, skin, 0, t);
            int hash = 17;
            for (ulong tick = 2; tick < 4400; tick += 2)
            {
                stumps.Step(tick, stepDt, poses, pieces, null, skin, drips, tick * tickDt, t);
                foreach (var d in stumps.Drops) hash = HashCode.Combine(hash, d.Position, d.Velocity, d.Volume);
                floor.DetachSticky(stumps.Drops, null, t.DropDrag, tickDt, 0x57u);
                floor.Collide(tick, poses, t.DropDrag, tickDt, t.SprayOwnSkip);
                floor.Step(tick, tick / 2, Vector2.Zero, t.DropSpread, t.PuddleMl, null);
            }
            for (ulong tick = 4400; tick < 6000 && floor.Flying.Length > 0; tick += 2)
            {
                floor.Collide(tick, poses, t.DropDrag, tickDt, t.SprayOwnSkip);
                floor.Step(tick, tick / 2, Vector2.Zero, t.DropSpread, t.PuddleMl, null);
            }
            Assert.Equal(0, floor.Flying.Length);
            Assert.Equal(0, floor.Lost);
            Assert.Equal(0, stumps.Alive);
            // На коже: у края (сразу и долей струи), потёки по культе, прилипшие капли.
            double onSkin = stumps.RimMl + stumps.RunMl + floor.StuckMl;
            Assert.InRange(skin.Deposited, stumps.RimMl * 0.999, stumps.RimMl * 1.001);
            return (stumps.OutMl, floor.LandedMl, onSkin, floor.StuckMl, floor.Landed, hash);
        }
        var r = Run();
        output.WriteLine($"выпущено {r.Out:F2} мл; на полу {r.Floor:F2} ({r.Landed} капель), на коже {r.Skin:F2} (прилипло {r.Stuck:F2})");
        Assert.True(r.Floor > 0.5 * r.Out, "на полу меньше половины");
        Assert.InRange(r.Floor + r.Skin, 0.95 * r.Out, 1.05 * r.Out);
        Assert.Equal(r, Run());
    }

    [Fact]
    public void PieceSource_FollowsPiece()
    {
        var (surface, poses, pieces, cut) = Scene();
        var t = new BloodTuning();
        var stumps = new BloodStumps(surface);
        stumps.Sever(1, cut, 0, poses, pieces, null, 0, t);
        int piece = Enumerable.Range(0, BloodStumps.Capacity).Single(i => stumps.Source(i).Alive && stumps.Source(i).Side == StumpSide.Piece);
        var start = pieces.Bodies[0].Position;
        for (int k = 1; k <= 60; k++)
        {
            // Кусок падает и кувыркается.
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.05f * k);
            var c = start + new Vector3(0.01f * k, -0.015f * k, 0);
            var world = Matrix4x4.CreateFromQuaternion(rot);
            world.Translation = c;
            pieces.Bodies[0] = pieces.Bodies[0] with { Position = c, Rotation = rot, World = world };
            stumps.Step((ulong)(2 * k), 1f / 60, poses, pieces, null, null, null, k / 60f, t);
            ref readonly var s = ref stumps.Source(piece);
            var expected = c + Vector3.Transform(new Vector3(0, -0.12f, 0), rot);
            Assert.True(Vector3.Distance(s.Point, expected) < 1e-5f, $"шаг {k}: {s.Point} ≠ {expected}");
            Assert.True(Vector3.Dot(s.Normal, Vector3.Transform(-Vector3.UnitY, rot)) > 0.9999f);
            foreach (var d in stumps.Drops)
                if (Vector3.Distance(d.Position, new Vector3(0, 1.2f, 0)) > 0.3f)   // не культи — значит, куска: у его крышки
                    Assert.True(Vector3.Distance(d.Position, s.Point) < 0.2f, $"шаг {k}: капля {d.Position} не у крышки куска {s.Point}");
        }
        Assert.True(Vector3.Distance(stumps.Source(piece).Point, start) > 0.5f);
    }

    [Fact]
    public void Step_NoAllocations()
    {
        var (surface, poses, pieces, cut) = Scene();
        var t = new BloodTuning();
        var stumps = new BloodStumps(surface);
        var skin = new BloodSkin(surface, 2);
        var drips = new BloodDrips(surface, t, 32, 256);
        stumps.Sever(1, cut, 0, poses, pieces, skin, 0, t);
        Func<ulong, bool> alive = _ => false;
        for (int k = 1; k < 30; k++) stumps.Step((ulong)(2 * k), 1f / 60, poses, pieces, alive, skin, drips, k / 60f, t);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 30; k < 300; k++) stumps.Step((ulong)(2 * k), 1f / 60, poses, pieces, alive, skin, drips, k / 60f, t);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---------------- на паке: отруб в сцене ----------------

    private sealed class SeverRun
    {
        public readonly List<string> Failures = [];
        public int Sources;
        public double PieceTravel, SprayAtCutMl, FullSprayMl, OutMl, DropsMl, RimMl;
        public int Hash = 17;
    }

    /// <summary>
    /// Враг с поднятой рукой: проба рубит левое предплечье живого (60 урона) — источники культи и куска на плоскости среза того же тика
    /// (± 2 см), нормали противоположны; дальше 6 с — источник куска на том же месте в теле куска, пока кусок падает.
    /// </summary>
    private static SeverRun RunSever(int segment)
    {
        var config = ConfigSet.Empty(["arena.enemies=1", "combat.enemiesAttack=false", "combat.playerInvulnerable=true"]);
        var sim = Sword.Game.Bootstrap.SwordSimulation.Create(config, BloodBodyTests.Pak.Value);
        var blood = new BloodModule();
        ArenaFighterPoses? poses = null;
        ArenaSeverFeed? sever = null;
        ArenaPiecePoses? pieces = null;
        var services = new ClientServices()
            .Add<IBloodFeed>(link => new ArenaBloodFeed(link))
            .Add<IFighterPoses>(link => poses = new ArenaFighterPoses(link))
            .Add<ISkinMesh>(BloodBodyTests.PakMesh.Value)
            .Add<ISeverFeed>(link => sever = new ArenaSeverFeed(link))
            .Add<IPiecePoses>(link => pieces = new ArenaPiecePoses(link));
        var modules = ClientModules.Compose(config, sim, [blood], services);
        var run = new SeverRun();
        ulong cutTick = ulong.MaxValue;
        int pieceSource = -1;
        Vector3 pieceLocal = default, pieceStart = default;
        double sprayBefore = 0;
        var clock = new SimulationClock(sim, (_, _) => { }, 1, afterTick: tick =>
        {
            sprayBefore = blood.Spray.EmittedMl;
            modules.AfterTick(tick);
            var st = blood.Stumps;
            if (st is not null)
                foreach (var d in st.Drops) run.Hash = HashCode.Combine(run.Hash, d.Position, d.Velocity, d.Volume, d.Tick);
            if (sever!.Detached.Length > 0 && cutTick == ulong.MaxValue)
            {
                cutTick = tick;
                run.SprayAtCutMl = blood.Spray.EmittedMl - sprayBefore;
                var e = sever.Detached[0].Event;
                ulong fighter = sever.Detached[0].Fighter;
                var seg = poses!.Segments(poses.Find(fighter))[e.Segment];
                var bone = seg.Position - Vector3.TransformNormal(Vector3.UnitY, seg.World) * e.StumpShift;
                var n = Vector3.Normalize(Vector3.TransformNormal(e.Normal, seg.World));
                var found = new List<(StumpSide Side, Vector3 P, Vector3 N, int I)>();
                for (int i = 0; i < BloodStumps.Capacity; i++)
                    if (st!.Source(i).Alive && st.Source(i).Born == tick && st.Locate(i, poses, pieces, out var p, out var nn))
                        found.Add((st.Source(i).Side, p, nn, i));
                run.Sources = found.Count;
                foreach (var f in found)
                {
                    float d = Vector3.Dot(n, f.P - bone) - e.Offset;
                    if (MathF.Abs(d) > 0.02f) run.Failures.Add($"{f.Side}: до плоскости {d * 100:F1} см");
                }
                if (found.Count == 2 && Vector3.Dot(found[0].N, found[1].N) > -0.999f) run.Failures.Add($"нормали не противоположны: {Vector3.Dot(found[0].N, found[1].N):F4}");
                var piece = found.FirstOrDefault(f => f.Side == StumpSide.Piece);
                if (piece.N != default)
                {
                    pieceSource = piece.I;
                    var body = pieces!.Pieces(fighter)[e.Piece];
                    Matrix4x4.Invert(body.World, out var inv);
                    pieceLocal = Vector3.Transform(piece.P, inv);
                    pieceStart = piece.P;
                }
            }
            else if (pieceSource >= 0 && st!.Source(pieceSource).Alive && tick > cutTick + 2)
            {
                ref readonly var s = ref st.Source(pieceSource);
                var body = pieces!.Pieces(s.Fighter);
                if (body.Length > s.Piece && st.Locate(pieceSource, poses!, pieces, out var p, out _))
                {
                    float off = Vector3.Distance(Vector3.Transform(pieceLocal, body[s.Piece].World), p);
                    if (off > 1e-4f) run.Failures.Add($"тик {tick}: источник куска ушёл с куска на {off * 100:F2} см");
                    run.PieceTravel = Math.Max(run.PieceTravel, Vector3.Distance(p, pieceStart));
                }
            }
        });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.6f), Facing = MathF.PI });
        for (int t = 0; t < 60 + 720; t++)
        {
            if (t == 60) sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = segment, Damage = 60, Kind = HitKind.Slash });
            modules.BeginFrame(sim.Tick);
            int ticks = clock.RunFrame(0);
            modules.Extract(new FrameView(1, ticks, 0, default));
        }
        if (cutTick == ulong.MaxValue) run.Failures.Add("отруба не было");
        run.FullSprayMl = 60 * blood.Tuning.SprayPerDamage;
        var stumps = blood.Stumps!;
        (run.OutMl, run.DropsMl, run.RimMl) = (stumps.OutMl, stumps.DropsMl, stumps.RimMl);
        return run;
    }

    [Theory]
    [Trait("Category", "Content")]
    [InlineData(BodySegs.FArmL)]
    [InlineData(BodySegs.ThighR)]
    public void Pak_Sever_SourcesOnPlane_PieceFollows_Replayable(int segment)
    {
        var a = RunSever(segment);
        output.WriteLine($"{BodySegs.Name(segment)}: источников {a.Sources}, кусок ушёл на {a.PieceTravel:F2} м, брызги в тике отруба {a.SprayAtCutMl:F3} мл " +
            $"(полные {a.FullSprayMl:F3}); выпущено {a.OutMl:F1} мл (каплями {a.DropsMl:F1}, у края {a.RimMl:F1})");
        foreach (var f in a.Failures.Take(10)) output.WriteLine(f);
        Assert.Empty(a.Failures);
        Assert.Equal(2, a.Sources);
        Assert.True(a.PieceTravel > 0.2, $"кусок не упал: {a.PieceTravel:F2} м");
        Assert.True(a.SprayAtCutMl <= 0.3 * a.FullSprayMl, "брызги из касания при отрубе не ослаблены");
        Assert.True(a.DropsMl > 50, "струи нет");
        Assert.True(a.RimMl > 0, "у края среза крови нет");
        var b = RunSever(segment);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.OutMl, b.OutMl);
    }
}
