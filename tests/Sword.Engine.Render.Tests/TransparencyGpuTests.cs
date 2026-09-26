using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Xunit.Abstractions;

namespace Sword.Engine.Render.Tests;

/// <summary>
/// Прозрачная фаза на настоящем устройстве (скрытое окно нечётного размера, отладочный слой D3D12 с GPU-валидацией): премультиплицированное
/// смешивание поверх HDR-сцены по формуле, тест по глубине сцены (привязана на чтение и читается текстурой в том же проходе), мягкий
/// край против CPU-эталона <see cref="TransparencyMath"/>, слой ½ (<see cref="HalfResTransparency"/>) — апсемпл без ореолов у краёв
/// глубины и билинейный на ровном, нечётные размеры и смена размера, 0 Б на кадр. Значения — сырой half цели сцены.
/// </summary>
[Collection("gpu")]
[Trait("Category", "Gpu")]
public sealed class TransparencyGpuTests : IDisposable, ILogSink
{
    private const string Shader = "transparent_tests.hlsl";
    private const float Near = 0.1f;
    private static readonly Vector2 K = new(0, Near);   // обратный Z, бесконечная дальняя плоскость (как камера арены)
    private static readonly GraphicsPipelineDesc Ramp = new(Shader, "VsQuad", "PsDepthRamp", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Rects = new(Shader, "VsQuad", "PsDepthRects", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc Premul = new(Shader, "VsQuad", "PsPremul", BlendMode.Premultiplied);
    private static readonly GraphicsPipelineDesc PremulTested = Premul with { Depth = DepthMode.Test };
    private static readonly GraphicsPipelineDesc Fade = new(Shader, "VsQuad", "PsFadeQuad", BlendMode.Premultiplied, DepthMode.Test);
    private static readonly GraphicsPipelineDesc HalfLayer = new(Shader, "VsQuad", "PsHalfLayer", BlendMode.Premultiplied);

    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly List<string> _d3d12 = [];
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-transparent-{Guid.NewGuid():N}");

    public TransparencyGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 67, 41, Activate: false));
        _renderer = Create(_window, validation: true);
    }

    private static Renderer Create(Win32Window window, bool validation)
    {
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        return new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height, DebugLayer: validation,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: validation), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
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
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    private void AssertNoDebugMessages() =>
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));

    private int W => _renderer.Width;
    private int H => _renderer.Height;

    /// <summary>Кадр со снимком HDR; вернёт сырой цвет сцены (RGBA на пиксель, строки сверху вниз).</summary>
    private Vector4[] Shot(Action<RenderFrame> build, string name, bool transparent = false)
    {
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, name + ".hdr.png");
        _window.PumpMessages();
        _renderer.Render(build, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png"))
        {
            HdrPath = hdr,
            TransparentPath = transparent ? Path.Combine(_dir, name + ".transparent.png") : null,
        });
        return ReadHalf(Path.ChangeExtension(hdr, ".bin"));
    }

    private static Vector4[] ReadHalf(string path)
    {
        var halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(path));
        var rgba = new Vector4[halves.Length / 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = new((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2], (float)halves[i * 4 + 3]);
        return rgba;
    }

    private static float Round(float v) => (float)(Half)v;

    /// <summary>Линейная глубина столбца x сцены-рампы — как в <c>PsDepthRamp</c>.</summary>
    private float RampDepth(int x, float z0, float z1) => z0 + (z1 - z0) * (x + 0.5f) / W;

    private void SceneRamp(RenderFrame f, Vector3 colour, float z0, float z1) =>
        f.Graph.AddPass("test.scene").Color(f.SceneColor, _renderer.SceneColorClear).Depth(f.Scene.Depth, DepthAccess.Write, 0).Execute(ctx =>
        {
            ctx.SetPipeline(Ramp);
            ctx.SetConstant(1, colour.X);
            ctx.SetConstant(2, colour.Y);
            ctx.SetConstant(3, colour.Z);
            ctx.SetConstant(4, Near);
            ctx.SetConstant(5, z0);
            ctx.SetConstant(6, z1);
            ctx.SetConstant(7, ctx.Width);
            ctx.Draw(3);
        });

    private static void SetColour(RenderPassContext ctx, Vector4 premul)
    {
        ctx.SetConstant(1, premul.X);
        ctx.SetConstant(2, premul.Y);
        ctx.SetConstant(3, premul.Z);
        ctx.SetConstant(4, premul.W);
    }

    private static void AssertClose(Vector4 expected, Vector4 got, float rel, string what)
    {
        for (int c = 0; c < 3; c++)
            Assert.True(MathF.Abs(got[c] - expected[c]) <= rel * MathF.Abs(expected[c]) + 1e-3f, $"{what}: канал {c}: GPU {got[c]}, ожидалось {expected[c]}");
    }

    [Fact]
    public void Premultiplied_OverHdrScene_IsSrcPlusDstTimesOneMinusAlpha()
    {
        var dst = new Vector3(2, 0.5f, 0.125f);
        // Три полосы строк: полупрозрачное, ничего (альфа 0, цвет 0), непрозрачное HDR (замена).
        Vector4[] src = [new(0.3f, 0.2f, 0.1f, 0.25f), Vector4.Zero, new(1.5f, 6f, 0, 1)];
        int band = H / src.Length;
        var gpu = Shot(f =>
        {
            SceneRamp(f, dst, 4, 12);
            f.Graph.AddPass("test.premul", FramePhase.Transparent).Color(f.SceneColor).Execute(ctx =>
            {
                ctx.SetPipeline(Premul);
                for (int i = 0; i < src.Length; i++)
                {
                    ctx.SetScissor(0, i * band, ctx.Width, (i + 1) * band);
                    SetColour(ctx, src[i]);
                    ctx.Draw(3);
                }
            });
        }, "premul");
        for (int i = 0; i < src.Length; i++)
        {
            var s = src[i];
            var e = new Vector3(s.X, s.Y, s.Z) + dst * (1 - s.W);
            for (int x = 0; x < W; x += 7)
                AssertClose(new Vector4(Round(e.X), Round(e.Y), Round(e.Z), 1), gpu[(i * band + band / 2) * W + x], 2e-3f, $"полоса {i} x {x}");
        }
        Assert.Equal(FramePhase.Transparent, _renderer.Graph.LastFrame.Single(p => p.Name == "test.premul").Phase);
        AssertNoDebugMessages();
    }

    [Fact]
    public void DepthRead_BehindWallHidden_SoftEdgeLinearInViewDepth()
    {
        // Сцена — стена с глубиной 4…12 м слева направо; квад на 8 м: верхняя половина — жёсткий (только тест глубины), нижняя —
        // мягкий край 2 м. Оба прохода держат глубину на чтение и читают её текстурой.
        const float quad = 8, fadeDistance = 2;
        var colour = new Vector4(1, 0.5f, 0.25f, 1) * 0.8f;
        int srv = 0;
        var gpu = Shot(f =>
        {
            SceneRamp(f, Vector3.Zero, 4, 12);
            srv = f.Scene.DepthSrv;
            var depth = f.Scene.Depth;
            f.Graph.AddPass("test.hard", FramePhase.Transparent).Color(f.SceneColor).Depth(depth, DepthAccess.Read).Read(depth).Execute(ctx =>
            {
                ctx.SetPipeline(PremulTested);
                ctx.SetScissor(0, 0, ctx.Width, ctx.Height / 2);
                ctx.SetConstant(0, Near / quad);
                SetColour(ctx, colour);
                ctx.Draw(3);
            });
            f.Graph.AddPass("test.soft", FramePhase.Transparent).Color(f.SceneColor).Depth(depth, DepthAccess.Read).Read(depth).Execute(ctx =>
            {
                ctx.SetPipeline(Fade);
                ctx.SetScissor(0, ctx.Height / 2, ctx.Width, ctx.Height);
                ctx.SetConstant(0, Near / quad);
                SetColour(ctx, colour);
                ctx.SetConstant(5, fadeDistance);
                ctx.SetConstant(6, K.X);
                ctx.SetConstant(7, K.Y);
                ctx.SetConstant(8, srv);
                ctx.Draw(3);
            });
        }, "fade");

        int hidden = 0, ramp = 0;
        for (int x = 0; x < W; x++)
        {
            float z = RampDepth(x, 4, 12);
            // Жёсткий: за стеной (стена ближе квада) — ничего; перед — весь цвет.
            var hard = gpu[(H / 4) * W + x];
            if (z < quad - 0.1f)
            {
                hidden++;
                Assert.True(hard.X == 0 && hard.Y == 0 && hard.Z == 0, $"x {x}: квад за стеной ({z} м) виден: {hard}");
            }
            else if (z > quad + 0.1f) AssertClose(colour, hard, 2e-3f, $"жёсткий x {x}");
            // Мягкий: эталон по линейным глубинам сцены и квада.
            float fade = TransparencyMath.DepthFade(TransparencyMath.ViewDepth(Near / z, K), TransparencyMath.ViewDepth(Near / quad, K), fadeDistance);
            if (fade is > 0 and < 1) ramp++;
            AssertClose(colour * fade, gpu[(3 * H / 4) * W + x], 3e-3f, $"мягкий x {x} (z {z}, fade {fade})");
        }
        Assert.True(hidden > 20 && ramp > 10, $"за стеной {hidden} столбцов, на склоне края {ramp}");
        // Глубина — DEPTH_READ | SRV с первого прозрачного прохода: второму переходы не нужны (GPU-валидация проверила состояния).
        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(["test.scene", "test.hard", "test.soft", "post.tonemap"], passes.Select(p => p.Name));
        Assert.True(passes[1].Barriers >= 1);
        Assert.Equal(0, passes[2].Barriers);
        Assert.Equal(["scene.depth"], passes[2].Reads);
        AssertNoDebugMessages();
    }

    [Fact]
    public void ViewDepthParams_FiniteAndInfiniteProjections()
    {
        // Бесконечная (как RtsCamera) и конечная проекция обратного Z: расстояние вдоль оси камеры восстанавливается из z/w.
        var infinite = new Matrix4x4(1.2f, 0, 0, 0, 0, 1.7f, 0, 0, 0, 0, 0, -1, 0, 0, Near, 0);
        float n = 0.5f, far = 200;
        var finite = new Matrix4x4(1.2f, 0, 0, 0, 0, 1.7f, 0, 0, 0, 0, n / (far - n), -1, 0, 0, far * n / (far - n), 0);
        foreach (var (proj, name) in new[] { (infinite, "бесконечная"), (finite, "конечная") })
        {
            var k = TransparencyMath.ViewDepthParams(proj);
            foreach (float d in (float[])[0.6f, 1, 7.5f, 24, 150])
            {
                var clip = Vector4.Transform(new Vector4(0.3f, -0.2f, -d, 1), proj);
                Assert.True(MathF.Abs(TransparencyMath.ViewDepth(clip.Z / clip.W, k) - d) <= 1e-4f * d, $"{name}: {d} м");
            }
        }
        Assert.True(MathF.Abs(TransparencyMath.ViewDepth(0, TransparencyMath.ViewDepthParams(finite)) - far) < 0.1f);
        Assert.True(TransparencyMath.ViewDepth(0, TransparencyMath.ViewDepthParams(infinite)) > 1e15f);
        Assert.Throws<ArgumentException>(() => TransparencyMath.ViewDepthParams(Matrix4x4.CreateOrthographic(10, 10, 0.1f, 100)));
        Assert.Equal(0.5f, TransparencyMath.DepthFade(9, 8, 2));
        Assert.Equal(0f, TransparencyMath.DepthFade(7, 8, 2));
        Assert.Equal((1, 1), (TransparencyMath.HalfSize(1), TransparencyMath.HalfSize(2)));
        Assert.Equal((34, 21), (TransparencyMath.HalfSize(67), TransparencyMath.HalfSize(41)));
        Assert.Equal((0, 0, 0.75f), TransparencyMath.Taps(0, 34));
        Assert.Equal((0, 1, 0.25f), TransparencyMath.Taps(1, 34));
        Assert.Equal((32, 33, 0.75f), TransparencyMath.Taps(66, 34));
    }

    // Передний план (5 м) — прямоугольники [x0, y0, x1, y1): столбцы на чётных и нечётных краях, столбец в 1 пиксель, полоса строк; фон — 50 м.
    private static readonly int[] FrontRects = [20, 0, 27, 1000, 41, 0, 44, 1000, 55, 0, 56, 1000, 0, 31, 15, 34];
    private const float Front = 5, Back = 50;

    private static bool IsFront(int x, int y)
    {
        for (int r = 0; r < FrontRects.Length; r += 4)
            if (x >= FrontRects[r] && x < FrontRects[r + 2] && y >= FrontRects[r + 1] && y < FrontRects[r + 3]) return true;
        return false;
    }

    // Проходы сцены и слоя ½ — без замыканий на кадр (делегаты созданы один раз): замер 0 Б видит только рендер.
    private static readonly Action<RenderPassContext> RecordRects = ctx =>
    {
        ctx.SetPipeline(Rects);
        ctx.SetConstant(4, Near);
        ctx.SetConstant(5, Front);
        ctx.SetConstant(6, Back);
        for (int i = 0; i < FrontRects.Length; i++) ctx.SetConstant(8 + i, FrontRects[i]);
        ctx.Draw(3);
    };

    private void SceneRects(RenderFrame f) =>
        f.Graph.AddPass("test.scene").Color(f.SceneColor, _renderer.SceneColorClear).Depth(f.Scene.Depth, DepthAccess.Write, 0).Execute(RecordRects);

    private HalfResTarget _half;
    private int _mode;
    private Action<RenderPassContext>? _recordHalf;

    /// <summary>Слой ½: режим 0 — красный у текселей ближе 20 м, синий у дальних (альфа 0.5); 1 — плавный узор.</summary>
    private void HalfPass(RenderFrame f, HalfResTarget half, int mode)
    {
        _half = half;
        _mode = mode;
        f.Graph.AddPass("test.half", FramePhase.Transparent).Color(half.Color).Read(half.Depth).Execute(_recordHalf ??= RecordHalf);
    }

    private void RecordHalf(RenderPassContext ctx)
    {
        ctx.SetPipeline(HalfLayer);
        ctx.SetConstant(0, _mode);
        ctx.SetConstant(1, _half.DepthSrv);
        ctx.SetConstant(2, _half.ViewDepth.X);
        ctx.SetConstant(3, _half.ViewDepth.Y);
        ctx.SetConstant(4, 20f);
        ctx.SetConstant(5, 0.5f);
        ctx.SetConstant(6, _half.Width);
        ctx.SetConstant(7, _half.Height);
        ctx.Draw(3);
    }

    /// <summary>Линейные глубины ½ на CPU: ближайшая глубина блока 2×2 (как <c>PsDownsampleDepth</c>).</summary>
    private float[] HalfDepths(Func<int, int, float> viewDepth, int hw, int hh)
    {
        var z = new float[hw * hh];
        for (int j = 0; j < hh; j++)
            for (int i = 0; i < hw; i++)
            {
                float best = float.MaxValue;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        best = MathF.Min(best, viewDepth(Math.Min(2 * i + dx, W - 1), Math.Min(2 * j + dy, H - 1)));
                z[j * hw + i] = best;
            }
        return z;
    }

    [Fact]
    public void HalfRes_DepthEdges_NoHalo_FlatAreaBilinear()
    {
        Vector4[] Frame(float tolerance, int mode, bool rects, string name)
        {
            _renderer.HalfRes.DepthTolerance = tolerance;
            return Shot(f =>
            {
                if (rects) SceneRects(f);
                else SceneRamp(f, Vector3.Zero, Back, Back);
                var half = f.HalfRes.Begin(K);
                HalfPass(f, half, mode);
                f.HalfRes.Composite();
            }, name, transparent: true);
        }

        int hw = TransparencyMath.HalfSize(W), hh = TransparencyMath.HalfSize(H);
        var gpu = Frame(HalfResTransparency.DefaultDepthTolerance, 0, rects: true, "edges");
        var t = _renderer.LastCapture!.Hdr!.Transparent!;
        Assert.Equal((hw, hh), (t.Width, t.Height));
        Assert.Equal(0.5f, t.Coverage, 1e-3f);
        Assert.True(File.Exists(t.Path) && new FileInfo(t.RawPath!).Length == hw * hh * 8L);
        var passes = _renderer.Graph.LastFrame;
        Assert.Equal(["test.scene", HalfResTransparency.DepthPass, "test.half", HalfResTransparency.CompositePass, "post.tonemap"], passes.Select(p => p.Name));
        Assert.Equal([HalfResTransparency.ColorName, HalfResTransparency.DepthName], passes[1].Writes);
        Assert.Equal(["scene.color"], passes[3].Writes);

        // Эталон: слой ½ по ближайшей глубине блока, апсемпл TransparencyMath.Upsample.
        float Depth(int x, int y) => IsFront(x, y) ? Front : Back;
        var halfZ = HalfDepths(Depth, hw, hh);
        var layer = halfZ.Select(z => z < 20 ? new Vector4(0.5f, 0, 0, 0.5f) : new Vector4(0, 0, 0.5f, 0.5f)).ToArray();
        int front = 0, back = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var got = gpu[y * W + x];
                var e = TransparencyMath.Upsample(layer, halfZ, hw, hh, x, y, Depth(x, y), HalfResTransparency.DefaultDepthTolerance);
                AssertClose(e, got, 2e-3f, $"пиксель ({x}, {y})");
                // Ореола нет: передний план — без синего фона, фон — без красного переднего плана.
                if (IsFront(x, y))
                {
                    front++;
                    Assert.True(got.Z <= 1e-3f && got.X >= 0.499f, $"передний план ({x}, {y}) получил фон: {got}");
                }
                else
                {
                    back++;
                    Assert.True(got.X <= 1e-3f && got.Z >= 0.499f, $"фон ({x}, {y}) получил передний план: {got}");
                }
            }
        _output.WriteLine($"{W}×{H} → ½ {hw}×{hh}: переднего плана {front} пикселей, фона {back}");

        // Контроль: чистый билинейный (допуск бесконечен) даёт ореол — проверка его видит.
        var bilinear = Frame(float.PositiveInfinity, 0, rects: true, "bilinear");
        float halo = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (IsFront(x, y)) halo = MathF.Max(halo, bilinear[y * W + x].Z);
        Assert.True(halo >= 0.1f, $"билинейный без глубины: наибольший ореол {halo}");

        // Ровная глубина: апсемпл = билинейный от узора ½.
        var flat = Frame(HalfResTransparency.DefaultDepthTolerance, 1, rects: false, "flat");
        var pattern = new Vector4[hw * hh];
        for (int j = 0; j < hh; j++)
            for (int i = 0; i < hw; i++)
                pattern[j * hw + i] = new Vector4(Round((i + 0.5f) / hw * 0.5f), Round((j + 0.5f) / hh * 0.5f), Round(0.125f), 0.5f);
        var sameZ = Enumerable.Repeat(Back, hw * hh).ToArray();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                AssertClose(TransparencyMath.Upsample(pattern, sameZ, hw, hh, x, y, Back, float.PositiveInfinity), flat[y * W + x], 2e-3f,
                    $"ровный ({x}, {y})");
        AssertNoDebugMessages();
    }

    [Fact]
    public void HalfRes_FirstFrame_OddSizes_Misuse_SteadyFrameAllocatesNothing()
    {
        Action<RenderFrame> steady = f =>
        {
            SceneRects(f);
            var half = f.HalfRes.Begin(K);
            HalfPass(f, half, 0);
            f.HalfRes.Composite();
        };
        // Первый кадр рендера — сразу со слоем ½; затем нечётные и крайние размеры: ½ — с округлением вверх.
        foreach (var (w, h) in new[] { (67, 41), (1, 1), (2, 3), (101, 55), (64, 64), (67, 41) })
        {
            _renderer.Resize(w, h);
            Shot(steady, $"size{w}x{h}", transparent: true);
            var t = _renderer.LastCapture!.Hdr!.Transparent!;
            Assert.Equal((TransparencyMath.HalfSize(w), TransparencyMath.HalfSize(h)), (t.Width, t.Height));
            Assert.Equal(0, t.NonFinite);
        }

        // Ошибки вызывающего — исключение, следующий кадр чистый.
        var noComposite = Assert.Throws<InvalidOperationException>(() => _renderer.Render(f =>
        {
            SceneRects(f);
            f.HalfRes.Begin(K);
        }, vsync: false));
        Assert.Contains("Composite", noComposite.Message);
        Assert.Throws<InvalidOperationException>(() => _renderer.Render(f =>
        {
            SceneRects(f);
            f.HalfRes.Begin(K);
            f.HalfRes.Begin(K);
        }, vsync: false));
        Assert.Throws<InvalidOperationException>(() => _renderer.Render(f => f.HalfRes.Composite(), vsync: false));
        _renderer.Render(steady, vsync: false);
        Assert.NotNull(_renderer.HalfRes.Current);   // слой кадра виден до следующего кадра (снимок, отчёт)
        _renderer.Hdr = false;
        Assert.Throws<InvalidOperationException>(() => _renderer.Render(f => f.HalfRes.Begin(K), vsync: false));
        _renderer.Hdr = true;

        for (int i = 0; i < 30; i++) _renderer.Render(steady, vsync: false);
        const int frames = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames; i++) _renderer.Render(steady, vsync: false);
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Assert.True(bytes == 0, $"кадр со слоем ½ выделил {bytes} Б");
        AssertNoDebugMessages();
    }

    [Fact]
    public void HalfRes_GpuTime_1280x720()
    {
        // Время утилиты без отладочного слоя и GPU-валидации (они замедляют GPU в разы): отдельный рендер 1280×720.
        using var window = new Win32Window(new WindowOptions("Sword.Engine.Render.Tests", 1280, 720, Activate: false));
        using var renderer = Create(window, validation: false);
        renderer.Resize(1280, 720);
        void Scene(RenderFrame f)
        {
            SceneRects(f);
            f.HalfRes.Begin(K);
            f.HalfRes.Composite();
        }
        double depth = 0, composite = 0, frame = 0;
        int samples = 0;
        long seen = -1;
        for (int i = 0; i < 400; i++)
        {
            window.PumpMessages();
            renderer.Render(Scene, vsync: false);
            var gpu = renderer.Gpu;
            if (i < 100 || !gpu.Valid || gpu.Sequence == seen) continue;
            seen = gpu.Sequence;
            for (int p = 0; p < gpu.Count; p++)
            {
                if (gpu.NameOf(p) == HalfResTransparency.DepthPass) depth += gpu.MillisecondsOf(p);
                if (gpu.NameOf(p) == HalfResTransparency.CompositePass) composite += gpu.MillisecondsOf(p);
            }
            frame += gpu.FrameMs;
            samples++;
        }
        Assert.True(samples > 100);
        depth /= samples;
        composite /= samples;
        frame /= samples;
        _output.WriteLine($"1280×720, {samples} кадров: уменьшение глубины {depth:0.000} мс, композит {composite:0.000} мс, кадр {frame:0.000} мс");
        Assert.True(depth > 0 && composite > 0);
    }
}
