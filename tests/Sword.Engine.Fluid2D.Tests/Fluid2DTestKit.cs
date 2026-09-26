using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>Поля, шумы, параметры и сверка half для тестов сима.</summary>
internal static class Fluid2DTestKit
{
    /// <summary>Вход шага «всё сразу»: дробный сдвиг площадки, мировой сдвиг шумов, поворот, инерция.</summary>
    public static readonly Fluid2DStepInput Input = new(1234, new Vector2(3.7f, -1.2f), new Vector2(0.013f, -0.0071f),
        Quaternion.CreateFromYawPitchRoll(0.4f, -0.7f, 0.2f)) { VelocityOffsetDelta = new Vector2(0.01f, 0.02f) };

    /// <summary>Факел на 64², шумы включены.</summary>
    public static Fluid2DParams Torch(int n = 64) => Fluid2DParams.Torch with { Resolution = n };

    /// <summary>Все режимы, которых нет у факела: ластик, инерция, сдвиги шумов, маски, ядро 7 со сдвигом рядов, грубый решатель, контраст, инверсия, дробный SimSpeed.</summary>
    public static Fluid2DParams Everything(int n = 64) => Fluid2DParams.Torch with
    {
        Resolution = n,
        SimSpeed = 1.5f,
        EraserStrength = 0.3f,
        VeloFromSimAreaMotion = 0.5f,
        RandomizeNoiseOffsets = 0.37f,
        MaskDirNoiseWithSimVelocity = 0.5f,
        WeakenCollisionMask = 0.2f,
        KernelMult = 0.6f,
        KernelIndexOffset = -1,
        PressureEdgeMasking = true,
        CrudePressureSolver = true,
        FluidDensityContrast = 1.7f,
        InvertFluidDensity = true,
        DirectVelocityInWorldSpace = false,
        DensityNoiseTile = 2,
        VeloAmpNoiseSize = 1.3f,
        DensityFloor = 1e-3f,
        PressureFeedback = 0.9f,
        DivergenceFeedback = 0.8f,
    };

    /// <summary>Тихий режим: ни шумов, ни постоянного ввода, пол плотности 0 — поле меняют только адвекция, давление и затухание.</summary>
    public static Fluid2DParams Quiet(int n = 64) => Fluid2DParams.Torch with
    {
        Resolution = n,
        VeloDirNoise = 0,
        DirectVelocity = Vector3.Zero,
        VeloAmplitudeNoiseEnabled = false,
        DensityNoiseEnabled = false,
        DensityFloor = 0,
    };

    public static byte[] NoiseRgba(int size, int seed)
    {
        var rng = new Random(seed);
        var rgba = new byte[size * size * 4];
        rng.NextBytes(rgba);
        return rgba;
    }

    /// <summary>Случайные поля: v в ±2, ρ в [0, 1], wet в [0, 2], P в ±1, D в ±2.</summary>
    public static Fluid2DFields RandomFields(int n, int seed)
    {
        var rng = new Random(seed);
        var f = new Fluid2DFields(n);
        FillVelocityDensity(f.Composite, rng);
        FillVelocityDensity(f.Advected, rng);
        FillPressure(f.Pressure, rng);
        FillPressure(f.Scratch, rng);
        return f;
    }

    private static void FillVelocityDensity(Half[] g, Random rng)
    {
        for (int i = 0; i < g.Length; i += 4)
        {
            g[i] = (Half)(rng.NextSingle() * 4 - 2);
            g[i + 1] = (Half)(rng.NextSingle() * 4 - 2);
            g[i + 2] = (Half)rng.NextSingle();
            g[i + 3] = (Half)(rng.NextSingle() * 2);
        }
    }

    private static void FillPressure(Half[] g, Random rng)
    {
        for (int i = 0; i < g.Length; i += 2)
        {
            g[i] = (Half)(rng.NextSingle() * 2 - 1);
            g[i + 1] = (Half)(rng.NextSingle() * 4 - 2);
        }
    }

