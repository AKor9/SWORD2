using Sword.Game.App;

namespace Sword.Game.Tests;

/// <summary>
/// Фокус окна Sword.exe: переменная <c>SWORD_NO_ACTIVATE</c> (её задают настройки Claude Code проекта — агентские запуски
/// без фокуса) задаёт умолчание, ключи <c>--activate</c> / <c>--no-activate</c> главнее, последний из них действует.
/// </summary>
public class AppOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void NoActivateFlag_Values(string? value, bool noActivate) => Assert.Equal(noActivate, Program.NoActivateFlag(value));

    [Theory]
    [InlineData("yes")]
    [InlineData("2")]
    [InlineData("on")]
    public void NoActivateFlag_Unknown_IsError(string value)
    {
        var e = Assert.Throws<ArgumentException>(() => Program.NoActivateFlag(value));
        Assert.Contains(Program.NoActivateVariable, e.Message);
        Assert.Throws<ArgumentException>(() => Program.Parse([], value));
    }

    [Theory]
    // Ни переменной, ни ключа — окно с фокусом, как было.
    [InlineData(null, new string[0], true, "default")]
    [InlineData("0", new string[0], true, "default")]
    [InlineData(null, new[] { "--no-activate" }, false, "--no-activate")]
    // Переменная — умолчание без фокуса.
    [InlineData("1", new string[0], false, "SWORD_NO_ACTIVATE")]
    [InlineData("true", new[] { "--windowed", "640x480", "--frames", "10" }, false, "SWORD_NO_ACTIVATE")]
    // Ключ главнее переменной; из двух ключей — последний.
    [InlineData("1", new[] { "--activate" }, true, "--activate")]
    [InlineData("1", new[] { "--no-activate" }, false, "--no-activate")]
    [InlineData(null, new[] { "--no-activate", "--activate" }, true, "--activate")]
    [InlineData("1", new[] { "--activate", "--no-activate" }, false, "--no-activate")]
    // Полный экран и окно без рамы переменная не отменяет: неактивное окно — внизу порядка перекрытия (Win32Window).
    [InlineData("1", new[] { "--fullscreen" }, false, "SWORD_NO_ACTIVATE")]
    [InlineData("1", new[] { "--borderless" }, false, "SWORD_NO_ACTIVATE")]
    public void Activate_VariableIsDefault_KeysWin(string? variable, string[] args, bool activate, string by)
    {
        var o = Program.Parse(args, variable);
        Assert.Equal(activate, o.Activate);
        Assert.Equal(by, o.ActivateBy);
    }

    [Fact]
    public void Fullscreen_WithVariable_StaysFullscreen()
    {
        var o = Program.Parse(["--fullscreen"], "1");
        Assert.True(o.Fullscreen);
        Assert.False(o.Activate);
    }
}
