using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sword.Engine.Content;

/// <summary>
/// Из чего собран пак — служебный файл <see cref="EntryPath"/> внутри самого пака: по ассету обработчик, отпечаток
/// его кода, хеш параметров, входы (с размером, временем и хешем) и выходы (с хешем как в оглавлении).
/// Пишет пекарня последним файлом пака, поэтому сведения всегда описывают именно этот пак. Хеши оглавления
/// отвечают на вопрос «не испорчен ли пак», эти сведения — «не устарел ли он».
/// </summary>
public sealed class PakBuildInfo
{
    public const string EntryPath = ".bake/build.json";
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Имя проекта пекарни.</summary>
    public string Project { get; set; } = "";
    public DateTime BuiltUtc { get; set; }
    public Dictionary<string, PakAssetRecord> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сведения из пака; нет файла или другая версия — null.</summary>
    /// <exception cref="InvalidDataException">Файл сведений не разбирается.</exception>
    public static PakBuildInfo? Read(IContentSource content)
    {
        if (!content.Exists(EntryPath)) return null;
        try
        {
            var info = JsonSerializer.Deserialize<PakBuildInfo>(content.ReadAllBytes(EntryPath), Json);
            if (info is not { Version: CurrentVersion }) return null;
            info.Assets = new Dictionary<string, PakAssetRecord>(info.Assets, StringComparer.OrdinalIgnoreCase);
            return info;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{content.Describe(EntryPath)}: {e.Message}", e);
        }
    }

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>ID ассета, записавшего файл <paramref name="contentPath"/> (первый по порядку, кроме <paramref name="except"/>).</summary>
    public string? OwnerOf(string contentPath, string? except = null) =>
        Assets.Where(kv => !string.Equals(kv.Key, except, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(kv => kv.Value.Outputs.Any(o => string.Equals(o.Path, contentPath, StringComparison.OrdinalIgnoreCase))).Key;

    /// <summary>
    /// Исходник файла контента для редакторов: единственный входной файл ассета, который записал
    /// <paramref name="contentPath"/>. У ассета нет входов или их несколько — null: однозначного исходника нет.
    /// </summary>
    public string? SingleSourceOf(string contentPath) =>
        OwnerOf(contentPath) is { } owner && Assets[owner] is { InputDirs.Count: 0, Inputs.Count: 1 } rec ? rec.Inputs[0].Path : null;
}

public sealed class PakAssetRecord
{
    public string Processor { get; set; } = "";
    /// <summary>Отпечаток обработчика: ID, версия и код сборок, из которых он загружен.</summary>
    public string Fingerprint { get; set; } = "";
    public string ParamsHash { get; set; } = "";
    public DateTime BakedUtc { get; set; }
    /// <summary>Входные файлы: полные пути.</summary>
    public List<PakFileRecord> Inputs { get; set; } = [];
    /// <summary>Каталоги-входы: набор файлов под ними сверяется при проверке.</summary>
    public List<string> InputDirs { get; set; } = [];
    /// <summary>Входы из готового контента (выходы других ассетов): пути внутри пака.</summary>
    public List<string> ContentInputs { get; set; } = [];
    /// <summary>Выходы: пути внутри пака, хеш — как в оглавлении.</summary>
    public List<PakFileRecord> Outputs { get; set; } = [];
}

public sealed class PakFileRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Время изменения (тики UTC) — только у входов.</summary>
    public long Mtime { get; set; }
    public string Hash { get; set; } = "";
}
