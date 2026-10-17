using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sword.Engine.Formats;

/// <summary>Вершина скинированного меша: 48 байт, раскладка совпадает с HLSL-структурой SkinVertex.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SkinVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    /// <summary>Индексы суставов (4 байта) — упакованы в uint: j0 | j1 &lt;&lt; 8 | j2 &lt;&lt; 16 | j3 &lt;&lt; 24.</summary>
    public uint Joints;
    public Vector3 Weights;   // четвёртый вес = 1 − сумма
    public const int Size = 48;
}

/// <summary>Часть индексов меша со своим материалом; по имени материала игра выбирает цвет и текстуры.</summary>
public readonly record struct MeshSection(int Start, int Count, string Material);

/// <summary>
/// Меш персонажа или оружия (.swmesh): вершины в пространстве персонажа (+Z вперёд, +X влево, Y вверх, метры), суставы —
/// имена, родители, обратные bind-матрицы (вектор слева). Статический меш — ноль суставов. Формат читает и эталонный пак:
/// «SWMS», версия, счётчики, вершины, индексы, суставы, секции (с версии 2), индексы меша теней (с версии 3), «SMWS».
/// </summary>
public sealed class SkinnedMeshData
{
    public static ReadOnlySpan<byte> Magic => "SWMS"u8;
    private static ReadOnlySpan<byte> EndMagic => "SMWS"u8;

    /// <summary>3 — с индексами меша теней; 2 (без них) и 1 (без секций) читаются.</summary>
    public const ushort Version = 3;

    /// <summary>Пределы, за которыми файл считается испорченным, а не большим.</summary>
    public const int MaxVertices = 4_000_000, MaxIndices = 12_000_000, MaxJoints = 1024, MaxSections = 1024;

    public SkinVertex[] Vertices = [];
    /// <summary>Секции по материалам подряд по индексам; пусто — весь меш одним материалом.</summary>
    public MeshSection[] Sections = [];
    public uint[] Indices = [];
    /// <summary>Меш теней — прореженный по тем же вершинам (треугольники, индексы <see cref="Vertices"/>); пусто — нет.</summary>
    public uint[] ShadowIndices = [];
    public string[] JointNames = [];
    public int[] JointParents = [];
    public Matrix4x4[] InverseBind = [];
    public int JointCount => JointNames.Length;

