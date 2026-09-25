namespace Sword.Engine.Formats;

/// <summary>Раскладка текселей запечённой текстуры. Номера — в файле, менять только с версией формата.</summary>
public enum TexturePixelFormat : uint
{
    R8 = 1,
    Rg8 = 2,
    Rgba8 = 3,
    Rgba8Srgb = 4,
    /// <summary>Один канал (маски, высоты), блок 4×4 — 8 Б.</summary>
    Bc4 = 10,
    /// <summary>Два канала (нормали xy), блок 4×4 — 16 Б.</summary>
    Bc5 = 11,
    Bc7 = 12,
    Bc7Srgb = 13,
    /// <summary>Цвет без альфы (альбедо), блок 4×4 — 8 Б: две опоры 5:6:5 и четыре градации между ними.</summary>
    Bc1 = 14,
    Bc1Srgb = 15,
}

/// <summary>Слой массива: имя (обычно имя исходника) и соотношение ширины к высоте до приведения к размеру массива.</summary>
public readonly record struct TextureLayer(string Name, float Aspect);

/// <summary>
/// Текстура или массив текстур одного размера — <c>*.swtex</c>. Порядок байтов — little-endian.
/// <code>
/// "SWTEX" 0 0 версия           8 Б
/// формат, ширина, высота       u32 (TexturePixelFormat)
/// мипов, слоёв                 u32
/// слои                         имя (строка BinaryWriter), соотношение ширины к высоте исходника float
/// подресурсы                   слой за слоем, в слое — мип за мипом: длина u32, байты
/// "XETW"
/// </code>
/// Байты мипа — ряды без выравнивания: блоков по ширине × байт на блок, ряд за рядом.
/// Слои массива приведены к одному размеру; <see cref="Layers"/> хранит имя и исходное соотношение сторон (вытянутые брызги 2:1).
///
/// Формат байт в байт — .swtex SWORD 2eab13b (его читает эталонный пак). Чтение — точным числом байт; обрезанный или
/// противоречивый файл — <see cref="InvalidDataException"/> с именем файла.
/// </summary>
public sealed class TextureData
{
    public const byte Version = 1;
    public const string Extension = ".swtex";

    public static ReadOnlySpan<byte> Magic => [(byte)'S', (byte)'W', (byte)'T', (byte)'E', (byte)'X', 0, 0, Version];
    public static ReadOnlySpan<byte> EndMagic => "XETW"u8;

    /// <summary>Пределы, за которыми файл считается испорченным, а не большим: имя слоя, байт в подресурсе (мип 16384² RGBA8).</summary>
    public const int MaxLayerName = 4096, MaxSubresourceBytes = 16384 * 16384 * 4;

    public required TexturePixelFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Mips { get; init; }
    public required int Slices { get; init; }

    /// <summary>Подресурсы: <c>[слой * Mips + мип]</c>.</summary>
    public required byte[][] Subresources { get; init; }

    /// <summary>Сведения о слоях; пусто — слои без имён, соотношение 1.</summary>
    public TextureLayer[] Layers { get; init; } = [];

