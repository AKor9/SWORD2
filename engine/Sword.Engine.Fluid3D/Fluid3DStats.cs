using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Formats;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Статистика состояния после шага (GPU-редукция в фиксированном порядке, <c>CsStatsSlices</c> → <c>CsStats</c>): Σρ, ΣT, max|v|, max ρ,
/// max T, число ячеек с Inf/NaN (в суммы не входят), центроид ρ в мире, Σ|div| до давления (скорость после адвекции) и после (итог шага),
/// энстрофия Σ|ω|². Сырые <see cref="Bytes"/> байт — <see cref="Fluid3DSim.StatsStorage"/> (асинхронно — <c>Renderer.CreateReadback</c>,
/// разбор — <see cref="Parse"/>).
/// </summary>
public readonly record struct Fluid3DStats(ulong Step, Fluid3DWindow Window, float DensitySum, float TemperatureSum, float MaxSpeed,
    float MaxDensity, float MaxTemperature, int NonFinite, Vector3 CentroidIndex, float DivergenceBefore, float DivergenceAfter, float Enstrophy)
{
    public const int Bytes = 128;

    /// <summary>Центроид плотности в мире (без плотности — центр бокса).</summary>
    public Vector3 Centroid => Window.ToWorld(CentroidIndex - new Vector3(0.5f));

    /// <summary>Остаток дивергенции после давления: Σ|div| после / до (0 — без дивергенции до).</summary>
    public float DivergenceRatio => DivergenceBefore > 0 ? DivergenceAfter / DivergenceBefore : 0;

    public static Fluid3DStats Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Bytes) throw new ArgumentException($"статистика газа: {raw.Length} Б, нужно {Bytes}", nameof(raw));
        var u = MemoryMarshal.Cast<byte, uint>(raw[..Bytes]);
        var f = MemoryMarshal.Cast<byte, float>(raw[..Bytes]);
        var window = new Fluid3DWindow((int)u[6], (int)u[7], (int)u[8], (int)u[3], (int)u[4], (int)u[5], f[9], default);
        window = window with { Target = window.Center };
        return new Fluid3DStats(u[0] | ((ulong)u[1] << 32), window, f[10], f[11], f[12], f[13], f[14], (int)u[2], new Vector3(f[15], f[16], f[17]),
            f[18], f[19], f[20]);
    }

    /// <summary>То же на CPU по полю (в double) — сверка GPU-редукции и снимок.</summary>
    public static Fluid3DStats Compute(Fluid3DFields f, Fluid3DWindow window, ulong step)
    {
        double rho = 0, temp = 0, sx = 0, sy = 0, sz = 0, before = 0, after = 0, enstrophy = 0;
        float maxSpeed = 0, maxRho = 0, maxT = 0;
        int bad = 0;
        double halfInvDx = 0.5 / window.CellSize;
        for (int z = 0; z < f.Nz; z++)
            for (int y = 0; y < f.Ny; y++)
                for (int x = 0; x < f.Nx; x++)
                {
                    int i = f.Index(x, y, z);
                    float vx = (float)f.Velocity[4 * i], vy = (float)f.Velocity[4 * i + 1], vz = (float)f.Velocity[4 * i + 2];
                    float r = (float)f.Scalars[2 * i], t = (float)f.Scalars[2 * i + 1];
                    if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(vz) || !float.IsFinite(r) || !float.IsFinite(t))
                    {
                        bad++;
                        continue;
                    }
                    rho += r;
                    temp += t;
                    sx += r * (x + 0.5);
                    sy += r * (y + 0.5);
                    sz += r * (z + 0.5);
                    maxSpeed = MathF.Max(maxSpeed, MathF.Sqrt(vx * vx + vy * vy + vz * vz));
                    maxRho = MathF.Max(maxRho, r);
                    maxT = MathF.Max(maxT, t);
                    before += Math.Abs((float)f.Aux[2 * i + 1]);
                    var (div, w2) = Derivatives(f, x, y, z, halfInvDx);
                    after += Math.Abs(div);
                    enstrophy += w2;
                }
        var centroid = rho > 0 ? new Vector3((float)(sx / rho), (float)(sy / rho), (float)(sz / rho)) : new Vector3(f.Nx, f.Ny, f.Nz) * 0.5f;
        return new Fluid3DStats(step, window, (float)rho, (float)temp, maxSpeed, maxRho, maxT, bad, centroid, (float)before, (float)after,
            (float)enstrophy);
    }

    /// <summary>Дивергенция и |ω|² итоговой скорости в ячейке (центральные разности, скорость у края — крайняя ячейка).</summary>
    private static (double Div, double Curl2) Derivatives(Fluid3DFields f, int x, int y, int z, double halfInvDx)
    {
        Vector3 V(int a, int b, int c)
        {
            int i = f.Index(Math.Clamp(a, 0, f.Nx - 1), Math.Clamp(b, 0, f.Ny - 1), Math.Clamp(c, 0, f.Nz - 1));
            return new Vector3((float)f.Velocity[4 * i], (float)f.Velocity[4 * i + 1], (float)f.Velocity[4 * i + 2]);
        }
        var dx = (V(x + 1, y, z) - V(x - 1, y, z)) * (float)halfInvDx;
        var dy = (V(x, y + 1, z) - V(x, y - 1, z)) * (float)halfInvDx;
        var dz = (V(x, y, z + 1) - V(x, y, z - 1)) * (float)halfInvDx;
        double wx = dy.Z - dz.Y, wy = dz.X - dx.Z, wz = dx.Y - dy.X;
        return ((double)dx.X + dy.Y + dz.Z, wx * wx + wy * wy + wz * wz);
    }
}

