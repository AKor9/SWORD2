using Sword.Engine.Platform;

namespace Sword.Engine.Platform.Tests;

/// <summary>
/// Ввод кадра: фронты против уровней. Каждый тест — случай, на котором SWORD однажды терял нажатие или залипал.
/// </summary>
public sealed class InputStateTests
{
    [Fact]
    public void KeyPressedAndReleasedWithinFrame_StillCountsAsPressed()
    {
        var input = new InputState();
        input.BeginFrame();
        input.SetKey((int)Key.F2, true);
        input.SetKey((int)Key.F2, false);

        Assert.True(input.WasPressed(Key.F2));
        Assert.False(input.IsDown(Key.F2));
    }

    [Fact]
    public void KeyRelease_IsAnEdge_OnlyFromHeldKey_ClearedNextFrame()
    {
        // Фронт отпускания клавиши (как у мыши): тап в одном кадре даёт и нажатие, и отпускание.
        var input = new InputState();
        input.BeginFrame();
        input.SetKey((int)Key.Space, true);
        input.SetKey((int)Key.Space, false);
        Assert.True(input.WasPressed(Key.Space) && input.WasReleased(Key.Space));

        input.BeginFrame();
        Assert.False(input.WasReleased(Key.Space));
        input.SetKey((int)Key.Space, false);   // отпускание не зажатой клавиши — не фронт
        Assert.False(input.WasReleased(Key.Space));

        input.SetKey((int)Key.Space, true);
        input.ForgetAll();                     // фокус потерян — фронты забыты
        Assert.False(input.WasReleased(Key.Space));
    }

    [Fact]
    public void BeginFrame_ClearsEdges_KeepsLevels()
    {
        var input = new InputState();
        input.SetKey((int)Key.W, true);
        input.BeginFrame();

        Assert.True(input.IsDown(Key.W));
        Assert.False(input.WasPressed(Key.W));
    }

    [Fact]
    public void HeldKeyDown_IsNotANewPress()
    {
        var input = new InputState();
        input.SetKey((int)Key.W, true);
        input.BeginFrame();
        input.SetKey((int)Key.W, true);

        Assert.False(input.WasPressed(Key.W));
    }

    [Fact]
    public void Repeat_IsVisibleOnlyToRepeatAwareQuery()
    {
        var input = new InputState();
        input.SetKey((int)Key.Backspace, true);
        input.BeginFrame();
        input.RepeatKey((int)Key.Backspace);

        Assert.False(input.WasPressed(Key.Backspace));
        Assert.True(input.WasPressedOrRepeated(Key.Backspace));
    }

    [Fact]
    public void LosingCapture_KeepsClickOfThisFrame()
    {
        // Окно само отпускает захват после щелчка, и система тут же присылает смену захвата — щелчок должен выжить.
        var input = new InputState();
        input.BeginFrame();
        input.SetMouseButton(MouseButton.Left, true);
        input.ForgetMouse();

        Assert.True(input.WasMousePressed(MouseButton.Left));
        Assert.False(input.IsMouseDown(MouseButton.Left));
    }

    [Fact]
    public void ReleaseAfterLostCapture_IsNotAClick()
    {
        var input = new InputState();
        input.SetMouseButton(MouseButton.Right, true);
        input.ForgetMouse();
        input.BeginFrame();
        input.SetMouseButton(MouseButton.Right, false);

        Assert.False(input.WasMouseReleased(MouseButton.Right));
    }

    [Fact]
    public void ReleaseOfHeldButton_IsAClick()
    {
        var input = new InputState();
        input.SetMouseButton(MouseButton.Right, true);
        input.BeginFrame();
        input.SetMouseButton(MouseButton.Right, false);

        Assert.True(input.WasMouseReleased(MouseButton.Right));
    }

    [Fact]
    public void ForgetAll_UnsticksKeys_AndPreventsMouseJump()
    {
        var input = new InputState();
        input.SetKey((int)Key.Shift, true);
        input.SetMousePosition(10, 10);
        input.ForgetAll();
        input.BeginFrame();
        input.SetMousePosition(900, 700);

        Assert.False(input.IsDown(Key.Shift));
        Assert.Equal(0, input.MouseDeltaX);
        Assert.Equal(0, input.MouseDeltaY);
    }

    [Fact]
    public void MouseDelta_AccumulatesWithinFrame_AndResetsNextFrame()
    {
        var input = new InputState();
        input.SetMousePosition(100, 100);
        input.BeginFrame();
        input.SetMousePosition(110, 95);
        input.SetMousePosition(115, 90);

        Assert.Equal(15, input.MouseDeltaX);
        Assert.Equal(-10, input.MouseDeltaY);

        input.BeginFrame();
        Assert.Equal(0, input.MouseDeltaX);
    }

    [Fact]
    public void AnchorMouse_MovesTheBase()
    {
        var input = new InputState();
        input.SetMousePosition(10, 10);
        input.AnchorMouse(400, 300);
        input.BeginFrame();
        input.SetMousePosition(405, 300);

        Assert.Equal(5, input.MouseDeltaX);
    }

    [Fact]
    public void Wheel_AccumulatesWithinFrame()
    {
        var input = new InputState();
        input.BeginFrame();
        input.AddWheel(1);
        input.AddWheel(-0.5f);

        Assert.Equal(0.5f, input.Wheel);
        input.BeginFrame();
        Assert.Equal(0, input.Wheel);
    }

    [Fact]
    public void TypedChars_SkipControlCharacters_AndDropUndrainedOnNextFrame()
    {
        var input = new InputState();
        input.AddTypedChar('a');
        input.AddTypedChar('\b');
        input.AddTypedChar((char)0x7F);
        input.AddTypedChar('ж');

        var typed = new List<char>();
        input.DrainTypedChars(typed.Add);
        Assert.Equal(['a', 'ж'], typed);

        input.AddTypedChar('b');
        input.BeginFrame();
        typed.Clear();
        input.DrainTypedChars(typed.Add);
        Assert.Empty(typed);
    }
}
