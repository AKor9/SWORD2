using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sword.Bake.Studio.Tests;

/// <summary>Ключи Studio: снимок и витрина — без фокуса всегда; SWORD_NO_ACTIVATE — умолчание, --activate/--no-activate главнее.</summary>
public sealed class StudioOptionsTests
{
    [Fact]
    public void ShotAndGallery_NeverActivate()
    {
        Assert.True(Program.Options.Parse([]).Activate);
        Assert.False(Program.Options.Parse(["--shot", "a.png"]).Activate);
        Assert.False(Program.Options.Parse(["--gallery", "dir", "--activate"]).Activate);
        Assert.False(Program.Options.Parse([], noActivate: "1").Activate);
        Assert.True(Program.Options.Parse(["--activate"], noActivate: "true").Activate);
        Assert.True(Program.Options.Parse([], noActivate: "0").Activate);
        Assert.False(Program.Options.Parse(["--no-activate"]).Activate);
        var o = Program.Options.Parse(["--windowed", "800x600", "--select", "content:x", "--tab", "3", "--mouse", "10,20"]);
        Assert.Equal((800, 600, "content:x", 3), (o.Width, o.Height, o.Select, o.Tab));
        Assert.Equal(new System.Numerics.Vector2(10, 20), o.Mouse);
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--bogus"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--windowed", "800"]));
    }
}

/// <summary>
/// Studio с окном (категория Gpu): <c>--shot</c> на временном проекте — снимок записан, код выхода 0, и пока процесс жив,
/// ни одно его окно не было активным (фокус пользователя не отнят).
/// </summary>
[Trait("Category", "Gpu")]
public sealed class StudioWindowTests : IDisposable
{
    private readonly string _root = StudioTestEnv.NewTemp("shot");

    public void Dispose() => StudioTestEnv.Delete(_root);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [Fact]
    public void Shot_WritesPng_WithoutTakingFocus()
    {
        var project = BakeProject.Create(Path.Combine(_root, "project"), "Shot");
        File.WriteAllText(Path.Combine(project.Root, "note.txt"), "x");
        project.AddAsset("data/note", "file.copy", new Dictionary<string, string> { ["from"] = Path.Combine(project.Root, "note.txt") });
        string shot = Path.Combine(_root, "shot.png");
        int foreground = 0, samples = 0;
        void Watch(Process p)
        {
            // пока Studio жива — окно переднего плана не её
            while (!p.HasExited)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == (uint)p.Id) foreground++;
                samples++;
                Thread.Sleep(20);
            }
        }
        var (exit, output) = StudioTestEnv.Run(StudioTestEnv.Exe("Sword.Bake.Studio", "Sword.Bake.Studio.exe"), _root, TimeSpan.FromMinutes(2), Watch,
            "--project", project.Root, "--windowed", "1024x640", "--select", "data/note", "--shot", shot);
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(shot), output);
        Assert.Contains("[studio] снимок", output);
        var png = File.ReadAllBytes(shot);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(1024, png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]);   // IHDR: ширина
        Assert.True(samples > 0);
        Assert.Equal(0, foreground);
    }

    [Fact]
    public void Gallery_WritesEveryView_WithoutTakingFocus()
    {
        var project = BakeProject.Create(Path.Combine(_root, "project"), "Gallery");
        File.WriteAllText(Path.Combine(project.Root, "note.txt"), "x");
        project.AddAsset("data/note", "file.copy", new Dictionary<string, string> { ["from"] = Path.Combine(project.Root, "note.txt") });
        string dir = Path.Combine(_root, "gallery");
        int foreground = 0, samples = 0;
        void Watch(Process p)
        {
            while (!p.HasExited)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == (uint)p.Id) foreground++;
                samples++;
                Thread.Sleep(20);
            }
        }
        var (exit, output) = StudioTestEnv.Run(StudioTestEnv.Exe("Sword.Bake.Studio", "Sword.Bake.Studio.exe"), _root, TimeSpan.FromMinutes(2), Watch,
            "--project", project.Root, "--windowed", "1024x640", "--gallery", dir);
        Assert.True(exit == 0, output);
        var shots = Directory.GetFiles(dir, "*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Contains("01-project.png", shots);
        Assert.Contains(shots, s => s!.EndsWith("-unit-preview.png", StringComparison.Ordinal));
        Assert.Contains(shots, s => s!.EndsWith("-widgets.png", StringComparison.Ordinal));
        Assert.True(samples > 0);
        Assert.Equal(0, foreground);
    }
}
