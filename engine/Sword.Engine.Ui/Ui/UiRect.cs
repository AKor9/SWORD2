using System.Numerics;

namespace Sword.Engine.Ui;

/// <summary>
/// Прямоугольник интерфейса в пикселях экрана: левый верхний (X0, Y0), правый нижний (X1, Y1).
/// Раскладка — нарезкой: <c>var bar = area.SplitTop(40, out area);</c> отрезает полосу сверху, остаток — в area.
/// </summary>
public readonly record struct UiRect(float X0, float Y0, float X1, float Y1)
{
    public static UiRect FromSize(float x, float y, float width, float height) => new(x, y, x + width, y + height);

    public float Width => X1 - X0;
    public float Height => Y1 - Y0;
    public Vector2 Min => new(X0, Y0);
    public Vector2 Max => new(X1, Y1);
    public Vector2 Size => new(Width, Height);
    public Vector2 Centre => new((X0 + X1) * 0.5f, (Y0 + Y1) * 0.5f);
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

    public bool Contains(Vector2 p) => p.X >= X0 && p.X < X1 && p.Y >= Y0 && p.Y < Y1;

    /// <summary>Пересечение; не пересекаются — пустой прямоугольник нулевого размера.</summary>
    public UiRect Intersect(UiRect o)
    {
        float x0 = MathF.Max(X0, o.X0), y0 = MathF.Max(Y0, o.Y0);
        return new UiRect(x0, y0, MathF.Max(x0, MathF.Min(X1, o.X1)), MathF.Max(y0, MathF.Min(Y1, o.Y1)));
    }

    public UiRect Union(UiRect o) =>
        IsEmpty ? o : o.IsEmpty ? this : new(MathF.Min(X0, o.X0), MathF.Min(Y0, o.Y0), MathF.Max(X1, o.X1), MathF.Max(Y1, o.Y1));

    /// <summary>Внутрь на <paramref name="d"/> со всех сторон; не меньше нуля.</summary>
    public UiRect Shrink(float d) => Shrink(d, d);

    public UiRect Shrink(float dx, float dy)
    {
        float cx = (X0 + X1) * 0.5f, cy = (Y0 + Y1) * 0.5f;
        return new UiRect(MathF.Min(X0 + dx, cx), MathF.Min(Y0 + dy, cy), MathF.Max(X1 - dx, cx), MathF.Max(Y1 - dy, cy));
    }

    public UiRect Expand(float d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);

    public UiRect Offset(float dx, float dy) => new(X0 + dx, Y0 + dy, X1 + dx, Y1 + dy);

    // ── нарезка: полоса от края и остаток (полоса не выходит за прямоугольник) ──
    //
    // Сначала копия себя, остаток пишется последним. `this` у readonly-структуры — ссылка на переменную
    // вызывающего, а привычный вызов `r.SplitTop(h, out r)` передаёт ту же переменную в out: запись
    // остатка меняла `this`, и полоса возвращалась уже от остатка.

    public UiRect SplitTop(float height, out UiRect rest)
    {
        var self = this;
        float y = MathF.Min(self.Y0 + MathF.Max(height, 0), self.Y1);
        rest = self with { Y0 = y };
        return self with { Y1 = y };
    }

    public UiRect SplitBottom(float height, out UiRect rest)
    {
        var self = this;
        float y = MathF.Max(self.Y1 - MathF.Max(height, 0), self.Y0);
        rest = self with { Y1 = y };
        return self with { Y0 = y };
    }

    public UiRect SplitLeft(float width, out UiRect rest)
    {
        var self = this;
        float x = MathF.Min(self.X0 + MathF.Max(width, 0), self.X1);
        rest = self with { X0 = x };
        return self with { X1 = x };
    }

    public UiRect SplitRight(float width, out UiRect rest)
    {
        var self = this;
        float x = MathF.Max(self.X1 - MathF.Max(width, 0), self.X0);
        rest = self with { X1 = x };
        return self with { X0 = x };
    }
}
