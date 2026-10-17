using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Impact;

/// <summary>Секция конфига <c>impact</c>: массы, доля клинка, тело и пороги баланса; подгоняются ручками «Сила удара».</summary>
public sealed record ImpactSettings
{
    /// <summary>Эффективная масса клинка с рукой, кг (<c>impact.bladeMass</c>).</summary>
    public float BladeMass { get; init; } = 3f;

    /// <summary>Эффективная масса тупого оружия с руками, кг (<c>impact.bluntMass</c>).</summary>
    public float BluntMass { get; init; } = 6f;

    /// <summary>Доля импульса клинка, которая доходит до тела (рез гасит удар) (<c>impact.bladeShare</c>).</summary>
    public float BladeShare { get; init; } = 0.35f;

    /// <summary>Упругость удара: импульс × (1 + e) (<c>impact.restitution</c>).</summary>
    public float Restitution { get; init; } = 0.2f;

    /// <summary>Масса тела, кг, и его инерция вокруг центра масс, кг·м² (<c>impact.bodyMass</c>, <c>impact.bodyInertia</c>).</summary>
    public float BodyMass { get; init; } = 75f;

    public float BodyInertia { get; init; } = 10f;

    /// <summary>Высота центра масс над опорой, м (<c>impact.comHeight</c>): маятник баланса.</summary>
    public float ComHeight { get; init; } = 1f;

    /// <summary>Сдвиг точки захвата, с которого боец пошатывается, м (<c>impact.staggerShift</c>).</summary>
    public float StaggerShift { get; init; } = 0.12f;

    /// <summary>Сдвиг точки захвата, с которого боец падает, м (<c>impact.fallShift</c>).</summary>
    public float FallShift { get; init; } = 0.45f;

    /// <summary>Множитель сдвига баланса от удара по ногам (<c>impact.legFactor</c>).</summary>
    public float LegFactor { get; init; } = 1.6f;

    /// <summary>Время восстановления баланса, с (<c>impact.recovery</c>): накопленный сдвиг гаснет по экспоненте.</summary>
    public float Recovery { get; init; } = 0.5f;

    /// <summary>Наименьшая сила вздрагивания (<c>impact.minFlinch</c>).</summary>
    public float MinFlinch { get; init; } = 0.15f;

    /// <summary>Наибольший толчок корня, м/с (<c>impact.maxPush</c>).</summary>
    public float MaxPush { get; init; } = 2.5f;
}

/// <summary>Правила силы удара — ресурс мира из секции <c>impact</c>; ручки «Сила удара» меняют его командой.</summary>
public struct ImpactRules
{
    public float BladeMass, BluntMass, BladeShare, Restitution, BodyMass, BodyInertia, ComHeight, StaggerShift, FallShift, LegFactor, Recovery,
        MinFlinch, MaxPush;

    /// <exception cref="InvalidDataException">Значение не конечное или вне допустимого.</exception>
    public static ImpactRules From(ImpactSettings s)
    {
        static float Positive(float value, string key) =>
            float.IsFinite(value) && value > 0 ? value : throw new InvalidDataException($"impact.{key} = {value}: нужно конечное больше 0");
        static float Share(float value, string key) =>
            float.IsFinite(value) && value is >= 0 and <= 1 ? value : throw new InvalidDataException($"impact.{key} = {value}: нужно 0…1");
        var r = new ImpactRules
        {
            BladeMass = Positive(s.BladeMass, "bladeMass"), BluntMass = Positive(s.BluntMass, "bluntMass"), BladeShare = Share(s.BladeShare, "bladeShare"),
            Restitution = Share(s.Restitution, "restitution"), BodyMass = Positive(s.BodyMass, "bodyMass"), BodyInertia = Positive(s.BodyInertia, "bodyInertia"),
            ComHeight = Positive(s.ComHeight, "comHeight"), StaggerShift = Positive(s.StaggerShift, "staggerShift"), FallShift = Positive(s.FallShift, "fallShift"),
            LegFactor = Positive(s.LegFactor, "legFactor"), Recovery = Positive(s.Recovery, "recovery"), MinFlinch = Share(s.MinFlinch, "minFlinch"),
            MaxPush = Positive(s.MaxPush, "maxPush"),
        };
        if (r.FallShift <= r.StaggerShift) throw new InvalidDataException($"impact.fallShift = {r.FallShift}: нужно больше staggerShift ({r.StaggerShift})");
        return r;
    }
}

