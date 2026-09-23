using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Sword.Engine.Content;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Locomotion;

namespace Sword.Game.Tests;

/// <summary>
/// Сверка с SWORD на эталонном паке (этап 2): трасса <c>tests/reference/anim-locomotion.trace.json</c> снята настоящим
/// кодом SWORD 2eab13b (<c>tests/reference/sword-trace</c>). Тот же сценарий команд игрока в симуляции SWORD2 обязан дать
/// тик за тиком тот же корень, курс, клип локомоции и побитово ту же позу — перенос логики без изменений.
///
/// Эталонный пак вне git: без него тест падает с подсказкой; категория Content — <c>verify.ps1 -SkipReference</c> её не
/// запускает. Перед сверкой SHA-256 пака сверяется с <c>tests/reference/reference.json</c>.
/// </summary>
[Trait("Category", "Content")]
public sealed class ReferenceTraceTests
{
    private sealed record Segment(int End, float MoveX, float MoveZ, bool Walk, Vector2? Gaze);

    /// <summary>Сценарий — тот же, что <c>Scenario.Segments</c> программы трассы.</summary>
    private static readonly Segment[] Segments =
    [
        new(60, 0, 0, false, null),
        new(300, 0, 1, false, new Vector2(0, 30)),
        new(420, 1, 0, true, null),
        new(600, -1, -1, false, new Vector2(30, 0)),
        new(720, 0, 0, false, null),
        new(900, 0, -1, false, new Vector2(0, -30)),
    ];

    /// <summary>Хеш позы — как в программе трассы: FNV-1a по битам всех 16 чисел каждой матрицы.</summary>
    public static ulong PoseHash(ReadOnlySpan<Matrix4x4> model)
    {
        ulong h = 14695981039346656037;
        void Add(float x) => h = (h ^ (uint)BitConverter.SingleToInt32Bits(x)) * 1099511628211;
        foreach (var m in model)
        {
            Add(m.M11); Add(m.M12); Add(m.M13); Add(m.M14);
            Add(m.M21); Add(m.M22); Add(m.M23); Add(m.M24);
            Add(m.M31); Add(m.M32); Add(m.M33); Add(m.M34);
            Add(m.M41); Add(m.M42); Add(m.M43); Add(m.M44);
        }
        return h;
    }

    /// <summary>Эталонный пак после проверки SHA-256.</summary>
    public static string ReferencePak()
    {
        string root = RepoRoot();
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests", "reference", "reference.json")));
        var content = reference.RootElement.GetProperty("content");
        string pak = Path.Combine(root, content.GetProperty("file").GetString()!);
        Assert.True(File.Exists(pak), $"нет эталонного пака {pak} — закрепите его (tools/scripts/reference.ps1 -Pin) или " +
            "запустите проверку без сверки с SWORD (verify.ps1 -SkipReference: категория Content не запускается)");
        string expected = content.GetProperty("sha256").GetString()!;
        using (var s = File.OpenRead(pak))
        {
            string actual = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            Assert.True(actual == expected, $"эталонный пак изменён: SHA-256 {actual}, закреплён {expected}");
        }
        return pak;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("не найден корень репозитория (SWORD.slnx)");
    }

