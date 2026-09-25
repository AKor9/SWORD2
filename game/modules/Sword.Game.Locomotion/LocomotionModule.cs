using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Locomotion.Motion;

namespace Sword.Game.Locomotion;

/// <summary>
/// Секция конфига <c>locomotion</c> (8е): уровень детализации походки игрока (<see cref="Player"/>: <c>clips</c> — клипами, как враги;
/// <c>mm</c> — Motion Matching), отбор клипов базы MM (<see cref="Clips"/>) и ручки «Локомоция» (начальные значения; на ходу — команда
/// ручки). В коде — <c>clips</c>: мир без <c>sword.json</c> (тесты, сценарии фич, эталоны SWORD2) — как до 8е; игра (<c>sword.json</c>) — на
/// <c>mm</c>. Значения по умолчанию ручек — <see cref="GaspMotion"/> (одно место умолчаний; копий констант нет). Секция читается в обоих
/// режимах (ключи строгие); базу MM строит только <c>mm</c>.
/// </summary>
public sealed record LocomotionSettings
{
    public const string MotionMatching = "mm", ClipsOnly = "clips";

    /// <summary>Походка игрока (<c>locomotion.player</c>): <c>clips</c> (по умолчанию) или <c>mm</c>. Враги — клипами всегда.</summary>
    public string Player { get; init; } = ClipsOnly;

    /// <summary>Отбор клипов базы MM — регулярка по короткому имени клипа <c>anim/locomotion</c> (<c>locomotion.clips</c>).</summary>
    public string Clips { get; init; } = GaspMotion.DefaultClips;

    public float SearchPeriod { get; init; } = GaspMotion.SearchPeriod;
    public float SwitchGain { get; init; } = GaspMotion.SwitchGain;
    public float InertialHalflife { get; init; } = GaspMotion.InertialHalflife;
    public float RateRange { get; init; } = GaspMotion.RateRange;

    /// <summary>Вес руки с мечом (8ж, <c>locomotion.armWeight</c>): стойка оружия по маске правой руки поверх позы MM.</summary>
    public float ArmWeight { get; init; } = SwordArm.DefaultWeight;

    // Веса 12 групп признаков — ключи GaspMotion.WeightKeys, значения по умолчанию — веса GaspMotion.Groups.
    public float WeightTrajectory { get; init; } = GaspMotion.Groups[0].Weight;
    public float WeightTrajectoryFacing { get; init; } = GaspMotion.Groups[1].Weight;
    public float WeightPast { get; init; } = GaspMotion.Groups[2].Weight;
    public float WeightRootVelocity { get; init; } = GaspMotion.Groups[3].Weight;
    public float WeightRootYawRate { get; init; } = GaspMotion.Groups[4].Weight;
    public float WeightFeet { get; init; } = GaspMotion.Groups[5].Weight;
    public float WeightFeetVelocity { get; init; } = GaspMotion.Groups[6].Weight;
    public float WeightKnees { get; init; } = GaspMotion.Groups[7].Weight;
    public float WeightPelvis { get; init; } = GaspMotion.Groups[8].Weight;
    public float WeightPelvisVelocity { get; init; } = GaspMotion.Groups[9].Weight;
    public float WeightPelvisFacing { get; init; } = GaspMotion.Groups[10].Weight;
    public float WeightChestFacing { get; init; } = GaspMotion.Groups[11].Weight;

    /// <summary>Игрок на Motion Matching; иначе — клипами (метод, а не свойство: итоговый конфиг — только ключи секции).</summary>
    /// <exception cref="InvalidDataException"><c>locomotion.player</c> — не <c>mm</c> и не <c>clips</c>.</exception>
    public bool PlayerOnMotionMatching() => Player.ToLowerInvariant() switch
    {
        MotionMatching => true,
        ClipsOnly => false,
        _ => throw new InvalidDataException($"locomotion.player = «{Player}»: нужно {MotionMatching} (Motion Matching) или {ClipsOnly} (клипами)"),
    };
}

/// <summary>
/// Ручки «Локомоция» (8е; механизм 6е): ресурс мира из секции <c>locomotion</c>, поле — ручка <c>locomotion.&lt;ключ&gt;</c>; изменение на ходу
/// — команда ручки (<c>TuningCommand</c>, система <c>tuning.locomotion</c> в Begin), в реплее. Есть только при <c>locomotion.player = mm</c>.
/// </summary>
public struct LocomotionRules
{
    /// <summary>Период поиска, с (<c>searchPeriod</c>).</summary>
    public float SearchPeriod;

    /// <summary>Порог смены кадра (<c>switchGain</c>), единиц цены поиска.</summary>
    public float SwitchGain;

    /// <summary>Полупериод инерциализации, с (<c>inertialHalflife</c>).</summary>
    public float InertialHalflife;

    /// <summary>Размах темпа (<c>rateRange</c>): темп в 1 ± размах.</summary>
    public float RateRange;

    /// <summary>Вес руки с мечом (<c>armWeight</c>, 8ж), 0…1.</summary>
    public float ArmWeight;

