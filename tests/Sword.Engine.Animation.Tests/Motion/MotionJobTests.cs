using System.Numerics;
using Sword.Engine.Animation.Motion;
using static Sword.Engine.Animation.Tests.MotionPlayerFixtures;

namespace Sword.Engine.Animation.Tests;

/// <summary>Задание пула <see cref="PosePool.Motion"/>: граф, затухание смещений, сэмпл кадра, переход с инерциализацией.</summary>
public sealed class MotionJobTests
{
    /// <summary>
    /// Запас к обычному изменению позы за тик в тике перехода и в следующем (рад, м): там скорость выхода непрерывна, остаётся второй
    /// порядок затухания — <c>(y·Dt)²/2·|смещение| + y·Dt·|Δv|·Dt</c> (y = 2·ln2/полупериод): при разрыве ~1 рад и разнице скоростей
    /// ~5 рад/с — 0,006 рад.
    /// </summary>
    private const float JumpRot = 0.015f, JumpPos = 0.002f;

    /// <summary>Кадров клипа в секунду игры у обычного тика (<see cref="PlayerRig.Tick"/>) — те же операции float, что в задании.</summary>
    private static float TickScale(MotionDatabase db) => db.SampleRate * (Dt * Tempo * db.SampleRate / db.SampleRate / Dt);

