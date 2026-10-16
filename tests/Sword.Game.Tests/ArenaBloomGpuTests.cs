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

/// <summary>Bloom на дневной арене (Gpu, Content): сцена почти вся ниже порога — кадр с bloom и без почти одинаков.</summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class ArenaBloomGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 1280, H = 720;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-bloom-{Guid.NewGuid():N}");

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
    public void DaylightArena_BloomAddsAlmostNothing()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.Bloom", W, H, Activate: false));
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=3"), CharacterAssets.LoadFromPak());
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new PlayerCommand { Move = new Vector2(1, 0.3f) }), ticksPerFrame: 2);
        // Камера — как у Sword.exe по умолчанию: арена сверху, бойцы, колонны и рельеф в кадре.
        var arena = new ArenaClient(new CameraPose(0.6f, 0.95f, 24f), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        arena.Initialize(renderer, clock.Link);
        Assert.True(renderer.Hdr && renderer.Bloom == BloomSettings.Default);
        for (int i = 0; i < 30; i++)
        {
            clock.RunFrame(0);
            arena.Extract(new FrameView(1, 2, 0, default));
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
        }

        byte[] Shot(string name)
        {
            string path = Path.Combine(_dir, name + ".bmp");
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, new FrameCapture(path) { BloomPath = Path.Combine(_dir, name + ".bloom.png") });
            return File.ReadAllBytes(path);
        }

        var on = Shot("on");
        var hdr = renderer.LastCapture!.Hdr!;
        var bloom = hdr.Bloom!;
        renderer.Bloom = BloomSettings.Off;
        var off = Shot("off");
        Assert.Null(renderer.LastCapture!.Hdr!.Bloom);

        // BMP: заголовок 54 байта, строки 1280 × 3 без выравнивания — сравниваются байты цвета.
        int worst = 0, changed = 0;
        for (int i = 54; i < on.Length; i++)
        {
            int d = Math.Abs(on[i] - off[i]);
            worst = Math.Max(worst, d);
            if (d > 1) changed++;
        }
        output.WriteLine($"вход тонмапа: максимум {hdr.MaxLuminance:0.###}, среднее {hdr.MeanLuminance:0.###}, ярче 1 — {hdr.OverOne:P4}; " +
            $"добавка bloom: максимум {bloom.MaxLuminance:G4}, среднее {bloom.MeanLuminance:G4}; кадр: худший байт {worst}, байтов с разницей > 1 — {changed}");
        Assert.Equal(0, bloom.NonFinite);
        Assert.True(bloom.MaxLuminance < 0.05f, $"добавка bloom на дневной арене до {bloom.MaxLuminance}");
        Assert.True(worst <= 2 && changed <= on.Length / 10000, $"bloom меняет дневную арену: худший байт {worst}, байтов > 1 — {changed}");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }
}
