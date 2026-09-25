using System.Globalization;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Рука с мечом игрока на Motion Matching (8ж; решение 3а этапа 8): клипы GASP — без меча, руки в них нейтральные, поэтому поверх позы MM
/// правая рука берёт стойку оружия — клип слота <c>idle</c> таблицы действий (<see cref="ActionTable.Idle"/>) в момент <see cref="StanceTime"/> —
/// смешиванием по маске руки (<see cref="Root"/> и всё его поддерево: ключица, плечо, предплечье, кисть, пальцы, скрутки и корректоры, сустав
/// оружия) с весом ручки <c>locomotion.armWeight</c>. Порядок заданий тика: шаг MM (<c>locomotion.mm</c>) → рука (<c>locomotion.mm-arm</c>) →
/// слой действий ядра (<c>core.animator-pose</c>): удар и рывок перекрывают руку, как раньше. Стойка — одна на все мечи (слот <c>idle</c> таблицы
/// действий не зависит от оружия): смена меча (T, инвентарь) меняет клинок, а не позу руки; сменится клип стойки — поза стойки берётся заново.
///
/// Поза постройки игрока (<see cref="FighterStartPose"/>) несёт руку слоем поверх с тем же весом, с каким её выведет первый тик после постройки
/// (<see cref="Init"/>): капсулы и клинок встают по позе постройки, и первый тик не даёт скачка (<c>physics.kinematicSpeed</c>) и прохода клинка.
/// Вес руки — состояние бойца (<see cref="MotionMatchArm.Weight"/>): после постройки — вес позы постройки, дальше идёт к ручке не быстрее
/// <see cref="WeightRate"/> в секунду (ручка на ходу не дёргает руку за тик).
///
/// Знание о скелете UE5 Manny (имена суставов) и умолчания руки — здесь, одно место (копий констант нет).
/// </summary>
public sealed class SwordArm
{
    /// <summary>Корень маски руки — правая ключица (поддерево — вся правая рука).</summary>
    public const string Root = "clavicle_r";

    /// <summary>Суставы, без которых руки с мечом нет, — все в поддереве <see cref="Root"/> (нет — ошибка старта).</summary>
    public static readonly IReadOnlyList<string> Joints = [Root, "upperarm_r", "lowerarm_r", "hand_r"];

    /// <summary>Вес руки по умолчанию (<c>locomotion.armWeight</c>): стойка целиком.</summary>
    public const float DefaultWeight = 1;

    /// <summary>Предел скорости веса руки, 1/с: от 0 до 1 — за 0,25 с (ручка на ходу).</summary>
    public const float WeightRate = 4;

    /// <summary>Момент клипа стойки, с: стойка неподвижна относительно груди (дыхание стойки не спорит с дыханием клипа MM).</summary>
    public const float StanceTime = 0;

    /// <summary>Метки систем руки.</summary>
    public static readonly SystemLabel Setup = new("locomotion.mm-arm-setup"), Step = new("locomotion.mm-arm"), Release = new("locomotion.mm-arm-release");

    /// <summary>Сустав <see cref="Root"/> в скелете; −1 — не заведена (<see cref="Init"/>).</summary>
    public int Joint { get; private set; } = -1;

    /// <summary>Маска руки в пуле поз симуляции.</summary>
    public PoseMask Mask { get; private set; }

    /// <summary>Компонент, системы и инвариант руки (только при <c>locomotion.player = mm</c>).</summary>
    public void Configure(SimulationBuilder b) =>
        b.Component<MotionMatchArm>()
            .System(GameStages.Gameplay, new SwordArmSetupSystem())
            .System(GameStages.Animation, new SwordArmSystem(this))
            .System(GameStages.End, new SwordArmReleaseSystem())
            .Invariant(new SwordArmInvariant());

