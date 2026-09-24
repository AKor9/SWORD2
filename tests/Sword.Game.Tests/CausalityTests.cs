using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sword.Engine.Content;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bootstrap;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 6д «Причинность и state.why»: диагностический режим причинности не меняет хеш ни на одном тике (бой с попаданиями,
/// кровопотерей, смертью и возрождением) и не выделяет памяти в тике ни выключенный, ни включённый; цепочки боя полные —
/// удар врага по игроку (решение ИИ → начало удара → StrikeContact → Hit → Health), смерть врага от ударов игрока (команда
/// игрока → …), смерть от кровопотери (проба → попадание → кровопотеря → Dead), возрождение (смерть → шаг «возрождение» → Health);
/// агенту — <c>state.why</c> и причина в <c>events.history</c>. Контент — синтетический (<see cref="TestAssets"/>): его удары клинком
/// никого не задевают, поэтому здесь касания — пробой удара; цепочки настоящих ударов клинком — <see cref="CausalityContentTests"/>.
/// </summary>
public sealed class CausalityTests
{
    private static Simulation Create(bool causality, params string[] sets)
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty(sets));
        if (causality) sim.EnableCausality();
        return sim;
    }

    internal static void Step(Simulation sim, int ticks, PlayerCommand command = default)
    {
        for (int t = 0; t < ticks; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(command);
            sim.Step();
        }
    }

    internal static Entity Enemy(Simulation sim, int slot = 1)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет врага на месте {slot}");
    }

    internal static WhyAnswer Why(Simulation sim, Entity e, string component, string? field, int changes = 1)
    {
        var ctx = new ServiceContext(sim);
        var type = sim.World.Components.Single(c => c.Type.Name == component);
        return CausalityQuery.Why(ctx, e, type, field, changes, CausalityQuery.MaxDepth, new SwordAgent.ArenaNames());
    }

    internal static string Describe(WhyChange c) =>
        $"{c.Change.Record.Kind} {c.Change.System} ← " + string.Join(" ← ", c.Chain.Select(s => $"{s.Record.Kind} {s.TypeName ?? s.Record.Note} {s.System}"))
        + (c.Complete ? "" : $" … {c.Stop}");

    internal static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    /// <summary>Команда игрока: бежать к врагу, смотреть на него и бить на каждом <paramref name="every"/>-м тике.</summary>
    private static PlayerCommand Toward(Simulation sim, Entity enemy, int tick, int every = 24)
    {
        var p = sim.World.Get<Transform>(GameCore.Player(sim.World)).Position;
        var e = sim.World.Get<Transform>(enemy).Position;
        var to = new Vector2(e.X - p.X, e.Z - p.Z);
        return new PlayerCommand
        {
            Move = to.Length() > 1.2f ? to : Vector2.Zero, HasGaze = true, Gaze = e, Attack = tick % every == 0,
        };
    }

    // ── хеш и память ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Режим включён — хеш тот же на каждом тике (враги замахиваются, игрок бежит к врагу и бьёт, проба с кровотечением, смерть от
    /// кровопотери, возрождение через 8 с), и записи журнала причинности есть; в тиках боя с кровопотерей (команды Set каждый тик,
    /// значения до и после) ни выключенный, ни включённый режим не выделяют памяти (кольца — заранее).
    /// </summary>
    [Fact]
    public void Hash_SameWithAndWithout_EveryTick_ZeroBytes()
    {
        var plain = Create(false, "arena.enemies=3");
        var caused = Create(true, "arena.enemies=3");
        var a = new ulong[plain.StateParts.Count];
        var b = new ulong[caused.StateParts.Count];
        var enemy = Enemy(plain);
        long allocatedPlain = 0, allocatedCaused = 0;
        for (int t = 0; t < 1800; t++)
        {
            var command = Toward(plain, enemy, t);
            if (t == 40)
            {
                // 80 очков и кровотечение 4/с: смерть от кровопотери через ≈ 5,9 с, возрождение — ещё через 8 с
                plain.Enqueue(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.UArmL, Damage = 200 });
                caused.Enqueue(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.UArmL, Damage = 200 });
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            Step(plain, 1, command);
            long mid = GC.GetAllocatedBytesForCurrentThread();
            Step(caused, 1, command);
            if (t is >= 200 and < 700)
            {
                allocatedPlain += mid - before;
                allocatedCaused += GC.GetAllocatedBytesForCurrentThread() - mid;
            }
            Assert.True(plain.HashState(a) == caused.HashState(b), $"тик {t + 1}: хеш с режимом причинности другой");
        }
        Assert.Equal(0, allocatedPlain);
        Assert.Equal(0, allocatedCaused);
        Assert.Null(plain.Causality);
        Assert.True(caused.Causality!.Written > 1000, $"{caused.Causality.Written}");
        var stats = caused.World.Resource<ArenaStats>();
        Assert.True(stats.Hits == 1 && stats.Kills == 1, $"попаданий {stats.Hits}, убито {stats.Kills}");
        Assert.False(caused.World.Has<Dead>(Enemy(caused, 2)));   // возродился
        Clean(caused);
    }

    // ── цепочки ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Смерть от кровопотери: проба удара (корень — команда <c>StrikeProbe</c>) → касание → попадание → кровопотеря
    /// (<c>core.bleed@Aftermath</c>) → <c>Add&lt;Dead&gt;</c>; <c>Health.Hp</c> в тике смерти — тоже от кровопотери, со значениями до и после.
    /// </summary>
    [Fact]
    public void BleedDeath_ChainToProbe()
    {
        var sim = Create(true, "arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 3);
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.UArmL, Damage = 200 });
        int ticks = 0;
        while (!sim.World.Has<Dead>(enemy) && ticks++ < 1200) Step(sim, 1);
        Assert.True(sim.World.Has<Dead>(enemy));

        var death = Assert.Single(Why(sim, enemy, nameof(Dead), null).Changes);
        Assert.True(death.Complete, Describe(death));
        Assert.Equal("core.bleed@Aftermath", death.Change.System);
        Assert.Equal([typeof(Hit), typeof(StrikeContact), typeof(StrikeProbe)], death.Chain.Select(s => s.ValueType));
        Assert.Equal(CauseKind.Command, death.Chain[^1].Record.Kind);
        Assert.Contains("DamageSystems.cs", death.Change.Record.File);

        var hp = Assert.Single(Why(sim, enemy, nameof(Health), nameof(Health.Hp)).Changes);
        Assert.Equal("core.bleed@Aftermath", hp.Change.System);
        var before = System.Runtime.InteropServices.MemoryMarshal.Read<Health>(hp.Change.Before);
        var after = System.Runtime.InteropServices.MemoryMarshal.Read<Health>(hp.Change.After);
        Assert.True(before.Hp > 0 && after.Hp <= 0 && after.Hp == sim.World.Get<Health>(enemy).Hp, $"{before.Hp} → {after.Hp}");
        Clean(sim);
    }

    /// <summary>
    /// Возрождение: <c>Health.Hp</c> после возрождения — <c>core.respawn@Aftermath</c> ← шаг «возрождение» (враг) ← <c>Add&lt;Dead&gt;</c> ← попадание
    /// ← касание ← проба (корень); метка <c>Dead</c> снята той же постройкой.
    /// </summary>
    [Fact]
    public void Respawn_ChainThroughDeath()
    {
        var sim = Create(true, "arena.enemies=1", "combat.enemiesAttack=false");
        var enemy = Enemy(sim);
        Step(sim, 3);
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Head, Damage = 100, Kind = HitKind.Thrust });
        Step(sim, 1);
        Assert.True(sim.World.Has<Dead>(enemy));
        int ticks = 0;
        while (sim.World.Has<Dead>(enemy) && ticks++ < 1200) Step(sim, 1);
        Assert.False(sim.World.Has<Dead>(enemy));

        var hp = Assert.Single(Why(sim, enemy, nameof(Health), nameof(Health.Hp)).Changes);
        Assert.True(hp.Complete, Describe(hp));
        Assert.Equal("core.respawn@Aftermath", hp.Change.System);
        var step = hp.Chain[0];
        Assert.Equal((CauseKind.Step, CombatCauses.RespawnDue, enemy), (step.Record.Kind, step.Record.Note, step.Record.Entity));
        Assert.Equal((CauseKind.Add, typeof(Dead)), (hp.Chain[1].Record.Kind, hp.Chain[1].ValueType));
        Assert.Equal([typeof(Hit), typeof(StrikeContact), typeof(StrikeProbe)], hp.Chain.Skip(2).Select(s => s.ValueType));
        Assert.True(ticks > 900, $"{ticks}");

        var removed = Assert.Single(Why(sim, enemy, nameof(Dead), null).Changes);
        Assert.Equal((CauseKind.Remove, "core.respawn@Aftermath"), (removed.Change.Record.Kind, removed.Change.System));
        Clean(sim);
    }

    /// <summary>Непрерывное поле (корень пишут на месте) — грубо: регистрации, писавшие столбец, и тик; без режима — выключено.</summary>
    [Fact]
    public void ContinuousField_WritersOnly_OffWithoutMode()
    {
        var sim = Create(true, "arena.enemies=1", "combat.enemiesAttack=false");
        Step(sim, 5, new PlayerCommand { Move = new Vector2(1, 0) });
        var why = Why(sim, GameCore.Player(sim.World), nameof(Transform), "Position.X");
        Assert.True(why.Enabled);
        Assert.Empty(why.Changes);
        Assert.Contains(why.Writers, w => w.Tick == sim.Tick - 1);
        Assert.NotEmpty(why.Producers);

        var off = Create(false, "arena.enemies=1");
        Step(off, 2);
        var none = Why(off, GameCore.Player(off.World), nameof(Health), nameof(Health.Hp));
        Assert.False(none.Enabled);
        Assert.Contains(none.Producers, p => p.StartsWith("core.hit-apply@Resolve", StringComparison.Ordinal));
    }

    // ── агент ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Агенту: <c>session.status</c> — режим включён; <c>state.why</c> по имени сущности и полю — изменение, цепочка до корня, строка
    /// итога; <c>events.history</c> — у записей причина и родитель; ошибка — неизвестное поле с перечнем.
    /// </summary>
    [Fact]
    public async Task Agent_StateWhy_AndEventsHistoryCauses()
    {
        var sim = Create(true, "arena.enemies=1", "combat.enemiesAttack=false");
        var host = new Sword.Engine.Agent.AgentHost(new Sword.Engine.Agent.AgentHostOptions
        {
            Mode = "headless", Catalog = new Catalog(sim.World, sim.CommandTypes),
            Time = new TimeControl(true, sim.Tick), Service = new ServiceQueue(),
            Log = new LogRing(16), ArtifactsRoot = Path.Combine(Path.GetTempPath(), "sword-game-agent-tests"), Stop = () => { },
            ServiceTimeout = TimeSpan.FromSeconds(5),
        });
        SwordAgent.Register(host);
        using var stop = new CancellationTokenSource();
        var loop = new Thread(() => Sword.Engine.Agent.SimulationLoop.Run(sim, host, stop.Token)) { IsBackground = true };
        loop.Start();
        try
        {
            async Task<JsonNode> Raw(string tool, JsonObject? args)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? [] },
                };
                return JsonNode.Parse((await host.Server.HandleAsync(request.ToJsonString()))!)!["result"]!;
            }
            async Task<JsonObject> Call(string tool, JsonObject? args = null)
            {
                var r = await Raw(tool, args);
                Assert.False(r["isError"]?.GetValue<bool>() ?? false, r.ToJsonString());
                return r["structuredContent"]!.AsObject();
            }

            Assert.True((await Call("session.status"))["causality"]!["capacity"]!.GetValue<int>() > 0);
            await Call("time.step", new() { ["ticks"] = 3 });
            await Call("command.send", new()
            {
                ["command"] = "StrikeProbe",
                ["fields"] = new JsonObject { ["Attacker"] = 0, ["Victim"] = 1, ["Part"] = BodySegs.Torso, ["Damage"] = 30 },
            });
            await Call("time.step", new() { ["ticks"] = 2 });

            var why = await Call("state.why", new() { ["entity"] = "enemy 1", ["field"] = "Health.Hp", ["changes"] = 2 });
            Assert.True(why["enabled"]!.GetValue<bool>());
            var change = why["changes"]![0]!;
            Assert.True(change["complete"]!.GetValue<bool>(), change.ToJsonString());
            Assert.Equal("Command", change["root"]!.GetValue<string>());
            string summary = change["summary"]!.GetValue<string>();
            Assert.Contains("Health.Hp", summary);
            Assert.Contains("StrikeProbe", summary);
            Assert.True(summary.Contains("Hit (player → enemy 1)"), summary);
            Assert.Contains("DamageSystems.cs:", change["source"]!.GetValue<string>());
            Assert.StartsWith("game/", change["source"]!.GetValue<string>());
            Assert.Equal("StrikeProbe", Last(change["chain"]!)["type"]!.GetValue<string>());
            Assert.Equal("агент", Last(change["chain"]!)["note"]!.GetValue<string>());

            var history = await Call("events.history", new() { ["types"] = new JsonArray("Hit") });
            var row = Assert.Single(history["events"]!.AsArray())!;
            Assert.Equal("Event", row["cause"]!["kind"]!.GetValue<string>());
            Assert.Equal("core.hit-apply@Resolve", row["cause"]!["system"]!.GetValue<string>());
            Assert.NotNull(row["cause"]!["parent"]);

            var bad = await Raw("state.why", new() { ["entity"] = "player", ["field"] = "Health.Hpp" });
            Assert.True(bad["isError"]!.GetValue<bool>());
            Assert.Contains("Hp", bad.ToJsonString());
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }

    private static JsonNode Last(JsonNode array) => array.AsArray()[^1]!;
}