    public float WeightTrajectory, WeightTrajectoryFacing, WeightPast, WeightRootVelocity, WeightRootYawRate, WeightFeet, WeightFeetVelocity,
        WeightKnees, WeightPelvis, WeightPelvisVelocity, WeightPelvisFacing, WeightChestFacing;

    /// <summary>Правила из секции; числа вне пределов — ошибка конфига.</summary>
    public static LocomotionRules From(LocomotionSettings s)
    {
        var r = new LocomotionRules
        {
            SearchPeriod = Check(s.SearchPeriod, "searchPeriod", 1e-4f, 10),
            SwitchGain = Check(s.SwitchGain, "switchGain", 0, 1000),
            InertialHalflife = Check(s.InertialHalflife, "inertialHalflife", 1e-3f, 10),
            RateRange = Check(s.RateRange, "rateRange", 0, 0.99f),
            ArmWeight = Check(s.ArmWeight, "armWeight", 0, 1),
            WeightTrajectory = s.WeightTrajectory, WeightTrajectoryFacing = s.WeightTrajectoryFacing, WeightPast = s.WeightPast,
            WeightRootVelocity = s.WeightRootVelocity, WeightRootYawRate = s.WeightRootYawRate, WeightFeet = s.WeightFeet,
            WeightFeetVelocity = s.WeightFeetVelocity, WeightKnees = s.WeightKnees, WeightPelvis = s.WeightPelvis,
            WeightPelvisVelocity = s.WeightPelvisVelocity, WeightPelvisFacing = s.WeightPelvisFacing, WeightChestFacing = s.WeightChestFacing,
        };
        Span<float> weights = stackalloc float[GaspMotion.WeightKeys.Count];
        r.Weights(weights);
        for (int g = 0; g < weights.Length; g++) Check(weights[g], GaspMotion.WeightKeys[g], 0, 100);
        return r;
    }

    private static float Check(float value, string key, float min, float max) =>
        float.IsFinite(value) && value >= min && value <= max ? value
            : throw new InvalidDataException($"locomotion.{key} = {value}: нужно конечное число в [{min}, {max}]");

    /// <summary>Веса групп в порядке схемы GASP (<see cref="GaspMotion.Groups"/>) — в <paramref name="into"/> (не меньше 12 мест).</summary>
    public readonly void Weights(Span<float> into)
    {
        into[0] = WeightTrajectory;
        into[1] = WeightTrajectoryFacing;
        into[2] = WeightPast;
        into[3] = WeightRootVelocity;
        into[4] = WeightRootYawRate;
        into[5] = WeightFeet;
        into[6] = WeightFeetVelocity;
        into[7] = WeightKnees;
        into[8] = WeightPelvis;
        into[9] = WeightPelvisVelocity;
        into[10] = WeightPelvisFacing;
        into[11] = WeightChestFacing;
    }

    /// <summary>Период поиска в тиках: <c>round(период / тик)</c>, не меньше 1.</summary>
    public readonly int SearchTicks(float dt) => Math.Max(1, (int)MathF.Round(SearchPeriod / dt));
}

/// <summary>Ручки «Локомоция» (8е): поля <see cref="LocomotionRules"/> — секция <c>locomotion</c>.</summary>
public static class LocomotionKnobs
{
    public const string Section = "locomotion";

    public static void Register(SimulationBuilder b)
    {
        var d = new LocomotionSettings();
        var t = b.Tuning<LocomotionRules>(Section)
            .Float("searchPeriod", "период поиска", (in LocomotionRules r) => r.SearchPeriod, (ref LocomotionRules r, float v) => r.SearchPeriod = v,
                0.01f, 1, d.SearchPeriod, format: "0.000", hint: "поиск кадра — раз в столько секунд (целое число тиков); сразу — при скачке цели и после рывка")
            .Float("switchGain", "порог смены", (in LocomotionRules r) => r.SwitchGain, (ref LocomotionRules r, float v) => r.SwitchGain = v,
                0, 10, d.SwitchGain, format: "0.00", hint: "найденный кадр сменяет текущий, только если дешевле его больше чем на порог: больше — реже смены клипа")
            .Float("inertialHalflife", "полупериод перехода", (in LocomotionRules r) => r.InertialHalflife,
                (ref LocomotionRules r, float v) => r.InertialHalflife = v, 0.01f, 0.5f, d.InertialHalflife, format: "0.000",
                hint: "за сколько секунд гаснет половина разрыва позы при смене кадра (инерциализация)")
            .Float("rateRange", "размах темпа", (in LocomotionRules r) => r.RateRange, (ref LocomotionRules r, float v) => r.RateRange = v,
                0, 0.9f, d.RateRange, format: "0.00", hint: "темп клипа — скорость корня / скорость клипа, в пределах 1 ± размах");
        for (int g = 0; g < GaspMotion.WeightKeys.Count; g++)
        {
            int group = g;
            t.Float(GaspMotion.WeightKeys[g], $"вес: {GaspMotion.Groups[g].Name}", (in LocomotionRules r) => Weight(r, group),
                (ref LocomotionRules r, float v) => SetWeight(ref r, group, v), 0, 10, GaspMotion.Groups[g].Weight, format: "0.00",
                hint: $"вес группы признаков «{GaspMotion.Groups[g].Name}» в цене кадра");
        }
        SwordArm.Knob(t, d.ArmWeight);   // 8ж: вес руки с мечом
    }

