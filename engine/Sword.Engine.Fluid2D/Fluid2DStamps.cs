using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// Штамп шага в словах буфера (<c>fluid.stamp</c>): центр (UV), матрица UV → локальные координаты формы q, пик плотности, форма, AABB (UV).
/// Слова 0–1 центр, 2–5 матрица (q = M·(uv − центр)), 6 пик, 7 форма, 8–11 AABB (min u, min v, max u, max v).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fluid2DStampRecord
{
    public const int Words = 12;

    public Vector2 Center;
    public float M00, M01, M10, M11;
    public float Peak;
    public uint Shape;
    public Vector2 Min, Max;

    /// <summary>Порядок — по словам записи: сумма в текселе не зависит от порядка <see cref="Fluid2DStamps.Add"/>, одинаковые штампы взаимозаменимы.</summary>
    public static int Compare(Fluid2DStampRecord a, Fluid2DStampRecord b)
    {
        var x = MemoryMarshal.Cast<Fluid2DStampRecord, uint>(MemoryMarshal.CreateReadOnlySpan(ref a, 1));
        var y = MemoryMarshal.Cast<Fluid2DStampRecord, uint>(MemoryMarshal.CreateReadOnlySpan(ref b, 1));
        return x.SequenceCompareTo(y);
    }
}

/// <summary>Итог шага штампов: добавлено, осталось, вне площадки, сверх предела, записей бининга, сторона ячейки (текселей), объём оставшихся.</summary>
public readonly record struct Fluid2DStampStats(ulong Step, int Added, int Kept, int Culled, int Dropped, int Entries, int CellTexels, float Volume);

/// <summary>
/// Штампы плотности шага (CPU): шаг — <see cref="Begin"/> → <see cref="Add"/> ×n → <see cref="End"/> → <c>Fluid2DSim.Step(…, stamps)</c>
/// (или <see cref="Fluid2DReference.Step"/>). Проход <c>fluid.stamp</c> перед Composite суммирует штампы в грид штампов (float), Composite
/// прибавляет его к ρ после множителя накопления (×маска края): масса C растёт ровно на объём. Сумма не зависит от порядка добавления:
/// штампы сортируются по словам записи, в ячейке бининга — в этом порядке, без атомиков. Кадр — 0 Б после прогрева.
/// </summary>
/// <remarks>
/// Объём — в единицах ρ·м² (Σ ρ·площадь текселя). Форма 0 — гаусс: σ = полуось/2 (не меньше 0.75 текселя — сумма по текселям равна
/// интегралу), обрезка на 4σ (пик поправлен на потерю e⁻⁸). Форма k ≥ 1 — слой k − 1 текстуры форм сима (канал r, билинейно с заворотом)
/// на прямоугольнике ±полуоси; пик = объём/(4·a·b·среднее слоя), среднее даёт вызывающий (<c>shapeMeans</c>). Плотность сверх 1/(накопление)
/// в следующем шаге обрежет <c>saturate</c> Composite — толщину на ρ выбирает вызывающий.
/// </remarks>
public sealed class Fluid2DStamps
{
    internal const int Tile = 8, MaxCellsPerAxis = 32;
    private const float MinSigmaTexels = 0.75f;
    private const float GaussKept = 0.99966453737f; // 1 − e⁻⁸: доля массы гаусса внутри 4σ

    private struct Pending
    {
        public Fluid2DStampRecord Record;
        public float Volume;
    }

    private static readonly Comparison<Pending> ByRecord = (a, b) => Fluid2DStampRecord.Compare(a.Record, b.Record);

    private readonly float[] _shapeMeans;
    private Pending[] _pending;
    private Fluid2DStampRecord[] _records;
    private int _pendingCount, _count;
    private readonly uint[] _cells = new uint[MaxCellsPerAxis * MaxCellsPerAxis];
    private readonly int[] _cellFill = new int[MaxCellsPerAxis * MaxCellsPerAxis];
    private uint[] _entries;
    private int _entryCount, _cellsPerAxis, _cellTexels;
    private bool _open;
    private Vector3 _center;
    private Quaternion _inverse = Quaternion.Identity;
    private int _added, _culled;
    private Fluid2DParams _params;

