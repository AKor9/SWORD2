using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Physics;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Прогрев кода шага физики (7з-2) — один раз на процесс, при сборке первой симуляции (конструктор <see cref="PhysicsStepSystem"/>): до
/// первой смерти тела на физике в игре нет, и код шага трупа (суставы, земля по граням, пары, трение, скоростной проход, расслабление,
/// толчки живых, удар) впервые компилировался бы на тике первой смерти и первую секунду шёл кодом уровня 0 (замер 7з, Release: тик
/// массовой смерти 25 мс, падение — до 7,9 мс). Прогрев — свой мир физики с теми же настройками, землёй, стеной и колоннами арены: три
/// синтетических трупа (14 капсул с массами и радиусами бойца, суставы — шарниры и наклон-скручивание) падают на ступени пирамиды,
/// друг на друга и к колонне, кинематическое «живое» сочленение толкает третьего проходом, по нему бьёт удар; шаги — до затишья JIT
/// (<see cref="JitWarmup"/>, ≤ <see cref="MaxSeconds"/>): горячие методы уходят на уровень 1 (перевод идёт в фоне через ~0,1 с после
/// вызовов — короткий прогрев оставлял уровень 0: замер — README «Рэгдолл», «7з-2»).
/// <para>
/// Прогрев идёт в фоновом потоке (JIT — общий на процесс): сборка симуляции его не ждёт, а первая смерть в игре бывает через секунды. Мира,
/// хеша, реплея и выделений тика симуляции не трогает (другой экземпляр физики, свой поток); статическое — флаг «уже грели» и итог (от них
/// поведение симуляции не зависит). Тесты и замеры, которым нужен тёплый JIT, ждут его — <see cref="Wait"/>.
/// </para>
/// </summary>
public static class PhysicsWarmup
{
    /// <summary>Предел прогрева, с (затишье JIT бывает раньше).</summary>
    public const double MaxSeconds = 1;

    private static int s_started;
    private static readonly ManualResetEventSlim s_done = new(false);

    /// <summary>Итог прогрева этого процесса (нет — не грели или ещё греет).</summary>
    public static JitWarmupResult? Result { get; private set; }

