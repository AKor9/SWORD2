using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Цикл симуляции без окна под агентом (<c>Sword.Headless --agent</c>, тесты): поток — вызывающий (поток симуляции);
/// после каждого тика и на паузе — точка обслуживания очереди агента, затем операции над симуляцией сеанса
/// (<see cref="AgentSimulation"/>: смена симуляции, перемотка); тики — сколько разрешает управление временем
/// (<see cref="TimeControl"/>); на паузе без запросов поток спит до запроса или команды времени. Клиента нет — очередь
/// кадра экспорта тика живёт один тик. Наблюдение агента (<see cref="AgentHost.Observation"/>) подключено к каждой
/// симуляции сеанса до её первого тика; останов посреди тика (<c>time.break</c>) — точка обслуживания прямо внутри
/// тика, после барьера стадии, пока время не разрешит идти дальше.
/// </summary>
public static class SimulationLoop
{
    /// <summary>Бюджет точки обслуживания за тик: остальные запросы — в следующей точке.</summary>
    public static readonly TimeSpan ServiceBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>Крутить одну симуляцию (без пересборки) до отмены <paramref name="stop"/>.</summary>
    public static void Run(Simulation sim, AgentHost host, CancellationToken stop) =>
        Run(new AgentSimulation(sim, ReplayInfo.Current(host.ContentHash)), host, stop);

    /// <summary>
    /// Крутить симуляцию сеанса до отмены <paramref name="stop"/>; ожидающие запросы и ожидания тиков в конце отменяются.
    /// <paramref name="started"/> — на потоке симуляции, когда сеанс уже подключён к <paramref name="host"/> (до первого
    /// тика): транспорт агента запускают здесь, иначе первый вызов после <c>initialize</c> может не застать симуляцию сеанса.
    /// </summary>
    public static void Run(AgentSimulation session, AgentHost host, CancellationToken stop, Action? started = null)
    {
        var context = new ServiceContext(session.Current);
        void Prepare(Simulation sim)
        {
            host.Observe(sim);
            sim.AttachProfiler(host.Profiler);   // профайлер сеанса — и у новой симуляции (включён или нет — как был)
        }
        void Changed(Simulation sim)
        {
            context = new ServiceContext(sim);
            host.Catalog = new Catalog(sim.World, sim.CommandTypes);
            if (host.Observation.Simulation != sim) host.Observe(sim);
            host.Time.Reset(sim.Tick);
        }
        // Останов посреди тика: мир согласован после барьера стадии — обслуживаем агента здесь, пока время стоит.
        void Break(Simulation sim, string stage)
        {
            Log.Info("agent", $"останов посреди тика {sim.Tick} после барьера стадии {stage}");
            // Тик прерван ожиданием агента: у профайлера он служебный — не в итогах и не аномалия (точка Пв).
            host.Profiler.Interrupt();
            var inTick = new ServiceContext(sim);
            while (!stop.IsCancellationRequested)
            {
                host.Service.Serve(inTick, ServiceBudget);
                // Дальше — когда время разрешит тики (step, runUntil, resume, новый break) или сеанс меняют (seek, start):
                // их операции исполняются между тиками — тик доигрывается.
                if (session.HasPending || !host.Time.Paused || host.Time.Allow(1) > 0) return;
                host.Service.WaitForWork(TimeSpan.FromMilliseconds(250), stop);
            }
        }
        session.Preparing += Prepare;
        session.Changed += Changed;
        host.Simulation = session;
        host.Time.Changed += host.Service.Wake;
        host.Observation.BreakHandler = Break;
        host.Observe(session.Current);
        session.Current.AttachProfiler(host.Profiler);
        try
        {
            started?.Invoke();
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
            host.Observation.BreakHandler = null;
            session.Preparing -= Prepare;
            session.Changed -= Changed;
            host.Time.Changed -= host.Service.Wake;
            session.CancelAll();
            host.Service.CancelAll();
            host.Time.Stop();
        }
    }
}
