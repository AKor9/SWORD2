using System.Globalization;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Агенту и на экран (только при <c>locomotion.player = mm</c>): вид <see cref="IMotionMatchView"/> для клиента и агента, события
/// переходов <see cref="MotionTransition"/> (журнал ECS и экспорт клиенту) и их система <c>locomotion.mm-events</c>, тяжёлые инварианты
/// <c>locomotion.mmDecay</c> и <c>locomotion.mmContinuity</c>, сведение реплея <c>locomotion.base</c> (отпечаток базы), рантайм MM —
/// ресурсом мира (системы и проверки симуляции; клиенту — нет: у наблюдателя ресурсы только-значения). Зона профайлера
/// <c>locomotion.search</c> и последний поиск — <see cref="MotionMatchDiagnostics"/> (в шаге <c>locomotion.mm</c>).
/// </summary>
public static class MotionMatchObservers
{
    /// <summary>Имя сведения реплея — отпечаток базы (16 шестнадцатеричных цифр).</summary>
    public const string BaseFact = "locomotion.base";

    /// <summary>Регистрации наблюдателей (после регистраций MM игрока: сборка мира вида — после <see cref="MotionMatchRuntime.Init"/>).</summary>
    public static void Register(SimulationBuilder b, MotionMatchRuntime runtime)
    {
        var view = new MotionMatchView(runtime);
        b.Resource(runtime)
            .Event<MotionTransition>()
            .Export<Event<MotionTransition>>()
            .ReadView<IMotionMatchView>(view)
            .System(GameStages.Movement, new MotionMatchEventSystem(runtime))
            .Invariant(new MotionMatchDecayInvariant(runtime))
            .Invariant(new MotionMatchContinuityInvariant(runtime))
            .ReplayFact(BaseFact, () => runtime.Database.Fingerprint.ToString("X16", CultureInfo.InvariantCulture))
            .OnWorldBuilt(w => view.Attach(w.Reader));
    }
}
