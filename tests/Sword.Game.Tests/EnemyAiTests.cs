using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Arena;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;

namespace Sword.Game.Tests;

/// <summary>
/// ИИ врагов: слагаемые намерения <see cref="EnemyAi.Think"/> объясняют скорость, расхождение — в порядке мест, время арены —
/// float; команды арены — до <c>Think</c> того же тика; детерминизм и реплей.
/// </summary>
public sealed class EnemyAiTests
{
    private static Simulation Create(params string[] sets) => TestAssets.Simulation(ConfigSet.Empty(sets));

    [Fact]
    public void Terms_ExplainDesiredVelocity()
    {
        var roots = new Vector3[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        roots[0] = new Vector3(0, 0, 0);
        roots[1] = new Vector3(0, 0, 5);       // 5 м перед игроком по +Z
        roots[2] = new Vector3(0.5f, 0, 5);    // сосед в 0,5 м справа (по −X от первого не толкает — толкает первого к −X)
        present[0] = present[1] = present[2] = true;
        var intent = EnemyAi.Think(1, roots[1], 0.25f, roots[0], 3f, roots, present, out var t);
        Assert.Equal(5f, t.Distance);
        Assert.Equal(3.2f, t.ApproachSpeed);                                  // (5 − 1,8)·2,5 = 8 → предел 3,2
        Assert.Equal(MathF.Sin(3f * 0.7f + 0.25f * 10) * 0.7f, t.SwaySpeed);
        Assert.Equal(1, t.Neighbours);
        Assert.Equal(new Vector3(-(1.4f - 0.5f) * 2, 0, 0), t.Separation);
        Assert.Equal(MathF.PI, MathF.Abs(intent.FacingGoal));                 // на игрока — по −Z
        var dir = new Vector3(0, 0, -1);
        var side = new Vector3(dir.Z, 0, -dir.X);
        var sum = dir * t.ApproachSpeed + side * t.SwaySpeed + t.Separation;
        Assert.True(Vector3.Distance(sum, intent.DesiredVelocity) < 1e-5f, $"{sum} ≠ {intent.DesiredVelocity}");
    }

    [Fact]
    public void Separation_AddsInSlotOrder()
    {
        // Три соседа, у которых сумма толчков в другом порядке даёт другие биты: ИИ складывает по местам 1…32.
        var rng = new Random(7);
        var roots = new Vector3[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            roots[0] = new Vector3(30, 0, 0);
            var self = new Vector3((float)rng.NextDouble(), 0, (float)rng.NextDouble());
            Array.Clear(present);
            present[0] = present[2] = true;
            roots[2] = self;
            for (int j = 1; j <= 4; j++)
            {
                if (j == 2) continue;
                roots[j] = self + new Vector3((float)rng.NextDouble() - 0.5f, 0, (float)rng.NextDouble() - 0.5f);
                present[j] = true;
            }
            var intent = EnemyAi.Think(2, self, 0.5f, roots[0], 1f, roots, present, out var terms);
            if (terms.Neighbours != 3) continue;
            Vector3 Push(int j)
            {
                var d = self - roots[j];
                float l = d.Length();
                return d / l * (1.4f - l) * 2f;
            }
            var to = roots[0] - self;
            float dist = to.Length();
            var dir = to / dist;
            var start = dir * Math.Clamp((dist - 1.8f) * 2.5f, -2f, 3.2f) + new Vector3(dir.Z, 0, -dir.X) * MathF.Sin(1f * 0.7f + 0.5f * 10) * 0.7f;
            var bySlot = start + Push(1) + Push(3) + Push(4);
            var reversed = start + Push(4) + Push(3) + Push(1);
            if (bySlot == reversed) continue;
            Assert.Equal(bySlot, intent.DesiredVelocity);
            Assert.NotEqual(reversed, intent.DesiredVelocity);
            return;
        }
        Assert.Fail("не нашлось раскладки, где порядок сложения меняет биты");
    }

    /// <summary>
    /// Случайные толпы (вплотную, на игроке, на корне соседа, с y ≠ 0): скорость в плоскости XZ и конечна, дистанция горизонтальна
    /// и не меньше <c>MinDistance</c>, соседи — ровно враги ближе <c>Personal</c>, толчок не больше <c>Personal·SeparationGain</c>,
    /// слагаемые объясняют скорость, курс — на игрока.
    /// </summary>
    [Fact]
    public void Think_RandomCrowds_TermsExplainVelocity_NeighboursWithinPersonal()
    {
        var rng = new Random(4242);
        var roots = new Vector3[ArenaRules.MaxFighters];
        var seeds = new float[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        float R() => (float)(rng.NextDouble() * 2 - 1);
        static float Flat(Vector3 v)
        {
            v.Y = 0;   // по горизонтали
            return v.Length();
        }
        int cases = 0, pushed = 0, backing = 0, capped = 0, onPlayer = 0, coincident = 0;
        for (int run = 0; run < 200; run++)
        {
            int enemies = 1 + run % ArenaState.MaxEnemies;
            Array.Clear(present);
            var player = new Vector3(R() * 20, 0, R() * 20);
            for (int i = 0; i <= enemies; i++)
            {
                roots[i] = i switch
                {
                    0 => player,
                    _ when i % 7 == 3 => player,                                                   // на игроке
                    _ when i % 5 == 4 && i > 1 => roots[i - 1],                                     // на корне соседа
                    _ when i % 3 == 0 => roots[i - 1] + new Vector3(R() * 0.8f, 0, R() * 0.8f),     // вплотную к соседу
                    _ => player + new Vector3(R() * 8, R(), R() * 8),                              // y ≠ 0 — не в счёт
                };
                (seeds[i], present[i]) = ((float)rng.NextDouble(), true);
            }
            float time = 0;
            for (int k = 0, n = rng.Next(1, 20000); k < n; k++) time += 1f / 120f;
            for (int slot = 1; slot <= enemies; slot++)
            {
                var intent = EnemyAi.Think(slot, roots[slot], seeds[slot], player, time, roots, present, out var t);
                string at = $"прогон {run}, место {slot}";
                var v = intent.DesiredVelocity;
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Z) && v.Y == 0 && float.IsFinite(intent.FacingGoal), $"{at}: {v}, курс {intent.FacingGoal}");
                Assert.Equal(MathF.Max(Flat(player - roots[slot]), EnemyAiRules.MinDistance), t.Distance);
                Assert.InRange(t.ApproachSpeed, EnemyAiRules.ApproachMin, EnemyAiRules.ApproachMax);
                int expected = 0;
                for (int j = 1; j <= enemies; j++)
                {
                    if (j == slot) continue;
                    float l = Flat(roots[slot] - roots[j]);
                    if (l < EnemyAiRules.Personal && l > EnemyAiRules.MinDistance) expected++;
                    else if (l <= EnemyAiRules.MinDistance) coincident++;
                }
                Assert.True(expected == t.Neighbours, $"{at}: соседей {t.Neighbours}, ближе {EnemyAiRules.Personal} м — {expected}");
                Assert.True(Flat(t.Separation) <= t.Neighbours * EnemyAiRules.Personal * EnemyAiRules.SeparationGain + 1e-4f, $"{at}: {t.Separation}");
                var to = player - roots[slot];
                var dir = new Vector3(to.X, 0, to.Z) / t.Distance;
                var sum = dir * t.ApproachSpeed + new Vector3(dir.Z, 0, -dir.X) * t.SwaySpeed + t.Separation;
                Assert.True(Vector3.Distance(sum, v) < 1e-4f, $"{at}: слагаемые {sum}, скорость {v}");
                if (t.Distance > EnemyAiRules.MinDistance)
                    Assert.True(MathF.Abs(MathF.IEEERemainder(intent.FacingGoal - MathF.Atan2(dir.X, dir.Z), MathF.Tau)) < 1e-5f, $"{at}: курс {intent.FacingGoal}");
                cases++;
                pushed += t.Neighbours > 0 ? 1 : 0;
                backing += t.ApproachSpeed < 0 ? 1 : 0;
                capped += t.ApproachSpeed == EnemyAiRules.ApproachMax ? 1 : 0;
                onPlayer += t.Distance == EnemyAiRules.MinDistance ? 1 : 0;
            }
        }
        // Покрытие (зерно 4242): случаев 3204 — расхождение 2092, отступ 936, предел 3,2 — 2057, на игроке 665, совпавших соседей 2770.
        Assert.True(pushed > 1_000 && backing > 500 && capped > 1_000 && onPlayer > 300 && coincident > 1_000,
            $"случаев {cases}: расхождение {pushed}, отступ {backing}, предел 3,2 — {capped}, на игроке {onPlayer}, совпавших соседей {coincident}");
    }

