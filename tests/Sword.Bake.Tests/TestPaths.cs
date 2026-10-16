using System.Diagnostics;
using System.Text;

namespace Sword.Bake.Tests;

/// <summary>
/// Пути собранного решения: sword-bake той же конфигурации, что тесты; корень сырых исходников машины.
/// </summary>
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

    /// <summary>sword-bake.exe из выхода tools/Sword.Bake той же конфигурации.</summary>
    public static string Cli()
    {
        string path = Path.Combine(RepoRoot, "tools", "Sword.Bake", "bin", Configuration, TargetFramework, "sword-bake.exe");
        Assert.True(File.Exists(path), $"нет {path} — соберите решение ({Configuration})");
        return path;
    }

    /// <summary>
    /// Корень исходников выпечки (<c>${src}</c>): переменная <c>BAKE_ROOT_SRC</c> (её же читает пекарня), иначе
    /// <c>D:/Temp/GamedevAssets</c>. null — нет raw-экспорта локомоции: тесты Source пропускаются.
    /// </summary>
    public static string? SourceRoot()
    {
        string root = Environment.GetEnvironmentVariable("BAKE_ROOT_SRC") is { Length: > 0 } env ? env : "D:/Temp/GamedevAssets";
        return File.Exists(Path.Combine(root, "_Export", "skin_gasp", "manifest.json")) ? Path.GetFullPath(root) : null;
    }

    /// <summary>Запустить sword-bake в каталоге <paramref name="workDir"/>: код выхода, stdout и stderr (UTF-8).</summary>
    public static (int Exit, string Out, string Err) RunCli(string workDir, TimeSpan timeout, params string[] args)
    {
        var info = new ProcessStartInfo(Cli())
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
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"sword-bake {string.Join(' ', args)}: не закончил за {timeout.TotalSeconds:F0} с");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>Тест на настоящих исходниках выпечки (категория Source): без raw-экспорта в <see cref="TestPaths.SourceRoot"/> — пропуск.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SourceFactAttribute : FactAttribute
{
    public SourceFactAttribute()
    {
        if (TestPaths.SourceRoot() is null)
            Skip = "нет исходников выпечки (BAKE_ROOT_SRC или D:/Temp/GamedevAssets/_Export/skin_gasp)";
    }
}
