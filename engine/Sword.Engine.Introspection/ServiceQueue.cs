using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection;

/// <summary>
/// Что видит запрос в точке обслуживания (раздел 8 плана, «Потоковая модель»): мир между тиками, согласованный, —
/// только для чтения, и постановка команды тика на будущий тик. Живёт ровно на время запроса: сохранять его нельзя.
/// </summary>
public readonly struct ServiceContext
{
    private readonly Simulation _simulation;

    public ServiceContext(Simulation simulation, ulong? frame = null)
    {
        _simulation = simulation;
        Frame = frame;
    }

    /// <summary>Мир на конце последнего тика.</summary>
    public WorldReader World => _simulation.World;

    /// <summary>Номер следующего тика (сыграно тиков); посреди тика (<see cref="Stage"/>) — номер идущего тика.</summary>
    public ulong Tick => _simulation.Tick;

    /// <summary>Стадия, после барьера которой идёт обслуживание (останов посреди тика, <c>time.break</c>); null — между тиками.</summary>
    public string? Stage => _simulation.Stage;

    /// <summary>Ближайший тик, на который можно поставить команду (посреди тика — следующий).</summary>
    public ulong NextTick => _simulation.NextCommandTick;

    /// <summary>Симуляция точки — для наблюдения (<see cref="ObservationHub"/>) и снимков; менять её отсюда нельзя.</summary>
    internal Simulation Simulation => _simulation;

    /// <summary>
    /// Инварианты симуляции (раздел 8 плана): список, включение, проверка сейчас. Мир и хеш от них не зависят — поэтому
    /// менять их включение можно из точки обслуживания.
    /// </summary>
    public SimulationInvariants Invariants => _simulation.Invariants;

    /// <summary>
    /// Диагностические режимы симуляции (трасса физики; <see cref="Simulation.Diagnostics"/>): мир, хеш и реплей от них не зависят —
    /// поэтому включать их можно из точки обслуживания (между тиками; посреди тика — <see cref="InvalidOperationException"/>).
    /// </summary>
    public SimulationDiagnostics Diagnostics => _simulation.Diagnostics;

    /// <summary>Есть ли диагностический режим <paramref name="id"/> (инструментам игры — без типов Runtime).</summary>
    public bool HasDiagnostic(string id) => _simulation.Diagnostics.Has(id);

    /// <summary>Включён ли диагностический режим <paramref name="id"/>; нет такого — false.</summary>
    public bool DiagnosticEnabled(string id) => _simulation.Diagnostics.IsEnabled(id);

    /// <summary>Включить или выключить диагностический режим (между тиками); нет такого — <see cref="ArgumentException"/>.</summary>
    public void SetDiagnostic(string id, bool enabled) => _simulation.Diagnostics.Set(id, enabled);

    /// <summary>Начать окно диагностического режима заново (между тиками).</summary>
    public void ResetDiagnosticWindow(string id) => _simulation.Diagnostics.ResetWindow(id);

    /// <summary>Шаг тика, с.</summary>
    public float Dt => _simulation.Dt;

    /// <summary>Номер кадра клиента, в котором обслуживается запрос; у симуляции без окна — null.</summary>
    public ulong? Frame { get; }

    public IReadOnlyList<string> ActiveModules => _simulation.ActiveModules;
    public IReadOnlyList<string> InactiveModules => _simulation.InactiveModules;

    /// <summary>Типы команд тика.</summary>
    public IReadOnlyList<Type> CommandTypes => _simulation.CommandTypes;

    /// <summary>
    /// Команда тика байтами на тик <paramref name="tick"/> — не раньше ближайшего (<see cref="NextTick"/>) — и на
    /// <paramref name="repeat"/> тиков подряд.
    /// </summary>
    public void Enqueue(Type command, ulong tick, ReadOnlySpan<byte> bytes, int repeat = 1) =>
        _simulation.EnqueueRaw(command, tick, bytes, repeat);

    /// <summary>Хеш состояния по участникам (раздел 7 плана) — между тиками, согласованного мира.</summary>
    public StateHash HashState() => _simulation.HashState();

    /// <summary>Описание участника хеша — ровно то, что он хеширует (JSON).</summary>
    public string DescribeState(string part, in DescribeQuery query) => _simulation.DescribeState(part, query);

    /// <summary>Участники хеша состояния по Id.</summary>
    public IReadOnlyList<string> StateParts => _simulation.StateParts;

    /// <summary>Запись реплея этой симуляции (с тика 0); null — не пишется. Копию берут здесь (<see cref="ReplayRecorder.ToReplay"/>).</summary>
    public ReplayRecorder? Recorder => _simulation.Recorder;

    /// <summary>Воспроизводимый реплей; null — нет.</summary>
    public ReplayPlayer? Player => _simulation.Player;

    /// <summary>Момент этой точки обслуживания: тик, кадр и стадия одного состояния (для квитанции).</summary>
    public ServicePoint Point => new(Tick, Frame, Stage);
}

