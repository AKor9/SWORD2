using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Захват аномалий (точка Пв): конец кадра и тика включённого профайлера — суд по порогу (<see cref="ProfileAnomalies"/>), при
/// срабатывании — разбор причин, главная зона и копия данных для писателя. Всё — без выделений: массивы заведены при настройке
/// и росте зон, код захвата скомпилирован заранее (иначе первая аномалия платила бы JIT в следующем кадре). Выключенный
/// профайлер сюда не заходит: суд — в невстраиваемых <c>CloseFrame</c>/<c>CloseTick</c> включённого.
/// </summary>
public sealed partial class CpuProfiler
{
    private ProfileAnomalies? _anomalies;

    /// <summary>Лишнее время и своё лишнее время зон аномального периода (скретч владельца).</summary>
    private long[] _excess = [], _selfExcess = [];

    /// <summary>Захват аномалий; null — не настроен.</summary>
    public ProfileAnomalies? Anomalies => _anomalies;

    /// <summary>
    /// Настроить захват аномалий (точка Пв): судит только включённый профайлер; файлы — в <see cref="AnomalyOptions.Directory"/>.
    /// Прежний захват закрывается (его очередь дописывается). Только на потоке владельца, не в кадре (выделяет, компилирует).
    /// </summary>
    /// <exception cref="InvalidOperationException">Это <see cref="Off"/>.</exception>
    public ProfileAnomalies CaptureAnomalies(AnomalyOptions options)
    {
        if (_off) throw new InvalidOperationException("CpuProfiler.Off не захватывает аномалий: подключите свой профайлер");
        _anomalies?.Dispose();
        _anomalies = null;
        if (_capacity < 64) Grow(64);
        var a = new ProfileAnomalies(this, options, _capacity);
        a.SetDisplay(_displayHz);
        WarmCapture();
        _anomalies = a;
        if (_enabled || _wanted) a.RequestWarm();   // уже включён — писатель прогревается сразу (иначе — при включении)
        return a;
    }

    /// <summary>Выключить захват аномалий (очередь дописывается).</summary>
    public void StopAnomalies()
    {
        _anomalies?.Dispose();
        _anomalies = null;
    }

    /// <summary>Дождаться записи файлов аномалий (перед отчётом и выходом). Без захвата — сразу.</summary>
    public bool FlushAnomalies(TimeSpan? timeout = null) => _anomalies?.Flush(timeout ?? TimeSpan.FromSeconds(5)) ?? true;

    private void GrowAnomalies(int capacity)
    {
        Array.Resize(ref _excess, capacity);
        Array.Resize(ref _selfExcess, capacity);
        _anomalies?.Grow(capacity);
    }

