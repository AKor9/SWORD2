namespace Sword.Engine.Content;

/// <summary>
/// Где игра берёт запечённый контент — только пак: переменная <see cref="EnvVar"/> (путь к паку), иначе ближайший
/// в каталоге exe или выше — сначала <see cref="PakName"/>, затем эталонный <see cref="ReferencePakName"/>. Каталог
/// россыпью игра не читает. Единственное место, где записан этот порядок.
///
/// Эталонный пак SWORD (этап 0, docs/history/stage-0-1.md) — источник, пока пекарня не перенесена в SWORD2; собранный
/// пекарней <see cref="PakName"/> рядом его заменяет.
/// </summary>
public static class ContentLocator
{
    public const string EnvVar = "SWORD_CONTENT";
    public const string PakName = "Content" + PakFormat.Extension;
    public const string ReferencePakName = "Content.reference" + PakFormat.Extension;

    public static bool IsPak(string path) => path.EndsWith(PakFormat.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Путь пака: из <see cref="EnvVar"/> или первый найденный вверх от <paramref name="start"/>; null — нет.</summary>
    /// <exception cref="ArgumentException"><see cref="EnvVar"/> указывает не на .swpak.</exception>
    public static string? Find(string? start = null)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env)
            return IsPak(env) ? Path.GetFullPath(env) : throw new ArgumentException($"{EnvVar}={env}: ожидается файл пака *{PakFormat.Extension}");
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (string name in (ReadOnlySpan<string>)[PakName, ReferencePakName])
            {
                string pak = Path.Combine(dir.FullName, name);
                if (File.Exists(pak)) return pak;
            }
        }
        return null;
    }

    /// <exception cref="FileNotFoundException">Пака нет — с подсказкой, откуда его взять.</exception>
    /// <exception cref="InvalidDataException">Пак испорчен.</exception>
    public static PakSource Open(string? start = null)
    {
        string path = Find(start) ?? throw new FileNotFoundException(
            $"нет пака контента: ни {PakName}, ни {ReferencePakName} в каталоге exe или выше, ни переменной {EnvVar}. " +
            "Эталонный пак закрепляет tools/scripts/reference.ps1 -Pin.");
        return File.Exists(path) ? PakSource.Open(path) : throw new FileNotFoundException($"{EnvVar}: нет пака {path}", path);
    }
}
