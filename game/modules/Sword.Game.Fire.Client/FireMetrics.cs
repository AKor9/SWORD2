using System.Numerics;

namespace Sword.Game.Fire;

/// <summary>
/// Пороги размеров огня по полю. Пламя и ядро — по яркости формулы пламени k̄ = DensityGain·n̄^FlowDensityPow·ρ^DensityPower при среднем шуме
/// <see cref="Noise"/> (шум flow-map в поле не записан): пламя — k̄ ≥ <see cref="FlameK"/>, ядро — k̄ ≥ <see cref="CoreK"/> (цвет рампы — Colour1).
/// Дым — оптическая толщина столба 3D-газа вдоль мировой z τ ≥ <see cref="SmokeTau"/> (σ дыма, <see cref="SmokeParams"/>).
/// </summary>
public sealed record FireMetricsParams
{
    public float Noise { get; init; } = 0.5f;
    public float FlameK { get; init; } = 0.1f;
    public float CoreK { get; init; } = 1f;
    public float SmokeTau { get; init; } = 0.1f;

    /// <summary>
    /// Начало видимого дыма у стыка с пламенем (<see cref="FireMetrics.Joint"/>): τ проекции на площадку ≥ этого (≈40 % непрозрачности) — у пламени
    /// дым на фоне его свечения и bloom с τ 0.1–0.3 глазом не виден (кадры <c>artifacts/f12d</c>).
    /// </summary>
    public float JointTau { get; init; } = 0.5f;

    public static FireMetricsParams Default { get; } = new();
}

/// <summary>
/// Область поля над порогом в осях от центра шишки (x — вправо, y — вверх), см: края текселей или ячеек крайних рядов и столбцов, центроид по
/// ним. Пусто — <see cref="Texels"/> 0.
/// </summary>
public readonly record struct FireRegion(int Texels, float TopCm, float BottomCm, float LeftCm, float RightCm, Vector2 CentroidCm)
{
    public float HeightCm => Texels > 0 ? TopCm - BottomCm : 0;
    public float WidthCm => Texels > 0 ? RightCm - LeftCm : 0;
}

/// <summary>
/// Стык пламени и дыма (<see cref="FireMetrics.Joint"/>) — как на экране: в осях площадки пламени от центра шишки, см (x — вправо, y — вверх по
/// площадке), дым — проекцией 3D-поля на площадку вдоль взгляда. Пламя: верх, x оси у верха, угол оси от «вверх» (°, + — вправо); дым: начало
/// видимого (первый ряд от верха пламени вверх, где τ ≥ <see cref="FireMetricsParams.JointTau"/> у оси), зазор до верха пламени (0 — дым начинается у верха), центр струи
/// на 10 и 30 см выше верха пламени и смещение от оси у верха, угол струи между ними. Нет пламени или дыма у оси — <see cref="Valid"/> false.
/// </summary>
public readonly record struct FireJoint(bool Valid, float FlameTopCm, float FlameTopXCm, float FlameAngleDeg, float SmokeStartCm, float GapCm,
    float Smoke10XCm, float Smoke30XCm, float SmokeAngleDeg, float[]? TauNearAxis = null)
{
    public float Offset10Cm => Smoke10XCm - FlameTopXCm;
    public float Offset30Cm => Smoke30XCm - FlameTopXCm;

    /// <summary>Угол струи дыма минус угол оси пламени, °.</summary>
    public float AngleDifferenceDeg => SmokeAngleDeg - FlameAngleDeg;
}

/// <summary>Слой профиля столба дыма (<see cref="FireMetrics.SmokeProfile"/>): высота середины над шишкой, Σσ, max ρ, центр σ по x и z, радиус σ, см.</summary>
public readonly record struct SmokeLayer(float HeightCm, float Sigma, float MaxRho, float XCm, float ZCm, float RadiusCm);