    /// <param name="maxStamps">Штампов на шаг (лишние — «сверх предела», остаются меньшие по записи).</param>
    /// <param name="maxEntries">Записей бининга; больше — ячейки вдвое крупнее.</param>
    /// <param name="shapeMeans">Среднее канала r слоёв текстуры форм: форма k ≥ 1 — слой k − 1.</param>
    public Fluid2DStamps(Fluid2DParams parameters, int maxStamps = 1024, int maxEntries = 16384, float[]? shapeMeans = null)
    {
        parameters.Validate();
        if (maxStamps is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(maxStamps), maxStamps, "штампов 1…65535");
        if (maxEntries < maxStamps || maxEntries > 65535) throw new ArgumentOutOfRangeException(nameof(maxEntries), maxEntries, "записей — от штампов до 65535");
        _params = parameters;
        MaxStamps = maxStamps;
        MaxEntries = maxEntries;
        _shapeMeans = shapeMeans ?? [];
        foreach (float m in _shapeMeans)
            if (!(m > 0)) throw new ArgumentOutOfRangeException(nameof(shapeMeans), m, "среднее слоя формы > 0");
        _pending = new Pending[maxStamps];
        _records = new Fluid2DStampRecord[maxStamps];
        _entries = new uint[maxEntries];
    }

    public int MaxStamps { get; }
    public int MaxEntries { get; }

    /// <summary>Площадка и сетка (сторона, Extents) — как у сима.</summary>
    public Fluid2DParams Params
    {
        get => _params;
        set
        {
            value.Validate();
            _params = value;
        }
    }

    public bool Ready { get; private set; }
    public ulong Step { get; private set; }
    public Fluid2DStampStats Stats { get; private set; }

    /// <summary>Штампов шага после отбора (в порядке записи).</summary>
    public int Count => _count;

    internal ReadOnlySpan<Fluid2DStampRecord> Records => _records.AsSpan(0, _count);
    internal ReadOnlySpan<uint> Cells => _cells.AsSpan(0, _cellsPerAxis * _cellsPerAxis);
    internal ReadOnlySpan<uint> Entries => _entries.AsSpan(0, _entryCount);
    internal int CellsPerAxis => _cellsPerAxis;
    internal int CellTexels => _cellTexels;
    internal int Words => Cells.Length + _entryCount + _count * Fluid2DStampRecord.Words;

    /// <summary>Начало шага: центр площадки в мире, м, и её поворот (оси площадки x — u, y — вверх по площадке (v убывает), z — нормаль).</summary>
    public void Begin(ulong step, Vector3 center = default, Quaternion planeRotation = default)
    {
        if (_open) throw new InvalidOperationException("штампы: Begin без End");
        _open = true;
        Ready = false;
        Step = step;
        _center = center;
        _inverse = Quaternion.Conjugate(planeRotation == default ? Quaternion.Identity : planeRotation);
        _pendingCount = _added = _culled = 0;
    }

    /// <summary>Штамп в мировой точке (проекция на площадку как у кисти: W2U = diag(1/E)·R⁻¹·(P − центр) + 0.5, v растёт вниз).</summary>
    public void Add(Vector3 world, float angle, Vector2 halfAxes, float volume, int shape = 0)
    {
        var l = Vector3.Transform(world - _center, _inverse);
        var e = _params.Extents;
        AddUv(new Vector2(0.5f + l.X / e.X, 0.5f - l.Y / e.Y), angle, halfAxes, volume, shape);
    }

    /// <summary>
    /// Штамп: центр в UV окна шага, угол (рад, от оси u к оси v), полуоси, м, объём ρ·м², форма (0 — гаусс, k ≥ 1 — слой k − 1 текстуры форм).
    /// </summary>
    public void AddUv(Vector2 centerUv, float angle, Vector2 halfAxes, float volume, int shape = 0)
    {
        if (!_open) throw new InvalidOperationException("штампы: Add вне Begin/End");
        if (shape < 0 || shape > _shapeMeans.Length) throw new ArgumentOutOfRangeException(nameof(shape), shape, $"форм {_shapeMeans.Length} (+ гаусс 0)");
        _added++;
        if (!(volume > 0) || !float.IsFinite(volume) || !float.IsFinite(centerUv.X) || !float.IsFinite(centerUv.Y)) return;
        var p = _params;
        var e = p.Extents;
        float texel = MathF.Max(e.X, e.Y) / p.Resolution;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        float sx, sy, peak, hx, hy;
        if (shape == 0)
        {
            sx = MathF.Max(0.5f * halfAxes.X, MinSigmaTexels * texel);
            sy = MathF.Max(0.5f * halfAxes.Y, MinSigmaTexels * texel);
            peak = volume / (2f * MathF.PI * sx * sy * GaussKept);
            hx = 4f * MathF.Sqrt(sx * cos * sx * cos + sy * sin * sy * sin);
            hy = 4f * MathF.Sqrt(sx * sin * sx * sin + sy * cos * sy * cos);
        }
        else
        {
            sx = MathF.Max(halfAxes.X, 2f * MinSigmaTexels * texel);
            sy = MathF.Max(halfAxes.Y, 2f * MinSigmaTexels * texel);
            peak = volume / (4f * sx * sy * _shapeMeans[shape - 1]);
            hx = MathF.Abs(sx * cos) + MathF.Abs(sy * sin);
            hy = MathF.Abs(sx * sin) + MathF.Abs(sy * cos);
        }
        var r = new Fluid2DStampRecord
        {
            Center = centerUv,
            M00 = cos * e.X / sx, M01 = sin * e.Y / sx,
            M10 = -sin * e.X / sy, M11 = cos * e.Y / sy,
            Peak = peak,
            Shape = (uint)shape,
            Min = new Vector2(centerUv.X - hx / e.X, centerUv.Y - hy / e.Y),
            Max = new Vector2(centerUv.X + hx / e.X, centerUv.Y + hy / e.Y),
        };
        if (r.Max.X < 0 || r.Min.X > 1 || r.Max.Y < 0 || r.Min.Y > 1)
        {
            _culled++;
            return;
        }
        if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
        _pending[_pendingCount++] = new Pending { Record = r, Volume = volume };
    }