    /// <summary>Случайная кисть: скорость ±0.05 (|bv| до 2 — обе ветви «глушения» ввода), плотность [0, 1], битмап.</summary>
    public static Fluid2DBrushTexel[] RandomBrush(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new Fluid2DBrushTexel[n * n];
        for (int i = 0; i < b.Length; i++)
            b[i] = new Fluid2DBrushTexel(rng.NextSingle() * 0.1f - 0.05f, rng.NextSingle() * 0.1f - 0.05f, rng.NextSingle(),
                rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle());
        return b;
    }

    /// <summary>Гладкие поля: капля плотности, вихрь и мокрая карта — как у горящего факела.</summary>
    public static Fluid2DFields SmoothFields(int n)
    {
        var f = new Fluid2DFields(n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.45f;
                float g = MathF.Exp(-(u * u + v * v) / 0.01f);
                int i = (y * n + x) * 4;
                f.Advected[i] = (Half)(-v * 3 * g);
                f.Advected[i + 1] = (Half)(u * 3 * g - 0.3f * g);
                f.Advected[i + 2] = (Half)g;
                f.Advected[i + 3] = (Half)(0.5f * g);
                f.Composite[i] = f.Advected[i];
                f.Composite[i + 1] = f.Advected[i + 1];
                f.Composite[i + 2] = f.Advected[i + 2];
                f.Composite[i + 3] = f.Advected[i + 3];
            }
        return f;
    }

