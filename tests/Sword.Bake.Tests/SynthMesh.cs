using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Sword.Bake.Tests;

/// <summary>
/// Синтетические исходники меша персонажа на <see cref="SynthSkeleton"/>: скинированный .glb (оси glTF экспортёра UE:
/// glTF (x, y-вверх, z) = UE (x, z, y), сантиметры) — у каждого сустава кучка из 8 вершин вокруг его точки покоя с весом 1;
/// и skeleton.json с позой покоя (как tools/ue/export_meshes.py).
/// </summary>
internal static class SynthMesh
{
    public const int VertsPerJoint = 8;

    /// <summary>Поза покоя: компонентные точки суставов UE, см (повороты — единичные).</summary>
    public static Vector3[] RestUe()
    {
        int n = SynthSkeleton.Joints.Length;
        var p = new Vector3[n];
        for (int j = 0; j < n; j++)
            p[j] = (SynthSkeleton.Parents[j] >= 0 ? p[SynthSkeleton.Parents[j]] : default) + SynthSkeleton.ToUe(SynthSkeleton.Offsets[j]);
        return p;
    }

    /// <summary>Смещение k-й вершины кучки от сустава, см: углы куба со стороной 4 см (центр — сустав).</summary>
    public static Vector3 Corner(int k) => new((k & 1) != 0 ? 2 : -2, (k & 2) != 0 ? 2 : -2, (k & 4) != 0 ? 2 : -2);

    public static void WriteSkeletonJson(string path)
    {
        var rest = RestUe();
        var bones = new JsonArray();
        for (int j = 0; j < rest.Length; j++)
            bones.Add(new JsonObject
            {
                ["name"] = SynthSkeleton.Joints[j],
                ["world"] = new JsonObject
                {
                    ["t"] = new JsonArray(rest[j].X, rest[j].Y, rest[j].Z),
                    ["q"] = new JsonArray(0, 0, 0, 1),
                },
            });
        File.WriteAllText(path, new JsonObject { ["character"] = new JsonObject { ["bones"] = bones } }.ToJsonString());
    }

    /// <summary>glTF экспортёра UE: UE (x, y, z) → glTF (x, z, y).</summary>
    public static Vector3 UeToGltf(Vector3 ue) => new(ue.X, ue.Z, ue.Y);

    public static void WriteGlb(string path)
    {
        var rest = RestUe();
        int jc = rest.Length, vc = jc * VertsPerJoint;
        var pos = new Vector3[vc];
        var nrm = new Vector3[vc];
        var uv = new Vector2[vc];
        var joints = new byte[vc * 4];
        var weights = new float[vc * 4];
        for (int j = 0; j < jc; j++)
            for (int k = 0; k < VertsPerJoint; k++)
            {
                int v = j * VertsPerJoint + k;
                pos[v] = UeToGltf(rest[j] + Corner(k));
                nrm[v] = Vector3.Normalize(UeToGltf(Corner(k)));
                uv[v] = new Vector2(j / (float)jc, k / (float)VertsPerJoint);
                joints[v * 4] = (byte)j;
                weights[v * 4] = 1;
            }
        var indices = Enumerable.Range(0, vc - vc % 3).Select(i => (uint)i).ToArray();
        var inverseBind = rest.Select(r => Matrix4x4.CreateTranslation(-UeToGltf(r))).ToArray();

        var bin = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();
        int Add<T>(T[] data, int componentType, string type) where T : struct
        {
            while (bin.Length % 4 != 0) bin.WriteByte(0);
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = bin.Length, ["byteLength"] = bytes.Length });
            bin.Write(bytes);
            int comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16 };
            int size = componentType == 5121 ? 1 : 4;
            accessors.Add(new JsonObject
            {
                ["bufferView"] = views.Count - 1, ["componentType"] = componentType, ["count"] = bytes.Length / size / comps, ["type"] = type,
            });
            return accessors.Count - 1;
        }
        int aPos = Add(pos, 5126, "VEC3"), aNrm = Add(nrm, 5126, "VEC3"), aUv = Add(uv, 5126, "VEC2");
        int aJoints = Add(joints, 5121, "VEC4"), aWeights = Add(weights, 5126, "VEC4"), aIdx = Add(indices, 5125, "SCALAR");
        // матрица glTF по столбцам (v' = M·v) в памяти — те же 16 чисел, что Matrix4x4 System.Numerics (v' = v·M) по строкам
        int aIb = Add(inverseBind, 5126, "MAT4");

        var nodes = new JsonArray();
        for (int j = 0; j < jc; j++) nodes.Add(new JsonObject { ["name"] = SynthSkeleton.Joints[j] });
        nodes.Add(new JsonObject { ["name"] = "mesh", ["mesh"] = 0, ["skin"] = 0 });
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = aPos, ["NORMAL"] = aNrm, ["TEXCOORD_0"] = aUv, ["JOINTS_0"] = aJoints, ["WEIGHTS_0"] = aWeights,
                    },
                    ["indices"] = aIdx,
                }),
            }),
            ["nodes"] = nodes,
            ["skins"] = new JsonArray(new JsonObject
            {
                ["joints"] = new JsonArray([.. Enumerable.Range(0, jc).Select(j => (JsonNode)j)]),
                ["inverseBindMatrices"] = aIb,
            }),
        };
        Glb.Write(path, json.ToJsonString(), bin.ToArray());
    }
}

/// <summary>Контейнер .glb: заголовок, чанк JSON (добит пробелами до 4 байт), чанк BIN.</summary>
internal static class Glb
{
    public static void Write(string path, string json, byte[] data)
    {
        var bin = new byte[(data.Length + 3) & ~3];
        data.CopyTo(bin, 0);
        var js = Encoding.UTF8.GetBytes(json);
        int jsLen = (js.Length + 3) & ~3;
        using var w = new BinaryWriter(File.Create(path));
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
}