    /// <summary>Конец шага: сортировка по записи, предел, бининг.</summary>
    public void End()
    {
        if (!_open) throw new InvalidOperationException("штампы: End без Begin");
        _open = false;
        _pending.AsSpan(0, _pendingCount).Sort(ByRecord);
        _count = Math.Min(_pendingCount, MaxStamps);
        float volume = 0;
        for (int i = 0; i < _count; i++)
        {
            _records[i] = _pending[i].Record;
            volume += _pending[i].Volume;
        }
        Bin();
        Ready = true;
        Stats = new Fluid2DStampStats(Step, _added, _count, _culled, _pendingCount - _count, _entryCount, _cellTexels, volume);
    }

    /// <summary>Ячейка — m×m тайлов 8×8 текселей сима (не больше 32 ячеек по оси); записей больше предела — ячейки вдвое крупнее.</summary>
    private void Bin()
    {
        int n = _params.Resolution;
        int groups = (n + Tile - 1) / Tile;
        int m = Math.Min(Math.Max(1, (groups + MaxCellsPerAxis - 1) / MaxCellsPerAxis), groups);
        while (CountEntries(n, groups, m) > MaxEntries && m < groups) m = Math.Min(m * 2, groups);
        _cellTexels = Tile * m;
        _cellsPerAxis = (groups + m - 1) / m;
        int cells = _cellsPerAxis * _cellsPerAxis;
        Array.Clear(_cellFill, 0, cells);
        for (int i = 0; i < _count; i++) Visit(i, n, count: true);
        int start = 0;
        for (int c = 0; c < cells; c++)
        {
            int k = _cellFill[c];
            _cells[c] = (uint)start | ((uint)k << 16);
            _cellFill[c] = start;
            start += k;
        }
        _entryCount = start;
        for (int i = 0; i < _count; i++) Visit(i, n, count: false);
    }

    private int CountEntries(int n, int groups, int m)
    {
        int total = 0;
        int side = Tile * m, cells = (groups + m - 1) / m;
        for (int i = 0; i < _count; i++)
            if (CellRange(_records[i], n, side, cells, out int x0, out int y0, out int x1, out int y1)) total += (x1 - x0 + 1) * (y1 - y0 + 1);
        return total;
    }

    private void Visit(int i, int n, bool count)
    {
        if (!CellRange(_records[i], n, _cellTexels, _cellsPerAxis, out int x0, out int y0, out int x1, out int y1)) return;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * _cellsPerAxis + x;
                if (count) _cellFill[c]++;
                else _entries[_cellFill[c]++] = (uint)i;
            }
    }

    /// <summary>Ячейки с текселями, центр которых может попасть в AABB (с запасом в тексель); false — AABB вне грида.</summary>
    private static bool CellRange(in Fluid2DStampRecord r, int n, int side, int cells, out int x0, out int y0, out int x1, out int y1)
    {
        int tx0 = (int)MathF.Floor(r.Min.X * n - 0.5f), ty0 = (int)MathF.Floor(r.Min.Y * n - 0.5f);
        int tx1 = (int)MathF.Ceiling(r.Max.X * n - 0.5f), ty1 = (int)MathF.Ceiling(r.Max.Y * n - 0.5f);
        x0 = y0 = x1 = y1 = 0;
        if (tx1 < 0 || ty1 < 0 || tx0 > n - 1 || ty0 > n - 1) return false;
        x0 = Math.Clamp(tx0, 0, n - 1) / side;
        y0 = Math.Clamp(ty0, 0, n - 1) / side;
        x1 = Math.Min(Math.Clamp(tx1, 0, n - 1) / side, cells - 1);
        y1 = Math.Min(Math.Clamp(ty1, 0, n - 1) / side, cells - 1);
        return true;
    }
}
