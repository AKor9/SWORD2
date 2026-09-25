using System.Numerics;
using System.Text;
using Sword.Bake.Engine.Anim;

namespace Sword.Bake.Engine.Tests;

public sealed class GltfTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sword-gltf-" + Guid.NewGuid().ToString("N") + ".glb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"ожидалось {expected}, получено {actual}");

    private void WriteGlb(string json, float[] data)
    {
        var bin = new byte[(data.Length * 4 + 3) & ~3];
        Buffer.BlockCopy(data, 0, bin, 0, data.Length * 4);
        var js = Encoding.UTF8.GetBytes(json);
        int jsLen = (js.Length + 3) & ~3;
        using var w = new BinaryWriter(File.Create(_path));
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsLen + 8 + bin.Length));
        w.Write((uint)jsLen);
        w.Write(0x4E4F534Au);
        w.Write(js);
        for (int i = js.Length; i < jsLen; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);
        w.Write(bin);
    }

    [Fact]
    public void InterleavedBuffer_AndNodeTransformOfStaticMesh()
    {
        // три вершины, позиция и нормаль вперемешку (byteStride 24);
        // узел: масштаб 2, поворот на 90° вокруг Z, сдвиг на 5 по Z
        float[] data = [0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 1, 0, 1, 0, 0];
        WriteGlb("""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":72}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":72,"byteStride":24}],
             "accessors":[{"bufferView":0,"byteOffset":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":0,"byteOffset":12,"componentType":5126,"count":3,"type":"VEC3"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1}}]}],
             "nodes":[{"mesh":0,"translation":[0,0,5],"rotation":[0,0,0.70710677,0.70710677],"scale":[2,2,2]}]}
            """, data);
        var mesh = GltfMesh.Load(_path);
        Assert.Equal(3, mesh.Positions.Length);
        Near(new Vector3(0, 0, 5), mesh.Positions[0]);
        Near(new Vector3(0, 2, 5), mesh.Positions[1]);
        Near(new Vector3(-2, 0, 5), mesh.Positions[2]);
        foreach (var n in mesh.Normals) Near(Vector3.UnitY, n);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
    }

    [Fact]
    public void AccessorOutsideBuffer_IsReported()
    {
        WriteGlb("""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":12}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":12}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """, [0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => GltfMesh.Load(_path));
    }
}
