using System.Numerics;

namespace Sword.Engine.Ui.Tests;

/// <summary>Нарезка прямоугольников, обрезка списка квадов и вызовы рисования.</summary>
public sealed class UiLayoutTests
{
    [Fact]
    public void SplitTopKeepsRest()
    {
        var area = new UiRect(0, 0, 100, 50);
        var bar = area.SplitTop(20, out area);
        Assert.Equal(new UiRect(0, 0, 100, 20), bar);
        Assert.Equal(new UiRect(0, 20, 100, 50), area);
    }

    [Fact]
    public void SplitLargerThanRectClamps()
    {
        var r = new UiRect(0, 0, 10, 10);
        var left = r.SplitLeft(40, out var rest);
        Assert.Equal(r, left);
        Assert.True(rest.IsEmpty);
    }

    [Fact]
    public void IntersectOfDisjointIsEmpty()
    {
        var a = new UiRect(0, 0, 10, 10);
        var b = new UiRect(20, 20, 30, 30);
        Assert.True(a.Intersect(b).IsEmpty);
        Assert.False(a.Overlaps(b));
    }

    [Fact]
    public void ShrinkNeverInverts()
    {
        var r = new UiRect(0, 0, 10, 4).Shrink(6);
        Assert.True(r.Width >= 0 && r.Height >= 0);
    }

    [Fact]
    public void ClipRunsRecordChangesOnly()
    {
        var d = new UiDrawList();
        d.Rect(0, 0, 10, 10, 1);
        d.PushClip(new UiRect(0, 0, 50, 50));
        d.Rect(5, 5, 20, 20, 1);
        d.PushClip(new UiRect(10, 10, 100, 100));   // пересечение: (10,10)–(50,50)
        d.PopClip();
        d.PopClip();
        d.Rect(0, 0, 10, 10, 1);

        Assert.Equal(3, d.Count);
        Assert.Equal(
            [new UiDrawList.ClipRun(1, new UiRect(0, 0, 50, 50)), new UiDrawList.ClipRun(2, UiDrawList.NoClip)],
            d.ClipRuns);
    }

    [Fact]
    public void QuadsOutsideClipAreSkipped()
    {
        var d = new UiDrawList();
        d.PushClip(new UiRect(0, 0, 10, 10));
        d.Rect(20, 20, 30, 30, 1);
        d.Circle(new Vector2(100, 100), 5, 1);
        d.Rect(5, 5, 15, 15, 1);
        d.PopClip();
        Assert.Equal(1, d.Count);
    }

    [Fact]
    public void AppendKeepsOverlayClipAndRestores()
    {
        var main = new UiDrawList();
        main.PushClip(new UiRect(0, 0, 100, 100));
        main.Rect(0, 0, 10, 10, 1);

        var overlay = new UiDrawList();
        overlay.Rect(0, 0, 10, 10, 2);
        overlay.PushClip(new UiRect(0, 0, 5, 5));
        overlay.Rect(0, 0, 10, 10, 2);
        overlay.PopClip();

        main.Append(overlay);
        main.Rect(0, 0, 10, 10, 1);

        Assert.Equal(4, main.Count);
        Assert.Equal(
            [
                new UiDrawList.ClipRun(0, new UiRect(0, 0, 100, 100)),
                new UiDrawList.ClipRun(1, UiDrawList.NoClip),
                new UiDrawList.ClipRun(2, new UiRect(0, 0, 5, 5)),
                new UiDrawList.ClipRun(3, new UiRect(0, 0, 100, 100)),
            ],
            main.ClipRuns);
    }

    [Fact]
    public void ListWithoutFonts_DrawsShapes_RefusesText()
    {
        var d = new UiDrawList();
        d.Rect(0, 0, 10, 10, 1);
        Assert.False(d.HasFonts);
        Assert.Throws<InvalidOperationException>(() => d.Text(UiFace.Body, 14, Vector2.Zero, 1, "x"));
        Assert.Equal(0f, d.Text(UiFace.Body, 14, Vector2.Zero, 1, ""));   // пустая строка шрифт не трогает
    }

    [Fact]
    public void Shapes_FillQuadFields_AsShaderExpects()
    {
        var d = new UiDrawList();
        d.RectGradient(10, 20, 30, 60, 0xFF0000FF, 0xFF00FF00, radius: 50);
        d.Line(new Vector2(0, 0), new Vector2(10, 0), 2, 7);
        var q = d.Quads;
        // Прямоугольник: центр, половина с каймой в пиксель, радиус не больше полустороны, градиент по вертикали.
        Assert.Equal(UiMode.Box, q[0].Mode);
        Assert.Equal(new Vector2(20, 40), q[0].Centre);
        Assert.Equal(new Vector2(11, 21), q[0].HalfSize);
        Assert.Equal(new Vector2(10, 20), q[0].Uv0);
        Assert.Equal(new Vector4(10, 0, 0, 1), q[0].Shape);
        Assert.Equal((0xFF0000FFu, 0xFF00FF00u), (q[0].ColourA, q[0].ColourB));
        // Линия — повёрнутый скруглённый прямоугольник вдоль оси.
        Assert.Equal(new Vector2(1, 0), q[1].Axis);
        Assert.Equal(new Vector2(5, 0), q[1].Centre);
    }

    [Fact]
    public void Split_GroupsByPipelineAndClip_ClampsScissorToTarget()
    {
        var d = new UiDrawList();
        d.Rect(0, 0, 10, 10, 1);
        d.Rect(20, 0, 30, 10, 1);                          // тот же конвейер, та же обрезка — тот же вызов
        d.PushClip(new UiRect(-5.5f, 2.2f, 50.1f, 700));   // дробные края — наружу, за целью — зажать
        d.Rect(0, 5, 10, 10, 1);
        d.PopClip();
        d.Rect(0, 0, 5, 5, 1);

        var batches = new List<UiBatch>();
        d.Split(640, 480, batches);
        Assert.Equal(
            [
                new UiBatch(0, 2, false, 0, 0, 640, 480),
                new UiBatch(2, 1, false, 0, 2, 51, 480),
                new UiBatch(3, 1, false, 0, 0, 640, 480),
            ],
            batches);
    }

    [Fact]
    public void Split_SkipsBatchWhoseClipIsOutsideTarget()
    {
        var d = new UiDrawList();
        d.PushClipReplace(new UiRect(700, 0, 800, 100));   // замена обрезки: квад виден в ней, но не в цели
        d.Rect(710, 10, 720, 20, 1);
        d.PopClip();
        var batches = new List<UiBatch>();
        d.Split(640, 480, batches);
        Assert.Empty(batches);
        Assert.Equal(1, d.Count);
    }
}
