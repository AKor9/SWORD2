using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Sword.Engine.Formats;

/// <summary>
/// Файл одного шрифта интерфейса — <c>fonts/&lt;шрифт&gt;.swfont</c>: заранее подготовленные контуры для GPU-векторного
/// текста и исходный ttf для HarfBuzz. Порядок байтов — little-endian, строки — как у <c>BinaryWriter</c>.
/// <code>
/// "SWFONT" 0 версия         8 Б
/// имя                       строка
/// ролей u32, роли           строки: имена ролей интерфейса (UiFace), которые берёт этот шрифт
/// ascent, descent, lineGap  float, в долях кегля
/// глифов, кривых, полос, индексов   int32
/// глифы                     bounds 4f | горизонтальные полосы u32 | вертикальные полосы u32 | bandScale 2f | bandOffset 2f
/// кривые                    p1 p2 p3 по 2f, 2f нулей (32 Б — шаг StructuredBuffer)
/// полосы                    число кривых u32 | смещение в индексах u32
/// индексы                   u32 — номер кривой
/// ttf                       длина int32, байты
/// "TNFW"
/// </code>
/// Номера полос, индексов и кривых — от начала своего файла: интерфейс склеивает шрифты в общие таблицы и сдвигает их.
/// Версия 3; её же читает эталонный пак.
/// </summary>
public static class UiVectorFontFormat
{
    public const byte Version = 3;

    public static ReadOnlySpan<byte> Magic =>
        [(byte)'S', (byte)'W', (byte)'F', (byte)'O', (byte)'N', (byte)'T', 0, Version];

    public static ReadOnlySpan<byte> EndMagic => "TNFW"u8;

    /// <summary>Каталог шрифтов в контенте.</summary>
    public const string Dir = "fonts";
    public const string Extension = ".swfont";

    /// <summary>Полос на ось у glyph'а — единственное объявление: пекарь, шейдер (<c>UI_VECTOR_BANDS</c> контракта).</summary>
    public const int BandsPerAxis = 16;

    /// <summary>
    /// Пределы, за которыми файл считается испорченным, а не большим. Ролей — не больше числа ролей интерфейса
    /// (<c>UiFace.Count</c>, равенство проверяет тест Ui); полос — ровно столько, сколько у <see cref="MaxGlyphs"/> glyph'ов.
    /// </summary>
    public const int MaxRoles = 5, MaxGlyphs = 65536, MaxCurves = 2_000_000, MaxBands = MaxGlyphs * 2 * BandsPerAxis,
        MaxIndices = 20_000_000, MaxTtfBytes = 64 << 20, MaxStringBytes = 4096;

    private static readonly string[] Roles = ["Body", "Head", "Label", "NumLight", "Num"];

    /// <summary>
    /// Имена ролей интерфейса по номерам (<c>UiFace</c>, совпадение проверяет тест Ui). Здесь, а не в Ui: роли проверяет пекарь.
    /// </summary>
    public static IReadOnlyList<string> RoleNames => Roles;

    /// <summary>Номер роли по имени без учёта регистра; не роль (число, <c>Count</c>, список «A,B») — false.</summary>
    public static bool TryParseRole(string name, out int role)
    {
        role = Array.FindIndex(Roles, r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
        return role >= 0;
    }

    /// <summary>Роли через запятую — для сообщений об ошибке.</summary>
    public static string RoleList => string.Join(", ", Roles);
}

/// <summary>Glyph для шейдера: рамка в долях кегля, полосы по осям, перевод координат в номер полосы. 40 Б.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VectorGlyph
{
    public Vector4 Bounds;
    public uint HorizontalHeader;
    public uint VerticalHeader;
    public Vector2 BandScale;
    public Vector2 BandOffset;
}

/// <summary>Квадратичная кривая контура: p1, p2 в <see cref="P12"/>, p3 в <see cref="P3Pad"/>.xy. 32 Б.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VectorCurve
{
    public Vector4 P12;
    public Vector4 P3Pad;
}

/// <summary>Полоса glyph'а: сколько кривых и с какого места в индексах.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VectorBand
{
    public uint Count;
    public uint Offset;
}

/// <summary>Один проверенный файл шрифта; номера — от начала файла, чтение и запись — потоком.</summary>
public sealed class UiFontFile
{
    public required string Name { get; init; }
    /// <summary>Имя для сообщений (путь в контенте).</summary>
    public required string Path { get; init; }
    public required string[] Roles { get; init; }
    public required float Ascent { get; init; }
    public required float Descent { get; init; }
    public required float LineGap { get; init; }
    public required VectorGlyph[] Glyphs { get; init; }
    public required VectorCurve[] Curves { get; init; }
    public required VectorBand[] Bands { get; init; }
    public required uint[] Indices { get; init; }
    public required byte[] Ttf { get; init; }

