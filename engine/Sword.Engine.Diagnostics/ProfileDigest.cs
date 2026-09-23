using System.Diagnostics;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Сводка профайлера для HUD (F2, точка Пв): главные зоны кадра по своему времени (мс на кадр, без дочерних — строки не
/// перекрываются и в сумме дают кадр) за прошлое окно <see cref="WindowSeconds"/>, средний кадр. Пересчёт — раз в окно из
/// итогов профайлера (разность сумм с прошлого пересчёта, O(зон)); в остальных кадрах — сравнение времени. Без выделений
/// (массивы растут только вместе с числом зон); однопоточная — поток кадра. Профайлер выключен — строк нет.
/// Точка Пг: отдельно — GPU-время кадра и главные проходы GPU (зоны вида <see cref="ProfileZoneKind.Gpu"/>, мс на кадр за то же
/// окно; времена кадров на 3 раньше — кадры в полёте); в строки зон CPU они не входят.
/// </summary>
public sealed class ProfileDigest(CpuProfiler profiler)
{
    public const double WindowSeconds = 0.25;
    public const int MaxLines = 8;

    /// <summary>Строк проходов GPU (точка Пг).</summary>
    public const int MaxGpuLines = 6;

    private readonly int[] _zone = new int[MaxLines];
    private readonly double[] _ms = new double[MaxLines];
    private readonly int[] _gpuZone = new int[MaxGpuLines];
    private readonly double[] _gpuMs = new double[MaxGpuLines];
    private long[] _previous = [], _delta = [], _self = [];
    private int _previousCount = -1;
    private long _last;
    private bool _started;

    /// <summary>Строк (зон) в сводке.</summary>
    public int Count { get; private set; }

    /// <summary>Средний кадр окна, мс; кадров в окне.</summary>
    public double FrameMs { get; private set; }
    public int Frames { get; private set; }

    /// <summary>Окно посчитано (профайлер включён и кадры были).</summary>
    public bool Ready { get; private set; }

    /// <summary>Пересчётов (проверка цены: в кадре без конца окна — ни одного).</summary>
    public long Updates { get; private set; }

    public int Zone(int line) => _zone[line];

    /// <summary>Имя зоны строки; корень кадра (своё время кадра вне зон) — <c>frame</c>.</summary>
    public string Name(int line) => profiler.NameOf(_zone[line]);

    /// <summary>Своё время зоны строки, мс на кадр.</summary>
    public double Ms(int line) => _ms[line];

    /// <summary>GPU-время кадра целиком за окно, мс на кадр (точка Пг); 0 — времён нет.</summary>
    public double GpuFrameMs { get; private set; }

    /// <summary>Строк проходов GPU.</summary>
    public int GpuCount { get; private set; }

    /// <summary>Имя прохода строки GPU — без <c>gpu.</c> (без выделений).</summary>
    public ReadOnlySpan<char> GpuName(int line)
    {
        string name = profiler.NameOf(_gpuZone[line]);
        return name.StartsWith(CpuProfiler.GpuPrefix, StringComparison.Ordinal) ? name.AsSpan(CpuProfiler.GpuPrefix.Length) : name;
    }

    /// <summary>GPU-время прохода строки, мс на кадр.</summary>
    public double GpuMs(int line) => _gpuMs[line];

    public CpuProfiler Profiler => profiler;

    /// <summary>Кадр: <paramref name="now"/> — отметка <see cref="Stopwatch"/>. Окно кончилось — пересчёт.</summary>
    public void Update(long now)
    {
        if (!profiler.Enabled)
        {
            // Выключен — пусто; окно начнётся в первом кадре включённого (F2 включает профайлер со следующего кадра: строки —
            // через 0,25 с, а не через 0,5).
            Ready = false;
            Count = 0;
            GpuCount = 0;
            GpuFrameMs = 0;
            _previousCount = -1;
            _started = false;
            return;
        }
        if (_started && now - _last < (long)(WindowSeconds * Stopwatch.Frequency)) return;
        _started = true;
        _last = now;
        Updates++;
        int zones = profiler.Zones.Count;
        if (_previous.Length < zones)
        {
            // Рост зон (новый проход графа, регистрация) — редко; прошлые суммы теряются, окно начинается заново.
            _previous = new long[zones];
            _delta = new long[zones];
            _self = new long[zones];
            _previousCount = -1;
        }
        var (total, count) = profiler.FrameTotals();
        int frames = count - _previousCount;
        if (_previousCount < 0 || frames <= 0)
        {
            // Первое окно или сброс итогов (бенч после прогрева): только запомнить суммы.
            Array.Copy(total, _previous, zones);
            _previousCount = count;
            if (frames < 0 || _previousCount == 0) Ready = false;
            return;
        }
        for (int z = 0; z < zones; z++)
        {
            _delta[z] = total[z] - _previous[z];
            _previous[z] = total[z];
            _self[z] = _delta[z];
        }
        _previousCount = count;
        for (int z = 0; z < zones; z++)
        {
            int p = profiler.ParentOf(z);
            if (p >= 0 && _delta[z] > 0) _self[p] -= _delta[z];
        }
        // Главные — по своему времени, вставкой в заранее выделенные строки; зоны GPU (точка Пг) — отдельно.
        int n = 0, g = 0;
        int gpuFrame = profiler.Find(CpuProfiler.GpuFrameName);
        for (int z = 0; z < zones; z++)
        {
            long v = _self[z];
            if (v <= 0) continue;
            if (profiler.KindOf(z) == ProfileZoneKind.Gpu)
            {
                if (z != gpuFrame) g = Insert(_gpuZone, g, z, v);
                continue;
            }
            n = Insert(_zone, n, z, v);
        }
        for (int i = 0; i < n; i++) _ms[i] = CpuProfiler.Ms(_self[_zone[i]]) / frames;
        for (int i = 0; i < g; i++) _gpuMs[i] = CpuProfiler.Ms(_self[_gpuZone[i]]) / frames;
        Count = n;
        GpuCount = g;
        GpuFrameMs = gpuFrame >= 0 ? CpuProfiler.Ms(_delta[gpuFrame]) / frames : 0;
        Frames = frames;
        FrameMs = CpuProfiler.Ms(_delta[CpuProfiler.FrameZone]) / frames;
        Ready = true;
    }

    /// <summary>Вставка зоны <paramref name="z"/> со временем <paramref name="v"/> в строки по убыванию; число строк после.</summary>
    private int Insert(int[] lines, int n, int z, long v)
    {
        int max = lines.Length;
        int at = n < max ? n : max;
        while (at > 0 && _self[lines[at - 1]] < v) at--;
        if (at >= max) return n;
        int last = Math.Min(n, max - 1);
        for (int i = last; i > at; i--) lines[i] = lines[i - 1];
        lines[at] = z;
        return n < max ? n + 1 : n;
    }
}
