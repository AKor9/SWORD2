using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// GPU-время проходов: рендер пишет в открытый кадр времена разобранного кадра зонами <see cref="ProfileZoneKind.Gpu"/>
/// (<c>gpu.frame</c>, <c>gpu.&lt;проход&gt;</c>) — вне дерева CPU. Отставание — <see cref="ProfileHeader.GpuFrame"/>
/// (в кадре N — кадр N − 3).
/// </summary>
public sealed partial class CpuProfiler
{
    /// <summary>Зона GPU-времени кадра целиком.</summary>
    public const string GpuFrameName = "gpu.frame";

    /// <summary>Префикс зон GPU-времени проходов.</summary>
    public const string GpuPrefix = "gpu.";

    /// <summary>GPU-время зоны — в открытый кадр как вызов с 0 байт. Выключенный и вне кадра — ничего.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordGpu(int zone, double ms)
    {
        if (_enabled) AddGpu(zone, ms);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddGpu(int zone, double ms)
    {
        if (_frameAt < 0 || (uint)zone >= (uint)_names.Count) return;
        _frames.Add(zone, (long)(ms * Stopwatch.Frequency / 1000.0), 0);
    }

    /// <summary>GPU-времена открытого кадра — кадра на <paramref name="lag"/> раньше (обычно 3; 0 — после ожидания простоя
    /// GPU). Выключенный и вне кадра — ничего.</summary>
    public void NoteGpuFrame(int lag)
    {
        if (!_enabled || _frameAt < 0) return;
        ulong number = _frames.Number;
        _frames.GpuFrame = lag >= 0 && (ulong)lag <= number ? number - (ulong)lag : ulong.MaxValue;
    }

    /// <summary>Отставание GPU-времён от кадра по последнему кадру кольца с ними; нет — null.</summary>
    private int? RecentGpuLag()
    {
        var s = _frames;
        if (s.Headers is null) return null;
        for (int i = 0; i < s.Filled; i++)
        {
            var h = s.Headers[s.Slot(i)];
            if (h.GpuFrame != ulong.MaxValue) return (int)(h.Number - h.GpuFrame);
        }
        return null;
    }
}
