using System.Globalization;
using System.Numerics;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Вид походки игрока на MM (категория Content, <c>player=mm</c>): колено опорной ноги над стопой при повороте на месте и на беге по
/// малому кругу, шаг поворота на месте без фиксации стоп, старт с места без рывка пригнувшись.
/// </summary>
[Trait("Category", "Content")]
public sealed class MotionQualityTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), TestContent.Reference.Value.Assets);

    private static void Step(Simulation sim, in PlayerCommand command)
    {
        sim.Export.BeginFrame();
        sim.Enqueue(command);
        sim.Step();
    }

    private static readonly PlayerCommand Stand = new() { HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    /// <summary>Стоя, курсор по кругу 1 рад/с — поворот на месте шагами.</summary>
    private static PlayerCommand TurnSlowly(int t) => new() { HasGaze = true, Gaze = new Vector3(MathF.Sin(t / 120f) * 5, 0, MathF.Cos(t / 120f) * 5) };

    /// <summary>Бег по кругу радиусом ~1,5 м, взгляд вперёд (стрейф).</summary>
    private static PlayerCommand SmallCircle(int t) => t < 120 ? Stand
        : new PlayerCommand { Move = new Vector2(MathF.Cos(t * 2.8f / 120), MathF.Sin(t * 2.8f / 120)), HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    /// <summary>Угол колена от носка вокруг линии бедро — голеностоп опорных (носок у земли) согнутых ног, градусы, по тикам.</summary>
    private static List<float> KneeToToe(Simulation sim, Func<int, PlayerCommand> command, int ticks)
    {
        var player = GameCore.Player(sim.World);
        var anim = sim.World.View<IAnimationView>();
        int[] thigh = [anim.FindJoint("thigh_l"), anim.FindJoint("thigh_r")], calf = [anim.FindJoint("calf_l"), anim.FindJoint("calf_r")],
            foot = [anim.FindJoint("foot_l"), anim.FindJoint("foot_r")], ball = [anim.FindJoint("ball_l"), anim.FindJoint("ball_r")];
        var angles = new List<float>();
        for (int t = 0; t < ticks; t++)
        {
            Step(sim, command(t));
            if (t < 130) continue;
            var m = anim.Model(sim.World.Get<PoseRef>(player));
            for (int l = 0; l < 2; l++)
            {
                var hip = m[thigh[l]].Translation;
                var ankle = m[foot[l]].Translation;
                var toe = m[ball[l]].Translation;
                if (toe.Y > GaspMotion.KneeGround) continue;
                var axis = Vector3.Normalize(ankle - hip);
                var bend = m[calf[l]].Translation - hip;
                bend -= axis * Vector3.Dot(bend, axis);
                var along = toe - ankle;
                along -= axis * Vector3.Dot(along, axis);
                if (bend.Length() < 0.06f || along.Length() < 0.06f) continue;
                angles.Add(MathF.Abs(MathF.Atan2(Vector3.Dot(Vector3.Cross(bend, along), axis), Vector3.Dot(bend, along))) * 180 / MathF.PI);
            }
        }
        angles.Sort();
        return angles;
    }

    private static float P95(List<float> v) => v[(int)(v.Count * 0.95)];

    /// <summary>
    /// Колено над стопой: опорное колено расходится с носком не больше предела <c>kneeLimit</c> и мягкого входа — при повороте на месте
    /// (фиксация стопы держала курс, а клип поворота вёл колено — до 100°; теперь шаг поворота без фиксации) и на беге по малому кругу
    /// (клипы смены направления — до 60°; без правки, <c>kneeLimit = 180</c>, расхождение больше — проверка различает).
    /// </summary>
    [Theory]
    [InlineData("поворот на месте")]
    [InlineData("малый круг")]
    public void Knee_StaysOverPlantedFoot(string scenario)
    {
        Func<int, PlayerCommand> command = scenario == "малый круг" ? SmallCircle : TurnSlowly;
        var on = KneeToToe(Create(), command, 720);
        var off = KneeToToe(Create("locomotion.kneeLimit=180"), command, 720);
        output.WriteLine(string.Create(Inv, $"{scenario}: колено от носка p95 {P95(on):F0}°, max {on[^1]:F0}° (без правки p95 {P95(off):F0}°, max {off[^1]:F0}°), опор {on.Count}"));
        Assert.True(on.Count > 100, $"опорных тиков {on.Count}");
        Assert.True(on[^1] <= GaspMotion.KneeLimit + GaspMotion.KneeSoft + 1, $"колено ушло от носка на {on[^1]}°");
        // На повороте на месте расхождение снимает уже шаг поворота без фиксации стоп; правка колена различима на малом круге.
        if (scenario == "малый круг") Assert.True(off[^1] > on[^1] + 5, $"без правки колено расходится лишь на {off[^1]}° — проверка ничего не различает");
    }

    /// <summary>Шаг поворота на месте (клип поворота ведёт стопы) — без фиксации: стопы не удерживаются, ноги не перекрещиваются.</summary>
    [Fact]
    public void TurnStep_FeetNotLocked()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        int steps = 0;
        for (int t = 0; t < 720; t++)
        {
            Step(sim, TurnSlowly(t));
            var s = sim.World.Get<MotionMatchState>(player);
            if (s.TurnClip < 0) continue;
            steps++;
            Assert.False(s.Feet.Left.Locked || s.Feet.Right.Locked, $"тик {t}: в шаге поворота стопа удержана");
        }
        Assert.True(steps > 60, $"тиков в шаге поворота {steps}");
    }

    /// <summary>
    /// Старт бегом с места — без рывка пригнувшись: таз не ниже стойки больше чем на 12 см (у стартов бегом GASP — 15–22 см), такие
    /// клипы не играют; через 1 с — бег полной скоростью.
    /// </summary>
    [Fact]
    public void Start_NoCrouchedLaunch()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        var anim = sim.World.View<IAnimationView>();
        int pelvis = anim.FindJoint("pelvis");
        for (int t = 0; t < 120; t++) Step(sim, Stand);
        float standing = anim.Model(sim.World.Get<PoseRef>(player))[pelvis].Translation.Y, lowest = standing;
        var run = Stand with { Move = new Vector2(0, 1) };
        for (int t = 0; t < 180; t++)
        {
            Step(sim, run);
            lowest = MathF.Min(lowest, anim.Model(sim.World.Get<PoseRef>(player))[pelvis].Translation.Y);
            string clip = anim.ClipName(sim.World.Get<MotionMatchState>(player).Clip);
            Assert.False(clip.Contains("Run_Start") || clip.Contains("Run_Reface_Start") || clip.Contains("Transition_Walk_to_Run"), clip);
        }
        output.WriteLine(string.Create(Inv, $"таз стоя {standing:F3} м, ниже всего на старте {lowest:F3} м"));
        Assert.True(standing - lowest < 0.12f, $"на старте таз опустился на {standing - lowest} м");
        Assert.Equal(4.2f, sim.World.Get<RootVelocity>(player).Value.Length(), 0.1f);
    }
}
