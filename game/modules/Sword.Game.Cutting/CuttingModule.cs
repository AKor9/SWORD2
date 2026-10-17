using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Cutting;

/// <summary>
/// Секция конфига <c>cutting</c>: энергия удара и прочность ткани. Удельные энергии разреза — порядок величин для мягких тканей и
/// кости (кость — в десятки раз дороже); подгоняются в игре ручками «Расчленение».
/// </summary>
public sealed record CuttingSettings
{
    /// <summary>Эффективная масса клинка с рукой в точке касания, кг (<c>cutting.effectiveMass</c>).</summary>
    public float EffectiveMass { get; init; } = 3f;

    /// <summary>Энергия разреза мягких тканей на площадь сечения, Дж/м² (<c>cutting.softToughness</c>).</summary>
    public float SoftToughness { get; init; } = 2000f;

    /// <summary>Энергия разреза кости на площадь сечения, Дж/м² (<c>cutting.boneToughness</c>).</summary>
    public float BoneToughness { get; init; } = 60000f;

    /// <summary>Кусок меньше этой доли объёма части не отделяется — это неполный разрез (<c>cutting.minPiece</c>).</summary>
    public float MinPiece { get; init; } = 0.03f;

    /// <summary>Эффективная масса тупого оружия с руками, кг (<c>cutting.bluntMass</c>): молот.</summary>
    public float BluntMass { get; init; } = 6f;

    /// <summary>Энергия перелома кости на площадь её сечения, Дж/м² (<c>cutting.boneFracture</c>).</summary>
    public float BoneFracture { get; init; } = 300000f;

    /// <summary>Энергия отрыва части по суставу на площадь сечения у сустава, Дж/м² (<c>cutting.tearToughness</c>).</summary>
    public float TearToughness { get; init; } = 60000f;

    /// <summary>Энергия, с которой голова лопается в клочья, Дж (<c>cutting.headCrush</c>).</summary>
    public float HeadCrush { get; init; } = 350f;
}

/// <summary>Правила разреза — ресурс мира из секции <c>cutting</c>; ручки «Расчленение» меняют его командой.</summary>
public struct CuttingRules
{
    public float EffectiveMass, SoftToughness, BoneToughness, MinPiece, BluntMass, BoneFracture, TearToughness, HeadCrush;

    /// <exception cref="InvalidDataException">Значение не конечное или не больше 0.</exception>
    public static CuttingRules From(CuttingSettings s)
    {
        static float Check(float value, string key) =>
            float.IsFinite(value) && value > 0 ? value : throw new InvalidDataException($"cutting.{key} = {value}: нужно конечное больше 0");
        return new CuttingRules
        {
            EffectiveMass = Check(s.EffectiveMass, "effectiveMass"),
            SoftToughness = Check(s.SoftToughness, "softToughness"),
            BoneToughness = Check(s.BoneToughness, "boneToughness"),
            MinPiece = Check(s.MinPiece, "minPiece"),
            BluntMass = Check(s.BluntMass, "bluntMass"),
            BoneFracture = Check(s.BoneFracture, "boneFracture"),
            TearToughness = Check(s.TearToughness, "tearToughness"),
            HeadCrush = Check(s.HeadCrush, "headCrush"),
        };
    }
}

/// <summary>
/// Модуль Cutting (<c>cutting</c>), часть симуляции: в стадии Dismember решает заявки <see cref="SeverRequested"/> — энергия удара в
/// точке касания против энергии разреза сечения части (<see cref="SeverPhysics"/>): отруб (<see cref="LimbDetached"/>) или неполный
/// разрез (<see cref="PartialCut"/>). Отделение применяет ядро. Без модуля конечности на месте.
/// </summary>
public sealed class CuttingModule : IModule
{
    public const string ModuleId = "cutting";

    public string Id => ModuleId;

    public void Configure(SimulationBuilder b)
    {
        var settings = b.Settings<CuttingSettings>();
        var d = new CuttingSettings();
        b.Resource(CuttingRules.From(settings))
            .System(GameStages.Dismember, new SeverDecisionSystem());
        b.Tuning<CuttingRules>(ModuleId)
            .Float("effectiveMass", "масса удара", (in CuttingRules r) => r.EffectiveMass, (ref CuttingRules r, float v) => r.EffectiveMass = v,
                0.5f, 20, d.EffectiveMass, log: true, format: "0.0",
                hint: "эффективная масса клинка с рукой в точке касания, кг: энергия удара = ½ · масса · скорость² · острота")
            .Float("softToughness", "мягкие ткани", (in CuttingRules r) => r.SoftToughness, (ref CuttingRules r, float v) => r.SoftToughness = v,
                100, 50000, d.SoftToughness, log: true, format: "0",
                hint: "энергия разреза кожи и мышц на площадь сечения, Дж/м²")
            .Float("boneToughness", "кость", (in CuttingRules r) => r.BoneToughness, (ref CuttingRules r, float v) => r.BoneToughness = v,
                1000, 1000000, d.BoneToughness, log: true, format: "0",
                hint: "энергия разреза кости на площадь сечения, Дж/м²")
            .Float("minPiece", "наименьший кусок", (in CuttingRules r) => r.MinPiece, (ref CuttingRules r, float v) => r.MinPiece = v,
                0.005f, 0.3f, d.MinPiece, format: "0.000",
                hint: "кусок меньше этой доли объёма части не отделяется — неполный разрез")
            .Float("bluntMass", "масса молота", (in CuttingRules r) => r.BluntMass, (ref CuttingRules r, float v) => r.BluntMass = v,
                0.5f, 40, d.BluntMass, log: true, format: "0.0", hint: "эффективная масса тупого оружия с руками, кг")
            .Float("boneFracture", "перелом кости", (in CuttingRules r) => r.BoneFracture, (ref CuttingRules r, float v) => r.BoneFracture = v,
                10000, 10000000, d.BoneFracture, log: true, format: "0", hint: "энергия перелома на площадь сечения кости, Дж/м²")
            .Float("tearToughness", "отрыв", (in CuttingRules r) => r.TearToughness, (ref CuttingRules r, float v) => r.TearToughness = v,
                1000, 1000000, d.TearToughness, log: true, format: "0", hint: "энергия отрыва по суставу на площадь сечения у сустава, Дж/м²")
            .Float("headCrush", "голова в клочья", (in CuttingRules r) => r.HeadCrush, (ref CuttingRules r, float v) => r.HeadCrush = v,
                10, 5000, d.HeadCrush, log: true, format: "0", hint: "энергия удара, с которой голова лопается в клочья, Дж");
    }
}
