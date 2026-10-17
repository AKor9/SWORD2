using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Передача от действия всем телом к Motion Matching игрока (категория Content, <c>player=mm</c>): особый приём (укол
/// <c>Anim_PS_Combo_B1_RM</c> слоем <c>full</c>) на бегу играет целиком, а уходя, отдаёт позу MM, который ищет кадр по видимой позе.
/// </summary>
[Trait("Category", "Content")]
public sealed class MotionHandoffTests(ITestOutputHelper output)
{
    private const string SpecialMove = "sword_1h/Anim_PS_Combo_B1_RM";

    private static readonly PlayerCommand Run = new() { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    private static Simulation Create()
    {
        var warnings = new List<string>();
        var assets = TestContent.Reference.Value.Assets.WithActions(
            $$"""{ "stab1": "{{SpecialMove}}", "stab1.layer": "full", "stab2": "{{SpecialMove}}", "stab2.layer": "full", "stab3": "{{SpecialMove}}", "stab3.layer": "full" }""",
            warnings);
        Assert.Empty(warnings);
        return SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm"]), assets);
    }

    private static void Step(Simulation sim, in PlayerCommand command)
    {
        sim.Export.BeginFrame();
        sim.Enqueue(command);
        sim.Step();
    }

    /// <summary>
    /// На бегу особый приём — всем телом (ноги и разворот таза — клипа), бег гаснет, выпад ведёт клип; под приёмом MM не ищет, а в
    /// первом тике ухода ищет и меняет кадр (кадр бега, оставшийся под приёмом, проигрывает видимой позе); таз в мире не скачет
    /// (вторая разность за тик — миллиметры) ни в приёме, ни на передаче; после — снова бег; нарушений инвариантов нет.
    /// </summary>
    [Fact]
    public void SpecialMove_OnTheRun_FullBody_MmTakesOverFromVisiblePose()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        var anim = sim.World.View<IAnimationView>();
        int pelvis = anim.FindJoint("pelvis");
        var shown = new List<Vector3>();
        void Record()
        {
            var t = sim.World.Get<Transform>(player);
            var world = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, t.Facing));
            world.Translation = t.Position;
            shown.Add(Vector3.Transform(anim.Model(sim.World.Get<PoseRef>(player))[pelvis].Translation, world));
        }
        for (int t = 0; t < 180; t++) Step(sim, Run);
        var start = sim.World.Get<Transform>(player).Position;
        float speed = sim.World.Get<RootVelocity>(player).Value.Length();
        Step(sim, Run with { Stab = true });
        Record();
        var a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal((ActionKind.Attack, SpecialMove), (a.Kind, anim.ClipName(a.Clip)));
        int handoff = -1, ticks = 0;
        var before = sim.World.Get<MotionMatchState>(player);
        while (sim.World.Get<AnimatorAction>(player).Kind == ActionKind.Attack && ticks++ < 600)
        {
            a = sim.World.Get<AnimatorAction>(player);
            Assert.False(a.Upper);
            Assert.Equal(1, a.Stand);
            var s = sim.World.Get<MotionMatchState>(player);
            if (handoff < 0 && s.Searches > before.Searches)
            {
                // первый поиск после начала приёма — когда приём уходит, и он меняет кадр
                handoff = ticks;
                Assert.True(a.Leaving, $"MM искал под приёмом (тик {ticks}, время клипа {a.Time:F2} с)");
                Assert.Equal(before.Searches + 1, s.Searches);
                Assert.Equal(before.Switches + 1, s.Switches);
                output.WriteLine($"передача на тике {ticks} приёма: {anim.ClipName(s.Clip)} кадр {s.ClipFrame}");
            }
            Step(sim, Run);
            Record();
        }
        Assert.True(handoff > 0, "приём не уходил");
        float lunge = Vector3.Distance(start, sim.World.Get<Transform>(player).Position);
        for (int t = 0; t < 120; t++)
        {
            Step(sim, Run);
            Record();
        }
        Assert.Equal(speed, sim.World.Get<RootVelocity>(player).Value.Length(), 0.05f);
        float jump = 0;
        for (int i = 1; i < shown.Count - 1; i++) jump = MathF.Max(jump, (shown[i + 1] - 2 * shown[i] + shown[i - 1]).Length());
        output.WriteLine($"выпад за приём {lunge:F2} м, наибольшая вторая разность таза {jump * 1000:F1} мм");
        Assert.True(lunge > 1.5f, $"выпад приёма {lunge} м — root motion клипа не ведёт корень");
        Assert.True(jump < 0.02f, $"таз скачет: вторая разность {jump * 1000:F1} мм за тик");
        Assert.True(sim.Invariants.Total == 0,
            string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));
    }
}