    /// <summary>Номер слоя по имени (без учёта регистра); нет — −1.</summary>
    public int FindLayer(string name) => Array.FindIndex(Layers, l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    public byte[] Subresource(int slice, int mip) => Subresources[slice * Mips + mip];

    public static int MipSize(int size, int mip) => Math.Max(1, size >> mip);

    /// <summary>Байт на блок и сторона блока в текселях.</summary>
    public static (int Bytes, int Block) BlockInfo(TexturePixelFormat f) => f switch
    {
        TexturePixelFormat.R8 => (1, 1),
        TexturePixelFormat.Rg8 => (2, 1),
        TexturePixelFormat.Rgba8 or TexturePixelFormat.Rgba8Srgb => (4, 1),
        TexturePixelFormat.Bc4 or TexturePixelFormat.Bc1 or TexturePixelFormat.Bc1Srgb => (8, 4),
        TexturePixelFormat.Bc5 or TexturePixelFormat.Bc7 or TexturePixelFormat.Bc7Srgb => (16, 4),
        _ => throw new InvalidDataException($"неизвестный формат текстуры {(uint)f}"),
    };

    public static int BlocksAcross(TexturePixelFormat f, int texels)
    {
        int block = BlockInfo(f).Block;
        return (texels + block - 1) / block;
    }

    /// <summary>Байт в одном ряду блоков мипа.</summary>
    public int RowBytes(int mip) => BlocksAcross(Format, MipSize(Width, mip)) * BlockInfo(Format).Bytes;

    public int RowCount(int mip) => BlocksAcross(Format, MipSize(Height, mip));

    public int MipBytes(int mip) => RowBytes(mip) * RowCount(mip);

    /// <summary>Сверяет размеры и длины подресурсов; ошибка — с именем <paramref name="path"/>.</summary>
    public void Validate(string path)
    {
        BlockInfo(Format);
        if (Width <= 0 || Height <= 0 || Width > 16384 || Height > 16384)
            throw new InvalidDataException($"{path}: размер {Width}×{Height}");
        int maxMips = 1 + (int)Math.Log2(Math.Max(Width, Height));
        if (Mips <= 0 || Mips > maxMips) throw new InvalidDataException($"{path}: мипов {Mips} при размере {Width}×{Height}");
        if (Slices <= 0 || Slices > 2048) throw new InvalidDataException($"{path}: слоёв {Slices}");
        if (Layers.Length != 0 && Layers.Length != Slices)
            throw new InvalidDataException($"{path}: сведений о слоях {Layers.Length} при {Slices} слоях");
        if (Subresources.Length != Mips * Slices)
            throw new InvalidDataException($"{path}: подресурсов {Subresources.Length}, ожидается {Mips * Slices}");
        for (int s = 0; s < Slices; s++)
            for (int m = 0; m < Mips; m++)
                if (Subresource(s, m).Length != MipBytes(m))
                    throw new InvalidDataException($"{path}: слой {s} мип {m} — {Subresource(s, m).Length} Б, ожидается {MipBytes(m)}");
    }

    public void Write(Stream stream)
    {
        Validate("запись текстуры");
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write((uint)Format);
        w.Write((uint)Width);
        w.Write((uint)Height);
        w.Write((uint)Mips);
        w.Write((uint)Slices);
        for (int s = 0; s < Slices; s++)
        {
            w.Write(s < Layers.Length ? Layers[s].Name : "");
            w.Write(s < Layers.Length ? Layers[s].Aspect : 1f);
        }
        foreach (var sub in Subresources)
        {
            w.Write((uint)sub.Length);
            w.Write(sub);
        }
        w.Write(EndMagic);
    }

    public static TextureData Read(Stream stream, string path)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            Span<byte> magic = stackalloc byte[Magic.Length];
            stream.ReadExactly(magic);
            if (!magic[..7].SequenceEqual(Magic[..7]))
                throw new InvalidDataException($"{path}: не текстура {Extension}");
            if (magic[^1] != Version)
                throw new InvalidDataException($"{path}: версия {magic[^1]}, ожидается {Version} — перепеките текстуры");

            var format = (TexturePixelFormat)r.ReadUInt32();
            int width = (int)r.ReadUInt32(), height = (int)r.ReadUInt32();
            int mips = (int)r.ReadUInt32(), slices = (int)r.ReadUInt32();
            if (mips <= 0 || slices <= 0 || (long)mips * slices > 65536)
                throw new InvalidDataException($"{path}: мипов {mips}, слоёв {slices}");
            var layers = new TextureLayer[slices];
            for (int s = 0; s < slices; s++) layers[s] = new TextureLayer(FormatStrings.Read(r, path, MaxLayerName), r.ReadSingle());
            var subs = new byte[mips * slices][];
            for (int i = 0; i < subs.Length; i++)
            {
                uint length = r.ReadUInt32();
                if (stream.CanSeek && length > stream.Length - stream.Position)
                    throw new InvalidDataException($"{path}: подресурс {i} длиной {length} Б больше остатка файла");
                if (length > MaxSubresourceBytes) throw new InvalidDataException($"{path}: подресурс {i} длиной {length} Б");
                subs[i] = new byte[length];
                stream.ReadExactly(subs[i]);
            }
            Span<byte> end = stackalloc byte[EndMagic.Length];
            stream.ReadExactly(end);
            if (!end.SequenceEqual(EndMagic))
                throw new InvalidDataException($"{path}: нет конца файла текстуры");

            var data = new TextureData { Format = format, Width = width, Height = height, Mips = mips, Slices = slices, Subresources = subs, Layers = layers };
            data.Validate(path);
            return data;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{path}: файл текстуры обрезан");
        }
    }
}
