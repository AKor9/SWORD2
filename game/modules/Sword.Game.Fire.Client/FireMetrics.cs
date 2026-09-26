using System.Numerics;

namespace Sword.Game.Fire;

/// <summary>
/// Пороги размеров огня по полю. Пламя и ядро — по яркости формулы пламени k̄ = DensityGain·n̄^FlowDensityPow·ρ^DensityPower при среднем шуме
/// <see cref="Noise"/> (шум flow-map в поле не записан): пламя — k̄ ≥ <see cref="FlameK"/>, ядро — k̄ ≥ <see cref="CoreK"/> (цвет рампы — Colour1).
/// Дым — оптическая толщина слоя вдоль нормали площадки τ ≥ <see cref="SmokeTau"/> (σ дыма по толщине объёма, маска краёв).
/// </summary>
public sealed record FireMetricsParams
{
    public float Noise { get; init; } = 0.5f;
    public float FlameK { get; init; } = 0.1f;
    public float CoreK { get; init; } = 1f;
    public float SmokeTau { get; init; } = 0.1f;

    public static FireMetricsParams Default { get; } = new();
}

/// <summary>
/// Область поля над порогом в осях площадки (x — вправо, y — вверх) от её центра (центр шишки на шаге), см: края текселей крайних рядов и
/// столбцов, центроид по текселям области. Пусто — <see cref="Texels"/> 0.
/// </summary>
public readonly record struct FireRegion(int Texels, float TopCm, float BottomCm, float LeftCm, float RightCm, Vector2 CentroidCm)
{
    public float HeightCm => Texels > 0 ? TopCm - BottomCm : 0;
    public float WidthCm => Texels > 0 ? RightCm - LeftCm : 0;
}

/// <summary>
/// Размеры огня по полю сима: пламя, ядро, столб дыма (<see cref="FireRegion"/>), доля ядра по высоте, верх дыма над верхом пламени; пороги ρ
/// пламени и ядра, которые дали пороги k̄.
/// </summary>
public readonly record struct FireMetrics(FireRegion Flame, FireRegion Core, FireRegion Smoke, float FlameRho, float CoreRho)
{
    /// <summary>Высота ядра / высота пламени (пламени нет — 0).</summary>
    public float CoreShare => Flame.HeightCm > 0 ? Core.HeightCm / Flame.HeightCm : 0;

    /// <summary>Верх столба дыма над верхом пламени, см (нет одного из них — 0).</summary>
    public float SmokeAboveFlameCm => Flame.Texels > 0 && Smoke.Texels > 0 ? Smoke.TopCm - Flame.TopCm : 0;

    /// <summary>
    /// Размеры по выходу поля C (<paramref name="composite"/>: <paramref name="n"/>² текселей RGBA half — v.xy, ρ, wet; ряд 0 — v = 0, верх
    /// площадки) площадки <paramref name="extents"/> (м).
    /// </summary>
    public static FireMetrics Compute(ReadOnlySpan<Half> composite, int n, Vector2 extents, FlameParams flame, SmokeParams smoke, FireMetricsParams p)
    {
        if (composite.Length < 4 * n * n) throw new ArgumentException($"поле {composite.Length} half, нужно {4 * n * n}", nameof(composite));
        float k = flame.DensityGain * MathF.Pow(p.Noise, flame.FlowDensityPow);
        float flameRho = RhoFor(p.FlameK, k, flame.DensityPower);
        float coreRho = RhoFor(p.CoreK, k, flame.DensityPower);
        Span<float> tau = stackalloc float[TauTable + 1];
        for (int i = 0; i <= TauTable; i++) tau[i] = NormalTau((float)i / TauTable, smoke);
        var f = new Accumulator(n, extents);
        var c = new Accumulator(n, extents);
        var s = new Accumulator(n, extents);
        for (int row = 0; row < n; row++)
            Row(composite.Slice(4 * n * row, 4 * n), row, n, flameRho, coreRho, tau, smoke, p.SmokeTau, ref f, ref c, ref s);
        return new FireMetrics(f.Region(), c.Region(), s.Region(), flameRho, coreRho);
    }

    private const int TauTable = 256;

    private static void Row(ReadOnlySpan<Half> row, int y, int n, float flameRho, float coreRho, ReadOnlySpan<float> tau, SmokeParams smoke,
        float smokeTau, ref Accumulator flame, ref Accumulator core, ref Accumulator smokeRegion)
    {
        float v = (y + 0.5f) / n;
        for (int x = 0; x < n; x++)
        {
            float rho = Math.Clamp((float)row[4 * x + 2], 0f, 1f);
            if (!(rho > 0)) continue;
            if (rho >= flameRho) flame.Add(x, y);
            if (rho >= coreRho) core.Add(x, y);
            float t = rho * TauTable;
            int i = Math.Min((int)t, TauTable - 1);
            float normal = tau[i] + (tau[i + 1] - tau[i]) * (t - i);
            if (SmokeMath.Edge(new Vector2((x + 0.5f) / n, v), smoke) * normal >= smokeTau) smokeRegion.Add(x, y);
        }
    }

    /// <summary>ρ, при которой k̄ = gain·ρ^power достигает <paramref name="threshold"/> (выше 1 — порог недостижим: 1).</summary>
    private static float RhoFor(float threshold, float gain, float power) =>
        gain > 0 ? MathF.Min(MathF.Pow(threshold / gain, 1f / power), 1f) : 1f;

    /// <summary>
    /// Оптическая толщина слоя дыма вдоль нормали площадки при плотности <paramref name="rho"/> без маски краёв: UnitsPerMetre·Ez·∫σ(ρ, z)dz,
    /// середины 256 отрезков по uvw.z.
    /// </summary>
    public static float NormalTau(float rho, SmokeParams smoke)
    {
        const int steps = 256;
        double sum = 0;
        for (int i = 0; i < steps; i++)
            sum += SmokeMath.Sigma(rho, new Vector3(0.5f, 0.5f, (i + 0.5f) / steps), smoke);
        return (float)(sum / steps * smoke.Extents.Z * smoke.UnitsPerMetre);
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