/// <summary>
/// Точка 6д на эталонном паке (настоящие удары клинком; категория Content — <c>verify.ps1 -SkipReference</c> её не запускает): хеш с
/// режимом причинности и без — тот же на каждом тике боя (касания, попадания, реакции, вздрагивание, смерть); удар врага по игроку —
/// <c>state.why(player, Health.Hp)</c> называет попадание, врага, начало его удара и решение ИИ; смерть врага в дуэли — цепочка от
/// команды игрока (сценарий <c>duel</c> манифеста <c>combat</c>).
/// </summary>
[Trait("Category", "Content")]
public sealed class CausalityContentTests
{
    private static readonly Lazy<CharacterAssets> Reference = new(() =>
    {
        using var pak = PakSource.Open(TestContent.ReferencePak());
        return CharacterAssets.Load(pak);
    });

    private static Simulation Create(bool causality, params string[] sets)
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty(sets), Reference.Value);
        if (causality) sim.EnableCausality();
        return sim;
    }

    private static void Step(Simulation sim, int ticks, PlayerCommand command = default) => CausalityTests.Step(sim, ticks, command);

    private static Entity Enemy(Simulation sim, int slot = 1) => CausalityTests.Enemy(sim, slot);

    private static WhyAnswer Why(Simulation sim, Entity e, string component, string? field) => CausalityTests.Why(sim, e, component, field);

    private static string Describe(WhyChange c) => CausalityTests.Describe(c);

    /// <summary>Команды дуэли манифеста <c>combat</c>: взгляд на подходящего врага, ЛКМ раз в 0,5 с с тика 240.</summary>
    private static PlayerCommand Duel(int tick) => new()
    {
        HasGaze = true, Gaze = new Vector3(-10, 0, -3), Attack = tick >= 240 && tick % 60 == 0,
    };

    /// <summary>Хеш с режимом и без — на каждом тике настоящего боя: 3 врага бьют игрока, игрок бьёт в ответ.</summary>
    [Fact]
    public void Hash_SameWithAndWithout_EveryTick_RealStrikes()
    {
        var plain = Create(false, "arena.enemies=3");
        var caused = Create(true, "arena.enemies=3");
        var a = new ulong[plain.StateParts.Count];
        var b = new ulong[caused.StateParts.Count];
        for (int t = 0; t < 900; t++)
        {
            Step(plain, 1, Duel(t));
            Step(caused, 1, Duel(t));
            Assert.True(plain.HashState(a) == caused.HashState(b), $"тик {t + 1}: хеш с режимом причинности другой");
        }
        Assert.True(caused.World.Resource<ArenaStats>().Hits >= 3, $"попаданий {caused.World.Resource<ArenaStats>().Hits}");
    }

    /// <summary>
    /// Удар врага по игроку: <c>state.why(player, Health.Hp)</c> — последнее изменение (кровопотеря после попадания или само попадание)
    /// называет попадание врага (клинком, не пробой), его касание, начало удара этого врага с номером удара и корень — решение ИИ
    /// <c>core.enemy-ai</c> «удар по таймеру»; источник — файл и строка команды.
    /// </summary>
    [Fact]
    public void PlayerHp_ByEnemyStrike_NamesHitEnemyAndStrikeStart()
    {
        var sim = Create(true, "arena.enemies=3");
        var player = GameCore.Player(sim.World);
        int ticks = 0;
        while (sim.World.Get<Health>(player).Hp >= Health.Full && ticks++ < 1200) Step(sim, 1);
        Assert.True(sim.World.Get<Health>(player).Hp < Health.Full, "враги не попали в игрока за 10 с");
        Step(sim, 2);

        var why = Why(sim, player, nameof(Health), nameof(Health.Hp));
        var change = Assert.Single(why.Changes);
        Assert.True(change.Complete, Describe(change));
        var hit = change.Chain.First(s => s.Record.Kind == CauseKind.Event && s.ValueType == typeof(Hit));
        var value = MemoryMarshal.Read<Hit>(hit.After);
        Assert.Equal((player, false), (value.Victim, value.Probe));
        Assert.True(sim.World.Has<EnemyTag>(value.Attacker));
        Assert.Contains(change.Chain, s => s.Record.Kind == CauseKind.Event && s.ValueType == typeof(StrikeContact) && s.System == "core.strike-detection@Combat");
        var start = change.Chain.Single(s => s.Record.Kind == CauseKind.Step);
        Assert.Equal((value.Attacker, CombatCauses.AttackStart), (start.Record.Entity, start.Record.Note));
        Assert.StartsWith("enemy ", start.EntityName);
        Assert.Equal(MemoryMarshal.Read<StrikeContact>(change.Chain.Single(s => s.ValueType == typeof(StrikeContact)).After).AttackSerial, start.Record.Arg);
        var root = change.Chain[^1];
        Assert.Equal((CauseKind.Decision, CombatCauses.AiStrike, "core.enemy-ai@Intent", value.Attacker),
            (root.Record.Kind, root.Record.Note, root.System, root.Record.Entity));
        Assert.Contains("EnemyAi.cs", root.Record.File);
        Assert.Contains("DamageSystems.cs", change.Change.Record.File);
        CausalityTests.Clean(sim);
    }

    /// <summary>
    /// Смерть врага от ударов игрока (дуэль): <c>state.why(enemy 1, Dead)</c> — <c>Add&lt;Dead&gt;</c> ← попадание игрока (клинком) ← касание ←
    /// начало удара игрока ← команда игрока с <c>Attack</c> (корень; клиент — очередь владельца).
    /// </summary>
    [Fact]
    public void EnemyDeath_ByPlayerStrikes_ChainToPlayerCommand()
    {
        var sim = Create(true, "arena.enemies=1", "combat.enemiesAttack=false");
        var player = GameCore.Player(sim.World);
        var enemy = Enemy(sim);
        int t = 0;
        while (!sim.World.Has<Dead>(enemy) && t < 1000) Step(sim, 1, Duel(t++));
        Assert.True(sim.World.Has<Dead>(enemy), $"игрок не убил врага за {t} тиков; попаданий {sim.World.Resource<ArenaStats>().Hits}");

        var change = Assert.Single(Why(sim, enemy, nameof(Dead), null).Changes);
        Assert.True(change.Complete, Describe(change));
        Assert.Equal(CauseKind.Add, change.Change.Record.Kind);
        var hit = change.Chain.Single(s => s.ValueType == typeof(Hit));
        var value = MemoryMarshal.Read<Hit>(hit.After);
        Assert.Equal((player, enemy, false), (value.Attacker, value.Victim, value.Probe));
        Assert.Equal([typeof(Hit), typeof(StrikeContact), null, typeof(PlayerCommand)], change.Chain.Select(s => s.ValueType));
        Assert.Equal((CauseKind.Step, player), (change.Chain[2].Record.Kind, change.Chain[2].Record.Entity));
        var root = change.Chain[^1];
        Assert.Equal((CauseKind.Command, "клиент"), (root.Record.Kind, root.Record.Note));
        Assert.True(MemoryMarshal.Read<PlayerCommand>(root.After).Attack);
        CausalityTests.Clean(sim);
    }
}
