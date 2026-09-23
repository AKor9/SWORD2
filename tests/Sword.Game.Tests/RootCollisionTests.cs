using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Contracts;
using Sword.Game.Movement;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Столкновения корней бойцов, точка 4в: <c>ResolveRoots</c> SWORD 2eab13b целиком — побитово против копии SWORD на
/// случайных толпах; в мире — порядок мест и вид толчков <see cref="RootContacts"/>; толпа из 32 врагов (замер
/// перекрытия для инварианта <c>movement.rootOverlap</c>); игрок упирается в толпу и расталкивает её; 0 байт на тик;
/// агентский вид.
/// </summary>
public sealed class RootCollisionTests(ITestOutputHelper output)
{
    // ── копия SWORD ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Gameplay/Ragdoll/DuelArena.cs SWORD 2eab13b, ResolveRoots — дословно (бойцы и арена — минимальные заглушки; колонны
    // и радиус арены — ArenaLayout SWORD2, сверенный с SWORD в ArenaTerrainTests). Добавлены только счётчики теста.

    private sealed class SwordFighter
    {
        public Vector3 Root, RootVel;
        public bool Dead { get; init; }   // смерти до этапа 6 нет
    }

    private sealed class SwordArena(SwordFighter[] fighters)
    {
        private readonly SwordFighter[] _fighters = fighters;
        private int ActiveCount => _fighters.Length;
        public int Pairs, Pillars, Edges, Coincident;   // счётчики теста

        public void ResolveRoots()
        {
            const float r = 0.35f;
            for (int iter = 0; iter < 2; iter++)
            {
                for (int i = 0; i < ActiveCount; i++)
                {
                    var a = _fighters[i];
                    if (a.Dead) continue;
                    for (int j = i + 1; j < ActiveCount; j++)
                    {
                        var b = _fighters[j];
                        if (b.Dead) continue;
                        var d = a.Root - b.Root;
                        float l = d.Length();
                        if (l < 1e-4f) Coincident++;   // счётчик теста
                        if (l >= 2 * r || l < 1e-4f) continue;
                        Pairs++;   // счётчик теста
                        var n = d / l;
                        var push = n * ((2 * r - l) * 0.5f);
                        a.Root += push;
                        b.Root -= push;
                        float va = Vector3.Dot(a.RootVel, n), vb = Vector3.Dot(b.RootVel, n);
                        if (va < 0) a.RootVel -= n * va;
                        if (vb > 0) b.RootVel -= n * vb;
                    }
                    foreach (var pl in ArenaLayout.Pillars)
                    {
                        var d = new Vector3(a.Root.X - pl.X, 0, a.Root.Z - pl.Y);
                        float l = d.Length(), rr = pl.Z + r;
                        if (l >= rr || l < 1e-4f) continue;
                        Pillars++;   // счётчик теста
                        var n = d / l;
                        a.Root = new Vector3(pl.X, 0, pl.Y) + n * rr;
                        float v = Vector3.Dot(a.RootVel, n);
                        if (v < 0) a.RootVel -= n * v;
                    }
                    float lim = ArenaLayout.ArenaRadius - 0.6f, dist = a.Root.Length();
                    if (dist > lim)
                    {
                        Edges++;   // счётчик теста
                        var n = a.Root / dist;
                        a.Root = n * lim;
                        float v = Vector3.Dot(a.RootVel, n);
                        if (v > 0) a.RootVel -= n * v;
                    }
                }
            }
        }
    }

    private static void AssertBits(Vector3 expected, Vector3 actual, string what)
    {
        static int B(float f) => BitConverter.SingleToInt32Bits(f);
        Assert.True(B(expected.X) == B(actual.X) && B(expected.Y) == B(actual.Y) && B(expected.Z) == B(actual.Z),
            $"{what}: SWORD {expected:R}, SWORD2 {actual:R}");
    }

