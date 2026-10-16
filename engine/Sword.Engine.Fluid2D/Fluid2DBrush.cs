using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid2D;

/// <summary>Точка кисти шага для отладки (оверлей, <c>--json</c>): ключ, голова и хвост мазка в UV, UV-скорость, σ в см, есть ли отрезок.</summary>
public readonly record struct Fluid2DBrushPointInfo(Fluid2DBrushKey Key, Vector3 Unit, Vector2 Prev, Vector3 UnitVelocity, float Scale,
    float SigmaCm, bool Segment);

/// <summary>
/// Итог шага кисти: добавлено, осталось после отбора (Z-полоса, AABB штампа ∩ площадка), отсеяно, сверх <see cref="Fluid2DParams.BrushMaxPoints"/>
/// («отброшено» — точки с большими ключами), записей бининга и сторона ячейки в текселях кисти.
/// </summary>
public readonly record struct Fluid2DBrushStats(ulong Step, ulong TickId, long AgeSteps, int Added, int Kept, int Culled, int Dropped,
    int Entries, int CellTexels);

/// <summary>
/// CPU-часть детерминированной кисти (BR §8.2, шаги 1–4): точки шага по ключу → UV, скорость, джиттер, отрезок → отбор → сортировка по
/// ключу → бининг по ячейкам тайлов кисти. Шаг: <see cref="Begin"/> → <see cref="Add"/> ×n → <see cref="End"/> → <c>Fluid2DSim.Step</c>
/// (или <see cref="Fluid2DPainterReference.Paint"/>). Кадр — 0 Б после прогрева (буферы растут только при новом максимуме точек).
/// </summary>
/// <remarks>
/// Прошлая позиция и скорость — по ключу: новый ключ (или не бывший в прошлом шаге, или <see cref="Forget"/>) — без отрезка, V = 0;
/// смена состава не трогает остальные точки. V = Δp/<see cref="Fluid2DParams.StepSeconds"/>, первые 0.25 с после <see cref="Reset"/> — 0.
/// Джиттер — хеш (зерно, TickId, ключ), только у массивов. Упреждения нет (у L2 оно компенсирует задержку массивов на шаг).
/// </remarks>
public sealed class Fluid2DBrush
{
    /// <summary>Тайл группы Painter (8×8 текселей) — наименьшая ячейка бининга.</summary>
    internal const int Tile = 8;

    /// <summary>Ячеек бининга по оси не больше — таблица ячеек шага ≤ 1024 слов.</summary>
    internal const int MaxCellsPerAxis = 32;

    private struct KeyState
    {
        public Vector3 World, Unit, UnitVelocity;
        public long LastStep;
        public bool Forgotten;
    }

    private struct Pending
    {
        public Fluid2DBrushKey Key;
        public Vector3 Position;
        public float Scale;
        public Fluid2DBrushFlags Flags;
    }

    private readonly Dictionary<ulong, KeyState> _states;
    private ulong[] _stale;
    private Pending[] _pending;
    private int _pendingCount;

    private ulong[] _keptKeys;
    private int[] _keptOrder;
    private Fluid2DBrushPoint[] _keptRecords, _records;
    private Fluid2DBrushPointInfo[] _keptInfos, _infos;
    private int _keptCount, _count;
    private readonly uint[] _cells = new uint[MaxCellsPerAxis * MaxCellsPerAxis];
    private readonly int[] _cellFill = new int[MaxCellsPerAxis * MaxCellsPerAxis];
    private uint[] _entries;
    private int _entryCount, _cellsPerAxis, _cellGroups;

    private long _brushStep = -1;
    private bool _open;
    private Fluid2DStepInput _input;
    private ulong _tickId;
    private Vector3 _center;
    private Vector3? _camera;
    private int _added, _culled, _dropped;
    private Fluid2DParams _params;

    public Fluid2DBrush(Fluid2DParams parameters, ulong seed)
    {
        parameters.Validate();
        _params = parameters;
        Seed = seed;
        int n = parameters.BrushMaxPoints;
        _states = new Dictionary<ulong, KeyState>(2 * n);
        _stale = new ulong[2 * n];
        _pending = new Pending[2 * n];
        _keptKeys = new ulong[2 * n];
        _keptOrder = new int[2 * n];
        _keptRecords = new Fluid2DBrushPoint[2 * n];
        _keptInfos = new Fluid2DBrushPointInfo[2 * n];
        _records = new Fluid2DBrushPoint[n];
        _infos = new Fluid2DBrushPointInfo[n];
        _entries = new uint[parameters.BrushMaxEntries];
    }