    /// <param name="file">Поток; с поиском — проверяется, что после шрифта ничего нет.</param>
    /// <param name="path">Имя для сообщений.</param>
    /// <exception cref="InvalidDataException">Не шрифт, другая версия, обрезан, ссылки за границы таблиц.</exception>
    public static UiFontFile Read(Stream file, string path)
    {
        using var r = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        try
        {
            Span<byte> magic = stackalloc byte[UiVectorFontFormat.Magic.Length];
            r.BaseStream.ReadExactly(magic);
            if (!magic[..6].SequenceEqual(UiVectorFontFormat.Magic[..6]))
                throw new InvalidDataException($"{path}: не файл шрифта {UiVectorFontFormat.Extension}");
            if (magic[^1] != UiVectorFontFormat.Version)
                throw new InvalidDataException($"{path}: версия {magic[^1]}, ожидается {UiVectorFontFormat.Version} — перепеките шрифты");

            string name = ReadString(r, path);
            int roleCount = Count(r.ReadInt32(), 0, UiVectorFontFormat.MaxRoles, "ролей", path);
            var roles = new string[roleCount];
            for (int i = 0; i < roleCount; i++) roles[i] = ReadString(r, path);

            float ascent = r.ReadSingle(), descent = r.ReadSingle(), lineGap = r.ReadSingle();

            int glyphCount = Count(r.ReadInt32(), 1, UiVectorFontFormat.MaxGlyphs, "glyphs", path);
            int curveCount = Count(r.ReadInt32(), 0, UiVectorFontFormat.MaxCurves, "curves", path);
            int bandCount = Count(r.ReadInt32(), 1, UiVectorFontFormat.MaxBands, "bands", path);
            int indexCount = Count(r.ReadInt32(), 0, UiVectorFontFormat.MaxIndices, "indices", path);
            // Таблицы — до выделения памяти: испорченные числа в заголовке крошечного файла не заказывают сотни МБ.
            // Полосы — ровно по числу glyph'ов (полностью проверит Validate), таблицы — не длиннее остатка файла.
            if (bandCount != (long)glyphCount * 2 * UiVectorFontFormat.BandsPerAxis)
                throw new InvalidDataException($"{path}: bands {bandCount}, ожидается {(long)glyphCount * 2 * UiVectorFontFormat.BandsPerAxis}");
            long tables = (long)glyphCount * Unsafe.SizeOf<VectorGlyph>() + (long)curveCount * Unsafe.SizeOf<VectorCurve>()
                + (long)bandCount * Unsafe.SizeOf<VectorBand>() + (long)indexCount * sizeof(uint);
            if (file.CanSeek && tables + sizeof(int) + UiVectorFontFormat.EndMagic.Length > file.Length - file.Position)
                throw new InvalidDataException($"{path}: файл обрезан (таблицы — {tables} Б, осталось {file.Length - file.Position} Б)");

            var glyphs = new VectorGlyph[glyphCount];
            for (int i = 0; i < glyphCount; i++)
                glyphs[i] = new VectorGlyph
                {
                    Bounds = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    HorizontalHeader = r.ReadUInt32(),
                    VerticalHeader = r.ReadUInt32(),
                    BandScale = new Vector2(r.ReadSingle(), r.ReadSingle()),
                    BandOffset = new Vector2(r.ReadSingle(), r.ReadSingle()),
                };

            var curves = new VectorCurve[curveCount];
            for (int i = 0; i < curveCount; i++)
                curves[i] = new VectorCurve
                {
                    P12 = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    P3Pad = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                };

            var bands = new VectorBand[bandCount];
            for (int i = 0; i < bandCount; i++)
                bands[i] = new VectorBand { Count = r.ReadUInt32(), Offset = r.ReadUInt32() };

            var indices = new uint[indexCount];
            for (int i = 0; i < indexCount; i++) indices[i] = r.ReadUInt32();

            int ttfLength = Count(r.ReadInt32(), 1, UiVectorFontFormat.MaxTtfBytes, "ttf", path);
            byte[] ttf = new byte[ttfLength];
            r.BaseStream.ReadExactly(ttf);
            Span<byte> end = stackalloc byte[4];
            r.BaseStream.ReadExactly(end);
            if (!end.SequenceEqual(UiVectorFontFormat.EndMagic))
                throw new InvalidDataException($"{path}: нет конца файла шрифта");
            if (file.CanSeek && file.Position != file.Length)
                throw new InvalidDataException($"{path}: лишние данные после шрифта");

            var font = new UiFontFile
            {
                Name = name, Path = path, Roles = roles, Ascent = ascent, Descent = descent, LineGap = lineGap,
                Glyphs = glyphs, Curves = curves, Bands = bands, Indices = indices, Ttf = ttf,
            };
            font.Validate();
            return font;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException($"{path}: файл обрезан");
        }
        // Остальные IOException — ошибки чтения потока (диск, пак), а не испорченные данные: уходят как есть.
    }

    /// <summary>Строка шрифта: испорченная длина — <see cref="InvalidDataException"/> (<see cref="FormatStrings.Read"/>).</summary>
    private static string ReadString(BinaryReader r, string path) => FormatStrings.Read(r, path, UiVectorFontFormat.MaxStringBytes);

