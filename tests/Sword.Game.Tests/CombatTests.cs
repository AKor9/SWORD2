using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Formats;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>
/// Подготовка и оружие: таблица оружия (мечи пака по алфавиту, клинок по мешу), меч в руке (враг — случайный новой
/// выборкой потока спауна, игрок — <c>combat.playerWeapon</c>), инвентарь и T, клинок каждого тика (на кисти, прошлое — прошлый
/// тик, смена меча — без прохода), инварианты <c>combat.*</c> на неисправностях, команды арены в Begin, таблица ударов
/// (<see cref="StrikeWindows"/>), инструмент <c>combat.weapons</c>, поиск Sword.exe сервером агента.
/// </summary>
public sealed class CombatTests
{
    private static Simulation Create(params string[] sets) => TestAssets.Simulation(ConfigSet.Empty(sets));

    private static void Step(Simulation sim, int ticks, PlayerCommand command = default)
    {
        for (int t = 0; t < ticks; t++)
        {
            sim.Enqueue(command);
            sim.Step();
        }
    }

    [Fact]
    public void WeaponTable_PakWeaponsByName_BladeFromFarthestVertex()
    {
        var weapons = TestAssets.Assets.Weapons;
        Assert.Equal(["katana", "sword_1h"], weapons.Weapons.ToArray().Select(w => w.Name));   // порядок списка контента
        Assert.Equal(1, weapons.Default);                                                       // sword_1h — меч по умолчанию
        Assert.Equal((0, 1, -1), (weapons.Find("KATANA"), weapons.Find("sword_1h"), weapons.Find("axe")));
        var sword = weapons.Weapons[1];
        Assert.Equal("weapons/sword_1h.swmesh", sword.Mesh);
        Assert.Equal(TestAssets.SwordTip, sword.BladeTip);                                      // самая дальняя вершина
        Assert.Equal(Vector3.Normalize(TestAssets.SwordTip) * WeaponMesh.BaseOffset, sword.BladeBase);   // 22 см по той же оси
        Assert.Equal(TestAssets.KatanaTip, weapons.Weapons[0].BladeTip);
    }

    [Fact]
    public void WeaponMesh_WithJointsOrWithoutBlade_NoWeapons_GameDoesNotStart()
    {
        static Stream Mesh(SkinnedMeshData m)
        {
            var s = new MemoryStream();
            m.Write(s);
            s.Position = 0;
            return s;
        }
        var skinned = TestAssets.Weapon(TestAssets.SwordTip);
        skinned.JointNames = ["hand_r"];
        skinned.JointParents = [-1];
        skinned.InverseBind = [Matrix4x4.Identity];
        Assert.Contains("суставов 1", Assert.Throws<InvalidDataException>(() => WeaponMesh.Read(Mesh(skinned), "w")).Message);
        Assert.Contains("клинка нет", Assert.Throws<InvalidDataException>(() => WeaponMesh.Read(Mesh(new SkinnedMeshData { Vertices = [default, default, default], Indices = [0, 1, 2] }), "w")).Message);
        Assert.Throws<FileNotFoundException>(() => CharacterAssets.Load(TestAssets.Content(weapons: false)));
    }

