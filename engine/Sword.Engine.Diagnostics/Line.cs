using System.Globalization;

namespace Sword.Engine.Diagnostics;

/// <summary>
/// Строка на стеке: текст HUD и строк состояния (<see cref="TuningText"/>) без выделений в кадре. Не влезло в буфер —
/// обрезается (строка состояния не роняет кадр); числа — инвариантной культурой.
/// </summary>
public ref struct Line
{
    private readonly Span<char> _buf;
    private int _n;

    public Line(Span<char> buf)
    {
        _buf = buf;
        _n = 0;
    }

    public Line S(ReadOnlySpan<char> s)
    {
        int k = Math.Min(s.Length, _buf.Length - _n);
        s[..k].CopyTo(_buf[_n..]);
        _n += k;
        return this;
    }

    public Line F(double v, string fmt = "F1")
    {
        if (v.TryFormat(_buf[_n..], out int w, fmt, CultureInfo.InvariantCulture)) _n += w;
        return this;
    }

    public Line I(long v)
    {
        if (v.TryFormat(_buf[_n..], out int w, default, CultureInfo.InvariantCulture)) _n += w;
        return this;
    }

    public readonly ReadOnlySpan<char> Text => _buf[.._n];

    /// <summary>Длина написанного — итог <see cref="TuningText"/>.</summary>
    public readonly int Length => _n;
}
