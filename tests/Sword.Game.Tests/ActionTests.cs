using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Movement;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 6в «Удар и рывок»: выбор удара (<see cref="AttackPicker"/>: связка, сброс через 1,6 с, жест, без повтора клипа), удар игрока
/// командой (буфер 0,3 с, связка сразу после окна урона, ход не замедляется, хвост на бегу), снимок позы при прерывании, рывок (прибавка
/// скорости без последствий для хода, назад без хода, перезарядка, неуязвимость), удар из рывка, удар ИИ по таймеру и командой врагу,
/// мирные враги, инварианты слоя действий и управления на неисправностях, жест клиента (затухание по времени, удар по экрану),
/// кончик клинка по тикам агенту. Контент — синтетический (<see cref="TestAssets"/>: удары — взмах плеча, уклонения — сдвиг корня).
/// </summary>
public sealed class ActionTests
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

    private static ReadOnlySpan<AttackInfo> Attacks => TestAssets.Assets.Strikes.Attacks;

    /// <summary>Нарушений инвариантов нет; есть — какие.</summary>
    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static Entity Enemy(Simulation sim)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("нет врага");
    }

    // ── выбор удара ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static AttackInfo A(string slot, int clip, float x, float y, bool thrust = false) =>
        new(slot, clip, -1, 1.2f, thrust, true, [new StrikeWindow(0.2f, 0.4f, 0.3f, 30f, Vector2.Normalize(new Vector2(x, y)))]);

    /// <summary>Таблица как у пака: направления замаха SWORD (slash1–4), три укола одним клипом.</summary>
    private static readonly AttackInfo[] Table =
    [
        A("slash1", 10, -0.14f, -0.99f), A("slash2", 11, 0.97f, -0.26f), A("slash3", 12, 0.37f, -0.93f), A("slash4", 13, -0.92f, -0.40f),
        A("stab1", 20, 0.54f, 0.84f, true), A("stab2", 20, 0.54f, 0.84f, true), A("stab3", 20, 0.54f, 0.84f, true),
    ];

    [Fact]
    public void Picker_ComboOfFour_ResetAfterPause_GestureNearest_NoClipRepeat()
    {
        var combo = AttackCombo.Start;
        int Pick(float time, Vector2 gesture = default, bool stab = false, int roll = 0) =>
            AttackPicker.Pick(ref combo, time, gesture, stab, roll, Table, 4);
        // связка slash1–4 по кругу
        Assert.Equal([0, 1, 2, 3, 0], new[] { Pick(0), Pick(0.5f), Pick(1f), Pick(1.5f), Pick(2f) });
        // пауза дольше 1,6 с — связка с начала
        Assert.Equal(1, Pick(3.5f));
        Assert.Equal(0, Pick(5.2f));
        // жест — ближайший замах (в системе бойца), связка продолжается со следующего за ним
        Assert.Equal(1, Pick(5.3f, new Vector2(1, -0.2f)));
        Assert.Equal(2, Pick(5.4f));
        Assert.Equal(3, Pick(5.5f, new Vector2(-30, -12)));   // длина не важна
        // укол — случайный номер уколов, связку не двигает
        Assert.Equal(6, Pick(5.6f, stab: true, roll: 2));
        Assert.Equal(0, Pick(5.7f));
        // не повторять клип подряд: у slash1 и slash2 один клип — после slash1 связка идёт к slash3
        AttackInfo[] same = [A("slash1", 10, 0, -1), A("slash2", 10, 1, 0), A("slash3", 12, 0, -1), A("slash4", 13, -1, 0), A("stab1", 20, 0, 1, true)];
        var c2 = AttackCombo.Start;
        Assert.Equal(0, AttackPicker.Pick(ref c2, 0, default, false, 0, same, 4));
        Assert.Equal(2, AttackPicker.Pick(ref c2, 0.1f, default, false, 0, same, 4));
        // уколы разными клипами: тот же клип подряд — следующий
        AttackInfo[] stabs = [.. Table[..4], A("stab1", 20, 0, 1, true), A("stab2", 21, 0, 1, true), A("stab3", 22, 0, 1, true)];
        var c3 = AttackCombo.Start;
        Assert.Equal(5, AttackPicker.Pick(ref c3, 0, default, true, 1, stabs, 4));
        Assert.Equal(6, AttackPicker.Pick(ref c3, 0.1f, default, true, 1, stabs, 4));
    }

    // ── удар игрока ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ЛКМ: удар в тике фронта; нажатие во время удара ждёт в буфере и даёт следующий удар связки ровно в первом тике после окна урона
    /// (<c>Agile</c>) — прерывание видимого удара снимает позу; четыре разных удара, номер удара растёт; инварианты чисты.
    /// </summary>
    [Fact]
    public void PlayerAttack_ComboOfFour_BufferedUntilAfterWindow_Snapshot()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 5);
        Step(sim, 1, new PlayerCommand { Attack = true });
        var a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal((ActionKind.Attack, 0, 1, ActionKind.None), (a.Kind, a.Attack, a.AttackSerial, a.From));
        Assert.True(a.Upper);
        var seen = new List<int> { a.Attack };
        for (int k = 1; k < 4; k++)
        {
            float end = Attacks[a.Attack].ActiveEnd;
            // нажатие за 0,2 с до конца окна — ждёт в буфере
            while (sim.World.Get<AnimatorAction>(player).Time < end - 0.2f) Step(sim, 1);
            Step(sim, 1, new PlayerCommand { Attack = true });
            Assert.Equal(seen[^1], sim.World.Get<AnimatorAction>(player).Attack);
            Assert.True(sim.World.Get<PlayerControl>(player).SwingBuffer > 0);
            float before;
            do
            {
                before = sim.World.Get<AnimatorAction>(player).Time;
                Step(sim, 1);
            } while (sim.World.Get<AnimatorAction>(player).AttackSerial == k);
            a = sim.World.Get<AnimatorAction>(player);
            Assert.True(before > end && before - sim.Dt <= end, $"удар {k}: начат после {before} с, окно до {end}");
            Assert.Equal((ActionKind.Attack, k + 1, ActionKind.Attack), (a.Kind, a.AttackSerial, a.From));
            Assert.Equal(k, a.Snapshots);   // прерван видимый удар — снимок
            Assert.False(sim.World.Get<ActionPoses>(player).Snap.IsNone);
            seen.Add(a.Attack);
        }
        Assert.Equal([0, 1, 2, 3], seen);
        Assert.Equal(4, seen.Select(s => Attacks[s].Clip).Distinct().Count());
        Clean(sim);
    }

    [Fact]
    public void PlayerStab_Q_StabFromTable()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 3);
        Step(sim, 1, new PlayerCommand { Stab = true });
        var a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal(ActionKind.Attack, a.Kind);
        Assert.True(Attacks[a.Attack].Thrust, Attacks[a.Attack].Slot);
        Assert.Equal(0, sim.World.Get<AttackCombo>(player).Next);   // укол связку не двигает
        Clean(sim);
    }

    /// <summary>
    /// Отзывчивость: бег с ударом — скорость корня побитово как без удара (ход не замедляется); после окна урона на бегу хвост удара
    /// обрезан и гаснет быстрее; ноги — у локомоции (доля клипа в ногах 0).
    /// </summary>
    [Fact]
    public void PlayerAttack_RunNotSlowed_RecoveryCutOnTheRun()
    {
        var run = new PlayerCommand { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 50) };
        var plain = Create();
        var striking = Create();
        var p1 = GameCore.Player(plain.World);
        var p2 = GameCore.Player(striking.World);
        Step(plain, 60, run);
        Step(striking, 60, run);
        bool cut = false;
        for (int t = 0; t < 120; t++)
        {
            Step(plain, 1, run);
            Step(striking, 1, t == 0 ? run with { Attack = true } : run);
            Assert.Equal(plain.World.Get<RootVelocity>(p1).Value, striking.World.Get<RootVelocity>(p2).Value);
            var a = striking.World.Get<AnimatorAction>(p2);
            if (a.Kind != ActionKind.Attack) continue;
            Assert.Equal(0, a.Stand);
            cut |= a.CutRecovery;
            if (a.CutRecovery) Assert.True(a.Time > Attacks[a.Attack].ActiveEnd + ActionRules.RecoveryCutAfter);
        }
        Assert.True(cut, "хвост удара на бегу не обрезан");
        Assert.Equal(ActionKind.None, striking.World.Get<AnimatorAction>(p2).Kind);
        Clean(striking);
    }

    // ── рывок ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Рывок на бегу: скорость хода (<see cref="RootVelocity"/>) — побитово как без рывка на каждом тике (без провала и без инерции),
    /// корень ушёл вперёд на дистанцию прибавки (скорость·время·½); вид — клип уклонения вперёд, гаснет к концу рывка, root motion
    /// клипа не применяется; неуязвимость и перезарядка; второй рывок до перезарядки — нет.
    /// </summary>
    [Fact]
    public void Dash_OnTheRun_BoostOnly_SpeedUntouched_CooldownAndInvulnerability()
    {
        var run = new PlayerCommand { Move = new Vector2(0, 1), HasGaze = true, Gaze = new Vector3(0, 0, 50) };
        var plain = Create();
        var dashing = Create();
        var p1 = GameCore.Player(plain.World);
        var p2 = GameCore.Player(dashing.World);
        var rules = dashing.World.Resource<CombatRules>();
        Step(plain, 40, run);
        Step(dashing, 40, run);
        Step(plain, 1, run);
        Step(dashing, 1, run with { Dash = true });
        var c = dashing.World.Get<PlayerControl>(p2);
        Assert.True(c.Dashing);
        Assert.Equal(1, c.Dashes);
        Assert.Equal(rules.DashInvulnerable, c.Invulnerable);
        Assert.Equal(rules.DashCooldown, c.DashCooldown);
        Assert.Equal(rules.DashSpeed, c.Boost.Length(), 4);   // прибавка — с тика рывка
        var a = dashing.World.Get<AnimatorAction>(p2);
        Assert.Equal(ActionKind.Dash, a.Kind);
        Assert.Equal(TestAssets.Assets.Actions.Clip[Data.ActionSlots.EvadeForward], a.Clip);
        float maxBoost = c.Boost.Length();
        for (int t = 0; t < 90; t++)
        {
            Step(plain, 1, run);
            Step(dashing, 1, t == 10 ? run with { Dash = true } : run);   // до перезарядки — не рывок
            Assert.Equal(plain.World.Get<RootVelocity>(p1).Value, dashing.World.Get<RootVelocity>(p2).Value);
            a = dashing.World.Get<AnimatorAction>(p2);
            Assert.Equal(Vector3.Zero, a.RootDelta);
            maxBoost = MathF.Max(maxBoost, dashing.World.Get<PlayerControl>(p2).Boost.Length());
        }
        c = dashing.World.Get<PlayerControl>(p2);
        Assert.False(c.Dashing);
        Assert.Equal(1, c.Dashes);
        Assert.Equal((0f, Vector3.Zero), (c.Invulnerable, c.Boost));
        Assert.Equal(ActionKind.None, a.Kind);
        Assert.Equal(rules.DashSpeed, maxBoost, 4);
        var gain = dashing.World.Get<Transform>(p2).Position - plain.World.Get<Transform>(p1).Position;
        Assert.True(MathF.Abs(gain.Z - rules.DashDistance) < 0.05f && MathF.Abs(gain.X) < 1e-3f, $"прибавка рывка {gain}, ждали {rules.DashDistance} м");
        Clean(dashing);
    }

    [Fact]
    public void Dash_Standing_GoesBackFromFacing()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 10);
        Step(sim, 1, new PlayerCommand { Dash = true });
        Assert.Equal(TestAssets.Assets.Actions.Clip[Data.ActionSlots.EvadeBack], sim.World.Get<AnimatorAction>(player).Clip);
        Step(sim, 60);
        var p = sim.World.Get<Transform>(player).Position;
        float distance = sim.World.Resource<CombatRules>().DashDistance;
        Assert.True(MathF.Abs(-p.Z - distance) < 0.05f && MathF.Abs(p.X) < 1e-3f, $"{p}: курс 0 — назад по −Z на {distance} м");
        Assert.Equal(Vector3.Zero, sim.World.Get<RootVelocity>(player).Value);
        Clean(sim);
    }

    /// <summary>
    /// Рывок и удар в одном тике — удар из рывка сразу (прибавка доживает своё); рывок посреди удара — прерывает удар (снимок позы).
    /// </summary>
    [Fact]
    public void AttackFromDash_Immediate_DashInterruptsAttack()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 5);
        Step(sim, 1, new PlayerCommand { Dash = true, Attack = true });
        var a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal((ActionKind.Attack, ActionKind.Dash), (a.Kind, a.From));
        Step(sim, 5);
        Assert.True(sim.World.Get<PlayerControl>(player).Dashing);
        Assert.NotEqual(Vector3.Zero, sim.World.Get<PlayerControl>(player).Boost);
        Step(sim, 80);
        Step(sim, 1, new PlayerCommand { Attack = true });
        Step(sim, 20);
        int snaps = sim.World.Get<AnimatorAction>(player).Snapshots;
        Step(sim, 1, new PlayerCommand { Dash = true });
        a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal((ActionKind.Dash, ActionKind.Attack, snaps + 1), (a.Kind, a.From, a.Snapshots));
        Clean(sim);
    }

    // ── удар врага ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ИИ: подошедший враг бьёт по таймеру (новый таймер 1,1…2,5 с), ход врага в ударе — 0,35 желаемой; мирные враги
    /// (<c>combat.enemiesAttack=false</c>) не бьют, а удар командой врагу (<see cref="EnemyCommand.Attack"/>) — бьют.
    /// </summary>
    [Fact]
    public void Enemies_SwingByTimerWhenClose_PeacefulOff_CommandAttack()
    {
        var sim = Create("arena.enemies=1");
        var enemy = Enemy(sim);
        float timer = sim.World.Get<EnemyAttack>(enemy).Timer;
        Assert.InRange(timer, EnemyAiRules.FirstTimer, EnemyAiRules.FirstTimer + EnemyAiRules.FirstTimerSpread);
        int ticks = 0;
        while (sim.World.Get<AnimatorAction>(enemy).AttackSerial == 0 && ticks++ < 1200) Step(sim, 1);
        var a = sim.World.Get<AnimatorAction>(enemy);
        Assert.Equal(ActionKind.Attack, a.Kind);
        Assert.True(sim.World.Get<EnemyAiTerms>(enemy).Distance < EnemyAiRules.Reach);
        Assert.InRange(sim.World.Get<EnemyAttack>(enemy).Timer, EnemyAiRules.NextTimer, EnemyAiRules.NextTimer + EnemyAiRules.NextTimerSpread);
        Assert.Equal(ActionRules.AttackMoveScale, ActionLayer.MoveScale(a, agile: false));
        Clean(sim);

        var peaceful = Create("arena.enemies=1", "combat.enemiesAttack=false");
        var e2 = Enemy(peaceful);
        Step(peaceful, 1200);
        Assert.Equal(0, peaceful.World.Get<AnimatorAction>(e2).AttackSerial);
        Assert.Equal(0, peaceful.World.Get<EnemyAttack>(e2).Timer);
        peaceful.Enqueue(new EnemyCommand { Slot = 1, Attack = 5 });   // stab1
        Step(peaceful, 1);
        a = peaceful.World.Get<AnimatorAction>(e2);
        Assert.Equal((ActionKind.Attack, 4, 1), (a.Kind, a.Attack, a.AttackSerial));
        peaceful.Enqueue(new EnemyCommand { Slot = 1, Attack = 1 });   // во время удара — не может действовать: ничего
        Step(peaceful, 1);
        Assert.Equal((4, 1), (peaceful.World.Get<AnimatorAction>(e2).Attack, peaceful.World.Get<AnimatorAction>(e2).AttackSerial));
        Clean(peaceful);
    }

    /// <summary>Постройка заново (R): действие и рывок — с нуля, номер удара и счёт снимков — прежние.</summary>
    [Fact]
    public void Rebuild_KeepsAttackSerial_ResetsActionAndControl()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        Step(sim, 2, new PlayerCommand { Attack = true, Dash = true });
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim, 1);
        var a = sim.World.Get<AnimatorAction>(player);
        Assert.Equal((ActionKind.None, 1, 1f), (a.Kind, a.AttackSerial, a.SnapFade));
        Assert.False(sim.World.Get<PlayerControl>(player).Dashing);
        Clean(sim);
    }

    // ── инварианты ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ActionFault.AttackWhenNotAllowed, "animation.actions", "начат, когда бить было нельзя")]
    [InlineData(ActionFault.SerialDecreased, "animation.actions", "номер удара убыл: 7 → 6")]
    [InlineData(ActionFault.TimeBeyondDuration, "animation.actions", "время действия")]
    [InlineData(ActionFault.DashNaN, "combat.control", "рывок идёт NaN с")]
    [InlineData(ActionFault.EnemyTimerNegative, "combat.control", "таймер удара -1")]
    public void Fault_CaughtOnItsTick(ActionFault fault, string id, string details)
    {
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=1", "combat.enemiesAttack=false"), GameStages.All,
            b => GameCore.Configure(b, TestAssets.Assets), [.. SwordSimulation.Modules(), new ActionFaultModule(fault)]);
        Step(sim, (int)ActionFaultModule.Tick);
        Clean(sim);
        Step(sim, 1);
        var first = sim.Invariants.Recent().First(v => v.Id == id);
        Assert.Equal((ActionFaultModule.Tick, GameStages.End), (first.Tick, first.Stage));
        Assert.Contains(details, first.Details);
    }

    // ── жест клиента ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Затухание жеста — по времени: при 60 Гц — ×0,85 за кадр, как SWORD; при 120 Гц два кадра — как один кадр 60 Гц. Выбор — по
    /// экрану: камера SWORD по умолчанию смотрит на бойца с курсом 0 сбоку, его «вправо» на экране — влево, поэтому жест мышью влево
    /// выбирает замах вправо бойца (slash2), вправо — влево бойца (slash4); в SWORD (экран без камеры) было бы наоборот.
    /// </summary>
    [Fact]
    public void Gesture_TimeDecay_StrikeNearestOnScreen()
    {
        var m = StrikeGesture.Accumulate(new Vector2(100, 0), 0, 0, 1f / 60);
        Assert.Equal(85f, m.X, 3);
        var twice = StrikeGesture.Accumulate(StrikeGesture.Accumulate(new Vector2(100, 0), 0, 0, 1f / 120), 0, 0, 1f / 120);
        Assert.Equal(85f, twice.X, 3);
        Assert.Equal(new Vector2(3, 4), StrikeGesture.Accumulate(Vector2.Zero, 3, 4, 1f / 60));

        var camera = new RtsCamera();
        camera.Rebuild(1280, 720);
        Vector2[] swings = [.. Table[..4].Select(a => a.SwingDir)];
        Assert.Equal(Vector2.Zero, StrikeGesture.Pick(new Vector2(-8, 0), swings, camera.ViewProj, 1280, 720, Vector3.Zero, 0));
        Assert.Equal(swings[1], StrikeGesture.Pick(new Vector2(-40, 0), swings, camera.ViewProj, 1280, 720, Vector3.Zero, 0));
        Assert.Equal(swings[3], StrikeGesture.Pick(new Vector2(40, 0), swings, camera.ViewProj, 1280, 720, Vector3.Zero, 0));
        // вниз по экрану — рубящий сверху (slash1 или slash3: оба почти вертикальные)
        var down = StrikeGesture.Pick(new Vector2(0, 40), swings, camera.ViewProj, 1280, 720, Vector3.Zero, 0);
        Assert.True(down == swings[0] || down == swings[2], $"{down}");
        // выбранное направление в команде выбирает тот же удар в симуляции
        var combo = AttackCombo.Start;
        Assert.Equal(1, AttackPicker.Pick(ref combo, 0, swings[1], false, 0, Table, 4));
    }

    // ── агент ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Кончик клинка по тикам удара агенту: <c>watch.add</c> по полям <c>Blade.Tip.*</c> и <c>AnimatorAction.*</c>, <c>entity.history</c> —
    /// строка на тик, кончик движется в замахе; состояние удара и рывка — <c>entity.get</c> (<c>AnimatorAction</c>,
    /// <c>PlayerControl</c>, <c>AttackCombo</c>); удар — <c>command.send</c> полем <c>Attack</c>.
    /// </summary>
    [Fact]
    public async Task Agent_BladeTipByTick_AttackStateByEntityGet()
    {
        var sim = Create();
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

            await Call("time.step", new JsonObject { ["ticks"] = 2 });
            await Call("watch.add", new JsonObject
            {
                ["entities"] = new JsonArray("player"),
                ["fields"] = new JsonArray("Blade.Tip.X", "Blade.Tip.Y", "Blade.Tip.Z", "AnimatorAction.Kind", "AnimatorAction.Time"),
            });
            await Call("command.send", new JsonObject { ["command"] = "PlayerCommand", ["fields"] = new JsonObject { ["Attack"] = true } });
            await Call("time.step", new JsonObject { ["ticks"] = 30 });
            var rows = (await Call("entity.history", new JsonObject { ["entity"] = "player" }))["rows"]!.AsArray();
            Assert.Equal(30, rows.Count);
            Assert.Equal((int)ActionKind.Attack, rows[0]!["values"]!["AnimatorAction.Kind"]!.GetValue<int>());
            var tips = rows.Select(r => new Vector3((float)r!["values"]!["Blade.Tip.X"]!.GetValue<double>(),
                (float)r["values"]!["Blade.Tip.Y"]!.GetValue<double>(), (float)r["values"]!["Blade.Tip.Z"]!.GetValue<double>())).ToArray();
            Assert.True(Vector3.Distance(tips[0], tips[^1]) > 0.1f, $"кончик не двинулся: {tips[0]} → {tips[^1]}");

            var state = await Call("entity.get", new JsonObject
            {
                ["entity"] = "player", ["components"] = new JsonArray("AnimatorAction", "PlayerControl", "AttackCombo"),
            });
            var text = state.ToJsonString();
            Assert.Contains("AttackSerial", text);
            Assert.Contains("SwingBuffer", text);
            Assert.Contains("Next", text);
            var pose = await Call("anim.pose", new JsonObject { ["entity"] = "player", ["joints"] = new JsonArray("hand_r") });
            Assert.Equal("slash1", pose["action"]!["attack"]!.GetValue<string>());
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }
}