    // ── в мире ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// В Intent после ИИ врагов (корни ещё до движения тика) — намерение каждого врага обязано быть <see cref="EnemyAi.Think"/>
    /// по арене этого места: игрок, враги по местам, время арены. Проверяется каждый тик, в том числе тики команд арены.
    /// </summary>
    private sealed class IntentProbe
    {
        public int Checked, CommandTicksChecked, Mismatches;
        public float Time;
        public string? First;
        public readonly HashSet<ulong> CommandTicks = [];
        private readonly Vector3[] _roots = new Vector3[ArenaRules.MaxFighters];
        private readonly float[] _seeds = new float[ArenaRules.MaxFighters];
        private readonly bool[] _present = new bool[ArenaRules.MaxFighters];
        private float _expectedTime;

        public StageProbe Module => new("intent_probe", GameStages.Intent, Check, after: GameLabels.EnemyAi);

        private void Check(WorldReader w, ulong tick)
        {
            _expectedTime += 1f / 120f;   // время арены — float, шаг тика прибавляется первым делом (до Intent)
            Time = w.Resource<SimTime>().Time;
            if (BitConverter.SingleToInt32Bits(Time) != BitConverter.SingleToInt32Bits(_expectedTime))
                Fail($"тик {tick}: время арены {Time:R}, ждали {_expectedTime:R}");
            Array.Clear(_present);
            foreach (var chunk in w.Iterate(w.Query().With<Fighter>()))
                foreach (var e in chunk.Entities)
                {
                    int slot = w.Get<FighterSlot>(e).Index;
                    (_roots[slot], _seeds[slot], _present[slot]) = (w.Get<Transform>(e).Position, w.Get<FighterSeed>(e).Value, true);
                }
            foreach (var chunk in w.Iterate(w.Query().With<EnemyTag>()))
                foreach (var e in chunk.Entities)
                {
                    int slot = w.Get<FighterSlot>(e).Index;
                    var expected = EnemyAi.Think(slot, _roots[slot], _seeds[slot], _roots[0], Time, _roots, _present, out var terms);
                    var actual = w.Get<FighterCommand>(e);
                    var gaze = w.Get<GazeTarget>(e);
                    bool same = expected.DesiredVelocity == actual.DesiredVelocity && expected.FacingGoal == actual.FacingGoal
                        && gaze.Has && gaze.Point == _roots[0] && terms.Equals(w.Get<EnemyAiTerms>(e));
                    if (!same) Fail($"тик {tick}, место {slot}: намерение {actual.DesiredVelocity}/{actual.FacingGoal}, ждали {expected.DesiredVelocity}/{expected.FacingGoal}");
                    Checked++;
                    if (CommandTicks.Contains(tick)) CommandTicksChecked++;
                }
        }

