using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Locomotion.Motion;

namespace Sword.Game.Locomotion;

/// <summary>
/// Секция конфига <c>locomotion</c>: походка игрока (<see cref="Player"/>: <c>clips</c> или <c>mm</c>), отбор клипов базы MM
/// и начальные значения ручек «Локомоция» (умолчания — <see cref="GaspMotion"/>). В коде умолчание — <c>clips</c> (тесты,
/// эталоны), игра (<c>sword.json</c>) — на <c>mm</c>. Ключи строгие в обоих режимах; базу MM строит только <c>mm</c>.
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

    /// <summary>Вес руки с мечом (<c>locomotion.armWeight</c>): стойка оружия по маске правой руки поверх позы MM.</summary>
    public float ArmWeight { get; init; } = SwordArm.DefaultWeight;

    // Поворот на месте: углы — градусы, времена — секунды (GaspMotion.Turn*).
    public float TurnAngle { get; init; } = GaspMotion.TurnAngle;
    public float TurnSmallAngle { get; init; } = GaspMotion.TurnSmallAngle;
    public float TurnHold { get; init; } = GaspMotion.TurnHold;
    public float TurnMax { get; init; } = GaspMotion.TurnMax;
    public float TurnReturn { get; init; } = GaspMotion.TurnReturn;

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
/// Ручки «Локомоция»: ресурс мира из секции <c>locomotion</c>; изменение на ходу — команда ручки (в реплее). Есть только
/// при <c>locomotion.player = mm</c>.
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

    /// <summary>Вес руки с мечом (<c>armWeight</c>), 0…1.</summary>
    public float ArmWeight;

    /// <summary>
    /// Поворот на месте (<see cref="TurnInPlace"/>): порог клипа поворота и малый угол, градусы (<c>turnAngle</c>, <c>turnSmallAngle</c>), выдержка, с
    /// (<c>turnHold</c>), потолок расхождения, градусы (<c>turnMax</c>), возврат на ходу, с (<c>turnReturn</c>).
    /// </summary>
    public float TurnAngle, TurnSmallAngle, TurnHold, TurnMax, TurnReturn;

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
            TurnAngle = Check(s.TurnAngle, "turnAngle", 1, 180),
            TurnSmallAngle = Check(s.TurnSmallAngle, "turnSmallAngle", 0, 180),
            TurnHold = Check(s.TurnHold, "turnHold", 0, 10),
            TurnMax = Check(s.TurnMax, "turnMax", 0, 180),
            TurnReturn = Check(s.TurnReturn, "turnReturn", 0.01f, 5),
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

/// <summary>Ручки «Локомоция»: поля <see cref="LocomotionRules"/> — секция <c>locomotion</c>.</summary>
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
            string name = GaspMotion.Groups[g].Name.Replace("ω", "поворот");   // в шрифте панели нет греческих букв
            t.Float(GaspMotion.WeightKeys[g], $"вес: {name}", (in LocomotionRules r) => Weight(r, group),
                (ref LocomotionRules r, float v) => SetWeight(ref r, group, v), 0, 10, GaspMotion.Groups[g].Weight, format: "0.00",
                hint: $"вес группы признаков «{name}» в цене кадра");
        }
        SwordArm.Knob(t, d.ArmWeight);   // вес руки с мечом
        t.Float("turnAngle", "поворот: порог", (in LocomotionRules r) => r.TurnAngle, (ref LocomotionRules r, float v) => r.TurnAngle = v,
                1, 180, d.TurnAngle, format: "0",
                hint: "стоя курс корня ушёл от ног дальше, градусы, — сразу клип поворота на месте (Stand_Turn); меньше — ноги стоят, корпус скручивается")
            .Float("turnSmallAngle", "поворот: малый угол", (in LocomotionRules r) => r.TurnSmallAngle,
                (ref LocomotionRules r, float v) => r.TurnSmallAngle = v, 0, 180, d.TurnSmallAngle, format: "0",
                hint: "курс стоит дольше выдержки, а расхождение с ногами больше, градусы, — ноги доворачиваются клипом поворота")
            .Float("turnHold", "поворот: выдержка", (in LocomotionRules r) => r.TurnHold, (ref LocomotionRules r, float v) => r.TurnHold = v,
                0, 3, d.TurnHold, format: "0.00", hint: "сколько секунд курс корня стоит, прежде чем ноги доворачиваются на малое расхождение")
            .Float("turnMax", "поворот: потолок", (in LocomotionRules r) => r.TurnMax, (ref LocomotionRules r, float v) => r.TurnMax = v,
                0, 180, d.TurnMax, format: "0",
                hint: "наибольшее расхождение курса корня и ног, градусы: дальше ноги подтягиваются (скользят); 0 — без скручивания и клипов поворота")
            .Float("turnReturn", "поворот: возврат на ходу", (in LocomotionRules r) => r.TurnReturn, (ref LocomotionRules r, float v) => r.TurnReturn = v,
                0.01f, 1, d.TurnReturn, format: "0.00", hint: "пошёл — расхождение ног и корня гаснет к 0 не дольше стольких секунд");
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
/// Модуль Locomotion: локомоция клипами у врагов и у игрока при <c>locomotion.player = clips</c>, Motion Matching игрока
/// при <c>mm</c>. Без модуля ходьбы нет: корень движется, поза — поза привязки. <c>clips</c> регистрирует только локомоцию
/// клипами (мир побитово тот же); всё MM — только при <c>mm</c>.
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
            // Поза постройки бойца — стойка с нулевого момента: по ней ядро ставит тело при постройке; без модуля — поза привязки.
            // Игрок на MM строится по своей.
            .OnWorldBuilt(w =>
            {
                var start = LegacyLocomotion.Start(w.Resource<ActionTable>());
                w.SetResourceImmediate(new StartPose { Clip = start.Clip, Time = start.Time });
            });
        if (mm) ConfigureMotionMatching(b, settings);
    }

    /// <summary>
    /// Motion Matching игрока: база и хранилище проигрывателей при сборке мира (ошибка данных — ошибка старта, без отката
    /// на клипы), компонент, системы, ручки, участник хеша <c>locomotion</c>, инвариант <c>locomotion.mm</c>.
    /// </summary>
    private static void ConfigureMotionMatching(SimulationBuilder b, LocomotionSettings settings)
    {
        var rules = LocomotionRules.From(settings);
        var runtime = new MotionMatchRuntime();
        var arm = new SwordArm();   // рука с мечом поверх позы MM
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
                arm.Init(w, runtime);   // маска руки, поза постройки со стойкой руки, состояние руки игроку
                Log.Info("locomotion", runtime.Base.StartLine());
                MotionMatchSetupSystem.PlaceWorldBuilt(w, runtime);
            });
        arm.Configure(b);
        MotionMatchObservers.Register(b, runtime);   // вид, события переходов, тяжёлые инварианты, сведение реплея
        LocomotionKnobs.Register(b);
    }
}