    /// <summary>
    /// Записать в поток: с мешем теней — версия 3, без него — 2 (байты прежних мешей не меняются). Поток остаётся открытым.
    /// </summary>
    public void Write(Stream stream)
    {
        if (JointParents.Length != JointCount || InverseBind.Length != JointCount)
            throw new InvalidOperationException($"суставов {JointCount}, родителей {JointParents.Length}, матриц {InverseBind.Length}");
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ushort version = ShadowIndices.Length > 0 ? Version : (ushort)2;
        w.Write(Magic);
        w.Write(version);
        w.Write(Vertices.Length);
        w.Write(Indices.Length);
        w.Write(JointCount);
        w.Write(MemoryMarshal.AsBytes(Vertices.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(Indices.AsSpan()));
        for (int j = 0; j < JointCount; j++)
        {
            w.Write(JointNames[j]);
            w.Write(JointParents[j]);
            w.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref InverseBind[j], 1)));
        }
        w.Write(Sections.Length);
        foreach (var s in Sections)
        {
            w.Write(s.Start);
            w.Write(s.Count);
            w.Write(s.Material);
        }
        if (version >= 3)
        {
            w.Write(ShadowIndices.Length);
            w.Write(MemoryMarshal.AsBytes(ShadowIndices.AsSpan()));
        }
        w.Write(EndMagic);
    }

    /// <summary>Прочитать и проверить меш из потока. <paramref name="name"/> — имя для сообщений (путь в контенте).</summary>
    /// <exception cref="InvalidDataException">Не .swmesh, другая версия, обрезан или противоречив.</exception>
    public static SkinnedMeshData Read(Stream stream, string name)
    {
        try
        {
            return ReadChecked(stream, name);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{name}: файл обрезан");
        }
    }

    private static SkinnedMeshData ReadChecked(Stream stream, string name)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException($"{name}: не .swmesh");
        ushort version = r.ReadUInt16();
        if (version is < 1 or > Version) throw new InvalidDataException($"{name}: версия {version}, ожидается 1…{Version}");
        int vc = Checked(r.ReadInt32(), 3, MaxVertices, "вершин", name);
        int ic = Checked(r.ReadInt32(), 3, MaxIndices, "индексов", name);
        int jc = Checked(r.ReadInt32(), 0, MaxJoints, "суставов", name);
        if (ic % 3 != 0) throw new InvalidDataException($"{name}: индексов {ic} — не кратно 3");

        var m = new SkinnedMeshData
        {
            Vertices = new SkinVertex[vc], Indices = new uint[ic],
            JointNames = new string[jc], JointParents = new int[jc], InverseBind = new Matrix4x4[jc],
        };
        // BinaryReader не читает вперёд: сырые массивы берутся прямо из потока, ровно своей длины.
        stream.ReadExactly(MemoryMarshal.AsBytes(m.Vertices.AsSpan()));
        stream.ReadExactly(MemoryMarshal.AsBytes(m.Indices.AsSpan()));
        for (int j = 0; j < jc; j++)
        {
            m.JointNames[j] = r.ReadString();
            m.JointParents[j] = r.ReadInt32();
            stream.ReadExactly(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref m.InverseBind[j], 1)));
            if (m.JointParents[j] < -1 || m.JointParents[j] >= jc)
                throw new InvalidDataException($"{name}: сустав {j}: родитель {m.JointParents[j]}");
        }
        if (version >= 2)
        {
            int sc = Checked(r.ReadInt32(), 0, MaxSections, "секций", name);
            m.Sections = new MeshSection[sc];
            for (int s = 0; s < sc; s++)
            {
                var sec = new MeshSection(r.ReadInt32(), r.ReadInt32(), r.ReadString());
                if (sec.Start < 0 || sec.Count < 0 || sec.Start % 3 != 0 || sec.Count % 3 != 0 || (long)sec.Start + sec.Count > ic)
                    throw new InvalidDataException($"{name}: секция {s} ({sec.Start}+{sec.Count}) вне индексов");
                m.Sections[s] = sec;
            }
        }
        if (version >= 3)
        {
            int sic = Checked(r.ReadInt32(), 0, MaxIndices, "индексов меша теней", name);
            if (sic % 3 != 0) throw new InvalidDataException($"{name}: индексов меша теней {sic} — не кратно 3");
            m.ShadowIndices = new uint[sic];
            stream.ReadExactly(MemoryMarshal.AsBytes(m.ShadowIndices.AsSpan()));
        }
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(EndMagic)) throw new InvalidDataException($"{name}: нет конца файла «SMWS» — файл испорчен");

        foreach (uint i in m.Indices)
            if (i >= vc) throw new InvalidDataException($"{name}: индекс {i} за пределами {vc} вершин");
        foreach (uint i in m.ShadowIndices)
            if (i >= vc) throw new InvalidDataException($"{name}: индекс меша теней {i} за пределами {vc} вершин");
        if (jc > 0)
            foreach (var v in m.Vertices)
                if ((v.Joints & 0xFF) >= jc || (v.Joints >> 8 & 0xFF) >= jc || (v.Joints >> 16 & 0xFF) >= jc || (v.Joints >> 24) >= jc)
                    throw new InvalidDataException($"{name}: сустав вершины вне скелета из {jc}");
        return m;
    }

    private static int Checked(int value, int min, int max, string what, string name) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"{name}: {what} {value}, допустимо {min}…{max}");
}