/// <summary>Баланс бойца (модуль Impact, хешируется): накопленный сдвиг точки захвата, м, и время последнего удара, с.</summary>
public struct Balance
{
    public float Shift;
    public float Time;
}

/// <summary>
/// Модуль Impact (<c>impact</c>), часть симуляции: в стадии Dismember каждое попадание по живому — импульс телу, сдвиг баланса и
/// отклик (<see cref="HitResponse"/>): вздрагивание по силе, пошатывание, падение; толчок корня и отдача оружия атакующего. Исполняет
/// ядро. Без модуля попадания не толкают и реакций нет.
/// </summary>
public sealed class ImpactModule : IModule
{
    public const string ModuleId = "impact";

    public string Id => ModuleId;

    public void Configure(SimulationBuilder b)
    {
        var settings = b.Settings<ImpactSettings>();
        var d = new ImpactSettings();
        b.Component<Balance>()
            .Resource(ImpactRules.From(settings))
            .System(GameStages.Dismember, new ImpactSystem());
        b.Tuning<ImpactRules>(ModuleId)
            .Float("bladeMass", "масса клинка", (in ImpactRules r) => r.BladeMass, (ref ImpactRules r, float v) => r.BladeMass = v,
                0.5f, 20, d.BladeMass, log: true, format: "0.0", hint: "эффективная масса клинка с рукой, кг")
            .Float("bluntMass", "масса молота", (in ImpactRules r) => r.BluntMass, (ref ImpactRules r, float v) => r.BluntMass = v,
                0.5f, 40, d.BluntMass, log: true, format: "0.0", hint: "эффективная масса тупого оружия с руками, кг")
            .Float("bladeShare", "доля клинка", (in ImpactRules r) => r.BladeShare, (ref ImpactRules r, float v) => r.BladeShare = v,
                0, 1, d.BladeShare, format: "0.00", hint: "доля импульса клинка, которая доходит до тела: рез гасит удар")
            .Float("staggerShift", "порог пошатывания", (in ImpactRules r) => r.StaggerShift, (ref ImpactRules r, float v) => r.StaggerShift = v,
                0.02f, 1, d.StaggerShift, format: "0.00", hint: "сдвиг точки захвата, м, с которого враг пошатывается")
            .Float("fallShift", "порог падения", (in ImpactRules r) => r.FallShift, (ref ImpactRules r, float v) => r.FallShift = v,
                0.05f, 3, d.FallShift, format: "0.00", hint: "сдвиг точки захвата, м, с которого враг падает")
            .Float("legFactor", "удар по ногам", (in ImpactRules r) => r.LegFactor, (ref ImpactRules r, float v) => r.LegFactor = v,
                1, 4, d.LegFactor, format: "0.0", hint: "множитель сдвига баланса от удара по ногам")
            .Float("recovery", "восстановление", (in ImpactRules r) => r.Recovery, (ref ImpactRules r, float v) => r.Recovery = v,
                0.05f, 5, d.Recovery, log: true, format: "0.00", hint: "время восстановления баланса, с: удары подряд копятся")
            .Float("maxPush", "наибольший толчок", (in ImpactRules r) => r.MaxPush, (ref ImpactRules r, float v) => r.MaxPush = v,
                0.1f, 10, d.MaxPush, format: "0.0", hint: "наибольшая прибавка скорости корня от удара, м/с");
    }
}
