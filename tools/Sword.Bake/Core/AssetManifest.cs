using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sword.Bake;

/// <summary>
/// Описание ассета — <c>&lt;каталог ассетов проекта&gt;/&lt;id&gt;.bake.json</c>: какой обработчик, с какими параметрами,
/// после каких ассетов. Простой JSON без комментариев (его переписывает Studio); пояснения — в поле <see cref="Notes"/>.
/// </summary>
public sealed class AssetManifest
{
    public const string Extension = ".bake.json";

    /// <summary>Путь манифеста от каталога ассетов без расширения, через «/»: «anim/1hm_sword».</summary>
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Processor { get; init; }
    public string Notes { get; init; } = "";
    /// <summary>ID импорта из <see cref="ImportRegistry"/> — откуда взяты исходники; пусто — не указано.</summary>
    public string Import { get; init; } = "";
    public JsonElement Params { get; init; }
    /// <summary>ID ассетов, которые запекаются раньше (кроме найденных по входам — см. <see cref="Builder"/>).</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // кириллица читаемой
    };

    /// <summary>Параметры обработчика: опечатка в имени поля — ошибка, а не молча пропущенный ключ.</summary>
    internal static readonly JsonSerializerOptions ParamsJson = new(Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private sealed class Shape
    {
        public string Processor { get; set; } = "";
        public string? Notes { get; set; }
        public string? Import { get; set; }
        public JsonElement Params { get; set; }
        public List<string>? DependsOn { get; set; }
    }

    public static AssetManifest Load(string file, string assetsDir)
    {
        Shape shape;
        try
        {
            shape = JsonSerializer.Deserialize<Shape>(System.IO.File.ReadAllText(file), Json)
                    ?? throw new InvalidDataException($"{file}: пустой манифест");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{file}: {e.Message}", e);
        }
        if (shape.Processor.Length == 0) throw new InvalidDataException($"{file}: нет поля processor");
        string rel = Path.GetRelativePath(assetsDir, file).Replace('\\', '/');
        return new AssetManifest
        {
            Id = rel[..^Extension.Length],
            File = Path.GetFullPath(file),
            Processor = shape.Processor,
            Notes = shape.Notes ?? "",
            Import = shape.Import ?? "",
            Params = shape.Params.ValueKind == JsonValueKind.Undefined ? default : shape.Params.Clone(),
            DependsOn = shape.DependsOn ?? [],
        };
    }

    /// <summary>Параметры, разобранные в класс обработчика.</summary>
    /// <exception cref="InvalidDataException">Поле неизвестно или не того типа.</exception>
    public object ReadParams(IAssetProcessor processor)
    {
        try
        {
            return (Params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                       ? Activator.CreateInstance(processor.ParamsType)
                       : Params.Deserialize(processor.ParamsType, ParamsJson))
                   ?? throw new InvalidDataException($"{File}: пустые params");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{File}: params — {e.Message}", e);
        }
    }
}
