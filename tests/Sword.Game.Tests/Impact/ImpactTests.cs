using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;
using Sword.Game.Data;
using Sword.Game.Impact;
using Sword.Game.Movement;

namespace Sword.Game.Tests.Impact;

/// <summary>
/// Сила удара: импульс по массе оружия и точке касания, баланс по точке захвата, отклик (вздрагивание по силе, пошатывание с толчком,
/// падение), игрок не теряет управления, отдача оружия атакующего.
/// </summary>
public sealed class ImpactTests
{
    private static readonly ImpactRules Rules = ImpactRules.From(new ImpactSettings());

    /// <summary>
    /// Порядки величин: молот 9 м/с в грудь — 45–75 Н·с, сдвиг баланса выше порога пошатывания и ниже порога падения; рубящий меч 12 м/с —
    /// 8–25 Н·с, вздрагивание; удар в конец конечности (далеко от центра масс) передаёт меньше, чем в центр.
    /// </summary>
    [Fact]
    public void Impulse_MaulStaggers_SwordFlinches_EdgeTransfersLess()
    {
        var chest = new Vector3(0, 0.35f, 0.15f);
        var maul = ImpactPhysics.Impulse(new Vector3(0, 0, -9), chest, blunt: true, Rules, out float maulMomentum);
        Assert.InRange(maul.Length(), 45, 75);
        float shift = ImpactPhysics.CaptureShift(maul, Rules);
        Assert.Equal(HitLevel.Stagger, ImpactPhysics.Level(shift, player: false, Rules, out _));
        Assert.True(maul.Length() / maulMomentum > 0.8f, "молот должен почти встать в теле");

        var sword = ImpactPhysics.Impulse(new Vector3(-12, 0, 0), chest, blunt: false, Rules, out float swordMomentum);
        Assert.InRange(sword.Length(), 8, 25);
        Assert.Equal(HitLevel.Flinch, ImpactPhysics.Level(ImpactPhysics.CaptureShift(sword, Rules), false, Rules, out float strength));
        Assert.InRange(strength, Rules.MinFlinch, 0.999f);
        Assert.True(sword.Length() / swordMomentum < 0.5f, "клинок режет и идёт дальше");

        var hand = ImpactPhysics.Impulse(new Vector3(0, 0, -9), new Vector3(0.7f, 0.3f, 0), blunt: true, Rules, out _);
        Assert.True(hand.Length() < maul.Length(), $"удар по кисти {hand.Length():F1} не меньше, чем в грудь {maul.Length():F1}");
    }

    /// <summary>Баланс копится: два удара подряд — дальше порога, через секунду — гаснет; игрок только вздрагивает.</summary>
    [Fact]
    public void Balance_Accumulates_Decays_PlayerOnlyFlinches()
    {
        float one = Rules.StaggerShift * 0.7f;
        Assert.Equal(HitLevel.Flinch, ImpactPhysics.Level(one, false, Rules, out _));
        float two = ImpactPhysics.Decay(one, 0.1f, Rules) + one;
        Assert.Equal(HitLevel.Stagger, ImpactPhysics.Level(two, false, Rules, out _));
        Assert.True(ImpactPhysics.Decay(two, 2f, Rules) < 0.1f * two);
        Assert.Equal(HitLevel.Flinch, ImpactPhysics.Level(Rules.FallShift * 3, player: true, Rules, out float s));
        Assert.Equal(1f, s);
        Assert.Equal(HitLevel.Knockdown, ImpactPhysics.Level(Rules.FallShift * 1.01f, false, Rules, out _));
    }