    /// <summary>Зерно джиттера: другое зерно — другой джиттер, то же зерно и TickId — те же точки бит в бит.</summary>
    public ulong Seed { get; }

    /// <summary>Параметры (площадка, джиттер, Painter); меняются между шагами.</summary>
    public Fluid2DParams Params
    {
        get => _params;
        set
        {
            value.Validate();
            _params = value;
            if (_records.Length < value.BrushMaxPoints)
            {
                Array.Resize(ref _records, value.BrushMaxPoints);
                Array.Resize(ref _infos, value.BrushMaxPoints);
            }
            if (_entries.Length < value.BrushMaxEntries) Array.Resize(ref _entries, value.BrushMaxEntries);
        }
    }

    /// <summary>Шагов кисти с активации (<see cref="Reset"/>): Age = AgeSteps·StepSeconds (0.25 с = 15 шагов, 10 с = 600 при 60 Гц).</summary>
    public long AgeSteps => Math.Max(_brushStep, 0);

    /// <summary>Шаг готов (<see cref="End"/> после <see cref="Begin"/>) и его номер.</summary>
    public bool Ready { get; private set; }

    public ulong Step => _input.Step;

    public Fluid2DBrushStats Stats { get; private set; }

    /// <summary>Точки последнего шага в порядке ключа (после отбора).</summary>
    public ReadOnlySpan<Fluid2DBrushPointInfo> Points => _infos.AsSpan(0, _count);

    internal ReadOnlySpan<Fluid2DBrushPoint> Records => _records.AsSpan(0, _count);
    internal ReadOnlySpan<uint> Cells => _cells.AsSpan(0, _cellsPerAxis * _cellsPerAxis);
    internal ReadOnlySpan<uint> Entries => _entries.AsSpan(0, _entryCount);
    internal int CellsPerAxis => _cellsPerAxis;

    /// <summary>Ячейка бининга — CellGroups×CellGroups тайлов 8×8 текселей.</summary>
    internal int CellGroups => _cellGroups;

    internal float Age => (float)(AgeSteps * _params.StepSeconds);

    /// <summary>Слов буфера кисти на шаг: таблица ячеек, записи, точки.</summary>
    internal int Words => Cells.Length + _entryCount + _count * Fluid2DBrushPoint.Words;

    /// <summary>
    /// s примитива L2 (UseTrackedObjectSize, UseObjBounds): 0.1·0.7·R·множитель, R — радиус сферы границ, см (шишка факела: R 2.35–2.5,
    /// ×2 → 0.33–0.35).
    /// </summary>
    public static float PrimitiveScale(float boundsRadiusCm, float mult = 1) => 0.1f * 0.7f * boundsRadiusCm * mult;

    /// <summary>s кости L2: 0.05·длина до родителя (см)·множитель.</summary>
    public static float BoneScale(float lengthCm, float mult = 1) => 0.05f * lengthCm * mult;

    /// <summary>Активация (перезапуск эффекта): Age = 0, все ключи забыты — первые 0.25 с точки без скорости и отрезков, прокол нарастает 10 с.</summary>
    public void Reset()
    {
        _states.Clear();
        _brushStep = -1;
        _open = false;
        Ready = false;
        _count = _entryCount = 0;
    }

    /// <summary>Ключ пересоздан (например, оружие построено заново): следующий <see cref="Add"/> — новая точка, без отрезка и скорости.</summary>
    public void Forget(Fluid2DBrushKey key)
    {
        if (_states.TryGetValue(key.Packed, out var s))
        {
            s.Forgotten = true;
            _states[key.Packed] = s;
        }
    }

