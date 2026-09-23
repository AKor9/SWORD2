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