    [Fact]
    public void Locomotion_MatchesSwordTickByTick()
    {
        CharacterAssets assets;
        using (var pak = PakSource.Open(ReferencePak())) assets = CharacterAssets.Load(pak);

        using var trace = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "reference", "anim-locomotion.trace.json")));
        var ticks = trace.RootElement.GetProperty("ticks");
        Assert.Equal(Segments[^1].End, ticks.GetArrayLength());
        Assert.Equal(trace.RootElement.GetProperty("clips").GetInt32(), assets.Clips.ClipCount);
        Assert.Equal(trace.RootElement.GetProperty("joints").GetInt32(), assets.Clips.JointCount);

        var sim = SwordSimulation.Create(ConfigSet.Empty(), assets);
        var player = GameCore.Player(sim.World);
        var poses = sim.World.View<IAnimationView>();
        foreach (var expected in ticks.EnumerateArray())
        {
            int tick = expected.GetProperty("tick").GetInt32();
            var seg = Segments.First(s => tick - 1 < s.End);
            sim.Enqueue(new PlayerCommand
            {
                Move = new Vector2(seg.MoveX, seg.MoveZ), Walk = seg.Walk,
                HasGaze = seg.Gaze is not null, Gaze = seg.Gaze is { } g ? new Vector3(g.X, 0, g.Y) : default,
            });
            sim.Step();

            var t = sim.World.Get<Transform>(player);
            var loco = sim.World.Get<LegacyLocomotionState>(player);
            string clip = assets.Clips.Clips[loco.Clip].Name;
            var root = expected.GetProperty("root");
            string where = $"тик {tick}: SWORD {expected.GetProperty("clip").GetString()} ({root[0].GetSingle()}, {root[1].GetSingle()}) " +
                $"курс {expected.GetProperty("facing").GetSingle()}; SWORD2 {clip} ({t.Position.X}, {t.Position.Z}) курс {t.Facing}";
            Assert.True(expected.GetProperty("clip").GetString() == clip, "клип: " + where);
            Assert.True(root[0].GetSingle() == t.Position.X && root[1].GetSingle() == t.Position.Z, "корень: " + where);
            Assert.True(expected.GetProperty("facing").GetSingle() == t.Facing, "курс: " + where);
            string hash = PoseHash(poses.Model(sim.World.Get<PoseRef>(player))).ToString("X16");
            Assert.True(expected.GetProperty("poseHash").GetString() == hash, "поза: " + where);
        }
    }

    /// <summary>
    /// Точка 4г: трасса <c>tests/reference/anim-turn.trace.json</c> — боец не-<c>Agile</c> SWORD (настоящий
    /// <c>FighterAnimator</c>: слой действий, поворот на месте клипом), намерение — сценарий трассы. Здесь тот же сценарий —
    /// враг места 1 (<c>arena.enemies = 1</c>), его намерение каждый тик — команда врагу (<see cref="EnemyCommand"/>) вместо
    /// ИИ; игрок стоит в центре и до врага не достаёт. Тик за тиком побитово: корень, курс, клип локомоции, действие и его
    /// клип, время, вес, растяжение yaw, поворот курса и сдвиг корня клипом, поза.
    /// </summary>
    [Fact]
    public void EnemyTurn_MatchesSwordTickByTick()
    {
        CharacterAssets assets;
        using (var pak = PakSource.Open(ReferencePak())) assets = CharacterAssets.Load(pak);
        using var trace = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "reference", "anim-turn.trace.json")));
        var doc = trace.RootElement;
        Assert.Equal(doc.GetProperty("clips").GetInt32(), assets.Clips.ClipCount);
        var segments = TurnSegments(doc);

        var sim = SwordSimulation.Create(ConfigSet.Empty("arena.enemies=1"), assets);
        var enemy = Enemy(sim);
        // Начало трассы — расстановка SWORD2 (счётчиковый ГСЧ, sim.seed 1): другая — снять трассу заново (README трассы).
        var start = doc.GetProperty("start");
        var t0 = sim.World.Get<Transform>(enemy);
        Assert.True(start.GetProperty("root")[0].GetSingle() == t0.Position.X && start.GetProperty("root")[1].GetSingle() == t0.Position.Z
            && start.GetProperty("facing").GetSingle() == t0.Facing,
            $"враг 1 построен в ({t0.Position.X:R}, {t0.Position.Z:R}) курсом {t0.Facing:R}, а трасса начинается с {start} — снять трассу заново");

        var poses = sim.World.View<IAnimationView>();
        var ticks = doc.GetProperty("ticks");
        Assert.Equal(segments[^1].End, ticks.GetArrayLength());
        int compared = 0;
        foreach (var expected in ticks.EnumerateArray())
        {
            int tick = expected.GetProperty("tick").GetInt32();
            var seg = segments.First(s => tick - 1 < s.End);
            sim.Enqueue(new EnemyCommand { Slot = 1, Velocity = seg.Velocity, FacingGoal = seg.FacingGoal });
            sim.Step();

            var t = sim.World.Get<Transform>(enemy);
            var a = sim.World.Get<AnimatorAction>(enemy);
            string clip = assets.Clips.Clips[sim.World.Get<LegacyLocomotionState>(enemy).Clip].Name;
            string actionClip = a.Kind == ActionKind.None ? "" : assets.Clips.Clips[a.Clip].Name;
            var root = expected.GetProperty("root");
            string where = $"тик {tick}: SWORD {expected.GetProperty("action").GetString()} {expected.GetProperty("actionClip").GetString()} " +
                $"({root[0].GetSingle()}, {root[1].GetSingle()}) курс {expected.GetProperty("facing").GetSingle()}; SWORD2 {a.Kind} {actionClip} " +
                $"({t.Position.X}, {t.Position.Z}) курс {t.Facing}";
            Assert.True(expected.GetProperty("clip").GetString() == clip, "клип локомоции: " + where);
            Assert.True(expected.GetProperty("action").GetString() == a.Kind.ToString(), "действие: " + where);
            Assert.True(expected.GetProperty("actionClip").GetString() == actionClip, "клип действия: " + where);
            if (a.Kind != ActionKind.None)
            {
                Assert.True(expected.GetProperty("actionTime").GetSingle() == a.Time, $"время действия {a.Time}: " + where);
                Assert.True(expected.GetProperty("turnScale").GetSingle() == a.TurnScale, $"растяжение {a.TurnScale}: " + where);
            }
            Assert.True(expected.GetProperty("actionWeight").GetSingle() == a.Weight, $"вес {a.Weight}: " + where);
            Assert.True(expected.GetProperty("yawDelta").GetSingle() == a.YawDelta, $"поворот клипом {a.YawDelta}: " + where);
            var rootDelta = expected.GetProperty("rootDelta");
            Assert.True(rootDelta[0].GetSingle() == a.RootDelta.X && rootDelta[1].GetSingle() == a.RootDelta.Z, $"сдвиг клипом {a.RootDelta}: " + where);
            Assert.True(root[0].GetSingle() == t.Position.X && root[1].GetSingle() == t.Position.Z, "корень: " + where);
            Assert.True(expected.GetProperty("facing").GetSingle() == t.Facing, "курс: " + where);
            string hash = PoseHash(poses.Model(sim.World.Get<PoseRef>(enemy))).ToString("X16");
            Assert.True(expected.GetProperty("poseHash").GetString() == hash, "поза: " + where);
            compared++;
        }
        Assert.Equal(0, sim.Invariants.Total);
        // Сценарий трассы покрывает поворот: клипом во все стороны, стоя 2,5 рад/с, курс ведёт клип.
        var summary = doc.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("turnClips").GetArrayLength());
        Assert.True(summary.GetProperty("idleTurnTicks").GetInt32() > 0 && summary.GetProperty("clipDrivenTicks").GetInt32() > 0);
        Assert.Equal(ticks.GetArrayLength(), compared);
    }

    // ── Толпа (точка 4д) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Сегмент команд игрока толпы: до тика (индекс цикла, как <see cref="Segments"/>), куда идти, шагом, точка взгляда.</summary>
    public sealed record CrowdSegment(int End, float MoveX, float MoveZ, bool Walk, Vector2? Gaze);

    /// <summary>Команда арены толпы: перед шагом <paramref name="At"/> (действует в тике <c>At + 1</c>; в манифесте — <c>from</c>).</summary>
    public sealed record CrowdArena(int At, ArenaAction Action, int Enemies);

    /// <summary>Расстановка бойца: в каком тике построен (0 — сборка мира), место, корень (x, z), курс, зерно.</summary>
    public sealed record CrowdSpawn(int Tick, int Slot, float X, float Z, float Facing, float Seed);

    /// <summary>Врагов при сборке мира и зерно ГСЧ SWORD2 — расстановка толпы трассы.</summary>
    public const int CrowdEnemies = 12;
    public const ulong CrowdSeed = 1;

    /// <summary>Колонна 0 SWORD (<c>ArenaLayout.Pillars[0]</c>: cos 0,39 · 30, sin 0,39 · 30 м) — к ней игрок уводит толпу.</summary>
    private static readonly Vector2 CrowdPillar = new(27.747272f, 11.405652f);

    /// <summary>
    /// Сценарий трассы толпы — тот же, что пишет <c>crowd.input.json</c> для программы трассы (и сценарий <c>sword-crowd</c>
    /// манифеста <c>enemies</c>): игрок стоит — враги сходятся и окружают; разворачивается взглядом; пробегает сквозь кольцо;
    /// бегает кругами (8 сторон по 45 тиков); уходит шагом; стоит; после сброса стоит, пока враги сходятся к центру; бежит к
    /// колонне 0 (упирается в неё, толпа догоняет и прижимается к колонне); стоит у колонны.
    /// </summary>
    public static readonly CrowdSegment[] CrowdSegments =
    [
        new(480, 0, 0, false, null),
        new(600, 0, 0, false, new Vector2(10, 0)),
        new(780, 1, 0, false, new Vector2(30, 0)),
        new(825, 0, 1, false, null),
        new(870, 1, 1, false, null),
        new(915, 1, 0, false, null),
        new(960, 1, -1, false, null),
        new(1005, 0, -1, false, null),
        new(1050, -1, -1, false, null),
        new(1095, -1, 0, false, null),
        new(1140, -1, 1, false, null),
        new(1320, -1, 0, true, new Vector2(-30, 0)),
        new(1400, 1, 1, false, new Vector2(0, 300)),
        new(1470, 0.454f, 0.891f, false, null),
        new(1560, 0, 0, false, null),
        new(2420, 26.635f, 9.223f, false, CrowdPillar),   // из точки, где игрок стоит на тике 1560, — на центр колонны
        new(3000, 0, 0, false, null),
    ];

    /// <summary>
    /// Команды арены по ходу (<c>DuelArena.SetEnemyCount</c>/<c>Reset</c> SWORD): PageUp ×2 и 16; PageDown ×3 и PageUp ×3 — те
    /// же места строятся заново (аниматор места в SWORD остаётся прежним и сбрасывается <c>Reset</c>); R — игрок в центре,
    /// все заново; у колонны — 32 врага.
    /// </summary>
    public static readonly CrowdArena[] CrowdArenaCommands =
    [
        new(480, ArenaAction.ChangeEnemies, 1),
        new(540, ArenaAction.ChangeEnemies, 1),
        new(600, ArenaAction.SetEnemies, 16),
        new(1180, ArenaAction.ChangeEnemies, -1),
        new(1190, ArenaAction.ChangeEnemies, -1),
        new(1200, ArenaAction.ChangeEnemies, -1),
        new(1240, ArenaAction.ChangeEnemies, 1),
        new(1250, ArenaAction.ChangeEnemies, 1),
        new(1260, ArenaAction.ChangeEnemies, 1),
        new(1400, ArenaAction.Reset, 0),
        new(2700, ArenaAction.SetEnemies, 32),
    ];

    /// <summary>Боец толпы после тика в SWORD2 — то же, что пишет трасса.</summary>
    public readonly record struct CrowdFighter(float X, float Z, float Facing, string Clip, string Action, string ActionClip, string PoseHash);

    /// <summary>Прогон сценария толпы в SWORD2: расстановка (постройки по тикам) и бойцы по местам после каждого тика.</summary>
    public static (List<CrowdSpawn> Spawns, List<CrowdFighter[]> Ticks, int Violations) RunCrowd(CharacterAssets assets)
    {
        var sim = SwordSimulation.Create(ConfigSet.Empty($"arena.enemies={CrowdEnemies}", $"sim.seed={CrowdSeed}"), assets);
        var w = sim.World;
        var poses = w.View<IAnimationView>();
        var spawns = new List<CrowdSpawn>();
        var ticks = new List<CrowdFighter[]>();
        var last = new (Engine.Ecs.Entity Entity, float Seed)[33];
        var bySlot = new Engine.Ecs.Entity[33];

        int Gather()
        {
            int n = 0;
            Array.Fill(bySlot, Engine.Ecs.Entity.Null);
            foreach (var chunk in w.Iterate(w.Query().With<Fighter>()))
                foreach (var e in chunk.Entities)
                {
                    int s = w.Get<FighterSlot>(e).Index;
                    bySlot[s] = e;
                    n = Math.Max(n, s + 1);
                }
            return n;
        }

        // Постройка — новая сущность места или новое зерно (сброс строит на той же сущности): корень и курс постройки —
        // PrevTransform (его пишет постройка, а Begin — до неё), после сборки мира — Transform.
        void Built(int tick, int count)
        {
            for (int s = 0; s < count; s++)
            {
                var e = bySlot[s];
                float seed = w.Get<FighterSeed>(e).Value;
                if (last[s].Entity == e && last[s].Seed.Equals(seed)) continue;
                last[s] = (e, seed);
                var p = tick == 0 ? w.Get<Transform>(e) : new Transform { Position = w.Get<PrevTransform>(e).Position, Facing = w.Get<PrevTransform>(e).Facing };
                spawns.Add(new CrowdSpawn(tick, s, p.Position.X, p.Position.Z, p.Facing, seed));
            }
        }

        Built(0, Gather());
        int total = CrowdSegments[^1].End;
        for (int t = 0; t < total; t++)
        {
            var seg = CrowdSegments.First(s => t < s.End);
            sim.Enqueue(new PlayerCommand
            {
                Move = new Vector2(seg.MoveX, seg.MoveZ), Walk = seg.Walk,
                HasGaze = seg.Gaze is not null, Gaze = seg.Gaze is { } g ? new Vector3(g.X, 0, g.Y) : default,
            });
            foreach (var a in CrowdArenaCommands.Where(a => a.At == t)) sim.Enqueue(new ArenaCommand { Action = a.Action, Enemies = a.Enemies });
            sim.Step();

            int count = Gather();
            Built(t + 1, count);
            var row = new CrowdFighter[count];
            for (int s = 0; s < count; s++)
            {
                var e = bySlot[s];
                var tr = w.Get<Transform>(e);
                string clip = assets.Clips.Clips[w.Get<LegacyLocomotionState>(e).Clip].Name;
                var a = w.TryGet<AnimatorAction>(e, out var act) ? act : AnimatorAction.None;
                row[s] = new CrowdFighter(tr.Position.X, tr.Position.Z, tr.Facing, clip, a.Kind == ActionKind.None ? "" : a.Kind.ToString(),
                    a.Kind == ActionKind.None ? "" : assets.Clips.Clips[a.Clip].Name, PoseHash(poses.Model(w.Get<PoseRef>(e))).ToString("X16"));
            }
            ticks.Add(row);
        }
        return (spawns, ticks, (int)sim.Invariants.Total);
    }

    /// <summary>
    /// Вход программы трассы толпы (<c>tests/reference/sword-trace</c>, сценарий <c>crowd</c>): сценарий и расстановка SWORD2 —
    /// SWORD <c>Random(7)</c> не воспроизводится, поэтому трасса строит бойцов там же, где SWORD2.
    /// </summary>
    public static string CrowdInputJson(IEnumerable<CrowdSpawn> spawns) => JsonSerializer.Serialize(new
    {
        enemies = CrowdEnemies,
        seed = CrowdSeed,
        segments = CrowdSegments.Select(s => new { s.End, move = new[] { s.MoveX, s.MoveZ }, s.Walk, gaze = s.Gaze is { } g ? new[] { g.X, g.Y } : null }),
        arena = CrowdArenaCommands.Select(a => new { a.At, action = a.Action.ToString(), a.Enemies }),
        spawns = spawns.Select(s => new { s.Tick, s.Slot, root = new[] { s.X, s.Z }, s.Facing, s.Seed }),
    });

    /// <summary>
    /// Точка 4д: трасса <c>tests/reference/anim-crowd.trace.json</c> — толпа SWORD: игрок (<c>Agile</c>) и враги (не-<c>Agile</c>)
    /// с настоящим <c>FighterAnimator</c>, копии <c>EnemyAi.Think</c> (без удара) и <c>ResolveRoots</c>, порядок
    /// <c>DuelArena.Tick</c>, команды арены по ходу. Здесь тот же сценарий в SWORD2 — ИИ, столкновения и команды арены
    /// настоящие. Тик за тиком у каждого бойца побитово: корень, курс, клип локомоции, действие и его клип, поза. Прогон пишет
    /// вход программы трассы — <c>artifacts/reference/crowd.input.json</c> (как снять заново — README трассы).
    /// </summary>
    [Fact]
    public void Crowd_MatchesSwordTickByTick()
    {
        CharacterAssets assets;
        using (var pak = PakSource.Open(ReferencePak())) assets = CharacterAssets.Load(pak);
        var (spawns, run, violations) = RunCrowd(assets);
        string input = Path.Combine(RepoRoot(), "artifacts", "reference", "crowd.input.json");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(input, CrowdInputJson(spawns));
        Assert.Equal(0, violations);

        string path = Path.Combine(RepoRoot(), "tests", "reference", "anim-crowd.trace.json");
        Assert.True(File.Exists(path), $"нет трассы {path} — снять программой tests/reference/sword-trace по {input} (README трассы)");
        using var trace = JsonDocument.Parse(File.ReadAllText(path));
        var doc = trace.RootElement;
        Assert.Equal(doc.GetProperty("clips").GetInt32(), assets.Clips.ClipCount);
        // Сценарий и расстановка трассы — те же, что здесь: иначе снять трассу заново (README трассы).
        using (var expectedInput = JsonDocument.Parse(CrowdInputJson(spawns)))
            foreach (string key in new[] { "enemies", "seed", "segments", "arena", "spawns" })
                Assert.True(expectedInput.RootElement.GetProperty(key).GetRawText() == doc.GetProperty("input").GetProperty(key).GetRawText(),
                    $"в трассе другой «{key}», чем у SWORD2 сейчас (расстановка или сценарий изменились) — снять трассу заново по {input}");

        var clipNames = doc.GetProperty("clipNames").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var ticks = doc.GetProperty("ticks");
        Assert.Equal(run.Count, ticks.GetArrayLength());
        long compared = 0;
        int maxFighters = 0;
        foreach (var expected in ticks.EnumerateArray())
        {
            int tick = expected.GetProperty("tick").GetInt32();
            var row = run[tick - 1];
            var x = expected.GetProperty("x");
            Assert.True(x.GetArrayLength() == row.Length, $"тик {tick}: бойцов в SWORD {x.GetArrayLength()}, в SWORD2 {row.Length}");
            maxFighters = Math.Max(maxFighters, row.Length);
            for (int s = 0; s < row.Length; s++)
            {
                var f = row[s];
                float ex = x[s].GetSingle(), ez = expected.GetProperty("z")[s].GetSingle(), ef = expected.GetProperty("facing")[s].GetSingle();
                string clip = clipNames[expected.GetProperty("clip")[s].GetInt32()];
                string action = expected.GetProperty("action")[s].GetString()!;
                int actionClip = expected.GetProperty("actionClip")[s].GetInt32();
                string where = $"тик {tick}, место {s}: SWORD {clip} {action} ({ex}, {ez}) курс {ef}; SWORD2 {f.Clip} {f.Action} ({f.X}, {f.Z}) курс {f.Facing}";
                Assert.True(clip == f.Clip, "клип локомоции: " + where);
                Assert.True(action == f.Action, "действие: " + where);
                Assert.True((actionClip < 0 ? "" : clipNames[actionClip]) == f.ActionClip, "клип действия: " + where);
                Assert.True(ex == f.X && ez == f.Z, "корень: " + where);
                Assert.True(ef == f.Facing, "курс: " + where);
                Assert.True(expected.GetProperty("poseHash")[s].GetString() == f.PoseHash, "поза: " + where);
                compared++;
            }
        }
        // Сценарий покрывает то, ради чего снят: толпа до 32 врагов, пары корней, колонна, команды арены.
        var summary = doc.GetProperty("summary");
        Assert.Equal(33, maxFighters);
        Assert.True(summary.GetProperty("pairs").GetInt64() > 0 && summary.GetProperty("pillars").GetInt64() > 0);
        Assert.Equal(CrowdArenaCommands.Length, summary.GetProperty("arenaCommands").GetInt32());
        Assert.True(compared > 0);
    }

    /// <summary>Сегмент сценария трассы поворота: до тика, желаемая скорость (x, z), курс-цель.</summary>
    public sealed record TurnSegment(int End, Vector2 Velocity, float FacingGoal);

    public static TurnSegment[] TurnSegments(JsonElement trace) =>
    [
        .. trace.GetProperty("segments").EnumerateArray().Select(s => new TurnSegment(s.GetProperty("End").GetInt32(),
            new Vector2(s.GetProperty("velocity")[0].GetSingle(), s.GetProperty("velocity")[1].GetSingle()), s.GetProperty("FacingGoal").GetSingle())),
    ];

    private static Engine.Ecs.Entity Enemy(Simulation sim)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>()))
            if (chunk.Count > 0) return chunk.Entities[0];
        throw new InvalidOperationException("нет врага");
    }
}
