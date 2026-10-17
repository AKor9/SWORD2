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
/// Скорости сцены арены (цель <c>SceneTargets.Velocity</c>, предпроход; устройство, Manny из пака, категории Gpu и Content): кадр без
/// тиков и без движения камеры — везде ноль; сдвиг камеры — скорость земли как у точки мира в старой и новой матрицах; бегущий игрок
/// при неподвижной камере — скорость не ноль только у бойца (палитра прошлого кадра по ключу движения), у земли — ноль.
/// </summary>
[Collection("game-gpu")]
public sealed class ArenaVelocityGpuTests(ITestOutputHelper output) : IDisposable, ILogSink
{
    private const int W = 960, H = 540;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-arena-velocity-{Guid.NewGuid():N}");
    private Vector2 _move;

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
    [Trait("Category", "Gpu")]
    [Trait("Category", "Content")]
    public void Velocity_ZeroWhenStill_CameraMatchesMatrices_RunnerOnlyMoves()
    {
        Log.AddSink(this);
        Directory.CreateDirectory(_dir);
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.Velocity", W, H, Activate: false));
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=0"), CharacterAssets.LoadFromPak());
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(new PlayerCommand { Move = _move }), ticksPerFrame: 1);
        var arena = new ArenaClient(new CameraPose(0.6f, 1.1f, 9f), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        arena.Initialize(renderer, clock.Link);

        void Frame(bool tick)
        {
            if (tick) clock.RunFrame(0);
            arena.Extract(new FrameView(1, 1, 0, default));
            window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
        }
        Half[] Velocity(bool tick, string name)
        {
            if (tick) clock.RunFrame(0);
            arena.Extract(new FrameView(1, 1, 0, default));
            window.PumpMessages();
            string path = Path.Combine(_dir, name + ".velocity.bin");
            renderer.Render(arena.BuildFrame, vsync: false, new FrameCapture(null) { VelocityPath = path });
            Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "scene.prepass");
            return MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(path)).ToArray();
        }
        static float Speed(Half[] v, int p) => new Vector2((float)v[p * 2], (float)v[p * 2 + 1]).Length();

        for (int i = 0; i < 20; i++) Frame(tick: true);
        Frame(tick: false);

        // Без тиков и движения камеры — ни одной скорости.
        var still = Velocity(tick: false, "still");
        float stillMax = 0;
        for (int p = 0; p < W * H; p++) stillMax = MathF.Max(stillMax, Speed(still, p));
        Assert.True(stillMax < 1e-4f, $"без движения скорость до {stillMax}");

        // Сдвиг камеры на 0.4 м: скорость земли в центре кадра — разность проекций той же точки мира.
        var before = arena.Camera.ViewProj;
        arena.Camera.Target += new Vector3(0.4f, 0, 0.1f);
        var moved = Velocity(tick: false, "camera");
        var after = arena.Camera.ViewProj;
        int cx = W / 2 + 40, cy = H / 2 + 60;
        Assert.True(arena.Camera.ScreenToGround(cx + 0.5f, cy + 0.5f, W, H, out var ground));
        var a = Vector4.Transform(new Vector4(ground, 1), after);
        var b = Vector4.Transform(new Vector4(ground, 1), before);
        var expected = (new Vector2(a.X, a.Y) / a.W - new Vector2(b.X, b.Y) / b.W) * new Vector2(0.5f, -0.5f);
        var got = new Vector2((float)moved[(cy * W + cx) * 2], (float)moved[(cy * W + cx) * 2 + 1]);
        output.WriteLine($"сдвиг камеры: скорость земли {got}, по матрицам {expected}");
        Assert.True(expected.Length() > 0.005f, $"сдвиг камеры слишком мал: {expected}");
        Assert.True((got - expected).Length() < 0.002f + expected.Length() * 0.02f, $"скорость земли {got} против {expected}");

        // Игрок бежит, камера стоит: скорость — только у бойца (и его меча), земля — ноль.
        _move = new Vector2(1, 0.3f);
        for (int i = 0; i < 12; i++) Frame(tick: true);
        var run = Velocity(tick: true, "run");
        int movingPixels = 0;
        float fastest = 0;
        for (int p = 0; p < W * H; p++)
        {
            float s = Speed(run, p);
            if (s > 2e-4f) movingPixels++;
            fastest = MathF.Max(fastest, s);
        }
        output.WriteLine($"бег: движется {movingPixels} пикселей, наибольшая скорость {fastest:0.####} экрана за кадр");
        Assert.True(movingPixels > 300, $"движется лишь {movingPixels} пикселей — палитра прошлого кадра не нашлась?");
        Assert.True(movingPixels < W * H / 10, $"движется {movingPixels} пикселей — земля тоже?");
        Assert.True(_d3d12.Count == 0, string.Join("\n", _d3d12));
    }
}
