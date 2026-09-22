using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;

namespace Sword.Engine.Agent;

/// <summary>
/// Симуляция сеанса агента (Headless, этап 3б): текущая симуляция, её запись с тика 0 и воспроизводимый реплей. Её меняют
/// <c>session.start</c> (новая симуляция из конфига или реплея) и <c>time.seek</c> назад (пересборка из конфига сеанса и
/// повтор таймлайна: сыгранное — из записи, будущее воспроизводимого реплея — из файла). Меняет её только поток
/// симуляции — в цикле (<see cref="SimulationLoop"/>) между точками обслуживания: операции агента ставятся в очередь
/// (<see cref="Post{T}"/>). Без построителя (<c>build</c> — только у Headless) пересборки нет: перемотка — только вперёд.
/// </summary>
public sealed class AgentSimulation
{
    private readonly Func<ConfigSet, Simulation>? _build;
    private readonly Func<IReadOnlyList<string>, ConfigSet>? _load;
    private readonly object _gate = new();
    private readonly Queue<(Action Run, Action Cancel)> _ops = new();
    private bool _closed;

    /// <param name="initial">Симуляция на старте сеанса; на тике 0 и без записи — запись начинается здесь.</param>
    /// <param name="info">Сборка и контент — в заголовок реплеев сеанса.</param>
    /// <param name="build">Симуляция из конфига — та же композиция, что у exe (конфиг — до <see cref="ConfigSet.Complete"/> включительно).</param>
    /// <param name="load">Конфиг запуска из файлов и ключей <c>--set</c> — для <c>session.start</c> без реплея.</param>
    public AgentSimulation(Simulation initial, ReplayInfo info, Func<ConfigSet, Simulation>? build = null,
        Func<IReadOnlyList<string>, ConfigSet>? load = null)
    {
        Info = info;
        _build = build;
        _load = load;
        Current = initial;
        if (initial.Recorder is null && initial.Tick == 0) initial.Record(info);
    }

    public ReplayInfo Info { get; }

    /// <summary>Текущая симуляция. Читать и менять — только на потоке симуляции.</summary>
    public Simulation Current { get; private set; }

    /// <summary>Номер симуляции в сеансе: 1 — стартовая, дальше — после каждой смены.</summary>
    public int Run { get; private set; } = 1;

    /// <summary>Файл воспроизводимого реплея (<c>session.start</c> с реплеем); null — нет.</summary>
    public string? SourceFile { get; private set; }

    /// <summary>Умеет ли процесс собирать симуляцию заново (<c>session.start</c>, <c>time.seek</c> назад, <c>replay.check</c>).</summary>
    public bool CanRebuild => _build is not null;

    /// <summary>Симуляция сменилась (поток симуляции): цикл обновляет контекст обслуживания, каталог и время.</summary>
    public event Action<Simulation>? Changed;

    /// <summary>Собрать отдельную симуляцию (например, для <c>replay.check</c>): с любого потока — ассеты общие и неизменяемые.</summary>
    public Simulation Build(ConfigSet config) =>
        (_build ?? throw new InvalidOperationException("этот процесс не собирает симуляции заново — нужен Sword.Headless --agent"))(config);

    /// <summary>Конфиг запуска из файлов и ключей (как при старте exe).</summary>
    public ConfigSet LoadConfig(IReadOnlyList<string> sets) =>
        (_load ?? throw new InvalidOperationException("этот процесс не собирает симуляции заново — нужен Sword.Headless --agent"))(sets);

    /// <summary>Операция над симуляцией сеанса — исполнится на потоке симуляции в ближайшем обороте цикла.</summary>
    public Task<T> Post<T>(Func<AgentSimulation, T> op, CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancel.CanBeCanceled ? cancel.Register(() => done.TrySetCanceled(cancel)) : default;
        void Run()
        {
            registration.Dispose();
            if (done.Task.IsCompleted) return;   // отменена, пока ждала
            try
            {
                done.TrySetResult(op(this));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }
        lock (_gate)
        {
            if (_closed)
            {
                done.TrySetCanceled();
                return done.Task;
            }
            _ops.Enqueue((Run, () => done.TrySetCanceled()));
        }
        return done.Task;
    }

    /// <summary>Поток симуляции: исполнить поставленные операции. true — что-то исполнено.</summary>
    internal bool RunPending()
    {
        bool any = false;
        while (true)
        {
            (Action Run, Action Cancel) op;
            lock (_gate)
                if (!_ops.TryDequeue(out op)) return any;
            op.Run();
            any = true;
        }
    }

    /// <summary>Симуляция остановлена: ждущие операции отменяются, новые — сразу отменены.</summary>
    internal void CancelAll()
    {
        lock (_gate)
        {
            _closed = true;
            while (_ops.Count > 0) _ops.Dequeue().Cancel();
        }
    }

    /// <summary>
    /// Новая симуляция (поток симуляции): из <paramref name="config"/>, с реплеем — воспроизводит его; запись — с тика 0.
    /// </summary>
    public Simulation Start(ConfigSet config, Replay? replay, string? file)
    {
        var sim = Build(config);
        if (replay is not null) sim.Play(replay);
        sim.Record(Info);
        Swap(sim, replay is null ? null : file);
        return sim;
    }

    /// <summary>
    /// Перемотка (поток симуляции): вперёд — тики подряд; назад — новая симуляция из конфига сеанса, которая
    /// воспроизводит таймлайн до <paramref name="tick"/> (прежнее будущее — дальше в таймлайне; команды очереди на ещё не
    /// сыгранные тики остаются у прежней симуляции — сбрасываются). Возвращает, пересобрана ли симуляция. Отмена вперёд
    /// оставляет сыгранные тики (симуляция та же, <see cref="Changed"/> нет); отмена назад — прежнюю симуляцию.
    /// </summary>
    public bool Seek(ulong tick, CancellationToken cancel = default)
    {
        var sim = Current;
        if (tick >= sim.Tick)
        {
            RunTo(sim, tick, cancel);
            return false;
        }
        if (sim.Recorder is null) throw new InvalidOperationException("перемотка назад: симуляция сеанса не пишет реплей с тика 0");
        var timeline = sim.Recorder.ToReplay();
        if (sim.Player is { } player && player.Length > sim.Tick) timeline = Replay.Splice(timeline, player.Replay, sim.Tick);
        var fresh = Build(timeline.CreateConfig());
        fresh.Play(timeline);
        fresh.Record(Info);
        RunTo(fresh, tick, cancel);
        Swap(fresh, SourceFile);
        return true;
    }

    private static void RunTo(Simulation sim, ulong tick, CancellationToken cancel)
    {
        while (sim.Tick < tick)
        {
            if ((sim.Tick & 1023) == 0) cancel.ThrowIfCancellationRequested();
            sim.Export.BeginFrame();
            sim.Step();
        }
    }

    private void Swap(Simulation sim, string? file)
    {
        Current = sim;
        SourceFile = file;
        Run++;
        Log.Info("agent", $"симуляция сеанса №{Run}: тик {sim.Tick}{(file is null ? "" : $", реплей {file}")}");
        Changed?.Invoke(sim);
    }
}
