using System.Numerics;
using System.Text;
using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

/// <summary>
/// Скелет (<c>.swskeleton</c>, версия 1) и клип (<c>.swclip</c>, версия 2) библиотеки анимаций — спецификация нашего формата пака
/// контента: раскладка поле за полем, запись и чтение без искажений, версия 1 клипа, обрезка.
/// </summary>
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

    /// <summary>
    /// Раскладка клипа версии 2 (little-endian; строки — с длиной 7-битным кодом, UTF-8): магия <c>SWCL</c>, версия (ushort),
    /// скелет, хеш скелета (uint), частота (float), кадров, суставов (int), цикл (bool), путь и хеш источника; по кадрам и суставам —
    /// поворот (x, y, z, w) и смещение (x, y, z); корневое движение по кадрам (x, y, z); поворот корня по кадрам; магия конца <c>LCWS</c>.
    /// </summary>
    [Fact]
    public void Clip_LayoutV2_FieldByField()
    {
        var c = Clip();
        var bytes = Save(c.Write);
        using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        Assert.Equal("SWCL"u8.ToArray(), r.ReadBytes(4));
        Assert.Equal((ushort)2, r.ReadUInt16());
        Assert.Equal(c.Skeleton, r.ReadString());
        Assert.Equal(c.SkeletonHash, r.ReadUInt32());
        Assert.Equal(c.SampleRate, r.ReadSingle());
        Assert.Equal((c.Frames, c.Joints), (r.ReadInt32(), r.ReadInt32()));
        Assert.Equal(c.Loop, r.ReadBoolean());
        Assert.Equal((c.SourcePath, c.SourceHash), (r.ReadString(), r.ReadString()));
        for (int i = 0; i < c.Frames * c.Joints; i++)
        {
            Assert.Equal(c.LocalRot[i], new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Assert.Equal(c.LocalPos[i], new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        }
        for (int f = 0; f < c.Frames; f++) Assert.Equal(c.RootMotion[f], new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        for (int f = 0; f < c.Frames; f++) Assert.Equal(c.RootYaw[f], r.ReadSingle());
        Assert.Equal("LCWS"u8.ToArray(), r.ReadBytes(4));
        Assert.Equal(bytes.Length, r.BaseStream.Position);
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
        Assert.Equal(a.RootYaw, b.RootYaw);
        Assert.Equal(bytes, Save(b.Write));
    }

    [Fact]
    public void Clip_ReadHeader_HeaderOnly_NoPoses()
    {
        var a = Clip();
        var bytes = Save(a.Write);
        var h = ClipFile.ReadHeader(new MemoryStream(bytes), "mem");
        Assert.Equal((a.Skeleton, a.SkeletonHash, a.SampleRate, a.Frames, a.Joints, a.Loop, a.SourcePath, a.SourceHash),
            (h.Skeleton, h.SkeletonHash, h.SampleRate, h.Frames, h.Joints, h.Loop, h.SourcePath, h.SourceHash));
        Assert.Empty(h.LocalRot);
        Assert.Empty(h.RootMotion);
        Assert.Throws<InvalidDataException>(() => ClipFile.ReadHeader(new MemoryStream(bytes[..10]), "mem"));
        bytes[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => ClipFile.ReadHeader(new MemoryStream(bytes), "mem"));
    }

    [Fact]
    public void Clip_Version1_ReadsWithZeroYaw()
    {
        var c = Clip(2);
        var bytes = Save(c.Write);
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

    /// <summary>
    /// Раскладка скелета версии 1: магия <c>SWSN</c>, версия (ushort), имя скелета, суставов (int), по суставам — имя и номер родителя
    /// (int, −1 — корень), магия конца <c>NSWS</c>; чтение возвращает то же.
    /// </summary>
    [Fact]
    public void Skeleton_LayoutV1_FieldByField_AndRoundTrip()
    {
        var a = new SkeletonFile { Id = "ue5_manny", JointNames = Joints, JointParents = Parents };
        var bytes = Save(a.Write);
        using (var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8))
        {
            Assert.Equal("SWSN"u8.ToArray(), r.ReadBytes(4));
            Assert.Equal((ushort)1, r.ReadUInt16());
            Assert.Equal(a.Id, r.ReadString());
            Assert.Equal(Joints.Length, r.ReadInt32());
            for (int j = 0; j < Joints.Length; j++) Assert.Equal((Joints[j], Parents[j]), (r.ReadString(), r.ReadInt32()));
            Assert.Equal("NSWS"u8.ToArray(), r.ReadBytes(4));
            Assert.Equal(bytes.Length, r.BaseStream.Position);
        }
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

    /// <summary>
    /// Хеш скелета — FNV-1a по байтам UTF-16 имён (с нулём после имени) и двум младшим байтам номеров родителей: клипы пака контента
    /// находят по нему свой скелет (хеш записан в клипах).
    /// </summary>
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
