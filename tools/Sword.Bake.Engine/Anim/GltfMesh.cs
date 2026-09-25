using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Sword.Bake.Engine.Anim;

/// <summary>
/// Читатель .glb (glTF 2.0 binary): первый меш, треугольники, POSITION/NORMAL/TEXCOORD_0, скин JOINTS_n/WEIGHTS_n
/// (четыре сильнейших влияния), имена суставов и inverse bind. Буферы с byteStride поддерживаются; sparse
/// и внешние буферы — нет, с ошибкой. Трансформ узла меша применяется к вершинам только у меша без скина:
/// у скинированного меша по спецификации glTF он игнорируется, позу задают суставы.
/// Оси и единицы остаются как в файле; перевод — у вызывающего.
/// </summary>
public sealed class GltfMesh
{
    public Vector3[] Positions = [], Normals = [];
    public Vector2[] Uvs = [];
    /// <summary>Имена материалов glTF (пусто — без имени) и материал каждой вершины (−1 — без материала).</summary>
    public string[] MaterialNames = [];
    public int[] VertexMaterial = [];
    /// <summary>[вершина·4] четыре самых сильных влияния (перенормированы); индексы — в <see cref="JointNames"/>.</summary>
    public int[] Joints = [];
    public float[] Weights = [];
    public int[] Indices = [];
    public string[] JointNames = [];
    public Matrix4x4[] InverseBind = [];

    private const uint MagicGlb = 0x46546C67, ChunkJson = 0x4E4F534A, ChunkBin = 0x004E4942;
    private const int TypeByte = 5121, TypeShort = 5123, TypeInt = 5125, TypeFloat = 5126;