    /// <summary>Капля плотности в покое с центром (<paramref name="cx"/>, <paramref name="cy"/>) UV и радиусом <paramref name="r"/> UV — в A.</summary>
    public static Fluid2DFields Drop(int n, float cx, float cy, float r)
    {
        var f = new Fluid2DFields(n);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - cx, v = (y + 0.5f) / n - cy;
                f.Advected[(y * n + x) * 4 + 2] = (Half)MathF.Exp(-(u * u + v * v) / (r * r));
            }
        return f;
    }

    public static float Sum(Half[] g, int channel, int channels)
    {
        double s = 0;
        for (int i = channel; i < g.Length; i += channels) s += (float)g[i];
        return (float)s;
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
        Fluid2DSim.Contribute(contract);
        contract.AddSlot("gTestIn", 0).AddSlot("gTestOut", 1).AddSlot("gTestSize", 2).AddSlot("gTestLayer", 3);
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

    /// <summary>
    /// Шумы сима: случайная картинка sRGB на GPU (<see cref="GpuImage"/>) и для эталона — значения, прочитанные с GPU (декод sRGB железом
    /// не совпадает с формулой бит в бит, <see cref="SrgbDecodeError"/>; сверяется арифметика сима, а не декод текстур — его держат тесты Render).
    /// </summary>
    public (Fluid2DNoise Gpu, Fluid2DNoiseImage Cpu, GpuImage Image) Noise(string name, int size, int seed)
    {
        var rgba = Fluid2DTestKit.NoiseRgba(size, seed);
        var image = Renderer.CreateImage(name, size, size, rgba, mips: false, srgb: true);
        var noise = Fluid2DNoise.FromImage(image);
        var values = ReadNoise(noise).Values;
        var exact = Fluid2DNoiseImage.FromRgba8(rgba, size, size, srgb: true);
        for (int i = 0; i < values.Length; i++) SrgbDecodeError = Math.Max(SrgbDecodeError, Math.Abs(values[i] - exact.Values[i]));
        return (noise, new Fluid2DNoiseImage(values, size, size), image);
    }

    /// <summary>Шум, как его читает сим (<c>Load</c> слоя, мип 0, канал r; sRGB декодирует железо), — картинкой для эталона.</summary>
    public Fluid2DNoiseImage ReadNoise(Fluid2DNoise noise)
    {
        Assert.Equal(noise.Width, noise.Height);
        var values = new float[noise.Width * noise.Height];
        using (var storage = Renderer.CreateStorage<float>("test.noise", (uint)values.Length))
        {
            Frame(f =>
            {
                var g = f.Graph;
                var s = g.Import(storage);
                g.AddComputePass("test.noise", FramePhase.Simulation).Write(s).Execute(ctx =>
                {
                    ctx.SetPipeline(new ComputePipelineDesc("fluid2d_tests.hlsl", "CsReadNoise"));
                    ctx.SetConstant(0, noise.Srv);
                    ctx.SetConstant(1, g.UavOf(s));
                    ctx.SetConstant(2, noise.Width);
                    ctx.SetConstant(3, noise.Layer);
                    ctx.Dispatch((uint)(noise.Width + 7) / 8, (uint)(noise.Height + 7) / 8);
                });
            });
            Renderer.ReadStorage(storage, values.AsSpan());
        }
        return new Fluid2DNoiseImage(values, noise.Width, noise.Height);
    }

    /// <summary>Картинка кисти (шум RG или битмап RGB) из случайных байтов, линейная: GPU и значения, прочитанные с GPU, для эталона.</summary>
    public (Fluid2DNoise Gpu, Fluid2DImage Cpu, GpuImage Image) Image(string name, int size, int seed)
    {
        var image = Renderer.CreateImage(name, size, size, Fluid2DTestKit.NoiseRgba(size, seed), mips: false, srgb: false);
        var handle = Fluid2DNoise.FromImage(image);
        return (handle, ReadImage(handle), image);
    }

    /// <summary>Все каналы слоя (Load, мип 0) — картинкой для эталона Painter.</summary>
    public Fluid2DImage ReadImage(Fluid2DNoise noise)
    {
        Assert.Equal(noise.Width, noise.Height);
        var values = new float[4 * noise.Width * noise.Height];
        using (var storage = Renderer.CreateStorage<System.Numerics.Vector4>("test.image", (uint)(noise.Width * noise.Height)))
        {
            Frame(f =>
            {
                var g = f.Graph;
                var s = g.Import(storage);
                g.AddComputePass("test.image", FramePhase.Simulation).Write(s).Execute(ctx =>
                {
                    ctx.SetPipeline(new ComputePipelineDesc("fluid2d_tests.hlsl", "CsReadRgba"));
                    ctx.SetConstant(0, noise.Srv);
                    ctx.SetConstant(1, g.UavOf(s));
                    ctx.SetConstant(2, noise.Width);
                    ctx.SetConstant(3, noise.Layer);
                    ctx.Dispatch((uint)(noise.Width + 7) / 8, (uint)(noise.Height + 7) / 8);
                });
            });
            Renderer.ReadStorage(storage, System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector4>(values.AsSpan()));
        }
        return new Fluid2DImage(values, noise.Width, noise.Height);
    }

    /// <summary>Наибольшее расхождение декода sRGB железом с формулой IEC 61966-2-1 на прочитанных шумах.</summary>
    public double SrgbDecodeError { get; private set; }

    /// <summary>Текстура RGBA16F кадра → half на CPU: копия проходом <c>CsCopyTexture</c> в хранилище и чтение между кадрами.</summary>
    public Half[] ReadExport(Fluid2DSim sim)
    {
        int n = sim.Resolution;
        using var storage = Renderer.CreateStorage<ulong>("test.export", (uint)(n * n));
        Frame(f =>
        {
            var g = f.Graph;
            var rt = sim.Export(f);
            var s = g.Import(storage);
            g.AddComputePass("test.copy", FramePhase.Simulation).Read(rt).Write(s).Execute(ctx =>
            {
                ctx.SetPipeline(new ComputePipelineDesc("fluid2d_tests.hlsl", "CsCopyTexture"));
                ctx.SetConstant(0, g.SrvOf(rt));
                ctx.SetConstant(1, g.UavOf(s));
                ctx.SetConstant(2, n);
                ctx.Dispatch((uint)(n + 7) / 8, (uint)(n + 7) / 8);
            });
        });
        var result = new Half[4 * n * n];
        Renderer.ReadStorage(storage, MemoryMarshal.Cast<Half, ulong>(result.AsSpan()));
        return result;
    }

    public void Dispose()
    {
        Renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
    }
}