        private void Fail(string what)
        {
            Mismatches++;
            First ??= what;
        }
    }

    private static ArenaCommand Change(int n) => new() { Action = ArenaAction.ChangeEnemies, Enemies = n };
    private static readonly ArenaCommand Reset = new() { Action = ArenaAction.Reset };

    /// <summary>Сценарий погони с командами арены: игрок бежит, PageUp, R, PageDown ×2, 32 врага, игрок встаёт; −5, R, +2.</summary>
    private static readonly Dictionary<int, ArenaCommand[]> ArenaScript = new()
    {
        [40] = [Change(1)],
        [150] = [Reset],
        [151] = [Change(3), Reset, Change(-1)],
        [260] = [Change(-1), Change(-1)],
        [330] = [new ArenaCommand { Action = ArenaAction.SetEnemies, Enemies = 32 }],
        [420] = [Change(-5), Reset, Change(2)],   // PageDown, R, затем PageUp: места 28, 29 — вокруг центра после сброса
    };

    private static void ScriptStep(Simulation sim, int t)
    {
        if (t < 400) sim.Enqueue(new PlayerCommand { Move = t < 200 ? new Vector2(1, 0.3f) : new Vector2(-0.5f, -1), HasGaze = t > 60, Gaze = new Vector3(8, 0, -8) });
        if (ArenaScript.TryGetValue(t, out var commands))
            foreach (var c in commands) sim.Enqueue(c);
        sim.Export.BeginFrame();
        sim.Step();
    }

