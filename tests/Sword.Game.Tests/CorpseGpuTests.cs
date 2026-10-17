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

/// <summary>
/// Труп на устройстве и настоящем контенте: враг убит и падает, «сдвинуть тела» — скин идёт за телами; K включает трассу;
/// кадр с трупами и всеми оверлеями — 0 Б; вкладки F3 «Бой» и «Рэгдолл»; у лёгшего трупа кресты, 13 суставов и контакты с
/// землёй. Любое предупреждение D3D12 — провал.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class CorpseGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-corpse-{Guid.NewGuid():N}");

    public CorpseGpuTests()
    {
        Log.AddSink(this);
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.Corpse", W, H, Activate: false));
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

    /// <summary>Нажатие действия в этом кадре (удержание с первого кадра).</summary>
    private static FrameInput Pressed(params InputAction[] actions) =>
        new(new InputState(), InputBindings.Resolve(ArenaInput.All, null), actions.Select(a => a.Name).ToHashSet(), true, true, W, H, 1f / 60);

    [Fact]
    public void CorpseSkin_Overlays_TraceByKey()
    {
        // Сон — позже (10 с): контакты последнего подшага рисует только неспящий труп, а жёсткий труп с самоколлизией засыпает до тика 300
        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=3", "combat.enemiesAttack=false", "ragdoll.sleepTime=10"), CharacterAssets.LoadFromPak());
        var pending = new List<SceneCommand>();
        var clock = new SimulationClock(sim, (link, _) =>
        {
            foreach (var c in pending) link.Enqueue(c);
            pending.Clear();
        }, ticksPerFrame: 1);
        Entity corpse = default;
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == 1) corpse = chunk.Entities[i];
        var root = sim.World.Get<Transform>(corpse).Position;   // место врага 1 при сборке мира: убит через 4 тика — рядом
        // Камера (как --camera) — близко к трупу: голова — десятки пикселей.
        var arena = new ArenaClient(new CameraPose(0.6f, 0.7f, 5f, root.X + 0.25f, root.Z - 0.2f), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            Assert.Equal(["Бой", "Рэгдолл", "Расчленение", "Свет"], arena.TuningGroups.Select(g => g.Title));   // вкладки F3
            // Бойцы приходят очередью кадра — Extract каждый кадр.
            void Frames(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    clock.RunFrame(0);
                    arena.Extract(new FrameView(1, 1, 0, default));
                }
            }
            Frames(4);
            pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
            Frames(3);
            pending.Add(new SceneCommand { Action = SceneAction.ShiftBodies, Slot = 1, Offset = new Vector3(0.25f, 0, -0.2f) });
            Frames(1);
            Assert.True(sim.World.Has<Dead>(corpse));
            arena.Input(Pressed());
            var capture = new FrameCapture(Path.Combine(_dir, "color.png"));
            arena.Extract(new FrameView(1, 0, 0, default));
            var shown = arena.View.Fighters.Single(f => f.Entity == corpse);
            Assert.True(shown.Dead && shown.Corpse);
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, capture);
            Assert.True(File.Exists(capture.ColorPath));
            Assert.Equal(0, arena.DrawnCapsules);   // оверлеи не показаны — капсул нет

            // K — контакты на экран и трасса физики: включает, выключает только включённую сам; включённую не им оставляет.
            const string trace = IPhysicsDebugView.TraceMode;
            arena.Input(Pressed(ArenaInput.DebugContacts));
            Assert.True(arena.ShowContacts && sim.Diagnostics.IsEnabled(trace));
            arena.Input(Pressed(ArenaInput.DebugContacts));
            Assert.False(arena.ShowContacts || sim.Diagnostics.IsEnabled(trace));
            sim.Diagnostics.Set(trace, true);   // не клиент
            arena.Input(Pressed(ArenaInput.DebugContacts));
            arena.Input(Pressed(ArenaInput.DebugContacts));
            Assert.True(!arena.ShowContacts && sim.Diagnostics.IsEnabled(trace));
            sim.Diagnostics.Set(trace, false);

            // Кадр с трупом и всеми оверлеями на экране (F7, J, K) — один проход debug.bodies, построение кадра игры без выделений.
            arena.Input(Pressed(ArenaInput.DebugBodies, ArenaInput.DebugJoints, ArenaInput.DebugContacts));
            Assert.True(arena.ShowBodies && arena.ShowJoints && arena.ShowContacts);
            long bytes = 0;
            void Measured(RenderFrame frame)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                arena.BuildFrame(frame);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            for (int i = 0; i < 8; i++)
            {
                clock.RunFrame(0);
                arena.Extract(new FrameView(0.5f, 1, 1f / 120, default));
                if (i == 4) bytes = 0;
                _window.PumpMessages();
                renderer.Render(Measured, vsync: false);
            }
            Assert.Contains(renderer.Graph.LastFrame, p => p.Name == "debug.bodies");
            Assert.Equal(0, bytes);
            Assert.Equal(BodySegs.Bodies * 4, arena.DrawnCapsules);
            Assert.Equal(BodySegs.Bodies, arena.DrawnMarks);   // крест — у каждого тела трупа (живые — кинематические)

            // Труп лёг: 300 тиков после смерти, трасса физики включена K (ShowContacts); оверлеи на экране — с данными.
            Frames(300);
            Assert.True(sim.Diagnostics.IsEnabled(trace));
            arena.Extract(new FrameView(1, 0, 0, default));
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false);
            Assert.Equal(BodySegs.Bodies, arena.DrawnMarks);
            Assert.InRange(arena.DrawnGroundedMarks, 3, BodySegs.Bodies);   // лежит: кресты касающихся земли — зелёные
            Assert.Equal(13, arena.DrawnJoints);
            var body = sim.World.Get<BodyRef>(corpse);
            Assert.True(arena.View.Contacts.ToArray().Count(c => c.Source == PhysicsContactSource.Ground && c.Body.Index == body.Index) >= 3,
                "у лежащего трупа нет контактов с землёй");
            Assert.True(arena.DrawnContacts >= 3);
            Assert.Equal((1, BodySegs.Bodies), (arena.View.CorpseStats.Corpses, arena.View.CorpseStats.SimulatedBodies));
            Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