    /// <summary>
    /// Молот в грудь врага: отклик — пошатывание, враг играет сильный клип реакции и отъезжает назад от удара (толчок), вздрагивание
    /// добавкой; лёгкий удар клинком — обычная реакция, толчок меньше. Инварианты чисты.
    /// </summary>
    [Fact]
    public void MaulToChest_StaggersAndPushesBack_LightSwordDoesLess()
    {
        (HitResponse Response, AnimatorAction Action, float Moved) Run(HitKind kind, float damage)
        {
            var sim = Create();
            var enemy = Enemy(sim);
            for (int t = 0; t < 5; t++) sim.Step();
            var before = sim.World.Get<Transform>(enemy).Position;
            sim.Export.BeginFrame();
            sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.Torso, Damage = damage, Kind = kind });
            sim.Step();
            var response = Assert.Single(sim.Export.Frame<Event<HitResponse>>().ToArray()).Record.Value;
            var action = sim.World.Get<AnimatorAction>(enemy);
            Assert.True(sim.World.Get<Flinch>(enemy).Count == 1, "враг не вздрогнул");
            for (int t = 0; t < 30; t++) sim.Step();
            var moved = sim.World.Get<Transform>(enemy).Position - before;
            Clean(sim);
            // назад — по направлению удара (от игрока к врагу, +Z)
            return (response, action, moved.Z);
        }
        var maul = Run(HitKind.Blunt, 40);
        Assert.Equal(HitLevel.Stagger, maul.Response.Level);
        Assert.Equal(ActionKind.HitReact, maul.Action.Kind);
        Assert.Equal(TestAssets.Assets.Actions.Clip[ActionSlots.HitBig], maul.Action.Clip);
        Assert.True(maul.Response.Push.Z > HitReactionSystemBigPush(), $"толчок {maul.Response.Push}");

        var sword = Run(HitKind.Slash, 20);
        Assert.Equal(HitLevel.Flinch, sword.Response.Level);
        Assert.Equal(ActionKind.HitReact, sword.Action.Kind);
        Assert.NotEqual(TestAssets.Assets.Actions.Clip[ActionSlots.HitBig], sword.Action.Clip);
        Assert.True(sword.Response.Strength < 1);
        Assert.True(maul.Moved > sword.Moved + 0.05f, $"молот сдвинул на {maul.Moved:F3} м, меч — на {sword.Moved:F3} м");
    }

    private static float HitReactionSystemBigPush() => Combat.HitReactionSystem.BigPush;

    /// <summary>
    /// Настоящий удар игрока по врагу перед ним: попадание даёт атакующему отдачу — клип удара замедлен (оружие вязнет в теле), затем
    /// идёт с прежней скоростью.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void PlayerStrike_HasRecoil()
    {
        var sim = Create(BloodBodyTests.Pak.Value);
        var player = GameCore.Player(sim.World);
        bool hit = false, held = false;
        for (int t = 0; t < 400 && !held; t++)
        {
            sim.Export.BeginFrame();
            sim.Enqueue(new PlayerCommand { Attack = true, HasGaze = true, Gaze = new Vector3(0, 0, 1.1f) });
            sim.Step();
            foreach (var h in sim.Export.Frame<Event<Hit>>())
                if (h.Record.Value.Attacker == player) hit = true;
            var a = sim.World.Get<AnimatorAction>(player);
            held |= hit && a.Hold > 0 && a.HoldRate < 1;
        }
        Assert.True(hit, "удар не попал");
        Assert.True(held, "у попадания нет отдачи");
        for (int t = 0; t < 30; t++) sim.Step();
        Assert.Equal(0f, sim.World.Get<AnimatorAction>(player).Hold);
        Clean(sim);
    }

    /// <summary>Игрок от удара молотом только вздрагивает и отъезжает: действия реакции нет, скорость управления та же.</summary>
    [Fact]
    public void PlayerHitByMaul_FlinchesAndIsPushed_NoReaction()
    {
        var sim = Create();
        var player = GameCore.Player(sim.World);
        for (int t = 0; t < 5; t++) sim.Step();
        sim.Export.BeginFrame();
        sim.Enqueue(new StrikeProbe { Attacker = 1, Victim = 0, Part = BodySegs.Torso, Damage = 40, Kind = HitKind.Blunt });
        sim.Step();
        var r = Assert.Single(sim.Export.Frame<Event<HitResponse>>().ToArray()).Record.Value;
        Assert.Equal(HitLevel.Flinch, r.Level);
        Assert.Equal(1f, r.Strength);
        Assert.Equal(ActionKind.None, sim.World.Get<AnimatorAction>(player).Kind);
        Assert.True(sim.World.Get<Shove>(player).Velocity.Length() > 0.3f);
        Clean(sim);
    }

    /// <summary>
    /// Два сильных удара молотом в таз подряд сбивают врага с ног: тела на физике, корень за тазом, рисуется по телам; отлежавшись,
    /// встаёт клипом вставания (тела снова кинематические, привязки скина нет), затем снова может действовать. Инварианты чисты.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void Knockdown_FallsOnPhysics_ThenStandsUp()
    {
        var sim = Create(BloodBodyTests.Pak.Value);
        var enemy = Enemy(sim);
        for (int t = 0; t < 10; t++) sim.Step();
        var bodies = sim.World.View<IBodyView>();
        var bodyRef = sim.World.Get<BodyRef>(enemy);
        var seen = new List<string>();
        foreach (int part in new[] { BodySegs.Pelvis, BodySegs.Pelvis })
        {
            sim.Export.BeginFrame();
            sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = part, Damage = 40, Kind = HitKind.Blunt });
            sim.Step();
            foreach (var r in sim.Export.Frame<Event<HitResponse>>()) seen.Add($"{r.Record.Value.Level} {r.Record.Value.Push.Length():F2}");
            foreach (var r in sim.Export.Frame<Event<LimbDetached>>()) seen.Add($"{r.Record.Value.Mode}");
        }
        Assert.True(sim.World.Has<Downed>(enemy), $"враг не сбит: {string.Join("; ", seen)}, мёртв {sim.World.Has<Dead>(enemy)}");
        Assert.False(sim.World.Has<Dead>(enemy));
        Assert.True(bodies.Body(bodyRef, BodySegs.Body(BodySegs.Pelvis)).Simulated, "таз сбитого не на физике");
        var start = sim.World.Get<Transform>(enemy).Position;
        bool rose = false;
        for (int t = 0; t < 120 * 7 && !rose; t++)
        {
            sim.Step();
            rose = !sim.World.Has<Downed>(enemy);
        }
        Assert.True(rose, "не встал за 7 с");
        Assert.False(sim.World.Has<Dead>(enemy));
        Assert.Equal(ActionKind.StandUp, sim.World.Get<AnimatorAction>(enemy).Kind);
        for (int k = 0; k < BodySegs.Bodies; k++) Assert.False(bodies.Body(bodyRef, k).Simulated, $"тело {k} вставшего на физике");
        Assert.False(sim.World.View<ISkinView>().TryGet(sim.World.Get<BodyRef>(enemy), out _, out _), "у вставшего осталась привязка скина");
        for (int t = 0; t < 120 * 4 && sim.World.Get<AnimatorAction>(enemy).Kind == ActionKind.StandUp; t++) sim.Step();
        Assert.NotEqual(ActionKind.StandUp, sim.World.Get<AnimatorAction>(enemy).Kind);
        Clean(sim);
    }

    /// <summary>Молот ломает голень живого: не смерть — падает на физику и не встаёт (нога не держит).</summary>
    [Fact]
    [Trait("Category", "Content")]
    public void BrokenLeg_FallsAndStaysDown_Alive()
    {
        var sim = Create(BloodBodyTests.Pak.Value);
        var enemy = Enemy(sim);
        for (int t = 0; t < 10; t++) sim.Step();
        sim.Export.BeginFrame();
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.ShinL, Damage = 40, Kind = HitKind.Blunt });
        sim.Step();
        Assert.Contains(sim.Export.Frame<Event<LimbDetached>>().ToArray(), r => r.Record.Value.Mode == SeverMode.Fracture);
        Assert.True(sim.World.Has<Downed>(enemy) && sim.World.Get<Downed>(enemy).Crippled, "сломанная нога не свалила");
        for (int t = 0; t < 120 * 7; t++) sim.Step();
        Assert.True(sim.World.Has<Downed>(enemy), "встал на сломанной ноге");
        Assert.False(sim.World.Has<Dead>(enemy));
        Clean(sim);
    }

    /// <summary>
    /// Сцена силы удара на паке (реплей <c>artifacts/impact/scene.swreplay</c>, вне git — его показывает <c>Sword.exe --replay</c>): враг 1
    /// пошатывается от молота в грудь, враг 2 сбит двумя ударами в таз и встаёт, враг 3 — сломанная голень (лежит), враг 2 сбит снова
    /// и убит лёжа. Инварианты чисты.
    /// </summary>
    [Fact]
    [Trait("Category", "Content")]
    public void Scene_RecordsReplay()
    {
        using var pak = Sword.Engine.Content.PakSource.Open(TestContent.GamePak());
        string content = pak.ContentHash;
        var sim = Bootstrap.SwordSimulation.Create(ConfigSet.Empty(["arena.enemies=3", "combat.enemiesAttack=false"]), BloodBodyTests.Pak.Value);
        var recorder = sim.Record(new ReplayInfo("test", "tests", content));
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.6f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 2, Position = new Vector2(1.8f, 1.2f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 3, Position = new Vector2(-1.8f, 1.2f), Facing = MathF.PI });
        var downs = new List<int>();
        Entity second = default;
        for (int t = 0; t < 1500; t++)
        {
            void Probe(int victim, int part, float damage) =>
                sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = victim, Part = part, Damage = damage, Kind = HitKind.Blunt });
            if (t == 60) Probe(1, BodySegs.Torso, 30);
            if (t is 120 or 121) Probe(2, BodySegs.Pelvis, 40);
            if (t == 180) Probe(3, BodySegs.ShinR, 40);
            if (t is 900 or 901) Probe(2, BodySegs.Pelvis, 40);
            if (t == 1100) Probe(2, BodySegs.Torso, 200);
            sim.Step();
            if (t == 5) second = Enemy(sim, 2);
            if (second != default && sim.World.Has<Downed>(second) && (downs.Count == 0 || downs[^1] != t - 1)) downs.Add(t);
        }
        Assert.True(downs.Count >= 2, $"враг 2 сбит {downs.Count} раз");
        Assert.True(sim.World.Has<Dead>(second));
        Clean(sim);
        string dir = Path.Combine(Root(), "artifacts", "impact");
        Directory.CreateDirectory(dir);
        recorder.ToReplay().Save(Path.Combine(dir, "scene.swreplay"));
    }

    private static Entity Enemy(Simulation sim, int slot)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет врага {slot}");
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("нет корня репозитория");
    }

    private static Simulation Create(CharacterAssets? assets = null) =>
        Place(Bootstrap.SwordSimulation.Create(ConfigSet.Empty(["arena.enemies=1", "combat.enemiesAttack=false"]), assets ?? TestAssets.Assets));

    private static Simulation Place(Simulation sim)
    {
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.1f), Facing = MathF.PI });
        return sim;
    }

    private static Entity Enemy(Simulation sim)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == 1) return chunk.Entities[i];
        throw new InvalidOperationException("нет врага");
    }

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));
}
