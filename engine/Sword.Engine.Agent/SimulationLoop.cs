using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Цикл симуляции без окна под агентом (<c>Sword.Headless --agent</c>, тесты): поток — вызывающий (поток симуляции);
/// после каждого тика и на паузе — точка обслуживания очереди агента; тики — сколько разрешает управление временем
/// (<see cref="TimeControl"/>); на паузе без запросов поток спит до запроса или команды времени. Клиента нет — очередь
/// кадра экспорта тика живёт один тик.
/// </summary>
public static class SimulationLoop
{
    /// <summary>Бюджет точки обслуживания за тик: остальные запросы — в следующей точке.</summary>
    public static readonly TimeSpan ServiceBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>Крутить до отмены <paramref name="stop"/>; ожидающие запросы и ожидания тиков в конце отменяются.</summary>
    public static void Run(Simulation sim, AgentHost host, CancellationToken stop)
    {
        var context = new ServiceContext(sim);
        host.Time.Changed += host.Service.Wake;
        try
        {
            host.Time.Ran(sim.Tick);
            while (!stop.IsCancellationRequested)
            {
                host.Service.Serve(context, ServiceBudget);
                if (host.Time.Allow(1) > 0)
                {
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
            host.Time.Changed -= host.Service.Wake;
            host.Service.CancelAll();
            host.Time.Stop();
        }
    }
}
