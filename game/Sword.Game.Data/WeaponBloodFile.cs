using System.Numerics;

namespace Sword.Game.Data;

/// <summary>Развёртка зоны крови: как точка меша переводится в клетку сетки зоны. Номера — в файле.</summary>
public enum BloodZoneKind : byte
{
    /// <summary>
    /// Прямоугольник: u — вдоль <see cref="WeaponBloodZone.AxisU"/> от 0 до <see cref="WeaponBloodZone.Length"/>,
    /// v — вдоль <see cref="WeaponBloodZone.AxisV"/> от −<see cref="WeaponBloodZone.Half"/> до +. Проекция вдоль нормали: обе стороны
    /// плоского предмета — одна сетка. Лезвие, щека топора, наконечник, лицо щита, боёк.
    /// </summary>
    Plane = 0,
    /// <summary>
    /// Цилиндр: u — вдоль оси от 0 до <see cref="WeaponBloodZone.Length"/>, v — угол вокруг оси от <see cref="WeaponBloodZone.AxisV"/>
    /// (−π…π). <see cref="WeaponBloodZone.Half"/> — радиус. Рукоять, древко, посох.
    /// </summary>
    Cylinder = 1,
    /// <summary>
    /// Сфера вокруг <see cref="WeaponBloodZone.Origin"/>: u — от полюса +<see cref="WeaponBloodZone.AxisU"/> (0…π), v — долгота от
    /// <see cref="WeaponBloodZone.AxisV"/>. <see cref="WeaponBloodZone.Half"/> — радиус. Головка булавы, навершие.
    /// </summary>
    Sphere = 2,
}

/// <summary>
/// Зона крови оружия в пространстве его меша (как вершины .swmesh). <see cref="PrevZone"/>/<see cref="NextZone"/> — что с кровью на краях
/// u = 0 и u = Length: капает или переходит в соседнюю зону (переходы — позже, формат готов).
/// </summary>
public sealed record WeaponBloodZone
{
    public required string Name { get; init; }
    public required BloodZoneKind Kind { get; init; }
    public required Vector3 Origin { get; init; }
    /// <summary>Ось длины (единичная).</summary>
    public required Vector3 AxisU { get; init; }
    /// <summary>Ось ширины или начало угла (единичная, перпендикулярна <see cref="AxisU"/>).</summary>
    public required Vector3 AxisV { get; init; }
    public required float Length { get; init; }
    /// <summary>Полуширина плоскости или радиус цилиндра и сферы.</summary>
    public required float Half { get; init; }
    /// <summary>Клеток сетки по u и по v.</summary>
    public required int CellsU { get; init; }
    public required int CellsV { get; init; }
    /// <summary>Соседние зоны на краях u = 0 и u = Length; −1 — край, кровь капает.</summary>
    public int PrevZone { get; init; } = -1;
    public int NextZone { get; init; } = -1;
}

/// <summary>
/// Зоны крови оружия — <c>data/weapons/&lt;имя&gt;.swblood</c> к мешу <c>meshes/weapons/&lt;имя&gt;.swmesh</c>: список зон и зона
/// каждой вершины (<see cref="NoZone"/> — крови не бывает). Кладёт конвертер weapon пекарни. Little-endian.
/// <code>
/// "SWBLD" 0 0 версия      8 Б
/// основание, острие       Vector3 × 2 (главная ось удара: от хвата к концу оружия)
/// зон u32; зона           имя (строка BinaryWriter), вид u8, origin, axisU, axisV Vector3, length, half float, клеток u, v u32, prev, next i32
/// вершин u32; зоны        u8 на вершину
/// "DLBW"
/// </code>
/// </summary>
public sealed class WeaponBloodData
{
    public const byte Version = 1;
    public const string Extension = ".swblood";
    public const byte NoZone = 255;
    /// <summary>Клеток на всё оружие — бюджет буфера на GPU.</summary>
    public const int MaxCells = 1024;

    public static ReadOnlySpan<byte> Magic => [(byte)'S', (byte)'W', (byte)'B', (byte)'L', (byte)'D', 0, 0, Version];
    public static ReadOnlySpan<byte> EndMagic => "DLBW"u8;