    /// <summary>Ручка веса руки (в секции ручек «Локомоция»).</summary>
    internal static void Knob(TuningSection<LocomotionRules> t, float defaultValue) =>
        t.Float("armWeight", "вес руки с мечом", (in LocomotionRules r) => r.ArmWeight, (ref LocomotionRules r, float v) => r.ArmWeight = v,
            0, 1, defaultValue, format: "0.00",
            hint: "стойка оружия в правой руке поверх позы Motion Matching (клипы GASP — без меча): 1 — держит меч, как в стойке, 0 — рука клипа; на ходу — плавно, 0→1 за 0,25 с");

    /// <summary>
    /// Сборка мира (после <see cref="MotionMatchRuntime.Init"/>, до постановки игроку позы постройки): маска руки в пуле, поза постройки MM со
    /// слоем руки (стойка, сустав, вес ручки при сборке) и состояние руки игрокам сборки мира.
    /// </summary>
    /// <exception cref="InvalidDataException">В скелете нет сустава руки или он не в поддереве <see cref="Root"/>.</exception>
    public void Init(World world, MotionMatchRuntime runtime)
    {
        var pool = world.Resource<PosePool>();
        var skeleton = pool.Skeleton;
        int root = skeleton.Find(Root);
        var mask = new float[skeleton.JointCount];
        if (root >= 0) skeleton.SubtreeMask(root, mask);
        foreach (string name in Joints)
        {
            int j = skeleton.Find(name);
            if (j < 0 || mask[j] != 1)
                throw new InvalidDataException($"рука с мечом игрока (locomotion.player = mm) не стартует: в скелете {skeleton.Id} " +
                    (j < 0 ? $"нет сустава «{name}»" : $"сустав «{name}» не в поддереве «{Root}»"));
        }
        Joint = root;
        Mask = pool.AddMask(mask);
        runtime.StartPose = runtime.StartPose with
        {
            LayerClip = world.Resource<ActionTable>().Idle, LayerTime = StanceTime, LayerJoint = Joint,
            LayerWeight = world.Resource<LocomotionRules>().ArmWeight,
        };
        var players = new List<Entity>();
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>().With<PlayerTag>().Without<MotionMatchArm>()))
            players.AddRange(chunk.Entities);
        foreach (var e in players) world.AddImmediate(e, MotionMatchArm.Fresh);
    }

    /// <summary>Вес к цели <paramref name="target"/> не больше чем на <paramref name="step"/>.</summary>
    public static float Approach(float weight, float target, float step) =>
        weight < target ? MathF.Min(target, weight + step) : MathF.Max(target, weight - step);
}

/// <summary>
/// Состояние руки с мечом бойца на Motion Matching (8ж; компонент модуля — хеширует участник <c>ecs</c>, видно <c>entity.get</c>,
/// <c>watch.add</c>, <c>replay.observe</c>): поза стойки в пуле (источник смешивания по маске руки), её клип, действующий вес. Есть только у
/// игрока на MM (<c>locomotion.player = mm</c>).
/// </summary>
public struct MotionMatchArm
{
    /// <summary>Поза стойки руки в пуле поз (заводит первый шаг руки, возвращает <c>locomotion.mm-arm-release</c>).</summary>
    public PoseHandle Pose;

    /// <summary>Клип стойки в <see cref="Pose"/>; −1 — поза стойки ещё не взята.</summary>
    public int Clip;

    /// <summary>Действующий вес руки 0…1: после постройки — вес позы постройки, дальше — к ручке не быстрее <see cref="SwordArm.WeightRate"/>/с.</summary>
    public float Weight;

    /// <summary>Построен (заново): следующий шаг — вес позы постройки и стойка заново. Ставит <c>locomotion.mm-arm-setup</c> по <c>Added&lt;Fighter&gt;</c>.</summary>
    public bool Restart;

    /// <summary>Новое состояние: позы нет, первый шаг — как после постройки.</summary>
    public static MotionMatchArm Fresh => new() { Clip = -1, Restart = true };
}

