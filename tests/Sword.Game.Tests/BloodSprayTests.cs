using System.Numerics;
using Sword.Game.Blood;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Брызги из раны (v6): веер — число и объём ∝ урону, направления в конусе вокруг хода клинка, два прогона бит в бит; капля через капсулу
/// другого бойца прилипает к его коже (объём тот же), из своего тела — нет; масса: вылетело = на полу + на коже.
/// </summary>
public sealed class BloodSprayTests(ITestOutputHelper output)
{
    /// <summary>Позы с телами сегментов: бойцы с ключами 1…N.</summary>
    private sealed class Poses(Matrix4x4[][] joints, SegmentBody[][] segments) : IFighterPoses
    {
        public ulong Tick { get; set; }
        public int Count => joints.Length;
        public PosedFighter Fighter(int index) => new((ulong)index + 1, index, index == 0, false, false, joints[index].Length, segments[index].Length);
        public int Find(ulong fighter) => fighter >= 1 && fighter <= (ulong)joints.Length ? (int)fighter - 1 : -1;
        public ReadOnlySpan<Matrix4x4> Joints(int index) => joints[index];
        public ReadOnlySpan<SegmentBody> Segments(int index) => segments[index];
    }

    /// <summary>Тела бойца: все 14 сегментов — капсулы с осью Y; <paramref name="place"/> — центр и радиус по сегменту (нет — далеко внизу, радиус 0).</summary>
    private static SegmentBody[] Body(Func<int, (Vector3 Center, float Radius, float Half)?> place)
    {
        var bodies = new SegmentBody[BodySegs.Count];
        for (int k = 0; k < bodies.Length; k++)
        {
            var (c, r, h) = place(k) ?? (new Vector3(0, -50, 0), 0f, 0f);
            bodies[k] = new SegmentBody(k, c, Quaternion.Identity, Matrix4x4.CreateTranslation(c), r, h);
        }
        return bodies;
    }

    private static readonly Matrix4x4 VictimJoint = Matrix4x4.CreateTranslation(0, 1.2f, 0);

    /// <summary>Рана на стене (плоскость z = 0 в привязке, нормаль +Z), поднятой на 1.2 м; удар по +X клинком вдоль Y, 10 м/с.</summary>
    private static (BloodHit Hit, BloodWound Wound, SkinSurface Surface) Wound(float damage, HitKind kind = HitKind.Slash, ulong tick = 100)
    {
        var surface = new SkinSurface(BloodBodyTests.Wall(seam: false));
        var hit = new BloodHit(tick, 1, 2, BodySegs.Torso, new Vector3(0, 1.4f, 0.01f), Vector3.UnitX, Vector3.UnitY, 10, damage, kind,
            false, false, false, 0);
        var wounds = new BloodWounds();
        int index = wounds.Record(hit, new[] { VictimJoint }, surface, new BloodTuning(), out int row);
        Assert.True(index >= 0);
        return (hit, wounds.At(row, index), surface);
    }

    [Fact]
    public void Fan_CountAndVolumeByDamage_InConeAroundDir_Deterministic()
    {
        var t = new BloodTuning { SprayBackShare = 0 };
        BloodDrop[] Emit(float damage, HitKind kind = HitKind.Slash)
        {
            var (hit, w, s) = Wound(damage, kind);
            return new BloodSpray().Emit(hit, w, new[] { VictimJoint }, default, s, t).ToArray();
        }
        var a = Emit(30);
        var b = Emit(60);
        double va = a.Sum(d => d.Volume), vb = b.Sum(d => d.Volume);
        output.WriteLine($"30 урона: {a.Length} капель, {va:F4} мл; 60 урона: {b.Length} капель, {vb:F4} мл");
        Assert.Equal(30 * t.SprayPerDamage, va, 4);
        Assert.Equal(60 * t.SprayPerDamage, vb, 4);
        Assert.InRange(b.Length / (double)a.Length, 1.5, 2.5);
        float maxMl = MathF.PI / 6 * t.SprayMaxMm * t.SprayMaxMm * t.SprayMaxMm * 1e-3f;
        float minMl = MathF.PI / 6 * t.SprayMinMm * t.SprayMinMm * t.SprayMinMm * 1e-3f;
        for (int i = 0; i < b.Length - 1; i++) Assert.InRange(b[i].Volume, minMl * 0.999f, maxMl * 1.001f);
        var mean = Vector3.Zero;
        foreach (var d in b)
        {
            float speed = d.Velocity.Length();
            Assert.InRange(speed, t.SprayMinSpeed - 1e-3f, t.SprayMaxSpeed + 1e-3f);
            var dir = d.Velocity / speed;
            Assert.True(Vector3.Dot(dir, Vector3.UnitX) > 0.6f, $"капля вне конуса: {dir}");   // ход клинка
            Assert.True(dir.Z > 0, $"капля в кожу: {dir}");   // наружу по нормали
            mean += dir;
        }
        mean = Vector3.Normalize(mean);
        Assert.True(MathF.Abs(mean.Y) < 0.25f, $"веер не вокруг хода: {mean}");   // разброс в плоскости хода — симметричный
        Assert.Equal(b, Emit(60));   // бит в бит

        var thrust = Emit(60, HitKind.Thrust);
        foreach (var d in thrust) Assert.True(Vector3.Dot(Vector3.Normalize(d.Velocity), -Vector3.UnitX) > 0.4f, "укол: струя не назад по ходу");
    }

