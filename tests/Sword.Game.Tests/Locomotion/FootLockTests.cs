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
/// Фиксация опорной стопы игрока на MM (<see cref="FootLock"/>, <c>locomotion.mm-feet</c>): логика на синтетической стопе и на
/// паке — скольжение опорной стопы по итоговой позе падает на сценариях замеров, стопы не скачут, с <c>footLock = 0</c> поза —
/// анимация.
/// </summary>
public sealed class FootLockTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const float Dt = 1f / 120;
    private static readonly FootLockRules Rules = new(0.2f, 45 * MathF.PI / 180, 0.08f, 0.15f, 0.06f);

    // ── логика на синтетической стопе ───────────────────────────────────────────────────────────────────────────────

    private const float AnkleY = 0.08f, BallY = 0.01f;   // стопа стоя: голеностоп над носком на 7 см

    private static Vector3 At(Vector2 xz, float y) => new(xz.X, y, xz.Y);

    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);

    /// <summary>Шаг фиксации синтетической стопы: колено — над голеностопом (голень отвесна).</summary>
    private static void Step(ref FootLockLeg s, Vector3 ankle, Vector3 ball, bool heel, bool toe, float dt, in FootLockRules rules) =>
        FootLock.Step(ref s, ankle + new Vector3(0, 0.42f, 0), ankle, ball, heel, toe, dt, rules);

    /// <summary>Носок стопы, которую видно после шага: цель голеностопа + стопа анимации, повёрнутая, как её повернёт IK.</summary>
    private static Vector2 VisibleBall(in FootLockLeg s, Vector3 ankle, Vector3 ball)
    {
        var foot = ball - ankle;
        return Xz(ankle) + s.Offset + Xz(Vector3.Transform(foot, FootLock.FootRotation(foot, new Vector3(0, -0.42f, 0), s.YawOffset, s.Pitch)));
    }

    /// <summary>Опора на пятке: стопа анимации едет и поворачивается — цель голеностопа стоит в мире, курс стопы — тот, с которым встала.</summary>
    [Fact]
    public void Planted_HoldsPointAndYaw_WhileAnimationSlides()
    {
        var s = default(FootLockLeg);
        var start = new Vector2(1, 2);
        for (int t = 0; t < 12; t++)
        {
            var ankle = start + new Vector2(0.5f * t * Dt, 0);   // скольжение 0,5 м/с
            float yaw = 0.3f * t * Dt;   // поворот 0,3 рад/с
            var ball = ankle + FootLock.Rotate(new Vector2(0, 0.17f), yaw);
            Step(ref s, At(ankle, AnkleY), At(ball, BallY + 0.05f), heel: true, toe: false, Dt, Rules);
            Assert.True(Vector2.Distance(ankle + s.Offset, start) < 1e-5f, $"тик {t}: цель {ankle + s.Offset}");
            Assert.Equal(0, yaw + s.YawOffset, 5);
        }
        Assert.True(s.Locked);
    }

    /// <summary>На всей стопе: анимация наклоняет стопу и ведёт её — голеностоп и носок стоят (наклон подогнан).</summary>
    [Fact]
    public void Flat_HoldsHeelAndBall_WhileAnimationPitches()
    {
        var s = default(FootLockLeg);
        var heel = new Vector2(0, 0);
        var ballStart = new Vector2(0, 0.155f);
        for (int t = 0; t < 30; t++)
        {
            var ankle = new Vector2(0.005f * t, 0.002f * t);   // в пределе фиксации
            float rise = -0.07f + 0.0015f * t;   // анимация опускает пятку и поднимает носок
            var ball = ankle + new Vector2(0, MathF.Sqrt(0.17f * 0.17f - rise * rise));
            var a3 = At(ankle, AnkleY);
            var b3 = At(ball, AnkleY + rise);
            Step(ref s, a3, b3, heel: true, toe: true, Dt, Rules);
            Assert.True(Vector2.Distance(ankle + s.Offset, heel) < 1e-5f, $"тик {t}: голеностоп {ankle + s.Offset}");
            if (t == 0) ballStart = VisibleBall(s, a3, b3);
            Assert.True(Vector2.Distance(VisibleBall(s, a3, b3), ballStart) < 1e-4f, $"тик {t}: носок {VisibleBall(s, a3, b3)}");
        }
    }

    /// <summary>Дальше предела точка опоры тянется за анимацией: смещение не больше предела, стопа едет со скоростью остатка.</summary>
    [Fact]
    public void BeyondLimit_PointFollows_OffsetClamped()
    {
        var s = default(FootLockLeg);
        Vector2 last = default;
        for (int t = 0; t < 120; t++)
        {
            var ankle = new Vector2(3f * t * Dt, 0);   // 3 м/с — предел 0,2 м пройден за 0,07 с
            Step(ref s, At(ankle, AnkleY), At(ankle + new Vector2(0, 0.155f), BallY), true, true, Dt, Rules);
            Assert.True(s.Offset.Length() <= Rules.MaxOffset + 1e-5f);
            var target = ankle + s.Offset;
            if (t > 0) Assert.True(Vector2.Distance(target, last) <= 3f * Dt + 1e-5f, $"тик {t}: шаг цели {Vector2.Distance(target, last)}");
            last = target;
        }
        Assert.Equal(Rules.MaxOffset, s.Offset.Length(), 4);
    }

    /// <summary>Пятка поднялась — опора на носке: носок стоит, стопа поворачивается вокруг него, голеностоп не прыгает.</summary>
    [Fact]
    public void HeelOff_PivotsOnBall_NoJump()
    {
        var s = default(FootLockLeg);
        var ankle = At(Vector2.Zero, AnkleY);
        var ball = At(new Vector2(0, 0.155f), BallY);
        Step(ref s, ankle, ball, true, true, Dt, Rules);
        var ballWorld = VisibleBall(s, ankle, ball);
        var lastTarget = Xz(ankle) + s.Offset;
        for (int t = 1; t < 20; t++)
        {
            // Анимация ведёт стопу вперёд (носок тоже) и поднимает пятку.
            float lift = 0.004f * t;
            ankle = At(new Vector2(0, 0.01f * t), AnkleY + lift);
            ball = At(new Vector2(0, 0.01f * t + MathF.Sqrt(0.17f * 0.17f - (0.07f + lift) * (0.07f + lift))), BallY);
            Step(ref s, ankle, ball, heel: false, toe: true, Dt, Rules);
            var target = Xz(ankle) + s.Offset;
            Assert.True(Vector2.Distance(VisibleBall(s, ankle, ball), ballWorld) < 1e-4f, $"тик {t}: носок {VisibleBall(s, ankle, ball)}");
            Assert.True(Vector2.Distance(target, lastTarget) < 0.02f);
            lastTarget = target;
        }
    }

    /// <summary>Опора кончилась — смещения гаснут непрерывно (без скачка) и становятся ровно нулём; предел 0 — опоры нет.</summary>
    [Fact]
    public void Released_DecaysContinuously_ToExactZero_ZeroLimitNeverLocks()
    {
        var s = default(FootLockLeg);
        for (int t = 0; t < 30; t++)
        {
            var ankle = new Vector2(2f * t * Dt, 0);
            float rise = -0.07f + 0.002f * t;
            Step(ref s, At(ankle, AnkleY), At(ankle + new Vector2(0, MathF.Sqrt(0.17f * 0.17f - rise * rise)), AnkleY + rise), true, true, Dt, Rules);
        }
        Assert.True(s.Offset.Length() > 0.1f && s.Pitch != 0);
        float worst = 0, worstPitch = 0;
        int ticks = 0;
        var still = At(default, AnkleY);
        var stillBall = At(new Vector2(0, 0.155f), BallY);
        while (!s.Idle && ticks < 1000)
        {
            var before = s;
            Step(ref s, still, stillBall, false, false, Dt, Rules);
            worst = MathF.Max(worst, Vector2.Distance(before.Offset, s.Offset));
            worstPitch = MathF.Max(worstPitch, MathF.Abs(before.Pitch - s.Pitch));
            ticks++;
        }
        Assert.True(s.Idle && !s.Locked, $"не погасло за {ticks} тиков");
        Assert.True(worst < 2.5f * Dt, $"наибольший шаг смещения {worst} м за тик");
        Assert.True(worstPitch < 0.02f, $"наибольший шаг наклона {worstPitch} рад за тик");
        Assert.True(ticks * Dt < 1, $"гасло {ticks * Dt} с");

        var off = default(FootLockLeg);
        Step(ref off, still, stillBall, true, true, Dt, Rules with { MaxOffset = 0 });
        Assert.True(off.Idle && !off.Locked);
    }

    /// <summary>Поворот стопы для IK: курс — вокруг вертикали, наклон — носок вверх на заданный угол, длина стопы та же; носком вниз — без скачка.</summary>
    [Fact]
    public void FootRotation_YawThenPitch()
    {
        var foot = new Vector3(0, -0.07f, 0.155f);
        var shin = new Vector3(0, -0.42f, -0.03f);
        var q = FootLock.FootRotation(foot, shin, 0.5f, 0.2f);
        var r = Vector3.Transform(foot, q);
        float before = MathF.Atan2(foot.Y, new Vector2(foot.X, foot.Z).Length()), after = MathF.Atan2(r.Y, new Vector2(r.X, r.Z).Length());
        Assert.Equal(0.2f, after - before, 2);
        Assert.Equal(0.5f, MathF.Atan2(r.X, r.Z), 2);
        Assert.Equal(foot.Length(), r.Length(), 5);
        Assert.Equal(Quaternion.Identity, FootLock.FootRotation(foot, shin, 0, 0));
        var down = new Vector3(0, -0.17f, 0.001f);   // стопа носком вниз (перенос): наклон без скачка оси
        var a1 = Vector3.Transform(down, FootLock.FootRotation(down, shin, 0, 0.2f));
        var a2 = Vector3.Transform(down with { Z = -0.001f }, FootLock.FootRotation(down with { Z = -0.001f }, shin, 0, 0.2f));
        Assert.True(Vector3.Distance(a1, a2) < 0.005f, $"{a1} против {a2}");
    }

    // ── на паке ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Animation.CharacterAssets Assets => TestContent.Reference.Value.Assets;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), Assets);

    private readonly record struct Run(double Mean, double P95, double SourceMean, double SourceP95, double StepP99, double StepMax, int Samples);

    /// <summary>Прогон 12 с: скольжение опорной стопы (итог и выход проигрывателя) и шаг стоп и коленей в мире за тик.</summary>
    private static Run Measure(Func<int, PlayerCommand> command, params string[] sets)
    {
        var sim = Create(sets);
        var view = sim.World.View<IMotionMatchView>();
        var anim = sim.World.View<IAnimationView>();
        var player = GameCore.Player(sim.World);
        // Стопы (суставы контакта) и колени: правка IK видна на них первой.
        int[] feet = [.. Enumerable.Range(0, view.ContactJointCount).Select(view.ContactJoint), anim.FindJoint(FootLockLegs.LeftJoints[1]),
            anim.FindJoint(FootLockLegs.RightJoints[1])];
        var slides = new List<double>();
        var sources = new List<double>();
        var steps = new List<double>();
        var prev = new Vector3[feet.Length];
        for (int t = 0; t < 1440; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(command(t));
            sim.Step();
            var root = sim.World.Get<Transform>(player);
            var model = anim.Model(sim.World.Get<PoseRef>(player));
            var frame = new Pose2(root.Position, root.Facing);
            for (int k = 0; k < feet.Length; k++)
            {
                var w = frame.ToParent(model[feet[k]].Translation);
                if (t > 120) steps.Add(Vector3.Distance(w, prev[k]));
                prev[k] = w;
            }
            if (t <= 120) continue;
            var m = view.Measure(player);
            if (float.IsFinite(m.FootSlide)) slides.Add(m.FootSlide);
            if (float.IsFinite(m.SourceSlide)) sources.Add(m.SourceSlide);
        }
        Assert.True(sim.Invariants.Total == 0,
            string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));
        slides.Sort();
        sources.Sort();
        steps.Sort();
        static double P(List<double> v, double p) => v[Math.Clamp((int)Math.Ceiling(p * v.Count), 1, v.Count) - 1];
        return new Run(slides.Average(), P(slides, 0.95), sources.Average(), P(sources, 0.95), P(steps, 0.99), steps[^1], slides.Count);
    }

    /// <summary>
    /// Сценарии замеров (12 с): скольжение опорной стопы по итоговой позе с фиксацией — меньше, чем без неё (<c>footLock = 0</c>; в каждом прогоне на 20 %, в среднем на 35 %)
    /// и чем у выхода проигрывателя; без фиксации и действий итог = выход (замер по итоговой позе верен); шаг стоп и коленей в мире за тик не растёт — новых
    /// скачков нет. Таблица — в вывод (README модуля, «Замеры»).
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void Scenarios_SlideDrops_NoNewPops()
    {
        double meanOn = 0, meanOff = 0;
        foreach (var (name, command) in MotionMatchViewTests.Scenarios)
        {
            var on = Measure(command);
            var off = Measure(command, "locomotion.footLock=0");
            output.WriteLine(string.Create(Inv,
                $"{name}: скольжение итог {on.Mean:F3} / p95 {on.P95:F3} м/с; без фиксации {off.Mean:F3} / {off.P95:F3}; выход проигрывателя {on.SourceMean:F3} / {on.SourceP95:F3}; " +
                $"шаг стоп и коленей за тик p99 {on.StepP99:F4} макс {on.StepMax:F4} м (без фиксации {off.StepP99:F4} / {off.StepMax:F4})"));
            if (!name.Contains("удар", StringComparison.Ordinal))
                Assert.Equal(off.SourceMean, off.Mean, 2);   // без фиксации и действий ноги итоговой позы — выход проигрывателя
            Assert.True(on.Mean < 0.8 * off.Mean, $"{name}: скольжение {on.Mean:F3} против {off.Mean:F3} без фиксации");
            Assert.True(on.StepMax < off.StepMax * 1.15 + 0.005, $"{name}: шаг стопы за тик {on.StepMax:F4} против {off.StepMax:F4}");
            meanOn += on.Mean;
            meanOff += off.Mean;
        }
        output.WriteLine(string.Create(Inv, $"среднее по сценариям: {meanOn / MotionMatchViewTests.Scenarios.Length:F3} против {meanOff / MotionMatchViewTests.Scenarios.Length:F3} м/с"));
        // Предел удержания 0,1 м (не 0,2: на малом круге ноги перекрещивались) — скольжение в среднем меньше хотя бы на 35 %.
        Assert.True(meanOn < 0.65 * meanOff, $"скольжение в среднем {meanOn:F3} против {meanOff:F3} без фиксации");
    }

    /// <summary>Стоя на месте и на беге по прямой: IK держит длины бедра и голени (± 1 мм) — нога не тянется и не сжимается.</summary>
    [Fact]
    [Trait("Category", "Content")]
    public void LegIk_KeepsBoneLengths_OnPack()
    {
        var sim = Create();
        var anim = sim.World.View<IAnimationView>();
        var player = GameCore.Player(sim.World);
        string[][] chains = [[.. FootLockLegs.LeftJoints.Take(3)], [.. FootLockLegs.RightJoints.Take(3)]];
        var reference = anim.ReferenceModel();
        int checkedTicks = 0;
        for (int t = 0; t < 600; t++)
        {
            var move = t < 120 ? Vector2.Zero : new Vector2(MathF.Sin(t * 0.01f), 1);
            sim.Export.BeginFrame();
            sim.Enqueue(new PlayerCommand { Move = move, HasGaze = true, Gaze = new Vector3(0, 0, 200) });
            sim.Step();
            if (State(sim).Feet.Left.Idle && State(sim).Feet.Right.Idle) continue;
            checkedTicks++;
            var model = anim.Model(sim.World.Get<PoseRef>(player));
            foreach (var chain in chains)
            {
                int hip = anim.FindJoint(chain[0]), knee = anim.FindJoint(chain[1]), ankle = anim.FindJoint(chain[2]);
                Assert.Equal(Vector3.Distance(reference[hip].Translation, reference[knee].Translation), Vector3.Distance(model[hip].Translation, model[knee].Translation), 3);
                Assert.Equal(Vector3.Distance(reference[knee].Translation, reference[ankle].Translation), Vector3.Distance(model[knee].Translation, model[ankle].Translation), 3);
            }
        }
        Assert.True(checkedTicks > 300, $"тиков с IK {checkedTicks}");
    }

    private static MotionMatchState State(Simulation sim) => sim.World.Get<MotionMatchState>(GameCore.Player(sim.World));
}
