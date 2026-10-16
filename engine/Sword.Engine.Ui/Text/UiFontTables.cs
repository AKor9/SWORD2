using Sword.Engine.Formats;

namespace Sword.Engine.Ui;

/// <summary>
/// Шрифты интерфейса, склеенные в общие таблицы: весь интерфейс рисуется одним набором буферов без переключений.
/// </summary>
public sealed class UiFontTables
{
    private readonly VectorGlyph[] _glyphs;
    private readonly VectorCurve[] _curves;
    private readonly VectorBand[] _bands;
    private readonly uint[] _indices;
    private readonly int[] _roleFont, _faceOffsets, _faceGlyphCounts;
    private readonly UiFontMetrics[] _metrics;

    public ReadOnlySpan<VectorGlyph> Glyphs => _glyphs;
    public ReadOnlySpan<VectorCurve> Curves => _curves;
    public ReadOnlySpan<VectorBand> Bands => _bands;
    public ReadOnlySpan<uint> Indices => _indices;

    /// <summary>Шрифты с ролями, в порядке склейки.</summary>
    public IReadOnlyList<UiFontFile> Fonts { get; }

    /// <summary>[роль] → номер шрифта в <see cref="Fonts"/>.</summary>
    public ReadOnlySpan<int> RoleFont => _roleFont;

    /// <summary>[роль] → первый glyph шрифта в общей таблице.</summary>
    public ReadOnlySpan<int> FaceOffsets => _faceOffsets;

    public ReadOnlySpan<int> FaceGlyphCounts => _faceGlyphCounts;

    public UiFontMetrics Metrics(UiFace face) => _metrics[(int)face];

    private UiFontTables(VectorGlyph[] glyphs, VectorCurve[] curves, VectorBand[] bands, uint[] indices,
        IReadOnlyList<UiFontFile> fonts, int[] roleFont, int[] faceOffsets, int[] faceGlyphCounts, UiFontMetrics[] metrics)
    {
        _glyphs = glyphs;
        _curves = curves;
        _bands = bands;
        _indices = indices;
        Fonts = fonts;
        _roleFont = roleFont;
        _faceOffsets = faceOffsets;
        _faceGlyphCounts = faceGlyphCounts;
        _metrics = metrics;
    }

    /// <summary>
    /// Склеить шрифты: каждая роль — ровно у одного шрифта, шрифты без ролей не загружаются; каждый шрифт проверяется.
    /// </summary>
    /// <exception cref="InvalidDataException">Противоречивый шрифт, неизвестная роль, роль у двух шрифтов или без шрифта.</exception>
    public static UiFontTables Merge(IReadOnlyList<UiFontFile> files)
    {
        foreach (var f in files) f.Validate();
        int roleCount = (int)UiFace.Count;
        var owner = new UiFontFile?[roleCount];
        foreach (var f in files)
            foreach (var role in f.Roles)
            {
                if (!UiFaces.TryParse(role, out var face))
                    throw new InvalidDataException($"{f.Path}: неизвестная роль «{role}»; роли: {UiFaces.List}");
                if (owner[(int)face] is { } other && other != f)
                    throw new InvalidDataException($"роль {face} у двух шрифтов: {other.Path} и {f.Path}");
                owner[(int)face] = f;
            }
        var missing = Enumerable.Range(0, roleCount).Where(i => owner[i] is null).Select(i => ((UiFace)i).ToString()).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"нет шрифта для ролей {string.Join(", ", missing)} — роли задаются полем roles рецепта шрифта пекарни; " +
                $"загружены: {(files.Count == 0 ? "ничего" : string.Join(", ", files.Select(f => $"{f.Name} [{string.Join(", ", f.Roles)}]")))}");

        var fonts = files.Where(f => owner.Contains(f)).ToList();
        var glyphs = new VectorGlyph[fonts.Sum(f => f.Glyphs.Length)];
        var curves = new VectorCurve[fonts.Sum(f => f.Curves.Length)];
        var bands = new VectorBand[fonts.Sum(f => f.Bands.Length)];
        var indices = new uint[fonts.Sum(f => f.Indices.Length)];
        var glyphBase = new int[fonts.Count];
        int g = 0, c = 0, b = 0, x = 0;
        for (int k = 0; k < fonts.Count; k++)
        {
            var f = fonts[k];
            glyphBase[k] = g;
            foreach (var glyph in f.Glyphs)
                glyphs[g++] = glyph with { HorizontalHeader = glyph.HorizontalHeader + (uint)b, VerticalHeader = glyph.VerticalHeader + (uint)b };
            for (int i = 0; i < f.Bands.Length; i++)
                bands[b + i] = new VectorBand { Count = f.Bands[i].Count, Offset = f.Bands[i].Offset + (uint)x };
            for (int i = 0; i < f.Indices.Length; i++)
                indices[x + i] = f.Indices[i] + (uint)c;
            f.Curves.CopyTo(curves, c);
            c += f.Curves.Length;
            b += f.Bands.Length;
            x += f.Indices.Length;
        }

        var roleFont = new int[roleCount];
        var offsets = new int[roleCount];
        var counts = new int[roleCount];
        var metrics = new UiFontMetrics[roleCount];
        for (int role = 0; role < roleCount; role++)
        {
            int k = fonts.IndexOf(owner[role]!);
            roleFont[role] = k;
            offsets[role] = glyphBase[k];
            counts[role] = fonts[k].Glyphs.Length;
            metrics[role] = new UiFontMetrics(fonts[k].Ascent, fonts[k].Descent, fonts[k].LineGap);
        }
        return new UiFontTables(glyphs, curves, bands, indices, fonts, roleFont, offsets, counts, metrics);
    }
}
