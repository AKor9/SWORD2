using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Engine.Ui.Tests;

/// <summary>Склейка шрифтов в общие таблицы на синтетических шрифтах.</summary>
public sealed class UiFontMergeTests
{
    private const int Bands = UiVectorFontFormat.BandsPerAxis;

    private static UiFontFile Font(string name, int glyphs, params string[] roles)
    {
        var g = new VectorGlyph[glyphs];
        var curves = new VectorCurve[glyphs];
        var bands = new VectorBand[glyphs * 2 * Bands];
        var indices = new uint[glyphs];
        for (int i = 0; i < glyphs; i++)
        {
            g[i] = new VectorGlyph { Bounds = new Vector4(0, 0, 1, 1), HorizontalHeader = (uint)(i * 2 * Bands), VerticalHeader = (uint)(i * 2 * Bands + Bands), BandScale = Vector2.One };
            curves[i] = new VectorCurve { P12 = new Vector4(i, 0, 0, 0) };
            indices[i] = (uint)i;
            for (int b = 0; b < 2 * Bands; b++) bands[i * 2 * Bands + b] = new VectorBand { Count = 1, Offset = (uint)i };
        }
        return new UiFontFile
        {
            Name = name, Path = name + ".swfont", Roles = roles, Ascent = 0.8f, Descent = -0.2f, LineGap = 0,
            Glyphs = g, Curves = curves, Bands = bands, Indices = indices, Ttf = [1],
        };
    }

    [Fact]
    public void MergeShiftsBandsIndicesAndCurves()
    {
        var a = Font("a", 2, "Body", "Head");
        var b = Font("b", 3, "Label", "NumLight", "Num");
        var data = UiFontTables.Merge([a, b]);

        Assert.Equal(5, data.Glyphs.Length);
        Assert.Equal([0, 0, 2, 2, 2], data.FaceOffsets.ToArray());
        Assert.Equal([2, 2, 3, 3, 3], data.FaceGlyphCounts.ToArray());
        Assert.Equal([0, 0, 1, 1, 1], data.RoleFont.ToArray());
        Assert.Equal(new UiFontMetrics(0.8f, -0.2f, 0), data.Metrics(UiFace.Num));

        // первый glyph второго шрифта: его полосы, индекс и кривая — со сдвигом на первый шрифт
        var glyph = data.Glyphs[2];
        Assert.Equal((uint)(2 * 2 * Bands), glyph.HorizontalHeader);
        var band = data.Bands[(int)glyph.HorizontalHeader];
        Assert.Equal(2u, band.Offset);
        Assert.Equal(2u, data.Indices[(int)band.Offset]);
        Assert.Equal(0f, data.Curves[(int)data.Indices[(int)band.Offset]].P12.X);   // своя кривая №0 второго шрифта
    }

    [Fact]
    public void FontWithoutRolesIsNotLoaded()
    {
        var data = UiFontTables.Merge([Font("all", 1, "Body", "Head", "Label", "NumLight", "Num"), Font("spare", 7)]);
        Assert.Single(data.Fonts);
        Assert.Equal(1, data.Glyphs.Length);
    }

