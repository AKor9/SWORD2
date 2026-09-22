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

    /// <summary>Номер следующего тика (сыграно тиков).</summary>
    public ulong Tick => _simulation.Tick;

    /// <summary>Шаг тика, с.</summary>
    public float Dt => _simulation.Dt;

    /// <summary>Номер кадра клиента, в котором обслуживается запрос; у симуляции без окна — null.</summary>
    public ulong? Frame { get; }

    public IReadOnlyList<string> ActiveModules => _simulation.ActiveModules;
    public IReadOnlyList<string> InactiveModules => _simulation.InactiveModules;

    /// <summary>Типы команд тика.</summary>
    public IReadOnlyList<Type> CommandTypes => _simulation.CommandTypes;

    /// <summary>
    /// Команда тика байтами на тик <paramref name="tick"/> — не раньше следующего (<see cref="Tick"/>) — и на
    /// <paramref name="repeat"/> тиков подряд.
    /// </summary>
    public void Enqueue(Type command, ulong tick, ReadOnlySpan<byte> bytes, int repeat = 1) =>
        _simulation.EnqueueRaw(command, tick, bytes, repeat);

    /// <summary>Момент этой точки обслуживания: тик и кадр одного состояния (для квитанции).</summary>
    public ServicePoint Point => new(Tick, Frame);
}

/// <summary>Момент точки обслуживания: сыграно тиков и кадр клиента (null — без окна) — одного и того же состояния.</summary>
public readonly record struct ServicePoint(ulong Tick, ulong? Frame);

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