/// <summary>
/// Gameplay, реактивная по <c>Added&lt;Fighter&gt;</c> игрока (<c>locomotion.mm-arm-setup</c>): построенный (заново) игрок начинает руку с
/// постройки — флаг <see cref="MotionMatchArm.Restart"/> (как <c>locomotion.mm-setup</c> у состояния MM); игроку без руки (создан командой) —
/// состояние командой. Две записи одного бойца за срабатывание — одна команда.
/// </summary>
public sealed class SwordArmSetupSystem : IReactiveSystem<Added<Fighter>>
{
    public void Setup(SystemSetup s) =>
        s.Label(SwordArm.Setup).Adds<MotionMatchArm>().Sets<MotionMatchArm>().NextTickOk<Added<Fighter>>();

    public void React(in SystemContext ctx, ReadOnlySpan<Added<Fighter>> added)
    {
        var world = ctx.World;
        for (int i = 0; i < added.Length; i++)
        {
            var e = added[i].Entity;
            if (!world.IsAlive(e) || !world.Has<PlayerTag>(e) || Later(added, i)) continue;
            ctx.Cause = added[i].Cause;   // причина постройки бойца — родитель состояния руки (state.why)
            if (world.TryGet<MotionMatchArm>(e, out var arm))
            {
                arm.Restart = true;
                ctx.Commands.Set(e, arm);
            }
            else ctx.Commands.Add(e, MotionMatchArm.Fresh);
        }
    }

    private static bool Later(ReadOnlySpan<Added<Fighter>> added, int i)
    {
        for (int j = i + 1; j < added.Length; j++)
            if (added[j].Entity == added[i].Entity) return true;
        return false;
    }
}

/// <summary>
/// Animation, после шага MM (<c>locomotion.mm</c> — он пишет позу бойца целиком) и до слоя действий ядра (<c>core.animator-pose</c>)
/// (<c>locomotion.mm-arm</c>): рука с мечом живого бойца на MM — смешивание позы стойки в его позу по маске руки (доля вне маски 0) с
/// действующим весом. После постройки вес — вес слоя позы постройки (<see cref="FighterStartPose.LayerWeight"/>; нет слоя — 0), иначе — к
/// ручке <c>locomotion.armWeight</c> не быстрее <see cref="SwordArm.WeightRate"/>/с. Поза стойки берётся (сэмпл клипа слота <c>idle</c>) при
/// постройке и при смене клипа стойки; вес 0 — задания смешивания нет. Мёртвого не трогает. Память не выделяет (поза — из запаса пула).
/// </summary>
public sealed class SwordArmSystem(SwordArm arm) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(SwordArm.Step).After(MotionMatchSystems.Step).Before(GameLabels.AnimatorPose).Reads<PoseRef>().Reads<FighterStartPose>()
            .Writes<MotionMatchArm>();
        _query = s.Query().With<Fighter>().With<MotionMatchState>().Without<Dead>().Read<PoseRef>().Write<MotionMatchArm>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var pool = world.Resource<PosePool>();
        float target = world.Resource<LocomotionRules>().ArmWeight;
        float step = SwordArm.WeightRate * world.Resource<SimTime>().Dt;
        int stance = world.Resource<ActionTable>().Idle;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Write<MotionMatchArm>();
            var pose = chunk.Read<PoseRef>()[..state.Length];
            var entities = chunk.Entities;
            for (int i = 0; i < state.Length; i++)
            {
                var handle = new PoseHandle(pose[i].Index, pose[i].Generation);
                if (!pool.IsAlive(handle)) continue;   // позу ещё не завели (её ловит animation.owners)
                Tick(ref state[i], world, entities[i], handle, pool, target, step, stance);
            }
        }
    }

    /// <summary>Шаг руки бойца (тело цикла — свой метод: правило OSR).</summary>
    private void Tick(ref MotionMatchArm s, World world, Entity e, PoseHandle pose, PosePool pool, float target, float step, int stance)
    {
        if (s.Pose.IsNone) s.Pose = pool.Alloc();   // из запаса пула (PosesPerFighter): поза руки вместо слоя и снимка локомоции клипами
        if (s.Restart)
        {
            s.Restart = false;
            s.Weight = world.TryGet<FighterStartPose>(e, out var start) ? start.LayerWeight : 0;
            s.Clip = -1;
        }
        else s.Weight = SwordArm.Approach(s.Weight, target, step);
        if (s.Clip != stance)
        {
            pool.Sample(s.Pose, stance, SwordArm.StanceTime);
            s.Clip = stance;
        }
        if (s.Weight > 0) pool.BlendMasked(pose, s.Pose, s.Weight, arm.Mask, 0);
    }
}

