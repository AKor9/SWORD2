using Sword.Engine.Formats;

namespace Sword.Engine.Formats.Tests;

/// <summary>Пределы и ошибки чтения шрифта: полный предел glyph'ов, испорченный заголовок, ошибка потока.</summary>
public class UiFontLimitsTests
{
    [Fact]
    public void MaxGlyphs_RoundTrips()
    {
        // Полос у MaxGlyphs glyph'ов — ровно MaxBands: заявленный предел glyph'ов достижим (в SWORD — только 62 500).
        Assert.Equal(UiVectorFontFormat.MaxGlyphs * 2 * UiVectorFontFormat.BandsPerAxis, UiVectorFontFormat.MaxBands);
        var font = UiFontFileTests.Font("cjk", UiVectorFontFormat.MaxGlyphs, "Body");
        var back = UiFontFile.Read(new MemoryStream(UiFontFileTests.Save(font)), "cjk");
        Assert.Equal(UiVectorFontFormat.MaxGlyphs, back.Glyphs.Length);
    }

    [Fact]
    public void CorruptHeader_OfTinyFile_AllocatesNoTables()
    {
        // Заголовок заявляет предельные таблицы, а файл кончается сразу за ним: ошибка — до выделения сотен МБ.
        var bytes = UiFontFileTests.Save(UiFontFileTests.Font("b", 1, "Body"));
        const int counts = 8 + 2 + 4 + 5 + 12;   // магия, имя «b», число ролей, роль «Body», метрики
        var header = bytes[..(counts + 16)];
        BitConverter.GetBytes(UiVectorFontFormat.MaxGlyphs).CopyTo(header, counts);
        BitConverter.GetBytes(UiVectorFontFormat.MaxCurves).CopyTo(header, counts + 4);
        BitConverter.GetBytes(UiVectorFontFormat.MaxBands).CopyTo(header, counts + 8);
        BitConverter.GetBytes(UiVectorFontFormat.MaxIndices).CopyTo(header, counts + 12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(header), "tiny"));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20, "таблицы выделены до проверки размера");
        Assert.Contains("обрезан", e.Message);
    }

    [Fact]
    public void StreamReadError_IsNotCalledCorruptData()
    {
        // Ошибка чтения пака или диска — не «испорчена строка»: уходит как есть.
        var bytes = UiFontFileTests.Save(UiFontFileTests.Font("body", 1, "Body"));
        var e = Assert.Throws<IOException>(() => UiFontFile.Read(new FailingStream(bytes, failAt: 10), "io"));
        Assert.IsNotType<InvalidDataException>(e);
        Assert.Equal("диск", e.Message);
    }

    [Fact]
    public void HugeStringLength_IsInvalidData()
    {
        var bytes = UiFontFileTests.Save(UiFontFileTests.Font("body", 1, "Body"));
        byte[] broken = [.. bytes[..8], 0xFF, 0xFF, 0x03, .. bytes[9..]];   // длина имени 65 535 байт
        Assert.Contains("испорчена строка", Assert.Throws<InvalidDataException>(() => UiFontFile.Read(new MemoryStream(broken), "str")).Message);
    }

    /// <summary>Поток, чтение которого с байта <c>failAt</c> — IOException (ошибка диска).</summary>
    private sealed class FailingStream(byte[] data, int failAt) : MemoryStream(data)
    {
        // Read(Span) наследника MemoryStream идёт через Read(byte[], …).
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAt) throw new IOException("диск");
            return base.Read(buffer, offset, (int)Math.Min(count, failAt - Position));
        }

        public override int ReadByte() => Position >= failAt ? throw new IOException("диск") : base.ReadByte();
    }
}
