using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;

namespace Sword.Engine.Fluid3D.Tests;

/// <summary>Поля, параметры и сверка half для тестов газа.</summary>
internal static class Fluid3DTestKit
{
    /// <summary>Малая сетка: все силы, MacCormack, тёплый старт, предел скорости 3 м/с (случайные поля его превышают).</summary>
    public static Fluid3DParams Small(int nx, int ny, int nz) => Fluid3DParams.Smoke with { Nx = nx, Ny = ny, Nz = nz, MaxSpeed = 3f };

    /// <summary>Тихий газ: без сил и затухания — поля меняют только адвекция и давление.</summary>
    public static Fluid3DParams Quiet(int nx, int ny, int nz) => Small(nx, ny, nz) with
    {
        DensityBuoyancy = 0,
        TemperatureBuoyancy = 0,
        VorticityConfinement = 0,
        DensityDecay = 0,
        TemperatureDecay = 0,
        TemperatureDissipation = 0,
    };

    /// <summary>Окно с нижним углом у (5, −3, 7) ячеек и целью у его центра.</summary>
    public static Fluid3DWindow WindowAt(Fluid3DParams p, int x = 5, int y = -3, int z = 7)
    {
        var w = new Fluid3DWindow(x, y, z, p.Nx, p.Ny, p.Nz, p.CellSize, default);
        return w with { Target = w.Center };
    }

    /// <summary>Случайные поля: v в ±2 м/с, ρ в [0, 1], T в [0, 2], p в ±1, t в ±2 — во всех буферах.</summary>
    public static Fluid3DFields RandomFields(int nx, int ny, int nz, int seed)
    {
        var rng = new Random(seed);
        var f = new Fluid3DFields(nx, ny, nz);
        FillVelocity(f.Velocity, rng);
        FillVelocity(f.VelocityScratch, rng);
        FillScalars(f.Scalars, rng);
        FillScalars(f.ScalarScratch, rng);
        FillScalars(f.ScalarForward, rng);
        FillAux(f.Aux, rng);
        FillAux(f.AuxPrevious, rng);
        return f;
    }

    private static void FillVelocity(Half[] v, Random rng)
    {
        for (int i = 0; i < v.Length; i += 4)
        {
            v[i] = (Half)(rng.NextSingle() * 4 - 2);
            v[i + 1] = (Half)(rng.NextSingle() * 4 - 2);
            v[i + 2] = (Half)(rng.NextSingle() * 4 - 2);
        }
    }

    private static void FillScalars(Half[] s, Random rng)
    {
        for (int i = 0; i < s.Length; i += 2)
        {
            s[i] = (Half)rng.NextSingle();
            s[i + 1] = (Half)(rng.NextSingle() * 2);
        }
    }

    private static void FillAux(Half[] a, Random rng)
    {
        for (int i = 0; i < a.Length; i += 2)
        {
            a[i] = (Half)(rng.NextSingle() * 2 - 1);
            a[i + 1] = (Half)(rng.NextSingle() * 4 - 2);
        }
    }

    /// <summary>Гладкий шар (гаусс) ρ и T в S с центром <paramref name="c"/> (индексы) и радиусом <paramref name="r"/> ячеек; скорость — 0.</summary>
    public static Fluid3DFields Blob(Fluid3DParams p, Vector3 c, float r, float density, float temperature)
    {
        var f = new Fluid3DFields(p.Nx, p.Ny, p.Nz);
        for (int z = 0; z < p.Nz; z++)
            for (int y = 0; y < p.Ny; y++)
                for (int x = 0; x < p.Nx; x++)
                {
                    float d2 = (new Vector3(x, y, z) - c).LengthSquared();
                    float g = MathF.Exp(-d2 / (r * r));
                    if (g < 1e-4f) continue;
                    int i = f.Index(x, y, z);
                    f.Scalars[2 * i] = (Half)(density * g);
                    f.Scalars[2 * i + 1] = (Half)(temperature * g);
                }
        return f;
    }

