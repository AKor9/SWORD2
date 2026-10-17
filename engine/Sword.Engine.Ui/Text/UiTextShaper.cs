using HarfBuzzSharp;
using Sword.Engine.Formats;

namespace Sword.Engine.Ui;

/// <summary>
/// OpenType shaping HarfBuzz поверх ttf из .swfont: glyph ID, kerning, лигатуры, combining marks; без растеризации.
/// </summary>
internal sealed class UiTextShaper : IDisposable
{
    private readonly Blob[] _blobs;
    private readonly Face[] _faces;
    private readonly Font[] _fonts;
    private readonly float[] _invEm;
    private readonly int[] _roleFont;
    private readonly HarfBuzzSharp.Buffer _buffer = new();
    private UiShapedGlyph[] _result = new UiShapedGlyph[128];
    private readonly UiShapeCache _cache = new();

    /// <param name="fonts">Шрифты в порядке склейки.</param>
    /// <param name="roleFont">[роль] → номер шрифта.</param>
    public UiTextShaper(IReadOnlyList<UiFontFile> fonts, ReadOnlySpan<int> roleFont)
    {
        _roleFont = roleFont.ToArray();
        _blobs = new Blob[fonts.Count];
        _faces = new Face[fonts.Count];
        _fonts = new Font[fonts.Count];
        _invEm = new float[fonts.Count];

        try
        {
            for (int i = 0; i < fonts.Count; i++)
            {
                var file = fonts[i];
                _blobs[i] = NativeBlob(file.Ttf);
                _faces[i] = new Face(_blobs[i], 0);
                if (_faces[i].GlyphCount != file.Glyphs.Length)
                    throw new InvalidDataException(
                        $"{file.Path}: HarfBuzz видит {_faces[i].GlyphCount} glyphs, " +
                        $"vector font содержит {file.Glyphs.Length}");

                _fonts[i] = new Font(_faces[i]);
                _fonts[i].SetFunctionsOpenType();
                _fonts[i].SetScale(_faces[i].UnitsPerEm, _faces[i].UnitsPerEm);
                _invEm[i] = 1f / _faces[i].UnitsPerEm;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Blob над копией файла в неуправляемой памяти (освобождает HarfBuzz): Face читает таблицы лениво при каждом shaping,
    /// а <c>Blob.FromStream</c> не обещает, что данные доживут.
    /// </summary>
    private static unsafe Blob NativeBlob(byte[] data)
    {
        nint memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        data.AsSpan().CopyTo(new Span<byte>((void*)memory, data.Length));
        return new Blob(memory, data.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal(memory));
    }

    /// <summary>
    /// Shaping строки шрифтом роли. Результат живёт до следующего вызова. Повтор той же строки той же ролью — из кэша
    /// (<see cref="UiShapeCache"/>): интерфейс каждый кадр рисует почти те же строки, а HarfBuzz — самое дорогое в тексте.
    /// </summary>
    public ReadOnlySpan<UiShapedGlyph> Shape(UiFace face, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return [];
        if (_cache.TryGet(face, text, out var cached)) return cached;
        return _cache.Add(face, text, ShapeNow(face, text));
    }

    /// <summary>Строк, отданных HarfBuzz с создания.</summary>
    public long Shaped { get; private set; }

    private ReadOnlySpan<UiShapedGlyph> ShapeNow(UiFace face, ReadOnlySpan<char> text)
    {
        Shaped++;
        int font = _roleFont[(int)face];

        _buffer.Reset();
        _buffer.AddUtf16(text);
        _buffer.GuessSegmentProperties();
        // Feature[] через HarfBuzzSharp выделяет ~56 Б на вызов; разрядка — наша, лигатур в подписях нет, поэтому штатный
        // набор фич даёт тот же результат без выделения.
        _fonts[font].Shape(_buffer);

        ReadOnlySpan<GlyphInfo> infos = _buffer.GetGlyphInfoSpan();
        ReadOnlySpan<GlyphPosition> positions = _buffer.GetGlyphPositionSpan();
        if (_result.Length < infos.Length)
            Array.Resize(ref _result, Math.Max(infos.Length, _result.Length * 2));

        float invEm = _invEm[font];
        for (int i = 0; i < infos.Length; i++)
        {
            ref readonly var p = ref positions[i];
            _result[i] = new UiShapedGlyph(
                infos[i].Codepoint, infos[i].Cluster,
                p.XAdvance * invEm, -p.YAdvance * invEm,
                p.XOffset * invEm, -p.YOffset * invEm);
        }

        return _result.AsSpan(0, infos.Length);
    }

    public void Dispose()
    {
        _buffer.Dispose();
        for (int i = _fonts.Length - 1; i >= 0; i--)
        {
            _fonts[i]?.Dispose();
            _faces[i]?.Dispose();
            _blobs[i]?.Dispose();
        }
    }
}
