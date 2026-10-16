using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Источник в числах шага (<c>GasSource</c> HLSL), индексы окна (центры ячеек — целые): <see cref="Tail"/> — начало отрезка, w — внешний радиус
/// r/dx + h/2 в ячейках; <see cref="Axis"/> — конец − начало, w — 1/|Axis|² (у шара 0); <see cref="Rates"/> — ρ·dt, T·dt, доля скорости за
/// шаг, 1/h (h — ширина края в ячейках); <see cref="Velocity"/> — скорость газа источника.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fluid3DSourceData
{
    public Vector4 Tail, Axis, Rates, Velocity;
}

[InlineArray(Fluid3DConstants.MaxSources)]
internal struct Fluid3DSourceArray
{
    private Fluid3DSourceData _element;
}

/// <summary>
/// Числа одного шага — общие для GPU (элемент <c>StructuredBuffer&lt;GasConstants&gt;</c>, <c>fluid3d.hlsl</c>) и CPU-эталона: обе стороны
/// считают по одним и тем же готовым числам (раскладка плотная, сверка — <c>_Static_assert</c> с <c>FLUID3D_CONSTANTS_SIZE</c>).
/// Всё, что не зависит от ячейки (обратные величины, веса SOR, источники в индексах окна), считается здесь в double.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fluid3DConstants
{
    /// <summary>Источников за шаг не больше.</summary>
    public const int MaxSources = 8;

    public uint Nx, Ny, Nz, Sources;
    public int ShiftX, ShiftY, ShiftZ;
    public uint WarmStart;
    public int SourceMinX, SourceMinY, SourceMinZ;
    public float Dt;
    public int SourceMaxX, SourceMaxY, SourceMaxZ;
    public float HalfInvDx;
    public float BackStep, Vorticity, MaxSpeed, MaxSpeed2;
    public float Alpha, Beta, GravityX, GravityY;
    public float GravityZ, DensityKeep, DensityLoss, TemperatureKeep;
    public float TemperatureLoss, SorKeep, SorWeight6, PressureRhs;
    public float GradScale;
    public uint StepLo, StepHi;
    public int OriginX, OriginY, OriginZ;
    public float CellSize;
    public float NoiseAmp;
    public float NoiseStep, NoiseInvDensity, NoiseX, NoiseY;
    public float NoiseZ;
    public float WindX, WindY, WindZ;
    public float PlaneX, PlaneY, PlaneZ;
    public uint PlaneSide;
    public float PlaneUx, PlaneUy, PlaneUz, PlaneHalf;
    public float PlaneVx, PlaneVy, PlaneVz, PlaneThick;
    public float PlaneNx, PlaneNy, PlaneNz, PlaneInvEdge;
    public uint PlaneOffset, PlanePad0, PlanePad1, PlanePad2;
    public Fluid3DSourceArray Source;

    public readonly int Cells => (int)(Nx * Ny * Nz);

    /// <param name="shift">Сдвиг окна за шаг в ячейках (ячейка x нового окна — x + сдвиг прошлого).</param>
    /// <param name="plane">Источник-карта шага (null — нет); <paramref name="mapOffset"/> — номер первой клетки его карты в буфере.</param>
    public static Fluid3DConstants Build(Fluid3DParams p, Fluid3DWindow window, (int X, int Y, int Z) shift, ulong step,
        ReadOnlySpan<Fluid3DSource> sources, Fluid3DPlane? plane = null, int mapOffset = 0)
    {
        if (sources.Length > MaxSources) throw new ArgumentException($"источников {sources.Length}, больше {MaxSources}", nameof(sources));
        double dx = p.CellSize, dt = p.StepSeconds;
        double w = p.SorWeightValue;
        var k = new Fluid3DConstants
        {
            Nx = (uint)p.Nx,
            Ny = (uint)p.Ny,
            Nz = (uint)p.Nz,
            ShiftX = shift.X,
            ShiftY = shift.Y,
            ShiftZ = shift.Z,
            WarmStart = p.PressureWarmStart ? 1u : 0u,
            Dt = (float)dt,
            HalfInvDx = (float)(0.5 / dx),
            BackStep = (float)(dt / dx),
            Vorticity = (float)(p.VorticityConfinement * dx),
            MaxSpeed = p.MaxSpeed,
            MaxSpeed2 = (float)((double)p.MaxSpeed * p.MaxSpeed),
            Alpha = p.DensityBuoyancy,
            Beta = p.TemperatureBuoyancy,
            GravityX = p.Gravity.X,
            GravityY = p.Gravity.Y,
            GravityZ = p.Gravity.Z,
            DensityKeep = (float)(1 / (1 + p.DensityDecay * dt)),
            DensityLoss = (float)(p.DensityDissipation * dt),
            TemperatureKeep = (float)(1 / (1 + p.TemperatureDecay * dt)),
            TemperatureLoss = (float)(p.TemperatureDissipation * dt),
            SorKeep = (float)(1 - w),
            SorWeight6 = (float)(w / 6),
            PressureRhs = (float)(dx * dx / dt),
            GradScale = (float)(dt * 0.5 / dx),
            StepLo = (uint)step,
            StepHi = (uint)(step >> 32),
            OriginX = window.X,
            OriginY = window.Y,
            OriginZ = window.Z,
            CellSize = p.CellSize,
            WindX = (float)(p.Wind.X * dt / dx),
            WindY = (float)(p.Wind.Y * dt / dx),
            WindZ = (float)(p.Wind.Z * dt / dx),
            SourceMinX = int.MaxValue,
            SourceMinY = int.MaxValue,
            SourceMinZ = int.MaxValue,
            SourceMaxX = int.MinValue,
            SourceMaxY = int.MinValue,
            SourceMaxZ = int.MinValue,
        };
        if (p.CurlNoise > 0)
        {
            // Координаты шума центра ячейки c: c·dx/L + (угол окна + ½)·dx/L, узор поднимается на подъём·время; по модулю периода решётки.
            double l = p.CurlNoiseScale, time = step * dt;
            k.NoiseAmp = (float)(p.CurlNoise * (double)Fluid3DCurlNoise.Normalization);
            k.NoiseStep = (float)(dx / l);
            k.NoiseInvDensity = p.CurlNoiseDensity > 0 ? (float)(1 / (double)p.CurlNoiseDensity) : 0;
            k.NoiseX = Fluid3DCurlNoise.Wrap((window.X + 0.5) * dx / l);
            k.NoiseY = Fluid3DCurlNoise.Wrap(((window.Y + 0.5) * dx - p.CurlNoiseRise * time) / l);
            k.NoiseZ = Fluid3DCurlNoise.Wrap((window.Z + 0.5) * dx / l);
        }
        uint n = 0;
        foreach (var s in sources)
        {
            var from = s.From ?? s.Center;
            if (!(s.Radius > 0) || !float.IsFinite(s.Center.X + s.Center.Y + s.Center.Z) || !float.IsFinite(from.X + from.Y + from.Z)) continue;
            double edge = s.Edge / dx > 1 ? s.Edge / dx : 1;
            double r = s.Radius / dx + 0.5 * edge;
            double bx = s.Center.X / dx - window.X - 0.5, by = s.Center.Y / dx - window.Y - 0.5, bz = s.Center.Z / dx - window.Z - 0.5;
            double ax = from.X / dx - window.X - 0.5, ay = from.Y / dx - window.Y - 0.5, az = from.Z / dx - window.Z - 0.5;
            int minX = Floor(Math.Min(ax, bx) - r), minY = Floor(Math.Min(ay, by) - r), minZ = Floor(Math.Min(az, bz) - r);
            int maxX = Ceil(Math.Max(ax, bx) + r), maxY = Ceil(Math.Max(ay, by) + r), maxZ = Ceil(Math.Max(az, bz) + r);
            if (maxX < 0 || maxY < 0 || maxZ < 0 || minX >= p.Nx || minY >= p.Ny || minZ >= p.Nz) continue;
            k.SourceMinX = Math.Min(k.SourceMinX, minX);
            k.SourceMinY = Math.Min(k.SourceMinY, minY);
            k.SourceMinZ = Math.Min(k.SourceMinZ, minZ);
            k.SourceMaxX = Math.Max(k.SourceMaxX, maxX);
            k.SourceMaxY = Math.Max(k.SourceMaxY, maxY);
            k.SourceMaxZ = Math.Max(k.SourceMaxZ, maxZ);
            var axis = new Vector3((float)(bx - ax), (float)(by - ay), (float)(bz - az));
            double length2 = (double)axis.X * axis.X + (double)axis.Y * axis.Y + (double)axis.Z * axis.Z;
            if (!(length2 > 1e-12)) axis = Vector3.Zero;
            k.Source[(int)n++] = new Fluid3DSourceData
            {
                Tail = new Vector4((float)ax, (float)ay, (float)az, (float)r),
                Axis = new Vector4(axis, length2 > 1e-12 ? (float)(1 / length2) : 0),
                Rates = new Vector4((float)(s.Density * dt), (float)(s.Temperature * dt), (float)Math.Min(1, Math.Max(0, s.VelocityRate * dt)),
                    (float)(1 / edge)),
                Velocity = new Vector4(s.Velocity, 0),
            };
        }
        k.Sources = n;
        if (plane is { } q) AddPlane(ref k, p, window, q, mapOffset);
        return k;
    }

    /// <summary>
    /// Источник-карта в индексах окна: начало — центр карты, оси карты в клетках на ячейку (dx/клетка), нормаль в ячейках, полутолщина слоя
    /// h/dx + e/2 и 1/e; бокс источников расширяется на бокс слоя (углы карты ± полутолщина с краем вдоль нормали).
    /// </summary>
    private static void AddPlane(ref Fluid3DConstants k, Fluid3DParams p, Fluid3DWindow window, Fluid3DPlane q, int mapOffset)
    {
        q.Validate();
        double dx = p.CellSize;
        double edge = q.Edge / dx > 1 ? q.Edge / dx : 1;
        double thick = q.HalfThickness / dx + 0.5 * edge;
        double ox = q.Center.X / dx - window.X - 0.5, oy = q.Center.Y / dx - window.Y - 0.5, oz = q.Center.Z / dx - window.Z - 0.5;
        var normal = Vector3.Normalize(q.Normal);
        double scale = dx / q.Texel, half = 0.5 * q.Side * q.Texel / dx;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int c = 0; c < 8; c++)
        {
            double a = (c & 1) != 0 ? half : -half, b = (c & 2) != 0 ? half : -half, t = (c & 4) != 0 ? thick : -thick;
            double x = ox + q.AxisX.X * a + q.AxisY.X * b + normal.X * t, y = oy + q.AxisX.Y * a + q.AxisY.Y * b + normal.Y * t;
            double z = oz + q.AxisX.Z * a + q.AxisY.Z * b + normal.Z * t;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }
        int bx0 = Floor(minX), by0 = Floor(minY), bz0 = Floor(minZ), bx1 = Ceil(maxX), by1 = Ceil(maxY), bz1 = Ceil(maxZ);
        if (bx1 < 0 || by1 < 0 || bz1 < 0 || bx0 >= p.Nx || by0 >= p.Ny || bz0 >= p.Nz) return;
        k.SourceMinX = Math.Min(k.SourceMinX, bx0);
        k.SourceMinY = Math.Min(k.SourceMinY, by0);
        k.SourceMinZ = Math.Min(k.SourceMinZ, bz0);
        k.SourceMaxX = Math.Max(k.SourceMaxX, bx1);
        k.SourceMaxY = Math.Max(k.SourceMaxY, by1);
        k.SourceMaxZ = Math.Max(k.SourceMaxZ, bz1);
        k.PlaneX = (float)ox;
        k.PlaneY = (float)oy;
        k.PlaneZ = (float)oz;
        k.PlaneSide = (uint)q.Side;
        k.PlaneUx = (float)(q.AxisX.X * scale);
        k.PlaneUy = (float)(q.AxisX.Y * scale);
        k.PlaneUz = (float)(q.AxisX.Z * scale);
        k.PlaneHalf = (float)(0.5 * q.Side - 0.5);
        k.PlaneVx = (float)(q.AxisY.X * scale);
        k.PlaneVy = (float)(q.AxisY.Y * scale);
        k.PlaneVz = (float)(q.AxisY.Z * scale);
        k.PlaneThick = (float)thick;
        k.PlaneNx = normal.X;
        k.PlaneNy = normal.Y;
        k.PlaneNz = normal.Z;
        k.PlaneInvEdge = (float)(1 / edge);
        k.PlaneOffset = (uint)mapOffset;
    }

    private static int Floor(double x) => (int)Math.Clamp(Math.Floor(x), -1, int.MaxValue / 2);

    private static int Ceil(double x) => (int)Math.Clamp(Math.Ceiling(x), int.MinValue / 2, 1 << 20);
}
