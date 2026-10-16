using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Engine.Client;

/// <summary>
/// GPU-симуляция клиентского модуля — представление вне симуляции, хеша и реплея. Каждый кадр в фазе
/// <see cref="FramePhase.Simulation"/> получает тики кадра с заявками (<see cref="AddPasses"/>), чтение — с задержкой
/// <see cref="GpuReadback.Latency"/> кадров. Шаг решает сама (огонь — каждый второй <c>TickId</c>); кадр без тиков — без шага.
/// </summary>
public interface IGpuSimulation
{
    /// <summary>Имя для итога и журнала (латиница, <c>fire.0</c>).</summary>
    string Name { get; }

    /// <summary>Постоянные ресурсы (двойная буферизация состояния) — один раз, рендер готов.</summary>
    void Create(GpuSimContext context);

    /// <summary>Проходы кадра — только фазы <see cref="FramePhase.Simulation"/>: шаги на тиках <paramref name="sim"/> с заявками своих тиков.</summary>
    void AddPasses(RenderFrame frame, in SimFrame sim);

    /// <summary>Данные чтения кадра <see cref="GpuReadback.Latency"/> кадров назад: только представление и диагностика.</summary>
    void OnReadback(ReadOnlySpan<byte> data);
}

/// <summary>Что GPU-симуляция получает при создании: рендер, асинхронное чтение, владение ресурсами.</summary>
public sealed class GpuSimContext
{
    private readonly IGpuSimulation _simulation;
    private readonly List<IDisposable> _owned;
    private readonly ReadbackHandler _readback;

    internal GpuSimContext(string module, IGpuSimulation simulation, Renderer renderer, List<IDisposable> owned)
    {
        Module = module;
        _simulation = simulation;
        Renderer = renderer;
        _owned = owned;
        _readback = (data, _) => _simulation.OnReadback(data);
    }

    /// <summary>Модуль-владелец.</summary>
    public string Module { get; }

    public Renderer Renderer { get; }

    /// <summary>Читать первые <paramref name="bytes"/> байт хранилища каждый кадр — в <see cref="IGpuSimulation.OnReadback"/>.</summary>
    public void Readback(GpuStorage storage, uint bytes) => Own(Renderer.CreateReadback(storage, bytes, _readback));

    /// <summary>Освободить вместе с модулем (после ожидания GPU, до рендера).</summary>
    public T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }
}

/// <summary>Тики кадра для GPU-симуляции: <c>TickId</c> первого (с 0), сколько (0 — пауза), заявки CPU (<see cref="SimRequests"/>).</summary>
public readonly struct SimFrame(ulong firstTick, int ticks, SimRequests? requests = null)
{
    /// <summary><c>TickId</c> первого тика кадра; без тиков — тика, который будет следующим.</summary>
    public ulong FirstTick { get; } = firstTick;

    public int Ticks { get; } = ticks;

    /// <summary>Все заявки кадра по порядку тиков (свой <c>TickId</c> заявка несёт в полях, если он ей нужен).</summary>
    public ReadOnlySpan<T> Requests<T>() where T : unmanaged => requests is null ? default : requests.All<T>();
}

/// <summary>
/// Заявки CPU одной GPU-симуляции на тики кадра: наблюдатель добавляет по неубывающему <c>TickId</c>, симуляция берёт все
/// по порядку (<see cref="SimFrame.Requests{T}()"/>). Тип заявки один на симуляцию; буфер растёт удвоением, без выделений
/// в установившемся режиме.
/// </summary>
public sealed class SimRequests
{
    private byte[] _data = new byte[256];
    private int _bytes, _size;
    private ulong _lastTick;

    /// <summary>Заявка <paramref name="request"/> тика <paramref name="tick"/>.</summary>
    public void Add<T>(ulong tick, in T request) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (_size == 0) _size = size;
        else if (_size != size) throw new ArgumentException($"заявка {typeof(T).Name}: {size} Б, а у этой симуляции — {_size} Б (тип заявки один)");
        if (_bytes > 0 && tick < _lastTick)
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"заявки — по порядку тиков: последний {_lastTick}");
        if (_bytes + size > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, _bytes + size));
        MemoryMarshal.Write(_data.AsSpan(_bytes), in request);
        _lastTick = tick;
        _bytes += size;
    }

    /// <summary>Все заявки кадра по порядку тиков.</summary>
    public ReadOnlySpan<T> All<T>() where T : unmanaged
    {
        Check<T>();
        return MemoryMarshal.Cast<byte, T>(_data.AsSpan(0, _bytes));
    }

    private void Check<T>() where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (_size != 0 && size != _size) throw new ArgumentException($"заявка {typeof(T).Name}: {size} Б, а у этой симуляции — {_size} Б");
    }

    /// <summary>Начало кадра: заявок нет (память остаётся).</summary>
    internal void Clear() => _bytes = 0;
}
