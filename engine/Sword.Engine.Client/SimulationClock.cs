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

    /// <summary>
    /// Допуск накопителя, с. Накопитель считает в точном шаге <c>1 / tickRate</c> (double), а не во float-шаге тика
    /// (1/120 ≈ 0,0083333338): иначе «кадр 1/60 с» набирал бы 1,99999994 тика, недобор копился бы от кадра к кадру и
    /// заданный шаг кадра (<c>--fps</c>) терял тики. Допуск — от округления double; на часах наносекунда незаметна.
    /// </summary>
    private const double Epsilon = 1e-12;

    private readonly Simulation _simulation;
    private readonly Action<SimulationLink, TickInput> _beforeTick;
    private readonly Action<ulong>? _afterTick;
    private readonly InputEdges? _edges;
    private readonly int _ticksPerFrame;
    private double _accumulator;
    private bool _gated;

    /// <param name="beforeTick">Команды игры на тик (<see cref="IClientGame.BeforeTick"/>) — с фронтами ввода для тика.</param>
    /// <param name="ticksPerFrame">Ровно столько тиков на кадр; 0 — по часам.</param>
    /// <param name="edges">Защёлка фронтов ввода: первый тик после кадра забирает их; null — без ввода.</param>
    /// <param name="afterTick">
    /// Наблюдатель тиков (клиентские модули, <see cref="ClientModules.AfterTick"/>): после каждого тика, до ворот инструмента, с <c>TickId</c>
    /// сыгранного тика (номер с 0); мир — после своего тика. Null — без наблюдателя.
    /// </param>
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

    /// <summary>Ворота тиков (инструмент: пауза, шаги); null — тики по часам или заданные.</summary>
    public ITickGate? Gate { get; set; }

    /// <summary>
    /// Доля следующего тика, прошедшая по часам после тиков кадра (<c>accumulator / dt</c> SWORD): 0 — показать прошлый
    /// тик, 1 — последний. При заданном числе тиков на кадр и при тиках, заданных воротами (пауза, шаги), — 1: кадр
    /// показывает ровно состояние тика.
    /// </summary>
    public float Alpha => _ticksPerFrame > 0 || _gated ? 1f : (float)Math.Clamp(_accumulator / TickSeconds, 0, 1);

    /// <summary>Точный шаг тика для накопителя, с.</summary>
    private double TickSeconds => 1.0 / _simulation.Settings.TickRate;

    /// <summary>
    /// Шаг кадра для представления (сглаживание камеры): при заданном числе тиков — их время, иначе как есть, без
    /// ограничения 0,25 с (SWORD отдаёт камере сырой <c>frameDt</c>: после паузы камера догоняет цель сразу).
    /// </summary>
    public float FrameSeconds(double measured) =>
        _ticksPerFrame > 0 ? _ticksPerFrame * _simulation.Dt : (float)(measured > 0 ? measured : 0);

    /// <summary>
    /// Тики кадра длиной <paramref name="frameSeconds"/> (отрицательная и нечисловая — ноль: часы не встают навсегда);
    /// вернёт, сколько тиков сделано.
    /// </summary>
    public int RunFrame(double frameSeconds)
    {
        if (!(frameSeconds > 0)) frameSeconds = 0;
        _simulation.Export.BeginFrame();
        if (Gate?.Override(_simulation.Settings.MaxTicksPerFrame) is { } forced)
        {
            // Пауза или шаги инструмента: часы стоят, кадр показывает последний тик.
            int played = 0;
            while (played < forced)
            {
                Tick();
                played++;
                if (Gate.StopAfterTick(_simulation.Tick)) break;
            }
            _accumulator = 0;
            _gated = true;
            Gate.Ran(_simulation.Tick);
            return played;
        }
        _gated = false;
        int done = RunByClock(frameSeconds);
        Gate?.Ran(_simulation.Tick);
        return done;
    }

    private int RunByClock(double frameSeconds)
    {
        if (_ticksPerFrame > 0)
        {
            for (int i = 0; i < _ticksPerFrame; i++)
            {
                Tick();
                if (Gate?.StopAfterTick(_simulation.Tick) == true) return i + 1;
            }
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
            if (Gate?.StopAfterTick(_simulation.Tick) == true)
            {
                _accumulator = 0;   // встали посреди кадра: несыгранное время кадра не догоняется после паузы
                return ticks;
            }
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