    /// <summary>Начать прогрев в фоне, если в этом процессе ещё не начинали (иначе — ничего).</summary>
    public static void Once(PhysicsSettings settings)
    {
        if (Interlocked.Exchange(ref s_started, 1) != 0) return;
        var thread = new Thread(() =>
        {
            try
            {
                Result = Run(settings, MaxSeconds);
                Log.Info("physics", $"прогрев шага физики трупа: {Result}");
            }
            catch (Exception e)
            {
                // прогрев — не игра: его ошибка не роняет процесс (фоновый поток), игра идёт без прогрева
                Log.Error("physics", $"прогрев шага физики упал: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                s_done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "physics warmup",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Дождаться прогрева (начатого <see cref="Once"/>); не начинали — сразу. Истина — прогрев закончен.</summary>
    public static bool Wait(TimeSpan timeout) => Volatile.Read(ref s_started) == 0 || s_done.Wait(timeout);

    /// <summary>Прогрев на своём мире физики: шаги до затишья JIT или <paramref name="maxSeconds"/>.</summary>
    public static JitWarmupResult Run(PhysicsSettings settings, double maxSeconds)
    {
        var w = new PhysicsWorld(4, 64, settings);
        w.SetGround(ArenaGround.Instance);
        w.SetWall(ArenaGround.WallRadius);
        w.SetPillars(ArenaGround.Pillars);
        int profile = w.RegisterDynamics(new BodyDynamics() with { MaxSpeed = 15, MaxAngularSpeed = 40 });
        var pillar = ArenaGround.Pillars[0];
        var a = Corpse(w, new Vector3(ArenaTerrain.StepCentreX + ArenaTerrain.TierHalf(1) + 0.4f, 0.45f, 0), 0.3f, profile);
        Corpse(w, new Vector3(ArenaTerrain.StepCentreX + ArenaTerrain.TierHalf(1) + 0.5f, 0.75f, 0.15f), -0.4f, profile);
        var c = Corpse(w, new Vector3(pillar.X + pillar.Radius + 0.5f, 0.4f, pillar.Z), 1.2f, profile);
        var living = Living(w, new Vector3(pillar.X + pillar.Radius + 0.9f, 0.4f, pillar.Z + 0.3f));
        var target = w.Body(c, BodySegs.Torso);
        const float dt = 1f / 120;
        int tick = 0;
        // 7з-3: сон — через раз «сонные» настройки (засыпают сразу): засыпание, проверка пробуждения, цепочка, спящее как опора, пробуждение
        // ударом, толчком и сменой настроек тоже компилируются заранее
        var sleepy = settings with { SleepSpeed = 100, SleepBodySpeed = 100, SleepTime = 0.05f };
        return JitWarmup.Run(() =>
        {
            tick++;
            if (tick % 40 == 0) w.Settings = (tick / 40) % 2 == 0 ? settings : sleepy;
            // живой: удар (проход клинком), толчок телом, отдача — как игра 7к каждый тик
            for (int k = 0; k < w.BodyCount(living); k++)
            {
                var b = w.Body(living, k);
                var s = w.Get(b);
                w.SetKinematicTarget(b, s.Position + new Vector3(MathF.Sin(tick * 0.05f) * 0.01f, 0, 0), s.Rotation, dt);
            }
            w.PreparePass(living, tick / 60, 1.4f, 8);
            w.Step(dt);
            if (tick % 30 == 0)
            {
                w.PassHit(living, c);
                w.ApplyHit(target, w.Get(target).Position, new Vector3(20, 0, 0), 8, living);
            }
            w.TakeReaction(living);
            w.EndKinematicTick();
            _ = w.NonFiniteArticulations + (w.HasNonFinite(a) ? 1 : 0);
            return true;
        }, JitWarmup.QuietMs, maxSeconds);
    }

    /// <summary>
    /// Синтетический труп: 14 капсул с массами и радиусами бойца лёжа вдоль оси курсом <paramref name="yaw"/> у точки <paramref name="at"/>,
    /// суставы дерева бойца по этой позе (локти и колени — шарниры, остальные — наклон-скручивание, запястье выключено), на физике.
    /// </summary>
    private static ArticulationHandle Corpse(PhysicsWorld w, Vector3 at, float yaw, int profile)
    {
        var art = w.CreateArticulation(BodySegs.Bodies);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        var radii = FighterShape.Radii;
        var masses = FighterShape.Masses;
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var b = w.Body(art, k);
            w.SetShape(b, masses[k], radii[k], 0.12f);
            var local = new Vector3(0.15f * (k % 7) - 0.45f, 0.05f * (k / 7), 0.12f * (k % 3) - 0.12f);
            w.Place(b, at + Vector3.Transform(local, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)), turn);
        }
        Span<JointDesc> joints = stackalloc JointDesc[BodySegs.Bodies - 1];
        for (int child = 1; child < BodySegs.Bodies; child++)
        {
            int parent = Parent(child);
            var anchor = new Vector3(0, -0.12f, 0);
            joints[child - 1] = child is BodySegs.FArmL or BodySegs.FArmR or BodySegs.ShinL or BodySegs.ShinR
                ? JointDesc.Hinge(parent, child, anchor, 0, 2.5f, 0.1f, 1)
                : JointDesc.SwingTwist(parent, child, anchor, -0.6f, 0.4f, 0.4f, 0.6f);
        }
        w.SetJoints(art, joints);
        w.SetJointEnabled(art, BodySegs.Weapon - 1, false);
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var b = w.Body(art, k);
            w.SetMode(b, BodyMode.Simulated);
            w.SetDynamics(b, profile);
        }
        return art;
    }

    /// <summary>Родитель тела в дереве бойца.</summary>
    private static int Parent(int child) => child switch
    {
        BodySegs.Torso or BodySegs.ThighL or BodySegs.ThighR => BodySegs.Pelvis,
        BodySegs.Head or BodySegs.UArmL or BodySegs.UArmR => BodySegs.Torso,
        BodySegs.FArmL => BodySegs.UArmL,
        BodySegs.FArmR => BodySegs.UArmR,
        BodySegs.ShinL => BodySegs.ThighL,
        BodySegs.ShinR => BodySegs.ThighR,
        BodySegs.Weapon => BodySegs.FArmR,
        BodySegs.FootL => BodySegs.ShinL,
        _ => BodySegs.ShinR,
    };

    /// <summary>«Живой»: два кинематических тела, толкающих клинком в ударе (проход) и телом.</summary>
    private static ArticulationHandle Living(PhysicsWorld w, Vector3 at)
    {
        var art = w.CreateArticulation(2);
        for (int k = 0; k < 2; k++)
        {
            var b = w.Body(art, k);
            w.SetShape(b, 5, 0.1f, 0.3f);
            w.Place(b, at + new Vector3(0, 0.3f * k, 0), Quaternion.Identity);
        }
        w.SetKinematicSource(w.Body(art, 1), KinematicSource.Pass);
        return art;
    }
}
