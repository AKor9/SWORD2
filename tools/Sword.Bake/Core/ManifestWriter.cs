using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Bake;

/// <summary>
/// Запись манифестов. <see cref="SaveParams"/> меняет только поля params, чьё значение отличается от записанного
/// в файле; остальные поля, их порядок и поля манифеста вне params (processor, import, notes, dependsOn) сохраняются.
/// Окончания строк файла сохраняются (<see cref="TextFiles"/>).
/// </summary>
public static class ManifestWriter
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static bool IsEmpty(JsonNode? n) => n is null || n is JsonArray { Count: 0 };

    /// <param name="baseline">Параметры, от которых начинались правки; по умолчанию — записанные в файле сейчас.
    /// С ним поля, изменённые в файле снаружи после начала правок, не затираются.</param>
    /// <returns>Файл изменён.</returns>
    /// <exception cref="InvalidDataException">Манифест не разбирается.</exception>
    public static bool SaveParams(AssetManifest asset, IAssetProcessor processor, object parameters, object? baseline = null)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(asset.File), documentOptions: ReadOptions) as JsonObject
                   ?? throw new InvalidDataException($"{asset.File}: манифест — не объект JSON");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{asset.File}: {e.Message}", e);
        }

        var before = (JsonObject)JsonSerializer.SerializeToNode(baseline ?? asset.ReadParams(processor), processor.ParamsType, AssetManifest.Json)!;
        var after = (JsonObject)JsonSerializer.SerializeToNode(parameters, processor.ParamsType, AssetManifest.Json)!;
        if (root["params"] is not JsonObject target)
        {
            target = new JsonObject();
            root["params"] = target;
        }

        bool changed = false;
        foreach (string key in before.Select(kv => kv.Key).Union(after.Select(kv => kv.Key)).ToList())
        {
            var oldValue = before[key];
            var newValue = after[key];
            if (JsonNode.DeepEquals(oldValue, newValue) || IsEmpty(oldValue) && IsEmpty(newValue)) continue;   // незаданный список = пустой
            // имя в файле могло быть записано в другом регистре (чтение без учёта регистра)
            string name = target.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (newValue is null) target.Remove(name);
            else target[name] = newValue.DeepClone();
            changed = true;
        }
        if (!changed) return false;
        TextFiles.WriteJson(asset.File, root);
        return true;
    }

    /// <summary>Новый манифест: processor, import, notes, params целиком.</summary>
    /// <exception cref="IOException">Файл уже есть.</exception>
    public static void Create(string file, IAssetProcessor processor, object parameters, string notes = "", IReadOnlyList<string>? dependsOn = null,
        string import = "")
    {
        if (File.Exists(file)) throw new IOException($"манифест {file} уже есть");
        var root = new JsonObject { ["processor"] = processor.Id };
        if (import.Length > 0) root["import"] = import;
        if (notes.Length > 0) root["notes"] = notes;
        root["params"] = JsonSerializer.SerializeToNode(parameters, processor.ParamsType, AssetManifest.Json);
        if (dependsOn is { Count: > 0 }) root["dependsOn"] = new JsonArray([.. dependsOn.Select(d => (JsonNode)d)]);
        TextFiles.WriteJson(file, root);
    }
}
