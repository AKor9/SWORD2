using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;
using Sword.Game.Locomotion.Motion;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Рука с мечом игрока на Motion Matching (8ж; категория Content — клипы GASP есть только в паке; <c>locomotion.player = mm</c>): кисть и
/// клинок относительно груди (<c>spine_05</c> — родитель ключицы) — у стойки оружия (слот <c>idle</c>) стоя и на ходу; в ударе рука — у слоя
/// действий; первый тик после постройки, R, смерть и возрождение — ровно поза постройки (со слоем руки), без скачков капсул и прохода
/// клинка; ручка веса на ходу — плавно; смена меча — та же стойка; <c>clips</c> — ничего от руки; запас пула и 0 Б в тике.
/// </summary>
[Trait("Category", "Content")]
public sealed class SwordArmTests(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Допуск «у стойки» / «у слоя действий», м: кисть и кончик клинка относительно груди (смешивание с весом 1 — те же повороты суставов руки).</summary>
    private const float Eps = 1e-4f;

    private static Animation.CharacterAssets Assets => FeatureManifestsTests.Reference.Value.Assets;

    private static Simulation Create(params string[] sets) =>
        SwordSimulation.Create(ConfigSet.Empty(["locomotion.player=mm", .. sets]), Assets);

    /// <summary>Симуляция игры и её пул поз (ресурс-класс наблюдателю не виден — берёт модуль-зонд при сборке мира).</summary>
    private static (Simulation Sim, PosePool Pool) WithPool(params string[] sets)
    {
        var probe = new PoolProbe();
        var sim = Simulation.Build(ConfigSet.Empty(sets), GameStages.All, b => GameCore.Configure(b, Assets), [.. SwordSimulation.Modules(), probe]);
        return (sim, probe.Pool!);
    }

    private sealed class PoolProbe : IModule
    {
        public PosePool? Pool;

        public string Id => "pool_probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => Pool = w.Resource<PosePool>());
    }

    private static void Step(Simulation sim, in PlayerCommand command)
    {
        sim.Export.BeginFrame();
        sim.Enqueue(command);
        sim.Step();
    }

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static readonly PlayerCommand Stand = new() { HasGaze = true, Gaze = new Vector3(0, 0, 100) };
    private static readonly PlayerCommand RunForward = new() { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 200) };

    /// <summary>Ход по фазам (по 1 с): бег вперёд, бег вправо со взглядом вперёд, шаг назад, бег влево, бег по кругу направлений.</summary>
    private static PlayerCommand Moving(int tick)
    {
        var gaze = new Vector3(0, 0, 200);
        return (tick / 120) switch
        {
            0 => RunForward,
            1 => new PlayerCommand { Move = new Vector2(1, 0), HasGaze = true, Gaze = gaze },
            2 => new PlayerCommand { Move = new Vector2(0, -1), Walk = true, HasGaze = true, Gaze = gaze },
            3 => new PlayerCommand { Move = new Vector2(-1, 0), HasGaze = true, Gaze = gaze },
            _ => new PlayerCommand { Move = new Vector2(MathF.Sin(tick * 0.05f), MathF.Cos(tick * 0.05f)), HasGaze = true, Gaze = gaze },
        };
    }

    private static int Chest => Assets.Clips.Skeleton.Find("spine_05");

    /// <summary>Кисть в системе груди.</summary>
    private static Matrix4x4 HandInChest(ReadOnlySpan<Matrix4x4> model)
    {
        Assert.True(Matrix4x4.Invert(model[Chest], out var chest));
        return model[Assets.Hand] * chest;
    }

    /// <summary>Кисть в системе груди у клипа <paramref name="clip"/> в момент <paramref name="time"/>.</summary>
    private static Matrix4x4 ClipHandInChest(int clip, float time)
    {
        var pool = new PosePool(Assets.Clips, capacity: 1);
        var h = pool.Alloc();
        pool.Sample(h, clip, time);
        pool.ToModel(h);
        pool.Tick();
        return HandInChest(pool.Model(h));
    }

    /// <summary>Стойка оружия — кисть в системе груди.</summary>
    private static Matrix4x4 Stance => ClipHandInChest(Assets.Actions.Idle, SwordArm.StanceTime);

    private static Matrix4x4 PlayerHandInChest(Simulation sim)
    {
        var world = sim.World;
        return HandInChest(world.View<IAnimationView>().Model(world.Get<PoseRef>(GameCore.Player(world))));
    }

    /// <summary>Расхождение двух систем кисти, м: большее из расстояний начала кисти и кончика клинка меча по умолчанию.</summary>
    private static float Off(in Matrix4x4 a, in Matrix4x4 b)
    {
        var tip = Assets.Weapons.Weapons[Assets.Weapons.Default].BladeTip;
        return MathF.Max(Vector3.Distance(a.Translation, b.Translation), Vector3.Distance(Vector3.Transform(tip, a), Vector3.Transform(tip, b)));
    }

    private static MotionMatchArm Arm(Simulation sim) => sim.World.Get<MotionMatchArm>(GameCore.Player(sim.World));

    /// <summary>Наибольшее расстояние суставов итоговой позы игрока от его позы постройки (матрицы постройки ядра — со слоем руки).</summary>
    private static float FromBuildPose(Simulation sim)
    {
        var player = GameCore.Player(sim.World);
        var built = new Bodies.StartPoseModel(Assets.Clips).Of(sim.World.Get<FighterStartPose>(player));
        var pose = sim.World.View<IAnimationView>().Model(sim.World.Get<PoseRef>(player));
        float max = 0;
        for (int j = 0; j < built.Length; j++) max = MathF.Max(max, Vector3.Distance(built[j].Translation, pose[j].Translation));
        return max;
    }

    /// <summary>Маска руки — правая ключица и всё её поддерево (46 суставов на паке); поза постройки игрока несёт руку слоем поверх.</summary>
    [Fact]
    public void Mask_IsRightArmSubtree_BuildPoseCarriesArm()
    {
        var (sim, pool) = WithPool("locomotion.player=mm", "arena.enemies=0");
        var skeleton = pool.Skeleton;
        Assert.Equal(3, pool.MaskCount);   // ядро: верх и всё тело; модуль: рука
        var mask = pool.Mask(new PoseMask(2)).ToArray();
        var names = Enumerable.Range(0, mask.Length).Where(j => mask[j] == 1).Select(j => skeleton.JointNames[j]).ToArray();
        output.WriteLine($"маска руки: {names.Length} суставов — {string.Join(", ", names)}");
        Assert.Equal(mask.Length, Enumerable.Range(0, mask.Length).Count(j => mask[j] is 0 or 1));
        foreach (string n in (string[])["clavicle_r", "upperarm_r", "lowerarm_r", "hand_r", "index_03_r", "thumb_01_r", "upperarm_twist_01_r",
                     "lowerarm_twist_02_r", "weapon_r"])
            Assert.Contains(n, names);
        foreach (string n in (string[])["spine_05", "neck_01", "head", "clavicle_l", "hand_l", "pelvis", "thigh_r"])
            Assert.DoesNotContain(n, names);
        var subtree = new float[skeleton.JointCount];
        skeleton.SubtreeMask(skeleton.Find(SwordArm.Root), subtree);
        Assert.Equal(subtree, mask);

        var start = sim.World.Get<FighterStartPose>(GameCore.Player(sim.World));
        Assert.Equal(Assets.Actions.Idle, start.LayerClip);
        Assert.Equal(SwordArm.StanceTime, start.LayerTime);
        Assert.Equal(skeleton.Find(SwordArm.Root), start.LayerJoint);
        Assert.Equal(SwordArm.DefaultWeight, start.LayerWeight);
        var half = Create("arena.enemies=0", "locomotion.armWeight=0.5");
        Assert.Equal(0.5f, half.World.Get<FighterStartPose>(GameCore.Player(half.World)).LayerWeight);
    }

    /// <summary>
    /// Кисть и кончик клинка относительно груди — у стойки оружия (ε) в каждом тике стоя и на ходу (бег в стороны, шаг назад, круг
    /// направлений), без ударов; при весе руки 0 рука — нейтральная рука клипов GASP, далеко от стойки (рука правда подменена).
    /// </summary>
    [Fact]
    public void HandToChest_IsStance_StandingAndMoving()
    {
        var stance = Stance;
        var sim = Create("arena.enemies=0");
        var neutral = Create("arena.enemies=0", "locomotion.armWeight=0");
        float stand = 0, moving = 0, neutralMin = float.MaxValue;
        for (int t = 0; t < 120; t++)
        {
            Step(sim, Stand);
            Step(neutral, Stand);
            stand = MathF.Max(stand, Off(PlayerHandInChest(sim), stance));
            neutralMin = MathF.Min(neutralMin, Off(PlayerHandInChest(neutral), stance));
        }
        for (int t = 0; t < 720; t++)
        {
            Step(sim, Moving(t));
            Step(neutral, Moving(t));
            moving = MathF.Max(moving, Off(PlayerHandInChest(sim), stance));
            neutralMin = MathF.Min(neutralMin, Off(PlayerHandInChest(neutral), stance));
        }
        var s = sim.World.Get<MotionMatchState>(GameCore.Player(sim.World));
        output.WriteLine(string.Create(Inv,
            $"от стойки: стоя {stand:G3} м, на ходу {moving:G3} м (переходов MM {s.Transitions}); вес 0 — ближе всего {neutralMin:F3} м"));
        Assert.True(stand < Eps && moving < Eps, $"кисть от стойки: стоя {stand}, на ходу {moving}");
        Assert.True(s.Transitions > 5, $"переходов MM {s.Transitions}");
        Assert.True(neutralMin > 0.1f, $"рука клипов GASP у стойки ({neutralMin} м) — проверка ничего не проверяет");
        Assert.Equal(0, Arm(neutral).Weight);
        Clean(sim);
        Clean(neutral);
    }

    /// <summary>
    /// Удар на бегу (слоем верха): при полном весе слоя действий кисть относительно груди — ровно клип удара в его момент (рука у слоя
    /// действий, стойка под ним не видна), замах уводит её далеко от стойки; удар погас — снова стойка.
    /// </summary>
    [Fact]
    public void Attack_ArmBelongsToActionLayer()
    {
        var stance = Stance;
        var sim = Create("arena.enemies=0");
        var player = GameCore.Player(sim.World);
        for (int t = 0; t < 90; t++) Step(sim, RunForward);
        Step(sim, RunForward with { Attack = true });
        int full = 0;
        float fromAction = 0, fromStance = 0;
        for (int t = 0; t < 150; t++)
        {
            Step(sim, RunForward);
            var a = sim.World.Get<Animation.AnimatorAction>(player);
            if (a.Kind != Animation.ActionKind.Attack || a.Weight < 1) continue;
            full++;
            Assert.True(a.Upper, "удар на бегу — слоем верха");
            var hand = PlayerHandInChest(sim);
            fromAction = MathF.Max(fromAction, Off(hand, ClipHandInChest(a.Clip, a.Time)));
            fromStance = MathF.Max(fromStance, Off(hand, stance));
        }
        var after = sim.World.Get<Animation.AnimatorAction>(player);
        float back = Off(PlayerHandInChest(sim), stance);
        output.WriteLine(string.Create(Inv,
            $"удар: тиков с полным весом {full}, от клипа удара {fromAction:G3} м, от стойки до {fromStance:F3} м; после удара от стойки {back:G3} м"));
        Assert.True(full > 10, $"тиков удара с полным весом {full}");
        Assert.True(fromAction < Eps, $"рука в ударе отошла от клипа удара на {fromAction} м");
        Assert.True(fromStance > 0.2f, $"удар не увёл руку от стойки ({fromStance} м)");
        Assert.Equal(Animation.ActionKind.None, after.Kind);
        Assert.True(back < Eps, $"после удара рука не вернулась в стойку ({back} м)");
        Clean(sim);
    }

    /// <summary>
    /// Первый тик после постройки, после R и после возрождения — ровно поза постройки со слоем руки (капсулы и клинок встали по ней: нет
    /// скачка и прохода клинка); ручка веса на ходу 1 → 0 → 1 — вес идёт не быстрее 4/с; R при ручке 0 — вес позы постройки (1, как при
    /// сборке), дальше — к ручке. Ни одного нарушения инвариантов (<c>physics.kinematicSpeed</c>, <c>physics.kinematicPose</c>,
    /// <c>combat.blade</c>, <c>locomotion.mmArm</c>) — в том числе до первого тика.
    /// </summary>
    [Fact]
    public void BuildReset_KnobOnTheRun_NoJumps()
    {
        var sim = Create("arena.enemies=2", "combat.enemiesAttack=false");
        var player = GameCore.Player(sim.World);
        float dt = sim.Dt;
        Assert.All(sim.Invariants.Run(["physics.kinematicPose", "combat.blade", "locomotion.mmArm"], 0, "—", dt),
            r => Assert.Equal(0, r.Violations));
        Step(sim, default);
        AssertBuildPose(sim, "тик 1");
        for (int t = 0; t < 240; t++) Step(sim, Moving(t) with { Attack = t % 50 == 10, Dash = t == 170 });

        // ручка 1 → 0 на ходу: вес — ровно по 4·dt за тик, до нуля за 30 тиков
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of("locomotion.armWeight"), Value = 0 });
        int ticks = 0;
        for (float before = Arm(sim).Weight; Arm(sim).Weight > 0 && ticks < 100; ticks++)
        {
            Step(sim, Moving(ticks));
            float w = Arm(sim).Weight;
            Assert.True(before - w <= SwordArm.WeightRate * dt * 1.0001f && w < before, $"вес {before} → {w}");
            before = w;
        }
        Assert.InRange(ticks, 29, 31);

        // R при ручке 0: поза постройки — со слоем веса сборки (1), первый тик — ровно она, затем вес к ручке
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, default);
        Assert.Equal(1f, sim.World.Get<FighterStartPose>(player).LayerWeight);
        Assert.Equal(1f, Arm(sim).Weight);
        AssertBuildPose(sim, "после R");
        Step(sim, RunForward);
        Assert.Equal(1 - SwordArm.WeightRate * dt, Arm(sim).Weight, 1e-6f);
        for (int t = 0; t < 60; t++) Step(sim, Moving(t));
        Assert.Equal(0f, Arm(sim).Weight);

        // ручка 0 → 1, смерть и возрождение командой сцены: первый тик живого — поза постройки
        sim.Enqueue(new TuningCommand { Knob = TuningKey.Of("locomotion.armWeight"), Value = 1 });
        for (int t = 0; t < 60; t++) Step(sim, Moving(t));
        Assert.Equal(1f, Arm(sim).Weight);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        for (int t = 0; t < 90; t++) Step(sim, RunForward);
        Assert.True(sim.World.Has<Dead>(player));
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(3, -2), Facing = 1 });
        Step(sim, default);
        Assert.False(sim.World.Has<Dead>(player));
        AssertBuildPose(sim, "после возрождения");
        for (int t = 0; t < 120; t++) Step(sim, Moving(t) with { Attack = t % 40 == 5 });
        Clean(sim);
    }

    /// <summary>Поза тика — поза постройки (со слоем руки), клинок тика — без прохода от клинка постройки.</summary>
    private static void AssertBuildPose(Simulation sim, string when)
    {
        float off = FromBuildPose(sim);
        Assert.True(off < 1e-5f, $"{when}: поза тика отошла от позы постройки на {off} м");
        var blade = sim.World.Get<Blade>(GameCore.Player(sim.World));
        float sweep = MathF.Max(Vector3.Distance(blade.Base, blade.PrevBase), Vector3.Distance(blade.Tip, blade.PrevTip));
        Assert.True(sweep < 1e-3f, $"{when}: клинок прошёл {sweep} м от клинка постройки");
    }

    /// <summary>
    /// Смена меча (T — следующий меч инвентаря): стойка одна на все мечи (слот <c>idle</c> таблицы действий), поза стойки не берётся
    /// заново; клинок — нового меча на той же кисти.
    /// </summary>
    [Fact]
    public void WeaponChange_SameStance_NewBlade()
    {
        Assert.True(Assets.Weapons.Count >= 2, "в паке один меч");
        var stance = Stance;
        var sim = Create("arena.enemies=0");
        var player = GameCore.Player(sim.World);
        for (int t = 0; t < 30; t++) Step(sim, Stand);
        var arm = Arm(sim);
        int before = sim.World.Get<WeaponSlot>(player).Index;
        for (int k = 0; k < Assets.Weapons.Count; k++)
        {
            sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });
            Step(sim, Stand);
            int weapon = sim.World.Get<WeaponSlot>(player).Index;
            Assert.Equal(weapon, sim.World.Get<Blade>(player).Weapon);
            Assert.True(Off(PlayerHandInChest(sim), stance) < Eps);
            Assert.Equal(arm.Pose, Arm(sim).Pose);
            Assert.Equal(Assets.Actions.Idle, Arm(sim).Clip);
            for (int t = 0; t < 30; t++) Step(sim, Moving(t));
            Assert.True(Off(PlayerHandInChest(sim), stance) < Eps);
        }
        Assert.Equal(before, sim.World.Get<WeaponSlot>(player).Index);   // по кругу
        Clean(sim);
    }

    /// <summary><c>locomotion.player = clips</c>: руки нет — ни компонента, ни маски в пуле, ни ручки.</summary>
    [Fact]
    public void ClipsMode_NoArm()
    {
        var (sim, pool) = WithPool();
        Step(sim, RunForward);
        Assert.False(sim.World.Knows<MotionMatchArm>());
        Assert.Equal(2, pool.MaskCount);
        Assert.DoesNotContain(sim.Tuning, k => k.Section == LocomotionKnobs.Section);
        Assert.False(sim.World.Has<FighterStartPose>(GameCore.Player(sim.World)));
    }

    /// <summary>
    /// Запас пула: у игрока на MM с рукой, ударами, рывком и вздрагиванием (проба удара по нему) поз не больше
    /// <see cref="GameCore.PosesPerFighter"/> (наибольшее — все позы слоя действий, вздрагивания и рука), пул не растёт с постройки (память —
    /// запас сборки мира). 0 Б в тике после прогрева — без врагов (удары игрока по врагам, смерть и возрождение — не про руку): бег,
    /// удары, рывки.
    /// </summary>
    [Fact]
    public void PoolWithinReserve_ZeroBytes()
    {
        var (sim, pool) = WithPool("locomotion.player=mm", "arena.enemies=1", "combat.enemiesAttack=false");
        var player = GameCore.Player(sim.World);
        (int reserved, int jobs) = (pool.Reserved, pool.JobsReserved);
        int most = 0;
        for (int t = 0; t < 1200; t++)
        {
            // удары и рывки, затем вздрагивания (пробы удара по игроку); вместе не бьются — вздрагивание в ударе даёт скачок капсул и без руки
            // (physics.kinematicSpeed при locomotion.player = clips — до 8е; запись в отчёте 8ж)
            bool acting = t < 600;
            if (!acting && t % 90 == 45) sim.Enqueue(new StrikeProbe { Attacker = 1, Victim = 0, Part = BodySegs.UArmL, Damage = 0.5f });
            Step(sim, Moving(t % 600) with { Attack = acting && t % 50 == 7, Dash = acting && t % 300 == 150 });
            most = Math.Max(most, PlayerPoses(sim, player));
        }
        output.WriteLine($"поз у игрока (наибольшее) {most} из {GameCore.PosesPerFighter}; слотов пула {pool.Reserved}, мест заданий {pool.JobsReserved}");
        Assert.False(sim.World.Has<Dead>(player));
        Assert.Equal(GameCore.PosesPerFighter - 1, most);   // поза бойца, 8 поз слоя действий и вздрагивания, поза руки
        Assert.Equal((reserved, jobs), (pool.Reserved, pool.JobsReserved));
        Clean(sim);

        var alone = Create("arena.enemies=0");
        long allocated = 0;
        for (int t = 0; t < 1800; t++)
        {
            var command = Moving(t % 600) with { Attack = t % 50 == 7, Dash = t % 300 == 150 };
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            Step(alone, command);
            if (t >= 600) allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
        }
        Assert.Equal(0, allocated);
        Clean(alone);
    }

    private static int PlayerPoses(Simulation sim, Entity player)
    {
        var a = sim.World.Get<Animation.ActionPoses>(player);
        PoseHandle[] held = [a.Layer, a.Snap, a.BlendOutput, a.BlendShortest, a.FlinchRef, a.FlinchPose, a.SnapOutput, a.SnapShortest, Arm(sim).Pose];
        return 1 + held.Count(h => !h.IsNone);
    }

    /// <summary>
    /// Смешивание по кратчайшей дуге при частичном весе ручки не перескакивает: повороты суставов маски во всех кадрах базы MM и в стойке
    /// расходятся меньше чем на 150° (перескок — у 180°), поэтому памяти смешивания рука не держит.
    /// </summary>
    [Fact]
    public void StanceArc_FarFromHalfTurn()
    {
        var lib = Assets.Clips;
        var skeleton = lib.Skeleton;
        int jc = skeleton.JointCount;
        var mask = new float[jc];
        skeleton.SubtreeMask(skeleton.Find(SwordArm.Root), mask);
        var rot = new Quaternion[jc];
        var pos = new Vector3[jc];
        lib.Sample(Assets.Actions.Idle, SwordArm.StanceTime, rot, pos);
        float most = 0;
        string at = "";
        foreach (var c in GaspMotionBase.For(lib).Database.Clips)
            for (int f = c.Start; f < c.End; f++)
            {
                int frame = lib.Clips[c.LibraryClip].Start + (f - c.Start);
                for (int j = 0; j < jc; j++)
                {
                    if (mask[j] == 0) continue;
                    float angle = 2 * MathF.Acos(Math.Min(1, MathF.Abs(Quaternion.Dot(lib.LocalRot[frame * jc + j], rot[j])))) * 180 / MathF.PI;
                    if (angle > most) (most, at) = (angle, $"{skeleton.JointNames[j]}, {lib.Clips[c.LibraryClip].Name}");
                }
            }
        output.WriteLine(string.Create(Inv, $"наибольший угол сустава руки от стойки в базе MM: {most:F1}° ({at})"));
        Assert.True(most < 150, $"{most}° ({at})");
    }

    /// <summary>Инвариант <c>locomotion.mmArm</c> ловит неисправности: вес вне 0…1; поза стойки — итоговая поза бойца.</summary>
    [Fact]
    public void ArmInvariant_CatchesFaults()
    {
        Assert.Contains("вес руки 2 вне 0…1", Fault(a => a with { Weight = 2 }));
        Assert.Contains("это итоговая поза бойца", Fault(a => a with { Pose = new PoseHandle(a.Pose.Index, a.Pose.Generation) }, ownPose: true));
    }

    private static string Fault(Func<MotionMatchArm, MotionMatchArm> spoil, bool ownPose = false)
    {
        var sim = Simulation.Build(ConfigSet.Empty("locomotion.player=mm", "arena.enemies=0"), GameStages.All, b => GameCore.Configure(b, Assets),
            [.. SwordSimulation.Modules(), new SpoilArm(spoil, ownPose)]);
        var result = Assert.Single(sim.Invariants.Run(["locomotion.mmArm"], sim.Tick, "между тиками", sim.Dt));
        return Assert.Single(result.Details).Details;
    }

    private sealed class SpoilArm(Func<MotionMatchArm, MotionMatchArm> spoil, bool ownPose) : IModule
    {
        public string Id => "spoil_arm";

        public void Configure(SimulationBuilder b) => b.OnWorldReady(w =>
        {
            var player = GameCore.Player(w.Reader);
            var arm = w.Get<MotionMatchArm>(player) with { Restart = false };
            if (ownPose)
            {
                var own = w.Get<PoseRef>(player);
                arm.Pose = new PoseHandle(own.Index, own.Generation);
            }
            w.SetImmediate(player, spoil(arm));
        });
    }
}
