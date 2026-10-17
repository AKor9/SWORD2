using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;
using static Sword.Engine.Animation.Tests.TestClips;

namespace Sword.Engine.Animation.Tests;

/// <summary>Библиотека клипов: файлы — потоки в памяти в порядке контента, скелет передаётся готовым.</summary>
public sealed class LibraryTests
{
    [Fact]
    public void Load_GroupsBySourceAndSkipsForeignClips()
    {
        var skipped = new List<string>();
        // порядок путей контента: по возрастанию без учёта регистра
        var lib = ClipLibrary.Load(Skeleton(),
        [
            Marked("alpha", "Idle", 5, 3),
            Marked("alpha", "Other", 5, 4, skeleton: "other"),
            Marked("alpha", "Rate", 5, 6, rate: 60),
            Marked("alpha", "Slash", 3, 2),
            Marked("alpha", "Stale", 5, 5, hash: 1),
            Marked("zeta", "Walk_Loop", 4, 1),
        ], skipped)!;
        Assert.Equal(["alpha/Idle", "alpha/Slash", "zeta/Walk_Loop"], lib.Clips.ToArray().Select(c => c.Name));
        string log = string.Join("\n", skipped);
        Assert.Contains("alpha/Stale", log);
        Assert.Contains("alpha/Rate", log);
        Assert.DoesNotContain("alpha/Other", log);   // чужой скелет — молча
        Assert.Equal("zeta", lib.Clips[2].Source);
        Assert.Equal("Walk_Loop", lib.Clips[2].ShortName);
        Assert.True(lib.Clips[2].Loop);

        // позы и корневое движение скопированы в свои диапазоны
        var slash = lib.Clips[1];
        Assert.Equal(5, slash.Start);
        Assert.Equal(2, lib.LocalPos[slash.Start * Joints.Length].X);
        Assert.Equal(0.2f, lib.RootMotion[slash.Start + 2].Z, 5);

        Assert.Equal(1, lib.FindClip("Slash"));
        Assert.Equal(1, lib.FindClip("alpha/Slash"));
        Assert.Equal(-1, lib.FindClip("beta/Slash"));
    }

    [Fact]
    public void FindClip_AmbiguousShortName_NotFound()
    {
        var lib = Library(Marked("a", "Idle", 2, 0), Marked("b", "Idle", 2, 0));
        Assert.Equal(-1, lib.FindClip("Idle"));
        Assert.Equal(1, lib.FindClip("b/Idle"));
    }

    [Fact]
    public void Load_NoClips_ReturnsNull()
    {
        Assert.Null(ClipLibrary.Load(Skeleton(), []));
        Assert.Null(ClipLibrary.Load(Skeleton(), [Marked("a", "Idle", 2, 0, skeleton: "other")]));
    }

    [Fact]
    public void Load_IgnoresFilesOutsideSourceFolders()
    {
        var deep = Marked("a", "Idle", 2, 0);
        var lib = Library(deep with { Path = "animations/a/sub/Idle.swclip" }, Marked("a", "Walk", 2, 0), deep with { Path = "animations/Idle.swclip" });
        Assert.Equal(["a/Walk"], lib.Clips.ToArray().Select(c => c.Name));
    }

    [Fact]
    public void Load_CorruptClip_IsSkippedWithReason()
    {
        var bad = new ClipSource("animations/a/Bad.swclip", () => new MemoryStream([1, 2, 3]), "mem:bad");
        var skipped = new List<string>();
        var lib = ClipLibrary.Load(Skeleton(), [bad, Marked("a", "Idle", 2, 0)], skipped)!;
        Assert.Equal(["a/Idle"], lib.Clips.ToArray().Select(c => c.Name));
        Assert.Contains(skipped, s => s.StartsWith("a/Bad: пропущен", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_LoopWrapsAndOneShotClamps()
    {
        var lib = Library(Marked("a", "Walk_Loop", 4, 0), Marked("a", "Hit", 4, 0));
        var rot = new System.Numerics.Quaternion[Joints.Length];
        var pos = new System.Numerics.Vector3[Joints.Length];
        // y смещения — номер ключа в файле клипа: кадр f сустава 0 — f·3
        lib.Sample(0, 1.5f / 30, rot, pos);
        Assert.Equal(4.5f, pos[0].Y, 4);
        lib.Sample(0, 4f / 30, rot, pos);   // цикл 3 кадра: 4 → 1
        Assert.Equal(3f, pos[0].Y, 4);
        lib.Sample(0, -1f / 30, rot, pos);  // назад по кругу: −1 → 2
        Assert.Equal(6f, pos[0].Y, 4);
        lib.Sample(1, 10, rot, pos);        // не цикл — последний кадр (номер ключа — внутри своего файла)
        Assert.Equal(3 * 3f, pos[0].Y, 4);
        Assert.Equal(3f / 30, lib.Duration(1), 6);
        Assert.Equal(0.3f / (3f / 30), lib.RootSpeed(1), 4);
    }

    /// <summary>
    /// Сэмпл между ключами — сплайн: ключи — как есть, а ускорение на 120 Гц не скачет на ключах 30 кадров/с. Ключи — равноускоренное
    /// движение (смещение, root motion и угол поворота — квадрат номера кадра): сплайн повторяет его и между ключами, вторая разность
    /// по тикам постоянна, а у линейного сэмпла она нулевая внутри отрезка и всплеском — на каждом ключе.
    /// </summary>
    [Fact]
    public void Sample_BetweenKeys_SplineKeepsAccelerationAcrossKeys()
    {
        const int frames = 9;
        var file = new ClipFile
        {
            Skeleton = "test", SkeletonHash = SkeletonFile.ComputeHash(Joints, Parents), SampleRate = 30, Frames = frames, Joints = Joints.Length,
            LocalRot = Enumerable.Range(0, frames * Joints.Length)
                .Select(i => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.02f * (i / Joints.Length) * (i / Joints.Length))).ToArray(),
            LocalPos = Enumerable.Range(0, frames * Joints.Length).Select(i => new Vector3(0.01f * (i / Joints.Length) * (i / Joints.Length), 0, 0)).ToArray(),
            RootMotion = Enumerable.Range(0, frames).Select(f => new Vector3(0, 0, 0.01f * f * f)).ToArray(),
            RootYaw = new float[frames],
        };
        var lib = Library(Source("a", "Accel", file));
        var rot = new Quaternion[Joints.Length];
        var pos = new Vector3[Joints.Length];
        float Position(float f) => 0.01f * f * f;
        for (int key = 0; key < frames; key++)
        {
            lib.Sample(0, key / 30f, rot, pos);
            Assert.Equal(Position(key), pos[1].X, 1e-5f);
        }
        // внутренние отрезки (соседи — настоящие ключи): 120 Гц, четыре тика на кадр
        var x = new List<float>();
        for (int tick = 4; tick <= 4 * (frames - 2); tick++)
        {
            float f = tick / 4f;
            lib.Sample(0, f / 30, rot, pos);
            Assert.Equal(Position(f), pos[1].X, 1e-5f);
            Assert.Equal(0.02f * f * f, 2 * MathF.Acos(Math.Clamp(MathF.Abs(rot[1].W), 0, 1)), 1e-3f);
            Assert.Equal(Position(f), lib.RootMotionAt(0, f / 30).Z, 1e-5f);
            x.Add(pos[1].X);
        }
        for (int i = 1; i < x.Count - 1; i++) Assert.Equal(2 * 0.01f / 16, x[i + 1] - 2 * x[i] + x[i - 1], 1e-5f);
    }
}
