using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

internal static class UiFontFileBuild
{
    public static UiFontFile WithBands(this UiFontFile f, int drop) => new()
    {
        Name = f.Name, Path = f.Path, Roles = f.Roles, Ascent = f.Ascent, Descent = f.Descent, LineGap = f.LineGap,
        Glyphs = f.Glyphs, Curves = f.Curves, Bands = f.Bands[..^drop], Indices = f.Indices, Ttf = f.Ttf,
    };

    public static UiFontFile WithMetrics(this UiFontFile f, float ascent, float descent) => new()
    {
        Name = f.Name, Path = f.Path, Roles = f.Roles, Ascent = ascent, Descent = descent, LineGap = f.LineGap,
        Glyphs = f.Glyphs, Curves = f.Curves, Bands = f.Bands, Indices = f.Indices, Ttf = f.Ttf,
    };
}

public class UiFontFileTests
{
    private const int Bands = UiVectorFontFormat.BandsPerAxis;

    /// <summary>Синтетический шрифт: glyph'ы по одной кривой, полосы glyph'а ссылаются на его кривую.</summary>
    internal static UiFontFile Font(string name, int glyphs, params string[] roles)
    {
        var g = new VectorGlyph[glyphs];
        var curves = new VectorCurve[glyphs];
        var bands = new VectorBand[glyphs * 2 * Bands];
        var indices = new uint[glyphs];
        for (int i = 0; i < glyphs; i++)
        {
            g[i] = new VectorGlyph
            {
                Bounds = new Vector4(0, 0, 1, 1), HorizontalHeader = (uint)(i * 2 * Bands),
                VerticalHeader = (uint)(i * 2 * Bands + Bands), BandScale = Vector2.One,
            };
            curves[i] = new VectorCurve { P12 = new Vector4(i, 0, 0, 0) };
            indices[i] = (uint)i;
            for (int b = 0; b < 2 * Bands; b++) bands[i * 2 * Bands + b] = new VectorBand { Count = 1, Offset = (uint)i };
        }
        return new UiFontFile
        {
            Name = name, Path = name + UiVectorFontFormat.Extension, Roles = roles, Ascent = 0.8f, Descent = -0.2f, LineGap = 0,
            Glyphs = g, Curves = curves, Bands = bands, Indices = indices, Ttf = [1, 2, 3],
        };
    }

    internal static byte[] Save(UiFontFile f)
    {
        using var s = new MemoryStream();
        f.Write(s);
        return s.ToArray();
    }

    [Fact]
    public unsafe void RecordSizesMatchShaderStrides()
    {
        Assert.Equal(40, sizeof(VectorGlyph));
        Assert.Equal(32, sizeof(VectorCurve));
        Assert.Equal(8, sizeof(VectorBand));
    }

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var a = Font("body", 3, "Body", "Head");
        var bytes = Save(a);
        var b = UiFontFile.Read(new MemoryStream(bytes), "mem");
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.Roles, b.Roles);
        Assert.Equal((a.Ascent, a.Descent, a.LineGap), (b.Ascent, b.Descent, b.LineGap));
        Assert.Equal(a.Glyphs, b.Glyphs);
        Assert.Equal(a.Curves, b.Curves);
        Assert.Equal(a.Bands, b.Bands);
        Assert.Equal(a.Indices, b.Indices);
        Assert.Equal(a.Ttf, b.Ttf);
        Assert.Equal(bytes, Save(b));
    }

    [Fact]
    public void EveryTruncationIsInvalidData()
    {
        var bytes = Save(Font("body", 2, "Body"));
        for (int n = 0; n < bytes.Length; n++)
        {
            var e = Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(bytes, 0, n), "cut"));
            Assert.Contains("cut", e.Message);
        }
    }

    [Fact]
    public void TrailingBytesAndOtherVersionAreRejected()
    {
        var bytes = Save(Font("body", 1, "Body"));
        Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream([.. bytes, 0]), "tail"));
        var old = (byte[])bytes.Clone();
        old[7] = UiVectorFontFormat.Version - 1;
        Assert.Contains("перепеките", Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(old), "old")).Message);
    }

    [Fact]
    public void BrokenStringLength_IsInvalidData_NotIoOrFormatException()
    {
        // Длина имени — 7-битное число BinaryWriter: «FF FF FF FF 0F» — −1 (IOException), «FF×5» — не число (FormatException).
        var bytes = Save(Font("body", 1, "Body"));
        foreach (byte[] length in (byte[][])[[0xFF, 0xFF, 0xFF, 0xFF, 0x0F], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]])
        {
            byte[] broken = [.. bytes[..8], .. length, .. bytes[9..]];
            Assert.Contains("испорчена строка", Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(broken), "str")).Message);
        }
    }

    [Fact]
    public void InconsistentFont_IsRejectedOnReadAndWrite()
    {
        static void Rejected(UiFontFile f)
        {
            Assert.Throws<InvalidDataException>(() => f.Write(new MemoryStream()));
            Assert.Throws<InvalidDataException>(() => f.Validate());
        }
        var header = Font("body", 2, "Body");
        header.Glyphs[1].VerticalHeader = (uint)header.Bands.Length - 1;   // полосы glyph'а — за концом таблицы
        Rejected(header);
        Rejected(Font("body", 2, "Body").WithBands(1));
        Rejected(Font("body", 1, "Body").WithMetrics(0.2f, 0.8f));
        Rejected(Font("body", 1, "Body", "Head", "Label", "NumLight", "Num", "Body"));   // ролей больше пяти

        // Чтение ловит то же: запись в обход проверки — байты правильного файла с испорченной ссылкой.
        var good = Save(Font("body", 2, "Body"));
        int glyphs = 8 + 5 + 4 + 5 + 12 + 16;                 // магия, имя «body», ролей, «Body», метрики, счётчики
        BitConverter.TryWriteBytes(good.AsSpan(glyphs + 40 + 20), 10_000u);   // вертикальные полосы второго glyph'а
        Assert.Contains("band headers", Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(good), "hdr")).Message);
    }

    [Fact]
    public void ReferencesOutsideTablesAreRejected()
    {
        var f = Font("body", 2, "Body");
        f.Indices[1] = 99;   // кривой 99 нет
        Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(Save(f)), "idx"));

        var g = Font("body", 2, "Body");
        g.Bands[3] = new VectorBand { Count = 5, Offset = 0 };   // полоса за концом индексов
        Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(Save(g)), "band"));

        var h = Font("body", 2, "Body");
        h.Glyphs[1].Bounds = new Vector4(float.NaN, 0, 1, 1);
        Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(Save(h)), "nan"));
    }
}
