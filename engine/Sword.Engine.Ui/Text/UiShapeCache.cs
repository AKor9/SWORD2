namespace Sword.Engine.Ui;

/// <summary>
/// Кэш shaping: (роль, строка) → glyph'ы. Таблица с открытой адресацией и два буфера (символы ключей, glyph'ы) заводятся
/// один раз; места нет — кэш очищается целиком (меняющиеся числа HUD забивают его за сотни кадров, постоянные подписи
/// заново shaping'уются один раз). Выделений после создания нет. Длинные строки не кэшируются.
/// </summary>
internal sealed class UiShapeCache
{
    /// <summary>Строки длиннее не кэшируются: справка и подсказки короче, а одна такая строка заняла бы буфер.</summary>
    public const int MaxText = 512;

    private const int Slots = 4096, MaxEntries = Slots / 2, Chars = 1 << 16, Glyphs = 1 << 16;

    private struct Entry
    {
        public int Hash;      // 0 — пусто
        public int Face, CharStart, Length, GlyphStart, GlyphCount;
    }

    private readonly Entry[] _entries = new Entry[Slots];
    private readonly char[] _chars = new char[Chars];
    private readonly UiShapedGlyph[] _glyphs = new UiShapedGlyph[Glyphs];
    private int _count, _charsUsed, _glyphsUsed;

    public bool TryGet(UiFace face, ReadOnlySpan<char> text, out ReadOnlySpan<UiShapedGlyph> glyphs)
    {
        int hash = Hash(face, text);
        for (int i = hash & (Slots - 1); ; i = (i + 1) & (Slots - 1))
        {
            ref readonly var e = ref _entries[i];
            if (e.Hash == 0) break;
            if (e.Hash == hash && e.Face == (int)face && e.Length == text.Length && text.SequenceEqual(_chars.AsSpan(e.CharStart, e.Length)))
            {
                glyphs = _glyphs.AsSpan(e.GlyphStart, e.GlyphCount);
                return true;
            }
        }
        glyphs = default;
        return false;
    }

    /// <summary>Запомнить <paramref name="glyphs"/> строки и вернуть их копию из кэша (или их самих, если строка не кэшируется).</summary>
    public ReadOnlySpan<UiShapedGlyph> Add(UiFace face, ReadOnlySpan<char> text, ReadOnlySpan<UiShapedGlyph> glyphs)
    {
        if (text.Length > MaxText || glyphs.Length > MaxText) return glyphs;
        if (_count == MaxEntries || _charsUsed + text.Length > Chars || _glyphsUsed + glyphs.Length > Glyphs) Reset();

        int hash = Hash(face, text);
        int i = hash & (Slots - 1);
        while (_entries[i].Hash != 0) i = (i + 1) & (Slots - 1);
        _entries[i] = new Entry
        {
            Hash = hash, Face = (int)face, CharStart = _charsUsed, Length = text.Length, GlyphStart = _glyphsUsed, GlyphCount = glyphs.Length,
        };
        text.CopyTo(_chars.AsSpan(_charsUsed));
        glyphs.CopyTo(_glyphs.AsSpan(_glyphsUsed));
        _charsUsed += text.Length;
        _glyphsUsed += glyphs.Length;
        _count++;
        return _glyphs.AsSpan(_entries[i].GlyphStart, glyphs.Length);
    }

    private void Reset()
    {
        Array.Clear(_entries);
        _count = _charsUsed = _glyphsUsed = 0;
    }

    private static int Hash(UiFace face, ReadOnlySpan<char> text)
    {
        int h = HashCode.Combine(string.GetHashCode(text), (int)face);
        return h == 0 ? 1 : h;
    }
}
