using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Цикл симуляции без окна под агентом (<c>Sword.Headless --agent</c>, тесты): поток — вызывающий (поток симуляции);
/// после каждого тика и на паузе — точка обслуживания очереди агента, затем операции над симуляцией сеанса
/// (<see cref="AgentSimulation"/>: смена симуляции, перемотка); тики — сколько разрешает управление временем
/// (<see cref="TimeControl"/>); на паузе без запросов поток спит до запроса или команды времени. Клиента нет — очередь
/// кадра экспорта тика живёт один тик.
/// </summary>
public static class SimulationLoop
{
    /// <summary>Бюджет точки обслуживания за тик: остальные запросы — в следующей точке.</summary>
    public static readonly TimeSpan ServiceBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>Крутить одну симуляцию (без пересборки) до отмены <paramref name="stop"/>.</summary>
    public static void Run(Simulation sim, AgentHost host, CancellationToken stop) =>
        Run(new AgentSimulation(sim, ReplayInfo.Current(host.ContentHash)), host, stop);

    /// <summary>Крутить симуляцию сеанса до отмены <paramref name="stop"/>; ожидающие запросы и ожидания тиков в конце отменяются.</summary>
    public static void Run(AgentSimulation session, AgentHost host, CancellationToken stop)
    {
        var context = new ServiceContext(session.Current);
        void Changed(Simulation sim)
        {
            context = new ServiceContext(sim);
            host.Catalog = new Catalog(sim.World, sim.CommandTypes);
            host.Time.Reset(sim.Tick);
        }
        session.Changed += Changed;
        host.Simulation = session;
        host.Time.Changed += host.Service.Wake;
        try
        {
            host.Time.Ran(session.Current.Tick);
            while (!stop.IsCancellationRequested)
            {
                host.Service.Serve(context, ServiceBudget);
                if (session.RunPending()) continue;
                if (host.Time.Allow(1) > 0)
                {
                    var sim = session.Current;
                    sim.Export.BeginFrame();
                    sim.Step();
                    host.Time.Ran(sim.Tick);
                    continue;
                }
                host.Service.WaitForWork(TimeSpan.FromMilliseconds(250), stop);
            }
        }
        finally
        {
            session.Changed -= Changed;
            host.Time.Changed -= host.Service.Wake;
            session.CancelAll();
            host.Service.CancelAll();
            host.Time.Stop();
        }
    }
}
