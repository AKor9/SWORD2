using System.Numerics;
using Sword.Bake.Engine.Anim;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Bake.Engine.Tests;

/// <summary>
/// Сравнитель <c>.swclip</c> для <c>sword-bake diff</c> (<see cref="ClipComparer"/>): клипы, отличные только
/// <see cref="ClipFile.SourceHash"/>, равны; отличие любого другого поля — «отличаются» с этим полем; не клип — ошибка
/// разбора. Два пака, отличные только хешем исходника клипа, по <see cref="PakDiff"/> равны (без сравнителя — нет).
/// </summary>
public sealed class ClipComparerTests : IDisposable
{
    private const string ClipPath = "anim/walk/Walk_F.swclip";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-bake-engine-tests", "comparer-" + Guid.NewGuid().ToString("N")[..12]);

    public ClipComparerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static ClipFile Clip(string sourceHash) => new()
    {
        Skeleton = "synth", SkeletonHash = 0x1234ABCD, SampleRate = 30, Frames = 2, Joints = 2, Loop = true,
        SourcePath = "/Game/Synth/Walk/Walk_F", SourceHash = sourceHash,
        LocalRot = [Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(0.1f, 0, 0), Quaternion.Identity, Quaternion.Identity],
        LocalPos = [new(0, 0.95f, 0), new(0.1f, -0.05f, 0), new(0, 0.96f, 0), new(0.1f, -0.05f, 0)],
        RootMotion = [Vector3.Zero, new(0, 0, 0.04f)],
        RootYaw = [0, 0.02f],
    };

    private static byte[] Bytes(ClipFile clip)
    {
        using var s = new MemoryStream();
        clip.Write(s);
        return s.ToArray();
    }

    [Fact]
    public void OnlySourceHashDiffers_Equal()
    {
        var r = new ClipComparer().Compare(ClipPath, Bytes(Clip("aaaa")), Bytes(Clip("bbbbbbbb")));
        Assert.True(r.Equal, r.Detail);
        Assert.Contains("SourceHash", r.Detail);
    }

    [Fact]
    public void AnyOtherField_Differs()
    {
        var comparer = new ClipComparer();
        byte[] a = Bytes(Clip("aaaa"));

        var yaw = Clip("aaaa");
        yaw.RootYaw[1] = 0.03f;
        var r = comparer.Compare(ClipPath, a, Bytes(yaw));
        Assert.False(r.Equal);
        Assert.Contains("поворот root motion", r.Detail);

        var source = Clip("bbbb");
        source.SourcePath = "/Game/Synth/Run/Walk_F";
        r = comparer.Compare(ClipPath, a, Bytes(source));
        Assert.False(r.Equal);
        Assert.Contains("путь исходника", r.Detail);

        var pose = Clip("aaaa");
        pose.LocalPos[3] = new Vector3(0.1f, -0.05f, -0f);   // −0 ≠ +0: сравнение побайтное
        r = comparer.Compare(ClipPath, a, Bytes(pose));
        Assert.False(r.Equal);
        Assert.Contains("смещения", r.Detail);
    }

    [Fact]
    public void NotAClip_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => new ClipComparer().Compare(ClipPath, Bytes(Clip("aaaa")), "not a clip"u8.ToArray()));

    [Fact]
    public void PaksDifferingOnlyInSourceHash_EqualWithComparer()
    {
        string a = Pak("a", Clip("aaaa")), b = Pak("b", Clip("bbbbbbbb"));

        var report = PakDiff.Compare(a, b, [new ClipComparer()]);
        Assert.True(report.Ok, string.Join("\n", report.Files.Where(f => f.IsDiscrepancy).Select(f => $"{f.Path}: {f.Detail}")));
        var clip = Assert.Single(report.Files, f => f.Path == ClipPath);
        Assert.Equal(FileDiffKind.SameContent, clip.Kind);
        Assert.Equal(FileDiffKind.Same, Assert.Single(report.Files, f => f.Path == "combat/actions.json").Kind);

        // без сравнителя — отличаются байтами
        Assert.False(PakDiff.Compare(a, b, []).Ok);
    }

    private string Pak(string name, ClipFile clip)
    {
        string path = Path.Combine(_dir, name + PakFormat.Extension);
        using var writer = new PakWriter(path);
        using (var s = new MemoryStream(Bytes(clip))) writer.Add(ClipPath, s);
        using (var s = new MemoryStream("{}"u8.ToArray())) writer.Add("combat/actions.json", s);
        writer.Commit();
        return path;
    }
}