    [Fact]
    public void RoleOwnedByTwoFontsIsRejected()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            UiFontTables.Merge([Font("a", 1, "Body", "Head", "Label"), Font("b", 1, "Body", "NumLight", "Num")]));
        Assert.Contains("Body", e.Message);
    }

    [Fact]
    public void MissingRoleIsRejected()
    {
        var e = Assert.Throws<InvalidDataException>(() => UiFontTables.Merge([Font("a", 1, "Body", "Head", "Label", "Num")]));
        Assert.Contains("NumLight", e.Message);
    }

    [Fact]
    public void RoleList_IsNotARole_AndRoleLimitIsFaceCount()
    {
        // Enum.TryParse сложил бы «Label,Head» в NumLight (2 | 1) — опечатка дала бы шрифту чужую роль.
        Assert.False(UiFaces.TryParse("Label,Head", out _));
        Assert.False(UiFaces.TryParse("Body, Head", out _));
        Assert.False(UiFaces.TryParse("", out _));
        Assert.Equal((int)UiFace.Count, UiVectorFontFormat.MaxRoles);
    }

    [Fact]
    public void Merge_ValidatesFontsBuiltInMemory()
    {
        // Шрифт, собранный в памяти, не прошёл через чтение: ссылка полосы за концом индексов в видеопамять не уходит.
        var broken = Font("all", 2, "Body", "Head", "Label", "NumLight", "Num");
        broken.Bands[5] = new VectorBand { Count = 3, Offset = 1 };
        Assert.Throws<InvalidDataException>(() => UiFontTables.Merge([broken]));
    }

    [Fact]
    public void UnknownRoleIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => UiFontTables.Merge([Font("a", 1, "Body", "Head", "Label", "NumLight", "Num", "Title")]));
        Assert.False(UiFaces.TryParse("3", out _));
        Assert.False(UiFaces.TryParse("Count", out _));
        Assert.True(UiFaces.TryParse("numlight", out var face) && face == UiFace.NumLight);
    }

    [Fact]
    public void FaceNames_MatchFormatRoleList()
    {
        // Роли манифеста шрифта проверяет пекарь по списку Formats (Ui пекарня не видит): номер роли — место в списке.
        Assert.Equal(Enum.GetNames<UiFace>()[..(int)UiFace.Count], UiVectorFontFormat.RoleNames);
        for (var f = UiFace.Body; f < UiFace.Count; f++)
            Assert.True(UiFaces.TryParse(f.ToString(), out var parsed) && parsed == f);
    }
}

/// <summary>
/// Текст на шрифтах пака: shaping HarfBuzz, метрики, квады, кадр без выделений. Категория Content: нужен пак
/// (<see cref="ContentLocator"/>); <c>verify.ps1 -SkipReference</c> их не запускает.
/// </summary>
[Trait("Category", "Content")]
public sealed class UiTextTests : IDisposable
{
    private readonly UiFonts _fonts;

    public UiTextTests()
    {
        using var pak = ContentLocator.Open();
        var files = pak.List(UiVectorFontFormat.Dir, UiVectorFontFormat.Extension)
            .Select(f =>
            {
                using var s = pak.OpenRead(f);
                return UiFontFile.Read(s, pak.Describe(f));
            }).ToList();
        _fonts = new UiFonts(files);
    }

    public void Dispose() => _fonts.Dispose();

    [Fact]
    public void EveryRole_ShapesCyrillicAndDigits_WithoutNotdef()
    {
        for (var face = UiFace.Body; face < UiFace.Count; face++)
        {
            var glyphs = _fonts.Shape(face, "FPS 60 мс").ToArray();
            Assert.Equal(9, glyphs.Length);
            Assert.All(glyphs, g => Assert.NotEqual(0u, g.GlyphId));   // .notdef — шрифт не знает символа
            Assert.All(glyphs, g => Assert.True(g.XAdvance > 0 && g.XAdvance < 2, $"{face}: шаг {g.XAdvance} em"));
            var m = _fonts.Metrics(face);
            Assert.True(m.Ascent > 0 && m.Descent < 0, $"{face}: метрики {m}");
        }
    }

    [Fact]
    public void Text_EmitsGlyphQuads_WidthMatchesMeasure_RightAlignEndsAtAnchor()
    {
        var d = new UiDrawList(_fonts);
        float measured = d.Measure(UiFace.Head, 26, "2722");
        float width = d.Text(UiFace.Head, 26, new Vector2(10, 10), 0xFFFFFFFF, "2722");
        Assert.Equal(measured, width, 3);
        Assert.Equal(4, d.Count);
        foreach (ref readonly var q in d.Quads)
        {
            Assert.Equal(UiMode.Text, q.Mode);
            uint glyph = (uint)q.Shape.X;
            Assert.InRange((int)glyph, _fonts.Tables.FaceOffsets[(int)UiFace.Head],
                _fonts.Tables.FaceOffsets[(int)UiFace.Head] + _fonts.Tables.FaceGlyphCounts[(int)UiFace.Head] - 1);
        }

        d.Clear();
        float w = d.TextRight(UiFace.Num, 15, new Vector2(300, 0), 1, "0.35 мс");
        float rightmost = 0;
        foreach (ref readonly var q in d.Quads) rightmost = MathF.Max(rightmost, q.Centre.X + q.HalfSize.X);
        Assert.InRange(rightmost, 300 - w * 0.2f, 300 + 2);   // последний glyph кончается у якоря (кайма — пиксель)
    }

