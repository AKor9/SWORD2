using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

public class SkinnedMeshDataTests
{
    private static SkinnedMeshData Triangle() => new()
    {
        Vertices =
        [
            new SkinVertex { Position = new(0, 0, 0), Normal = Vector3.UnitY, Uv = new(0, 0), Joints = 0, Weights = new(1, 0, 0) },
            new SkinVertex { Position = new(1, 0, 0), Normal = Vector3.UnitY, Uv = new(1, 0), Joints = 1, Weights = new(0.5f, 0.5f, 0) },
            new SkinVertex { Position = new(0, 0, 1), Normal = Vector3.UnitY, Uv = new(0, 1), Joints = 1 | 1 << 8, Weights = new(1, 0, 0) },
        ],
        Indices = [0, 1, 2],
        Sections = [new MeshSection(0, 3, "skin")],
        JointNames = ["root", "pelvis"],
        JointParents = [-1, 0],
        InverseBind = [Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0)],
    };

    private static byte[] Save(SkinnedMeshData m)
    {
        using var s = new MemoryStream();
        m.Write(s);
        return s.ToArray();
    }

    /// <summary>Поток, отдающий за один <c>Read</c> не больше <paramref name="chunk"/> байт, — законное поведение потока.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
    }

    [Fact]
    public unsafe void VertexLayoutMatchesShader() => Assert.Equal(SkinVertex.Size, sizeof(SkinVertex));

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var a = Triangle();
        var bytes = Save(a);
        var b = SkinnedMeshData.Read(new MemoryStream(bytes), "mem");
        Assert.Equal(a.Vertices, b.Vertices);
        Assert.Equal(a.Indices, b.Indices);
        Assert.Equal(a.Sections, b.Sections);
        Assert.Equal(a.JointNames, b.JointNames);
        Assert.Equal(a.JointParents, b.JointParents);
        Assert.Equal(a.InverseBind, b.InverseBind);
        Assert.Equal(bytes, Save(b));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ShortReadsAreNotTruncation(int chunk)
    {
        var bytes = Save(Triangle());
        var m = SkinnedMeshData.Read(new TrickleStream(bytes, chunk), "trickle");
        Assert.Equal(Triangle().Vertices, m.Vertices);
        Assert.Equal(bytes, Save(m));
    }

    [Fact]
    public void EveryTruncationIsInvalidData()
    {
        // Обрезка в любом месте — InvalidDataException, а не EndOfStream, IndexOutOfRange или «успех».
        var bytes = Save(Triangle());
        for (int n = 0; n < bytes.Length; n++)
            Assert.Throws<InvalidDataException>(() => SkinnedMeshData.Read(new MemoryStream(bytes[..n]), $"обрезан до {n}"));
    }

    [Fact]
    public void JointOutsideSkeletonIsRejected()
    {
        var m = Triangle();
        m.Vertices[2].Joints = 5;
        Assert.Throws<InvalidDataException>(() => SkinnedMeshData.Read(new MemoryStream(Save(m)), "mem"));
    }
}