    /// <summary>
    /// Код захвата — скомпилирован сейчас, а не в первой аномалии: методы захвата — <c>AggressiveOptimization</c> (сразу
    /// оптимизированные) и <see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/>; сортировка окна, снимок машины и
    /// частота TSC (время потока в мс — её первое чтение калибрует ~8 мс) — прогреты.
    /// </summary>
    private static void WarmCapture()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in (string[])[nameof(Capture), nameof(Classify), nameof(FillSlot), nameof(PendTick), nameof(FrameAnomaly), nameof(TickAnomaly),
            nameof(MeansOf)])
            RuntimeHelpers.PrepareMethod(typeof(CpuProfiler).GetMethod(name, any)!.MethodHandle);
        // И то, что они зовут только на аномалии (не встраивается в оптимизированный код захвата — иначе ~10 методов первой
        // аномалии компилировались бы на главном потоке, найдено при приёмке Пв): список, ведро, места, снимок машины, класс.
        foreach (string name in (string[])[nameof(ProfileAnomalies.Record), nameof(ProfileAnomalies.Admit), nameof(ProfileAnomalies.Acquire),
            nameof(ProfileAnomalies.Submit), nameof(ProfileAnomalies.SetFile), nameof(ProfileAnomalies.Baseline), nameof(ProfileAnomalies.DropPending),
            nameof(ProfileAnomalies.NoteMachine), nameof(ProfileAnomalies.NoteLimit)])
            RuntimeHelpers.PrepareMethod(typeof(ProfileAnomalies).GetMethod(name, any)!.MethodHandle);
        foreach (var m in typeof(ProfileAnomalies.Detector).GetMethods(any | BindingFlags.DeclaredOnly)) RuntimeHelpers.PrepareMethod(m.MethodHandle);
        // Записи — конструкторы и свойства (в Debug и на уровне 0 они не встраиваются: `with` зовёт init-свойства).
        foreach (var type in (Type[])[typeof(ProfileAnomaly), typeof(AnomalyCauses), typeof(ProfileAnomalies), typeof(AnomalyOptions)])
        {
            foreach (var c in type.GetConstructors(any)) RuntimeHelpers.PrepareMethod(c.MethodHandle);
            foreach (var p in type.GetProperties(any | BindingFlags.DeclaredOnly))
                foreach (var m in (MethodInfo?[])[p.GetMethod, p.SetMethod])
                    if (m is not null) RuntimeHelpers.PrepareMethod(m.MethodHandle);
        }
        RuntimeHelpers.PrepareMethod(typeof(Series).GetMethod(nameof(Series.Slot), any)!.MethodHandle);
        Span<long> sort = stackalloc long[8];
        sort.Sort();
        _ = ThreadClock.CyclesPerSecond;
        MachineSample.Take(probe: false);
    }

    /// <summary>
    /// Конец кадра (до записи в кольцо): суд кадра; аномальный тик кадра — пишется с ним. Кадр инструментов (<paramref name="tools"/>,
    /// после Пв) как кадр не судится и в окно медианы не идёт — его лишнее время за работой агента; аномальный тик в нём — пишется
    /// (тики сыграны до точки обслуживания).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void FrameAnomaly(float alpha, bool service, bool tools, in ProfileCounters counters)
    {
        var a = _anomalies!;
        long time = _frames.Time[FrameZone];
        a.NoteMachine(_frames.StartTs + time);
        if (service || tools)
        {
            // Служебный кадр (первый, смена размера, снимок) медленный по делу: не судится и в окно медианы не идёт.
            if (!service && a.Pending.Active) Capture(ProfilePeriod.Frame, alpha, counters, periodHit: false, toolsFrame: true);
            a.DropPending();
            return;
        }
        bool hit = a.Frames.Push(time);
        if (hit || a.Pending.Active) Capture(ProfilePeriod.Frame, alpha, counters, hit, toolsFrame: false);
        a.DropPending();
    }

    /// <summary>Конец тика (до записи в кольцо): суд тика; внутри кадра App — ждёт кадра.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TickAnomaly(in ProfileCounters counters)
    {
        var a = _anomalies!;
        long time = _ticks.Time[TickZone];
        if (_frameAt < 0) a.NoteMachine(_ticks.StartTs + time);
        if (!a.Ticks.Push(time)) return;
        if (_frameAt >= 0) PendTick(counters, time);
        else Capture(ProfilePeriod.Tick, 1, counters, true, toolsFrame: false);
    }

    /// <summary>Аномальный тик кадра App: разбор и копия строки тика — до конца кадра (худший тик кадра).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void PendTick(in ProfileCounters counters, long time)
    {
        var a = _anomalies!;
        var p = a.Pending;
        long over = time - a.Ticks.Threshold;
        if (p.Active && over <= p.Time - p.Threshold) return;   // в кадре уже был тик хуже
        int zones = _names.Count;
        var causes = Classify(_ticks, TickZone, counters, time, a.Ticks.Median, a.Ticks.Absolute, zones, out int main);
        p.Active = true;
        p.Number = _ticks.Number;
        p.Start = _ticks.StartTs;
        p.Time = time;
        p.Median = a.Ticks.Median;
        p.Threshold = a.Ticks.Threshold;
        p.Header = new ProfileHeader(_ticks.Number, _ticks.FirstTick, _ticks.Ticks, 1, time, _ticks.Bytes[TickZone], false, _frames.Number, counters);
        p.Causes = causes;
        p.Zone = main;
        p.ZoneTime = main >= 0 ? _ticks.Time[main] : 0;
        p.ZoneExcess = main >= 0 ? _excess[main] : 0;
        Array.Copy(_ticks.Time, p.ZoneTimes, zones);
        Array.Copy(_ticks.Bytes, p.Bytes, zones);
        Array.Copy(_ticks.Calls, p.Calls, zones);
        MeansOf(_ticks, p.Mean, zones);
    }

    /// <summary>Среднее время зон за период (с последнего сброса, без служебных), отсчёты <see cref="Stopwatch"/>.</summary>
    private static void MeansOf(Series s, long[] into, int zones)
    {
        int n = s.Count;
        for (int z = 0; z < zones; z++) into[z] = n > 0 ? s.TotalTime[z] / n : 0;
    }

    /// <summary>
    /// Разбор лишнего времени периода: лишнее — сверх медианы; JIT главного потока и пауза GC — счётчики периода; ожидание GPU —
    /// сверх среднего у зон вида <see cref="ProfileZoneKind.GpuWait"/>; вне CPU — настенное минус ожидания минус время потока
    /// (без паузы GC); остаток — код. Главная зона — наибольшее своё лишнее (лишнее зоны минус лишнее дочерних); ни у одной
    /// зоны своё лишнее не больше нуля — главной зоны нет (<see cref="ProfileAnomaly.NoZone"/>: абсолютный порог сработал на
    /// периоде не дольше обычного — исправления после Пд, замечание проверяющего; раньше выходила случайная зона с «+0»); так же
    /// при абсолютном пороге, если сам период не дольше своего среднего.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private AnomalyCauses Classify(Series s, int root, in ProfileCounters c, long time, long median, bool absolute, int zones, out int main)
    {
        int n = s.Count;
        var excess = _excess;
        var self = _selfExcess;
        for (int z = 0; z < zones; z++)
        {
            long mean = n > 0 ? s.TotalTime[z] / n : 0;
            excess[z] = s.Time[z] - mean;
            self[z] = excess[z];
        }
        for (int z = 0; z < zones; z++)
        {
            int p = _parent[z];
            if (p >= 0 && z != root) self[p] -= excess[z];
        }
        main = root;
        long best = long.MinValue;
        double wait = 0, gpu = 0;
        for (int z = 0; z < zones; z++)
        {
            if (s.Calls[z] == 0) continue;
            if (self[z] > best && (z == root || _parent[z] >= 0 || _parent[z] == Several))
            {
                best = self[z];
                main = z;
            }
            var kind = (ProfileZoneKind)_kind[z];
            // GPU-время прохода (точка Пг) — не время потока: в ожидания и разбор не идёт (его родителя нет — не главная зона).
            if (kind is ProfileZoneKind.Work or ProfileZoneKind.Gpu) continue;
            wait += Ms(s.Time[z]);
            if (kind == ProfileZoneKind.GpuWait) gpu += Math.Max(0, Ms(excess[z]));
        }
        // Сумма своих лишних — лишнее корня: по медиане (период дольше медианы на порог) хоть одна зона выше своего среднего;
        // по абсолютному порогу период бывает и не дольше обычного — тогда главной зоны нет (а не шум в микросекунды у случайной).
        if (best <= 0 || absolute && excess[root] <= 0) main = ProfileAnomaly.NoZone;
        double wall = Ms(time), extra = Math.Max(0, wall - Ms(median));
        double jit = c.JitMs, gc = c.PauseMs;
        double offCpu = Math.Max(0, wall - wait - c.CpuMs);
        double machine = Math.Max(0, offCpu - gc);
        double code = Math.Max(0, extra - jit - gc - gpu - machine);
        return new AnomalyCauses(extra, jit, gc, gpu, machine, code);
    }

    /// <summary>
    /// Аномалия периода (или кадра App с аномальным тиком): список, файл (если пускают лимит класса и частота; заметную — и сверх
    /// лимита класса, из запаса заметных, но не сверх частоты) — копия писателю. Заметная (после Пв) — кадр не короче интервала показа
    /// (<see cref="ProfileAnomalies.NoticeableMs"/>); тик без кадров и кадр инструментов (его длина — за работой агента) — нет.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void Capture(ProfilePeriod period, float alpha, in ProfileCounters counters, bool periodHit, bool toolsFrame)
    {
        var a = _anomalies!;
        var s = period == ProfilePeriod.Frame ? _frames : _ticks;
        var d = period == ProfilePeriod.Frame ? a.Frames : a.Ticks;
        int root = period == ProfilePeriod.Frame ? FrameZone : TickZone;
        int zones = _names.Count;
        long time = s.Time[root];
        var pending = a.Pending;
        bool tickTrigger = !periodHit;
        AnomalyCauses causes;
        int main;
        long zoneTime, zoneExcess;
        if (periodHit)
        {
            causes = Classify(s, root, counters, time, d.Median, d.Absolute, zones, out main);
            zoneTime = main >= 0 ? s.Time[main] : 0;
            zoneExcess = main >= 0 ? _excess[main] : 0;
        }
        else
        {
            causes = pending.Causes;
            main = pending.Zone;
            zoneTime = pending.ZoneTime;
            zoneExcess = pending.ZoneExcess;
        }
        long now = Now();   // ведро жетонов — по часам профайлера (поддельные в тестах)
        var cls = causes.Class;
        // Заметная: кадр App не короче интервала показа — хотя бы одно обновление экрана показало прежний кадр (поле, не свойство:
        // путь аномалии не зовёт некомпилированного).
        bool noticeable = period == ProfilePeriod.Frame && !toolsFrame && time >= a.NoticeableTicks;
        var record = a.Record(new ProfileAnomaly(0, period, s.Number, pending.Active ? pending.Number : null, tickTrigger,
            Ms(tickTrigger ? pending.Time : time), Ms(tickTrigger ? pending.Median : d.Median), Ms(tickTrigger ? pending.Threshold : d.Threshold),
            Ms(time), causes, cls, main, main >= 0 ? _names[main] : "", Ms(zoneTime), Ms(zoneExcess), 0, AnomalyFile.Queued, null, now, noticeable));
        long seq = record.Sequence;
        var file = a.Admit(now, cls, noticeable, out int bucket);
        var slot = file == AnomalyFile.Queued ? a.Acquire(zones) : null;
        if (file == AnomalyFile.Queued && slot is null) file = AnomalyFile.Busy;   // в лимит идёт только отданное писателю (Submit)
        if (slot is null)
        {
            a.SetFile(seq, file, null);
            return;
        }
        slot.Record = record;   // копия записи: кольцо списка (256) писатель не перечитывает — его могли уже перезаписать
        FillSlot(slot, s, period, root, alpha, counters, zones, seq, periodHit, causes, main, d);
        a.Submit(slot, bucket);
    }

    /// <summary>Копия для писателя: строки зон, средние, родители, виды, последние периоды, тик кадра, машина.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void FillSlot(AnomalySlot slot, Series s, ProfilePeriod period, int root, float alpha, in ProfileCounters counters, int zones,
        long seq, bool periodHit, AnomalyCauses causes, int main, ProfileAnomalies.Detector d)
    {
        var a = _anomalies!;
        slot.Sequence = seq;
        slot.Epoch = a.Epoch;
        slot.Zones = zones;
        slot.Period = period;
        slot.Number = s.Number;
        slot.PeriodHit = periodHit;
        slot.TickTrigger = !periodHit;
        long time = s.Time[root];
        slot.Header = new ProfileHeader(s.Number, s.FirstTick, s.Ticks, alpha, time, s.Bytes[root], false,
            period == ProfilePeriod.Tick && _frameAt >= 0 ? _frames.Number : ulong.MaxValue, counters,
            period == ProfilePeriod.Frame ? s.GpuFrame : ulong.MaxValue);
        slot.Start = s.StartTs;
        slot.Median = d.Median;
        slot.Threshold = d.Threshold;
        slot.AbsoluteMs = d.AbsoluteMs;
        slot.NoticeableTicks = a.NoticeableTicks;
        slot.DisplayHz = a.DisplayHz;
        slot.Causes = causes;
        slot.MainZone = main;
        Array.Copy(s.Time, slot.Time, zones);
        Array.Copy(s.Bytes, slot.Bytes, zones);
        Array.Copy(s.Calls, slot.Calls, zones);
        MeansOf(s, slot.Mean, zones);
        Array.Copy(_parent, slot.Parent, zones);
        Array.Copy(_kind, slot.Kind, zones);

        // Тик кадра App (аномальный — худший в кадре).
        var p = a.Pending;
        slot.HasTick = period == ProfilePeriod.Frame && p.Active;
        if (slot.HasTick)
        {
            slot.TickNumber = p.Number;
            slot.TickHeader = p.Header;
            slot.TickStart = p.Start;
            slot.TickMedian = p.Median;
            slot.TickThreshold = p.Threshold;
            slot.TickCauses = p.Causes;
            slot.TickZone = p.Zone;
            Array.Copy(p.ZoneTimes, slot.TickTime, zones);
            Array.Copy(p.Bytes, slot.TickBytes, zones);
            Array.Copy(p.Calls, slot.TickCalls, zones);
            Array.Copy(p.Mean, slot.TickMean, zones);
        }

        // Последние периоды: из кольца (от старых к новым), последний — аномальный.
        int recent = slot.RecentHeaders.Length;
        int prior = s.Headers is null ? 0 : Math.Min(recent - 1, s.Filled);
        int stride = slot.Stride;
        for (int i = 0; i < prior; i++)
        {
            int ring = s.Slot(prior - 1 - i);
            slot.RecentHeaders[i] = s.Headers![ring];
            slot.RecentStart[i] = s.RingStart![ring];
            Array.Copy(s.RingTime!, ring * s.Stride, slot.RecentTime, i * stride, zones);
            Array.Copy(s.RingCalls!, ring * s.Stride, slot.RecentCalls, i * stride, zones);
        }
        slot.RecentHeaders[prior] = slot.Header;
        slot.RecentStart[prior] = slot.Start;
        Array.Copy(s.Time, 0, slot.RecentTime, prior * stride, zones);
        Array.Copy(s.Calls, 0, slot.RecentCalls, prior * stride, zones);
        slot.Recent = prior + 1;

        // Тики окна кадров App — заголовки (их зоны — в строках кадров).
        slot.RecentTicks = 0;
        if (period == ProfilePeriod.Frame && _ticks.Headers is not null)
        {
            long from = slot.RecentStart[0];
            int n = 0, cap = slot.RecentTickHeaders.Length;
            while (n < Math.Min(cap, _ticks.Filled) && _ticks.RingStart![_ticks.Slot(n)] >= from) n++;
            for (int i = 0; i < n; i++)
            {
                int ring = _ticks.Slot(n - 1 - i);
                slot.RecentTickHeaders[i] = _ticks.Headers[ring];
                slot.RecentTickStart[i] = _ticks.RingStart![ring];
            }
            slot.RecentTicks = n;
        }

        slot.Now = MachineSample.Take(probe: false);
        slot.HasBaseline = a.Baseline(slot.Now.Time, out slot.Baseline);
        if (!slot.HasBaseline && _windowStarted)
        {
            slot.Baseline = _windowStart;
            slot.HasBaseline = true;
        }
        slot.ComputeBound = _frames.Count == 0 && _frames.Service == 0 && period == ProfilePeriod.Tick;
        slot.Events = _events;
        slot.Timeline = _timeline;
    }
}
