namespace Sword.Engine.Introspection;

/// <summary>
/// Управление временем симуляции для агента и инструментов (раздел 8: <c>time.pause</c>, <c>time.step</c>,
/// <c>time.runUntil</c>): пауза, запас тиков на паузе, ожидание тика. Хозяин цикла (Headless — после каждого тика,
/// клиент — раз в кадр) спрашивает <see cref="Gate"/> или <see cref="Allow"/>, сколько тиков можно сыграть, и сообщает
/// сыгранное (<see cref="Ran"/>). Потокобезопасно: агент ставит, симуляция исполняет; решение «пауза или нет, и сколько»
/// принимается под одной блокировкой.
/// </summary>
public sealed class TimeControl
{
    private readonly object _gate = new();
    private readonly List<(ulong Tick, TaskCompletionSource<ulong> Done)> _waiters = [];
    private bool _paused, _stopped;
    private ulong _tick, _target;   // сыграно тиков; на паузе играть до _target

    /// <param name="paused">Начать на паузе (Headless с агентом: время двигает агент).</param>
    /// <param name="tick">Сыграно тиков к началу.</param>
    public TimeControl(bool paused, ulong tick = 0)
    {
        _paused = paused;
        _tick = _target = tick;
    }

    /// <summary>Вызывается при изменении: разбудить хозяина цикла (Headless ждёт на паузе).</summary>
    public event Action? Changed;

    public bool Paused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    /// <summary>Сыграно тиков (по сообщениям хозяина цикла).</summary>
    public ulong Tick
    {
        get
        {
            lock (_gate) return _tick;
        }
    }

    /// <summary>Тиков осталось сыграть на паузе (запас <see cref="Step"/> / <see cref="RunUntil"/>); без паузы — 0.</summary>
    public ulong Remaining
    {
        get
        {
            lock (_gate) return _paused && _target > _tick ? _target - _tick : 0;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _target = _tick;   // запас на паузе снимается: пауза — стоять
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _target = _tick;   // запас прошлой паузы не переживает снятие паузы
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Сыграть ещё <paramref name="ticks"/> тиков (на паузе — и остановиться; без паузы — просто дождаться). Возвращает
    /// тик, после которого остановимся (на паузе — после уже заказанного запаса).
    /// </summary>
    public ulong Step(ulong ticks)
    {
        ulong target;
        lock (_gate)
        {
            target = (_paused ? Math.Max(_target, _tick) : _tick) + ticks;
            if (_paused) _target = target;
        }
        Changed?.Invoke();
        return target;
    }

    /// <summary>Играть до тика <paramref name="tick"/> (сыграно тиков = <paramref name="tick"/>); прошедший — ничего.</summary>
    public ulong RunUntil(ulong tick)
    {
        lock (_gate)
        {
            if (_paused && tick > _target) _target = tick;
        }
        Changed?.Invoke();
        return tick;
    }

    /// <summary>Хозяин цикла: сколько тиков можно сыграть сейчас, если хочется <paramref name="wanted"/>.</summary>
    public int Allow(int wanted) => Gate(wanted) ?? wanted;

    /// <summary>
    /// Ворота кадра: null — пауза не стоит (тики по часам); число — на паузе столько тиков из запаса (не больше
    /// <paramref name="max"/>). Одно решение под одной блокировкой: снятие паузы между «пауза ли» и «сколько» не даёт
    /// всплеска из <paramref name="max"/> тиков.
    /// </summary>
    public int? Gate(int max)
    {
        lock (_gate)
        {
            if (!_paused) return null;
            ulong left = _target > _tick ? _target - _tick : 0;
            return (int)Math.Min((ulong)Math.Max(max, 0), left);
        }
    }

    /// <summary>Хозяин цикла: сыграно всего <paramref name="tick"/> тиков — ожидающие своего тика отпускаются.</summary>
    public void Ran(ulong tick)
    {
        List<TaskCompletionSource<ulong>>? done = null;
        lock (_gate)
        {
            _tick = tick;
            for (int i = _waiters.Count - 1; i >= 0; i--)
                if (_waiters[i].Tick <= tick)
                {
                    (done ??= []).Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
        }
        if (done is not null)
            foreach (var d in done) d.TrySetResult(tick);
    }

    /// <summary>
    /// Дождаться, когда сыграно не меньше <paramref name="tick"/> тиков. Отмена <paramref name="cancel"/> (в том числе
    /// таймаут вызывающего) убирает ожидание сразу.
    /// </summary>
    public Task<ulong> WhenReached(ulong tick, CancellationToken cancel = default)
    {
        var done = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_tick >= tick) return Task.FromResult(_tick);
            if (_stopped) return Task.FromCanceled<ulong>(new CancellationToken(canceled: true));   // симуляция не пойдёт
            _waiters.Add((tick, done));
        }
        if (cancel.CanBeCanceled)
        {
            var registration = cancel.Register(() =>
            {
                lock (_gate) _waiters.RemoveAll(w => w.Done == done);
                done.TrySetCanceled(cancel);
            });
            done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        }
        return done.Task;
    }

    /// <summary>Сколько ожиданий тика висит (для тестов и диагностики).</summary>
    public int Waiters
    {
        get
        {
            lock (_gate) return _waiters.Count;
        }
    }

    /// <summary>Остановка симуляции: ожидающие отменяются, новые ожидания недостигнутого тика — сразу отменены.</summary>
    public void Stop()
    {
        List<TaskCompletionSource<ulong>> all;
        lock (_gate)
        {
            _stopped = true;
            all = [.. _waiters.Select(w => w.Done)];
            _waiters.Clear();
        }
        foreach (var d in all) d.TrySetCanceled();
    }
}
