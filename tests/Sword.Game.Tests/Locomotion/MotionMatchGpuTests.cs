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
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Оверлей <c>mm</c> на настоящем устройстве (слой отладки D3D12 с GPU-валидацией) и настоящем контенте: игрок на Motion Matching бежит
/// за камерой — слой снимка <c>mm</c> снят без заметки (траектории: запрос, найденный кадр, история, стопы), M переключает оверлей на экране,
/// кадр с ним (снятие в Extract и построение) — 0 Б; вкладка F3 «Локомоция» есть; при <c>clips</c> — слой пуст с заметкой, вкладки нет.
/// Любое предупреждение D3D12 — провал.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class MotionMatchGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-mm-{Guid.NewGuid():N}");

    public MotionMatchGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.MotionMatch", W, H, Activate: false));
        Directory.CreateDirectory(_dir);
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _window.Dispose();
        Log.RemoveSink(this);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static FrameInput Pressed(params InputAction[] actions) =>
        new(new InputState(), InputBindings.Resolve(ArenaInput.All, null), actions.Select(a => a.Name).ToHashSet(), true, true, W, H, 1f / 60);

    private static readonly PlayerCommand Run = new() { Move = new Vector2(0.6f, 1), HasGaze = true, Gaze = new Vector3(20, 0, 200) };

    [Fact]
    public void MotionOverlay_LayerAndKey_ZeroBytesPerFrame()
    {
        var assets = CharacterAssets.LoadFromPak();
        var sim = SwordSimulation.Create(ConfigSet.Empty("locomotion.player=mm", "arena.enemies=0"), assets);
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(Run), ticksPerFrame: 1);
        var arena = new ArenaClient(hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            Assert.Equal(["Бой", "Рэгдолл", "Локомоция"], arena.TuningGroups.Select(g => g.Title));
            void Frames(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    arena.Input(Pressed());   // камера — за игроком
                    clock.RunFrame(0);
                    arena.Extract(new FrameView(1, 1, 0, default));
                }
            }
            Frames(240);
            Assert.False(arena.Motion.Available);   // не показан и не заказан — не снимается

            string Layer(string name) => Path.Combine(_dir, name + ".png");
            var capture = new FrameCapture(Path.Combine(_dir, "color.png")) { Layers = [new CaptureLayer(ArenaClient.MotionOverlay, Layer("mm"))] };
            arena.Input(Pressed());
            arena.Extract(new FrameView(1, 0, 0, default) { Capture = capture });
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, capture);
            var result = renderer.LastCapture!;
            var layer = Assert.Single(result.Layers);
            Assert.True(File.Exists(layer.Path));
            Assert.DoesNotContain(result.Notes, n => n.StartsWith("mm:", StringComparison.Ordinal));
            Assert.True(arena.Motion.Available);
            Assert.True(arena.Motion.Points >= 5 + 5 + 30, $"точек {arena.Motion.Points}");
            Assert.Equal(4, arena.Motion.Feet);
            Assert.True(arena.DrawnMotion > 30, $"примитивов {arena.DrawnMotion}");

            // M — оверлей на экране; кадр с ним (снятие и построение) — без выделений.
            arena.Input(Pressed(ArenaInput.DebugMotion));
            Assert.True(arena.ShowMotion);
            long bytes = 0;
            void Measured(RenderFrame frame)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                arena.BuildFrame(frame);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (int i = 0; i < 12; i++)
            {
                if (i == 6) bytes = 0;   // первые кадры — прогрев буферов
                arena.Input(Pressed());
                clock.RunFrame(0);
                long before = GC.GetAllocatedBytesForCurrentThread();
                arena.Extract(new FrameView(0.5f, 1, 1f / 120, default));
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                _window.PumpMessages();
                renderer.Render(Measured, vsync: false);
            }
            Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "debug.bodies");
            Assert.Equal(0, bytes);
            Assert.True(arena.DrawnMotion > 30);
            arena.Input(Pressed(ArenaInput.DebugMotion));
            Assert.False(arena.ShowMotion);

            // Игрок клипами (locomotion.player = clips): вида нет — оверлей не снимается (слой — пуст с заметкой NoMotionNote), вкладки F3 нет.
            var plain = new SimulationLink(SwordSimulation.Create(ConfigSet.Empty("arena.enemies=0"), assets));
            var shown = new ArenaView(Sword.Game.Arena.ArenaTerrain.Height);
            shown.Extract(plain, 1);
            var overlay = new MotionMatchOverlay(Sword.Game.Arena.ArenaTerrain.Height);
            overlay.Extract(plain.World, shown);
            Assert.False(overlay.Available);
            Assert.Equal(0, overlay.Points);
            Assert.Null(LocomotionTuning.Build(new TuningBinding(plain), plain, shown));
            Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