/// <summary>End (<c>locomotion.mm-arm-release</c>): снятое состояние руки (боец удалён или компонент снят) возвращает позу стойки в пул.</summary>
public sealed class SwordArmReleaseSystem : IReactiveSystem<Removed<MotionMatchArm>>
{
    public void Setup(SystemSetup s) => s.Label(SwordArm.Release);

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<MotionMatchArm>> removed)
    {
        var pool = ctx.World.Resource<PosePool>();
        foreach (var r in removed)
            if (pool.IsAlive(r.Value.Pose)) pool.Free(r.Value.Pose);
    }
}

/// <summary>
/// <c>locomotion.mmArm</c> (8ж; лёгкий — каждый тик, после End): у бойца на Motion Matching есть рука с мечом; её вес конечен и в 0…1, клип
/// стойки — клип библиотеки или −1; поза стойки живая, своя (не у двух бойцов) и не итоговая поза бойца (<see cref="PoseRef"/>). Пул — только
/// через вид <see cref="IAnimationView"/>. Бойцы на постройке заново (<see cref="MotionMatchArm.Restart"/>) — не проверяются.
/// </summary>
public sealed class SwordArmInvariant : IInvariant
{
    private ReadQuery? _arms, _bare;
    private Entity[] _owner = new Entity[16];

    public string Id => "locomotion.mmArm";
    public InvariantCost Cost => InvariantCost.EveryTick;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        var world = ctx.World;
        var view = world.View<IAnimationView>();
        _arms ??= world.Query().Read<MotionMatchArm>();
        _bare ??= world.Query().With<MotionMatchState>().Without<MotionMatchArm>();
        Array.Clear(_owner);
        foreach (var chunk in world.Iterate(_bare))
            foreach (var e in chunk.Entities)
            {
                violations.Report(e, "боец на Motion Matching без руки с мечом (MotionMatchArm)");
                if (violations.Full) return;
            }
        foreach (var chunk in world.Iterate(_arms))
        {
            var entities = chunk.Entities;
            var arms = chunk.Read<MotionMatchArm>();
            for (int i = 0; i < entities.Length; i++)
            {
                if (Problem(world, view, entities[i], arms[i]) is not { } problem) continue;
                violations.Report(entities[i], problem);
                if (violations.Full) return;
            }
        }
    }

    /// <summary>Первое нарушение бойца (тело цикла — свой метод); всё в порядке — null.</summary>
    private string? Problem(WorldReader world, IAnimationView view, Entity e, in MotionMatchArm s)
    {
        if (s.Restart) return null;
        if (!(float.IsFinite(s.Weight) && s.Weight >= 0 && s.Weight <= 1))
            return string.Create(CultureInfo.InvariantCulture, $"вес руки {s.Weight} вне 0…1");
        if (s.Clip < -1 || s.Clip >= view.ClipCount) return $"клип стойки {s.Clip} вне библиотеки (клипов {view.ClipCount})";
        if (s.Pose.IsNone) return null;   // позу берёт первый шаг руки
        if (view.Model(new PoseRef { Index = s.Pose.Index, Generation = s.Pose.Generation }).IsEmpty) return $"поза стойки {s.Pose} не жива";
        if (world.TryGet<PoseRef>(e, out var own) && own.Index == s.Pose.Index && own.Generation == s.Pose.Generation)
            return $"поза стойки {s.Pose} — это итоговая поза бойца";
        if (s.Pose.Index >= _owner.Length) Array.Resize(ref _owner, Math.Max(_owner.Length * 2, s.Pose.Index + 1));
        if (!_owner[s.Pose.Index].IsNull) return $"поза стойки {s.Pose} — ещё и у {_owner[s.Pose.Index]}";
        _owner[s.Pose.Index] = e;
        return null;
    }
}