    /// <summary>Вихревое кольцо и струя: гладкая скорость с завихрённостью и дивергенцией.</summary>
    public static Fluid3DFields Swirl(Fluid3DParams p)
    {
        var f = new Fluid3DFields(p.Nx, p.Ny, p.Nz);
        var c = new Vector3(p.Nx, p.Ny, p.Nz) * 0.5f;
        float r = MathF.Min(p.Nx, MathF.Min(p.Ny, p.Nz)) * 0.25f;
        for (int z = 0; z < p.Nz; z++)
            for (int y = 0; y < p.Ny; y++)
                for (int x = 0; x < p.Nx; x++)
                {
                    var d = new Vector3(x, y, z) - c;
                    float g = MathF.Exp(-d.LengthSquared() / (r * r));
                    var v = new Vector3(-d.Z, 0.3f * r, d.X) * (g / r) + new Vector3(0.4f * MathF.Sin(0.7f * y), 0, 0.3f * MathF.Cos(0.5f * x)) * g;
                    int i = f.Index(x, y, z);
                    f.Velocity[4 * i] = (Half)v.X;
                    f.Velocity[4 * i + 1] = (Half)v.Y;
                    f.Velocity[4 * i + 2] = (Half)v.Z;
                    f.Scalars[2 * i] = (Half)g;
                    f.Scalars[2 * i + 1] = (Half)(0.5f * g);
                }
        return f;
    }

    /// <summary>ULP half в точке x (у субнормальных — 2⁻²⁴).</summary>
    public static double HalfUlp(double x)
    {
        x = Math.Abs(x);
        return x < Math.Pow(2, -14) ? Math.Pow(2, -24) : Math.Pow(2, Math.Floor(Math.Log2(x)) - 10);
    }

    /// <summary>Наибольшее расхождение: в ULP half (по большему из пары) и абсолютное.</summary>
    public static (double Ulps, double Abs, int Index) Compare(ReadOnlySpan<Half> gpu, ReadOnlySpan<Half> cpu)
    {
        Assert.Equal(cpu.Length, gpu.Length);
        double ulps = 0, abs = 0;
        int worst = -1;
        for (int i = 0; i < cpu.Length; i++)
        {
            double g = (double)gpu[i], c = (double)cpu[i];
            if (double.IsNaN(g) || double.IsNaN(c))
            {
                if (double.IsNaN(g) != double.IsNaN(c)) return (double.PositiveInfinity, double.PositiveInfinity, i);
                continue;
            }
            double d = Math.Abs(g - c);
            double u = d / HalfUlp(Math.Max(Math.Abs(g), Math.Abs(c)));
            if (u > ulps) (ulps, worst) = (u, i);
            abs = Math.Max(abs, d);
        }
        return (ulps, abs, worst);
    }

    /// <summary>Биты half равны.</summary>
    public static bool SameBits(ReadOnlySpan<Half> a, ReadOnlySpan<Half> b) =>
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(a).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b));

    /// <summary>Энстрофия Σ|ω|² скорости состояния.</summary>
    public static double Enstrophy(Fluid3DFields f, float cellSize) => Fluid3DStats.Compute(f, new Fluid3DWindow(0, 0, 0, f.Nx, f.Ny, f.Nz, cellSize, default), 0).Enstrophy;
}

/// <summary>Настоящее устройство D3D12 (скрытое окно 64², отладочный слой, GPU-валидация); любое предупреждение D3D12 — провал.</summary>
internal sealed class GpuDevice : IDisposable, ILogSink
{
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];

    /// <param name="validation">false — без слоя отладки и GPU-валидации: замер GPU-времени (валидация инструментирует шейдеры).</param>
    public GpuDevice(string name, bool validation = true)
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions(name, 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        Fluid3DSim.Contribute(contract);
        Renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: validation,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: validation), contract);
    }

    public Renderer Renderer { get; }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Frame(Action<RenderFrame> build)
    {
        _window.PumpMessages();
        Renderer.Render(build, vsync: false);
    }

    public void AssertNoDebugMessages() => Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    public void Dispose()
    {
        Renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }
}