    [Fact]
    public void Split_SeparatesTextAndShapes_IntoPipelineBatches()
    {
        // Текст и фигуры — разные конвейеры: текст, фигура, текст — три вызова; подряд однородные — один.
        var d = new UiDrawList(_fonts);
        d.Text(UiFace.Body, 14, new Vector2(10, 10), 1, "ab");
        d.Rect(0, 0, 50, 20, 1);
        d.Rect(0, 30, 50, 50, 1);
        d.Text(UiFace.Num, 14, new Vector2(10, 40), 1, "12");
        var batches = new List<UiBatch>();
        d.Split(640, 480, batches);
        Assert.Equal([(0, 2, true), (2, 2, false), (4, 2, true)], batches.Select(b => (b.Start, b.Count, b.Vector)));
    }

    [Fact]
    public void Append_RefusesListWithOtherFonts()
    {
        // Номера glyph'ов текста — таблиц своих шрифтов: чужой текст рисовался бы не теми буквами.
        using var other = new UiFonts(_fonts.Tables.Fonts);
        var mine = new UiDrawList(_fonts);
        var foreign = new UiDrawList(other);
        foreign.Text(UiFace.Body, 14, Vector2.Zero, 1, "x");
        Assert.Throws<ArgumentException>(() => mine.Append(foreign));
        Assert.Throws<ArgumentException>(() => new UiDrawList().Append(foreign));
        mine.Append(new UiDrawList());   // фигуры без шрифтов — можно
    }

    [Fact]
    public void TextFit_CutsWithEllipsis()
    {
        var d = new UiDrawList(_fonts);
        string text = "NVIDIA GeForce RTX 5070 Laptop GPU";
        float full = d.Measure(UiFace.Body, 11, text);
        float w = d.TextFit(UiFace.Body, 11, Vector2.Zero, 1, text, full * 0.5f);
        Assert.True(w <= full * 0.5f && w > 0);
    }

    [Fact]
    public void CaretInsideSurrogatePair_StandsBeforeThePair()
    {
        // Индекс между половинками эмодзи: без поправки мерилась бы строка с одинокой половинкой (.notdef лишней ширины).
        var d = new UiDrawList(_fonts);
        const string text = "ab😀c";
        Assert.Equal(d.CaretX(UiFace.Body, 14, text, 2), d.CaretX(UiFace.Body, 14, text, 3));
        Assert.True(d.CaretX(UiFace.Body, 14, text, 4) > d.CaretX(UiFace.Body, 14, text, 2));
    }

    [Fact]
    public void RepeatedTextFrame_AllocatesNothing()
    {
        // HUD рисует каждый кадр: shaping и квады — без выделений (Line на стеке, буферы переиспользуются).
        var d = new UiDrawList(_fonts);
        Span<char> buf = stackalloc char[32];
        var batches = new List<UiBatch>();
        void Frame(Span<char> b, double fps)
        {
            d.Clear();
            fps.TryFormat(b, out int n, "F0", System.Globalization.CultureInfo.InvariantCulture);
            d.Text(UiFace.Head, 26, new Vector2(10, 10), 1, b[..n]);
            d.TextRight(UiFace.Body, 12, new Vector2(300, 10), 1, "макс 16.7 мс");
            d.PushClip(new UiRect(0, 0, 200, 50));
            d.TextCentered(UiFace.Label, 11, new Vector2(100, 20), 1, "КАДР");
            d.PopClip();
            d.Rect(0, 0, 100, 100, 1, 8);
            d.Split(640, 480, batches);
        }
        for (int i = 0; i < 10; i++) Frame(buf, 60 + i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Frame(buf, 1000 + i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