    /// <summary>Записать в поток (текущая версия формата). Поток остаётся открытым; противоречивый шрифт не пишется.</summary>
    /// <exception cref="InvalidDataException">Шрифт противоречив (<see cref="Validate"/>).</exception>
    public void Write(Stream stream)
    {
        Validate();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(UiVectorFontFormat.Magic);
        w.Write(Name);
        w.Write(Roles.Length);
        foreach (var role in Roles) w.Write(role);
        w.Write(Ascent);
        w.Write(Descent);
        w.Write(LineGap);
        w.Write(Glyphs.Length);
        w.Write(Curves.Length);
        w.Write(Bands.Length);
        w.Write(Indices.Length);
        foreach (var g in Glyphs)
        {
            w.Write(g.Bounds.X); w.Write(g.Bounds.Y); w.Write(g.Bounds.Z); w.Write(g.Bounds.W);
            w.Write(g.HorizontalHeader);
            w.Write(g.VerticalHeader);
            w.Write(g.BandScale.X); w.Write(g.BandScale.Y);
            w.Write(g.BandOffset.X); w.Write(g.BandOffset.Y);
        }
        foreach (var c in Curves)
        {
            w.Write(c.P12.X); w.Write(c.P12.Y); w.Write(c.P12.Z); w.Write(c.P12.W);
            w.Write(c.P3Pad.X); w.Write(c.P3Pad.Y); w.Write(c.P3Pad.Z); w.Write(c.P3Pad.W);
        }
        foreach (var b in Bands)
        {
            w.Write(b.Count);
            w.Write(b.Offset);
        }
        foreach (uint i in Indices) w.Write(i);
        w.Write(Ttf.Length);
        w.Write(Ttf);
        w.Write(UiVectorFontFormat.EndMagic);
    }

    /// <summary>
    /// Проверить шрифт целиком — число ролей, метрики, размеры таблиц и все ссылки между ними. Им проверяется каждый
    /// прочитанный файл и шрифт из памяти перед записью и склейкой.
    /// </summary>
    /// <exception cref="InvalidDataException">Что именно не так — с <see cref="Path"/>.</exception>
    public void Validate()
    {
        string path = Path;
        Count(Roles.Length, 0, UiVectorFontFormat.MaxRoles, "ролей", path);
        if (!float.IsFinite(Ascent) || !float.IsFinite(Descent) || !float.IsFinite(LineGap) || Ascent <= Descent)
            throw new InvalidDataException($"{path}: неверные метрики");
        Count(Glyphs.Length, 1, UiVectorFontFormat.MaxGlyphs, "glyphs", path);
        Count(Curves.Length, 0, UiVectorFontFormat.MaxCurves, "curves", path);
        Count(Bands.Length, 1, UiVectorFontFormat.MaxBands, "bands", path);
        Count(Indices.Length, 0, UiVectorFontFormat.MaxIndices, "indices", path);
        Count(Ttf.Length, 1, UiVectorFontFormat.MaxTtfBytes, "ttf", path);
        Validate(path, Glyphs, Curves, Bands, Indices);
    }

    private static void Validate(string path, VectorGlyph[] glyphs, VectorCurve[] curves, VectorBand[] bands, uint[] indices)
    {
        long expectedBands = (long)glyphs.Length * 2 * UiVectorFontFormat.BandsPerAxis;
        if (bands.Length != expectedBands)
            throw new InvalidDataException(
                $"{path}: bands {bands.Length}, ожидается {expectedBands} " +
                $"({glyphs.Length} glyphs × 2 оси × {UiVectorFontFormat.BandsPerAxis} полос)");

        for (int i = 0; i < bands.Length; i++)
            if ((ulong)bands[i].Offset + bands[i].Count > (ulong)indices.Length)
                throw new InvalidDataException($"{path}: band {i} ссылается за границу index buffer");

        for (int i = 0; i < indices.Length; i++)
            if (indices[i] >= (uint)curves.Length)
                throw new InvalidDataException($"{path}: index {i} указывает на curve {indices[i]} при {curves.Length} кривых");

        for (int i = 0; i < glyphs.Length; i++)
        {
            ref readonly var g = ref glyphs[i];
            if ((ulong)g.HorizontalHeader + UiVectorFontFormat.BandsPerAxis > (ulong)bands.Length ||
                (ulong)g.VerticalHeader + UiVectorFontFormat.BandsPerAxis > (ulong)bands.Length)
                throw new InvalidDataException($"{path}: glyph {i} имеет неверные band headers");
            if (!float.IsFinite(g.BandScale.X) || !float.IsFinite(g.BandScale.Y) ||
                !float.IsFinite(g.BandOffset.X) || !float.IsFinite(g.BandOffset.Y) ||
                !float.IsFinite(g.Bounds.X) || !float.IsFinite(g.Bounds.Y) || !float.IsFinite(g.Bounds.Z) || !float.IsFinite(g.Bounds.W) ||
                g.Bounds.X > g.Bounds.Z || g.Bounds.Y > g.Bounds.W)
                throw new InvalidDataException($"{path}: glyph {i} имеет неверные bounds или bands");
        }
    }

    private static int Count(int value, int min, int max, string what, string path) =>
        value >= min && value <= max ? value : throw new InvalidDataException($"{path}: {what}={value} вне допустимого диапазона");
}
