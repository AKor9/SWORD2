using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Platform;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Арена и враги, точка 4а: спаун кольцом курсом на игрока (<c>DuelArena.Spawn</c> SWORD), число врагов в пределах
/// (<c>SetEnemyCount</c>), сброс (<c>Reset</c>), клавиши PageUp/PageDown/R — команды арены ровно одним тиком и в реплее,
/// детерминизм, инвариант мест, 0 байт на тик с 32 врагами, имена и команды агента.
/// </summary>
public sealed class ArenaTests
{
    private static Simulation Create(params string[] sets) => TestAssets.Simulation(ConfigSet.Empty(sets));

    private sealed record EnemyRow(int Slot, Entity Entity, Transform Root, PrevTransform Built, FighterCommand Intent, float Seed);

    private static List<EnemyRow> Enemies(Simulation sim)
    {
        var w = sim.World;
        var rows = new List<EnemyRow>();
        foreach (var chunk in w.Iterate(w.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities)
                rows.Add(new EnemyRow(w.Get<FighterSlot>(e).Index, e, w.Get<Transform>(e), w.Get<PrevTransform>(e), w.Get<FighterCommand>(e),
                    w.Get<FighterSeed>(e).Value));
        return [.. rows.OrderBy(r => r.Slot)];
    }

    private static int FighterCount(Simulation sim)
    {
        int n = 0;
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<Fighter>())) n += chunk.Count;
        return n;
    }

    private static void Step(Simulation sim, params ArenaCommand[] commands)
    {
        foreach (var c in commands) sim.Enqueue(c);
        sim.Export.BeginFrame();
        sim.Step();
    }

    private static ArenaCommand Change(int n) => new() { Action = ArenaAction.ChangeEnemies, Enemies = n };
    private static ArenaCommand Set(int n) => new() { Action = ArenaAction.SetEnemies, Enemies = n };
    private static readonly ArenaCommand Reset = new() { Action = ArenaAction.Reset };

    /// <summary>
    /// Враги построены кольцом 5–9 м вокруг <paramref name="center"/>, курсом на него (формула SWORD побитово). Точка
    /// постройки — <see cref="PrevTransform"/>: построенный командой в тике враг уже сделал в нём шаг (ИИ, точка 4б), а
    /// прошлый корень — точка постройки (<c>RenderPos = Pos</c>). <paramref name="thought"/> — тик уже сыгран: намерение —
    /// ИИ на центр; нет (сборка мира) — «стоять курсом постройки» (<c>FacingGoal = facing</c>, <c>Fighter.Build</c>).
    /// </summary>
    private static void AssertRing(IReadOnlyList<EnemyRow> enemies, Vector3 center, bool thought)
    {
        foreach (var r in enemies)
        {
            var p = r.Built.Position;
            float d = Vector2.Distance(new Vector2(p.X, p.Z), new Vector2(center.X, center.Z));
            Assert.InRange(d, ArenaRules.SpawnMin - 1e-4f, ArenaRules.SpawnMax + 1e-4f);
            Assert.Equal(0, p.Y);
            Assert.Equal(MathF.Atan2(center.X - p.X, center.Z - p.Z), r.Built.Facing);
            if (thought)
            {
                Assert.True(MathF.Abs(r.Built.Facing - r.Intent.FacingGoal) < 1e-5f, $"курс-цель {r.Intent.FacingGoal} не на центр ({r.Built.Facing})");
                Assert.True(r.Intent.DesiredVelocity.Length() > 1, $"враг не идёт к центру: {r.Intent.DesiredVelocity}");
            }
            else
            {
                Assert.Equal(r.Root.Position, p);
                Assert.Equal(r.Root.Facing, r.Intent.FacingGoal);        // FacingGoal = facing (Fighter.Build)
                Assert.Equal(Vector3.Zero, r.Intent.DesiredVelocity);
            }
            Assert.InRange(r.Seed, 0f, 0.99999994f);
        }
    }

    [Fact]
    public void WorldBuild_SpawnsEnemiesInRing_FacingPlayer_WithFullFighter()
    {
        var sim = Create("arena.enemies=32");
        var w = sim.World;
        Assert.Equal(32, w.Resource<ArenaState>().Enemies);
        Assert.Equal(33, FighterCount(sim));
        var enemies = Enemies(sim);
        Assert.Equal(Enumerable.Range(1, 32), enemies.Select(r => r.Slot));
        AssertRing(enemies, Vector3.Zero, thought: false);
        Assert.True(enemies.Select(r => r.Seed).Distinct().Count() > 30, "зёрна врагов почти все одинаковые — ГСЧ по месту не работает?");

        // У врага — всё, что у игрока (движение, анимация, поза); место игрока — 0.
        var player = GameCore.Player(w);
        Assert.Equal(0, w.Get<FighterSlot>(player).Index);
        var view = w.View<IAnimationView>();
        foreach (var e in enemies.Select(r => r.Entity).Append(player))
        {
            Assert.True(w.Has<Fighter>(e) && w.Has<Transform>(e) && w.Has<PrevTransform>(e) && w.Has<RootVelocity>(e)
                && w.Has<FighterCommand>(e) && w.Has<GazeTarget>(e) && w.Has<AnimatorMotion>(e) && w.Has<PoseRef>(e)
                && w.Has<FighterSlot>(e) && w.Has<FighterSeed>(e), $"{e}: не полный набор бойца");
            Assert.Equal(w.Get<Transform>(e).Position, w.Get<PrevTransform>(e).Position);
            Assert.False(view.Model(w.Get<PoseRef>(e)).IsEmpty);
        }

        // Враги идут к игроку (ИИ — точка 4б), локомоция у них идёт: состояние модуля.
        for (int t = 0; t < 60; t++) Step(sim);
        Assert.All(enemies.Zip(Enemies(sim)), p => Assert.True(p.Second.Root.Position.Length() < p.First.Root.Position.Length() - 0.5f,
            $"место {p.First.Slot}: {p.First.Root.Position} → {p.Second.Root.Position} — не подошёл к игроку"));
        Assert.All(enemies, r => Assert.True(w.Has<Locomotion.LegacyLocomotionState>(r.Entity)));
        Assert.Equal(33 * 3, view.PoseCount);   // у каждого бойца — итоговая поза, слой и снимок локомоции
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Spawn_NearEdge_ClampedToArenaRadiusMinus3()
    {
        var rng = new SimRandom(1);
        var center = new Vector3(60, 0, 0);
        float lim = ArenaLayout.ArenaRadius - ArenaRules.SpawnEdgeMargin;
        int clamped = 0;
        for (uint batch = 0; batch < 20; batch++)
            for (int slot = 1; slot <= ArenaState.MaxEnemies; slot++)
            {
                var s = ArenaRules.Enemy(rng, 7, batch, slot, center);
                float r = new Vector2(s.Position.X, s.Position.Z).Length();
                Assert.True(r <= lim + 1e-4f, $"{r} > {lim}");
                if (MathF.Abs(r - lim) < 1e-4f) clamped++;
                Assert.Equal(MathF.Atan2(center.X - s.Position.X, center.Z - s.Position.Z), s.Facing);
            }
        Assert.True(clamped > 50, $"на краю только {clamped} спаунов — предел радиуса не срабатывает?");
        // Партия и место меняют числа, тик тоже; то же (зерно, тик, партия, место) — то же.
        Assert.Equal(ArenaRules.Enemy(rng, 7, 1, 3, center), ArenaRules.Enemy(rng, 7, 1, 3, center));
        Assert.NotEqual(ArenaRules.Enemy(rng, 7, 1, 3, center), ArenaRules.Enemy(rng, 7, 2, 3, center));
        Assert.NotEqual(ArenaRules.Enemy(rng, 7, 1, 3, center), ArenaRules.Enemy(rng, 8, 1, 3, center));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArenaRules.Enemy(rng, 0, 0, 0, center));
    }

    [Fact]
    public void Constants_AreSwordLiterals()
    {
        // DuelArena SWORD 2eab13b: MaxFighters = 33, SpawnMin = 5f, SpawnMax = 9f, ArenaLayout.ArenaRadius − 3.
        Assert.Equal(33, ArenaRules.MaxFighters);
        Assert.Equal(32, ArenaState.MaxEnemies);
        Assert.Equal((5f, 9f, 3f), (ArenaRules.SpawnMin, ArenaRules.SpawnMax, ArenaRules.SpawnEdgeMargin));
    }

    [Fact]
    public void Config_Enemies_DefaultEmpty_OutOfRangeIsError()
    {
        var empty = Create();
        Assert.Equal(0, empty.World.Resource<ArenaState>().Enemies);
        Assert.Equal(1, FighterCount(empty));
        foreach (var bad in new[] { "arena.enemies=33", "arena.enemies=-1" })
            Assert.Contains("arena.enemies", Assert.Throws<InvalidDataException>(() => Create(bad)).Message);
    }

    [Fact]
    public void Commands_ChangeAndSet_ClampToZeroAnd32_LastPlacesLeave()
    {
        var sim = Create("arena.enemies=10");
        var view = sim.World.View<IAnimationView>();
        Step(sim);
        var before = Enemies(sim);

        Step(sim, Change(1));   // PageUp: новое место 11, прежние на месте
        var after = Enemies(sim);
        Assert.Equal(11, sim.World.Resource<ArenaState>().Enemies);
        Assert.Equal(Enumerable.Range(1, 11), after.Select(r => r.Slot));
        Assert.Equal(before.Select(r => (r.Entity, r.Seed)), after.Take(10).Select(r => (r.Entity, r.Seed)));   // прежние не перестроены
        AssertRing([after[^1]], sim.World.Get<Transform>(GameCore.Player(sim.World)).Position, thought: true);
        Assert.Equal(12 * 3, view.PoseCount);

        Step(sim, Change(-1), Change(-1));   // PageDown дважды в одном тике: уходят места 11 и 10
        var fewer = Enemies(sim);
        Assert.Equal(Enumerable.Range(1, 9), fewer.Select(r => r.Slot));
        Assert.False(sim.World.IsAlive(after[^1].Entity) || sim.World.IsAlive(after[^2].Entity));
        Assert.Equal(10 * 3, view.PoseCount);   // позы ушедших вернулись в пул (ядро и локомоция)

        Step(sim, Set(100));
        Assert.Equal(32, sim.World.Resource<ArenaState>().Enemies);
        Assert.Equal(33, FighterCount(sim));
        Step(sim, Change(int.MaxValue));   // без переполнения
        Assert.Equal(32, sim.World.Resource<ArenaState>().Enemies);
        Step(sim, Change(int.MinValue));
        Assert.Equal(0, sim.World.Resource<ArenaState>().Enemies);
        Assert.Equal(1, FighterCount(sim));
        Step(sim, Set(-5), new ArenaCommand(), new ArenaCommand { Action = (ArenaAction)77, Enemies = 5 });   // пустая и чужая — ничего
        Assert.Equal(0, sim.World.Resource<ArenaState>().Enemies);

        // Убавили и прибавили в одном тике — места 2 и 3 строятся заново на тех же сущностях (черновик не создаётся и не
        // снимается), место 1 не трогается.
        Step(sim, Set(3));
        var three = Enemies(sim);
        Step(sim, Set(1), Set(3));
        var again = Enemies(sim);
        Assert.Equal(three.Select(r => r.Entity), again.Select(r => r.Entity));
        Assert.Equal(three[0].Seed, again[0].Seed);   // место 1 не строится заново
        Assert.NotEqual(three[1].Built.Position, again[1].Built.Position);
        Assert.NotEqual(three[2].Built.Position, again[2].Built.Position);
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Reset_PlayerToCenter_EnemiesAnew_SameEntities_PlayerKeepsTickCommandAndGaze()
    {
        var sim = Create("arena.enemies=5");
        for (int t = 0; t < 120; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0), HasGaze = true, Gaze = new Vector3(30, 0, 30) });
            Step(sim);
        }
        var oldPlayer = GameCore.Player(sim.World);
        var oldEnemies = Enemies(sim);
        Assert.True(sim.World.Get<Transform>(oldPlayer).Position.X > 3);

        Assert.True(sim.World.Get<Locomotion.LegacyLocomotionState>(oldPlayer).Run);
        int oldDir = sim.World.Get<Locomotion.LegacyLocomotionState>(oldPlayer).Dir;
        Assert.NotEqual(0, oldDir);   // бежит вбок от взгляда

        Step(sim, Reset);   // R, игрок стоит
        // Тот же боец места 0 (Fighter.Build SWORD над тем же бойцом): имя и номер у агента прежние.
        var player = GameCore.Player(sim.World);
        Assert.Equal(oldPlayer, player);
        var root = sim.World.Get<Transform>(player);
        Assert.Equal(Vector3.Zero, root.Position);
        Assert.Equal(Vector3.Zero, sim.World.Get<RootVelocity>(player).Value);
        // Точка взгляда контроллера (_cursor SWORD) сбросом не забыта: курс-цель — на неё, курс к ней поворачивает.
        Assert.True(sim.World.Get<GazeTarget>(player).Has);
        Assert.Equal(MathF.Atan2(30, 30), sim.World.Get<FighterCommand>(player).FacingGoal);
        Assert.Equal(0, sim.World.Get<FighterSlot>(player).Index);
        var enemies = Enemies(sim);
        Assert.Equal(Enumerable.Range(1, 5), enemies.Select(r => r.Slot));
        Assert.Equal(oldEnemies.Select(r => r.Entity), enemies.Select(r => r.Entity));
        Assert.All(enemies.Zip(oldEnemies), p => Assert.NotEqual(p.Second.Root.Position, p.First.Root.Position));
        AssertRing(enemies, Vector3.Zero, thought: true);
        Assert.Equal(6 * 3, sim.World.View<IAnimationView>().PoseCount);   // позы — прежние, ничего не утекло
        // Локомоция — заново со стойки, как после FighterAnimator.Reset SWORD: без кроссфейда, время клипа — один тик.
        var loco = sim.World.Get<Locomotion.LegacyLocomotionState>(player);
        Assert.Equal((false, 1f, 1f / 120), (loco.Run, loco.Fade, loco.Time));
        // …кроме стороны ходьбы: LegacyLocomotion.Reset SWORD не трогает _dir (найдено трассой толпы 4д).
        Assert.Equal(oldDir, loco.Dir);

        // Команда игрока тика сброса действует сразу (в SWORD Reset — до PlayerController.Think того же тика).
        sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
        Step(sim, Reset);
        var moved = GameCore.Player(sim.World);
        Assert.Equal(MovementRulesSpeedAfterOneTick(), sim.World.Get<RootVelocity>(moved).Value.Z, 5);
        Assert.Equal(0, sim.Invariants.Total);
    }

    /// <summary>Разгон за один тик с места до бега: 4,2 · min(1, 14/120).</summary>
    private static float MovementRulesSpeedAfterOneTick() => Movement.MovementRules.RunSpeed * MathF.Min(1, Movement.MovementRules.Accel / 120f);

    [Fact]
    public void Keys_BecomeArenaCommands_ExactlyOneTick_RecordedAndReplayed()
    {
        var bindings = InputBindings.Resolve(ArenaInput.All, null);
        var edges = new InputEdges(bindings);
        var client = new ArenaClient(scripted: new PlayerCommand());
        var sim = Create("arena.enemies=10");
        var recorder = sim.Record(new ReplayInfo("test", "tests", "none"));
        var clock = new SimulationClock(sim, (link, input) => client.BeforeTick(link, input), ticksPerFrame: 3, edges);

        // Кадр, в котором нажаты PageUp и R (удержание нажимает их в первом кадре), — три тика.
        var held = new HashSet<string> { ArenaInput.EnemiesMore.Name, ArenaInput.ArenaReset.Name };
        edges.Note(new FrameInput(new InputState(), bindings, held, heldPressed: true, true, 640, 480, 1f / 40));
        clock.RunFrame(0);
        edges.Note(new FrameInput(new InputState(), bindings, held, heldPressed: false, true, 640, 480, 1f / 40));
        clock.RunFrame(0);
        Assert.Equal(6ul, sim.Tick);
        Assert.Equal(11, sim.World.Resource<ArenaState>().Enemies);

        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        int arenaType = replay.CommandTypes.ToList().FindIndex(t => t.Name == typeof(ArenaCommand).FullName);
        Assert.True(arenaType >= 0, "команды арены нет в реплее");
        Assert.Equal(["Action:u8", "Enemies:i32"], replay.CommandTypes[arenaType].Fields);
        var first = replay.CommandsAt(0).Where(c => c.Type == arenaType).Select(c => ((ArenaAction)c.Leaves[0], (int)c.Leaves[1])).ToList();
        Assert.Equal([(ArenaAction.ChangeEnemies, 1), (ArenaAction.Reset, 0)], first);   // порядок SWORD: PageUp, затем R
        for (ulong t = 1; t < 6; t++) Assert.DoesNotContain(replay.CommandsAt(t), c => c.Type == arenaType);

        var b = TestAssets.Simulation(replay.CreateConfig());
        var player = b.Play(replay);
        while (b.Tick < replay.Ticks) b.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(7, player.HashesChecked);
        Assert.Equal(sim.HashState().Total, b.HashState().Total);
        Assert.Equal(11, b.World.Resource<ArenaState>().Enemies);
    }

    [Fact]
    public void ReplayMode_ClientSendsNoArenaCommands()
    {
        var bindings = InputBindings.Resolve(ArenaInput.All, null);
        var edges = new InputEdges(bindings);
        var client = new ArenaClient(playerCommands: false);
        var sim = Create("arena.enemies=4");
        var clock = new SimulationClock(sim, (link, input) => client.BeforeTick(link, input), ticksPerFrame: 1, edges);
        edges.Note(new FrameInput(new InputState(), bindings, new HashSet<string> { ArenaInput.EnemiesMore.Name }, heldPressed: true, true, 640, 480, 1f / 60));
        clock.RunFrame(0);
        Assert.Equal(4, sim.World.Resource<ArenaState>().Enemies);
    }

    /// <summary>Сценарий: бег, PageUp, бег со взглядом, R, PageDown ×2, установить 32, стоять.</summary>
    private static void Script(Simulation sim, int t)
    {
        if (t < 200) sim.Enqueue(new PlayerCommand { Move = t < 100 ? new Vector2(1, 0) : new Vector2(0, -1), HasGaze = t > 50, Gaze = new Vector3(10, 0, 10) });
        switch (t)
        {
            case 30: sim.Enqueue(Change(1)); break;
            case 120: sim.Enqueue(Reset); break;
            case 150: sim.Enqueue(Change(-1)); sim.Enqueue(Change(-1)); break;
            case 220: sim.Enqueue(Set(32)); break;
        }
        sim.Export.BeginFrame();
        sim.Step();
    }

    [Fact]
    public void TwoRuns_WithArenaCommands_SameHashEveryTick_ReplayRepeats_SeedMatters()
    {
        var a = Create("arena.enemies=10");
        var b = Create("arena.enemies=10");
        var recorder = a.Record(new ReplayInfo("test", "tests", "none"));
        for (int t = 0; t < 300; t++)
        {
            Script(a, t);
            Script(b, t);
            var ha = a.HashState();
            var hb = b.HashState();
            Assert.True(ha.Total == hb.Total, $"тик {t}: {ha} ≠ {hb}");
        }
        Assert.Equal(32, a.World.Resource<ArenaState>().Enemies);
        Assert.Equal(0, a.Invariants.Total);

        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        Assert.Contains("arena", replay.Config.ToJsonString());
        var c = TestAssets.Simulation(replay.CreateConfig());
        var played = c.Play(replay);
        while (c.Tick < replay.Ticks) c.Step();
        Assert.Null(played.Divergence);
        Assert.Equal(301, played.HashesChecked);

        // Другое главное зерно — другая расстановка (ГСЧ — счётчиковый, от sim.seed).
        var other = Create("arena.enemies=10", "sim.seed=2");
        Assert.NotEqual(Enemies(Create("arena.enemies=10")).Select(r => r.Root.Position), Enemies(other).Select(r => r.Root.Position));
    }

    [Fact]
    public void SteadyTick_With32Enemies_DoesNotAllocate()
    {
        var sim = Create("arena.enemies=32");
        var command = new PlayerCommand { Move = new Vector2(1, 1), HasGaze = true, Gaze = new Vector3(5, 0, 5) };
        for (int i = 0; i < 200; i++) { sim.Export.BeginFrame(); sim.Enqueue(command); sim.Step(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 600; i++) { sim.Export.BeginFrame(); sim.Enqueue(command); sim.Step(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, sim.Invariants.Total);
    }

    /// <summary>Неисправность мест на тике <see cref="Tick"/> (Settle): снять врага места 3 мимо команды арены или дать ему место 2.</summary>
    private sealed class SlotFault(bool duplicate) : IModule
    {
        public const ulong Tick = 25;

        public string Id => "slot_fault";

        public void Configure(SimulationBuilder b) => b.System(GameStages.Settle, new System(duplicate));

        private sealed class System(bool duplicate) : IContinuousSystem
        {
            private Query _query = null!;

            public void Setup(SystemSetup s)
            {
                s.Label(new SystemLabel("fault.slot")).Writes<FighterSlot>().Destroys();
                _query = s.Query().With<EnemyTag>().Write<FighterSlot>();
            }

            public void Run(in SystemContext ctx)
            {
                if (ctx.Tick != Tick) return;
                foreach (var chunk in ctx.Iterate(_query))
                {
                    var slots = chunk.Write<FighterSlot>();
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i].Index == 3)
                        {
                            if (duplicate) slots[i].Index = 2;
                            else ctx.Commands.Destroy(chunk.Entities[i]);
                        }
                }
            }
        }
    }

    [Theory]
    [InlineData(false, "место 3 пусто")]
    [InlineData(true, "место 2 — ещё и у")]
    public void SlotsInvariant_CatchesHoleAndRepeat_OnItsTick(bool duplicate, string details)
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=5"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. SwordSimulation.Modules(), new SlotFault(duplicate)]);
        Assert.Contains(sim.Invariants.List(), i => i.Id == "arena.slots" && i.Enabled);
        for (int t = 0; t < (int)SlotFault.Tick; t++) Step(sim);
        Assert.Equal(0, sim.Invariants.Total);
        Step(sim);
        var first = sim.Invariants.Recent()[0];
        Assert.Equal(("arena.slots", SlotFault.Tick, GameStages.End), (first.Id, first.Tick, first.Stage));
        Assert.Contains(sim.Invariants.Recent(), v => v.Details.Contains(details));
    }

    [Fact]
    public async Task Agent_NamesEnemiesBySlot_SendsArenaCommand()
    {
        var sim = Create("arena.enemies=3");
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes), Time = new TimeControl(true, sim.Tick),
            Service = new ServiceQueue(), Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"),
            Stop = () => { }, ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonObject> Call(string tool, JsonObject? args = null)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] },
                };
                var r = JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
                Assert.False(r["isError"]?.GetValue<bool>() ?? false, r.ToJsonString());
                return r["structuredContent"]!.AsObject();
            }

            var third = await Call("entity.get", new JsonObject { ["entity"] = "enemy 3", ["components"] = new JsonArray("FighterSlot") });
            Assert.Equal("enemy 3", third["name"]!.GetValue<string>());
            await Call("command.send", new JsonObject
            {
                ["command"] = "ArenaCommand", ["fields"] = new JsonObject { ["Action"] = "SetEnemies", ["Enemies"] = 7 },
            });
            await Call("time.step", new JsonObject { ["ticks"] = 1 });
            var seventh = await Call("entity.get", new JsonObject { ["entity"] = "enemy 7" });
            Assert.Equal("enemy 7", seventh["name"]!.GetValue<string>());
            var arena = await Call("world.query", new JsonObject { ["with"] = new JsonArray("EnemyTag") });
            Assert.Equal(7, arena["total"]!.GetValue<int>());
            var hash = await Call("state.describe", new JsonObject { ["part"] = "ecs", ["filter"] = "resource:" });
            Assert.Contains("ArenaState", hash.ToJsonString());
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
        var names = new SwordAgent.ArenaNames();
        Assert.Contains("enemy <место 1…32>", names.Known);
    }
}
