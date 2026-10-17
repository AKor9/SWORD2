using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Точечный свет на арене (устройство, Manny из пака, категории Gpu и Content): источник, до которого ничего не достаёт, кадр
/// не меняет; тестовый факел (<see cref="ArenaClient.TestLights"/>) — тёплое пятно без синего; 70 источников — в буфере 64.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class ArenaPointLightGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 1280, H = 720;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-lights-{Guid.NewGuid():N}");

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        Log.RemoveSink(this);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Arena_NoLightsBitExact_TorchWarmSpot_OverlayAndLimit()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.Lights", W, H, Activate: false));
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=3"), CharacterAssets.LoadFromPak());
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new PlayerCommand { Move = new Vector2(1, 0.3f) }), ticksPerFrame: 2);
        var arena = new ArenaClient(new CameraPose(0.6f, 0.95f, 12f), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        arena.Initialize(renderer, clock.Link);
        // Кадры сравниваются между собой: дрожание камеры TAA меняло бы сцену от кадра к кадру.
        renderer.Taa.Enabled = false;
        for (int i = 0; i < 30; i++)
        {
            clock.RunFrame(0);
            arena.Extract(new FrameView(1, 2, 0, default));
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
        }

        // Кадр без новых тиков — та же сцена; вернёт сырой цвет сцены (half RGBA).
        Half[] Scene(string name, Action<RenderFrame> build)
        {
            string hdr = Path.Combine(_dir, name + ".hdr.png");
            window.PumpMessages();
            renderer.Render(build, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr });
            return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin"))).ToArray();
        }

        var none = Scene("none", arena.BuildFrame);
        Assert.Equal(0, renderer.Lights.Report().Shown);
        // Источник вдали от арены: шейдер обходит его в каждом пикселе, вклад — ровно 0.
        var far = Scene("far", f =>
        {
            arena.BuildFrame(f);
            f.Lights.Add(new PointLight(new Vector3(300, 5, 300), Vector3.One, 50, 2, LightFalloff.InverseSquared));
        });
        Assert.Equal(1, renderer.Lights.Report().Shown);
        Assert.True(none.AsSpan().SequenceEqual(far), "источник, до которого ничего не достаёт, меняет цвет сцены");

        arena.TestLights = 1;
        arena.ShowLights = true;   // оверлей — после тонмапа: цвет HDR-сцены он не меняет
        var torch = Scene("torch", arena.BuildFrame);
        Assert.Empty(renderer.LastCapture!.Notes);
        Assert.Equal(1, arena.DrawnLights);
        arena.ShowLights = false;
        var report = renderer.Lights.Report();
        Assert.Equal(1, report.Shown);
        var light = report.Lights[0].Light;
        var player = arena.View.Player!.Value;
        Assert.Equal(new Vector3(player.Root.X, player.Height, player.Root.Z) + ArenaClient.TestLightOffset, light.Position);

        // Прибавка факела: синий — нигде, красный — пятно; пиксель пола под факелом.
        int warm = 0;
        float maxRed = 0;
        for (int p = 0; p < W * H; p++)
        {
            Assert.Equal(none[p * 4 + 2], torch[p * 4 + 2]);
            float dr = (float)torch[p * 4] - (float)none[p * 4];
            Assert.True(dr >= 0, $"факел затемнил пиксель {p % W}, {p / W}");
            if (dr > 0.1f) warm++;
            maxRed = MathF.Max(maxRed, dr);
        }
        // Пол в 0.8 м от точки под факелом по восьми направлениям (часть может закрыть сам игрок): прибавка ≈ 100·e^(−1.96−0.64)/π × альбедо.
        var ring = new List<string>();
        int lit = 0;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4;
            var floor = new Vector3(light.Position.X + 0.8f * MathF.Cos(a), player.Height, light.Position.Z + 0.8f * MathF.Sin(a));
            var clip = Vector4.Transform(new Vector4(floor, 1), arena.Camera.ViewProj);
            int fx = (int)((clip.X / clip.W * 0.5f + 0.5f) * W), fy = (int)((0.5f - clip.Y / clip.W * 0.5f) * H);
            Assert.InRange(fx, 0, W - 1);
            Assert.InRange(fy, 0, H - 1);
            float dr = (float)torch[(fy * W + fx) * 4] - (float)none[(fy * W + fx) * 4];
            ring.Add($"{dr:0.##}");
            if (dr > 0.2f) lit++;
        }
        output.WriteLine($"факел: пикселей с прибавкой красного > 0.1 — {warm} ({100.0 * warm / (W * H):0.##} %), наибольшая прибавка {maxRed:0.###}; " +
            $"пол в 0.8 м от точки под факелом: +{string.Join(", +", ring)}");
        Assert.True(lit >= 4, $"пол вокруг факела: {string.Join(", ", ring)}");
        Assert.True(warm > 1000, $"пятно факела — {warm} пикселей");

        // Предел буфера: 64 тестовых факела — все в буфере, 70 — шесть отброшены (время с 1 и 64 — Sword.exe --bench --test-light N,
        // без GPU-валидации).
        arena.TestLights = PointLights.MaxLights;
        Scene("many", arena.BuildFrame);
        Assert.Equal((PointLights.MaxLights, 0), (renderer.Lights.Report().Shown, renderer.Lights.Report().Dropped));
        arena.TestLights = 70;
        Scene("over", arena.BuildFrame);
        Assert.Equal((PointLights.MaxLights, 6), (renderer.Lights.Report().Shown, renderer.Lights.Report().Dropped));
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }
}