    /// <summary>
    /// Начало шага: вход шага сима (поворот площадки, сдвиг WOD), TickId для джиттера, центр площадки в мире, м, и позиция камеры
    /// (площадка к камере — перспективный перенос на глубину центра; null — ортогонально).
    /// </summary>
    public void Begin(in Fluid2DStepInput input, ulong tickId, Vector3 center, Vector3? camera = null)
    {
        if (_open) throw new InvalidOperationException("кисть: Begin без End");
        _open = true;
        Ready = false;
        _input = input;
        _tickId = tickId;
        _center = center;
        _camera = camera;
        _brushStep++;
        _pendingCount = _added = _culled = _dropped = 0;
    }

    /// <summary>Точка шага: мировая позиция, м, и размер s (σ = 9.496·s·BrushSize см). s ≤ 0 (корень скелета) — не рисует.</summary>
    public void Add(Fluid2DBrushKey key, Vector3 position, float scale, Fluid2DBrushFlags flags = Fluid2DBrushFlags.None)
    {
        if (!_open) throw new InvalidOperationException("кисть: Add вне Begin/End");
        if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        _pending[_pendingCount++] = new Pending { Key = key, Position = position, Scale = scale, Flags = flags };
        _added++;
    }

    /// <summary>Конец шага: точки → числа Painter, отбор, сортировка по ключу, бининг.</summary>
    public void End()
    {
        if (!_open) throw new InvalidOperationException("кисть: End без Begin");
        _open = false;
        var p = _params;
        _keptCount = 0;
        for (int i = 0; i < _pendingCount; i++) Prepare(p, i);
        PruneStates();
        SortAndTrim(p.BrushMaxPoints);
        Bin(p);
        Ready = true;
        Stats = new Fluid2DBrushStats(_input.Step, _tickId, AgeSteps, _added, _count, _culled, _dropped, _entryCount, Tile * _cellGroups);
    }

    // ── точка ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void Prepare(Fluid2DParams p, int i)
    {
        ref readonly var a = ref _pending[i];
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(_states, a.Key.Packed, out bool exists);
        if (exists && state.LastStep == _brushStep) throw new InvalidOperationException($"кисть: ключ {a.Key} дважды за шаг");
        bool continuous = exists && state.LastStep == _brushStep - 1 && !state.Forgotten;
        float dt = (float)p.StepSeconds;
        float age = Age;
        var rotation = _input.Rotation;
        var inverse = Quaternion.Conjugate(rotation);
        var e = p.Extents;
        float maxE = MathF.Max(e.X, e.Y);

        bool visible = Project(p, rotation, inverse, a.Position, out var unit);
        var world = continuous && age >= 0.25f ? (a.Position - state.World) / dt : Vector3.Zero;
        var l = Vector3.Transform(world, inverse);
        var uv = p.CameraFacing
            ? 0.1f * new Vector3(l.X / e.X, -l.Y / e.Y, l.Z / maxE)
            : 0.05f * new Vector3(l.X, -l.Y, l.Z);

        var head = unit;
        bool screen = (a.Flags & Fluid2DBrushFlags.Screen) != 0;
        if (a.Key.Source == Fluid2DBrushSource.Array && p.BrushPositionRandom != 0)
        {
            var (j0, j1) = Jitter(Seed, _tickId, a.Key.Packed);
            float scaleJ = p.BrushRndScaledByVelocity ? MathF.Min(MathF.Sqrt(25f * (uv.X * uv.X + uv.Y * uv.Y)), 1f) : 1f;
            float cm = 20f * p.BrushPositionRandom * scaleJ;
            head.X += (j0 - 0.5f) * cm / (100f * e.X);
            head.Y += (j1 - 0.5f) * cm / (100f * e.Y);
        }

        Vector2 prev;
        if (!continuous) prev = new Vector2(unit.X, unit.Y);
        else if (p.CameraFacing || screen) prev = new Vector2(state.Unit.X, state.Unit.Y);
        else
        {
            var lp = Vector3.Transform(state.World - _center, inverse);
            prev = new Vector2(0.5f + lp.X / e.X, 0.5f - lp.Y / e.Y);
        }
        var prevUv = continuous ? state.UnitVelocity : Vector3.Zero;
        // Точка за камерой без UV: в следующем шаге она новая, иначе отрезок начался бы из нулевой позиции.
        state = new KeyState { World = a.Position, Unit = head, UnitVelocity = uv, LastStep = _brushStep, Forgotten = !visible };

        if (!visible || !(a.Scale > 0))
        {
            _culled++;
            return;
        }
        var record = Fluid2DBrushPoint.Build(p, a.Key.Source, (a.Flags & Fluid2DBrushFlags.VelocityOnly) != 0, head, prev, uv, prevUv, a.Scale,
            age, _input.WorldOffsetDelta, out float sigma);
        bool inBand = MathF.Abs(unit.Z - 0.5f) * maxE <= 0.5f * e.Z;
        bool onArea = record.Max.X >= 0 && record.Min.X <= 1 && record.Max.Y >= 0 && record.Min.Y <= 1;
        if (!screen && !(inBand && onArea))
        {
            _culled++;
            return;
        }
        if (_keptCount == _keptKeys.Length) GrowKept();
        _keptKeys[_keptCount] = a.Key.Packed;
        _keptOrder[_keptCount] = _keptCount;
        _keptRecords[_keptCount] = record;
        _keptInfos[_keptCount] = new Fluid2DBrushPointInfo(a.Key, head, record.Prev, uv, a.Scale, sigma * 100f * maxE, record.SegLen >= 0);
        _keptCount++;
    }