    [Fact]
    public void NotStarted_WithoutTransitionNothing_FirstTransitionSetsFrameWithoutInertialization()
    {
        var db = Database(Library(), exit: true);
        var rig = new PlayerRig(db);
        var reference = rig.Rot;
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 0, 1), rig.Step(1));
        Assert.Equal(new MotionPlayerState(-1, 0, 0, false, Pose2.Identity), rig.State);
        Assert.Equal(reference, rig.Rot);

        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 0, 1), rig.Tick(Walk + 7));
        Assert.Equal(new MotionPlayerState(Walk + 7, 0, 0, true, Pose2.Identity), rig.State);
        var (r7, p7) = FramePose(db, Walk + 7);
        var (r8, p8) = FramePose(db, Walk + 8);
        Assert.Equal(Bits<Quaternion>(r7), Bits<Quaternion>(rig.Rot));   // выход — сам кадр, побитово
        Assert.Equal(Bits<Vector3>(p7), Bits<Vector3>(rig.Pos));
        Assert.Equal(r7, rig.Players.OutputRotations(rig.Player).ToArray());
        Assert.All(rig.Players.RotationOffsets(rig.Player).ToArray(), q => Assert.Equal(Quaternion.Identity, q));
        Assert.All(rig.Players.PositionOffsets(rig.Player).ToArray(), v => Assert.Equal(Vector3.Zero, v));
        // скорости выхода первого шага — клипа в этом кадре, во времени игры (темп — Advance/Dt)
        float scale = TickScale(db);
        for (int j = 0; j < r7.Length; j++)
        {
            Assert.True(Vector3.Distance(AnimMath.RotationVector(r7[j], r8[j]) * scale, rig.Players.AngularVelocities(rig.Player)[j]) < 1e-4f);
            Assert.True(Vector3.Distance((p8[j] - p7[j]) * scale, rig.Players.LinearVelocities(rig.Player)[j]) < 1e-5f);
        }

        // переход в кадр выхода — это его вход
        var exit = new PlayerRig(db);
        exit.Tick(Exit);
        Assert.Equal(Entry, exit.State.Frame);
    }

    /// <summary>
    /// Запрошенный переход: выход этого тика — тот же, что без перехода (побитово); кадр — вход с долей 0; смещение = выход ∘
    /// сопряжённый кадр входа (повороты — все суставы, позиции — только движущиеся), скорость смещения = скорость выхода − скорость
    /// клипа во входе.
    /// </summary>
    [Fact]
    public void RequestedTransition_ThisTickOutputUnchanged_OffsetsTakeTheGap()
    {
        var db = Database(Library());
        var a = new PlayerRig(db);
        var b = new PlayerRig(db);
        a.Tick(Walk + 3);
        b.Tick(Walk + 3);
        for (int t = 0; t < 20; t++)
        {
            a.Tick();
            b.Tick();
        }
        const int target = Idle + 11;
        a.Tick(target);
        b.Tick();
        Assert.Equal(Bits<Quaternion>(b.Rot), Bits<Quaternion>(a.Rot));
        Assert.Equal(Bits<Vector3>(b.Pos), Bits<Vector3>(a.Pos));
        Assert.Equal(new MotionPlayerState(target, 0, 1, true, b.State.RootStep), a.State);

        var (rd, pd) = FramePose(db, target);
        var (rn, pn) = FramePose(db, target + 1);
        float scale = TickScale(db), gap = 0;
        var output = a.Rot;
        var outPos = a.Pos;
        var off = a.Players.RotationOffsets(a.Player).ToArray();
        var offVel = a.Players.RotationOffsetVelocities(a.Player).ToArray();
        var posOff = a.Players.PositionOffsets(a.Player).ToArray();
        var posOffVel = a.Players.PositionOffsetVelocities(a.Player).ToArray();
        var angular = a.Players.AngularVelocities(a.Player).ToArray();
        var linear = a.Players.LinearVelocities(a.Player).ToArray();
        for (int j = 0; j < output.Length; j++)
        {
            Assert.True(Angle(Quaternion.Normalize(off[j] * rd[j]), output[j]) < 1e-5f, $"сустав {j}: смещение · вход ≠ выход");
            Assert.True(off[j].W >= 0);
            Assert.True(Vector3.Distance(angular[j] - AnimMath.RotationVector(rd[j], rn[j]) * scale, offVel[j]) < 1e-5f);
            gap = MathF.Max(gap, Angle(output[j], rd[j]));
        }
        foreach (int j in db.MovingJoints)
        {
            Assert.True(Vector3.Distance(pd[j] + posOff[j], outPos[j]) < 1e-6f);
            Assert.True(Vector3.Distance(linear[j] - (pn[j] - pd[j]) * scale, posOffVel[j]) < 1e-5f);
        }
        Assert.Equal(Vector3.Zero, posOff[MotionFixtures.Spine]);   // не движущийся — без смещения позиции
        Assert.Equal(Vector3.Zero, posOffVel[MotionFixtures.Spine]);
        Assert.True(gap > 0.3f, $"разрыв перехода {gap} рад — тест ничего бы не проверял");
    }

    /// <summary>Обычное изменение позы за тик: каждый клип проигрывается без переходов (темп и тик — как в тестах скачка).</summary>
    private static (float Rot, float Pos) Ordinary(MotionDatabase plain)
    {
        float rot = 0, pos = 0;
        foreach (int start in new[] { Walk, Stop, Idle })
        {
            var rig = new PlayerRig(plain);
            rig.Tick(start);
            var (r, p) = (rig.Rot, rig.Pos);
            for (int t = 0; t < 300; t++)
            {
                rig.Tick();
                var c = MaxChange(r, p, rig.Rot, rig.Pos);
                (rot, pos) = (MathF.Max(rot, c.Rot), MathF.Max(pos, c.Pos));
                (r, p) = (rig.Rot, rig.Pos);
            }
            Assert.Equal(0, rig.State.Transitions);
        }
        return (rot, pos);
    }

    /// <summary>
    /// Скачок позы в тике перехода (запрошенного и по ребру) и после — не больше обычного изменения за тик +
    /// <see cref="JumpRot"/>/<see cref="JumpPos"/>, хотя разрыв поз источника и входа — десятые доли радиана.
    /// </summary>
    [Fact]
    public void Jump_AtRequestedTransitionAndGraphEdge_WithinOrdinaryTickChange()
    {
        var lib = Library();
        var (ordinaryRot, ordinaryPos) = Ordinary(Database(lib));
        var db = Database(lib, exit: true);
        var rig = new PlayerRig(db);
        rig.Tick(Walk);
        int[] targets = [Idle + 9, Stop + 3, Walk + 17, Idle + 25, Stop + 8, Walk + 2, Idle + 1, Walk + 11];
        var (r, p) = (rig.Rot, rig.Pos);
        float gap = 0, worstRot = 0, worstPos = 0;
        int requested = 0, edges = 0, after = int.MaxValue;
        for (int t = 0, k = 0; t < 40 * targets.Length; t++)
        {
            int transition = t % 40 == 39 ? targets[k++] : -1;
            int before = rig.State.Transitions;
            rig.Tick(transition);
            bool edge = rig.State.Transitions - before > (transition >= 0 ? 1 : 0);
            if (transition >= 0)
            {
                requested++;
                var (rd, pd) = FramePose(db, rig.State.Frame);
                gap = MathF.Max(gap, MaxChange(rig.Rot, rig.Pos, rd, pd).Rot);
            }
            if (edge) edges++;
            after = transition >= 0 || edge ? 0 : after == int.MaxValue ? after : after + 1;
            var c = MaxChange(r, p, rig.Rot, rig.Pos);
            if (after <= 1)
            {
                worstRot = MathF.Max(worstRot, c.Rot);
                worstPos = MathF.Max(worstPos, c.Pos);
            }
            (r, p) = (rig.Rot, rig.Pos);
        }
        Assert.Equal(targets.Length, requested);
        Assert.True(edges >= 1, "ребро выхода не пройдено — тест его не проверил бы");
        var (exitRot, exitPos) = FramePose(db, Exit);
        var (entryRot, entryPos) = FramePose(db, Entry);
        Assert.True(MaxChange(exitRot, exitPos, entryRot, entryPos).Rot > 0.3f, "разрыв на ребре мал — тест ничего бы не проверял");
        Assert.True(gap > 0.3f, $"разрыв переходов {gap} рад — тест ничего бы не проверял");
        Assert.True(worstRot <= ordinaryRot + JumpRot, $"поворот: скачок {worstRot} рад, обычный тик {ordinaryRot} рад");
        Assert.True(worstPos <= ordinaryPos + JumpPos, $"смещение: скачок {worstPos} м, обычный тик {ordinaryPos} м");
    }

    /// <summary>
    /// Смещения гаснут пружиной <see cref="AnimMath.DecaySpring(ref Quaternion, ref Vector3, float, float)"/> (побитово, шаг за шагом) без
    /// выброса — |x(t)| ≤ |x₀| + |v₀|/(e·y), |v(t)| ≤ |v₀| + |j₁|/e; за 12 полупериодов — почти до нуля, хвост обнуляется ровно.
    /// </summary>
    [Fact]
    public void Offsets_DecayLikeDecaySpring_VanishWithoutOvershoot()
    {
        var db = Database(Library());
        var rig = new PlayerRig(db);
        rig.Tick(Stop + 2);
        for (int t = 0; t < 10; t++) rig.Tick();
        rig.Tick(Idle + 20);   // большой разрыв; цикл — дальше без рёбер
        var players = rig.Players;
        var h = rig.Player;
        int jc = players.JointCount;
        float y = AnimMath.HalflifeToDamping(Halflife) * 0.5f;
        var boundX = new float[jc];
        var boundV = new float[jc];
        var x0 = players.RotationOffsets(h).ToArray();
        var v0 = players.RotationOffsetVelocities(h).ToArray();
        float largest = 0;
        for (int j = 0; j < jc; j++)
        {
            var r = AnimMath.ToRotationVector(x0[j]);
            largest = MathF.Max(largest, r.Length());
            boundX[j] = r.Length() + v0[j].Length() / (MathF.E * y) + 1e-5f;
            boundV[j] = v0[j].Length() + (v0[j] + r * y).Length() / MathF.E + 1e-4f;
        }
        Assert.True(largest > 0.3f, $"смещение {largest} рад — тест ничего бы не проверял");
        int ticks12 = (int)MathF.Ceiling(12 * Halflife / Dt);
        for (int t = 1; t <= 1000; t++)
        {
            var q = players.RotationOffsets(h).ToArray();
            var v = players.RotationOffsetVelocities(h).ToArray();
            var px = players.PositionOffsets(h).ToArray();
            var pv = players.PositionOffsetVelocities(h).ToArray();
            rig.Tick();
            var off = players.RotationOffsets(h);
            var vel = players.RotationOffsetVelocities(h);
            for (int j = 0; j < jc; j++)
            {
                var eq = q[j];
                var ev = v[j];
                AnimMath.DecaySpring(ref eq, ref ev, Halflife, Dt);
                if (off[j] == Quaternion.Identity && vel[j] == Vector3.Zero)
                    Assert.True(AnimMath.ToRotationVector(eq).Length() < 1e-6f && ev.Length() < 1e-6f, $"сустав {j}: обнулено не малое");
                else
                {
                    Assert.Equal(eq, off[j]);
                    Assert.Equal(ev, vel[j]);
                }
                Assert.True(AnimMath.ToRotationVector(off[j]).Length() <= boundX[j], $"тик {t}, сустав {j}: выброс смещения");
                Assert.True(vel[j].Length() <= boundV[j], $"тик {t}, сустав {j}: выброс скорости смещения");
            }
            foreach (int j in db.MovingJoints)
            {
                var ex = px[j];
                var ev = pv[j];
                AnimMath.DecaySpring(ref ex, ref ev, Halflife, Dt);
                if (players.PositionOffsets(h)[j] != Vector3.Zero || players.PositionOffsetVelocities(h)[j] != Vector3.Zero)
                {
                    Assert.Equal(ex, players.PositionOffsets(h)[j]);
                    Assert.Equal(ev, players.PositionOffsetVelocities(h)[j]);
                }
            }
            if (t == ticks12)
                for (int j = 0; j < jc; j++)
                {
                    Assert.True(AnimMath.ToRotationVector(off[j]).Length() < 1e-4f, $"сустав {j}: за 12 полупериодов не погасло");
                    Assert.True(vel[j].Length() < 1e-3f);
                    Assert.True(players.PositionOffsets(h)[j].Length() < 1e-5f);
                }
        }
        Assert.All(players.RotationOffsets(h).ToArray(), q => Assert.Equal(Quaternion.Identity, q));   // хвост обнулён ровно
        Assert.All(players.RotationOffsetVelocities(h).ToArray(), v => Assert.Equal(Vector3.Zero, v));
        Assert.All(players.PositionOffsets(h).ToArray(), v => Assert.Equal(Vector3.Zero, v));
        Assert.Equal(1, rig.State.Transitions);
    }

    /// <summary>Продвижение на a + b — тот же кадр, доля, переходы и перенос корня, что на a, затем на b (в пределах float): через рёбра и швы.</summary>
    [Fact]
    public void Advance_APlusB_SameAsAThenB()
    {
        var db = Database(Library(), exit: true);
        var rng = new Random(3);
        int checkedCases = 0;
        for (int c = 0; c < 300; c++)
        {
            int start = rng.Next(db.FrameCount);
            float pre = (float)rng.NextDouble(), a = (float)(rng.NextDouble() * 3), b = (float)(rng.NextDouble() * 3);
            var one = new PlayerRig(db);
            var two = new PlayerRig(db);
            one.Step(0, start);
            two.Step(0, start);
            one.Step(pre);
            two.Step(pre);
            one.Step(a + b);
            two.Step(a);
            var rootA = two.State.RootStep;
            two.Step(b);
            var (s1, s2) = (one.State, two.State);
            if (s1.Alpha is < 1e-3f or > 1 - 1e-3f) continue;   // у границы интервала округление float решает, какой кадр
            checkedCases++;
            Assert.Equal(s1.Frame, s2.Frame);
            Assert.Equal(s1.Alpha, s2.Alpha, 1e-4f);
            Assert.Equal(s1.Transitions, s2.Transitions);
            Near(s1.RootStep, Pose2.Compose(rootA, s2.RootStep), 1e-4f, $"случай {c}");
        }
        Assert.True(checkedCases > 200, $"проверено {checkedCases} случаев");
    }

    /// <summary>Через кадр выхода — вход в продолжение ровно один раз, доля сохраняется; на кадре выхода проигрыватель не стоит.</summary>
    [Fact]
    public void ExitFrame_EntersContinuationExactlyOnce_AlphaKept()
    {
        var db = Database(Library(), exit: true);
        var rig = new PlayerRig(db);
        rig.Step(0, Exit - 1);
        rig.Step(0.5f);
        Assert.Equal((Exit - 1, 0), (rig.State.Frame, rig.State.Transitions));
        rig.Step(0.8f);
        var s = rig.State;
        Assert.Equal((Entry, 1), (s.Frame, s.Transitions));
        Assert.Equal(0.3f, s.Alpha, 1e-5f);
        var before = db.Interval[Exit - 1];
        Near(Pose2.Compose(Pose2.Relative(Pose2.Lerp(before, 0.5f), before), Pose2.Lerp(db.Interval[Entry], s.Alpha)), s.RootStep, 1e-5f);

        var walk = new PlayerRig(db);
        walk.Tick(Stop);
        for (int t = 0; t < 400; t++)
        {
            walk.Tick();
            Assert.NotEqual(Exit, walk.State.Frame);
        }
        Assert.Equal(1, walk.State.Transitions);
        Assert.InRange(walk.State.Frame, Idle, IdleLast);
    }

    /// <summary>Ребро графа и запрошенный переход в одном тике: оба — переходы; выход тика — с ребром (как без запроса), кадр — запрошенный.</summary>
    [Fact]
    public void EdgeAndRequestedTransitionInOneTick_BothCount()
    {
        var db = Database(Library(), exit: true);
        var a = new PlayerRig(db);
        var b = new PlayerRig(db);
        foreach (var rig in new[] { a, b })
        {
            rig.Step(0, Exit - 1);
            rig.Step(0.7f);
        }
        a.Step(0.6f, Walk + 5);
        b.Step(0.6f);
        Assert.Equal(Bits<Quaternion>(b.Rot), Bits<Quaternion>(a.Rot));
        Assert.Equal(Bits<Vector3>(b.Pos), Bits<Vector3>(a.Pos));
        Assert.Equal((Entry, 1), (b.State.Frame, b.State.Transitions));
        Assert.Equal(0.3f, b.State.Alpha, 1e-5f);
        Assert.Equal(new MotionPlayerState(Walk + 5, 0, 2, true, b.State.RootStep), a.State);
    }

    /// <summary>Цикл идёт по кругу: шов последнего кадра на второй — не переход; полный оборот — перенос корня клипа за оборот.</summary>
    [Fact]
    public void Loop_GoesAround_SeamIsNoTransition()
    {
        var lib = Library();
        var db = Database(lib);
        var rig = new PlayerRig(db);
        rig.Step(0, Walk);
        rig.Step(24);
        Assert.Equal(new MotionPlayerState(Walk + 24, 0, 0, true, rig.State.RootStep), rig.State);
        Near(Pose2.Relative(MotionFixtures.Frame(lib, lib.Clips[0].Start), MotionFixtures.Frame(lib, lib.Clips[0].Start + 24)), rig.State.RootStep,
            1e-4f, "оборот цикла");
        Assert.Equal(Walk, rig.Players.NearestFrame(rig.Player));
        rig.Step(1);
        Assert.Equal((Walk + 1, 0f, 0), (rig.State.Frame, rig.State.Alpha, rig.State.Transitions));
        int wraps = 0, last = rig.State.Frame;
        for (int t = 0; t < 3000; t++)
        {
            rig.Tick();
            Assert.InRange(rig.State.Frame, Walk, Walk + 24);
            if (rig.State.Frame < last) wraps++;
            last = rig.State.Frame;
        }
        Assert.Equal(0, rig.State.Transitions);
        Assert.True(wraps >= 30, $"оборотов {wraps}");
    }

    [Fact]
    public void FreedPlayer_JobIsStale_BadStepsRejected()
    {
        var db = Database(Library());
        var rig = new PlayerRig(db);
        rig.Tick(Walk + 2);
        var ok = new MotionStep(0.01f, Dt, -1, Halflife);
        var before = rig.Rot;
        rig.Pool.Motion(rig.Pose, rig.Players, rig.Player, ok);
        rig.Players.Free(rig.Player);   // освобождён до Tick — задание пропускается
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 1, 0), rig.Pool.Tick());
        Assert.Equal(before, rig.Rot);
        Assert.Throws<InvalidOperationException>(() => rig.Pool.Motion(rig.Pose, rig.Players, rig.Player, ok));

        var p = rig.Players.Alloc();
        var pose = rig.Pool.Alloc();
        rig.Pool.Motion(pose, rig.Players, p, ok with { Transition = 3 });
        rig.Pool.Free(pose);   // поза освобождена до Tick — тоже пропуск
        Assert.Equal(new PoseTickStats(0, 0, 0, 0, 1, 0), rig.Pool.Tick());
        Assert.Equal(-1, rig.Players.State(p).Frame);

        void Motion(MotionStep step) => rig.Pool.Motion(rig.Pose, rig.Players, p, step);
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Advance = -0.01f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Advance = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Advance = float.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Dt = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Halflife = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Transition = -2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Motion(ok with { Transition = db.FrameCount }));
        var other = new MotionPlayers(Database(Library()));   // другой экземпляр библиотеки
        Assert.Throws<ArgumentException>(() => rig.Pool.Motion(rig.Pose, other, other.Alloc(), ok));
        Assert.Throws<ArgumentNullException>(() => rig.Pool.Motion(rig.Pose, null!, p, ok));
        Assert.Equal(0, rig.Pool.Pending);
    }

    /// <summary>Задание MM — в общей очереди по порядку: копия видит выход MM, смешивание — поверх; два задания за тик — как два тика.</summary>
    [Fact]
    public void Motion_InSharedQueueOrder_TwoJobsAreTwoSteps()
    {
        var db = Database(Library());
        var rig = new PlayerRig(db);
        var copy = rig.Pool.Alloc();
        var aux = rig.Pool.Alloc();
        rig.Tick(Walk + 4);
        var step = new MotionStep(Dt * Tempo, Dt, -1, Halflife);
        rig.Pool.Sample(aux, 2, 0.3f);
        rig.Pool.Motion(rig.Pose, rig.Players, rig.Player, step);
        rig.Pool.Copy(copy, rig.Pose);
        rig.Pool.Blend(rig.Pose, aux, 0.5f);
        Assert.Equal(new PoseTickStats(1, 1, 1, 0, 0, 1), rig.Pool.Tick());
        var outRot = rig.Players.OutputRotations(rig.Player).ToArray();
        var outPos = rig.Players.OutputPositions(rig.Player).ToArray();
        Assert.Equal(outRot, rig.Pool.LocalRot(copy).ToArray());
        Assert.Equal(outPos, rig.Pool.LocalPos(copy).ToArray());
        ClipLibrary.Blend(outRot, outPos, rig.Pool.LocalRot(aux), rig.Pool.LocalPos(aux), 0.5f);
        Assert.Equal(outRot, rig.Pool.LocalRot(rig.Pose).ToArray());
        Assert.Equal(outPos, rig.Pool.LocalPos(rig.Pose).ToArray());

        var one = new PlayerRig(db);
        var two = new PlayerRig(db);
        one.Tick(Walk + 4);
        two.Tick(Walk + 4);
        var jump = step with { Transition = Idle + 3 };
        one.Pool.Motion(one.Pose, one.Players, one.Player, step);
        one.Pool.Motion(one.Pose, one.Players, one.Player, jump);
        Assert.Equal(2, one.Pool.Tick().Motions);
        two.Pool.Motion(two.Pose, two.Players, two.Player, step);
        two.Pool.Tick();
        two.Pool.Motion(two.Pose, two.Players, two.Player, jump);
        two.Pool.Tick();
        AssertSame(one.Pool, one.Players, two.Pool, two.Players);
        Assert.Equal(Bits<Quaternion>(two.Rot), Bits<Quaternion>(one.Rot));
    }

    /// <summary>
    /// После запаса пула (<see cref="PosePool.Reserve"/>) и хранилища (<see cref="MotionPlayers.Reserve"/>) тик с переходами —
    /// 0 Б с первого шага; без запаса — выделяет.
    /// </summary>
    [Fact]
    public void SteadyTick_WithTransitions_AfterReserve_DoesNotAllocate()
    {
        var db = Database(Library(), exit: true);
        Scenario(db, reserve: false);   // прогрев: JIT всех путей задания
        Assert.True(Scenario(db, reserve: false) > 0, "без запаса выделений нет — тест ничего не проверял бы");
        Assert.Equal(0, Scenario(db, reserve: true));
    }

    private static long Scenario(MotionDatabase db, bool reserve)
    {
        const int count = 6;
        var pool = new PosePool(db.Library);
        var players = new MotionPlayers(db);
        var aux = pool.Alloc();
        if (reserve)
        {
            pool.Reserve(count + 1, 4 * count);
            players.Reserve(count);
        }
        var poses = new PoseHandle[count];
        var handles = new MotionPlayerHandle[count];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
        {
            poses[i] = pool.Alloc();
            handles[i] = players.Alloc();
        }
        for (int t = 0; t < 600; t++)
        {
            for (int i = 0; i < count; i++)
            {
                int transition = t == 0 ? Stop + 2 * i : (t + 7 * i) % 29 == 0 ? (t * 13 + i * 5) % db.FrameCount : -1;
                pool.Motion(poses[i], players, handles[i], new MotionStep(Dt * (1 + 0.1f * i), Dt, transition, Halflife));
                if (i == 0 && t % 5 == 0) pool.Motion(poses[i], players, handles[i], new MotionStep(Dt, Dt, -1, Halflife));
            }
            pool.Sample(aux, 1, t * 0.01f);
            pool.Blend(poses[1], aux, 0.3f);
            pool.ToModel(poses[2]);
            pool.Tick();
            if (t == 300)
            {
                players.Free(handles[3]);
                handles[3] = players.Alloc();   // слот тот же — без выделений
            }
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Случайные шаги, переходы и освобождение посреди очереди; сравниваются позы, состояние и данные суставов.</summary>
    [Fact]
    public void TwoPools_SameJobs_BitwiseSameResult()
    {
        var lib = Library();
        var db = Database(lib, exit: true);
        var a = new World(db, seed: 11);
        var b = new World(db, seed: 11);
        for (int t = 0; t < 800; t++)
        {
            Assert.Equal(a.Tick(), b.Tick());
            for (int i = 0; i < World.Count; i++)
            {
                Assert.Equal(Bits(a.Pool.LocalRot(a.Poses[i])), Bits(b.Pool.LocalRot(b.Poses[i])));
                Assert.Equal(Bits(a.Pool.LocalPos(a.Poses[i])), Bits(b.Pool.LocalPos(b.Poses[i])));
                Assert.Equal(Bits(a.Pool.Model(a.Poses[i])), Bits(b.Pool.Model(b.Poses[i])));
            }
            AssertSame(a.Pool, a.Players, b.Pool, b.Players);
        }
        Assert.True(a.Transitions > 50, $"переходов {a.Transitions}");
    }

    /// <summary>Хранилища — побитово одинаковы: слоты, поколения, свободные, состояние и данные суставов живых проигрывателей.</summary>
    private static void AssertSame(PosePool poolA, MotionPlayers a, PosePool poolB, MotionPlayers b)
    {
        Assert.Equal(poolA.Capacity, poolB.Capacity);
        Assert.Equal(a.Capacity, b.Capacity);
        Assert.Equal(a.FreeSlots.ToArray(), b.FreeSlots.ToArray());
        for (int i = 0; i < a.Capacity; i++)
        {
            Assert.Equal(a.GenerationAt(i), b.GenerationAt(i));
            var h = a.HandleAt(i);
            if (h.IsNone) continue;
            var (sa, sb) = (a.State(h), b.State(h));
            Assert.Equal((sa.Frame, sa.Transitions, sa.HasOutput), (sb.Frame, sb.Transitions, sb.HasOutput));
            Assert.Equal(Bits<float>([sa.Alpha, sa.RootStep.X, sa.RootStep.Z, sa.RootStep.Yaw]),
                Bits<float>([sb.Alpha, sb.RootStep.X, sb.RootStep.Z, sb.RootStep.Yaw]));
            Assert.Equal(Bits(a.RotationOffsets(h)), Bits(b.RotationOffsets(h)));
            Assert.Equal(Bits(a.RotationOffsetVelocities(h)), Bits(b.RotationOffsetVelocities(h)));
            Assert.Equal(Bits(a.PositionOffsets(h)), Bits(b.PositionOffsets(h)));
            Assert.Equal(Bits(a.PositionOffsetVelocities(h)), Bits(b.PositionOffsetVelocities(h)));
            Assert.Equal(Bits(a.AngularVelocities(h)), Bits(b.AngularVelocities(h)));
            Assert.Equal(Bits(a.LinearVelocities(h)), Bits(b.LinearVelocities(h)));
            Assert.Equal(Bits(a.OutputRotations(h)), Bits(b.OutputRotations(h)));
            Assert.Equal(Bits(a.OutputPositions(h)), Bits(b.OutputPositions(h)));
        }
    }

    /// <summary>Пул и хранилище с проигрывателями и случайными заданиями по сиду.</summary>
    private sealed class World
    {
        public const int Count = 3;
        public readonly PosePool Pool;
        public readonly MotionPlayers Players;
        public readonly PoseHandle[] Poses = new PoseHandle[Count];
        public readonly MotionPlayerHandle[] Handles = new MotionPlayerHandle[Count];
        private readonly PoseHandle _aux;
        private readonly Random _rng;
        private readonly int _frames;
        public int Transitions;

        public World(MotionDatabase db, int seed)
        {
            Pool = new PosePool(db.Library);
            Players = new MotionPlayers(db);
            _rng = new Random(seed);
            _frames = db.FrameCount;
            _aux = Pool.Alloc();
            for (int i = 0; i < Count; i++)
            {
                Poses[i] = Pool.Alloc();
                Handles[i] = Players.Alloc();
            }
        }

        public PoseTickStats Tick()
        {
            for (int i = 0; i < Count; i++)
            {
                bool start = Players.State(Handles[i]).Frame < 0;
                int transition = start || _rng.Next(7) == 0 ? _rng.Next(_frames) : -1;
                if (transition >= 0) Transitions++;
                float dt = Dt * (0.5f + (float)_rng.NextDouble());
                var step = new MotionStep((float)_rng.NextDouble() * 3 * dt, dt, transition, 0.05f + 0.3f * (float)_rng.NextDouble());
                Pool.Motion(Poses[i], Players, Handles[i], step);
                if (_rng.Next(4) == 0) Pool.Blend(Poses[i], _aux, (float)_rng.NextDouble());
                Pool.ToModel(Poses[i]);
            }
            Pool.Sample(_aux, _rng.Next(3), (float)_rng.NextDouble());
            if (_rng.Next(50) == 0)
            {
                int i = _rng.Next(Count);
                Players.Free(Handles[i]);   // задание уже в очереди — пропуск
                Handles[i] = Players.Alloc();
            }
            return Pool.Tick();
        }
    }

    [Fact]
    public void LongRun_RandomTransitions_OutputStaysFinite()
    {
        var db = Database(Library(), exit: true);
        var rig = new PlayerRig(db);
        var rng = new Random(5);
        rig.Tick(Walk);
        for (int t = 0; t < 20000; t++)
        {
            int transition = rng.Next(10) == 0 ? rng.Next(db.FrameCount) : -1;
            float dt = Dt * (0.5f + (float)rng.NextDouble());
            rig.Step((float)rng.NextDouble() * 1.5f, transition, dt, 0.03f + 0.4f * (float)rng.NextDouble());
            var s = rig.State;
            Assert.InRange(s.Frame, 0, db.FrameCount - 1);
            Assert.True(s.Alpha is >= 0 and < 1 && s.RootStep.IsFinite, $"тик {t}: {s}");
            foreach (var q in rig.Rot) Assert.True(Finite(q) && MathF.Abs(q.Length() - 1) < 1e-3f, $"тик {t}: поворот {q}");
            foreach (var v in rig.Pos) Assert.True(Finite(v), $"тик {t}: смещение {v}");
            foreach (var q in rig.Players.RotationOffsets(rig.Player)) Assert.True(Finite(q) && q.W >= 0);
            foreach (var v in rig.Players.PositionOffsets(rig.Player)) Assert.True(Finite(v) && v.Length() < 1);
            foreach (var v in rig.Players.RotationOffsetVelocities(rig.Player)) Assert.True(Finite(v));
            foreach (var v in rig.Players.AngularVelocities(rig.Player)) Assert.True(Finite(v));
            foreach (var v in rig.Players.LinearVelocities(rig.Player)) Assert.True(Finite(v));
        }
        Assert.True(rig.State.Transitions > 1500);
    }

    /// <summary>
    /// Ребро посреди тика: шаг через кадр выхода — то же (смещения, их скорости, кадр, доля, выход), что два шага того же темпа со
    /// временем пропорционально продвижению, где бы ни прошёл разрез — до ребра, на нём или после: смещения до ребра гаснут до момента
    /// ребра, новое смещение — остаток. Затухание целиком после ребра не проходит разрез до ребра, целиком до ребра — разрез на ребре.
    /// </summary>
    [Fact]
    public void EdgeMidTick_SameAsTwoStepsCutAnywhere()
    {
        var db = Database(Library(), exit: true);
        const float total = 2.4f, dt = 3 * Dt;   // до кадра выхода — 2 кадра, после — 0,4
        foreach (float cut in new[] { 1.75f, 2f, 2.25f })
        {
            var one = new PlayerRig(db);
            var two = new PlayerRig(db);
            foreach (var rig in new[] { one, two })
            {
                rig.Tick(Walk + 3);
                for (int t = 0; t < 20; t++) rig.Tick();
                rig.Tick(Exit - 2);   // смещения не нулевые — есть что гасить до ребра
            }
            one.Step(total, dt: dt);
            two.Step(cut, dt: dt * cut / total);
            two.Step(total - cut, dt: dt * (total - cut) / total);
            Assert.Equal((Entry, 2), (one.State.Frame, one.State.Transitions));
            Assert.Equal((one.State.Frame, one.State.Transitions), (two.State.Frame, two.State.Transitions));
            Assert.Equal(one.State.Alpha, two.State.Alpha, 1e-5f);
            float largest = 0;
            for (int j = 0; j < db.Library.JointCount; j++)
            {
                largest = MathF.Max(largest, AnimMath.ToRotationVector(one.Players.RotationOffsets(one.Player)[j]).Length());
                Assert.True(Angle(one.Players.RotationOffsets(one.Player)[j], two.Players.RotationOffsets(two.Player)[j]) < 1e-5f,
                    $"разрез {cut}, сустав {j}: смещение");
                Assert.True(Vector3.Distance(one.Players.RotationOffsetVelocities(one.Player)[j], two.Players.RotationOffsetVelocities(two.Player)[j])
                    < 1e-4f, $"разрез {cut}, сустав {j}: скорость смещения");
                Assert.True(Vector3.Distance(one.Players.PositionOffsets(one.Player)[j], two.Players.PositionOffsets(two.Player)[j]) < 1e-6f);
                Assert.True(Vector3.Distance(one.Players.PositionOffsetVelocities(one.Player)[j], two.Players.PositionOffsetVelocities(two.Player)[j])
                    < 1e-5f);
            }
            Assert.True(largest > 0.1f, $"смещение {largest} рад — тест ничего бы не проверял");
            var c = MaxChange(one.Rot, one.Pos, two.Rot, two.Pos);
            Assert.True(c.Rot < 1e-5f && c.Pos < 1e-6f, $"разрез {cut}, выход: {c}");
        }
    }

    /// <summary>Огромное продвижение — не больше 1024 интервалов графа за задание; цикл входов не зацикливает ни переход, ни продвижение.</summary>
    [Fact]
    public void Safeguards_HugeAdvanceAndEntryCycle_Terminate()
    {
        var lib = Library();
        var db = Database(lib);
        int expected = Walk;
        for (int i = 0; i < 1024; i++) expected = db.Next[expected];
        foreach (float advance in new[] { 5000f / db.SampleRate, 1e30f })
        {
            var rig = new PlayerRig(db);
            rig.Step(0, Walk);
            rig.Pool.Motion(rig.Pose, rig.Players, rig.Player, new MotionStep(advance, Dt, -1, Halflife));
            rig.Pool.Tick();
            Assert.Equal((expected, 0f, 0), (rig.State.Frame, rig.State.Alpha, rig.State.Transitions));
            Assert.True(rig.State.RootStep.IsFinite);
        }

        var schema = MotionFixtures.Schema();
        var a = MotionFixtures.Plain(lib, schema, [0, 1, 2]);
        AddExit(a, Exit, Entry);
        AddExit(a, Entry, Exit);   // цикл входов: 38 → 49 → 38 → …
        var cycle = new PlayerRig(a.Build(lib, schema));
        cycle.Step(0, Exit - 1);
        for (int t = 0; t < 50; t++) cycle.Tick(t == 20 ? Entry : -1);
        Assert.InRange(cycle.State.Frame, 0, cycle.Database.FrameCount - 1);
        Assert.True(cycle.State.Alpha is >= 0 and < 1);
        foreach (var q in cycle.Rot) Assert.True(Finite(q));
    }

    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
