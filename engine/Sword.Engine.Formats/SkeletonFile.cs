using System.Text;

namespace Sword.Engine.Formats;

/// <summary>
/// Скелет библиотеки анимаций (.swskeleton): имена и родители суставов. Клипы ссылаются на скелет по id и <see cref="Hash"/> —
/// клип чужого скелета не загрузится молча. Формат: «SWSN», версия 1, id, суставы (имя, родитель), «NSWS».
/// </summary>
public sealed class SkeletonFile
{
    public static ReadOnlySpan<byte> Magic => "SWSN"u8;
    private static ReadOnlySpan<byte> EndMagic => "NSWS"u8;
    public const ushort Version = 1;
    public const int MaxJoints = 1024, MaxStringBytes = 4096;

    public string Id = "";
    public string[] JointNames = [];
    public int[] JointParents = [];

    /// <summary>FNV-1a по именам и родителям — по нему клип узнаёт свой скелет.</summary>
    public uint Hash => ComputeHash(JointNames, JointParents);

    public static uint ComputeHash(string[] names, int[] parents)
    {
        uint h = 2166136261;
        void Mix(int b) { h ^= (uint)b; h *= 16777619; }
        for (int j = 0; j < names.Length; j++)
        {
            foreach (char ch in names[j]) { Mix(ch & 0xFF); Mix(ch >> 8); }
            Mix(0);
            int p = j < parents.Length ? parents[j] : -1;
            Mix(p & 0xFF); Mix(p >> 8 & 0xFF);
        }
        return h;
    }

    /// <summary>Записать в поток. Поток остаётся открытым.</summary>
    public void Write(Stream stream)
    {
        if (JointParents.Length != JointNames.Length)
            throw new InvalidOperationException($"суставов {JointNames.Length}, родителей {JointParents.Length}");
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Id);
        w.Write(JointNames.Length);
        for (int j = 0; j < JointNames.Length; j++)
        {
            w.Write(JointNames[j]);
            w.Write(JointParents[j]);
        }
        w.Write(EndMagic);
    }

    /// <summary>Прочитать и проверить скелет. <paramref name="name"/> — имя для сообщений (путь в контенте).</summary>
    /// <exception cref="InvalidDataException">Не .swskeleton, другая версия, обрезан или противоречив.</exception>
    public static SkeletonFile Read(Stream stream, string name)
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

    private static SkeletonFile ReadChecked(Stream stream, string name)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException($"{name}: не .swskeleton");
        ushort version = r.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"{name}: версия {version}, ожидается {Version}");
        var s = new SkeletonFile { Id = FormatStrings.Read(r, name, MaxStringBytes) };
        int jc = r.ReadInt32();
        if (jc is < 1 or > MaxJoints) throw new InvalidDataException($"{name}: суставов {jc}");
        s.JointNames = new string[jc];
        s.JointParents = new int[jc];
        for (int j = 0; j < jc; j++)
        {
            s.JointNames[j] = FormatStrings.Read(r, name, MaxStringBytes);
            s.JointParents[j] = r.ReadInt32();
            if (s.JointParents[j] < -1 || s.JointParents[j] >= jc || s.JointParents[j] == j)
                throw new InvalidDataException($"{name}: родитель сустава {j}");
        }
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(EndMagic)) throw new InvalidDataException($"{name}: файл обрезан");
        return s;
    }
}