    /// <summary>Основание и конец главной оси (у клинка — начало лезвия и острие), пространство меша.</summary>
    public Vector3 Base { get; init; }
    public Vector3 Tip { get; init; }
    public required WeaponBloodZone[] Zones { get; init; }
    public required byte[] VertexZones { get; init; }

    public int CellCount => Zones.Sum(z => z.CellsU * z.CellsV);

    public void Validate(string path)
    {
        if (Zones.Length >= NoZone) throw new InvalidDataException($"{path}: зон {Zones.Length}");
        for (int i = 0; i < Zones.Length; i++)
        {
            var z = Zones[i];
            if (z.CellsU <= 0 || z.CellsV <= 0 || z.Length <= 0 || z.Half <= 0 || !float.IsFinite(z.Length + z.Half))
                throw new InvalidDataException($"{path}: зона {z.Name}: размеры {z.Length}×{z.Half}, клеток {z.CellsU}×{z.CellsV}");
            if (z.PrevZone >= Zones.Length || z.NextZone >= Zones.Length)
                throw new InvalidDataException($"{path}: зона {z.Name}: соседняя зона вне списка");
        }
        if (CellCount > MaxCells) throw new InvalidDataException($"{path}: клеток {CellCount}, больше {MaxCells}");
        foreach (byte b in VertexZones)
            if (b != NoZone && b >= Zones.Length) throw new InvalidDataException($"{path}: вершина в зоне {b}, зон {Zones.Length}");
    }

    public void Write(Stream stream)
    {
        Validate("запись зон крови");
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        WriteV(w, Base);
        WriteV(w, Tip);
        w.Write((uint)Zones.Length);
        foreach (var z in Zones)
        {
            w.Write(z.Name);
            w.Write((byte)z.Kind);
            WriteV(w, z.Origin);
            WriteV(w, z.AxisU);
            WriteV(w, z.AxisV);
            w.Write(z.Length);
            w.Write(z.Half);
            w.Write((uint)z.CellsU);
            w.Write((uint)z.CellsV);
            w.Write(z.PrevZone);
            w.Write(z.NextZone);
        }
        w.Write((uint)VertexZones.Length);
        w.Write(VertexZones);
        w.Write(EndMagic);
    }

    public static WeaponBloodData Read(Stream stream, string path)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            Span<byte> magic = stackalloc byte[Magic.Length];
            stream.ReadExactly(magic);
            if (!magic[..7].SequenceEqual(Magic[..7]))
                throw new InvalidDataException($"{path}: не зоны крови {Extension}");
            if (magic[^1] != Version)
                throw new InvalidDataException($"{path}: версия {magic[^1]}, ожидается {Version} — перепеките оружие");
            var baseP = ReadV(r);
            var tip = ReadV(r);
            int count = (int)r.ReadUInt32();
            if (count < 0 || count >= NoZone) throw new InvalidDataException($"{path}: зон {count}");
            var zones = new WeaponBloodZone[count];
            for (int i = 0; i < count; i++)
                zones[i] = new WeaponBloodZone
                {
                    Name = r.ReadString(), Kind = (BloodZoneKind)r.ReadByte(),
                    Origin = ReadV(r), AxisU = ReadV(r), AxisV = ReadV(r), Length = r.ReadSingle(), Half = r.ReadSingle(),
                    CellsU = (int)r.ReadUInt32(), CellsV = (int)r.ReadUInt32(), PrevZone = r.ReadInt32(), NextZone = r.ReadInt32(),
                };
            int vertices = (int)r.ReadUInt32();
            if (vertices < 0 || vertices > 1 << 24) throw new InvalidDataException($"{path}: вершин {vertices}");
            var vz = new byte[vertices];
            stream.ReadExactly(vz);
            Span<byte> end = stackalloc byte[4];
            stream.ReadExactly(end);
            if (!end.SequenceEqual(EndMagic)) throw new InvalidDataException($"{path}: нет конца файла");
            var data = new WeaponBloodData { Base = baseP, Tip = tip, Zones = zones, VertexZones = vz };
            data.Validate(path);
            return data;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{path}: файл обрезан");
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"{path}: испорчена строка ({e.Message})", e);
        }
    }

    private static void WriteV(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadV(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
