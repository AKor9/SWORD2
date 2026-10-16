using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Bloom на устройстве (скрытое окно 512×512, отладочный слой с GPU-валидацией): ниже порога кадр тот же; гало симметрично,
/// его энергия линейна по интенсивности и в долях экрана не зависит от размера; NaN не разносится; 0 Б на кадр. Добавку
/// тест читает из снимка (<see cref="FrameCapture.BloomPath"/>); <c>SWORD_TEST_KEEP=1</c> — не удалять снимки.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class BloomGpuTests : IDisposable, ILogSink
{
    private const int Size = 512;
    private const string Shader = "graph_tests.hlsl";
    private static readonly GraphicsPipelineDesc Solid = new(Shader, "VsFull", "PsSolid");
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-bloom-{Guid.NewGuid():N}");

    public BloomGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests.Bloom", Size, Size, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, _window.Width, _window.Height, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract)
        {
            Hdr = true,
        };
        Directory.CreateDirectory(_dir);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
        if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") == "1") _output.WriteLine($"снимки: {_dir}");
        else if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    /// <summary>Прямоугольник [x0, x1) × [y0, y1) линейного цвета сцены.</summary>
    private readonly record struct Spot(int X0, int Y0, int X1, int Y1, Vector3 Color);

    /// <summary>Пятно стороной <paramref name="side"/> по центру кадра.</summary>
    private Spot Centre(int side, float value) =>
        new((_renderer.Width - side) / 2, (_renderer.Height - side) / 2, (_renderer.Width + side) / 2, (_renderer.Height + side) / 2, new(value));

    /// <summary>Сцена: очистка цветом <see cref="Renderer.SceneColorClear"/>, пятна — обрезкой прохода.</summary>
    private Action<RenderFrame> Scene(params Spot[] spots) => f =>
        f.Graph.AddPass("test.spots").Color(f.SceneColor, _renderer.SceneColorClear).Execute(ctx => DrawSpots(ctx, spots));

    private static void DrawSpots(RenderPassContext ctx, Spot[] spots)
    {
        ctx.SetPipeline(Solid);
        foreach (var s in spots)
        {
            ctx.SetScissor(s.X0, s.Y0, s.X1, s.Y1);
            ctx.SetConstant(0, s.Color.X);
            ctx.SetConstant(1, s.Color.Y);
            ctx.SetConstant(2, s.Color.Z);
            ctx.Draw(3);
        }
    }

    private void Frame(Action<RenderFrame> build, FrameCapture? capture = null)
    {
        _window.PumpMessages();
        _renderer.Render(build, vsync: false, capture);
    }

    /// <summary>Добавка bloom из снимка: яркость Rec.709 без экспозиции, строки сверху вниз, ½ экрана.</summary>
    private sealed record BloomMap(int Width, int Height, float[] Luminance, BloomCapture Capture)
    {
        public float At(int x, int y) => Luminance[y * Width + x];

        /// <summary>Билинейно в координатах текселей (центр текселя i — i + 0.5).</summary>
        public float Sample(float x, float y)
        {
            float fx = x - 0.5f, fy = y - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            float Get(int i, int j) => At(Math.Clamp(i, 0, Width - 1), Math.Clamp(j, 0, Height - 1));
            return (1 - ty) * ((1 - tx) * Get(x0, y0) + tx * Get(x0 + 1, y0)) + ty * ((1 - tx) * Get(x0, y0 + 1) + tx * Get(x0 + 1, y0 + 1));
        }

        public double Sum() => Luminance.Sum(v => (double)v);
    }

    /// <summary>Кадр со снимком: цвет (BMP → RGB по строкам сверху вниз) и добавка bloom, если он в кадре есть.</summary>
    private (byte[] Rgb, BloomMap? Bloom) Shot(Action<RenderFrame> build, string name)
    {
        string color = Path.Combine(_dir, name + ".bmp");
        bool bloom = _renderer.Hdr && _renderer.Bloom.Active;
        Frame(build, new FrameCapture(color)
        {
            HdrPath = _renderer.Hdr ? Path.Combine(_dir, name + ".hdr.png") : null,
            BloomPath = bloom ? Path.Combine(_dir, name + ".bloom.png") : null,
        });
        var file = File.ReadAllBytes(color);
        int w = _renderer.Width, h = _renderer.Height, stride = (w * 3 + 3) & ~3;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int src = 54 + (h - 1 - y) * stride + x * 3, dst = (y * w + x) * 3;
                (rgb[dst], rgb[dst + 1], rgb[dst + 2]) = (file[src + 2], file[src + 1], file[src]);
            }
        var capture = _renderer.LastCapture!.Hdr?.Bloom;
        Assert.Equal(bloom, capture is not null);
        if (capture is null) return (rgb, null);
        var raw = File.ReadAllBytes(capture.RawPath!);
        Assert.Equal(capture.Width * capture.Height * 8, raw.Length);
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(raw);
        var luminance = new float[capture.Width * capture.Height];
        for (int i = 0; i < luminance.Length; i++)
            luminance[i] = Vector3.Dot(new Vector3((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2]), BloomKernel.Luma);
        return (rgb, new BloomMap(capture.Width, capture.Height, luminance, capture));
    }

    [Fact]
    public void BelowThreshold_NoBloom_FrameAsWithoutBloom()
    {
        // Яркость × экспозиция ниже порога 1: белый 1 (0.99), красный 2 (яркость 0.43 — ярок один канал), тёплый, серый.
        var scene = Scene(new Spot(60, 60, 200, 200, new(1)), new Spot(300, 60, 450, 200, new(2, 0, 0)),
            new Spot(60, 300, 200, 450, new(0.9f, 0.5f, 0.2f)), new Spot(300, 300, 450, 450, new(0.18f)));
        var (on, bloom) = Shot(scene, "dim-on");
        Assert.Equal(0f, bloom!.Capture.MaxLuminance);
        Assert.Equal(BloomSettings.Default, bloom.Capture.Settings);
        Assert.Contains(_renderer.Graph.LastFrame, p => p.Name == "post.bloom.setup");

        _renderer.Bloom = BloomSettings.Off;
        var (off, none) = Shot(scene, "dim-off");
        Assert.Null(none);
        Assert.DoesNotContain(_renderer.Graph.LastFrame, p => p.Name.StartsWith("post.bloom", StringComparison.Ordinal));
        int worst = on.Zip(off, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(worst <= 1, $"кадр с bloom ниже порога отличается от кадра без него на {worst}/255");
        AssertNoDebugMessages();
    }

    [Fact]
    public void BrightSpot_HaloRadiallySymmetric_EnergyIsGainTimesSpot()
    {
        const int side = 8;
        const float value = 100;
        var (_, bloom) = Shot(Scene(Centre(side, value)), "spot");
        var map = bloom!;
        Assert.Equal((Size / 2, Size / 2), (map.Width, map.Height));
        Assert.Equal(0, map.Capture.NonFinite);

        // Энергия (в пикселях экрана: тексель ½ — 4 пикселя) = сумма усилений × энергия пятна; теряется только край широкой ступени.
        double energy = map.Sum() * 4, expected = BloomKernel.TotalGain(10) * value * side * side;
        _output.WriteLine($"энергия bloom {energy:0.#}, ожидалось {expected:0.#} (отношение {energy / expected:0.0000})");
        Assert.InRange(energy / expected, 0.97, 1.01);

        // Зеркала по x, по y и по диагонали: пятно — по центру (угол между текселями 127 и 128).
        float max = map.Luminance.Max();
        int n = map.Width;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float v = map.At(x, y), tolerance = 1e-4f * max + 0.01f * v;
                Assert.True(Math.Abs(v - map.At(n - 1 - x, y)) <= tolerance, $"({x}, {y}) и зеркало по x: {v} против {map.At(n - 1 - x, y)}");
                Assert.True(Math.Abs(v - map.At(x, n - 1 - y)) <= tolerance, $"({x}, {y}) и зеркало по y");
                Assert.True(Math.Abs(v - map.At(y, x)) <= tolerance, $"({x}, {y}) и зеркало по диагонали");
            }

        // По кругу: от 0° до 45° на радиусах 5…30 % ширины кадра — разброс мал. Ближе видна квадратная форма самого пятна, дальше —
        // край кадра: за ним чёрный, и точка на оси у края темнее точки на диагонали.
        float centre = n / 2f;
        var spreads = new List<(float Fraction, float Spread)>();
        foreach (float fraction in (float[])[0.05f, 0.1f, 0.2f, 0.3f])
        {
            float r = fraction * n;
            var ring = Enumerable.Range(0, 7).Select(k => map.Sample(centre + r * MathF.Cos(k * MathF.PI / 24), centre + r * MathF.Sin(k * MathF.PI / 24))).ToArray();
            float spread = (ring.Max() - ring.Min()) / ring.Average();
            spreads.Add((fraction, spread));
            _output.WriteLine($"радиус {fraction:P0} ширины: добавка {ring.Average():G4} (пик {max:G4}), разброс по кругу {spread:P1}");
        }
        Assert.All(spreads, s => Assert.True(s.Spread <= 0.05f, $"радиус {s.Fraction:P0}: разброс по кругу {s.Spread:P1}"));
        AssertNoDebugMessages();
    }

    [Fact]
    public void Intensity_Doubled_DoublesAddition()
    {
        var scene = Scene(Centre(8, 100));
        var (_, ten) = Shot(scene, "i10");
        _renderer.Bloom = BloomSettings.Default with { Intensity = 20 };
        var (_, twenty) = Shot(scene, "i20");
        Assert.Equal(20f, twenty!.Capture.Settings.Intensity);
        double ratio = twenty.Sum() / ten!.Sum();
        _output.WriteLine($"энергия при 20 / при 10 = {ratio:0.0000}; максимум {twenty.Capture.MaxLuminance / ten.Capture.MaxLuminance:0.0000}");
        Assert.InRange(ratio, 1.995, 2.005);
        Assert.InRange(twenty.Capture.MaxLuminance / ten.Capture.MaxLuminance, 1.99f, 2.01f);
        AssertNoDebugMessages();
    }

    [Fact]
    public void Resize_HaloSameInScreenFractions()
    {
        // Окно 1280×720 → 1920×1080, пятно — та же доля экрана (8 и 12 пикселей): добавка на тех же долях ширины от него — та же.
        // Ядра растут с экраном, а ящичные уменьшения цепочки размывают на постоянные пиксели (у тексела 1/64 — 64 пикселя), так что на
        // малых экранах гало чуть шире; с 1280 это уже доли.
        _renderer.Resize(1280, 720);
        var small = Shot(Scene(Centre(8, 100)), "s1280").Bloom!;
        _renderer.Resize(1920, 1080);
        var large = Shot(Scene(Centre(12, 100)), "s1920").Bloom!;
        Assert.Equal((960, 540), (large.Width, large.Height));
        var rows = new List<(float Fraction, float A, float B)>();
        foreach (float fraction in (float[])[0.03f, 0.05f, 0.1f, 0.2f, 0.3f, 0.45f])
        {
            float a = small.Sample(small.Width / 2f + fraction * small.Width, small.Height / 2f);
            float b = large.Sample(large.Width / 2f + fraction * large.Width, large.Height / 2f);
            rows.Add((fraction, a, b));
            _output.WriteLine($"{fraction:P0} ширины: 1280 — {a:G4}, 1920 — {b:G4} ({b / a - 1:+0.0%;-0.0%})");
        }
        Assert.All(rows, r => Assert.True(Math.Abs(r.B / r.A - 1) <= 0.05, $"{r.Fraction:P0} ширины: {r.A} при 1280 и {r.B} при 1920"));
        AssertNoDebugMessages();
    }

    [Fact]
    public void Passes_Toggles_OddSize_SteadyFrameAllocatesNothing()
    {
        var scene = Scene(Centre(8, 100));
        Frame(scene);   // первый кадр — сразу с bloom
        string[] expected =
        [
            "test.spots", "post.bloom.setup", "post.bloom.d4", "post.bloom.d8", "post.bloom.d16", "post.bloom.d32", "post.bloom.d64",
            "post.bloom.x64", "post.bloom.y64", "post.bloom.x32", "post.bloom.y32", "post.bloom.x16", "post.bloom.y16",
            "post.bloom.x8", "post.bloom.y8", "post.bloom.x4", "post.bloom.y4", "post.bloom.x2", "post.bloom.y2", "post.tonemap",
        ];
        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(expected, passes.Select(p => p.Name));
        Assert.All(passes.Skip(1), p => Assert.Equal(FramePhase.Post, p.Phase));
        Assert.Equal(["scene.color", "bloom.y2"], passes[^1].Reads);
        Assert.Equal(["bloom.x4", "bloom.y8"], passes.Single(p => p.Name == "post.bloom.y4").Reads);

        static bool NoBloom(IReadOnlyList<RenderPassReport> frame) => !frame.Any(p => p.Name.StartsWith("post.bloom", StringComparison.Ordinal));
        _renderer.Bloom = BloomSettings.Off;
        Frame(scene);
        Assert.True(NoBloom(_renderer.Graph.LastFrame));
        Assert.Equal(["scene.color"], _renderer.Graph.LastFrame[^1].Reads);
        _renderer.Bloom = BloomSettings.Default with { Intensity = 0 };
        Frame(scene);
        Assert.True(NoBloom(_renderer.Graph.LastFrame));

        // HDR выключен — ни bloom, ни тонмапа; заказ карты bloom — заметка, а не ошибка.
        _renderer.Bloom = BloomSettings.Default;
        _renderer.Hdr = false;
        Frame(f => f.Graph.AddPass("test.ldr").Color(f.Graph.BackBuffer, Vector4.Zero),
            new FrameCapture(Path.Combine(_dir, "ldr.png")) { BloomPath = Path.Combine(_dir, "ldr.bloom.png") });
        Assert.Equal(["test.ldr"], _renderer.Graph.LastFrame.Select(p => p.Name));
        Assert.Contains(_renderer.LastCapture!.Notes, n => n.Contains("bloom"));
        Assert.False(File.Exists(Path.Combine(_dir, "ldr.bloom.png")));

        // Нечётный размер: уровни с округлением вверх, 301×201 → 151×101 … 5×4.
        _renderer.Hdr = true;
        _renderer.Resize(301, 201);
        var (_, odd) = Shot(Scene(new Spot(140, 90, 160, 110, new(50))), "odd");
        Assert.Equal((151, 101), (odd!.Width, odd.Height));
        Assert.True(odd.Capture.MaxLuminance > 0);
        Assert.Equal(0, odd.Capture.NonFinite);

        // Установившийся кадр с bloom — без выделений.
        var spots = new[] { new Spot(140, 90, 160, 110, new(50)) };
        Action<RenderPassContext> draw = ctx => DrawSpots(ctx, spots);
        Action<RenderFrame> steady = f => f.Graph.AddPass("test.spots").Color(f.SceneColor, _renderer.SceneColorClear).Execute(draw);
        for (int i = 0; i < 30; i++) Frame(steady);
        const int frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _renderer.Render(steady, vsync: false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр с bloom выделил {bytes} Б");
        Assert.Equal(expected.Length, _renderer.Graph.LastFramePasses);
        AssertNoDebugMessages();
    }

    [Fact]
    public void NonFiniteScene_BloomStaysFinite_NaNDoesNotGlow()
    {
        var nan = new Spot(100, 100, 120, 120, new(float.NaN));
        var (_, fromNaN) = Shot(Scene(nan), "nan");
        Assert.Equal(0f, fromNaN!.Capture.MaxLuminance);
        Assert.Equal(0, fromNaN.Capture.NonFinite);
        Assert.True(_renderer.LastCapture!.Hdr!.NonFinite > 0);

        // Бесконечность — предел half (белая, как у тонмапа) и светится, но добавка конечна, в том числе на весь кадр.
        var (_, fromInf) = Shot(Scene(nan, new Spot(300, 300, 340, 340, new(float.PositiveInfinity))), "inf");
        Assert.True(fromInf!.Capture.MaxLuminance > 0);
        Assert.Equal(0, fromInf.Capture.NonFinite);
        var (_, all) = Shot(Scene(new Spot(0, 0, Size, Size, new(float.PositiveInfinity))), "inf-all");
        Assert.Equal(0, all!.Capture.NonFinite);
        AssertNoDebugMessages();
    }

    [Fact]
    public void SpotsOfRisingBrightness_HaloGrowsWithBrightness()
    {
        // Серый фон ниже порога и четыре пятна 6×6 (огонь в HDR — порядка 10): чем ярче, тем больше и шире гало; угол кадра — почти без него.
        _renderer.SceneColorClear = new Vector4(0.18f, 0.18f, 0.18f, 1);
        float[] values = [2, 10, 100, 1000];
        var spots = values.Select((v, i) => new Spot(61 + i * 120, 253, 67 + i * 120, 259, new(v, 0.6f * v, 0.2f * v))).ToArray();
        var (_, bloom) = Shot(Scene(spots), "spots");
        var map = bloom!;
        float previous = 0;
        for (int i = 0; i < spots.Length; i++)
        {
            // Центр пятна — в текселях ½ экрана; 10 % ширины кадра выше него.
            float cx = (spots[i].X0 + spots[i].X1) / 4f, cy = (spots[i].Y0 + spots[i].Y1) / 4f;
            float near = map.Sample(cx, cy - 0.1f * map.Height);
            _output.WriteLine($"пятно {values[i]}: добавка в центре {map.Sample(cx, cy):G4}, на 10 % ширины выше {near:G4}");
            Assert.True(near > previous, $"пятно {values[i]}: гало {near} не больше, чем у более тусклого ({previous})");
            previous = near;
        }
        float corner = map.At(2, 2);
        _output.WriteLine($"угол кадра: {corner:G4}, максимум {map.Capture.MaxLuminance / _renderer.Exposure:G4}");
        Assert.True(corner < 0.05f, $"в углу кадра добавка {corner}");
        AssertNoDebugMessages();
    }
}
