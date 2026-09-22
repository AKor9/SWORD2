using System.Numerics;
using Sword.Engine.Formats;

namespace Sword.Engine.Ui;

/// <summary>
/// Шрифты интерфейса без GPU: склеенные таблицы контуров (<see cref="Tables"/> — их загружает в видеопамять UI-проход
/// рендера), shaping HarfBuzz и метрики. Файлы читает вызывающий (<see cref="UiFontFile.Read"/>: из пака — рендер).
/// Перенос CPU-части <c>Text/UiVectorFont.cs</c> SWORD 2eab13b; GPU-часть — в Render.
/// </summary>
public sealed class UiFonts : IDisposable
{
    private readonly UiTextShaper _shaper;
    private bool _disposed;

    /// <exception cref="InvalidDataException">Роли шрифтов не сходятся или HarfBuzz видит другой шрифт, чем контуры.</exception>
    public UiFonts(IReadOnlyList<UiFontFile> files)
    {
        Tables = UiFontTables.Merge(files);
        _shaper = new UiTextShaper(Tables.Fonts, Tables.RoleFont);
    }

    public UiFontTables Tables { get; }

    public int GlyphCount => Tables.Glyphs.Length;

    public UiFontMetrics Metrics(UiFace face) => Tables.Metrics(face);

    /// <summary>Имя шрифта, взявшего роль.</summary>
    public string FontName(UiFace face) => Tables.Fonts[Tables.RoleFont[(int)face]].Name;

    /// <summary>Рамка glyph'а в долях кегля; номер вне шрифта — glyph 0 (.notdef).</summary>
    public Vector4 Bounds(UiFace face, uint glyphId) => Tables.Glyphs[(int)VectorIndex(face, glyphId)].Bounds;

    /// <summary>Номер glyph'а в общей таблице; номер вне шрифта — glyph 0 шрифта роли.</summary>
    public uint VectorIndex(UiFace face, uint glyphId)
    {
        int f = (int)face;
        if (glyphId >= (uint)Tables.FaceGlyphCounts[f]) glyphId = 0;
        return (uint)(Tables.FaceOffsets[f] + glyphId);
    }

    /// <summary>Shaping строки шрифтом роли. Результат живёт до следующего вызова (буфер переиспользуется).</summary>
    public ReadOnlySpan<UiShapedGlyph> Shape(UiFace face, ReadOnlySpan<char> text) => _shaper.Shape(face, text);

    public string Report() => $"glyphs {GlyphCount}, шрифтов {Tables.Fonts.Count}: {string.Join(", ", Tables.Fonts.Select(f => f.Name))}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shaper.Dispose();
    }
}