/// <summary>
/// Момент точки обслуживания: сыграно тиков, кадр клиента (null — без окна) и стадия (null — между тиками; иначе
/// останов посреди тика <see cref="Tick"/> после барьера стадии) — одного и того же состояния.
/// </summary>
public readonly record struct ServicePoint(ulong Tick, ulong? Frame, string? Stage = null);

/// <summary>
/// Очередь запросов к симуляции: поток агента ставит запрос (<see cref="Post{T}"/>) и ждёт ответ, поток симуляции
/// исполняет накопленное в точке обслуживания (<see cref="Serve"/>) — между тиками, когда мир согласован. Запрос только
/// копирует данные (разбор и JSON — на потоке агента) и может поставить команду на будущий тик. Исключение запроса
/// уходит ожидающему, точку обслуживания не роняет. Отменённый запрос (таймаут, отмена вызова) не исполняется вовсе:
/// иначе команда, на которую агент уже получил ошибку, прошла бы позже и повтор дал бы её дважды.
/// </summary>
public sealed class ServiceQueue
{
    private readonly Queue<IWork> _pending = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _closed;

    private interface IWork
    {
        void Run(in ServiceContext context);
        void Cancel();
    }

    private sealed class Work<T>(Func<ServiceContext, T> run) : IWork
    {
        public readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;

        public void Run(in ServiceContext context)
        {
            Registration.Dispose();
            if (Result.Task.IsCompleted) return;   // отменён, пока ждал точку обслуживания
            try
            {
                Result.TrySetResult(run(context));
            }
            catch (Exception e)
            {
                Result.TrySetException(e);
            }
        }

        public void Cancel()
        {
            Registration.Dispose();
            Result.TrySetCanceled();
        }
    }

    /// <summary>Запросов ждёт обслуживания.</summary>
    public int Pending
    {
        get
        {
            lock (_gate) return _pending.Count;
        }
    }

    /// <summary>
    /// Поставить запрос; результат — после ближайшей точки обслуживания. Отмена <paramref name="cancel"/> до исполнения —
    /// запрос не исполнится. После <see cref="CancelAll"/> (симуляция остановлена) — сразу отменён.
    /// </summary>
    public Task<T> Post<T>(Func<ServiceContext, T> run, CancellationToken cancel = default)
    {
        var work = new Work<T>(run);
        if (cancel.CanBeCanceled) work.Registration = cancel.Register(() => work.Result.TrySetCanceled(cancel));
        lock (_gate)
        {
            if (_closed)
            {
                work.Cancel();
                return work.Result.Task;
            }
            _pending.Enqueue(work);
        }
        _signal.Release();
        return work.Result.Task;
    }

    /// <summary>
    /// Точка обслуживания (поток симуляции): исполнить запросы, стоявшие к началу вызова, — не дольше
    /// <paramref name="budget"/> (остальные — в следующей точке). Возвращает, сколько исполнено.
    /// </summary>
    public int Serve(in ServiceContext context, TimeSpan budget)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int count;
        lock (_gate) count = _pending.Count;
        int done = 0;
        for (; done < count; done++)
        {
            if (done > 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start) > budget) break;
            IWork? work;
            lock (_gate)
                if (!_pending.TryDequeue(out work)) break;
            work.Run(context);
        }
        return done;
    }

    /// <summary>Ждать новый запрос (поток симуляции на паузе): true — пришёл, false — время вышло.</summary>
    public bool WaitForWork(TimeSpan timeout, CancellationToken cancel = default)
    {
        try
        {
            return _signal.Wait(timeout, cancel);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Разбудить ждущий поток симуляции (например, пришла команда времени).</summary>
    public void Wake() => _signal.Release();

    /// <summary>Отменить все ждущие запросы и больше не принимать новых (симуляция останавливается).</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            _closed = true;
            while (_pending.Count > 0) _pending.Dequeue().Cancel();
        }
    }
}
