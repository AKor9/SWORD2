using Sword.Engine.Diagnostics;

namespace Sword.Engine.Content;

/// <summary>
/// Где игра берёт запечённый контент — только пак: переменная <see cref="EnvVar"/> (путь к паку), иначе ближайший
/// в каталоге exe или выше — сначала <see cref="PakName"/>, затем эталонный <see cref="ReferencePakName"/>. Каталог
/// россыпью игра не читает. Единственное место, где записан этот порядок.
///
/// Пак игры — <see cref="PakName"/> в корне репозитория: его печёт пекарня <c>Bake/</c> (вне git). Эталонный пак —
/// основа сверки пекарни (<c>bake diff</c>, <c>reference.ps1 -Check</c>). Найденный поиском вместо пака пекарни, он
/// работает, но не молча: <see cref="Open"/> пишет в журнал <see cref="ReferenceWarning"/> — реплеи и эталоны, записанные
/// на паке пекарни, на нём отвергаются по хешу контента.
/// </summary>
public static class ContentLocator
{
    public const string EnvVar = "SWORD_CONTENT";
    public const string PakName = "Content" + PakFormat.Extension;
    public const string ReferencePakName = "Content.reference" + PakFormat.Extension;

    /// <summary>Как получить пак игры (команда — из корня репозитория; рабочее дерево агента — корневой AGENTS.md).</summary>
    public const string BakeHint =
        "cd Bake; ..\\tools\\Sword.Bake.Cli\\bin\\Debug\\net10.0-windows10.0.22000.0\\sword-bake.exe build " +
        "(корни исходников — Bake/bake.user.json)";

    private static int _warned;

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

    /// <summary>
    /// Предупреждение, если <paramref name="path"/> — эталонный пак, найденный поиском (пака пекарни рядом нет); null — пак
    /// игры или пак, названный переменной <see cref="EnvVar"/> явно.
    /// </summary>
    public static string? ReferenceWarning(string path) =>
        Environment.GetEnvironmentVariable(EnvVar) is not { Length: > 0 }
        && string.Equals(Path.GetFileName(path), ReferencePakName, StringComparison.OrdinalIgnoreCase)
            ? $"контент — эталонный пак {path}: пака пекарни {PakName} нет; реплеи и эталоны, записанные на паке пекарни, " +
              $"здесь отвергаются по хешу контента. Пак игры: {BakeHint}"
            : null;

    /// <exception cref="FileNotFoundException">Пака нет — с подсказкой, откуда его взять.</exception>
    /// <exception cref="InvalidDataException">Пак испорчен.</exception>
    public static PakSource Open(string? start = null)
    {
        string path = Find(start) ?? throw new FileNotFoundException(
            $"нет пака контента: ни {PakName}, ни {ReferencePakName} в каталоге exe или выше, ни переменной {EnvVar}. Пак игры: {BakeHint}.");
        if (!File.Exists(path)) throw new FileNotFoundException($"{EnvVar}: нет пака {path}", path);
        if (ReferenceWarning(path) is { } warning && Interlocked.Exchange(ref _warned, 1) == 0) Log.Warning("content", warning);
        return PakSource.Open(path);
    }
}