    [Fact]
    public void Drop_ThroughOtherCapsule_SticksToItsSkin_NotToOwnBody()
    {
        var surface = new SkinSurface(BloodBodyTests.Wall(seam: false));
        // Другой боец: стена кожи — плоскость x = 1 (нормаль −X), торс — капсула перед ней.
        var other = Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 1.2f, 0);
        var poses = new Poses(
            [new[] { VictimJoint }, new[] { other }],
            [
                Body(k => k == BodySegs.Torso ? (new Vector3(0, 1.2f, 0), 0.15f, 0.3f) : null),
                Body(k => k == BodySegs.Torso ? (new Vector3(1.05f, 1.2f, 0), 0.06f, 0.3f) : null),
            ]);
        const float dt = 1f / 120;
        var t = new BloodTuning();
        (BloodFloor Floor, List<BloodStick> Sticks) Fly(Vector3 v)
        {
            var floor = new BloodFloor(t);
            floor.DetachSticky(new[] { new BloodDrop(1, 10, new Vector3(0, 1.2f, 0), v, 0.02f) }, null, t.DropDrag, dt, 7);
            var sticks = new List<BloodStick>();
            for (ulong tick = 10; tick < 400 && floor.Flying.Length > 0; tick += 2)
            {
                sticks.AddRange(floor.Collide(tick, poses, t.DropDrag, dt, t.SprayOwnSkip).ToArray());
                floor.Step(tick, tick / 2, Vector2.Zero, t.DropSpread, t.PuddleMl, null);
            }
            return (floor, sticks);
        }
        // Из своего торса (начало внутри своей капсулы) — к другому: прилипает к нему.
        var (f1, s1) = Fly(new Vector3(6, 0.5f, 0));
        var stick = Assert.Single(s1);
        Assert.Equal(2ul, stick.Fighter);
        Assert.Equal(BodySegs.Torso, stick.Segment);
        Assert.Equal(0.02f, stick.Volume);
        Assert.Equal(0, f1.Landed);
        Assert.InRange(stick.Point.X, 0.97f, 1.0f);
        // На кожу ближайшей вершины этого бойца: объём тот же.
        int triangle = BloodWounds.Anchor(surface, stick.Segment, stick.Point, poses.Joints(1), out var bary);
        Assert.True(triangle >= 0);
        var skin = new BloodSkin(surface, 2);
        skin.Stamp(stick.Fighter, triangle, bary, stick.Volume, 0, BloodModule.StickRadius(stick.Volume, t));
        Assert.Equal(stick.Volume, skin.Deposited, 6);
        int best = 0;
        for (int w = 0; w < surface.WeldCount; w++) if (skin.Thickness(2, w) > skin.Thickness(2, best)) best = w;
        var at = Vector3.Transform(surface.BindPosition(surface.WeldCopies(best)[0]), other);
        output.WriteLine($"касание {stick.Point}, толще всего вершина {at}");
        Assert.True(Vector3.Distance(at, stick.Point) < 0.05f, $"пятно не у касания: {at}");
        Assert.Equal(0, skin.Thickness(1, best));