    [Fact]
    public void PlayerWeapon_FromConfig_DefaultSword1h_UnknownIsConfigError()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Assert.Equal(1, sim.World.Get<WeaponSlot>(player).Index);
        var inventory = sim.World.Resource<PlayerInventory>();
        Assert.Equal((0b11UL, 1), (inventory.Owned, inventory.Equipped));                      // всё оружие пака, выбран sword_1h
        var katana = Create("combat.playerWeapon=KATANA");
        Assert.Equal(0, katana.World.Get<WeaponSlot>(GameCore.Player(katana.World)).Index);
        var error = Assert.Throws<InvalidDataException>(() => Create("combat.playerWeapon=axe"));
        Assert.Contains("combat.playerWeapon", error.Message);
        Assert.Contains("katana, sword_1h", error.Message);
    }

    [Fact]
    public void EnemyWeapon_RandomNewSample_SpawnNotShifted_Deterministic()
    {
        var rng = new SimRandom(1);
        var center = new Vector3(2, 0, -3);
        var seen = new HashSet<int>();
        for (uint batch = 0; batch < 4; batch++)
            for (int slot = 1; slot <= ArenaState.MaxEnemies; slot++)
            {
                var a = ArenaRules.Enemy(rng, 5, batch, slot, center, [0, 1]);
                var b = ArenaRules.Enemy(rng, 5, batch, slot, center, [.. Enumerable.Range(0, 64)]);
                // Меч — своя выборка: корень, курс и зерно от оружия врагов не зависят.
                Assert.Equal((a.Position, a.Facing, a.Seed), (b.Position, b.Facing, b.Seed));
                Assert.InRange(a.Weapon, 0, 1);
                seen.Add(a.Weapon);
            }
        Assert.Equal([0, 1], seen.Order());
        Assert.True(ArenaRules.WeaponSampleBase > (20u * ArenaRules.MaxFighters + 32) * 3 + 2);   // выше прежних выборок

        var one = Create("arena.enemies=32");
        var two = Create("arena.enemies=32");
        int[] Weapons(Simulation s) => [.. EnemiesBySlot(s).Select(e => s.World.Get<WeaponSlot>(e).Index)];
        Assert.Equal(Weapons(one), Weapons(two));
        Assert.Equal([0, 1], Weapons(one).Distinct().Order());   // у врагов разные мечи
        // Сброс строит врагов заново — со своими новыми мечами той же выборкой партии команды.
        one.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        two.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        one.Step();
        two.Step();
        Assert.Equal(Weapons(one), Weapons(two));
        Assert.Equal(0, one.Invariants.Total);
    }

    private static List<Entity> EnemiesBySlot(Simulation sim)
    {
        var list = new List<(int Slot, Entity E)>();
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++) list.Add((chunk.Read<FighterSlot>()[i].Index, chunk.Entities[i]));
        return [.. list.OrderBy(x => x.Slot).Select(x => x.E)];
    }

    [Fact]
    public void T_NextWeapon_CyclesInventory_SwitchHasNoSweep_ResetKeepsWeapon()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 10, new PlayerCommand { Move = new Vector2(0, 1) });
        var before = sim.World.Get<Blade>(player);
        Assert.Equal(1, before.Weapon);
        Assert.NotEqual(before.Tip, before.PrevTip);   // бежит — кончик движется

        sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });
        Step(sim, 1, new PlayerCommand { Move = new Vector2(0, 1) });
        Assert.Equal(0, sim.World.Resource<PlayerInventory>().Equipped);
        Assert.Equal(0, sim.World.Get<WeaponSlot>(player).Index);
        var switched = sim.World.Get<Blade>(player);
        Assert.Equal(0, switched.Weapon);
        Assert.Equal((switched.Base, switched.Tip), (switched.PrevBase, switched.PrevTip));   // смена меча — прохода нет
        Step(sim, 1, new PlayerCommand { Move = new Vector2(0, 1) });
        var next = sim.World.Get<Blade>(player);
        Assert.Equal((switched.Base, switched.Tip), (next.PrevBase, next.PrevTip));           // дальше — прошлый тик

        sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });   // по кругу: за последним — первый
        Step(sim, 1);
        Assert.Equal(1, sim.World.Get<WeaponSlot>(player).Index);
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.EquipWeapon, Weapon = 7 });   // такого нет — ничего
        Step(sim, 1);
        Assert.Equal(1, sim.World.Get<WeaponSlot>(player).Index);
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.EquipWeapon, Weapon = 0 });
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });                      // R после выбора: игрок заново — с ним
        Step(sim, 1);
        Assert.Equal((0, 0), (sim.World.Get<WeaponSlot>(player).Index, sim.World.Resource<PlayerInventory>().Equipped));
        var rebuilt = sim.World.Get<Blade>(player);
        Assert.Equal(0, rebuilt.Weapon);
        Assert.True(Vector3.Distance(rebuilt.Base, new Vector3(0, rebuilt.Base.Y, 0)) < 1f, $"клинок у игрока в центре: {rebuilt.Base}");
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent()));
    }

    [Fact]
    public void Blade_OnHandEveryTick_PrevIsLastTick_PlacedBeforeFirstTick()
    {
        var sim = Create("arena.enemies=3");
        var view = sim.World.View<ICombatView>();
        var poses = sim.World.View<IAnimationView>();
        int hand = poses.FindJoint(CharacterAssets.HandJoint);
        Assert.Equal(hand, view.HandJoint);
        // До первого тика клинки бойцов сборки мира стоят (как тела): меч в руке, прошлое = нынешнему.
        foreach (var e in EnemiesBySlot(sim).Append(GameCore.Player(sim.World)))
        {
            var b = sim.World.Get<Blade>(e);
            Assert.Equal(sim.World.Get<WeaponSlot>(e).Index, b.Weapon);
            Assert.Equal((b.Base, b.Tip), (b.PrevBase, b.PrevTip));
        }
        Assert.DoesNotContain(sim.Invariants.Run(["combat.blade"], sim.Tick, "между тиками", sim.Dt), r => r.Violations > 0);
        var last = new Dictionary<Entity, Blade>();
        for (int t = 0; t < 200; t++)
        {
            Step(sim, 1, new PlayerCommand { Move = t < 100 ? new Vector2(1, 0) : new Vector2(0, -1), Walk = t > 150 });
            foreach (var e in EnemiesBySlot(sim).Append(GameCore.Player(sim.World)))
            {
                var root = sim.World.Get<Transform>(e);
                var weapon = view.Weapons[sim.World.Get<WeaponSlot>(e).Index];
                var (bladeBase, tip) = BladePlacement.Of(poses.Model(sim.World.Get<PoseRef>(e)), hand, weapon, root.Position, root.Facing);
                var b = sim.World.Get<Blade>(e);
                Assert.Equal((bladeBase, tip), (b.Base, b.Tip));   // те же выражения — побитово
                Assert.InRange(Vector3.Distance(b.Base, b.Tip), weapon.Length - 1e-4f, weapon.Length + 1e-4f);
                if (last.TryGetValue(e, out var prev)) Assert.Equal((prev.Base, prev.Tip), (b.PrevBase, b.PrevTip));
                last[e] = b;
            }
        }
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent()));
        Assert.Equal(201, sim.Invariants.Find("combat.blade")!.Evaluations);   // 200 тиков и проверка до первого тика
    }

    [Fact]
    public void ArenaCommands_InBegin_IntentSeesArenaAfterThem()
    {
        var sim = Create("arena.enemies=2");
        var arena = sim.World.Systems.Single(s => s.Label == GameLabels.ArenaCommand.Name);
        Assert.Equal(GameStages.Begin, arena.Stage);
        Step(sim, 30, new PlayerCommand { Move = new Vector2(1, 0) });
        // Тик сброса с командой игрока: игрок построен заново в центре и уже в этом тике идёт по команде.
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, 1, new PlayerCommand { Move = new Vector2(0, 1) });
        var player = GameCore.Player(sim.World);
        Assert.Equal(new Vector3(0, 0, MovementRules.RunSpeed), sim.World.Get<FighterCommand>(player).DesiredVelocity);
        var t = sim.World.Get<Transform>(player);
        Assert.True(t.Position.Length() < 0.1f, $"{t.Position}");
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Theory]
    [InlineData(CombatFault.BladeShifted, "combat.blade", GameStages.PrePhysics, "не на кисти: основание в 0.05 м")]
    [InlineData(CombatFault.BladeWrongWeapon, "combat.blade", GameStages.PrePhysics, "клинок посчитан мечом 0, а в руке 1")]
    [InlineData(CombatFault.WeaponOutOfTable, "combat.weapons", GameStages.End, "меч 9 вне таблицы оружия (2)")]
    [InlineData(CombatFault.PlayerWeaponNotEquipped, "combat.weapons", GameStages.End, "в руке игрока katana, а в инвентаре выбран 1")]
    public void Fault_CaughtOnItsTick(CombatFault fault, string id, string stage, string details)
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=1"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. SwordSimulation.Modules(), new CombatFaultModule(fault)]);
        Step(sim, (int)CombatFaultModule.Tick);
        Assert.Equal(0, sim.Invariants.Total);
        Step(sim, 1);
        var first = sim.Invariants.Recent().First(v => v.Id == id);
        Assert.Equal((CombatFaultModule.Tick, stage), (first.Tick, first.Stage));
        Assert.Contains(details, first.Details);
    }

    [Fact]
    public void Inventory_EquipNext_InOrder_WrapsAndSkipsUnowned()
    {
        var inv = PlayerInventory.All(3, 1);
        inv.EquipNext();
        Assert.Equal(2, inv.Equipped);
        inv.EquipNext();
        Assert.Equal(0, inv.Equipped);
        var sparse = new PlayerInventory { Owned = 0b10100, Equipped = 2 };
        sparse.EquipNext();
        Assert.Equal(4, sparse.Equipped);
        sparse.EquipNext();
        Assert.Equal(2, sparse.Equipped);
        Assert.False(sparse.Equip(3));
        var lost = new PlayerInventory { Owned = 0b110, Equipped = 0 };   // выбранного нет — первое доступное (IndexOf −1 SWORD)
        lost.EquipNext();
        Assert.Equal(1, lost.Equipped);
        var full = PlayerInventory.All(64, 63);
        full.EquipNext();
        Assert.Equal(0, full.Equipped);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerInventory.All(2, 2));
    }

    [Fact]
    public void StrikeWindows_SinglePeak_Combo_Thrust()
    {
        // Кончик: стоит, быстрый взмах вниз (кадры 10–14), стоит; 30 кадров/с.
        var tips = new Vector3[30];
        for (int f = 0; f < tips.Length; f++) tips[f] = new Vector3(0, 1.5f - 0.3f * Math.Clamp(f - 9, 0, 5), 0.5f);
        var one = Assert.Single(StrikeWindows.Find(tips, 30));
        Assert.InRange(one.PeakTime, 10 / 30f, 14 / 30f);
        Assert.True(one.PeakSpeed > StrikeWindows.MinPeakSpeed);
        Assert.Equal(-1, one.SwingDir.Y, 3);   // вниз
        // Связка: вниз, пауза, вправо (−X персонажа) — два удара; укол оставляет только самый быстрый.
        var combo = new Vector3[40];
        for (int f = 0; f < combo.Length; f++)
            combo[f] = new Vector3(-0.35f * Math.Clamp(f - 24, 0, 4), 1.5f - 0.3f * Math.Clamp(f - 5, 0, 5), 0.5f);
        Assert.Equal(2, StrikeWindows.Find(combo, 30).Length);
        Assert.Single(StrikeWindows.Find(combo, 30, single: true));
        Assert.Equal(new StrikeWindow(0, 0, 0, 0, Vector2.UnitY), Assert.Single(StrikeWindows.Find([Vector3.Zero], 30)));
    }

    [Fact]
    public void StrikeTable_SlashesThenStabs_ByDefaultBlade()
    {
        var assets = TestAssets.Assets;
        var table = assets.Strikes;
        Assert.Equal([.. StrikeTable.SlashSlots, .. StrikeTable.StabSlots], table.Attacks.ToArray().Select(a => a.Slot));
        Assert.All(table.Slashes.ToArray(), a => Assert.False(a.Thrust));
        Assert.All(table.Stabs.ToArray(), a => Assert.True(a.Thrust && a.Windows.Length == 1));
        Assert.All(table.Attacks.ToArray(), a => Assert.True(a.Windows.Length >= 1 && a.BlockedClip == -1 && a.Upper));
        Assert.Equal(table.Attacks[0].Slot, table.Of(table.Attacks[0].Clip)?.Slot);
        // Кончик — клинок меча по умолчанию в кисти позы кадра.
        var tips = StrikeTable.Tips(assets.Clips, 0, assets.Hand, assets.Weapons.Weapons[assets.Weapons.Default].BladeTip);
        Assert.Equal(assets.Clips.Clips[0].Count, tips.Length);
    }

    [Fact]
    public async Task Agent_CombatWeapons_NamesWeaponsBladesAndAttacks()
    {
        var sim = Create("arena.enemies=2");
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

            await Call("time.step", new JsonObject { ["ticks"] = 3 });
            var all = await Call("combat.weapons");
            Assert.Equal(["katana", "sword_1h"], all["weapons"]!.AsArray().Select(w => w!["name"]!.GetValue<string>()));
            Assert.Equal("sword_1h", all["defaultWeapon"]!.GetValue<string>());
            Assert.Equal("sword_1h", all["inventory"]!["equipped"]!.GetValue<string>());
            Assert.Equal(3, all["fighters"]!.AsArray().Count);
            Assert.Equal(7, all["attacks"]!.AsArray().Count);
            Assert.Equal("slash1", all["attacks"]![0]!["slot"]!.GetValue<string>());
            await Call("command.send", new JsonObject { ["command"] = "ArenaCommand", ["fields"] = new JsonObject { ["Action"] = "NextWeapon" } });
            await Call("time.step", new JsonObject { ["ticks"] = 1 });
            var player = await Call("combat.weapons", new JsonObject { ["entity"] = "player", ["attacks"] = false });
            var p = Assert.Single(player["fighters"]!.AsArray())!;
            Assert.Equal(("player", "katana", "katana"), (p["name"]!.GetValue<string>(), p["weapon"]!.GetValue<string>(), p["bladeWeapon"]!.GetValue<string>()));
            Assert.Equal(0, p["tipTravel"]!.GetValue<double>());   // смена меча — прохода нет
            Assert.Null(player["attacks"]);
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void AppLocator_FindsAppByBinFolder_OrByOptimizationOutsideBin()
    {
        string root = Path.Combine(Path.GetTempPath(), "sword-app-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            string debug = Path.Combine(root, "game", "Sword.Game.App", "bin", "Debug", "net10.0-windows10.0.22000.0");
            Directory.CreateDirectory(debug);
            File.WriteAllBytes(Path.Combine(debug, "Sword.exe"), []);
            string exe = Path.Combine(debug, "Sword.exe");
            string headlessBin = Path.Combine(root, "game", "Sword.Game.Headless", "bin", "Debug", "net10.0-windows10.0.22000.0") + Path.DirectorySeparatorChar;
            string agentBin = Path.Combine(root, "artifacts", "agent-bin", "headless", "123456") + Path.DirectorySeparatorChar;
            Assert.Equal(exe, AppLocator.Find(root, headlessBin, optimized: true));    // по папке за bin — Debug
            Assert.Equal(exe, AppLocator.Find(root, agentBin, optimized: false));      // вне bin (agent-bin) — Debug сборки
            Assert.Null(AppLocator.Find(root, agentBin, optimized: true));             // Release App не собран
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public enum CombatFault { BladeShifted, BladeWrongWeapon, WeaponOutOfTable, PlayerWeaponNotEquipped }

/// <summary>Модуль-неисправность боя: на тике <see cref="Tick"/> портит клинок или меч.</summary>
public sealed class CombatFaultModule(CombatFault fault) : IModule
{
    public const ulong Tick = 25;

    public string Id => "fault";

    public void Configure(SimulationBuilder b)
    {
        switch (fault)
        {
            case CombatFault.BladeShifted: b.System(GameStages.PrePhysics, new BladeFault(wrongWeapon: false)); break;
            case CombatFault.BladeWrongWeapon: b.System(GameStages.PrePhysics, new BladeFault(wrongWeapon: true)); break;
            case CombatFault.WeaponOutOfTable: b.System(GameStages.End, new SlotFault(player: false)); break;
            case CombatFault.PlayerWeaponNotEquipped: b.System(GameStages.End, new SlotFault(player: true)); break;
        }
    }

    private static bool Now(in SystemContext ctx) => ctx.World.Resource<SimTime>().Tick == Tick;

    /// <summary>После постановки клинка: основание игрока выше на 5 см или клинок «посчитан» другим мечом.</summary>
    private sealed class BladeFault(bool wrongWeapon) : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.blade")).After(GameLabels.BladePlacement).Writes<Blade>();
            _query = s.Query().With<PlayerTag>().Write<Blade>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var b in chunk.Write<Blade>())
                    if (wrongWeapon) b.Weapon = 0;
                    else b.Base.Y += 0.05f;
        }
    }

    /// <summary>В конце тика: мечу врага — номер вне таблицы, или игроку — меч не из инвентаря.</summary>
    private sealed class SlotFault(bool player) : IContinuousSystem
    {
        private Query _query = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.weapon")).Writes<WeaponSlot>();
            _query = player ? s.Query().With<PlayerTag>().Write<WeaponSlot>() : s.Query().With<EnemyTag>().Write<WeaponSlot>();
        }

        public void Run(in SystemContext ctx)
        {
            if (!Now(ctx)) return;
            foreach (var chunk in ctx.Iterate(_query))
                foreach (ref var w in chunk.Write<WeaponSlot>()) w.Index = player ? 0 : 9;
        }
    }
}
