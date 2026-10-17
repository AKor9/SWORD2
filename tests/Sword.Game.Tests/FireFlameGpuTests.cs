using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Contracts;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Пламя огня на настоящем устройстве (слой отладки D3D12) без игры: площадка на весь кадр, синтетический выход поля и шум-рампа,
/// сцена с известной глубиной по столбцам. Пиксель = CPU-эталон <see cref="FlameMath"/>; альфа — из двух фонов (чёрного и белого):
/// α = 1 − (белый − чёрный); вне ρ — фон бит в бит; мягкий край 10 см по глубине, за стеной не видно; тот же кадр — тот же HDR; кадр — 0 Б.
/// Категория Gpu.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
public sealed class FireFlameGpuTests : IDisposable, ILogSink
{
    private const int W = 256, H = 256;
    private const float Near = 0.1f, QuadDepth = 2f;
    private const string Shader = "fire_flame_tests.hlsl";
    private static readonly GraphicsPipelineDesc ScenePipeline = new(Shader, "VsFull", "PsScene", Depth: DepthMode.TestWrite);
    private static readonly GraphicsPipelineDesc FieldPipeline = new(Shader, "VsFull", "PsField");
    private static readonly Vector2 K = new(0, Near);

    private readonly ITestOutputHelper _output;
    private readonly Win32Window _window;
    private readonly Renderer _renderer;
    private readonly GpuImage _noise;
    private readonly FireFlame _flame;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-flame-{Guid.NewGuid():N}");
    private readonly FlameQuad[] _quads = new FlameQuad[1];
    private long _flameBytes;

    public FireFlameGpuTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireFlame", W, H, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        FireFlame.Contribute(contract);
        _renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders")), contract)
        {
            Hdr = true,
            Bloom = BloomSettings.Off,
        };
        // Шум — рампа по x (r = x/255, строки одинаковы), линейный, с мипами: линейная функция координаты на любом мипе.
        var rgba = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
            {
                int o = (y * 256 + x) * 4;
                rgba[o] = rgba[o + 1] = rgba[o + 2] = (byte)x;
                rgba[o + 3] = 255;
            }
        _noise = _renderer.CreateImage("test.noise", 256, 256, rgba, mips: true);
        _flame = new FireFlame(_renderer, _noise.Srv, 0, 2);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _renderer.WaitForIdle();
        _flame.Resources.Dispose();
        _noise.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Log.RemoveSink(this);
        if (Directory.Exists(_dir) && Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(_dir, recursive: true);
    }

