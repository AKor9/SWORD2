using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// GPU-время проходов в профайлере кадра (этап П, точка Пг): рендер пишет в открытый кадр времена кадра, разобранного в нём
/// (таймстемпы D3D12 — Rhi и Render, профайлер о GPU не знает), зонами вида <see cref="ProfileZoneKind.Gpu"/>: <c>gpu.frame</c>
/// — кадр целиком, <c>gpu.&lt;проход&gt;</c> — проходы. Итоги, кольцо, медиана и p99 — как у зон CPU, но вне дерева CPU (родителя
/// нет): в своё время кадра, главную зону аномалии и сводку зон CPU они не входят. Отставание — в заголовке кадра
/// (<see cref="ProfileHeader.GpuFrame"/>): в кадре N — времена кадра N − 3.
/// </summary>
public sealed partial class CpuProfiler
{
    /// <summary>Зона GPU-времени кадра целиком (точка Пг).</summary>
    public const string GpuFrameName = "gpu.frame";

    /// <summary>Префикс зон GPU-времени проходов (точка Пг).</summary>
    public const string GpuPrefix = "gpu.";

    /// <summary>
    /// GPU-время <paramref name="ms"/> зоны <paramref name="zone"/> (вида <see cref="ProfileZoneKind.Gpu"/>) — в открытый кадр:
    /// вызов зоны с этим временем, 0 байт. Выключенный профайлер и вне кадра — ничего.
    /// </summary>
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

    /// <summary>
    /// GPU-времена, записанные в открытый кадр, — кадра на <paramref name="lag"/> раньше этого (обычно 3: кадры в полёте; 0 —
    /// разобраны ожиданием простоя GPU). Выключенный профайлер и вне кадра — ничего.
    /// </summary>
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