    /// <summary>
    /// Случайная толпа на местах 0…32: кучки у случайной точки арены, у колонны, у края; совпадающие корни и корни ближе
    /// 1e-4 м; часть мест пуста (дыры — в SWORD их нет, там список сжат); скорости любые.
    /// </summary>
    private static (Vector3[] Roots, Vector3[] Velocities, bool[] Present) Crowd(Random rng, int count)
    {
        var roots = new Vector3[ArenaRules.MaxFighters];
        var velocities = new Vector3[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        float R() => (float)(rng.NextDouble() * 2 - 1);
        Vector3 Anchor()
        {
            switch (rng.Next(3))
            {
                case 0:
                    var pillar = ArenaLayout.Pillars[rng.Next(ArenaLayout.Pillars.Length)];
                    float a = R() * MathF.PI, rad = pillar.Z + 0.35f + R() * 1.2f;
                    return new Vector3(pillar.X + MathF.Cos(a) * rad, 0, pillar.Y + MathF.Sin(a) * rad);
                case 1:
                    float e = R() * MathF.PI, er = ArenaLayout.ArenaRadius - 0.6f + R() * 1.5f;
                    return new Vector3(MathF.Cos(e) * er, 0, MathF.Sin(e) * er);
                default:
                    return new Vector3(R() * 40, 0, R() * 40);
            }
        }
        var anchor = Anchor();
        int slot = 0;
        for (int k = 0; k < count; k++)
        {
            if (count < ArenaRules.MaxFighters && rng.Next(6) == 0) slot++;   // дыра
            if (slot >= ArenaRules.MaxFighters) break;
            if (rng.Next(8) == 0) anchor = Anchor();
            Vector3 root = rng.Next(12) switch
            {
                0 when k > 0 => roots[slot - 1],                                               // совпадает с соседом
                1 when k > 0 => roots[slot - 1] + new Vector3(R() * 5e-5f, 0, R() * 5e-5f),    // ближе 1e-4 м
                2 => anchor + new Vector3(R(), R() * 0.2f, R()),                               // y ≠ 0 (d.Y в SWORD не обнуляется)
                _ => anchor + new Vector3(R() * 1.5f, 0, R() * 1.5f),                          // кучка: пары ближе 0,7 м
            };
            (roots[slot], velocities[slot], present[slot]) = (root, new Vector3(R() * 5, 0, R() * 5), true);
            slot++;
        }
        return (roots, velocities, present);
    }

    [Fact]
    public void Resolve_MatchesSwordResolveRoots_Bitwise()
    {
        var rng = new Random(4343);
        var contacts = new RootContacts[ArenaRules.MaxFighters];
        int pairs = 0, pillars = 0, edges = 0, coincident = 0, holes = 0, crowds = 0;
        for (int run = 0; run < 3000; run++)
        {
            int count = 1 + run % ArenaRules.MaxFighters;
            var (roots, velocities, present) = Crowd(rng, count);
            var sword = new List<(int Slot, SwordFighter F)>();
            for (int s = 0; s < ArenaRules.MaxFighters; s++)
                if (present[s]) sword.Add((s, new SwordFighter { Root = roots[s], RootVel = velocities[s] }));
            holes += Enumerable.Range(0, Array.LastIndexOf(present, true)).Count(s => !present[s]);
            var arena = new SwordArena([.. sword.Select(x => x.F)]);
            arena.ResolveRoots();
            RootCollision.Resolve(roots, velocities, present, contacts);
            foreach (var (s, f) in sword)
            {
                AssertBits(f.Root, roots[s], $"прогон {run}, место {s}: Root");
                AssertBits(f.RootVel, velocities[s], $"прогон {run}, место {s}: RootVel");
            }
            (pairs, pillars, edges, coincident) = (pairs + arena.Pairs, pillars + arena.Pillars, edges + arena.Edges, coincident + arena.Coincident);
            if (sword.Count > 1) crowds++;
        }
        output.WriteLine($"толп {crowds}: пар {pairs}, колонн {pillars}, края {edges}, совпавших корней {coincident}, дыр {holes}");
        Assert.True(pairs > 20_000 && pillars > 1_000 && edges > 1_000 && coincident > 200 && holes > 500,
            $"пар {pairs}, колонн {pillars}, края {edges}, совпавших {coincident}, дыр {holes}");
    }

    [Fact]
    public void SingleFighter_IsPillarsThenEdge_AsBefore4v()
    {
        // До 4в RootCollisionSystem разрешал бойца в одиночку: два прохода {колонны, край}. Один боец — те же биты
        // (эталоны прототипа и анимации — один игрок).
        var rng = new Random(11);
        var contacts = new RootContacts[ArenaRules.MaxFighters];
        var roots = new Vector3[ArenaRules.MaxFighters];
        var velocities = new Vector3[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        for (int run = 0; run < 2000; run++)
        {
            var crowd = Crowd(rng, 1);
            int slot = Array.IndexOf(crowd.Present, true);
            var (root, velocity) = (crowd.Roots[slot], crowd.Velocities[slot]);
            (roots[0], velocities[0], present[0]) = (root, velocity, true);
            RootCollision.Resolve(roots, velocities, present, contacts);
            for (int iter = 0; iter < 2; iter++)
            {
                foreach (var pillar in ArenaLayout.Pillars) RootCollisionSystem.PushOutOfPillar(ref root, ref velocity, pillar);
                RootCollisionSystem.KeepInsideArena(ref root, ref velocity);
            }
            AssertBits(root, roots[0], $"прогон {run}: корень");
            AssertBits(velocity, velocities[0], $"прогон {run}: скорость");
            Assert.Equal(RootContacts.None, contacts[0]);
        }
    }

    [Fact]
    public void Contacts_ExplainPushes()
    {
        var roots = new Vector3[ArenaRules.MaxFighters];
        var velocities = new Vector3[ArenaRules.MaxFighters];
        var present = new bool[ArenaRules.MaxFighters];
        var contacts = new RootContacts[ArenaRules.MaxFighters];
        // Игрок в центре, враг 2 в 0,5 м справа (+X) и бежит на игрока, враг 5 в 0,6 м от врага 2 дальше по +X.
        roots[0] = Vector3.Zero;
        roots[2] = new Vector3(0.5f, 0, 0);
        roots[5] = new Vector3(1.1f, 0, 0);
        velocities[2] = new Vector3(-3, 0, 1);
        present[0] = present[2] = present[5] = true;
        var before = (Vector3[])roots.Clone();
        RootCollision.Resolve(roots, velocities, present, contacts);

        Assert.Equal(-1, contacts[1].DeepestWith);
        Assert.Equal(RootContacts.None, contacts[1]);
        // Пара 0–2 (перекрытие 0,2 м) — первым, враг 2 уходит к +X и заходит во врага 5, того тоже толкает.
        Assert.Equal(0, contacts[2].DeepestWith);
        Assert.Equal(0.2f, contacts[2].Deepest, 1e-6f);
        Assert.True(contacts[2].FromPlayer.X > 0.099f, $"игрок толкнул врага 2 на {contacts[2].FromPlayer}");
        Assert.Equal(2, contacts[5].DeepestWith);
        Assert.Equal(Vector3.Zero, contacts[5].FromPlayer);
        Assert.Equal(Vector3.Zero, contacts[0].FromPlayer);   // у игрока вида нет — только у врагов
        for (int s = 0; s < ArenaRules.MaxFighters; s++)
        {
            // Колонн и края рядом нет — весь сдвиг корня от пар.
            Assert.True(Vector3.Distance(roots[s] - before[s], contacts[s].Push) < 1e-6f, $"место {s}: сдвиг {roots[s] - before[s]}, Push {contacts[s].Push}");
        }
        // Толчок игрока — минус сумма FromPlayer врагов.
        Assert.True(Vector3.Distance(contacts[0].Push, -contacts[2].FromPlayer) < 1e-6f);
        Assert.True(contacts[0].Contacts >= 1 && contacts[2].Contacts >= 2 && contacts[5].Contacts >= 1);
        Assert.True(velocities[2].X >= 0, $"скорость врага 2 внутрь игрока не погашена: {velocities[2]}");
        Assert.Equal(1f, velocities[2].Z);   // вдоль — остаётся
    }

    // ── в мире ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Каждый тик: корни и скорости по местам после Animation → перенос (<see cref="RootControlSystem.Move"/>) и
    /// <see cref="RootCollision.Resolve"/> обязаны дать ровно корни, скорости и виды толчков мира после Movement.
    /// Заодно — замер перекрытия корней после Movement: худшее за прогон и сколько тиков подряд пара перекрыта больше
    /// допуска инварианта.
    /// </summary>
    internal sealed class MovementProbe : ISimulationObserver
    {
        public int Checked, Mismatches, LongestRun, LongestHardRun;
        public string? First;
        public float Worst, WorstSteady;
        private readonly Vector3[] _roots = new Vector3[ArenaRules.MaxFighters], _velocities = new Vector3[ArenaRules.MaxFighters];
        private readonly bool[] _present = new bool[ArenaRules.MaxFighters];
        private readonly RootContacts[] _contacts = new RootContacts[ArenaRules.MaxFighters];
        private readonly int[] _runs = new int[ArenaRules.MaxFighters * ArenaRules.MaxFighters];
        private readonly int[] _hardRuns = new int[ArenaRules.MaxFighters * ArenaRules.MaxFighters];
        private readonly Entity[] _owner = new Entity[ArenaRules.MaxFighters];
        private readonly Entity[] _last = new Entity[ArenaRules.MaxFighters];
        private readonly Vector3[] _lastRoot = new Vector3[ArenaRules.MaxFighters];
        private readonly ulong[] _built = new ulong[ArenaRules.MaxFighters];

        public void AfterStage(Simulation sim, int stage)
        {
            var w = sim.World;
            string name = w.Stages[stage];
            if (name == GameStages.Animation)
            {
                Array.Clear(_present);
                foreach (var chunk in w.Iterate(w.Query().With<Fighter>()))
                    foreach (var e in chunk.Entities)
                    {
                        int s = w.Get<FighterSlot>(e).Index;
                        var t = w.Get<Transform>(e);
                        var v = w.Get<RootVelocity>(e).Value;
                        if (w.TryGet<AnimatorAction>(e, out var action)) RootControlSystem.Move(ref t, v, action, sim.Dt);   // враг: поворот и сдвиг клипом (4г)
                        else RootControlSystem.Move(ref t, v, sim.Dt);
                        (_roots[s], _velocities[s], _present[s], _owner[s]) = (t.Position, v, true, e);
                        // Постройка — новая сущность места или прыжок корня (сброс строит на той же сущности).
                        if (_last[s] != e || Vector3.Distance(_lastRoot[s], t.Position) > 0.5f) _built[s] = sim.Tick;
                        _last[s] = e;
                    }
                RootCollision.Resolve(_roots, _velocities, _present, _contacts);
                return;
            }
            if (name != GameStages.Movement) return;
            for (int s = 0; s < ArenaRules.MaxFighters; s++)
            {
                if (!_present[s])
                {
                    Array.Clear(_runs, s * ArenaRules.MaxFighters, ArenaRules.MaxFighters);
                    Array.Clear(_hardRuns, s * ArenaRules.MaxFighters, ArenaRules.MaxFighters);
                    continue;
                }
                var e = _owner[s];
                bool same = w.Get<Transform>(e).Position == _roots[s] && w.Get<RootVelocity>(e).Value == _velocities[s]
                    && (!w.Has<EnemyTag>(e) || w.Get<RootContacts>(e).Equals(_contacts[s]));
                if (!same)
                {
                    Mismatches++;
                    First ??= $"тик {sim.Tick}, место {s}: {w.Get<Transform>(e).Position} ≠ {_roots[s]}";
                }
                Checked++;
                _lastRoot[s] = _roots[s];
                for (int o = s + 1; o < ArenaRules.MaxFighters; o++)
                {
                    int pair = s * ArenaRules.MaxFighters + o;
                    if (!_present[o])
                    {
                        _runs[pair] = _hardRuns[pair] = 0;
                        continue;
                    }
                    float overlap = 2 * MovementRules.RootRadius - (_roots[s] - _roots[o]).Length();
                    Worst = MathF.Max(Worst, overlap);
                    // «Установившееся» — оба бойца построены не меньше трёх тиков назад.
                    if (sim.Tick >= _built[s] + 3 && sim.Tick >= _built[o] + 3) WorstSteady = MathF.Max(WorstSteady, overlap);
                    _runs[pair] = overlap > RootOverlapInvariant.Tolerance ? _runs[pair] + 1 : 0;
                    _hardRuns[pair] = overlap > 2 * MovementRules.RootRadius - RootOverlapInvariant.HardLimit ? _hardRuns[pair] + 1 : 0;
                    LongestRun = Math.Max(LongestRun, _runs[pair]);
                    LongestHardRun = Math.Max(LongestHardRun, _hardRuns[pair]);
                }
            }
        }

        public void AfterTick(Simulation simulation)
        {
        }
    }

    /// <summary>
    /// Толпа из 32 врагов: игрок стоит 20 с (толпа обступает), 5 с бежит сквозь неё, стоит, 10 с бегает кругами, затем
    /// бегает туда-сюда, а враги каждую секунду прибывают и убывают по 3 (PageUp/PageDown — спаун в бегущую толпу) и раз в
    /// 7 с — сброс арены (R: все враги заново).
    /// </summary>
    private static void CrowdStep(Simulation sim, int t)
    {
        Vector2? move = t switch
        {
            < 2400 => null,
            < 3000 => new Vector2(1, 0),
            < 3600 => null,
            < 4800 => new Vector2(MathF.Cos(t / 120f), MathF.Sin(t / 120f)),
            _ => new Vector2(t / 240 % 2 * 2 - 1, 0.3f),
        };
        if (move is { } m) sim.Enqueue(new PlayerCommand { Move = m });
        if (t >= 4800 && t % 120 == 0) sim.Enqueue(new ArenaCommand { Action = ArenaAction.ChangeEnemies, Enemies = t / 120 % 2 == 0 ? -3 : 3 });
        if (t >= 4800 && t % 840 == 0) sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        sim.Export.BeginFrame();
        sim.Step();
    }

    public const int CrowdTicks = 7200;

    /// <summary>
    /// У края арены: 32 врага, игрок 20 с бежит от центра курсом, зависящим от зерна, и упирается в край (61,4 м); толпа
    /// прижата к нему, а кольцо спауна 5–9 м вокруг него прижимается к окружности <c>ArenaRadius − 3</c> — новые враги
    /// появляются прямо в толпе. С 20 с — команды арены по образцу <paramref name="pattern"/>: <c>A</c> — PageDown/PageUp
    /// раз в 0,5 с, <c>B</c> — ±1 раз в 0,1 с, <c>C</c> — ±4 каждые 6 тиков, <c>D1</c> — −32 и через 0,5 с +32, <c>D2</c> —
    /// −32 и в следующем тике +32 (раз в 0,5 с); <paramref name="press"/> — игрок продолжает давить в край, иначе стоит.
    /// </summary>
    internal static void EdgeStep(Simulation sim, int t, int seed, string pattern, bool press)
    {
        float a = seed * 0.9f;
        if (t < 2400 || press) sim.Enqueue(new PlayerCommand { Move = new Vector2(MathF.Cos(a), MathF.Sin(a)) });
        int k = t - 2400;
        if (k >= 0)
        {
            int? change = pattern switch
            {
                "A" when k % 60 == 0 => k / 60 % 2 == 0 ? -1 : 1,
                "B" when k % 12 == 0 => k / 12 % 2 == 0 ? -1 : 1,
                "C" when k % 6 == 0 => k / 6 % 2 == 0 ? -4 : 4,
                "D1" when k % 60 == 0 => k / 60 % 2 == 0 ? -32 : 32,
                "D2" when k % 60 == 0 => -32,
                "D2" when k % 60 == 1 => 32,
                _ => null,
            };
            if (change is { } c) sim.Enqueue(new ArenaCommand { Action = ArenaAction.ChangeEnemies, Enemies = c });
        }
        sim.Export.BeginFrame();
        sim.Step();
    }

    internal static Simulation EdgeSim(int seed, out MovementProbe probe)
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32", $"sim.seed={seed}"));
        probe = new MovementProbe();
        sim.Observe(probe);
        return sim;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Crowd32_EveryTickIsResolveRoots_OverlapWithinInvariant(int seed)
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32", $"sim.seed={seed}"));
        var probe = new MovementProbe();
        sim.Observe(probe);
        for (int t = 0; t < CrowdTicks; t++) CrowdStep(sim, t);
        output.WriteLine($"sim.seed={seed}: перекрытие худшее {probe.Worst:0.0000} м, установившееся {probe.WorstSteady:0.0000} м, " +
            $"больше {RootOverlapInvariant.Tolerance} м подряд — {probe.LongestRun} тиков");
        Assert.True(probe.Mismatches == 0, probe.First);
        Assert.True(probe.Checked > 150_000, $"проверено {probe.Checked}");
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent().Take(5)));
        Assert.Contains(sim.Invariants.List(), i => i.Id == "movement.rootOverlap" && i.Enabled && i.Evaluations == CrowdTicks);
        // Вдали от края: корень ни разу не в круге соседа, перекрытие больше допуска — не дольше 2 тиков; установившееся
        // перекрытие — вдвое меньше допуска.
        Assert.InRange(probe.Worst, 0, RootOverlapInvariant.HardLimit / 2);
        Assert.Equal(0, probe.LongestHardRun);
        Assert.InRange(probe.WorstSteady, 0, RootOverlapInvariant.Tolerance / 2);
        Assert.InRange(probe.LongestRun, 0, 2);
    }

    /// <summary>
    /// У края арены с частыми командами арены (<see cref="EdgeStep"/>) — худшие случаи замера: новые враги появляются прямо
    /// в толпе, прижатой к игроку. Зёрна и образцы — самые длинные перекрытия замера на 40 зёрнах (README фичи «Враги»):
    /// ближе радиуса корня — до 2 тиков подряд, больше 0,02 м — до 12. Нарушений инварианта нет, запас до порогов — вдвое.
    /// </summary>
    [Theory]
    [InlineData("A", 19, false)]
    [InlineData("B", 12, false)]
    [InlineData("C", 24, false)]
    [InlineData("D1", 1, true)]
    [InlineData("D2", 11, true)]
    [InlineData("D2", 4, false)]
    public void Edge_FrequentArenaCommands_NoViolations(string pattern, int seed, bool press)
    {
        var sim = EdgeSim(seed, out var probe);
        for (int t = 0; t < 2400 + 7200; t++) EdgeStep(sim, t, seed, pattern, press);
        var player = sim.World.Get<Transform>(GameCore.Player(sim.World)).Position;
        output.WriteLine($"{pattern}, sim.seed={seed}, давит в край {press}: игрок в {player.Length():0.00} м от центра; ближе " +
            $"{RootOverlapInvariant.HardLimit} м подряд — {probe.LongestHardRun} тиков, больше {RootOverlapInvariant.Tolerance} м " +
            $"подряд — {probe.LongestRun} тиков, худшее перекрытие {probe.Worst:0.0000} м");
        Assert.True(probe.Mismatches == 0, probe.First);
        Assert.True(player.Length() > 58, $"игрок не у края: {player}");
        Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent().Take(5)));
        Assert.InRange(probe.LongestHardRun, 0, RootOverlapInvariant.HardMaxTicks / 2);
        Assert.InRange(probe.LongestRun, 1, RootOverlapInvariant.MaxTicks / 2);
    }

    [Fact]
    public void SpawnOverlap_FadesWithinTwoTicks()
    {
        // Кольцо спауна 5–9 м под 32 врага: пары ближе 0,7 м — обычное дело, кучки из 3–4 врагов два прохода не
        // разводят. Замер для допуска инварианта: тик постройки — не глубже радиуса корня, дальше — рассасывается.
        var worst = new float[4];
        for (int seed = 1; seed <= 400; seed++)
        {
            var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32", $"sim.seed={seed}"));
            var probe = new MovementProbe();
            sim.Observe(probe);
            for (int t = 0; t < worst.Length; t++)
            {
                probe.Worst = 0;
                sim.Step();
                worst[t] = MathF.Max(worst[t], probe.Worst);
            }
            Assert.True(probe.Mismatches == 0, probe.First);
            Assert.True(sim.Invariants.Total == 0, string.Join("\n", sim.Invariants.Recent().Take(5)));
        }
        output.WriteLine("худшее перекрытие по тикам после спауна: " + string.Join(", ", worst.Select(w => w.ToString("0.0000"))));
        Assert.InRange(worst[0], 0.02f, RootOverlapInvariant.HardLimit / 2);   // спаун внахлёст бывает
        Assert.InRange(worst[2], 0, RootOverlapInvariant.Tolerance);
    }

    [Fact]
    public void PlayerRunsIntoCrowd_IsBlocked_PushesThrough_CrowdNeverInsidePlayer()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32"));
        var w = sim.World;
        var player = GameCore.Player(w);
        float slowest = float.MaxValue, nearest = float.MaxValue;
        int pushedTicks = 0, crowdContacts = 0;
        for (int t = 0; t < 1080; t++)
        {
            if (t >= 600) sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });
            sim.Export.BeginFrame();
            sim.Step();
            var p = w.Get<Transform>(player).Position;
            bool pushed = false;
            foreach (var chunk in w.Iterate(w.Query().With<EnemyTag>()))
                foreach (var e in chunk.Entities)
                {
                    var c = w.Get<RootContacts>(e);
                    pushed |= c.FromPlayer.X > 0;
                    if (t < 600) crowdContacts += c.Contacts;
                    nearest = MathF.Min(nearest, (w.Get<Transform>(e).Position - p).Length());
                }
            if (pushed) pushedTicks++;
            if (t >= 640 && t < 700) slowest = MathF.Min(slowest, w.Get<RootVelocity>(player).Value.X);
        }
        var end = w.Get<Transform>(player).Position;
        output.WriteLine($"толпа толкается {crowdContacts}; игрок толкнул врагов {pushedTicks} тиков; медленнее всего {slowest:0.00} м/с; ближайший враг {nearest:0.0000} м; игрок в {end}");
        Assert.True(crowdContacts > 100, "толпа не толкается");
        Assert.True(pushedTicks > 3, "игрок не толкнул ни одного врага");
        Assert.True(slowest < 1f, $"игрок не упёрся: {slowest} м/с");
        Assert.True(end.X > 5, $"игрок не прошёл сквозь толпу: {end}");
        Assert.True(nearest > 2 * MovementRules.RootRadius - RootOverlapInvariant.Tolerance, $"враг внутри игрока: {nearest} м");
        Assert.Equal(0, sim.Invariants.Total);
    }

    [Fact]
    public void Crowd32_RunningIntoIt_DoesNotAllocate()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32"));
        for (int t = 0; t < 600; t++) CrowdStep(sim, t);
        for (int t = 2400; t < 2410; t++) CrowdStep(sim, t);   // первая команда игрока заводит журнал команд (не 4в)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 2410; t < 3000; t++) CrowdStep(sim, t);   // бег сквозь толпу
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"590 тиков выделили {allocated} Б");
    }

    [Fact]
    public async Task Agent_SeesWhoPushedWhom_ContactsInEntityGetConditionsAggregates()
    {
        var sim = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=32"));
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

            var invariants = (await Call("invariant.list")).ToJsonString();
            Assert.Contains("movement.rootOverlap", invariants);
            await Call("time.runUntil", new JsonObject { ["tick"] = 600 });
            await Call("command.send", new JsonObject
            {
                ["command"] = "PlayerCommand", ["fields"] = JsonNode.Parse("""{"Move":{"X":1,"Y":0}}"""), ["repeat"] = 480,
            });
            // «Когда игрок толкнул врага»: условие по виду толчков.
            var met = await Call("time.runUntil", new JsonObject
            {
                ["condition"] = JsonNode.Parse("""{"exists":{"all":[{"component":"EnemyTag"},{"field":"RootContacts.DeepestWith","equals":0}]}}"""),
                ["maxTicks"] = 480,
            });
            Assert.True(met["met"]!.GetValue<bool>(), met.ToJsonString());
            var pushed = await Call("world.query", new JsonObject
            {
                ["with"] = new JsonArray("EnemyTag"), ["where"] = JsonNode.Parse("""{"field":"RootContacts.DeepestWith","equals":0}"""),
                ["fields"] = new JsonArray("RootContacts", "FighterSlot"),
            });
            var text = pushed.ToJsonString();
            foreach (var field in new[] { "Contacts", "Push", "FromPlayer", "Deepest", "DeepestWith" }) Assert.Contains(field, text);
            var sum = await Call("world.query", new JsonObject
            {
                ["with"] = new JsonArray("EnemyTag"), ["aggregate"] = new JsonArray("RootContacts.FromPlayer.X", "RootContacts.Deepest"), ["take"] = 0,
            });
            Assert.Contains("FromPlayer", sum.ToJsonString());
            var describe = await Call("state.describe", new JsonObject { ["part"] = "ecs", ["filter"] = "RootContacts" });
            Assert.Contains("DeepestWith", describe.ToJsonString());
        }
        finally
        {
            stop.Cancel();
            host.Service.Wake();
            loop.Join(TimeSpan.FromSeconds(5));
        }
    }
}
