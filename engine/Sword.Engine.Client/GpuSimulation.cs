using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Engine.Client;

/// <summary>
/// GPU-симуляция клиентского модуля (раздел 6 плана, «GPU-симуляции»): представление, вне симуляции, хеша и реплея. Хост создаёт её,
/// когда рендер готов (<see cref="Create"/>), каждый кадр — в фазе <see cref="FramePhase.Simulation"/> — отдаёт ей тики кадра с их заявками
/// (<see cref="AddPasses"/>), а данные асинхронного чтения — с задержкой <see cref="GpuReadback.Latency"/> кадров (<see cref="OnReadback"/>).
/// Шаг решает сама симуляция по <see cref="SimFrame"/>: например, 60 Гц — на каждом втором <c>TickId</c> кадра (при 120 тиках/с); кадр
/// без тиков (пауза, перетаскивание рамки) — ни одного шага.
/// </summary>
public interface IGpuSimulation
{
    /// <summary>Имя для итога и журнала (латиница, <c>fire.torch</c>).</summary>
    string Name { get; }

    /// <summary>Постоянные ресурсы (двойная буферизация состояния) — один раз, рендер готов.</summary>
    void Create(GpuSimContext context);

    /// <summary>Проходы кадра — только фазы <see cref="FramePhase.Simulation"/>: шаги на тиках <paramref name="sim"/> с заявками своих тиков.</summary>
    void AddPasses(RenderFrame frame, in SimFrame sim);

    /// <summary>
    /// Данные чтения (<see cref="GpuSimContext.Readback"/>) кадра, отправленного <see cref="GpuReadback.Latency"/> кадров назад: только
    /// представление и диагностика — на авторитетную симуляцию не влияют никогда.
    /// </summary>
    void OnReadback(ReadOnlySpan<byte> data);
}

/// <summary>Что GPU-симуляция получает при создании: рендер, шаг тика, асинхронное чтение, владение ресурсами.</summary>
public sealed class GpuSimContext
{
    private readonly IGpuSimulation _simulation;
    private readonly List<IDisposable> _owned;
    private readonly ReadbackHandler _readback;

    internal GpuSimContext(string module, IGpuSimulation simulation, Renderer renderer, float tickDt, List<IDisposable> owned)
    {
        Module = module;
        _simulation = simulation;
        Renderer = renderer;
        TickDt = tickDt;
        _owned = owned;
        _readback = (data, _) => _simulation.OnReadback(data);
    }

    /// <summary>Модуль-владелец.</summary>
    public string Module { get; }

    public Renderer Renderer { get; }

    /// <summary>Шаг тика симуляции, с (1/120).</summary>
    public float TickDt { get; }

    /// <summary>Читать первые <paramref name="bytes"/> байт хранилища каждый кадр — в <see cref="IGpuSimulation.OnReadback"/>.</summary>
    public void Readback(GpuStorage storage, uint bytes) => Own(Renderer.CreateReadback(storage, bytes, _readback));

    /// <summary>Освободить вместе с модулем (после ожидания GPU, до рендера).</summary>
    public T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }
}

/// <summary>
/// Тики кадра для GPU-симуляции: <c>TickId</c> первого (номер сыгранного тика — как <c>SimTime.Tick</c> в системах и записи очереди кадра,
/// с 0), сколько их (0 — кадр без тиков: пауза, перетаскивание рамки), шаг тика и заявки CPU каждого тика (<see cref="SimRequests"/>).
/// </summary>
public readonly struct SimFrame(ulong firstTick, int ticks, float dt, SimRequests? requests = null)
{
    /// <summary><c>TickId</c> первого тика кадра; без тиков — тика, который будет следующим.</summary>
    public ulong FirstTick { get; } = firstTick;

    public int Ticks { get; } = ticks;

    /// <summary>Шаг тика, с.</summary>
    public float Dt { get; } = dt;

    /// <summary><c>TickId</c> последнего тика кадра; без тиков — <see cref="FirstTick"/> − 1.</summary>
    public ulong LastTick => FirstTick + (ulong)Ticks - 1;

    /// <summary>Тик <paramref name="tick"/> сыгран этим кадром.</summary>
    public bool Contains(ulong tick) => Ticks > 0 && tick >= FirstTick && tick - FirstTick < (ulong)Ticks;

    /// <summary>Заявок за кадр.</summary>
    public int RequestCount => requests?.Count ?? 0;

    /// <summary>Заявки тика <paramref name="tick"/> по порядку добавления.</summary>
    public ReadOnlySpan<T> Requests<T>(ulong tick) where T : unmanaged => requests is null ? default : requests.Of<T>(tick);

    /// <summary>Все заявки кадра по порядку тиков.</summary>
    public ReadOnlySpan<T> Requests<T>() where T : unmanaged => requests is null ? default : requests.All<T>();
}

/// <summary>
/// Заявки CPU одной GPU-симуляции на тики кадра (спаун капли, точка кисти): добавляет наблюдатель тиков модуля с <c>TickId</c> своего
/// тика — не меньше прошлого; шаг тика t берёт диапазон заявок тика t (<see cref="SimFrame.Requests{T}(ulong)"/>). Кадр начинается — пусто.
/// Тип заявки один на симуляцию (размер проверяется). Без выделений в установившемся режиме: массивы растут удвоением.
/// </summary>
public sealed class SimRequests
{
    private byte[] _data = new byte[256];
    private int _bytes, _size;
    private (ulong Tick, int Start, int Count)[] _ticks = new (ulong, int, int)[16];
    private int _tickCount;

    /// <summary>Заявок с начала кадра.</summary>
    public int Count { get; private set; }

    /// <summary>Заявка <paramref name="request"/> тика <paramref name="tick"/>.</summary>
    public void Add<T>(ulong tick, in T request) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (_size == 0) _size = size;
        else if (_size != size) throw new ArgumentException($"заявка {typeof(T).Name}: {size} Б, а у этой симуляции — {_size} Б (тип заявки один)");
        if (_tickCount > 0 && tick < _ticks[_tickCount - 1].Tick)
            throw new ArgumentOutOfRangeException(nameof(tick), tick, $"заявки — по порядку тиков: последний {_ticks[_tickCount - 1].Tick}");
        if (_bytes + size > _data.Length) Array.Resize(ref _data, Math.Max(_data.Length * 2, _bytes + size));
        MemoryMarshal.Write(_data.AsSpan(_bytes), in request);
        if (_tickCount == 0 || _ticks[_tickCount - 1].Tick != tick)
        {
            if (_tickCount == _ticks.Length) Array.Resize(ref _ticks, _ticks.Length * 2);
            _ticks[_tickCount++] = (tick, Count, 0);
        }
        _ticks[_tickCount - 1].Count++;
        _bytes += size;
        Count++;
    }

    /// <summary>Заявки тика <paramref name="tick"/>; нет — пусто.</summary>
    public ReadOnlySpan<T> Of<T>(ulong tick) where T : unmanaged
    {
        Check<T>();
        for (int i = 0; i < _tickCount; i++)
        {
            var (t, start, count) = _ticks[i];
            if (t == tick) return MemoryMarshal.Cast<byte, T>(_data.AsSpan(start * _size, count * _size));
        }
        return default;
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
    internal void Clear()
    {
        _bytes = _tickCount = 0;
        Count = 0;
    }
}