    public static GltfMesh Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != MagicGlb) throw new InvalidDataException($"{path}: не .glb");
        int jsonLen = BitConverter.ToInt32(bytes, 12);
        if (BitConverter.ToUInt32(bytes, 16) != ChunkJson || jsonLen < 0 || 20L + jsonLen > bytes.Length)
            throw new InvalidDataException($"{path}: первый чанк не JSON");
        int binHeader = 20 + jsonLen;
        ReadOnlyMemory<byte> bin = default;
        if (binHeader + 8 <= bytes.Length)
        {
            int binLen = BitConverter.ToInt32(bytes, binHeader);
            if (BitConverter.ToUInt32(bytes, binHeader + 4) != ChunkBin || binLen < 0 || binHeader + 8L + binLen > bytes.Length)
                throw new InvalidDataException($"{path}: второй чанк не BIN");
            bin = bytes.AsMemory(binHeader + 8, binLen);
        }
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, jsonLen));
        var root = doc.RootElement;
        var views = root.GetProperty("bufferViews");
        var accessors = root.GetProperty("accessors");

        (int Offset, int Stride, int Count, int Type, int Size) Layout(int accessor, int comps)
        {
            var acc = accessors[accessor];
            if (acc.TryGetProperty("sparse", out _)) throw new InvalidDataException($"{path}: accessor {accessor}: sparse не поддерживается");
            if (!acc.TryGetProperty("bufferView", out var bv)) throw new InvalidDataException($"{path}: accessor {accessor} без bufferView");
            var view = views[bv.GetInt32()];
            if (view.TryGetProperty("buffer", out var buf) && buf.GetInt32() != 0) throw new InvalidDataException($"{path}: внешние буферы не поддерживаются");
            int type = acc.GetProperty("componentType").GetInt32();
            int size = type switch
            {
                TypeByte => 1,
                TypeShort => 2,
                TypeInt or TypeFloat => 4,
                _ => throw new InvalidDataException($"{path}: accessor {accessor}: тип компонента {type} не поддерживается"),
            };
            int offset = (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                         + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size * comps;
            int count = acc.GetProperty("count").GetInt32();
            if (count > 0 && offset + (long)(count - 1) * stride + size * comps > bin.Length)
                throw new InvalidDataException($"{path}: accessor {accessor} выходит за буфер");
            return (offset, stride, count, type, size);
        }

        float[] Floats(int accessor, int comps)
        {
            var (offset, stride, count, type, size) = Layout(accessor, comps);
            var span = bin.Span;
            var r = new float[count * comps];
            if (type == TypeFloat && stride == size * comps)
            {
                MemoryMarshal.Cast<byte, float>(span.Slice(offset, count * comps * size)).CopyTo(r);
                return r;
            }
            for (int e = 0; e < count; e++)
                for (int c = 0; c < comps; c++)
                {
                    var at = span[(offset + e * stride + c * size)..];
                    r[e * comps + c] = type switch
                    {
                        TypeFloat => BitConverter.ToSingle(at),
                        // нормализованные целые (веса скина у экспортёра UE)
                        TypeByte => at[0] / 255f,
                        TypeShort => BitConverter.ToUInt16(at) / 65535f,
                        _ => throw new InvalidDataException($"{path}: accessor {accessor}: ожидались числа с плавающей точкой"),
                    };
                }
            return r;
        }

        int[] Ints(int accessor, int comps)
        {
            var (offset, stride, count, type, size) = Layout(accessor, comps);
            var span = bin.Span;
            var r = new int[count * comps];
            for (int e = 0; e < count; e++)
                for (int c = 0; c < comps; c++)
                {
                    var at = span[(offset + e * stride + c * size)..];
                    r[e * comps + c] = type switch
                    {
                        TypeByte => at[0],
                        TypeShort => BitConverter.ToUInt16(at),
                        TypeInt => checked((int)BitConverter.ToUInt32(at)),
                        _ => throw new InvalidDataException($"{path}: accessor {accessor}: ожидались целые"),
                    };
                }
            return r;
        }

        var mesh = new GltfMesh();
        var prims = root.GetProperty("meshes")[0].GetProperty("primitives");
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var uv = new List<Vector2>();
        var idx = new List<int>();
        var joints = new List<int>();
        var weights = new List<float>();
        var vertexMaterial = new List<int>();
        Span<(float W, int J)> top = stackalloc (float, int)[4];
        foreach (var prim in prims.EnumerateArray())
        {
            if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;
            int baseVertex = pos.Count;
            var attr = prim.GetProperty("attributes");
            var p = Floats(attr.GetProperty("POSITION").GetInt32(), 3);
            int n = p.Length / 3;
            for (int i = 0; i < n; i++) pos.Add(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]));
            int material = prim.TryGetProperty("material", out var ma) ? ma.GetInt32() : -1;
            for (int i = 0; i < n; i++) vertexMaterial.Add(material);
            if (attr.TryGetProperty("NORMAL", out var na))
            {
                var q = Floats(na.GetInt32(), 3);
                for (int i = 0; i < n; i++) nrm.Add(new Vector3(q[i * 3], q[i * 3 + 1], q[i * 3 + 2]));
            }
            else for (int i = 0; i < n; i++) nrm.Add(Vector3.UnitY);
            if (attr.TryGetProperty("TEXCOORD_0", out var ta))
            {
                var q = Floats(ta.GetInt32(), 2);
                for (int i = 0; i < n; i++) uv.Add(new Vector2(q[i * 2], q[i * 2 + 1]));
            }
            else for (int i = 0; i < n; i++) uv.Add(Vector2.Zero);

            // все наборы влияний → 4 сильнейших
            var sets = new List<(int[] J, float[] W)>();
            for (int s = 0; attr.TryGetProperty($"JOINTS_{s}", out var ja) && attr.TryGetProperty($"WEIGHTS_{s}", out var wa); s++)
                sets.Add((Ints(ja.GetInt32(), 4), Floats(wa.GetInt32(), 4)));
            for (int i = 0; i < n; i++)
            {
                top.Clear();
                foreach (var (J, W) in sets)
                    for (int k = 0; k < 4; k++)
                    {
                        float w = W[i * 4 + k];
                        if (w <= 0) continue;
                        int slot = 0;
                        for (int t = 1; t < 4; t++) if (top[t].W < top[slot].W) slot = t;
                        if (w > top[slot].W) top[slot] = (w, J[i * 4 + k]);
                    }
                float sum = top[0].W + top[1].W + top[2].W + top[3].W;
                for (int t = 0; t < 4; t++)
                {
                    joints.Add(top[t].J);
                    weights.Add(sum > 0 ? top[t].W / sum : t == 0 ? 1 : 0);
                }
            }

            if (prim.TryGetProperty("indices", out var ia))
                foreach (int k in Ints(ia.GetInt32(), 1)) idx.Add(baseVertex + k);
            else
                for (int k = 0; k < n; k++) idx.Add(baseVertex + k);
        }
        mesh.Positions = [.. pos];
        mesh.Normals = [.. nrm];
        mesh.Uvs = [.. uv];
        mesh.Indices = [.. idx];
        mesh.Joints = [.. joints];
        mesh.Weights = [.. weights];
        mesh.VertexMaterial = [.. vertexMaterial];
        if (root.TryGetProperty("materials", out var materials))
            mesh.MaterialNames = materials.EnumerateArray().Select(m => m.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "").ToArray();

        if (root.TryGetProperty("nodes", out var nodes))
        {
            int meshNode = -1;
            for (int i = 0; i < nodes.GetArrayLength() && meshNode < 0; i++)
                if (nodes[i].TryGetProperty("mesh", out var mi) && mi.GetInt32() == 0) meshNode = i;
            if (meshNode >= 0 && !nodes[meshNode].TryGetProperty("skin", out _))
            {
                var world = NodeWorld(nodes, meshNode);
                if (!world.IsIdentity)
                {
                    if (!Matrix4x4.Invert(world, out var worldInv)) throw new InvalidDataException($"{path}: вырожденный трансформ узла меша");
                    var normalMatrix = Matrix4x4.Transpose(worldInv);
                    for (int v = 0; v < mesh.Positions.Length; v++)
                    {
                        mesh.Positions[v] = Vector3.Transform(mesh.Positions[v], world);
                        mesh.Normals[v] = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[v], normalMatrix));
                    }
                }
            }
        }

        if (root.TryGetProperty("skins", out var skins) && skins.GetArrayLength() > 0)
        {
            if (nodes.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{path}: скин без узлов (nodes)");
            var skin = skins[0];
            var jointNodes = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            mesh.JointNames = jointNodes.Select(j => nodes[j].TryGetProperty("name", out var nm) ? nm.GetString() ?? $"joint{j}" : $"joint{j}").ToArray();
            var ib = Floats(skin.GetProperty("inverseBindMatrices").GetInt32(), 16);
            mesh.InverseBind = new Matrix4x4[jointNodes.Length];
            for (int j = 0; j < jointNodes.Length; j++) mesh.InverseBind[j] = FromColumns(ib.AsSpan(j * 16, 16));
        }
        return mesh;
    }

    /// <summary>Матрица glTF (16 чисел по столбцам): чтение подряд в строки System.Numerics — транспонирование, нужное для вектора слева.</summary>
    private static Matrix4x4 FromColumns(ReadOnlySpan<float> m) =>
        new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>Мировой трансформ узла: локальные трансформы от узла к корню сцены (вектор слева: v·Local·Parent·…).</summary>
    private static Matrix4x4 NodeWorld(JsonElement nodes, int node)
    {
        int count = nodes.GetArrayLength();
        var parent = new int[count];
        Array.Fill(parent, -1);
        for (int i = 0; i < count; i++)
            if (nodes[i].TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray()) parent[c.GetInt32()] = i;
        var world = Matrix4x4.Identity;
        for (int i = node, depth = 0; i >= 0; i = parent[i])
        {
            if (++depth > count) throw new InvalidDataException("цикл в иерархии узлов glTF");
            world *= NodeLocal(nodes[i]);
        }
        return world;
    }

    private static Matrix4x4 NodeLocal(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
            return FromColumns(matrix.EnumerateArray().Select(e => e.GetSingle()).ToArray());
        static Vector3 Vec(JsonElement n, string name, Vector3 fallback) =>
            n.TryGetProperty(name, out var a) ? new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle()) : fallback;
        var q = node.TryGetProperty("rotation", out var r)
            ? Quaternion.Normalize(new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle()))
            : Quaternion.Identity;
        return Matrix4x4.CreateScale(Vec(node, "scale", Vector3.One)) * Matrix4x4.CreateFromQuaternion(q)
               * Matrix4x4.CreateTranslation(Vec(node, "translation", Vector3.Zero));
    }
}
