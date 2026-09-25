using System.Diagnostics;
using System.Text;

namespace Sword.Bake.Tests;

/// <summary>Пути собранного решения для тестов плагинов и CLI: той же конфигурации и TFM, что сами тесты.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string Output => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>Debug или Release — по каталогу тестов bin/&lt;конфигурация&gt;/&lt;TFM&gt;.</summary>
    public static string Configuration => new DirectoryInfo(Output).Parent!.Name;

    public static string TargetFramework => new DirectoryInfo(Output).Name;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException($"не найден SWORD.slnx выше {AppContext.BaseDirectory}");
    }

    /// <summary>Сборка тестового плагина (tests/&lt;имя&gt;/bin/&lt;конфигурация&gt;/&lt;имя&gt;.dll — выход плагина без TFM).</summary>
    public static string Plugin(string name)
    {
        string path = Path.Combine(RepoRoot, "tests", name, "bin", Configuration, name + ".dll");
        Assert.True(File.Exists(path), $"нет {path} — соберите решение ({Configuration})");
        return path;
    }

    /// <summary>sword-bake.exe из выхода tools/Sword.Bake.Cli той же конфигурации.</summary>
    public static string Cli()
    {
        string path = Path.Combine(RepoRoot, "tools", "Sword.Bake.Cli", "bin", Configuration, TargetFramework, "sword-bake.exe");
        Assert.True(File.Exists(path), $"нет {path} — соберите решение ({Configuration})");
        return path;
    }

    /// <summary>Запустить sword-bake: код выхода, stdout и stderr (UTF-8). Не дольше минуты.</summary>
    public static (int Exit, string Out, string Err) RunCli(params string[] args) => RunCliIn(null, args);

    /// <summary>
    /// Запустить sword-bake в каталоге <paramref name="workingDirectory"/> (null — текущий каталог тестов): проект без
    /// --project ищется от него вверх, поэтому «вне проекта» — только во временном каталоге.
    /// </summary>
    public static (int Exit, string Out, string Err) RunCliIn(string? workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(Cli())
        {
            WorkingDirectory = workingDirectory ?? "",
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
        if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"sword-bake {string.Join(' ', args)}: не закончил за минуту");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