    [Fact]
    public void EveryTick_EnemyIntent_IsThinkOverArenaBeforeMovement_IncludingArenaCommandTicks()
    {
        var probe = new IntentProbe();
        foreach (int t in ArenaScript.Keys) probe.CommandTicks.Add((ulong)t);
        var sim = TestAssets.WithModules(ConfigSet.Empty("arena.enemies=10"), probe.Module);
        for (int t = 0; t < 500; t++) ScriptStep(sim, t);
        Assert.True(probe.Mismatches == 0, probe.First);
        Assert.True(probe.Checked > 9_000, $"проверено {probe.Checked}");
        // Тики команд: новые, сброшенные и оставшиеся враги думают по арене после команд (PageUp — сосед уже есть, R —
        // игрок в центре, враги в новых точках).
        Assert.Equal(11 + 11 + 13 + 11 + 32 + 29, probe.CommandTicksChecked);
        Assert.Equal(29, sim.World.Resource<ArenaState>().Enemies);
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void CommandTick_NewEnemyThinksAndMoves_SameTick()
    {
        // Команда арены — до Think того же тика: новый враг не стоит тик «постройки», а сразу идёт к игроку.
        var probe = new IntentProbe();
        var sim = TestAssets.WithModules(ConfigSet.Empty(), probe.Module);
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.SetEnemies, Enemies = 2 });
        sim.Export.BeginFrame();
        sim.Step();
        Assert.True(probe.Mismatches == 0, probe.First);
        Assert.Equal(2, probe.Checked);
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities)
            {
                Assert.True(sim.World.Get<FighterCommand>(e).DesiredVelocity.Length() > 1, "новый враг стоит тик постройки");
                Assert.NotEqual(Vector3.Zero, sim.World.Get<RootVelocity>(e).Value);
            }
    }

    [Fact]
    public void TenEnemies_SurroundPlayer_AtKeepDistance_AndStop()
    {
        var sim = Create("arena.enemies=10");
        var w = sim.World;
        var player = GameCore.Player(w);
        float maxSpawn = 0;
        foreach (var chunk in w.Iterate(w.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities) maxSpawn = MathF.Max(maxSpawn, w.Get<Transform>(e).Position.Length());
        Assert.True(maxSpawn > 5, $"кольцо спауна ближе 5 м: {maxSpawn}");

        for (int t = 0; t < 1200; t++) { sim.Export.BeginFrame(); sim.Step(); }   // 10 с, игрок стоит в центре

        Assert.Equal(Vector3.Zero, w.Get<Transform>(player).Position);
        var roots = new List<Vector3>();
        foreach (var chunk in w.Iterate(w.Query().With<EnemyTag>()))
            foreach (var e in chunk.Entities)
            {
                var root = w.Get<Transform>(e);
                var terms = w.Get<EnemyAiTerms>(e);
                float dist = new Vector2(root.Position.X, root.Position.Z).Length();
                Assert.InRange(dist, 1.6f, 2.4f);                                         // кольцо у 1,8 м (расхождение отодвигает)
                Assert.InRange(terms.ApproachSpeed, -1f, 1f);                             // к игроку уже не идёт (1 м/с — 2,2 м)
                Assert.InRange(w.Get<RootVelocity>(e).Value.Length(), 0, 1.2f);           // покачивание, не бег
                Assert.False(w.Get<LegacyLocomotionState>(e).Run, "враг бежит в кольце");
                float toPlayer = MathF.Atan2(-root.Position.X, -root.Position.Z);
                Assert.InRange(MathF.Abs(MathF.IEEERemainder(toPlayer - root.Facing, MathF.Tau)), 0, 0.1f);   // лицом к игроку
                roots.Add(root.Position);
            }
        Assert.Equal(10, roots.Count);
        float closest = float.MaxValue;
        for (int i = 0; i < roots.Count; i++)
            for (int j = i + 1; j < roots.Count; j++) closest = MathF.Min(closest, Vector3.Distance(roots[i], roots[j]));
        Assert.True(closest > 0.6f, $"враги слиплись: ближайшая пара {closest} м");
        Assert.Equal(0, sim.Invariants.Total);
    }

}
