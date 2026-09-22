using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Engine.Animation.Tests;

/// <summary>Клипы тестов в памяти: файлы .swclip как их отдаёт контент — путь, поток, имя для сообщений.</summary>
internal static class TestClips
{
    public static readonly string[] Joints = ["root", "pelvis", "spine"];
    public static readonly int[] Parents = [-1, 0, 1];

    public static Skeleton Skeleton(string id = "test") => new(id, Joints, Parents);

    public static ClipSource Source(string source, string name, ClipFile clip)
    {
        using var s = new MemoryStream();
        clip.Write(s);
        byte[] bytes = s.ToArray();
        string path = $"anim/{source}/{name}.swclip";
        return new ClipSource(path, () => new MemoryStream(bytes), "mem:" + path);
    }

    /// <summary>
    /// Клип <c>LibraryTests.WriteClip</c> SWORD: единичные повороты, смещение x = <paramref name="marker"/>, y — номер
    /// ключа; корень едет вперёд на 0,1 м за кадр.
    /// </summary>
    public static ClipSource Marked(string source, string name, int frames, float marker, string skeleton = "test",
        uint? hash = null, float rate = 30) => Source(source, name, new ClipFile
    {
        Skeleton = skeleton, SkeletonHash = hash ?? SkeletonFile.ComputeHash(Joints, Parents), SampleRate = rate,
        Frames = frames, Joints = Joints.Length, Loop = name.Contains("Loop"), SourcePath = "/Game/" + name, SourceHash = "h",
        LocalRot = Enumerable.Repeat(Quaternion.Identity, frames * Joints.Length).ToArray(),
        LocalPos = Enumerable.Range(0, frames * Joints.Length).Select(i => new Vector3(marker, i, 0)).ToArray(),
        RootMotion = Enumerable.Range(0, frames).Select(f => new Vector3(0, 0, f * 0.1f)).ToArray(),
        RootYaw = new float[frames],
    });

    /// <summary>Клип с поворотами, меняющимися от кадра к кадру: для сверки сэмплинга и смешивания.</summary>
    public static ClipSource Turning(string source, string name, int frames, float phase, bool loop) => Source(source, name, new ClipFile
    {
        Skeleton = "test", SkeletonHash = SkeletonFile.ComputeHash(Joints, Parents), SampleRate = 30, Frames = frames,
        Joints = Joints.Length, Loop = loop,
        LocalRot = Enumerable.Range(0, frames * Joints.Length)
            .Select(i => Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, i % 3, 0.5f)), phase + i * 0.07f)).ToArray(),
        LocalPos = Enumerable.Range(0, frames * Joints.Length).Select(i => new Vector3(0.1f * (i % 3), 0.3f + 0.01f * i, phase)).ToArray(),
        RootMotion = Enumerable.Range(0, frames).Select(f => new Vector3(0, 0, f * 0.05f)).ToArray(),
        RootYaw = Enumerable.Range(0, frames).Select(f => f * 0.01f).ToArray(),
    });

    public static ClipLibrary Library(params ClipSource[] files) => ClipLibrary.Load(Skeleton(), files)!;
}
