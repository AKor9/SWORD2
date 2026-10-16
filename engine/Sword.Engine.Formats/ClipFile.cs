using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Sword.Engine.Formats;

/// <summary>
/// Клип библиотеки анимаций (.swclip, в паке — <c>anim/&lt;источник&gt;/&lt;имя&gt;.swclip</c>): локальные позы суставов
/// по кадрам и корневое движение в пространстве персонажа (+Z вперёд, +X влево, Y вверх, метры). Формат: «SWCL», версия,
/// заголовок; на кадр·сустав — поворот и смещение; смещение корня; с версии 2 — поворот корня; «LCWS». Версия 1 читается
/// с нулевым поворотом корня.
/// </summary>
public sealed class ClipFile
{
    public static ReadOnlySpan<byte> Magic => "SWCL"u8;
    private static ReadOnlySpan<byte> EndMagic => "LCWS"u8;
    /// <summary>2 — поворот корневого движения.</summary>
    public const ushort Version = 2;
    public const int MaxFrames = 1_000_000, MaxJoints = 1024, MaxStringBytes = 4096;

    /// <summary>Версия прочитанного файла.</summary>
    public ushort FileVersion = Version;
    public string Skeleton = "";
    public uint SkeletonHash;
    public float SampleRate;
    public int Frames, Joints;
    public bool Loop;
    /// <summary>Путь исходного ассета (например, путь UE) — для сведений.</summary>
    public string SourcePath = "";
    /// <summary>Хеш исходных данных и версии конвертера (пекарня: совпал — перезаписывать не нужно).</summary>
    public string SourceHash = "";
    /// <summary>[кадр·суставы].</summary>
    public Quaternion[] LocalRot = [];
    public Vector3[] LocalPos = [];
    /// <summary>[кадр] смещение персонажа от первого кадра (Y = 0).</summary>
    public Vector3[] RootMotion = [];
    /// <summary>[кадр] поворот персонажа вокруг вертикали от первого кадра, рад.</summary>
    public float[] RootYaw = [];

    /// <summary>Сустав кадра в файле: поворот и смещение подряд, 28 байт.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct JointKey
    {
        public Quaternion Rot;
        public Vector3 Pos;
    }

    /// <summary>Записать в поток (текущая версия). Поток остаётся открытым.</summary>
    public void Write(Stream stream)
    {
        int n = Frames * Joints;
        if (LocalRot.Length != n || LocalPos.Length != n || RootMotion.Length != Frames)
            throw new InvalidOperationException($"кадров {Frames}, суставов {Joints}: поз {LocalRot.Length}/{LocalPos.Length}, " +
                $"корня {RootMotion.Length}");
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(Skeleton);
        w.Write(SkeletonHash);
        w.Write(SampleRate);
        w.Write(Frames);
        w.Write(Joints);
        w.Write(Loop);
        w.Write(SourcePath);
        w.Write(SourceHash);
        var keys = new JointKey[n];
        for (int i = 0; i < n; i++) keys[i] = new JointKey { Rot = LocalRot[i], Pos = LocalPos[i] };
        w.Write(MemoryMarshal.AsBytes(keys.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(RootMotion.AsSpan()));
        for (int f = 0; f < Frames; f++) w.Write(f < RootYaw.Length ? RootYaw[f] : 0f);
        w.Write(EndMagic);
    }

    /// <summary>Прочитать и проверить клип. <paramref name="name"/> — имя для сообщений (путь в контенте).</summary>
    /// <exception cref="InvalidDataException">Не .swclip, другая версия, обрезан или противоречив.</exception>
    public static ClipFile Read(Stream stream, string name)
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

    /// <summary>Только заголовок клипа, без поз (списки клипов в предпросмотре пекарни); массивы остаются пустыми.</summary>
    /// <exception cref="InvalidDataException">Не .swclip, другая версия, обрезан или противоречив.</exception>
    public static ClipFile ReadHeader(Stream stream, string name)
    {
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            return ReadHeaderFields(stream, r, name);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{name}: файл обрезан");
        }
    }

    private static ClipFile ReadHeaderFields(Stream stream, BinaryReader r, string name)
    {
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic)) throw new InvalidDataException($"{name}: не .swclip");
        ushort version = r.ReadUInt16();
        if (version is not (1 or Version)) throw new InvalidDataException($"{name}: версия {version}, ожидается 1…{Version}");
        var c = new ClipFile
        {
            FileVersion = version,
            Skeleton = FormatStrings.Read(r, name, MaxStringBytes), SkeletonHash = r.ReadUInt32(), SampleRate = r.ReadSingle(),
            Frames = r.ReadInt32(), Joints = r.ReadInt32(), Loop = r.ReadBoolean(),
            SourcePath = FormatStrings.Read(r, name, MaxStringBytes), SourceHash = FormatStrings.Read(r, name, MaxStringBytes),
        };
        if (!(c.SampleRate is > 1 and < 1000)) throw new InvalidDataException($"{name}: частота {c.SampleRate}");
        if (c.Frames is < 1 or > MaxFrames || c.Joints is < 1 or > MaxJoints)
            throw new InvalidDataException($"{name}: кадров {c.Frames}, суставов {c.Joints}");
        return c;
    }

    private static ClipFile ReadChecked(Stream stream, string name)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var c = ReadHeaderFields(stream, r, name);
        ushort version = c.FileVersion;
        Span<byte> magic = stackalloc byte[4];
        int n = c.Frames * c.Joints;
        // Испорченный счётчик не должен выделять гигабайты: данных в потоке должно хватать.
        long need = (long)n * Marshal.SizeOf<JointKey>() + (long)c.Frames * (12 + (version >= 2 ? 4 : 0)) + 4;
        if (stream.CanSeek && stream.Length - stream.Position < need) throw new InvalidDataException($"{name}: файл обрезан");

        var keys = new JointKey[n];
        stream.ReadExactly(MemoryMarshal.AsBytes(keys.AsSpan()));
        c.LocalRot = new Quaternion[n];
        c.LocalPos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            c.LocalRot[i] = keys[i].Rot;
            c.LocalPos[i] = keys[i].Pos;
        }
        c.RootMotion = new Vector3[c.Frames];
        stream.ReadExactly(MemoryMarshal.AsBytes(c.RootMotion.AsSpan()));
        c.RootYaw = new float[c.Frames];
        if (version >= 2) stream.ReadExactly(MemoryMarshal.AsBytes(c.RootYaw.AsSpan()));
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(EndMagic)) throw new InvalidDataException($"{name}: файл обрезан");
        return c;
    }
}
