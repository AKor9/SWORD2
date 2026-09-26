using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Formats;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// Статистика поля C после шага (GPU-редукция в фиксированном порядке, <c>CsStatsRows</c> → <c>CsStats</c>): Σρ, max|v|, max ρ, Σwet,
/// число текселей с Inf/NaN (в суммы не входят) и центроид ρ в UV. Сырые <see cref="Bytes"/> байт — <see cref="Fluid2DSim.StatsStorage"/>
/// (асинхронно — <c>Renderer.CreateReadback</c>, разбор — <see cref="Parse"/>).
/// </summary>
public readonly record struct Fluid2DStats(ulong Step, int Resolution, float DensitySum, float MaxSpeed, float MaxDensity, float WetSum,
    int NonFinite, Vector2 Centroid)
{
    public const int Bytes = 64;

    public static Fluid2DStats Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < Bytes) throw new ArgumentException($"статистика сима: {raw.Length} Б, нужно {Bytes}", nameof(raw));
        var u = MemoryMarshal.Cast<byte, uint>(raw[..Bytes]);
        return new Fluid2DStats(u[0] | ((ulong)u[1] << 32), (int)u[3], BitConverter.UInt32BitsToSingle(u[4]),
            BitConverter.UInt32BitsToSingle(u[5]), BitConverter.UInt32BitsToSingle(u[6]), BitConverter.UInt32BitsToSingle(u[7]),
            (int)u[2], new Vector2(BitConverter.UInt32BitsToSingle(u[8]), BitConverter.UInt32BitsToSingle(u[9])));
    }

    /// <summary>То же на CPU по полю (в double) — сверка GPU-редукции и снимок агента.</summary>
    public static Fluid2DStats Compute(Fluid2DFields fields, ulong step)
    {
        int n = fields.N;
        double sum = 0, sumX = 0, sumY = 0, wet = 0;
        float maxSpeed = 0, maxDensity = 0;
        int bad = 0;
        var c = fields.Composite;
        for (int i = 0; i < n * n; i++)
        {
            float vx = (float)c[i * 4], vy = (float)c[i * 4 + 1], rho = (float)c[i * 4 + 2], w = (float)c[i * 4 + 3];
            if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(rho) || !float.IsFinite(w))
            {
                bad++;
                continue;
            }
            sum += rho;
            sumX += rho * ((i % n + 0.5) / n);
            sumY += rho * ((i / n + 0.5) / n);
            wet += w;
            maxSpeed = MathF.Max(maxSpeed, MathF.Sqrt(vx * vx + vy * vy));
            maxDensity = MathF.Max(maxDensity, rho);
        }
        var centroid = sum > 0 ? new Vector2((float)(sumX / sum), (float)(sumY / sum)) : new Vector2(0.5f);
        return new Fluid2DStats(step, n, (float)sum, maxSpeed, maxDensity, (float)wet, bad, centroid);
    }
}

/// <summary>
/// Снимок полей для агента: поле C и его статистика, грид кисти (если был Painter). <see cref="Save"/> пишет <c>.density.png</c> (ρ, серый),
/// <c>.velocity.png</c> (vx, vy → R, G вокруг 0.5 в пределах ±VelocityClamp; B — wet) и сырые half (<c>.bin</c>: N × N × 4 half RGBA, ряд 0 —
/// v = 0); кисть — <c>.brush.density.png</c> (слой 2), <c>.brush.velocity.png</c> (слои 0, 1 вокруг 0.5 в пределах ±BVC·VMXY; B — слой 6) и
/// <c>.brush.bin</c> (BrushN × BrushN × 8 half).
/// </summary>
public sealed class Fluid2DSnapshot(Fluid2DFields fields, Fluid2DStats stats, float velocityClamp, Fluid2DBrushTexel[]? brush = null,
    float brushVelocityClamp = 2)
{
    public Fluid2DFields Fields { get; } = fields;
    public Fluid2DStats Stats { get; } = stats;

    /// <summary>Грид кисти (слои PainterGrid) или null.</summary>
    public Fluid2DBrushTexel[]? Brush { get; } = brush;

    /// <summary>Файлы снимка: <paramref name="pathPrefix"/> + <c>.density.png</c>, <c>.velocity.png</c>, <c>.bin</c> (и файлы кисти, если она есть).</summary>
    public (string Density, string Velocity, string Raw) Save(string pathPrefix)
    {
        int n = Fields.N;
        var density = new byte[n * n * 3];
        var velocity = new byte[n * n * 3];
        float scale = velocityClamp > 0 ? 0.5f / velocityClamp : 0.5f;
        var c = Fields.Composite;
        for (int i = 0; i < n * n; i++) FillPixel(c, i, scale, density, velocity);
        string d = pathPrefix + ".density.png", v = pathPrefix + ".velocity.png", raw = pathPrefix + ".bin";
        string? dir = Path.GetDirectoryName(Path.GetFullPath(d));
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(d, Png.Encode(density, n, n));
        File.WriteAllBytes(v, Png.Encode(velocity, n, n));
        File.WriteAllBytes(raw, MemoryMarshal.AsBytes(c.AsSpan()).ToArray());
        if (Brush is not null) SaveBrush(pathPrefix);
        return (d, v, raw);
    }

    private void SaveBrush(string pathPrefix)
    {
        var b = Brush!;
        int n = (int)Math.Round(Math.Sqrt(b.Length));
        var density = new byte[b.Length * 3];
        var velocity = new byte[b.Length * 3];
        float scale = brushVelocityClamp > 0 ? 0.5f / brushVelocityClamp : 0.5f;
        for (int i = 0; i < b.Length; i++)
        {
            density[i * 3] = density[i * 3 + 1] = density[i * 3 + 2] = Byte((float)b[i].Density);
            velocity[i * 3] = Byte(0.5f + (float)b[i].VelocityX * scale);
            velocity[i * 3 + 1] = Byte(0.5f + (float)b[i].VelocityY * scale);
            velocity[i * 3 + 2] = Byte((float)b[i].BitmapDensity);
        }
        File.WriteAllBytes(pathPrefix + ".brush.density.png", Png.Encode(density, n, n));
        File.WriteAllBytes(pathPrefix + ".brush.velocity.png", Png.Encode(velocity, n, n));
        File.WriteAllBytes(pathPrefix + ".brush.bin", MemoryMarshal.AsBytes(b.AsSpan()).ToArray());
    }

    private static void FillPixel(Half[] c, int i, float scale, byte[] density, byte[] velocity)
    {
        float vx = (float)c[i * 4], vy = (float)c[i * 4 + 1], rho = (float)c[i * 4 + 2], w = (float)c[i * 4 + 3];
        byte r = Byte(rho);
        density[i * 3] = density[i * 3 + 1] = density[i * 3 + 2] = r;
        velocity[i * 3] = Byte(0.5f + vx * scale);
        velocity[i * 3 + 1] = Byte(0.5f + vy * scale);
        velocity[i * 3 + 2] = Byte(w);
    }

    private static byte Byte(float x) => float.IsFinite(x) ? (byte)Math.Clamp((int)MathF.Round(x * 255f), 0, 255) : (byte)255;
}
