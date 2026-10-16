using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Тики кадра клиента: начать очередь кадра экспорта (<see cref="TickExport.BeginFrame"/>) и сделать ровно
/// <c>ticksPerFrame</c> тиков или по часам накопителем (шаг кадра до 0,25 с, не больше <see cref="SimSettings.MaxTicksPerFrame"/>,
/// после упора остаток — до одного тика). Кадр без тиков — пустая очередь. Без окна и GPU — тестируется отдельно.
/// </summary>
public sealed class SimulationClock
{
    /// <summary>Шаг кадра больше этого — пауза (отладчик, перетаскивание окна): симуляция не догоняет её.</summary>
    public const double MaxFrameSeconds = 0.25;

    /// <summary>Допуск накопителя, с. Накопитель считает в точном double-шаге <c>1 / tickRate</c>, а не во float-шаге тика:
    /// иначе кадр 1/60 с набирал бы 1,99999994 тика и заданный <c>--fps</c> терял тики.</summary>
    private const double Epsilon = 1e-12;

    private readonly Simulation _simulation;
    private readonly Action<SimulationLink, TickInput> _beforeTick;
    private readonly Action<ulong>? _afterTick;
    private readonly InputEdges? _edges;
    private readonly int _ticksPerFrame;
    private double _accumulator;

    /// <param name="beforeTick">Команды игры на тик — с фронтами ввода.</param>
    /// <param name="ticksPerFrame">Ровно столько тиков на кадр; 0 — по часам.</param>
    /// <param name="edges">Защёлка фронтов ввода: их забирает первый тик после кадра; null — без ввода.</param>
    /// <param name="afterTick">Наблюдатель после каждого тика (<see cref="ClientModules.AfterTick"/>); null — нет.</param>
    public SimulationClock(Simulation simulation, Action<SimulationLink, TickInput> beforeTick, int ticksPerFrame = 0,
        InputEdges? edges = null, Action<ulong>? afterTick = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticksPerFrame);
        _simulation = simulation;
        _beforeTick = beforeTick;
        _afterTick = afterTick;
        _edges = edges;
        _ticksPerFrame = ticksPerFrame;
        Link = new SimulationLink(simulation);
    }

    /// <summary>Доступ клиентской части игры к симуляции.</summary>
    public SimulationLink Link { get; }

    /// <summary>Доля следующего тика по часам после тиков кадра: 0 — показать прошлый тик, 1 — последний. При заданном
    /// числе тиков на кадр — 1.</summary>
    public float Alpha => _ticksPerFrame > 0 ? 1f : (float)Math.Clamp(_accumulator / TickSeconds, 0, 1);

    /// <summary>Точный шаг тика для накопителя, с.</summary>
    private double TickSeconds => 1.0 / _simulation.Settings.TickRate;

    /// <summary>Шаг кадра для представления (камера): при заданном числе тиков — их время, иначе как есть, без предела
    /// 0,25 с — после паузы камера догоняет цель сразу.</summary>
    public float FrameSeconds(double measured) =>
        _ticksPerFrame > 0 ? _ticksPerFrame * _simulation.Dt : (float)(measured > 0 ? measured : 0);

    /// <summary>Тики кадра длиной <paramref name="frameSeconds"/> (отрицательная и нечисловая — ноль); вернёт число тиков.</summary>
    public int RunFrame(double frameSeconds)
    {
        if (!(frameSeconds > 0)) frameSeconds = 0;
        _simulation.Export.BeginFrame();
        if (_ticksPerFrame > 0)
        {
            for (int i = 0; i < _ticksPerFrame; i++) Tick();
            return _ticksPerFrame;
        }
        double dt = TickSeconds;
        int max = _simulation.Settings.MaxTicksPerFrame, ticks = 0;
        _accumulator += Math.Clamp(frameSeconds, 0, MaxFrameSeconds);
        while (_accumulator + Epsilon >= dt && ticks < max)
        {
            Tick();
            _accumulator = Math.Max(_accumulator - dt, 0);
            ticks++;
        }
        if (ticks == max) _accumulator = Math.Min(_accumulator, dt);
        return ticks;
    }

    /// <summary>Кадр, который симуляцию не двигает (перерисовка при перетаскивании рамки): очередь кадра пуста.</summary>
    public void EmptyFrame() => _simulation.Export.BeginFrame();

    private void Tick()
    {
        _beforeTick(Link, _edges?.ForTick() ?? TickInput.None);
        _edges?.Consume();   // фронты — ровно одному тику
        _simulation.Step();
        _afterTick?.Invoke(_simulation.Tick - 1);
    }
}