    // Площадка на весь кадр: мир (x, y) = (2u − 1, 1 − 2v), клип = (x, y, near/D, 1) — глубина вида пикселя D при k = (0, near).
    private static Matrix4x4 ViewProj => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, Near / QuadDepth, 1);

    private static readonly Vector3 Eye = new(0, 0, -5);
    private static readonly Vector2 WorldOffset = new(0.123f, -0.071f);
    private const float Phase = 0.3f;

    private static SceneFog Fog => new(new Vector3(0.5f, 0.25f, 0.125f), 4.9f, 0.5f, 0.05f, 0.8f);

    /// <summary>Кадр: сцена (цвет <paramref name="background"/>, глубина — рампа по столбцам z0…z1), поле режима <paramref name="mode"/>, пламя.</summary>
    private void Build(RenderFrame f, Vector3 background, float z0, float z1, uint mode)
    {
        f.Graph.AddPass("test.scene").Color(f.SceneColor, _renderer.SceneColorClear).Depth(f.Scene.Depth, DepthAccess.Write, 0).Execute(ctx =>
        {
            ctx.SetPipeline(ScenePipeline);
            ctx.SetConstant(1, background.X);
            ctx.SetConstant(2, background.Y);
            ctx.SetConstant(3, background.Z);
            ctx.SetConstant(4, Near);
            ctx.SetConstant(5, z0);
            ctx.SetConstant(6, z1);
            ctx.SetConstant(7, ctx.Width);
            ctx.Draw(3);
        });
        var field = f.Graph.CreateTexture("test.field", new TextureDesc(TextureFormat.Rgba16Float, TextureUsage.ColorTarget, W, H));
        f.Graph.AddPass("test.field").Color(field, Vector4.Zero).Execute(ctx =>
        {
            ctx.SetPipeline(FieldPipeline);
            ctx.SetConstant(0, mode);
            ctx.SetConstant(1, W);
            ctx.SetConstant(2, H);
            ctx.Draw(3);
        });
        _quads[0] = new FlameQuad { Center = Vector3.Zero, U = Vector3.UnitX, V = Vector3.UnitY, WorldOffset = WorldOffset, Field = field };
        long before = GC.GetAllocatedBytesForCurrentThread();
        _flame.Add(f, new FlameView(ViewProj, Eye, K, Fog, Phase), _quads);
        _flameBytes += GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private Vector4[] Shot(Vector3 background, float z0, float z1, uint mode, string name)
    {
        Directory.CreateDirectory(_dir);
        string hdr = Path.Combine(_dir, name + ".hdr.png");
        _window.PumpMessages();
        _renderer.Render(f => Build(f, background, z0, z1, mode), vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr });
        var halves = MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin")));
        var rgba = new Vector4[halves.Length / 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = new((float)halves[i * 4], (float)halves[i * 4 + 1], (float)halves[i * 4 + 2], (float)halves[i * 4 + 3]);
        Assert.Equal(W * H, rgba.Length);
        return rgba;
    }

    /// <summary>Тексель синтетического поля (как <c>PsField</c>), значения — в half, как в цели RGBA16F.</summary>
    private static Vector4 FieldTexel(int x, int y, uint mode)
    {
        if (mode == 1) return new Vector4(0, 0, 1, 1);
        float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
        return new Vector4(Round(12f * (v - 0.5f)), Round(6f * (u - 0.5f)), Round(1.5f * u - 0.25f), 1);
    }

    private static float Round(float v) => (float)(Half)v;

    /// <summary>Шум-рампа в координате c (линейный на всех мипах вдали от шва заворота): (256·frac(c) − 0.5)/255.</summary>
    private static float Ramp(float c) => (256f * (c - MathF.Floor(c)) - 0.5f) / 255f;

    private static float SeamDistance(float c)
    {
        float f = c - MathF.Floor(c);
        return MathF.Min(f, 1 - f);
    }

    [Fact]
    public void Pixels_MatchCpuFormula_AlphaAtMostOne_TransparentOutsideDensity()
    {
        var p = FlameParams.Torch4;
        var black = Shot(Vector3.Zero, 1000, 1000, 0, "black");
        var white = Shot(Vector3.One, 1000, 1000, 0, "white");
        var again = Shot(Vector3.Zero, 1000, 1000, 0, "black2");
        Assert.True(MemoryMarshal.AsBytes(black.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(again.AsSpan())), "тот же кадр дважды — HDR не бит в бит");

        int compared = 0, outside = 0, tail = 0;
        float worst = 0, maxAlpha = 0, maxEmission = 0;
        for (int y = 2; y < H; y += 4)
            for (int x = 1; x < W; x += 3)
            {
                int i = y * W + x;
                var s = FieldTexel(x, y, 0);
                var uv = new Vector2((x + 0.5f) / W, (y + 0.5f) / H);
                float gpuAlpha = 1 - (white[i].Z - black[i].Z);   // синий: эмиссии в нём нет — half фона точнее
                Assert.True(gpuAlpha >= -2e-3f && gpuAlpha <= 1 + 2e-3f, $"({x}, {y}): α = {gpuAlpha} вне [0, 1]");
                maxAlpha = MathF.Max(maxAlpha, gpuAlpha);
                if (s.Z <= 0)
                {
                    // Вне ρ — прозрачно: фон бит в бит.
                    Assert.Equal(Vector4.Zero with { W = black[i].W }, black[i]);
                    Assert.Equal(new Vector3(1), new Vector3(white[i].X, white[i].Y, white[i].Z));
                    outside++;
                    continue;
                }
                var (a, b, _) = FlameMath.Flow(uv, WorldOffset, new Vector2(s.X, s.Y), Phase, p);
                if (SeamDistance(a.X) < 0.04f || SeamDistance(b.X) < 0.04f) continue;
                var world = new Vector3(2 * uv.X - 1, 1 - 2 * uv.Y, 0);
                float fog = Fog.Factor(Eye, world);
                var expected = FlameMath.Pixel(s, Ramp(a.X), Ramp(b.X), Phase, uv, 1, fog, Fog.Colour, p);
                // Допуск: шум железа ±0.004 (квантование рампы и мипов) через n³, плюс half и туман.
                float rho = FlameMath.Density(s.Z);
                float n = Ramp(a.X) + (Ramp(b.X) - Ramp(a.X)) * MathF.Abs(2 * Phase - 1);
                float dk = p.DensityGain * (MathF.Pow(n + 0.004f, 3) - MathF.Pow(MathF.Max(n - 0.004f, 0), 3)) * MathF.Pow(rho, p.DensityPower);
                // Срез хвоста saturate(k/GlowCut): тот же шум k переходит в альфу.
                float da = p.GlowCut > 0 ? dk / p.GlowCut : 0;
                var emission = FlameMath.Emission(n, rho, p);
                for (int c = 0; c < 3; c++)
                {
                    float got = black[i][c], want = expected[c];
                    float tol = expected.W * (1 - fog) * dk * MathF.Abs(p.Colour1[c] - p.Colour2[c]) + da * emission[c] + 4e-3f * MathF.Abs(want) + 1e-3f;
                    worst = MathF.Max(worst, MathF.Abs(got - want) / tol);
                    Assert.True(MathF.Abs(got - want) <= tol, $"({x}, {y}) канал {c}: GPU {got}, CPU {want}, допуск {tol} (n {n}, ρ {rho})");
                }
                Assert.True(MathF.Abs(gpuAlpha - expected.W) <= 3e-3f + da, $"({x}, {y}): α GPU {gpuAlpha}, CPU {expected.W}");
                if (expected.W < 1e-3f) tail++;
                maxEmission = MathF.Max(maxEmission, expected.X / MathF.Max(expected.W, 1e-6f));
                compared++;
            }
        _output.WriteLine($"сверено пикселей {compared} (срезано хвостом {tail}), вне ρ {outside}; худшее — {worst:0.00} допуска; max α {maxAlpha:0.000}, max эмиссии R {maxEmission:0.0}");
        Assert.True(compared > 1000 && outside > 100 && tail > 100, $"мало пикселей: {compared} сверено, {outside} вне ρ, {tail} срезано хвостом");
        Assert.True(maxEmission > 1, "в выборке нет ядра ярче 1");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void SoftEdge_FadesOverTenCentimetres_HiddenBehindWall()
    {
        // Стена по столбцам от D − 0.2 до D + 0.3 м: левее D — стена ближе площадки (не видно), дальше — мягкий край 10 см.
        // Срез хвоста выключен: альфа по ρ = 1 зависит только от маски краёв и мягкого края, не от шума.
        var p = FlameParams.Torch4 with { GlowCut = 0 };
        _flame.Params = p;
        float z0 = QuadDepth - 0.2f, z1 = QuadDepth + 0.3f;
        var black = Shot(Vector3.Zero, z0, z1, 1, "edge-black");
        var white = Shot(Vector3.One, z0, z1, 1, "edge-white");
        int hidden = 0, fading = 0, full = 0;
        int y = H / 2;
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            float z = z0 + (z1 - z0) * (x + 0.5f) / W;
            var uv = new Vector2((x + 0.5f) / W, (y + 0.5f) / H);
            float fade = Math.Clamp((z - QuadDepth) / p.DepthFadeDistance, 0, 1);
            float gpuAlpha = 1 - (white[i].Z - black[i].Z);   // синий: эмиссии в нём нет — half фона точнее
            if (z < QuadDepth - 1e-3f)
            {
                Assert.Equal(0f, black[i].X);
                Assert.Equal(1f, white[i].X);
                hidden++;
                continue;
            }
            if (MathF.Abs(z - QuadDepth) < 2e-3f) continue;   // у самой стены — сравнение глубин в пределах ULP
            float want = FlameMath.Alpha(1, uv, fade, p);
            Assert.True(MathF.Abs(gpuAlpha - want) <= 4e-3f, $"x {x} (z {z:0.000} м): α GPU {gpuAlpha}, CPU {want}");
            if (fade < 1) fading++;
            else full++;
        }
        _output.WriteLine($"строка {y}: за стеной {hidden}, мягкий край {fading}, полная {full}");
        Assert.True(hidden > 80 && fading > 40 && full > 80, $"столбцов: за стеной {hidden}, край {fading}, полная {full}");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    [Fact]
    public void Frame_AllocatesNothing()
    {
        for (int frame = 0; frame < 40; frame++)
        {
            if (frame == 10) _flameBytes = 0;
            _window.PumpMessages();
            _renderer.Render(f => Build(f, Vector3.Zero, 1000, 1000, 0), vsync: false);
        }
        _output.WriteLine($"байт пламени за 30 кадров: {_flameBytes}");
        Assert.Equal(1, _flame.LastQuads);
        Assert.True(_flameBytes == 0, $"кадр пламени выделил {_flameBytes} Б за 30 кадров");
    }
}
