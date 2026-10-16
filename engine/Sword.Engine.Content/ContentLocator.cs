using Sword.Engine.Diagnostics;

namespace Sword.Engine.Content;

/// <summary>
/// Где игра берёт запечённый контент — только пак: <see cref="EnvVar"/>, иначе ближайший <see cref="PakName"/> в каталоге
/// exe или выше. Пак собирает менеджер ресурсов из <c>Content/</c>. Единственное место, где записан этот порядок.
/// </summary>
public static class ContentLocator
{
    public const string EnvVar = "SWORD_CONTENT";
    public const string PakName = "Content" + PakFormat.Extension;

    /// <summary>Как получить пак игры (команда — из корня репозитория).</summary>
    public const string BakeHint =
        "tools\\Sword.Assets\\bin\\Debug\\net10.0-windows10.0.22000.0\\sword-assets.exe pack " +
        "(ресурсы Content/ — sword-bake --all, корни исходников — Bake/bake.user.json)";

    public static bool IsPak(string path) => path.EndsWith(PakFormat.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Путь пака: из <see cref="EnvVar"/> или первый найденный вверх от <paramref name="start"/>; null — нет.</summary>
    /// <exception cref="ArgumentException"><see cref="EnvVar"/> указывает не на .swpak.</exception>
    public static string? Find(string? start = null)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env)
            return IsPak(env) ? Path.GetFullPath(env) : throw new ArgumentException($"{EnvVar}={env}: ожидается файл пака *{PakFormat.Extension}");
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string pak = Path.Combine(dir.FullName, PakName);
            if (File.Exists(pak)) return pak;
        }
        return null;
    }

    /// <exception cref="FileNotFoundException">Пака нет — с подсказкой, откуда его взять.</exception>
    /// <exception cref="InvalidDataException">Пак испорчен.</exception>
    public static PakSource Open(string? start = null)
    {
        string path = Find(start) ?? throw new FileNotFoundException(
            $"нет пака контента: ни {PakName} в каталоге exe или выше, ни переменной {EnvVar}. Пак игры: {BakeHint}.");
        if (!File.Exists(path)) throw new FileNotFoundException($"{EnvVar}: нет пака {path}", path);
        return PakSource.Open(path);
    }
}