    private static float Weight(in LocomotionRules r, int group)
    {
        Span<float> w = stackalloc float[GaspMotion.WeightKeys.Count];
        r.Weights(w);
        return w[group];
    }

    private static void SetWeight(ref LocomotionRules r, int group, float v)
    {
        switch (group)
        {
            case 0: r.WeightTrajectory = v; break;
            case 1: r.WeightTrajectoryFacing = v; break;
            case 2: r.WeightPast = v; break;
            case 3: r.WeightRootVelocity = v; break;
            case 4: r.WeightRootYawRate = v; break;
            case 5: r.WeightFeet = v; break;
            case 6: r.WeightFeetVelocity = v; break;
            case 7: r.WeightKnees = v; break;
            case 8: r.WeightPelvis = v; break;
            case 9: r.WeightPelvisVelocity = v; break;
            case 10: r.WeightPelvisFacing = v; break;
            default: r.WeightChestFacing = v; break;
        }
    }
}

/// <summary>
/// Модуль Locomotion (раздел 10 плана): локомоция клипами (<see cref="LegacyLocomotion"/>) у врагов всегда и у игрока при
/// <c>locomotion.player = clips</c>; Motion Matching игрока (этап 8) — при <c>mm</c> (<see cref="MotionMatchSystems"/>). Без модуля ходьбы нет
/// ни у кого: корень движется, поза бойца — поза привязки. <c>clips</c> регистрирует ровно то же, что до 8е (мир побитово тот же); системы,
/// компоненты, ручки и участник хеша MM — только при <c>mm</c>.
/// </summary>
public sealed class LocomotionModule : IModule
{
    public string Id => "locomotion";

    public void Configure(SimulationBuilder b)
    {
        var settings = b.Settings<LocomotionSettings>();
        bool mm = settings.PlayerOnMotionMatching();
        b.Component<LegacyLocomotionState>()
            .System(GameStages.Gameplay, new LegacyLocomotionSetupSystem(player: !mm))
            .System(GameStages.Animation, new LegacyLocomotionSystem(player: !mm))
            .System(GameStages.End, new LegacyLocomotionReleaseSystem())
            .Invariant(new LocomotionPosesInvariant())
            // Поза постройки бойца (FighterAnimator.Reset → Tick(0) SWORD) — начало слоя локомоции: стойка с нулевого момента
            // (Start). По ней ядро ставит тело при постройке (5б); без модуля — поза привязки. Игрок на MM строится по своей (8е).
            .OnWorldBuilt(w =>
            {
                var start = LegacyLocomotion.Start(w.Resource<ActionTable>());
                w.SetResourceImmediate(new StartPose { Clip = start.Clip, Time = start.Time });
            });
        if (mm) ConfigureMotionMatching(b, settings);
    }

    /// <summary>
    /// Motion Matching игрока (8е): база на процесс и хранилище проигрывателей при сборке мира (ошибка данных — ошибка старта, без отката на
    /// клипы — решение 9), компонент состояния, системы, ручки, участник хеша <c>locomotion</c>, инвариант <c>locomotion.mm</c>.
    /// </summary>
    private static void ConfigureMotionMatching(SimulationBuilder b, LocomotionSettings settings)
    {
        var rules = LocomotionRules.From(settings);
        var runtime = new MotionMatchRuntime();
        var arm = new SwordArm();   // 8ж: рука с мечом поверх позы MM
        b.Component<MotionMatchState>()
            .Resource(rules)
            .System(GameStages.Gameplay, new MotionMatchSetupSystem(runtime))
            .System(GameStages.Animation, new MotionMatchSystem(runtime))
            .System(GameStages.Movement, new MotionMatchHistorySystem(runtime))
            .System(GameStages.End, new MotionMatchReleaseSystem(runtime))
            .StateContributor(new MotionMatchHash(runtime))
            .Invariant(new MotionMatchInvariant(runtime))
            .OnWorldBuilt(w =>
            {
                runtime.Init(w.Resource<PosePool>(), settings.Clips, w.Resource<SimTime>().Dt);
                arm.Init(w, runtime);   // 8ж: маска руки, поза постройки со стойкой руки, состояние руки игроку
                Log.Info("locomotion", runtime.Base.StartLine());
                MotionMatchSetupSystem.PlaceWorldBuilt(w, runtime);
            });
        arm.Configure(b);
        MotionMatchObservers.Register(b, runtime);   // 8з: вид, события переходов, тяжёлые инварианты, сведение реплея
        LocomotionKnobs.Register(b);
    }
}
