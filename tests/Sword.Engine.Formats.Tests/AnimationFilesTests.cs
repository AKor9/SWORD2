using System.Numerics;
using System.Text;
using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

/// <summary>Скелет и клип библиотеки анимаций: раскладка SWORD 2eab13b, запись и чтение без искажений, обрезка.</summary>
public class AnimationFilesTests
{
    private static readonly string[] Joints = ["root", "pelvis", "spine"];
    private static readonly int[] Parents = [-1, 0, 1];

    private static ClipFile Clip(int frames = 4) => new()
    {
        Skeleton = "test", SkeletonHash = SkeletonFile.ComputeHash(Joints, Parents), SampleRate = 30, Frames = frames,
        Joints = Joints.Length, Loop = true, SourcePath = "/Game/Walk", SourceHash = "h1",
        LocalRot = Enumerable.Range(0, frames * 3).Select(i => Quaternion.Normalize(new Quaternion(i, 1, 2, 3))).ToArray(),
        LocalPos = Enumerable.Range(0, frames * 3).Select(i => new Vector3(i, -i, i * 0.5f)).ToArray(),
        RootMotion = Enumerable.Range(0, frames).Select(f => new Vector3(0, 0, f * 0.1f)).ToArray(),
        RootYaw = Enumerable.Range(0, frames).Select(f => f * 0.05f).ToArray(),
    };

    private static byte[] Save(Action<Stream> write)
    {
        using var s = new MemoryStream();
        write(s);
        return s.ToArray();
    }

    /// <summary>Байты, которые пишет <c>ClipAsset.Save</c> SWORD, — повтор его записи поле за полем.</summary>
    private static byte[] SwordClipBytes(ClipFile c)
    {
        using var s = new MemoryStream();
        using (var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("SWCL"u8);
            w.Write((ushort)2);
            w.Write(c.Skeleton);
            w.Write(c.SkeletonHash);
            w.Write(c.SampleRate);
            w.Write(c.Frames);
            w.Write(c.Joints);
            w.Write(c.Loop);
            w.Write(c.SourcePath);
            w.Write(c.SourceHash);
            for (int i = 0; i < c.LocalRot.Length; i++)
            {
                var q = c.LocalRot[i];
                var p = c.LocalPos[i];
                w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
                w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
            }
            foreach (var m in c.RootMotion) { w.Write(m.X); w.Write(m.Y); w.Write(m.Z); }
            foreach (float y in c.RootYaw) w.Write(y);
            w.Write("LCWS"u8);
        }
        return s.ToArray();
    }

    [Fact]
    public void Clip_BytesMatchSword()
    {
        var c = Clip();
        Assert.Equal(SwordClipBytes(c), Save(c.Write));
    }

    [Fact]
    public void Clip_RoundTripKeepsEverything()
    {
        var a = Clip();
        var bytes = Save(a.Write);
        var b = ClipFile.Read(new MemoryStream(bytes), "mem");
        Assert.Equal(a.Skeleton, b.Skeleton);
        Assert.Equal(a.SkeletonHash, b.SkeletonHash);
        Assert.Equal(a.SampleRate, b.SampleRate);
        Assert.Equal(a.Loop, b.Loop);
        Assert.Equal(a.LocalRot, b.LocalRot);
        Assert.Equal(a.LocalPos, b.LocalPos);
        Assert.Equal(a.RootMotion, b.RootMotion);
        Assert.Equal(a.RootYaw, b.RootYaw);   // Formats_KeepYaw SWORD
        Assert.Equal(bytes, Save(b.Write));
    }

    [Fact]
    public void Clip_Version1_ReadsWithZeroYaw()
    {
        var c = Clip(2);
        var bytes = SwordClipBytes(c);
        // версия 1: без дорожки поворота корня
        bytes[4] = 1;
        int yawStart = bytes.Length - 4 - c.Frames * 4;
        var v1 = bytes[..yawStart].Concat("LCWS"u8.ToArray()).ToArray();
        var b = ClipFile.Read(new MemoryStream(v1), "v1");
        Assert.Equal(1, b.FileVersion);
        Assert.Equal(new float[c.Frames], b.RootYaw);
        Assert.Equal(c.RootMotion, b.RootMotion);
    }

    [Fact]
    public void Clip_EveryTruncationIsInvalidData()
    {
        var bytes = Save(Clip(2).Write);
        for (int n = 0; n < bytes.Length; n++)
            Assert.Throws<InvalidDataException>(() => ClipFile.Read(new MemoryStream(bytes[..n]), $"обрезан до {n}"));
    }

    [Fact]
    public void Clip_HugeFrameCountIsRejectedBeforeAllocation()
    {
        var bytes = Save(Clip(2).Write);
        // кадров — сразу за заголовком: магия 4, версия 2, строка «test» 5, хеш 4, частота 4
        BitConverter.GetBytes(1_000_000).CopyTo(bytes, 19);
        BitConverter.GetBytes(1000).CopyTo(bytes, 23);
        Assert.Throws<InvalidDataException>(() => ClipFile.Read(new MemoryStream(bytes), "огромный"));
    }

    [Fact]
    public void Skeleton_BytesMatchSwordAndRoundTrip()
    {
        var a = new SkeletonFile { Id = "ue5_manny", JointNames = Joints, JointParents = Parents };
        var bytes = Save(a.Write);
        using var expected = new MemoryStream();
        using (var w = new BinaryWriter(expected, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("SWSN"u8);
            w.Write((ushort)1);
            w.Write(a.Id);
            w.Write(Joints.Length);
            for (int j = 0; j < Joints.Length; j++) { w.Write(Joints[j]); w.Write(Parents[j]); }
            w.Write("NSWS"u8);
        }
        Assert.Equal(expected.ToArray(), bytes);
        var b = SkeletonFile.Read(new MemoryStream(bytes), "mem");
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(Joints, b.JointNames);
        Assert.Equal(Parents, b.JointParents);
        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Skeleton_EveryTruncationIsInvalidData()
    {
        var bytes = Save(new SkeletonFile { Id = "s", JointNames = Joints, JointParents = Parents }.Write);
        for (int n = 0; n < bytes.Length; n++)
            Assert.Throws<InvalidDataException>(() => SkeletonFile.Read(new MemoryStream(bytes[..n]), $"обрезан до {n}"));
    }

    [Fact]
    public void Skeleton_SelfParentIsRejected()
    {
        var bytes = Save(new SkeletonFile { Id = "s", JointNames = Joints, JointParents = [-1, 1, 1] }.Write);
        Assert.Throws<InvalidDataException>(() => SkeletonFile.Read(new MemoryStream(bytes), "сам себе родитель"));
    }

    /// <summary>Хеш скелета — тот же FNV-1a, что в SWORD: клипы эталонного пака находят по нему свой скелет.</summary>
    [Fact]
    public void Skeleton_HashIsFnvOfNamesAndParents()
    {
        uint h = 2166136261;
        void Mix(int b) { h ^= (uint)b; h *= 16777619; }
        foreach (char ch in "a") { Mix(ch & 0xFF); Mix(ch >> 8); }
        Mix(0);
        Mix(-1 & 0xFF); Mix(-1 >> 8 & 0xFF);
        Assert.Equal(h, SkeletonFile.ComputeHash(["a"], [-1]));
    }
}
