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
}
