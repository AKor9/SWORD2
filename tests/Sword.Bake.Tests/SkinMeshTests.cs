using System.Numerics;
using Sword.Bake.Anim;
using Sword.Engine.Animation;
using Sword.Engine.Formats;

namespace Sword.Bake.Tests;

/// <summary>
/// Меш персонажа (<see cref="SkinBake.Mesh"/>, конвертер character): оси glTF подбираются по позе покоя, вершины и bind-поза —
/// в пространстве персонажа, суставы — кости экспорта по имени; игра поднимает из меша позу привязки скелета.
/// </summary>
public sealed class SkinMeshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sword-skin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static Vector3 Character(Vector3 ue) => new Vector3(ue.X, ue.Z, ue.Y) * 0.01f;

    private static void Near(Vector3 expected, Vector3 actual, string what) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"{what}: ожидалось {expected}, получено {actual}");

    [Fact]
    public void Mesh_AxesFromRestPose_VerticesAndBindInCharacterSpace()
    {
        Directory.CreateDirectory(_dir);
        string glb = Path.Combine(_dir, "synth.glb"), skeleton = Path.Combine(_dir, "skeleton.json"), raw = Path.Combine(_dir, "raw");
        SynthMesh.WriteGlb(glb);
        SynthMesh.WriteSkeletonJson(skeleton);
        RawAnimIO.Write(raw, SynthSkeleton.Manifest(), [SynthSkeleton.Walk("Idle", 5, loop: true, distance: 0, turn: 0, swing: 0)]);
        string output = Path.Combine(_dir, "out", "mesh.swmesh");
        var log = new StringWriter();

        SkinBake.Mesh(glb, skeleton, raw, output, log);

        Assert.Contains("оси glTF → UE: ue=(x,z,y), среднее отклонение вершин от своих костей 0.0 см", log.ToString());
        SkinnedMeshData mesh;
        using (var s = File.OpenRead(output)) mesh = SkinnedMeshData.Read(s, output);
        Assert.Equal(SynthSkeleton.Joints, mesh.JointNames);
        Assert.Equal(SynthSkeleton.Parents, mesh.JointParents);
        var rest = SynthMesh.RestUe();
        for (int j = 0; j < rest.Length; j++)
        {
            Near(Vector3.Zero, Vector3.Transform(Character(rest[j]), mesh.InverseBind[j]), $"bind {SynthSkeleton.Joints[j]}");
            for (int k = 0; k < SynthMesh.VertsPerJoint; k++)
            {
                var v = mesh.Vertices[j * SynthMesh.VertsPerJoint + k];
                Near(Character(rest[j] + SynthMesh.Corner(k)), v.Position, $"вершина {j}.{k}");
                Near(Vector3.Normalize(Character(SynthMesh.Corner(k))), v.Normal, $"нормаль {j}.{k}");
                Assert.Equal((uint)j, v.Joints);
                Assert.Equal(Vector3.UnitX, v.Weights);
            }
        }

        // игра поднимает из меша позу привязки: локальные смещения — смещения синтетического скелета
        using var meshFile = File.OpenRead(output);
        var bound = new Skeleton("synth", SynthSkeleton.Joints, SynthSkeleton.Parents).WithMeshBindPose(meshFile, output);
        for (int j = 1; j < rest.Length; j++)
            Near(SynthSkeleton.Offsets[j], bound.ReferencePos[j], $"поза привязки {SynthSkeleton.Joints[j]}");
    }

    [Fact]
    public void Mesh_JointMissingInExport_IsAnError()
    {
        Directory.CreateDirectory(_dir);
        string glb = Path.Combine(_dir, "synth.glb"), skeleton = Path.Combine(_dir, "skeleton.json"), raw = Path.Combine(_dir, "raw");
        SynthMesh.WriteGlb(glb);
        SynthMesh.WriteSkeletonJson(skeleton);
        var man = SynthSkeleton.Manifest();
        man.Bones[^1] = "clavicle_x";   // у экспорта другой сустав — меш на него не сядет
        RawAnimIO.Write(raw, man, [SynthSkeleton.Walk("Idle", 5, loop: true, distance: 0, turn: 0, swing: 0)]);
        Assert.Throws<InvalidDataException>(() => SkinBake.Mesh(glb, skeleton, raw, Path.Combine(_dir, "mesh.swmesh"), TextWriter.Null));
    }
}