        // Прочь от другого: своё тело (вылет изнутри) не держит — капля на полу.
        var (f2, s2) = Fly(new Vector3(-3, 0.5f, 0.5f));
        Assert.Empty(s2);
        Assert.Equal(1, f2.Landed);
    }

    [Fact]
    public void Mass_Emitted_EqualsFloorPlusSkin()
    {
        var t = new BloodTuning { SprayBackShare = 0.5f };
        var (hit, w, surface) = Wound(60, tick: 10);
        // Атакующий — в 0.8 м перед раной (по нормали +Z).
        var attackerJoint = Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, 1.2f, 0.9f);
        // Кожа синтетики — только торс: остальные тела атакующего далеко (прицел в них отвергается — капли идут веером).
        var attacker = Body(k => k == BodySegs.Torso ? (new Vector3(0, 1.3f, 0.9f), 0.17f, 0.25f) : null);
        var poses = new Poses([new[] { VictimJoint }, new[] { attackerJoint }],
            [Body(k => k == BodySegs.Torso ? (new Vector3(0, 1.2f, -0.1f), 0.12f, 0.3f) : null), attacker]);
        const float dt = 1f / 120;
        (double Emitted, double Floor, double Skin, double Stuck, long Sticks) Run()
        {
            var spray = new BloodSpray();
            var floor = new BloodFloor(t);
            var skin = new BloodSkin(surface, 2);
            var drops = spray.Emit(hit, w, poses.Joints(0), poses.Segments(1), surface, t);
            floor.DetachSticky(drops, null, t.DropDrag, dt, w.Seed | 1);
            for (ulong tick = 10; tick < 1200 && floor.Flying.Length > 0; tick += 2)
            {
                foreach (var s in floor.Collide(tick, poses, t.DropDrag, dt, t.SprayOwnSkip))
                {
                    int tri = BloodWounds.Anchor(surface, s.Segment, s.Point, poses.Joints(poses.Find(s.Fighter)), out var bary);
                    if (tri >= 0) skin.Stamp(s.Fighter, tri, bary, s.Volume, tick * dt, BloodModule.StickRadius(s.Volume, t));
                }
                floor.Step(tick, tick / 2, Vector2.Zero, t.DropSpread, t.PuddleMl, null);
            }
            Assert.Equal(0, floor.Flying.Length);
            Assert.Equal(0, floor.Lost);
            return (spray.EmittedMl, floor.LandedMl, skin.Deposited, floor.StuckMl, floor.Stuck);
        }
        var r = Run();
        output.WriteLine($"вылетело {r.Emitted:F4} мл; на полу {r.Floor:F4}, на коже {r.Skin:F4} (прилипло {r.Sticks} капель, {r.Stuck:F4} мл)");
        Assert.True(r.Sticks > 0, "к атакующему ничего не прилипло");
        Assert.True(r.Floor > 0);
        Assert.InRange(r.Floor + r.Skin, 0.95 * r.Emitted, 1.05 * r.Emitted);
        Assert.Equal(r, Run());
    }
    [Fact]
    public void Spray_EmitFlyStick_NoAllocations()
    {
        var t = new BloodTuning { SprayBackShare = 0.5f };
        var (hit, w, surface) = Wound(60, tick: 10);
        var attacker = Body(k => k == BodySegs.Torso ? (new Vector3(0, 1.3f, 0.9f), 0.17f, 0.25f) : null);
        var poses = new Poses([new[] { VictimJoint }, new[] { Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0, 1.2f, 0.9f) }],
            [Body(k => k == BodySegs.Torso ? (new Vector3(0, 1.2f, -0.1f), 0.12f, 0.3f) : null), attacker]);
        var spray = new BloodSpray();
        var floor = new BloodFloor(t);
        var skin = new BloodSkin(surface, 2);
        const float dt = 1f / 120;
        ulong tick = 10;
        void Round()
        {
            floor.BeginFrame();
            var drops = spray.Emit(hit with { Tick = tick }, w, poses.Joints(0), poses.Segments(1), surface, t);
            floor.DetachSticky(drops, null, t.DropDrag, dt, w.Seed | 1);
            for (int k = 0; k < 120; k++, tick += 2)
            {
                foreach (var s in floor.Collide(tick, poses, t.DropDrag, dt, t.SprayOwnSkip))
                {
                    int tri = BloodWounds.Anchor(surface, s.Segment, s.Point, poses.Joints(poses.Find(s.Fighter)), out var bary, refine: false);
                    if (tri >= 0) skin.Stamp(s.Fighter, tri, bary, s.Volume, tick * dt, BloodModule.StickRadius(s.Volume, t));
                }
                floor.Step(tick, tick / 2, Vector2.Zero, t.DropSpread, t.PuddleMl, null);
                if (k % 8 == 0) floor.BeginFrame();
            }
            skin.Gather();
        }
        for (int i = 0; i < 3; i++) Round();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3; i++) Round();
        long spent = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(spent == 0, $"брызги выделили {spent} Б");
    }
}
