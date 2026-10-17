using System.Numerics;
using Sword.Engine.Platform;
using Sword.Engine.Ui;

namespace Sword.Engine.Client.Tests;

/// <summary>
/// Адаптер ввода интерфейса без окна: ввод платформы за кадр → <see cref="UiInput"/>; события не переживают кадр; курсор
/// интерфейса → курсор окна; без выделений.
/// </summary>
public sealed class UiInputSourceTests
{
    private readonly InputState _state = new();
    private readonly UiInputSource _source = new();

    private UiInput Fill(Action<InputState>? events = null, bool inside = true, double time = 1)
    {
        _state.BeginFrame();
        events?.Invoke(_state);
        _source.Fill(_state, inside, time);
        return _source.Input;
    }

    [Fact]
    public void MouseButtonsWheelAndPosition_AreCopied()
    {
        var ui = Fill(s =>
        {
            s.SetMousePosition(120, 45);
            s.SetMouseButton(MouseButton.Left, true);
            s.SetMouseButton(MouseButton.Right, true);
            s.SetMouseButton(MouseButton.Right, false);   // щелчок правой целиком в кадре
            s.AddWheel(-2);
        }, time: 3.5);

        Assert.Equal(new Vector2(120, 45), ui.Mouse);
        Assert.True(ui.MouseInside);
        Assert.Equal(3.5, ui.Time);
        Assert.Equal(-2, ui.Wheel);
        Assert.True(ui.Down(0) && ui.Pressed(0) && !ui.Released(0));
        Assert.True(!ui.Down(1) && ui.Pressed(1) && ui.Released(1));
        Assert.False(ui.Down(2) || ui.Pressed(2) || ui.Released(2));
        Assert.True(ui.HadEvents);

        // Следующий кадр без событий: нажатия и колесо сброшены, зажатая левая осталась, ввода не было.
        ui = Fill();
        Assert.True(ui.Down(0));
        Assert.False(ui.Pressed(0) || ui.Pressed(1) || ui.Released(1));
        Assert.Equal(0, ui.Wheel);
        Assert.False(ui.HadEvents);

        ui = Fill(s => s.SetMouseButton(MouseButton.Left, false));
        Assert.True(!ui.Down(0) && ui.Released(0) && ui.HadEvents);

        ui = Fill(inside: false);   // курсор ушёл из окна — тоже событие
        Assert.False(ui.MouseInside);
        Assert.True(ui.HadEvents);
    }

    [Fact]
    public void EditingKeys_PressAndRepeat_ModifiersAndTypedText()
    {
        var ui = Fill(s =>
        {
            s.SetKey((int)Key.Control, true);
            s.SetKey((int)Key.A, true);
            s.SetKey((int)Key.Enter, true);
            s.AddTypedChar('1');
            s.AddTypedChar('.');
            s.AddTypedChar('5');
            s.AddTypedChar('\b');   // управляющие — кодами клавиш, не текстом
        });
        Assert.True(ui.Ctrl && !ui.Shift && !ui.Alt);
        Assert.True(ui.Key(UiKey.A) && ui.Key(UiKey.Enter));
        Assert.False(ui.Key(UiKey.Backspace));
        Assert.Equal("1.5", ui.Typed.ToString());
        Assert.True(ui.HadEvents);

        // Удержание без повтора — не нажатие; автоповтор удержания — нажатие (Backspace стирает, стрелка листает).
        ui = Fill(s => s.SetKey((int)Key.Backspace, true));
        Assert.True(ui.Key(UiKey.Backspace));
        Assert.Equal("", ui.Typed.ToString());
        ui = Fill();
        Assert.False(ui.Key(UiKey.Backspace));
        ui = Fill(s => s.RepeatKey((int)Key.Backspace));
        Assert.True(ui.Key(UiKey.Backspace));

        // Клавиша не из набора виджетов (F3 — панели, W — игра) интерфейсу не видна и событием не считается.
        ui = Fill(s => s.SetKey((int)Key.F3, true));
        Assert.False(ui.HadEvents);
    }

    [Fact]
    public void EveryUiKey_HasPlatformKey()
    {
        var mapped = new HashSet<UiKey>();
        foreach (var key in Enum.GetValues<Key>())
        {
            var state = new InputState();
            state.SetKey((int)key, true);
            var source = new UiInputSource();
            source.Fill(state, true, 0);
            for (var k = (UiKey)0; k < UiKey.Count; k++)
                if (source.Input.Key(k)) mapped.Add(k);
        }
        Assert.Equal((int)UiKey.Count, mapped.Count);
    }

    [Fact]
    public void Cursor_MapsEveryShape()
    {
        Assert.Equal(CursorShape.Arrow, UiInputSource.Cursor(UiCursor.Arrow));
        Assert.Equal(CursorShape.IBeam, UiInputSource.Cursor(UiCursor.IBeam));
        Assert.Equal(CursorShape.SizeWE, UiInputSource.Cursor(UiCursor.SizeWE));
        Assert.Equal(CursorShape.SizeNS, UiInputSource.Cursor(UiCursor.SizeNS));
        Assert.Equal(CursorShape.SizeAll, UiInputSource.Cursor(UiCursor.SizeAll));
    }

    [Fact]
    public void Fill_AllocatesNothing()
    {
        void Events(InputState s)
        {
            s.SetMousePosition(s.MouseX + 1, 10);
            s.SetMouseButton(MouseButton.Left, !s.IsMouseDown(MouseButton.Left));
            s.SetKey((int)Key.Left, !s.IsDown(Key.Left));
            s.AddTypedChar('x');
            s.AddWheel(1);
        }
        Action<InputState> events = Events;
        for (int i = 0; i < 8; i++) Fill(events);   // прогрев: JIT, рост буфера символов
        _source.Input.Typed.Clear();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 120; i++) Fill(events);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
