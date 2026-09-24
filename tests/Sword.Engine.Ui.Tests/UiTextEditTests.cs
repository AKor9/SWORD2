namespace Sword.Engine.Ui.Tests;

/// <summary>Правка строки поля ввода без шрифтов (<c>UiTextEditTests</c> SWORD 2eab13b) и пары UTF-16.</summary>
public sealed class UiTextEditTests
{
    [Fact]
    public void InsertReplacesSelectionAndFlattensNewlines()
    {
        var e = new UiTextEdit();
        string t = "hello world";
        e.Anchor = 0;
        e.Caret = 5;
        t = e.Insert(t, "bye\r\nnow");
        Assert.Equal("bye now world", t);
        Assert.Equal(7, e.Caret);
        Assert.False(e.HasSelection);
    }

    [Fact]
    public void BackspaceWordStopsAtSeparator()
    {
        var e = new UiTextEdit();
        string t = "anim/1hm_sword  ";
        e.MoveTo(t, t.Length, extend: false);
        t = e.Backspace(t, word: true);
        Assert.Equal("anim/", t);
        Assert.Equal(5, e.Caret);
    }

    [Fact]
    public void DeleteAtEndDoesNothing()
    {
        var e = new UiTextEdit();
        string t = "abc";
        e.MoveTo(t, 3, extend: false);
        Assert.Equal("abc", e.Delete(t, word: false));
    }

    [Fact]
    public void LeftCollapsesSelectionToStart()
    {
        var e = new UiTextEdit { Anchor = 1, Caret = 4 };
        e.Left("abcdef", word: false, extend: false);
        Assert.Equal(1, e.Caret);
        Assert.Equal(1, e.Anchor);
    }

    [Fact]
    public void ShiftRightExtendsSelection()
    {
        var e = new UiTextEdit();
        e.Right("abcdef", word: false, extend: true);
        e.Right("abcdef", word: false, extend: true);
        Assert.Equal((0, 2), (e.SelectionStart, e.SelectionEnd));
    }

    [Fact]
    public void SelectWordTakesUnderscoresAndDashes()
    {
        var e = new UiTextEdit();
        e.SelectWord("load 1hm_sword-v2 now", 8);
        Assert.Equal("1hm_sword-v2", e.Selected("load 1hm_sword-v2 now"));
    }

    [Fact]
    public void ClampAfterExternalChange()
    {
        var e = new UiTextEdit { Caret = 10, Anchor = 12 };
        e.Clamp("abc");
        Assert.Equal(3, e.Caret);
        Assert.Equal(3, e.Anchor);
    }

    // ── пары UTF-16 (отличие от SWORD) ─────────────────────────────────

    private const string Emoji = "ab😀c";   // пара — индексы 2 и 3

    [Fact]
    public void BackspaceAndDelete_RemoveWholePair()
    {
        var e = new UiTextEdit();
        e.MoveTo(Emoji, 4, extend: false);
        Assert.Equal("abc", e.Backspace(Emoji, word: false));
        Assert.Equal(2, e.Caret);

        e.MoveTo(Emoji, 2, extend: false);
        Assert.Equal("abc", e.Delete(Emoji, word: false));
        Assert.Equal(2, e.Caret);
    }

    [Fact]
    public void ArrowsStepOverPair_CaretNeverInsidePair()
    {
        var e = new UiTextEdit();
        e.MoveTo(Emoji, 2, extend: false);
        e.Right(Emoji, word: false, extend: false);
        Assert.Equal(4, e.Caret);
        e.Left(Emoji, word: false, extend: false);
        Assert.Equal(2, e.Caret);

        e.MoveTo(Emoji, 3, extend: false);   // щелчок или код между половинками — каретка перед парой
        Assert.Equal(2, e.Caret);
        var outside = new UiTextEdit { Caret = 3, Anchor = 3 };
        outside.Clamp(Emoji);
        Assert.Equal((2, 2), (outside.Caret, outside.Anchor));
    }
}