    /// <summary>
    /// UV точки: без камеры и у мировой площадки — W2U = diag(1/E)·R⁻¹·(P − центр) + 0.5 (v растёт вниз по площадке, z — вдоль нормали
    /// ÷ max(E)); у площадки к камере с камерой — сначала перенос на глубину центра P′ = Cam + (P − Cam)·d_c/d_P, z = (d_c − d_P)/max(E) + 0.5.
    /// Точка за камерой не видна.
    /// </summary>
    private bool Project(Fluid2DParams p, Quaternion rotation, Quaternion inverse, Vector3 position, out Vector3 unit)
    {
        var e = p.Extents;
        float maxE = MathF.Max(e.X, e.Y);
        if (p.CameraFacing && _camera is { } cam)
        {
            var forward = -Vector3.Transform(Vector3.UnitZ, rotation);
            float dc = Vector3.Dot(_center - cam, forward), dp = Vector3.Dot(position - cam, forward);
            if (!(dp > 1e-4f))
            {
                unit = default;
                return false;
            }
            var moved = cam + (position - cam) * (dc / dp);
            var lc = Vector3.Transform(moved - _center, inverse);
            unit = new Vector3(0.5f + lc.X / e.X, 0.5f - lc.Y / e.Y, 0.5f + (dc - dp) / maxE);
            return true;
        }
        var lo = Vector3.Transform(position - _center, inverse);
        unit = new Vector3(0.5f + lo.X / e.X, 0.5f - lo.Y / e.Y, 0.5f + lo.Z / maxE);
        return true;
    }

