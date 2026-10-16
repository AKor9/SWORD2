using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sword.Assets.Tests;

/// <summary>Ключи окна: снимок — без фокуса всегда; SWORD_NO_ACTIVATE — умолчание, --activate/--no-activate главнее; команды pack и list.</summary>
public sealed class OptionsTests
{
    [Fact]
    public void Shot_NeverActivates_CommandsParse()
    {
        Assert.True(Program.Options.Parse([]).Activate);
        Assert.False(Program.Options.Parse(["--shot", "a.png", "--activate"]).Activate);
        Assert.False(Program.Options.Parse([], noActivate: "1").Activate);
        Assert.True(Program.Options.Parse(["--activate"], noActivate: "true").Activate);
        Assert.True(Program.Options.Parse([], noActivate: "0").Activate);
        Assert.False(Program.Options.Parse(["--no-activate"]).Activate);
        var o = Program.Options.Parse(["--windowed", "800x600", "--select", "animations/katana", "--tab", "1"]);
        Assert.Equal((800, 600, "animations/katana", 1), (o.Width, o.Height, o.Select, o.Tab));
        Assert.Equal("pack", Program.Options.Parse(["pack", "--out", "x.swpak"]).Command);
        Assert.Null(Program.Options.Parse(["--select", "pack"]).Command);
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--windowed", "800"]));
    }
}

/// <summary>
/// Окно (категория Gpu): <c>--shot</c> на временных Content/ и Bake/ — снимок записан, код выхода 0, и пока процесс жив,
/// ни одно его окно не было активным (фокус пользователя не отнят).
/// </summary>
[Trait("Category", "Gpu")]
public sealed class WindowTests : IDisposable
{
    private readonly string _root = AssetsTestEnv.NewTemp("shot");

    public void Dispose() => AssetsTestEnv.Delete(_root);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void Shot_WritesPng_WithoutTakingFocus(string tab)
    {
        string content = Path.Combine(_root, "Content"), bake = Path.Combine(_root, "Bake");
        Directory.CreateDirectory(Path.Combine(content, "data"));
        Directory.CreateDirectory(bake);
        File.WriteAllText(Path.Combine(content, "data", "note.json"), "{ \"x\": 1 }");
        string shot = Path.Combine(_root, "shot.png");
        int foreground = 0, samples = 0;
        void Watch(Process p)
        {
            // пока окно живо — окно переднего плана не его
            while (!p.HasExited)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == (uint)p.Id) foreground++;
                samples++;
                Thread.Sleep(20);
            }
        }
        var (exit, output) = AssetsTestEnv.Run(AssetsTestEnv.Exe("Sword.Assets", "sword-assets.exe"), _root, TimeSpan.FromMinutes(2), Watch,
            "--content", content, "--bake", bake, "--windowed", "1024x640", "--select", "data/note.json", "--tab", tab, "--shot", shot);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(shot), output);
        Assert.Contains("[assets] снимок", output);
        var png = File.ReadAllBytes(shot);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(1024, png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]);   // IHDR: ширина
        Assert.True(samples > 0);
        Assert.Equal(0, foreground);
    }
}
