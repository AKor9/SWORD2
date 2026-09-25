using System.Numerics;
using Sword.Engine.Animation.Motion;
using Sword.Game.Locomotion.Motion;

namespace Sword.Game.Tests;

/// <summary>
/// Политика графа GASP на синтетических измерениях клипов: выход медленнее 0,3 м/с — стойка (по имени, не медленный цикл);
/// иначе циклы той же походки в пределах 30° от направления выхода (по средней скорости цикла); запасные ветки — та же походка, любой
/// ход, стойка; прошлое — по измеренной скорости начала, а не по виду из имени.
/// </summary>
public sealed class GaspGraphPolicyTests
{
    /// <summary>Клип набора: имя и измеренное движение (номер в библиотеке — 100 + номер в наборе).</summary>
    private sealed record Clip(string Name, bool Loop, Vector3 Start, Vector3 Exit, Vector3 Mean);

    private static Vector3 At(float speed, float degrees) =>
        new(speed * MathF.Sin(degrees * MathF.PI / 180), 0, speed * MathF.Cos(degrees * MathF.PI / 180));

    private static (GaspGraphPolicy Policy, MotionClipInfo[] Infos) Set(params Clip[] clips)
    {
        var libraryClips = clips.Select((_, i) => 100 + i).ToArray();
        var names = clips.Select(c => GaspClipName.Parse(c.Name)).ToArray();
        var infos = clips.Select((c, i) => new MotionClipInfo(100 + i, c.Loop, 31, c.Start, c.Exit, c.Mean, 0)).ToArray();
        return (new GaspGraphPolicy(libraryClips, names), infos);
    }

    private static readonly Clip Idle = new("M_Neutral_Stand_Idle_Loop", true, default, default, default);
    private static readonly Clip WalkF = new("M_Neutral_Walk_Loop_F", true, At(2.07f, 0), At(2.07f, 0), At(2.07f, 0));
    private static readonly Clip WalkL20 = new("M_Neutral_Walk_Loop_F_L_20", true, At(2.07f, 20), At(2.07f, 20), At(2.07f, 20));
    private static readonly Clip WalkFL = new("M_Neutral_Walk_Loop_FL", true, At(2.07f, 45), At(2.07f, 45), At(2.07f, 45));
    private static readonly Clip WalkB = new("M_Neutral_Walk_Loop_B", true, At(1.55f, 180), At(1.55f, 180), At(1.55f, 180));
    private static readonly Clip RunF = new("M_Neutral_Run_Loop_F", true, At(5.17f, 0), At(5.17f, 0), At(5.17f, 0));
    private static readonly Clip Circle = new("M_Neutral_Walk_Circle_Strafe_L", true, At(1f, 90), At(1f, 90), At(0.05f, 90));

    private static Clip Start(float exitDegrees, string name = "M_Neutral_Walk_Start_F_Lfoot") =>
        new(name, false, At(0.05f, exitDegrees), At(2f, exitDegrees), At(1f, exitDegrees));

    private static List<int> Candidates(GaspGraphPolicy policy, MotionClipInfo[] infos, int clip, out GaspBranch branch)
    {
        var list = new List<int>();
        branch = policy.Choose(clip, infos, list);
        list.Sort();
        return list;
    }

    [Fact]
    public void SlowExit_GoesToIdleByName_NotToSlowLoop()
    {
        var stop = new Clip("M_Neutral_Walk_Stop_F_Lfoot", false, At(2f, 0), At(0.2f, 0), At(1f, 0));
        var (policy, infos) = Set(Idle, WalkF, Circle, stop);
        Assert.Equal([0], Candidates(policy, infos, 3, out var branch));
        Assert.Equal(GaspBranch.Stand, branch);
    }

    [Fact]
    public void MovingExit_SameGaitLoops_Within30Degrees()
    {
        var (policy, infos) = Set(Idle, WalkF, WalkL20, WalkFL, WalkB, RunF, Start(0));
        Assert.Equal([1, 2], Candidates(policy, infos, 6, out var branch));   // F и F_L_20 (20°), не FL (45°), не бег
        Assert.Equal(GaspBranch.SameGaitDirection, branch);

        (policy, infos) = Set(Idle, WalkF, WalkL20, WalkFL, WalkB, RunF, Start(40));
        Assert.Equal([2, 3], Candidates(policy, infos, 6, out branch));       // F_L_20 (20° от 40°) и FL (5°), не F (40°)
        Assert.Equal(GaspBranch.SameGaitDirection, branch);

        (policy, infos) = Set(Idle, WalkF, WalkB, RunF, Start(0, "M_Neutral_Run_Start_F_Lfoot"));
        Assert.Equal([3], Candidates(policy, infos, 4, out branch));          // бег — к бегу
        Assert.Equal(GaspBranch.SameGaitDirection, branch);
    }

