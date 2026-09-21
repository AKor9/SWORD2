using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Тики кадра клиента (раздел 7 плана, шаг Simulate): каждый кадр начинает очередь кадра экспорта тика
/// (<see cref="TickExport.BeginFrame"/>) и делает свои тики — ровно <c>ticksPerFrame</c> или по часам накопителем
/// (<c>Program.cs</c> SWORD: шаг кадра до 0,25 с, не больше <see cref="SimSettings.MaxTicksPerFrame"/> тиков, после
/// упора остаток срезается до одного тика). Перед каждым тиком — команды игры на этот тик. Кадр без тиков тоже кадр:
/// его очередь пуста, записи прошлого кадра не повторяются. Без окна и GPU — поэтому проверяется тестами отдельно
/// от <see cref="ClientHost"/>.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>Шаг кадра больше этого — пауза (отладчик, перетаскивание окна): симуляция не догоняет её.</summary>
    public const double MaxFrameSeconds = 0.25;

    private readonly Simulation _simulation;
    private readonly Action<SimulationLink> _beforeTick;
    private readonly int _ticksPerFrame;
    private double _accumulator;

    /// <param name="beforeTick">Команды игры на тик (<see cref="IClientGame.BeforeTick"/>).</param>
    /// <param name="ticksPerFrame">Ровно столько тиков на кадр; 0 — по часам.</param>
    public SimulationClock(Simulation simulation, Action<SimulationLink> beforeTick, int ticksPerFrame = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticksPerFrame);
        _simulation = simulation;
        _beforeTick = beforeTick;
        _ticksPerFrame = ticksPerFrame;
        Link = new SimulationLink(simulation);
    }

    /// <summary>Доступ клиентской части игры к симуляции.</summary>
    public SimulationLink Link { get; }

    /// <summary>
    /// Тики кадра длиной <paramref name="frameSeconds"/> (отрицательная и нечисловая — ноль: часы не встают навсегда);
    /// вернёт, сколько тиков сделано.
    /// </summary>
    public int RunFrame(double frameSeconds)
    {
        if (!(frameSeconds > 0)) frameSeconds = 0;
        _simulation.Export.BeginFrame();
        if (_ticksPerFrame > 0)
        {
            for (int i = 0; i < _ticksPerFrame; i++) Tick();
            return _ticksPerFrame;
        }
        double dt = _simulation.Dt;
        int max = _simulation.Settings.MaxTicksPerFrame, ticks = 0;
        _accumulator += Math.Clamp(frameSeconds, 0, MaxFrameSeconds);
        while (_accumulator >= dt && ticks < max)
        {
            Tick();
            _accumulator -= dt;
            ticks++;
        }
        if (ticks == max) _accumulator = Math.Min(_accumulator, dt);
        return ticks;
    }

    /// <summary>Кадр, который симуляцию не двигает (перерисовка при перетаскивании рамки): очередь кадра пуста.</summary>
    public void EmptyFrame() => _simulation.Export.BeginFrame();

    private void Tick()
    {
        _beforeTick(Link);
        _simulation.Step();
    }
}