    /// <summary>Два равномерных [0, 1) по 24 бита из SplitMix64 от (зерно, TickId, ключ).</summary>
    internal static (float X, float Y) Jitter(ulong seed, ulong tickId, ulong key)
    {
        ulong h = Mix(Mix(Mix(seed) ^ tickId) ^ key);
        const float scale = 1f / (1 << 24);
        return ((h >> 40) * scale, ((h >> 16) & 0xFFFFFF) * scale);
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Состояние ключей, которых нет в этом шаге, не нужно: в следующем они всё равно новые.</summary>
    private void PruneStates()
    {
        int stale = 0;
        foreach (var (key, s) in _states)
        {
            if (s.LastStep == _brushStep) continue;
            if (stale == _stale.Length) Array.Resize(ref _stale, _stale.Length * 2);
            _stale[stale++] = key;
        }
        for (int i = 0; i < stale; i++) _states.Remove(_stale[i]);
    }

    private void GrowKept()
    {
        int n = _keptKeys.Length * 2;
        Array.Resize(ref _keptKeys, n);
        Array.Resize(ref _keptOrder, n);
        Array.Resize(ref _keptRecords, n);
        Array.Resize(ref _keptInfos, n);
    }

    /// <summary>Порядок — по ключу (ключи шага различны); сверх предела остаются меньшие ключи.</summary>
    private void SortAndTrim(int max)
    {
        _keptKeys.AsSpan(0, _keptCount).Sort(_keptOrder.AsSpan(0, _keptCount));
        _count = Math.Min(_keptCount, max);
        _dropped = _keptCount - _count;
        for (int i = 0; i < _count; i++)
        {
            _records[i] = _keptRecords[_keptOrder[i]];
            _infos[i] = _keptInfos[_keptOrder[i]];
        }
    }

    // ── бининг ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ячейка — m×m тайлов 8×8 текселей, m ≥ радиуса штампа 4σ·stretch в тайлах (не больше 32 ячеек по оси); записей больше предела —
    /// ячейки вдвое крупнее (переполнения нет по построению). Точка входит в ячейки, которые пересекает её AABB; в ячейке точки — в порядке
    /// ключа. Слово ячейки: начало (16 бит) | число (16 бит).
    /// </summary>
    private void Bin(Fluid2DParams p)
    {
        int bn = p.BrushResolution;
        int groups = (bn + Tile - 1) / Tile;
        float rMax = 0;
        for (int i = 0; i < _count; i++)
            rMax = MathF.Max(rMax, 0.5f * MathF.Max(_records[i].Max.X - _records[i].Min.X, _records[i].Max.Y - _records[i].Min.Y));
        int m = Math.Max(1, Math.Max((int)MathF.Ceiling(rMax * bn / Tile), (groups + MaxCellsPerAxis - 1) / MaxCellsPerAxis));
        m = Math.Min(m, groups);
        while (true)
        {
            int total = CountEntries(bn, groups, m);
            if (total <= p.BrushMaxEntries || m >= groups) break;
            m = Math.Min(m * 2, groups);
        }
        _cellGroups = m;
        _cellsPerAxis = (groups + m - 1) / m;
        int cells = _cellsPerAxis * _cellsPerAxis;
        Array.Clear(_cellFill, 0, cells);
        for (int i = 0; i < _count; i++) Visit(i, bn, m, count: true);
        int start = 0;
        for (int c = 0; c < cells; c++)
        {
            int n = _cellFill[c];
            _cells[c] = (uint)start | ((uint)n << 16);
            _cellFill[c] = start;
            start += n;
        }
        _entryCount = start;
        for (int i = 0; i < _count; i++) Visit(i, bn, m, count: false);
    }

    private int CountEntries(int bn, int groups, int m)
    {
        int total = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!CellRange(_records[i], bn, groups, m, out int x0, out int y0, out int x1, out int y1)) continue;
            total += (x1 - x0 + 1) * (y1 - y0 + 1);
        }
        return total;
    }

    private void Visit(int i, int bn, int m, bool count)
    {
        int groups = (bn + Tile - 1) / Tile;
        if (!CellRange(_records[i], bn, groups, m, out int x0, out int y0, out int x1, out int y1)) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * _cellsPerAxis + x;
                if (count) _cellFill[c]++;
                else _entries[_cellFill[c]++] = (uint)i;
            }
    }

    /// <summary>Ячейки, в которых есть тексели с центром в AABB точки (с запасом в тексель); false — AABB вне грида.</summary>
    private static bool CellRange(in Fluid2DBrushPoint r, int bn, int groups, int m, out int x0, out int y0, out int x1, out int y1)
    {
        int tx0 = (int)MathF.Floor(r.Min.X * bn - 0.5f), ty0 = (int)MathF.Floor(r.Min.Y * bn - 0.5f);
        int tx1 = (int)MathF.Ceiling(r.Max.X * bn - 0.5f), ty1 = (int)MathF.Ceiling(r.Max.Y * bn - 0.5f);
        int cells = (groups + m - 1) / m;
        x0 = y0 = x1 = y1 = 0;
        if (tx1 < 0 || ty1 < 0 || tx0 > bn - 1 || ty0 > bn - 1) return false;
        int side = Tile * m;
        x0 = Math.Clamp(tx0, 0, bn - 1) / side;
        y0 = Math.Clamp(ty0, 0, bn - 1) / side;
        x1 = Math.Min(Math.Clamp(tx1, 0, bn - 1) / side, cells - 1);
        y1 = Math.Min(Math.Clamp(ty1, 0, bn - 1) / side, cells - 1);
        return true;
    }
}