/// <summary>
/// Снимок газа для отладки: состояние (V, S, Aux), окно и статистика по полю. <see cref="SaveSlices"/> пишет срезы через мировую точку:
/// XY (z точки; столбец — x, строка 0 — верх, y вверх) и XZ (y точки; столбец — x, строка — z): <c>.density.png</c> и <c>.temperature.png</c>
/// (серый, 0…max(1, максимум среза)), <c>.velocity.png</c> (vx, vy, vz → R, G, B вокруг 0.5 в пределах ±MaxSpeed) и сырые half
/// (<c>.bin</c>: ширина × высота × 6 half — vx, vy, vz, 0, ρ, T; ряд 0 — как у PNG).
/// </summary>
public sealed class Fluid3DSnapshot(Fluid3DFields fields, Fluid3DWindow window, Fluid3DStats stats, float maxSpeed)
{
    public Fluid3DFields Fields { get; } = fields;
    public Fluid3DWindow Window { get; } = window;
    public Fluid3DStats Stats { get; } = stats;

    /// <summary>Срезы через <paramref name="world"/> (точка вне бокса — ближайший срез): файлы <paramref name="pathPrefix"/> + <c>.xy.*</c>, <c>.xz.*</c>.</summary>
    public IReadOnlyList<string> SaveSlices(string pathPrefix, Vector3 world)
    {
        var q = Window.ToIndex(world);
        int z = Math.Clamp((int)MathF.Round(q.Z), 0, Fields.Nz - 1), y = Math.Clamp((int)MathF.Round(q.Y), 0, Fields.Ny - 1);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(pathPrefix + ".xy"));
        if (dir is not null) Directory.CreateDirectory(dir);
        var files = new List<string>(8);
        Save(pathPrefix + ".xy", Fields.Nx, Fields.Ny, (c, r) => Fields.Index(c, Fields.Ny - 1 - r, z), files);
        Save(pathPrefix + ".xz", Fields.Nx, Fields.Nz, (c, r) => Fields.Index(c, y, r), files);
        return files;
    }

    private void Save(string prefix, int w, int h, Func<int, int, int> cell, List<string> files)
    {
        var raw = new Half[w * h * 6];
        float maxRho = 1, maxT = 1;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int i = cell(c, r), o = (r * w + c) * 6;
                raw[o] = Fields.Velocity[4 * i];
                raw[o + 1] = Fields.Velocity[4 * i + 1];
                raw[o + 2] = Fields.Velocity[4 * i + 2];
                raw[o + 4] = Fields.Scalars[2 * i];
                raw[o + 5] = Fields.Scalars[2 * i + 1];
                maxRho = Finite((float)raw[o + 4]) > maxRho ? (float)raw[o + 4] : maxRho;
                maxT = Finite((float)raw[o + 5]) > maxT ? (float)raw[o + 5] : maxT;
            }
        var density = new byte[w * h * 3];
        var temperature = new byte[w * h * 3];
        var velocity = new byte[w * h * 3];
        float scale = maxSpeed > 0 ? 0.5f / maxSpeed : 0.5f;
        for (int p = 0; p < w * h; p++)
        {
            density[3 * p] = density[3 * p + 1] = density[3 * p + 2] = Byte((float)raw[6 * p + 4] / maxRho);
            temperature[3 * p] = temperature[3 * p + 1] = temperature[3 * p + 2] = Byte((float)raw[6 * p + 5] / maxT);
            velocity[3 * p] = Byte(0.5f + (float)raw[6 * p] * scale);
            velocity[3 * p + 1] = Byte(0.5f + (float)raw[6 * p + 1] * scale);
            velocity[3 * p + 2] = Byte(0.5f + (float)raw[6 * p + 2] * scale);
        }
        Write(prefix + ".density.png", Png.Encode(density, w, h), files);
        Write(prefix + ".temperature.png", Png.Encode(temperature, w, h), files);
        Write(prefix + ".velocity.png", Png.Encode(velocity, w, h), files);
        Write(prefix + ".bin", MemoryMarshal.AsBytes(raw.AsSpan()).ToArray(), files);
    }

    private static void Write(string path, byte[] data, List<string> files)
    {
        File.WriteAllBytes(path, data);
        files.Add(path);
    }

    private static float Finite(float x) => float.IsFinite(x) ? x : 0;

    private static byte Byte(float x) => float.IsFinite(x) ? (byte)Math.Clamp((int)MathF.Round(x * 255f), 0, 255) : (byte)255;
}
