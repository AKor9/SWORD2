using System.Globalization;
using Sword.Bake.Font;
using Sword.Engine.Content;
using Sword.Engine.Formats;

namespace Sword.Assets.Preview;

/// <summary>
/// Предпросмотр <c>.swfont</c>: образец текста разными кеглями и таблица знаков, которые обещает интерфейс
/// (<see cref="Ranges"/>; отсутствующие — красным). Рисуется из запечённых кривых (<see cref="UiFontRaster"/>, без шейпинга).
/// Перенос <c>FontPreview</c> SWORD 2eab13b.
/// </summary>
internal static class FontPreview
{
    private static readonly string[] Modes = ["Образец", "Набор знаков"];
    private const uint White = 0xFFFFFFFF;
    private static readonly uint Dim = PreviewScene.Rgb(150, 160, 170);

    /// <summary>
    /// Знаки, которые интерфейс обещает поддерживать (<c>UiFontFormat.Ranges</c> SWORD 2eab13b): диапазоны по возрастанию
    /// кода без пересечений — по ним раскладывается сетка набора знаков.
    /// </summary>
    internal static readonly (int First, int Last)[] Ranges =
    [
        (0x0020, 0x007E), (0x00A0, 0x00A0), (0x00AB, 0x00AB), (0x00B0, 0x00B0), (0x00B7, 0x00B7), (0x00BB, 0x00BB),
        (0x00D7, 0x00D7), (0x0401, 0x0401), (0x0410, 0x044F), (0x0451, 0x0451), (0x2013, 0x2014), (0x2018, 0x201A),
        (0x201C, 0x201E), (0x2026, 0x2026), (0x2212, 0x2212), (0x2248, 0x2248), (0x2264, 0x2265),
    ];

    private static int GlyphCount => Ranges.Sum(r => r.Last - r.First + 1);

    private static readonly (float Size, string Text)[] Sample =
    [
        (12, "Съешь же ещё этих мягких французских булок, да выпей чаю. The quick brown fox jumps over the lazy dog. 0123456789"),
        (14, "Съешь же ещё этих мягких французских булок, да выпей чаю. The quick brown fox jumps over the lazy dog."),
        (16, "Съешь же ещё этих мягких французских булок, да выпей чаю. The quick brown fox jumps."),
        (20, "Съешь же ещё этих мягких французских булок, да выпей чаю."),
        (28, "Широкая электрификация южных губерний"),
        (40, "Жёлтый чёрт · Quartz 1 234,56"),
        (64, "Ёж АБВ abc 0123"),
        (18, "«Кавычки» „лапки“ ‘одиночные’ — тире – минус −5 × ≈ ≤ ≥ 90° · … №"),
    ];

    public static PreviewScene? Build(IContentSource content, IReadOnlyList<string> paths, int mode)
    {
        string? path = paths.FirstOrDefault(p => p.EndsWith(UiVectorFontFormat.Extension, StringComparison.OrdinalIgnoreCase));
        if (path is null) return null;
        UiFontRaster font;
        using (var stream = content.OpenRead(path)) font = UiFontRaster.Read(stream, content.Describe(path));
        using var _ = font;
        var scene = new PreviewScene { Modes = Modes };
        scene.Info.Add(string.Create(CultureInfo.InvariantCulture,
            $"{font.Name}: роли {(font.Roles.Count == 0 ? "нет (игра не загрузит)" : string.Join(", ", font.Roles))}; glyphs {font.GlyphCount}, кривых {font.CurveCount}, ttf {font.TtfBytes / 1024.0:0} КБ"));
        scene.Info.Add(string.Create(CultureInfo.InvariantCulture,
            $"ascent {font.Ascent:0.###}, descent {font.Descent:0.###}, line gap {font.LineGap:0.###} em — строка {(font.Ascent - font.Descent + font.LineGap):0.###} em; расстановка — по cmap без шейпинга"));

        var missing = new List<int>();
        foreach (var (first, last) in Ranges)
            for (int c = first; c <= last; c++)
                if (!font.Has(c)) missing.Add(c);
        scene.Info.Add(missing.Count == 0
            ? $"все {GlyphCount} знаков интерфейса есть"
            : $"нет {missing.Count} из {GlyphCount} знаков интерфейса: {string.Join(" ", missing.Take(24).Select(c => $"U+{c:X4}"))}{(missing.Count > 24 ? " …" : "")}");

        scene.Images.Add(mode == 1 ? Charset(font, missing) : SampleSheet(font));
        return scene;
    }

    private static PreviewImage SampleSheet(UiFontRaster font)
    {
        const int width = 1600, pad = 24;
        float LineH(float size) => (font.Ascent - font.Descent + font.LineGap) * size;
        float height = pad * 2;
        foreach (var (size, _) in Sample) height += LineH(size) + 16 + 8;
        int h = (int)MathF.Ceiling(height);
        var rgba = new byte[width * h * 4];
        float y = pad;
        foreach (var (size, text) in Sample)
        {
            font.Draw(rgba, width, h, pad, y, 11, size.ToString(CultureInfo.InvariantCulture) + " px", Dim);
            y += 16;
            font.Draw(rgba, width, h, pad, y, size, text, White);
            y += LineH(size) + 8;
        }
        return new PreviewImage
        {
            Name = font.Name, Width = width, Height = h, Mips = PreviewImage.BuildMips(rgba, width, h),
            Caption = $"{width}×{h}",
        };
    }

    private static PreviewImage Charset(UiFontRaster font, List<int> missing)
    {
        const int columns = 16, cell = 84, pad = 12;
        var codes = Ranges.SelectMany(r => Enumerable.Range(r.First, r.Last - r.First + 1)).ToList();
        int rows = (codes.Count + columns - 1) / columns;
        int width = columns * cell + pad * 2, height = rows * cell + pad * 2;
        var rgba = new byte[width * height * 4];
        var absent = missing.ToHashSet();
        const float glyphSize = 44;
        for (int i = 0; i < codes.Count; i++)
        {
            int cx = pad + i % columns * cell, cy = pad + i / columns * cell;
            int code = codes[i];
            Fill(rgba, width, cx + 2, cy + 2, cell - 4, cell - 4, absent.Contains(code) ? PreviewScene.Rgb(120, 30, 30) : PreviewScene.Rgb(34, 38, 44));
            font.Draw(rgba, width, height, cx + 6, cy + 4, 11, $"{code:X4}", Dim);
            if (code is 0x20 or 0xA0) continue;
            string text = char.ConvertFromUtf32(code);
            float w = font.Measure(glyphSize, text);
            float lineTop = cy + cell - 10 - (font.Ascent - font.Descent) * glyphSize;
            font.Draw(rgba, width, height, cx + (cell - w) * 0.5f, lineTop, glyphSize, text, White);
        }
        return new PreviewImage
        {
            Name = font.Name, Width = width, Height = height, Mips = PreviewImage.BuildMips(rgba, width, height),
            Caption = $"{codes.Count} знаков",
        };
    }

    private static void Fill(byte[] rgba, int width, int x, int y, int w, int h, uint colour)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int o = (yy * width + xx) * 4;
                rgba[o] = (byte)colour;
                rgba[o + 1] = (byte)(colour >> 8);
                rgba[o + 2] = (byte)(colour >> 16);
                rgba[o + 3] = (byte)(colour >> 24);
            }
    }
}
