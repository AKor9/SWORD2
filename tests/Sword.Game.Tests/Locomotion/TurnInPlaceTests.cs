using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Поворот на месте на MM (категория Content): медленный поворот взгляда стоя — стопы стоят, клипы поворота нужной стороны
/// доворачивают ноги, расхождение сходит к малому углу; грудь по курсу корня; на ходу расхождение гаснет за <c>turnReturn</c>;
/// рука у стойки; 0 Б в тике.
/// </summary>
[Trait("Category", "Content")]
public sealed class TurnInPlaceTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const float Deg = MathF.PI / 180;

    private static Animation.CharacterAssets Assets => TestContent.Reference.Value.Assets;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), Assets);

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static PlayerCommand Gaze(float yaw, Vector2 move = default, bool walk = false) =>
        new() { Move = move, Walk = walk, HasGaze = true, Gaze = new Vector3(200 * MathF.Sin(yaw), 0, 200 * MathF.Cos(yaw)) };

    /// <summary>
    /// Взгляд стоя: 1 с вперёд, затем обходит по кругу со скоростью <paramref name="degreesPerSecond"/> (блоками по 2 тика — как мышью) до
    /// тика <paramref name="stop"/>, дальше стоит.
    /// </summary>
    private static PlayerCommand Circle(int tick, float degreesPerSecond, int stop = 480)
    {
        int t = Math.Clamp(tick, 120, stop) - 120;
        return Gaze(degreesPerSecond * Deg * (t / 2 * 2) / 120f);
    }

    private sealed record Sample(int Tick, float Facing, MotionMatchState S, MotionMatchMeasure M, float Chest, string Clip);

    /// <summary>Курс груди в модели — по ключицам (как признак «курс груди» базы): (левая − правая), повёрнутая +X → +Z.</summary>
    private static float ChestYaw(ReadOnlySpan<Matrix4x4> model, int left, int right)
    {
        var d = model[left].Translation - model[right].Translation;
        return MathF.Atan2(-d.Z, d.X);
    }

    private List<Sample> Run(Func<int, PlayerCommand> command, int ticks, Simulation? sim = null)
    {
        sim ??= Create();
        var world = sim.World;
        var player = GameCore.Player(world);
        var view = world.View<IAnimationView>();
        var mm = world.View<IMotionMatchView>();
        int left = view.FindJoint("clavicle_l"), right = view.FindJoint("clavicle_r");
        var samples = new List<Sample>(ticks);
        float reference = float.NaN;
        for (int tick = 0; tick < ticks; tick++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(command(tick));
            sim.Step();
            var s = world.Get<MotionMatchState>(player);
            float chest = ChestYaw(view.Model(world.Get<PoseRef>(player)), left, right);
            if (float.IsNaN(reference)) reference = chest;
            samples.Add(new Sample(tick + 1, world.Get<Transform>(player).Facing, s, mm.Measure(player), Pose2.Wrap(chest - reference),
                s.Clip >= 0 ? view.ClipName(s.Clip) : "-"));
        }
        Clean(sim);
        return samples;
    }

    private static float P95(IEnumerable<float> values)
    {
        var v = values.Where(float.IsFinite).OrderBy(x => x).ToArray();
        return v.Length == 0 ? 0 : v[(int)MathF.Floor(0.95f * (v.Length - 1))];
    }

    private static string D(float radians) => (radians / Deg).ToString("0.0", Inv);

    private void Print(List<Sample> run, int every = 12)
    {
        foreach (var x in run.Where(x => x.Tick % every == 0))
            output.WriteLine(string.Create(Inv,
                $"{x.Tick,5} курс {D(x.Facing),7} ноги {D(x.S.FeetYaw),7} δ {D(x.S.Twist),6} k {x.S.TurnWarp:0.00} грудь {D(x.Chest),5} скольжение {x.M.FootSlide:0.000} {x.Clip} поворотов {x.S.Turns}"));
    }

    [Fact]
    public void TurnClips_SideAndRemaining()
    {
        var sim = Create();
        var clips = Assets.Clips;
        var db = GaspMotionBase.For(clips).Database;
        int idle = db.Clips[db.FindClip(clips.FindClip("locomotion/M_Neutral_Stand_Idle_Loop"))].Start;
        var turn = new TurnInPlace(GaspMotionBase.For(clips), new PosePool(clips), idle);
        Assert.Equal(8, turn.ClipCount);
        // Клип поворота сам: грудь от своего корня и скольжение опорной стопы в его собственном мире (корень клипа) — в вывод.
        var skeleton = clips.Skeleton;
        var rot = new Quaternion[skeleton.JointCount];
        var pos = new Vector3[skeleton.JointCount];
        var model = new Matrix4x4[skeleton.JointCount];
        var contacts = db.Schema.ContactJoints;
        for (int c = 0; c < db.ClipCount; c++)
        {
            if (!turn.IsTurnClip(c)) continue;
            var clip = db.Clips[c];
            float chest = 0, slide = 0;
            var root = Pose2.Identity;
            var last = new Vector3[contacts.Length];
            for (int f = clip.Start; f < clip.End; f++)
            {
                clips.Sample(clip.LibraryClip, db.FrameInClip(f) / db.SampleRate, rot, pos);
                skeleton.ToModel(rot, pos, model);
                chest = MathF.Max(chest, MathF.Abs(turn.ChestFix(rot, pos)));
                for (int k = 0; k < contacts.Length; k++)
                {
                    var world = root.ToParent(model[contacts[k]].Translation);
                    if (f > clip.Start && db.Contact(f, k) && db.Contact(f - 1, k))
                        slide = MathF.Max(slide, new Vector2(world.X - last[k].X, world.Z - last[k].Z).Length() * db.SampleRate);
                    last[k] = world;
                }
                root = Pose2.Compose(root, db.Interval[f]);
            }
            output.WriteLine(string.Create(Inv, $"{clips.Clips[clip.LibraryClip].ShortName}: грудь от корня клипа до {D(chest)}°, скольжение опорной стопы в клипе до {slide:0.00} м/с"));
        }
        string Name(int clip) => clips.Clips[db.Clips[clip].LibraryClip].ShortName;
        Assert.EndsWith("Stand_Turn_045_L", Name(turn.Choose(10 * Deg)));
        Assert.EndsWith("Stand_Turn_090_L", Name(turn.Choose(70 * Deg)));
        Assert.EndsWith("Stand_Turn_090_R", Name(turn.Choose(-100 * Deg)));
        Assert.EndsWith("Stand_Turn_135_L", Name(turn.Choose(130 * Deg)));
        Assert.EndsWith("Stand_Turn_180_R", Name(turn.Choose(-170 * Deg)));
        foreach (int degrees in new[] { 45, 90, 135, 180 })
            foreach (var (side, sign) in new[] { ("L", 1), ("R", -1) })
            {
                int library = clips.FindClip($"locomotion/M_Neutral_Stand_Turn_{degrees:000}_{side}");
                var clip = db.Clips[db.FindClip(library)];
                // L — поворот к +X (курс +): δ > 0 — корень левее ног — клип L
                Assert.Equal(sign * degrees * Deg, clips.RootTurn(library), 1e-3f);
                Assert.Equal(sign * degrees * Deg, turn.Remaining(clip.Start), 0.05f);
                Assert.True(turn.IsTurnFrame(clip.Start) && !turn.IsTurnFrame(sim.World.Get<MotionMatchState>(GameCore.Player(sim.World)).Frame));
            }
        Assert.Equal(1, TurnInPlace.Warp(0.5f, 0.5f * Deg));   // остаток меньше 1° — поворот кончился
        Assert.Equal(0.5f, TurnInPlace.Warp(0.5f, 1));
        Assert.Equal(0, TurnInPlace.Warp(-0.5f, 1));
        Assert.Equal(GaspMotion.TurnWarpMax, TurnInPlace.Warp(3, 1));
    }

    /// <summary>
    /// Поворот взгляда стоя 60°/с, 3 с: ноги стоят, клипы поворота доворачивают их, после остановки расхождение сходит к малому углу;
    /// скольжение опорной стопы — против <c>turnMax = 0</c>.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(-60)]
    public void SlowTurn_FeetStay_TurnClipsCatchUp(float degreesPerSecond)
    {
        const int Ticks = 900, Stop = 480;
        var run = Run(t => Circle(t, degreesPerSecond, Stop), Ticks);
        var before = Run(t => Circle(t, degreesPerSecond, Stop), Ticks, Create("locomotion.turnMax=0"));
        Print(run);
        float slide = P95(run.Skip(120).Select(x => x.M.FootSlide)), slideBefore = P95(before.Skip(120).Select(x => x.M.FootSlide));
        float mean = run.Skip(120).Select(x => x.M.FootSlide).Where(float.IsFinite).Average();
        float meanBefore = before.Skip(120).Select(x => x.M.FootSlide).Where(float.IsFinite).Average();
        // фазы стойки — клип стойки, поворота нет; фазы поворота — клип поворота (до его конца)
        static bool Idle(Sample x) => x.Clip.EndsWith("Stand_Idle_Loop", StringComparison.Ordinal) && x.S.TurnClip < 0;
        float idle = P95(run.Skip(120).Where(Idle).Select(x => x.M.FootSlide)), idleBefore = P95(before.Skip(120).Where(Idle).Select(x => x.M.FootSlide));
        float turning = P95(run.Skip(120).Where(x => x.Clip.Contains("Stand_Turn", StringComparison.Ordinal)).Select(x => x.M.FootSlide));
        output.WriteLine(string.Create(Inv, $"фазы стойки: скольжение p95 {idle:0.000} (без скручивания {idleBefore:0.000}) м/с, тиков {run.Skip(120).Count(Idle)}; фазы клипа поворота: p95 {turning:0.000} м/с"));
        var last = run[^1].S;
        var turns = run.Select(x => x.S.TurnClip).Where(c => c >= 0).Distinct().Select(c => Assets.Clips.Clips[c].ShortName).ToArray();
        float chest = run.Skip(1).Max(x => MathF.Abs(x.Chest));
        output.WriteLine(string.Create(Inv,
            $"скольжение p95 {slide:0.000} (без скручивания {slideBefore:0.000}), среднее {mean:0.000} ({meanBefore:0.000}) м/с; поворотов {last.Turns}: {string.Join(", ", turns)}; грудь от курса корня ≤ {D(chest)}°; δ в конце {D(last.Twist)}°; JointJump max {run.Skip(120).Max(x => float.IsFinite(x.M.JointJump) ? x.M.JointJump : 0):0.000}"));

        string side = degreesPerSecond > 0 ? "_L" : "_R";
        Assert.True(last.Turns >= 2, $"поворотов {last.Turns}");
        Assert.All(turns, name => Assert.EndsWith(side, name));
        Assert.True(idle < 0.2f && idle < idleBefore / 4, $"скольжение стоя p95 {idle} против {idleBefore} без скручивания");
        Assert.True(slide < slideBefore, $"скольжение p95 {slide} против {slideBefore} без скручивания");
        Assert.True(MathF.Abs(last.Twist) <= GaspMotion.TurnSmallAngle * Deg, $"после остановки взгляда δ {D(last.Twist)}°");
        Assert.True(chest < 3 * Deg, $"грудь уходит от курса корня на {D(chest)}°");
        // в клипе поворота, пока его корень поворачивается, расхождение уменьшается
        for (int i = 1; i < run.Count; i++)
        {
            if (run[i].S.TurnClip < 0 || run[i - 1].S.TurnClip >= 0) continue;
            int end = i;
            while (end + 1 < run.Count && run[end + 1].S.TurnClip == run[i].S.TurnClip) end++;
            Assert.True(MathF.Abs(run[end].S.Twist) < MathF.Abs(run[i].S.Twist),
                $"поворот тиков {run[i].Tick}…{run[end].Tick}: δ {D(run[i].S.Twist)}° → {D(run[end].S.Twist)}°");
        }
    }

    /// <summary>Пошёл из скрученной стойки — расхождение гаснет к 0 за ≤ 0,25 с, клипа поворота нет; дальше обычный ход.</summary>
    [Fact]
    public void Walking_TwistReturnsToZero()
    {
        const int Go = 360;
        var walk = Circle(Go - 1, 60, 400) with { Move = new Vector2(0, 1) };   // взгляд — где остановился, ход вперёд
        var run = Run(t => t < Go ? Circle(t, 60, 400) : walk, 600);
        var start = run[Go];
        Assert.True(MathF.Abs(run[Go - 1].S.Twist) > 10 * Deg, $"к началу хода δ {D(run[Go - 1].S.Twist)}° — сценарий не скрутил корпус");
        int zero = run.FindIndex(Go, x => x.S.Twist == 0);
        output.WriteLine(string.Create(Inv, $"δ перед ходом {D(run[Go - 1].S.Twist)}°, ноль через {(zero - Go + 1) / 120f:0.000} с"));
        Assert.True(zero >= 0 && (zero - Go + 1) / 120f <= 0.25f, $"δ гаснет {(zero - Go + 1) / 120f} с");
        Assert.All(run.Skip(Go), x => Assert.Equal(-1, x.S.TurnClip));
        Assert.All(run.Skip(zero), x => Assert.Equal(0, x.S.Twist));
        Assert.Equal(-1, start.S.TurnClip);
    }

    /// <summary>Рука с мечом при скручивании: кисть и клинок относительно груди — у стойки оружия, как без скручивания.</summary>
    [Fact]
    public void SwordArm_HoldsStance_WhileTwisted()
    {
        var sim = Create();
        var world = sim.World;
        var player = GameCore.Player(world);
        var view = world.View<IAnimationView>();
        int chest = view.FindJoint("spine_05");
        Matrix4x4 HandInChest(ReadOnlySpan<Matrix4x4> model)
        {
            Assert.True(Matrix4x4.Invert(model[chest], out var inv));
            return model[Assets.Hand] * inv;
        }
        var pool = new PosePool(Assets.Clips, capacity: 1);
        var h = pool.Alloc();
        pool.Sample(h, Assets.Actions.Idle, SwordArm.StanceTime);
        pool.ToModel(h);
        pool.Tick();
        var stance = HandInChest(pool.Model(h));
        var tip = Assets.Weapons.Weapons[Assets.Weapons.Default].BladeTip;
        int twisted = 0;
        for (int tick = 0; tick < 480; tick++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(Circle(tick, 40));
            sim.Step();
            var s = world.Get<MotionMatchState>(player);
            if (tick < 150 || MathF.Abs(s.Twist) < 5 * Deg) continue;
            twisted++;
            var hand = HandInChest(view.Model(world.Get<PoseRef>(player)));
            float off = MathF.Max(Vector3.Distance(hand.Translation, stance.Translation),
                Vector3.Distance(Vector3.Transform(tip, hand), Vector3.Transform(tip, stance)));
            Assert.True(off < 1e-3f, $"тик {tick + 1}, δ {D(s.Twist)}°: кисть от стойки {off} м");
        }
        Assert.True(twisted > 60, $"тиков со скручиванием {twisted}");
        Clean(sim);
    }

    [Fact]
    public void Turning_DoesNotAllocate()
    {
        var sim = Create();
        PlayerCommand Command(int t) => t < 900 ? Circle(t, 60, 600) : Gaze(MathF.PI, new Vector2(0, 1));
        int tick = 0;
        for (; tick < 240; tick++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(Command(tick));
            sim.Step();
        }
        long allocated = 0;
        for (; tick < 1080; tick++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(Command(tick));
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            sim.Step();
            allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
        Assert.True(sim.World.Get<MotionMatchState>(GameCore.Player(sim.World)).Turns >= 2);
        Assert.Equal(0, allocated);
        Clean(sim);
    }
}
