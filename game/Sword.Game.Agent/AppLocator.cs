namespace Sword.Game.Agent;

/// <summary>
/// Sword.exe для видов и бюджетов фич (<c>feature.verify</c>, <c>Sword.Headless --feature</c>): App той же конфигурации сборки,
/// что Headless, — <c>game/Sword.Game.App/bin/&lt;конфигурация&gt;/…/Sword.exe</c> в корне репозитория. Конфигурация — по пути
/// Headless (папка за <c>bin</c>: <c>…\bin\Debug\…</c>); Headless, собранный вне <c>bin</c> (сервер агента
/// <c>tools/scripts/agent-headless.cmd</c> — <c>artifacts\agent-bin\headless\&lt;n&gt;</c>, где иначе виды отвечают «Sword.exe не
/// собран»), — по оптимизации его сборки: Release или Debug.
/// </summary>
public static class AppLocator
{
    /// <param name="root">Корень репозитория (папка с SWORD.slnx).</param>
    /// <param name="baseDirectory">Папка сборки Headless (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <param name="optimized">Headless собран с оптимизацией (Release): конфигурация, когда её не видно по пути.</param>
    /// <returns>Путь Sword.exe; не собран — null.</returns>
    public static string? Find(string root, string baseDirectory, bool optimized)
    {
        var parts = baseDirectory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int bin = Array.FindLastIndex(parts, p => string.Equals(p, "bin", StringComparison.OrdinalIgnoreCase));
        string configuration = bin >= 0 && bin + 1 < parts.Length ? parts[bin + 1] : optimized ? "Release" : "Debug";
        string dir = Path.Combine(root, "game", "Sword.Game.App", "bin", configuration);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "Sword.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }
}
