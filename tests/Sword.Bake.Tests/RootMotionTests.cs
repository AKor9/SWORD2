using System.Numerics;
using Sword.Bake.Anim;
using Sword.Engine.Animation;
using Sword.Engine.Formats;
using ClipSource = Sword.Bake.Anim.ClipSource;

namespace Sword.Bake.Tests;

/// <summary>
/// Корневое движение выпечки клипа (<see cref="SkinBake.BuildClips"/>): вынос поворота и смещения из кости root, равномерный
/// дрейф цикла, смещение и поворот начала клипа; импорт пишет поворот корня в .swclip.
/// </summary>
public sealed class RootMotionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-rm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>
    /// Разворот на 90° с шагом вперёд, как Turn_090 в GASP: движение и поворот — в кости root.
    /// <paramref name="startYaw"/> и <paramref name="start"/> (UE, см) — как повёрнут и где стоит клип на первом кадре.
    /// </summary>
    private void WriteTurn(bool loop, float startYaw = 0, Vector3 start = default)
    {
        const int frames = 31;
        var man = new RawManifest { Bones = ["root", "pelvis"], Parents = [-1, 0] };
        var info = new RawClipInfo { Name = "Turn", Path = "/Game/Test/Turn", File = "Turn.bin", Fps = 30, Frames = frames, Loop = loop };
        var clip = new RawClip(info, 2);
        var pelvisOffset = new Vector3(0, 0, 90);   // UE, см: таз над root
        var startRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, startYaw);
        for (int f = 0; f < frames; f++)
        {
            float k = f / (float)(frames - 1);
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, k * MathF.PI / 2);   // UE: вокруг вертикали
            var rootPos = new Vector3(0, 100 * k, 0);                                   // UE +Y — вперёд Manny
            var pelvisPos = rootPos + Vector3.Transform(pelvisOffset, q) + Vector3.Transform(new Vector3(5, 0, 0), q);
            clip.Pos[f * 2] = start + Vector3.Transform(rootPos, startRot);
            clip.Rot[f * 2] = startRot * q;
            clip.Pos[f * 2 + 1] = start + Vector3.Transform(pelvisPos, startRot);
            clip.Rot[f * 2 + 1] = startRot * q;
        }
        RawAnimIO.Write(_dir, man, [clip]);
    }

    private ClipLibrary BuildTurn(bool loop, float startYaw = 0, Vector3 start = default)
    {
        WriteTurn(loop, startYaw, start);
        return SkinBake.BuildClips(new ClipSource(RawAnimIO.ReadManifest(_dir), _dir), TextWriter.Null);
    }

    [Fact]
    public void Turn_ExtractedFromRoot_PoseFacesForward()
    {
        var clips = BuildTurn(loop: false);
        Assert.InRange(MathF.Abs(clips.RootTurn(0)), MathF.PI / 2 - 0.01f, MathF.PI / 2 + 0.01f);
        Assert.InRange(clips.RootMotion[^1].Length(), 0.99f, 1.01f);

        // поза стоит на месте лицом вперёд: корень без поворота и смещения в каждом кадре
        for (int f = 0; f < clips.FrameCount; f++)
        {
            Assert.True(MathF.Abs(Quaternion.Dot(clips.LocalRot[f * 2], Quaternion.Identity)) > 0.9999f, $"кадр {f}");
            Assert.True((clips.LocalPos[f * 2] with { Y = 0 }).Length() < 1e-3f, $"кадр {f}");
        }

        // FK · root motion восстанавливает экспорт
        var man = RawAnimIO.ReadManifest(_dir);
        var raw = RawAnimIO.ReadClip(_dir, man, man.Clips[0]);
        var space = new UeSpace(man);
        var rot = new Quaternion[2];
        var pos = new Vector3[2];
        var comp = new Matrix4x4[2];
        foreach (int f in new[] { 0, 15, 30 })
        {
            clips.Sample(0, f / clips.SampleRate, rot, pos);
            clips.Skeleton.ToModel(rot, pos, comp);
            var m = ClipLibrary.MotionMatrix(clips.RootMotion[f], clips.RootYaw[f]);
            for (int j = 0; j < 2; j++)
                Assert.True(Vector3.Distance(Vector3.Transform(comp[j].Translation, m), space.Point(raw.P(f, j))) < 1e-3f, $"кадр {f}, сустав {j}");
        }
    }

    [Fact]
    public void Turn_Loop_UniformDrift()
    {
        var clips = BuildTurn(loop: true);
        float end = clips.RootYaw[^1];
        // за цикл — весь разворот клипа, а не ноль: иначе равномерность проверялась бы на нулях
        Assert.InRange(MathF.Abs(end), MathF.PI / 2 - 0.01f, MathF.PI / 2 + 0.01f);
        for (int f = 0; f < clips.FrameCount; f++)
            Assert.Equal(end * f / (clips.FrameCount - 1), clips.RootYaw[f], 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartOffset_RemovedFromPoseAndMotion(bool loop)
    {
        // Клип, начатый в 3,6 м от нуля и повёрнутым на 30° (как Stop/Pivot GASP), — тот же клип, что из нуля лицом вперёд.
        var zero = BuildTurn(loop);
        var shifted = BuildTurn(loop, startYaw: MathF.PI / 6, start: new Vector3(200, 300, 0));
        for (int i = 0; i < zero.LocalRot.Length; i++)
        {
            Assert.True(MathF.Abs(Quaternion.Dot(zero.LocalRot[i], shifted.LocalRot[i])) > 0.9999f, $"поворот {i}");
            Assert.True(Vector3.Distance(zero.LocalPos[i], shifted.LocalPos[i]) < 1e-3f, $"смещение {i}");
        }
        for (int f = 0; f < zero.FrameCount; f++)
        {
            Assert.True(Vector3.Distance(zero.RootMotion[f], shifted.RootMotion[f]) < 1e-3f, $"root motion кадра {f}");
            Assert.Equal(zero.RootYaw[f], shifted.RootYaw[f], 3);
        }
    }

    [Fact]
    public void Import_WritesClipWithYaw()
    {
        // импорт кладёт в .swclip то же, что собрала выпечка, — с поворотом корня (формат v2)
        var clips = BuildTurn(loop: false);
        string content = Path.Combine(_dir, "content");
        var man = RawAnimIO.ReadManifest(_dir);
        string clipPath = Path.Combine(content, "test", "Turn.swclip");
        Directory.CreateDirectory(Path.GetDirectoryName(clipPath)!);
        AnimImport.Run(_dir, AnimImport.Route(man, new Dictionary<string, List<string>> { ["test"] = [] }), "synth", (_, _) => clipPath, TextWriter.Null);

        ClipFile file;
        using (var s = File.OpenRead(clipPath)) file = ClipFile.Read(s, "Turn");
        Assert.Equal(ClipFile.Version, file.FileVersion);
        Assert.Equal("synth", file.Skeleton);
        Assert.Equal(SkeletonFile.ComputeHash(man.Bones, man.Parents), file.SkeletonHash);
        Assert.Equal("/Game/Test/Turn", file.SourcePath);
        Assert.Equal(clips.RootYaw.ToArray(), file.RootYaw);
        Assert.Equal(clips.RootMotion.ToArray(), file.RootMotion);
        Assert.Equal(clips.LocalRot.ToArray(), file.LocalRot);
        Assert.Equal(clips.LocalPos.ToArray(), file.LocalPos);
        Assert.NotEqual(0, file.RootYaw[^1]);
    }
}
