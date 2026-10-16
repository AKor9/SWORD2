using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>Клиентский доступ к симуляции: мир только для чтения, команды на ближайший тик, время тика. Саму
/// <see cref="Simulation"/> держат клиент движка и exe.</summary>
public sealed class SimulationLink
{
    private readonly Simulation _simulation;

    internal SimulationLink(Simulation simulation) => _simulation = simulation;

    /// <summary>
    /// Очередь кадра: записи срабатывания <typeparamref name="T"/> всех тиков кадра с номерами тиков; кадр без тиков — пусто.
    /// Не экспортировано модулем — исключение (<see cref="Exports{T}"/>). Действует до следующего кадра.
    /// </summary>
    public ReadOnlySpan<(ulong Tick, T Record)> Frame<T>() where T : unmanaged => _simulation.Export.Frame<T>();

    /// <summary>Экспортирует ли симуляция срабатывание <typeparamref name="T"/>.</summary>
    public bool Exports<T>() where T : unmanaged => _simulation.Export.Exports<T>();

    /// <summary>Мир только для чтения (Extract, камера). Менять симуляцию можно только командами.</summary>
    public WorldReader World => _simulation.World;

    /// <summary>Номер следующего тика.</summary>
    public ulong Tick => _simulation.Tick;

    /// <summary>Время симуляции на последнем тике, с (<see cref="SimTime.Time"/>).</summary>
    public float Time => _simulation.World.Resource<SimTime>().Time;

    /// <summary>Фиксированный шаг тика, с.</summary>
    public float Dt => _simulation.Dt;

    /// <summary>Принимает ли симуляция команду <typeparamref name="T"/> (без модуля, который её регистрирует, — нет).</summary>
    public bool Accepts<T>() where T : unmanaged => _simulation.Accepts<T>();

    /// <summary>Команда на ближайший тик (удерживаемые действия клиент ставит в каждый тик кадра).</summary>
    public void Enqueue<T>(in T command) where T : unmanaged => _simulation.Enqueue(command);

    /// <summary>Ручки симуляции; изменение — <see cref="TuningCommand"/> на ближайший тик.</summary>
    public IReadOnlyList<TuningKnob> Tuning => _simulation.Tuning;

    /// <summary>Есть ли диагностический режим <paramref name="id"/> (например, <c>physics.trace</c>); без модуля его нет.</summary>
    public bool HasDiagnostic(string id) => _simulation.Diagnostics.Has(id);

    /// <summary>Включён ли диагностический режим <paramref name="id"/>; нет такого — false.</summary>
    public bool DiagnosticEnabled(string id) => _simulation.Diagnostics.IsEnabled(id);

    /// <summary>Включить или выключить диагностический режим между тиками — не команда тика. Нет такого —
    /// <see cref="ArgumentException"/>.</summary>
    public void SetDiagnostic(string id, bool enabled) => _simulation.Diagnostics.Set(id, enabled);
}