public enum ActionFault { AttackWhenNotAllowed, SerialDecreased, TimeBeyondDuration, DashNaN, EnemyTimerNegative }

/// <summary>Модуль-неисправность слоя действий и управления (6в): в конце тика <see cref="Tick"/> портит состояние.</summary>
public sealed class ActionFaultModule(ActionFault fault) : IModule
{
    public const ulong Tick = 25;

    public string Id => "fault";

    public void Configure(SimulationBuilder b) => b.System(GameStages.End, new Fault(fault));

    private sealed class Fault(ActionFault fault) : IContinuousSystem
    {
        private Query _player = null!, _enemy = null!;

        public void Setup(SystemSetup s)
        {
            s.Label(new SystemLabel("fault.action")).Writes<AnimatorAction>().Writes<PlayerControl>().Writes<EnemyAttack>();
            _player = s.Query().With<PlayerTag>().Write<AnimatorAction>().Write<PlayerControl>();
            _enemy = s.Query().With<EnemyTag>().Write<EnemyAttack>();
        }

        public void Run(in SystemContext ctx)
        {
            ulong tick = ctx.World.Resource<SimTime>().Tick;
            if (tick != Tick && !(fault == ActionFault.SerialDecreased && tick == Tick - 1)) return;
            var attack = TestAssets.Assets.Strikes.Attacks[0];
            foreach (var chunk in ctx.Iterate(_player))
            {
                var a = chunk.Write<AnimatorAction>();
                var c = chunk.Write<PlayerControl>();
                for (int i = 0; i < a.Length; i++)
                    switch (fault)
                    {
                        case ActionFault.AttackWhenNotAllowed:
                            a[i] = a[i] with
                            {
                                Kind = ActionKind.Attack, Attack = 0, Clip = attack.Clip, Upper = attack.Upper, Duration = attack.Duration, Time = 0.1f,
                                Weight = 0.5f, From = ActionKind.Attack, FromTime = 0.05f, FromAttack = 0,
                            };
                            break;
                        case ActionFault.SerialDecreased:
                            a[i].AttackSerial = tick == Tick ? 6 : 7;
                            break;
                        case ActionFault.TimeBeyondDuration:
                            a[i] = a[i] with
                            {
                                Kind = ActionKind.Attack, Attack = 0, Clip = attack.Clip, Upper = attack.Upper, Duration = attack.Duration,
                                Time = attack.Duration + 0.5f, Weight = 0.5f,
                            };
                            break;
                        case ActionFault.DashNaN:
                            c[i].Dashing = true;
                            c[i].DashElapsed = float.NaN;
                            c[i].DashDirection = Vector3.UnitZ;
                            break;
                    }
            }
            if (fault != ActionFault.EnemyTimerNegative) return;
            foreach (var chunk in ctx.Iterate(_enemy))
                foreach (ref var t in chunk.Write<EnemyAttack>()) t.Timer = -1;
        }
    }
}
