using System.Numerics;
using System.Runtime.InteropServices;
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
/// Скиннинг кадра и отсев теней на арене (устройство, Manny из пака, категории Gpu и Content): бойцы и мечи, скинированные одним
/// проходом, — тот же кадр, что со скиннингом в каждом проходе; отсев заслоняющих в тенях солнца и факела кадра не меняет (бит в бит),
/// а экземпляры отсеивает.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class ArenaSkinningGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 1280, H = 720;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-skinning-{Guid.NewGuid():N}");

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        Log.RemoveSink(this);
        if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") == "1") return;
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static float Luma(Half[] c, int p) => 0.2126f * (float)c[p * 4] + 0.7152f * (float)c[p * 4 + 1] + 0.0722f * (float)c[p * 4 + 2];

    [Fact]
    public void PreSkin_SameFrame_ShadowCulling_BitExactAndCulls()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.Skinning", W, H, Activate: false));
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=10"), CharacterAssets.LoadFromPak());
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new PlayerCommand { Move = new Vector2(1, 0.3f) }), ticksPerFrame: 2);
        // Камера вблизи: ближний каскад мал, часть бойцов — только в дальнем; факел у игрока — тени граней.
        var arena = new ArenaClient(new CameraPose(0.6f, 0.95f, 8f), hud: false) { TestLights = 1 };
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

        // Та же сцена без новых тиков (настройки — со следующего кадра): кадр прогрева и снимок сырого цвета сцены.
        Half[] Scene(string name)
        {
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
            string hdr = Path.Combine(_dir, name + ".hdr.png");
            renderer.Render(arena.BuildFrame, vsync: false, new FrameCapture(Path.Combine(_dir, name + ".png")) { HdrPath = hdr });
            return MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(Path.ChangeExtension(hdr, ".bin"))).ToArray();
        }

        var culled = Scene("culled");
        Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "skin.vertices");
        Assert.True(renderer.Lights.Shadowed >= 1, "факел без теней");
        renderer.Render(arena.BuildFrame, vsync: false);
        int culledInstances = renderer.Skinned.Culled;
        arena.CullShadowCasters = false;
        var all = Scene("all");
        renderer.Render(arena.BuildFrame, vsync: false);
        Assert.Equal(0, renderer.Skinned.Culled);
        output.WriteLine($"отсеяно экземпляров в проходах теней за кадр: {culledInstances}");
        Assert.True(culledInstances > 0, "отсев ничего не отсеял");
        Assert.True(all.AsSpan().SequenceEqual(culled), "отсев заслоняющих изменил кадр");

        // Меш теней (прореженный Manny) против полного: тени почти те же — заметно отличных пикселей мало.
        renderer.Skinned.ShadowMeshes = false;
        var fullShadows = Scene("fullShadows");
        renderer.Skinned.ShadowMeshes = true;
        int shadowDiffer = 0;
        for (int p = 0; p < W * H; p++)
        {
            float a = Luma(all, p), b = Luma(fullShadows, p);
            if (MathF.Abs(a - b) > 0.03f * MathF.Max(b, 0.05f)) shadowDiffer++;
        }
        output.WriteLine($"тени мешем теней против полного: отличных на 3 % пикселей {shadowDiffer} ({100.0 * shadowDiffer / (W * H):0.###} %)");
        Assert.True(shadowDiffer < W * H / 200, $"меш теней меняет {shadowDiffer} пикселей");

        renderer.Skinned.PreSkin = false;
        var perPass = Scene("perPass");
        Assert.DoesNotContain(renderer.Graph.LastFrame, p => p.Name == "skin.vertices");
        float maxDiff = 0;
        int differ = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if ((i & 3) == 3) continue;
            float a = (float)all[i], b = (float)perPass[i], d = MathF.Abs(a - b);
            maxDiff = MathF.Max(maxDiff, d / MathF.Max(MathF.Abs(b), 0.05f));
            if (d > 1e-3f + 1e-3f * MathF.Abs(b)) differ++;
        }
        output.WriteLine($"скиннинг кадра против скиннинга в проходах: отличных каналов {differ}, наибольшее отличие {maxDiff:0.####}");
        Assert.True(differ < all.Length / 4 / 2000, $"скиннинг кадра меняет {differ} каналов");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }
}