    [Fact]
    public void Fallbacks_SameGait_ThenAnyMoving_ThenIdle_ThenNone()
    {
        // той же походки в окне нет — та же походка в любом направлении
        var (policy, infos) = Set(Idle, WalkB, RunF, Start(90));
        Assert.Equal([1], Candidates(policy, infos, 3, out var branch));
        Assert.Equal(GaspBranch.SameGait, branch);

        // циклов своей походки нет — любые циклы хода
        (policy, infos) = Set(Idle, RunF, WalkB, Start(0, "M_Neutral_Transition_Walk_to_Run_Lfoot"), Start(0, "M_Neutral_Walk_Turn_L_090_Lfoot"));
        Assert.Equal([1], Candidates(policy, infos, 3, out branch));          // смена походки — к бегу (походка — целевая)
        Assert.Equal(GaspBranch.SameGaitDirection, branch);
        (policy, infos) = Set(Idle, RunF, Start(0, "M_Neutral_Walk_Turn_L_090_Lfoot"));
        Assert.Equal([1], Candidates(policy, infos, 2, out branch));
        Assert.Equal(GaspBranch.AnyMoving, branch);

        // циклов хода нет (медленный стрейф по кругу — не ход) — стойка
        (policy, infos) = Set(Idle, Circle, Start(0));
        Assert.Equal([0], Candidates(policy, infos, 2, out branch));
        Assert.Equal(GaspBranch.StandFallback, branch);

        // и стойки нет — продолжения нет
        (policy, infos) = Set(Circle, Start(0));
        Assert.Empty(Candidates(policy, infos, 1, out branch));
        Assert.Equal(GaspBranch.None, branch);
    }

    [Fact]
    public void Past_ByMeasuredStartSpeed_NotByName()
    {
        // «Reface_Start» по имени — старт, а входит с 0,45 м/с: «шёл»; старт, начинающий с 0,31 м/с, — тоже «шёл»; 0,29 — «стоял»
        var reface = new Clip("M_Neutral_Run_Reface_Start_F_L_090", false, At(0.45f, 0), At(5f, 0), At(3f, 0));
        var fast = new Clip("M_Neutral_Walk_Start_F_Lfoot", false, At(0.31f, 0), At(2f, 0), At(1f, 0));
        var slow = new Clip("M_Neutral_Walk_Start_F_Rfoot", false, At(0.29f, 0), At(2f, 0), At(1f, 0));
        var pivot = new Clip("M_Neutral_Walk_Pivot_F_B_Lfoot", false, At(2f, 0), At(1.5f, 180), At(0.3f, 0));
        var (policy, infos) = Set(Idle, WalkF, reface, fast, slow, pivot);
        Assert.Equal(MotionPast.StartVelocity, policy.Past(2, infos));
        Assert.Equal(MotionPast.StartVelocity, policy.Past(3, infos));
        Assert.Equal(MotionPast.Standing, policy.Past(4, infos));
        Assert.Equal(MotionPast.StartVelocity, policy.Past(5, infos));
        Assert.Equal((1, 3), (policy.Stats.StandingPast, policy.Stats.MovingPast));
    }

    [Fact]
    public void Continuations_CountsBranches()
    {
        var stop = new Clip("M_Neutral_Walk_Stop_F_Lfoot", false, At(2f, 0), At(0.1f, 0), At(1f, 0));
        var (policy, infos) = Set(Idle, WalkF, stop, Start(0), Start(10));
        var list = new List<int>();
        for (int c = 2; c < 5; c++) policy.Continuations(c, infos, list);
        Assert.Equal(new GaspPolicyStats(1, 2, 0, 0, 0, 0, 0, 0), policy.Stats);
    }

    [Fact]
    public void ClipOutsideSelection_IsError()
    {
        var (policy, infos) = Set(Idle, Start(0));
        var foreign = infos.Append(new MotionClipInfo(7, true, 31, default, default, At(2, 0), 0)).ToArray();
        Assert.Throws<ArgumentException>(() => policy.Choose(1, foreign, []));
        Assert.Throws<ArgumentException>(() => new GaspGraphPolicy([1, 2], [GaspClipName.Parse("M_Neutral_Stand_Idle_Loop")]));
    }
}
