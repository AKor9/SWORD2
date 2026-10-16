using System.Diagnostics;
using System.Text;

namespace Sword.Assets.Tests;

/// <summary>Пути решения и временные каталоги для тестов менеджера ресурсов.</summary>
internal static class AssetsTestEnv
{
    public static readonly string RepoRoot = FindRepoRoot();

    private static string Output => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Debug или Release — по каталогу тестов bin/&lt;конфигурация&gt;/&lt;TFM&gt;.</summary>
    public static string Configuration => new DirectoryInfo(Output).Parent!.Name;

    public static string TargetFramework => new DirectoryInfo(Output).Name;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException($"не найден SWORD.slnx выше {AppContext.BaseDirectory}");
    }

    public static string Exe(string project, string name)
    {
        string path = Path.Combine(RepoRoot, "tools", project, "bin", Configuration, TargetFramework, name);
        Assert.True(File.Exists(path), $"нет {path} — соберите решение ({Configuration})");
        return path;
    }

    public static string NewTemp(string what)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sword-assets-tests", $"{what}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void PumpUntil(AssetsApp app, Func<bool> done, string what, double seconds = 120)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            app.Pump();
            if (done()) return;
            if (sw.Elapsed.TotalSeconds > seconds) Assert.Fail($"{what}: не дождались за {seconds} с");
            Thread.Sleep(10);
        }
    }

    public static (int Exit, string Output) Run(string exe, string workDir, TimeSpan timeout, Action<Process>? watch = null, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        watch?.Invoke(process);
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{Path.GetFileName(exe)} {string.Join(' ', args)}: не закончил за {timeout.TotalSeconds:F0} с");
        }
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