/// <summary>
/// Размеры огня: пламя и ядро — по полю 2D-сима (оси площадки), столб дыма — по 3D-полю газа (<see cref="SmokeColumn"/>), доля ядра по высоте,
/// верх дыма над верхом пламени; пороги ρ пламени и ядра, которые дали пороги k̄.
/// </summary>
public readonly record struct FireMetrics(FireRegion Flame, FireRegion Core, FireRegion Smoke, float FlameRho, float CoreRho)
{
    /// <summary>Высота ядра / высота пламени (пламени нет — 0).</summary>
    public float CoreShare => Flame.HeightCm > 0 ? Core.HeightCm / Flame.HeightCm : 0;

    /// <summary>Верх столба дыма над верхом пламени, см (нет одного из них — 0).</summary>
    public float SmokeAboveFlameCm => Flame.Texels > 0 && Smoke.Texels > 0 ? Smoke.TopCm - Flame.TopCm : 0;

    /// <summary>
    /// Пламя и ядро по выходу поля C (<paramref name="composite"/>: <paramref name="n"/>² текселей RGBA half — v.xy, ρ, wet; ряд 0 — v = 0, верх
    /// площадки) площадки <paramref name="extents"/> (м); столба дыма нет (<see cref="SmokeColumn"/> — по газу).
    /// </summary>
    public static FireMetrics Compute(ReadOnlySpan<Half> composite, int n, Vector2 extents, FlameParams flame, FireMetricsParams p)
    {
        if (composite.Length < 4 * n * n) throw new ArgumentException($"поле {composite.Length} half, нужно {4 * n * n}", nameof(composite));
        float k = flame.DensityGain * MathF.Pow(p.Noise, flame.FlowDensityPow);
        float flameRho = RhoFor(p.FlameK, k, flame.DensityPower);
        float coreRho = RhoFor(p.CoreK, k, flame.DensityPower);
        var f = new Accumulator(n, extents);
        var c = new Accumulator(n, extents);
        for (int row = 0; row < n; row++)
            Row(composite.Slice(4 * n * row, 4 * n), row, n, flameRho, coreRho, ref f, ref c);
        return new FireMetrics(f.Region(), c.Region(), default, flameRho, coreRho);
    }

    private static void Row(ReadOnlySpan<Half> row, int y, int n, float flameRho, float coreRho, ref Accumulator flame, ref Accumulator core)
    {
        for (int x = 0; x < n; x++)
        {
            float rho = Math.Clamp((float)row[4 * x + 2], 0f, 1f);
            if (!(rho > 0)) continue;
            if (rho >= flameRho) flame.Add(x, y);
            if (rho >= coreRho) core.Add(x, y);
        }
    }

    /// <summary>ρ, при которой k̄ = gain·ρ^power достигает <paramref name="threshold"/> (выше 1 — порог недостижим: 1).</summary>
    private static float RhoFor(float threshold, float gain, float power) =>
        gain > 0 ? MathF.Min(MathF.Pow(threshold / gain, 1f / power), 1f) : 1f;

    /// <summary>
    /// Столб дыма по 3D-полю газа <paramref name="grid"/>: проекция вдоль мировой z — τ(x, y) = Σ_z σ(ρ ячейки)·ячейка·UnitsPerMetre, область
    /// τ ≥ <paramref name="tau"/>; края и центроид — см от <paramref name="knob"/>, x — мировая x, y — вверх. Вид сбоку: высота столба от угла
    /// камеры вокруг вертикали не зависит, ширина — по x.
    /// </summary>
    public static FireRegion SmokeColumn(SmokeGrid grid, Vector3 knob, SmokeParams smoke, float tau)
    {
        var acc = new ColumnAccumulator();
        float k = grid.CellSize * smoke.UnitsPerMetre;
        for (int y = 0; y < grid.Ny; y++)
            ColumnRow(grid, y, k, smoke, tau, ref acc);
        if (acc.Count == 0) return default;
        float cm = grid.CellSize * 100f;
        var origin = (grid.Min - knob) * 100f;
        return new FireRegion(acc.Count, origin.Y + (acc.MaxY + 1) * cm, origin.Y + acc.MinY * cm, origin.X + acc.MinX * cm,
            origin.X + (acc.MaxX + 1) * cm,
            new Vector2(origin.X + (float)(acc.SumX / acc.Count + 0.5) * cm, origin.Y + (float)(acc.SumY / acc.Count + 0.5) * cm));
    }

    private static void ColumnRow(SmokeGrid grid, int y, float k, SmokeParams smoke, float tau, ref ColumnAccumulator acc)
    {
        for (int x = 0; x < grid.Nx; x++)
        {
            float sum = 0;
            for (int z = 0; z < grid.Nz; z++) sum += SmokeMath.Sigma(grid.Cell(x, y, z), smoke);
            if (sum * k >= tau) acc.Add(x, y);
        }
    }

    /// <summary>Полуширина полосы у оси пламени, где ищется начало дыма, см.</summary>
    public const float JointAxisBandCm = 15f;

    /// <summary>Полуширина полосы у оси пламени, где берётся центр струи дыма, см.</summary>
    public const float JointJetBandCm = 30f;

    /// <summary>Рядов профиля τ у оси от ряда верха пламени вверх (<see cref="FireJoint.TauNearAxis"/>).</summary>
    public const int JointProfileRows = 12;

    /// <summary>Выше верха пламени дым ищется не дальше, см.</summary>
    public const float JointSearchCm = 100f;

    /// <summary>
    /// Стык (<see cref="FireJoint"/>) по полю пламени <paramref name="composite"/> (как <see cref="Compute"/>) и 3D-полю дыма <paramref name="grid"/>:
    /// ячейки газа проецируются на площадку <paramref name="plane"/> с центром <paramref name="knob"/> вдоль её нормали (взгляда) в клетки
    /// размера ячейки; τ клетки = Σ σ(ρ·(1 − Detail/2))·ячейка·UnitsPerMetre — σ марша при среднем детализирующем шуме. Ось пламени —
    /// прямая по текселям пламени выше центра (наименьшие квадраты x по y).
    /// </summary>
    public static FireJoint Joint(ReadOnlySpan<Half> composite, int n, Vector2 extents, FlameParams flame, FireMetricsParams p, SmokeGrid grid,
        Vector3 knob, Quaternion plane, SmokeParams smoke)
    {
        if (composite.Length < 4 * n * n) throw new ArgumentException($"поле {composite.Length} half, нужно {4 * n * n}", nameof(composite));
        float flameRho = RhoFor(p.FlameK, flame.DensityGain * MathF.Pow(p.Noise, flame.FlowDensityPow), flame.DensityPower);
        var fit = new AxisFit();
        float cx = extents.X * 100f / n, cy = extents.Y * 100f / n;
        int top = -1;
        for (int row = 0; row < n; row++)
            if (FlameRow(composite.Slice(4 * n * row, 4 * n), row, n, cx, cy, flameRho, ref fit) && top < 0) top = row;
        if (top < 0 || fit.W == 0) return default;
        float flameTop = (n * 0.5f - top) * cy;
        float slope = fit.Slope;
        float axisX = fit.MeanX + slope * (flameTop - fit.MeanY);
        var bins = SmokeBins.Project(grid, knob, plane, smoke);
        float start = float.NaN;
        for (int r = bins.Row(flameTop), last = bins.Row(flameTop + JointSearchCm); r <= last && float.IsNaN(start); r++)
            if (bins.MaxNear(r, axisX + slope * (bins.RowCentre(r) - flameTop), JointAxisBandCm) >= p.JointTau) start = bins.RowBottom(r);
        var profile = new float[JointProfileRows];
        for (int i = 0, r0 = bins.Row(flameTop); i < profile.Length; i++)
            profile[i] = MathF.Round(bins.MaxNear(Math.Min(r0 + i, bins.Rows - 1), axisX + slope * (bins.RowCentre(r0 + i) - flameTop), JointAxisBandCm), 3);
        if (float.IsNaN(start)) return new FireJoint { TauNearAxis = profile };
        int r10 = bins.Row(flameTop + 10f), r30 = bins.Row(flameTop + 30f);
        float y10 = bins.RowCentre(r10), y30 = bins.RowCentre(r30);
        float x10 = bins.Centre(r10, axisX + slope * (y10 - flameTop), JointJetBandCm);
        float x30 = bins.Centre(r30, axisX + slope * (y30 - flameTop), JointJetBandCm);
        float smokeAngle = float.IsNaN(x10) || float.IsNaN(x30) ? float.NaN : MathF.Atan2(x30 - x10, y30 - y10) * (180f / MathF.PI);
        return new FireJoint(true, flameTop, axisX, MathF.Atan(slope) * (180f / MathF.PI), start, MathF.Max(0, start - flameTop), x10, x30,
            smokeAngle, profile);
    }

    private static bool FlameRow(ReadOnlySpan<Half> row, int y, int n, float cx, float cy, float flameRho, ref AxisFit fit)
    {
        bool any = false;
        float yc = (n * 0.5f - y - 0.5f) * cy;
        for (int x = 0; x < n; x++)
        {
            float rho = Math.Clamp((float)row[4 * x + 2], 0f, 1f);
            if (!(rho >= flameRho)) continue;
            any = true;
            if (yc >= 0) fit.Add((x + 0.5f - n * 0.5f) * cx, yc);
        }
        return any;
    }

    private struct AxisFit
    {
        public double W, X, Y, XY, YY;

        public void Add(float x, float y)
        {
            W++;
            X += x;
            Y += y;
            XY += (double)x * y;
            YY += (double)y * y;
        }

        public readonly float MeanX => (float)(X / W);
        public readonly float MeanY => (float)(Y / W);

        public readonly float Slope
        {
            get
            {
                double vy = YY / W - (Y / W) * (Y / W);
                return vy > 1e-6 ? (float)((XY / W - (X / W) * (Y / W)) / vy) : 0f;
            }
        }
    }

    /// <summary>Проекция 3D-дыма на площадку: клетки размера ячейки газа, x — ±40 клеток от центра шишки, y — от −20 клеток, 120 рядов.</summary>
    private sealed class SmokeBins
    {
        private const int HalfX = 40, Below = 20, RowCount = 120;
        private readonly float[] _tau = new float[2 * HalfX * RowCount];
        private float _bin;

        public static SmokeBins Project(SmokeGrid grid, Vector3 knob, Quaternion plane, SmokeParams smoke)
        {
            var b = new SmokeBins { _bin = grid.CellSize * 100f };
            var ax = Vector3.Transform(Vector3.UnitX, plane);
            var ay = Vector3.Transform(Vector3.UnitY, plane);
            float k = grid.CellSize * smoke.UnitsPerMetre, mean = MathF.Max(0f, 1f - 0.5f * smoke.Detail);
            for (int z = 0; z < grid.Nz; z++)
                for (int y = 0; y < grid.Ny; y++) b.Add(grid, y, z, knob, ax, ay, k, mean, smoke);
            return b;
        }

        private void Add(SmokeGrid grid, int y, int z, Vector3 knob, Vector3 ax, Vector3 ay, float k, float mean, SmokeParams smoke)
        {
            int at = grid.Nx * (y + grid.Ny * z);
            for (int x = 0; x < grid.Nx; x++)
            {
                float sigma = SmokeMath.Sigma(grid.Rho[at + x] * mean, smoke);
                if (!(sigma > 0)) continue;
                var d = (grid.Min + (new Vector3(x, y, z) + new Vector3(0.5f)) * grid.CellSize - knob) * 100f;
                int bx = (int)MathF.Floor(Vector3.Dot(d, ax) / _bin) + HalfX, by = (int)MathF.Floor(Vector3.Dot(d, ay) / _bin) + Below;
                if (bx < 0 || bx >= 2 * HalfX || by < 0 || by >= RowCount) continue;
                _tau[by * 2 * HalfX + bx] += sigma * k;
            }
        }

        public int Rows => RowCount;

        public int Row(float yCm) => Math.Clamp((int)MathF.Floor(yCm / _bin) + Below, 0, RowCount - 1);
        public float RowBottom(int r) => (r - Below) * _bin;
        public float RowCentre(int r) => (r - Below + 0.5f) * _bin;

        /// <summary>Наибольшая τ ряда <paramref name="r"/> в полосе ±<paramref name="band"/> см у <paramref name="x"/>.</summary>
        public float MaxNear(int r, float x, float band)
        {
            float best = 0;
            for (int i = 0; i < 2 * HalfX; i++)
                if (MathF.Abs((i - HalfX + 0.5f) * _bin - x) <= band) best = MathF.Max(best, _tau[r * 2 * HalfX + i]);
            return best;
        }

        /// <summary>Центр τ ряда <paramref name="r"/> в полосе ±<paramref name="band"/> см у <paramref name="x"/>, см; дыма нет — NaN.</summary>
        public float Centre(int r, float x, float band)
        {
            double w = 0, s = 0;
            for (int i = 0; i < 2 * HalfX; i++)
            {
                float c = (i - HalfX + 0.5f) * _bin, t = _tau[r * 2 * HalfX + i];
                if (MathF.Abs(c - x) > band || !(t > 0)) continue;
                w += t;
                s += t * c;
            }
            return w > 0 ? (float)(s / w) : float.NaN;
        }
    }

    /// <summary>
    /// Профиль столба дыма по высоте: слои по <paramref name="layerCells"/> ячеек от низа окна; в слое — Σσ ячеек (масса дыма в
    /// единицах σ), наибольшая ρ, центр масс σ от шишки по x и z и радиус инерции σ по горизонтали (≈ полуширина видимого столба), см.
    /// Слои без дыма пропускаются.
    /// </summary>
    public static SmokeLayer[] SmokeProfile(SmokeGrid grid, Vector3 knob, SmokeParams smoke, int layerCells)
    {
        var layers = new List<SmokeLayer>();
        for (int y0 = 0; y0 < grid.Ny; y0 += layerCells)
        {
            var sum = new LayerSum();
            for (int y = y0; y < Math.Min(y0 + layerCells, grid.Ny); y++) LayerRow(grid, y, smoke, ref sum);
            if (!(sum.Mass > 0)) continue;
            double cx = sum.X / sum.Mass, cz = sum.Z / sum.Mass;
            double r2 = Math.Max(sum.XX / sum.Mass - cx * cx + sum.ZZ / sum.Mass - cz * cz, 0);
            float cm = grid.CellSize * 100f;
            float height = (grid.Min.Y + (y0 + Math.Min(layerCells, grid.Ny - y0) * 0.5f) * grid.CellSize - knob.Y) * 100f;
            layers.Add(new SmokeLayer(MathF.Round(height, 1), MathF.Round((float)sum.Mass, 3), MathF.Round(sum.MaxRho, 4),
                MathF.Round((grid.Min.X - knob.X) * 100f + (float)(cx + 0.5) * cm, 1), MathF.Round((grid.Min.Z - knob.Z) * 100f + (float)(cz + 0.5) * cm, 1),
                MathF.Round((float)Math.Sqrt(r2) * cm, 1)));
        }
        return [.. layers];
    }

    private static void LayerRow(SmokeGrid grid, int y, SmokeParams smoke, ref LayerSum sum)
    {
        for (int z = 0; z < grid.Nz; z++)
            for (int x = 0; x < grid.Nx; x++)
            {
                float rho = grid.Rho[x + grid.Nx * (y + grid.Ny * z)];
                float sigma = SmokeMath.Sigma(rho, smoke);
                if (!(sigma > 0)) continue;
                sum.Mass += sigma;
                sum.X += sigma * x;
                sum.Z += sigma * z;
                sum.XX += (double)sigma * x * x;
                sum.ZZ += (double)sigma * z * z;
                sum.MaxRho = MathF.Max(sum.MaxRho, rho);
            }
    }

    private struct LayerSum
    {
        public double Mass, X, Z, XX, ZZ;
        public float MaxRho;
    }

    private struct ColumnAccumulator
    {
        public int Count, MinX, MaxX, MinY, MaxY;
        public double SumX, SumY;

        public void Add(int x, int y)
        {
            if (Count == 0)
            {
                MinX = MaxX = x;
                MinY = MaxY = y;
            }
            Count++;
            MinX = Math.Min(MinX, x);
            MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y);
            MaxY = Math.Max(MaxY, y);
            SumX += x;
            SumY += y;
        }
    }

    private struct Accumulator(int n, Vector2 extents)
    {
        private int _count, _minX = int.MaxValue, _maxX = -1, _minY = int.MaxValue, _maxY = -1;
        private double _sumX, _sumY;

        public void Add(int x, int y)
        {
            _count++;
            _minX = Math.Min(_minX, x);
            _maxX = Math.Max(_maxX, x);
            _minY = Math.Min(_minY, y);
            _maxY = Math.Max(_maxY, y);
            _sumX += x;
            _sumY += y;
        }

        /// <summary>Тексель → см от центра: x = (i/n − 0.5)·Ex, y = (0.5 − j/n)·Ey (края текселей).</summary>
        public readonly FireRegion Region()
        {
            if (_count == 0) return default;
            float cx = extents.X * 100f / n, cy = extents.Y * 100f / n;
            float half = n * 0.5f;
            return new FireRegion(_count, (half - _minY) * cy, (half - _maxY - 1) * cy, (_minX - half) * cx, (_maxX + 1 - half) * cx,
                new Vector2((float)((_sumX / _count + 0.5 - half) * cx), (float)((half - _sumY / _count - 0.5) * cy)));
        }
    }
}
