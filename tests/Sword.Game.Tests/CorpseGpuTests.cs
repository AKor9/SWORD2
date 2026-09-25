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
/// Труп на экране (7и) на настоящем устройстве (слой отладки D3D12 с GPU-валидацией) и настоящем контенте (Manny из пака): враг убит
/// командой сцены и падает (труп на физике, 7з), его тела сдвинуты («сдвинуть тела») — скин трупа идёт за телами; ID-буфер: пиксель головы
/// трупа — его сущность и сегмент «голова», легенда — «голова трупа врага 1», меч на теле клинка — «клинок трупа врага 1», все пиксели
/// трупа — в его рамке по телам; слои снимка капсул, суставов и контактов сняты, у пустого — заметка (трасса выключена; суставы трупа с 7з
/// есть); K включает трассу физики и выключает только включённую сам; кадр с трупами и всеми оверлеями на экране — 0 Б. 7и, часть 2 — вкладки
/// F3 «Бой» и «Рэгдолл»; труп лёг (300 тиков после смерти, трасса по K): слои с данными — у капсул трупа кресты тел на физике (14, на земле
/// — часть), суставов 13, контакты с землёй есть, заметок нет; «клинок трупа» — в ID-буфере. Любое предупреждение D3D12 — провал.
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

    private static (int X, int Y) Pixel(in Matrix4x4 viewProj, Vector3 p)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        return ((int)((c.X / c.W * 0.5f + 0.5f) * W), (int)((0.5f - c.Y / c.W * 0.5f) * H));
    }

    /// <summary>Нажатие действия в этом кадре (удержание с первого кадра).</summary>
    private static FrameInput Pressed(params InputAction[] actions) =>
        new(new InputState(), InputBindings.Resolve(ArenaInput.All, null), actions.Select(a => a.Name).ToHashSet(), true, true, W, H, 1f / 60);

    [Fact]
    public void CorpseSkin_IdBufferAndOverlays_TraceByKey()
    {
        // 7з-4: сон — позже (10 с): контакты последнего подшага рисует только неспящий труп, а жёсткий труп с самоколлизией засыпает до тика 300
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
            Assert.Equal(["Бой", "Рэгдолл"], arena.TuningGroups.Select(g => g.Title));   // вкладки F3 (7и, часть 2)
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
            string ids = Path.Combine(_dir, "ids.png");
            CaptureLayer Layer(string name) => new(name, Path.Combine(_dir, name + ".png"));
            var capture = new FrameCapture(Path.Combine(_dir, "color.png"))
            {
                IdsPath = ids,
                Layers = [Layer(ArenaClient.CapsulesOverlay), Layer(ArenaClient.JointsOverlay), Layer(ArenaClient.ContactsOverlay)],
            };
            arena.Extract(new FrameView(1, 0, 0, default) { Capture = capture });
            var shown = arena.View.Fighters.Single(f => f.Entity == corpse);
            Assert.True(shown.Dead && shown.Corpse);
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, capture);
            var result = renderer.LastCapture!;
            Assert.Equal(3, result.Layers.Count);
            Assert.All(result.Layers, l => Assert.True(File.Exists(l.Path)));
            // Слой без данных — с заметкой: трасса выключена. Суставы у трупа есть (13, переход в труп 7з) — заметки «суставов нет» нет.
            Assert.Contains(result.Notes, n => n.StartsWith("contacts: трасса физики выключена", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Notes, n => n.StartsWith("joints: суставов нет", StringComparison.Ordinal));
            Assert.Equal(BodySegs.Bodies * 4, arena.DrawnCapsules);

            var raw = File.ReadAllBytes(result.IdsRawPath!);
            uint IdAt(int x, int y) => BitConverter.ToUInt32(raw, (y * W + x) * 4);
            uint owner = ArenaClient.OwnerOf(corpse);
            // Пиксель в центре головы трупа (показанной капсулы) — его сущность и сегмент «голова».
            var head = arena.View.Body(shown)[BodySegs.Body(BodySegs.Head)];
            var (hx, hy) = Pixel(arena.Camera.ViewProj, head.Center);
            Assert.InRange(hx, 0, W - 1);
            Assert.InRange(hy, 0, H - 1);
            Assert.Equal(SkinnedMeshes.IdOf(owner, BodySegs.Head), IdAt(hx, hy));
            var observable = (IClientObservable)arena;
            Assert.Equal(new IdMeaning(corpse, "enemy", "head", "голова трупа врага 1"), observable.DescribeId(IdAt(hx, hy)));
            // Меч трупа — на теле клинка: его пиксели — сегмент 11 трупа.
            var blade = result.Ids.Single(c => c.Id == SkinnedMeshes.IdOf(owner, BodySegs.Weapon));
            Assert.Equal("клинок трупа врага 1", observable.DescribeId(blade.Id)!.Value.Title);

            // Все пиксели трупа — в его рамке (рамка по показанным телам, спроецированная камерой; ±2 пикселя).
            var visible = new List<VisibleEntity>();
            observable.Visible(visible);
            var box = visible.Single(v => v.Entity == corpse);
            Assert.Equal((shown.BoundsMin, shown.BoundsMax), (box.Min, box.Max));
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? box.Min.X : box.Max.X, (k & 2) == 0 ? box.Min.Y : box.Max.Y, (k & 4) == 0 ? box.Min.Z : box.Max.Z);
                var (px, py) = Pixel(arena.Camera.ViewProj, corner);
                (x0, y0, x1, y1) = (Math.Min(x0, px), Math.Min(y0, py), Math.Max(x1, px), Math.Max(y1, py));
            }
            foreach (var c in result.Ids.Where(c => SkinnedMeshes.SplitId(c.Id).Owner == owner))
                Assert.True(c.X0 >= x0 - 2 && c.Y0 >= y0 - 2 && c.X1 <= x1 + 3 && c.Y1 <= y1 + 3,
                    $"код {c.Id} ({observable.DescribeId(c.Id)?.Title}) [{c.X0},{c.Y0},{c.X1},{c.Y1}) вне рамки трупа [{x0},{y0},{x1},{y1}]");

            // K — контакты на экран и трасса физики: включает, выключает только включённую сам; включённую агентом оставляет.
            const string trace = IPhysicsDebugView.TraceMode;
            arena.Input(Pressed(ArenaInput.DebugContacts));
            Assert.True(arena.ShowContacts && sim.Diagnostics.IsEnabled(trace));
            arena.Input(Pressed(ArenaInput.DebugContacts));
            Assert.False(arena.ShowContacts || sim.Diagnostics.IsEnabled(trace));
            sim.Diagnostics.Set(trace, true);   // агент
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
            Assert.Equal(BodySegs.Bodies, arena.DrawnMarks);   // крест — у каждого тела трупа (живые — кинематические)

            // Труп лёг (7и, часть 2): 300 тиков после смерти, трасса физики включена K (ShowContacts). Слои снимка — с данными.
            Frames(300);
            Assert.True(sim.Diagnostics.IsEnabled(trace));
            var lying = new FrameCapture(Path.Combine(_dir, "lying.png"))
            {
                IdsPath = Path.Combine(_dir, "lying-ids.png"),
                Layers = [Layer(ArenaClient.CapsulesOverlay), Layer(ArenaClient.JointsOverlay), Layer(ArenaClient.ContactsOverlay)],
            };
            arena.Extract(new FrameView(1, 0, 0, default) { Capture = lying });
            _window.PumpMessages();
            renderer.Render(arena.BuildFrame, vsync: false, lying);
            var after = renderer.LastCapture!;
            Assert.Equal(3, after.Layers.Count);
            Assert.Empty(after.Notes);   // трасса включена, суставы есть
            Assert.Equal(BodySegs.Bodies, arena.DrawnMarks);
            Assert.InRange(arena.DrawnGroundedMarks, 3, BodySegs.Bodies);   // лежит: кресты касающихся земли — зелёные
            Assert.Equal(13, arena.DrawnJoints);
            var body = sim.World.Get<BodyRef>(corpse);
            Assert.True(arena.View.Contacts.ToArray().Count(c => c.Source == PhysicsContactSource.Ground && c.Body.Index == body.Index) >= 3,
                "у лежащего трупа нет контактов с землёй");
            Assert.True(arena.DrawnContacts >= 3);
            Assert.Equal((1, BodySegs.Bodies), (arena.View.CorpseStats.Corpses, arena.View.CorpseStats.SimulatedBodies));
            Assert.Contains(after.Ids, c => c.Id == SkinnedMeshes.IdOf(owner, BodySegs.Weapon));   // выпавший меч виден — «клинок трупа»
            Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
        }
        finally
        {
            renderer.Dispose();
        }
    }
}
