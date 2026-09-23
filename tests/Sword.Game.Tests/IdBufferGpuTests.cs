using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>Тесты с устройством D3D12 в сборке тестов игры идут по одному: окно, устройство и журнал на процесс общие.</summary>
[CollectionDefinition("game-gpu", DisableParallelization = true)]
public sealed class GameGpuCollection;

/// <summary>
/// ID-проход и оверлей капсул снимка (5в) на настоящем устройстве (слой отладки D3D12 с GPU-валидацией) и настоящем
/// контенте (Manny из пака): кадр клиента арены с заказанным ID-буфером — пиксель в рамке бойца несёт его сущность и
/// сегмент тела (центр головы — <see cref="BodySegs.Head"/>), земля — 0; легенда называет код словами; слой капсул снят.
/// Любое предупреждение D3D12 — провал. Категории Gpu и Content: нужны видеокарта и пак.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class IdBufferGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-ids-{Guid.NewGuid():N}");

    public IdBufferGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.Ids", W, H, Activate: false));
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

    /// <summary>Точка мира → пиксель кадра (начало — левый верхний угол).</summary>
    private static (int X, int Y) Pixel(in Matrix4x4 viewProj, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        return ((int)((c.X / c.W * 0.5f + 0.5f) * W), (int)((0.5f - c.Y / c.W * 0.5f) * H));
    }

    [Fact]
    public void IdPass_PixelInFighterFrame_IsItsEntityAndSegment_OverlayLayerCaptured()
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=3"), CharacterAssets.LoadFromPak());
        var run = new PlayerCommand { Move = new Vector2(1, 0.3f) };
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(run), ticksPerFrame: 2);
        var player = GameCore.Player(sim.World);
        // 60 кадров по 2 тика: игрок бежит по (1; 0,3) со скоростью бега 4,2 м/с — камера (как --camera) близко к месту, где
        // он будет после разгона: голова — десятки пикселей.
        var goal = Vector2.Normalize(run.Move) * 3.6f;
        var arena = new ArenaClient(new CameraPose(0.6f, 0.7f, 6f, goal.X, goal.Y), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            for (int i = 0; i < 60; i++)
            {
                clock.RunFrame(0);
                arena.Extract(new FrameView(1, 2, 0, default));   // бойцы приходят очередью кадра — каждый кадр
            }
            string ids = Path.Combine(_dir, "ids.png"), layer = Path.Combine(_dir, "capsules.png");
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, new FrameCapture(Path.Combine(_dir, "color.png"))
            {
                IdsPath = ids, Layers = [new CaptureLayer(ArenaClient.CapsulesOverlay, layer)],
            });
            var result = renderer.LastCapture!;
            Assert.Empty(result.Notes);
            Assert.Equal((ids, Path.ChangeExtension(ids, ".bin")), (result.IdsPath, result.IdsRawPath));
            Assert.Equal([new CaptureLayer(ArenaClient.CapsulesOverlay, layer)], result.Layers);
            Assert.True(File.Exists(layer) && File.Exists(ids));
            Assert.Equal(arena.View.BodyCount, arena.DrawnCapsules);   // капсулы всех бойцов — в слое снимка

            var raw = File.ReadAllBytes(result.IdsRawPath!);
            Assert.Equal(W * H * 4, raw.Length);
            uint IdAt(int x, int y) => BitConverter.ToUInt32(raw, (y * W + x) * 4);

            // Пиксель в центре головы игрока — его сущность и сегмент «голова».
            var shown = arena.View.Player!.Value;
            var head = arena.View.Body(shown)[BodySegs.Body(BodySegs.Head)];
            var (hx, hy) = Pixel(arena.Camera.ViewProj, head.Center);
            Assert.InRange(hx, 0, W - 1);
            Assert.InRange(hy, 0, H - 1);
            uint owner = ArenaClient.OwnerOf(player);
            Assert.Equal(SkinnedMeshes.IdOf(owner, BodySegs.Head), IdAt(hx, hy));
            var meaning = ((IClientObservable)arena).DescribeId(IdAt(hx, hy));
            Assert.Equal(new IdMeaning(player, "player", "head", "голова игрока"), meaning);

            // Сводка кодов кадра: у игрока видны голова, торс и ноги; каждый код — владелец-боец кадра и сегмент с телом.
            var mine = result.Ids.Where(c => SkinnedMeshes.SplitId(c.Id).Owner == owner).Select(c => (int)SkinnedMeshes.SplitId(c.Id).Part).ToList();
            Assert.Contains(BodySegs.Head, mine);
            Assert.Contains(BodySegs.Torso, mine);
            Assert.Contains(mine, s => s is BodySegs.ThighL or BodySegs.ThighR or BodySegs.ShinL or BodySegs.ShinR);
            foreach (var c in result.Ids)
            {
                var m = ((IClientObservable)arena).DescribeId(c.Id);
                Assert.True(m is not null && m.Value.Part is not null && BodySegs.Find(m.Value.Part) >= 0, $"код {c.Id}: {m}");
                Assert.True(c.Pixels > 0 && c.X1 > c.X0 && c.Y1 > c.Y0);
            }
            // Рамка кода головы — вокруг пикселя головы; земля в углу кадра — 0.
            var headCode = result.Ids.Single(c => c.Id == SkinnedMeshes.IdOf(owner, BodySegs.Head));
            Assert.InRange(hx, headCode.X0, headCode.X1 - 1);
            Assert.InRange(hy, headCode.Y0, headCode.Y1 - 1);
            Assert.Equal(0u, IdAt(0, H - 1));
            Assert.Null(((IClientObservable)arena).DescribeId(0));

            // Проход ID — только в кадре снимка, который его заказал: обычный кадр его не рисует.
            Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "scene.ids");
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
            Assert.DoesNotContain(renderer.Graph.LastFrame, p => p.Name is "scene.ids" or "overlay.capsules" or "debug.bodies");

            // F7: капсулы поверх кадра проходом debug.bodies после сцены; построение кадра игры с капсулами 4 бойцов (52
            // капсулы: накопление отладочного рисования и регистрация прохода) — без выделений в установившемся кадре.
            arena.ShowBodies = true;
            long bytes = 0;
            void Measured(RenderFrame frame)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                arena.BuildFrame(frame);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (int i = 0; i < 8; i++)
            {
                if (i == 4) bytes = 0;   // первые кадры — прогрев (конвейеры, пул графа)
                _window.PumpMessages();
                renderer.Render(Measured, vsync: false);
            }
            Assert.Equal(4 * BodySegs.Bodies, arena.DrawnCapsules);
            Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "debug.bodies" && p.Draws == 1);
            Assert.Equal(0, bytes);
            Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
