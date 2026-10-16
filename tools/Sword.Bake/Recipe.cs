using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sword.Bake;

/// <summary>
/// Рецепт сырого файла — <c>recipes/&lt;корень&gt;/&lt;путь от корня&gt;.bake.json</c>: каким конвертером и с какими параметрами
/// он печётся. Пишется при первой выпечке (параметры спрошены или по умолчанию), дальше выпечка идёт по нему без вопросов.
/// </summary>
public sealed class Recipe
{
    public const string Extension = ".bake.json";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>ID конвертера (<see cref="IConverter.Id"/>).</summary>
    public string Converter { get; set; } = "";
    /// <summary>Происхождение: ключ <c>Bake/imports.json</c> (лицензии).</summary>
    public string? Import { get; set; }
    public string? Notes { get; set; }
    public JsonObject Params { get; set; } = [];

    /// <exception cref="InvalidDataException">Файл не разбирается.</exception>
    public static Recipe Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: пусто");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    public void Save(string path) => TextFiles.Write(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Параметры типа конвертера.</summary>
    /// <exception cref="InvalidDataException">Поле не того типа.</exception>
    public object ReadParams(Type type)
    {
        try
        {
            return Params.Deserialize(type, Json) ?? Activator.CreateInstance(type)!;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"params: {e.Message}", e);
        }
    }

    /// <summary>Параметры из объекта: все поля, кроме пустых.</summary>
    public static JsonObject WriteParams(object parameters) =>
        JsonSerializer.SerializeToNode(parameters, parameters.GetType(), Json)!.AsObject();

    /// <summary>
    /// Задать поле параметров из текста: JSON (число, true, массив, объект) или строка как есть. Имя — как в рецепте
    /// (camelCase); вложенное — через точку: <c>blood.handleEnd=25</c>.
    /// </summary>
    public void Set(string name, string value)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            node = JsonValue.Create(value);
        }
        var parts = name.Split('.');
        JsonObject target = Params;
        foreach (var part in parts[..^1])
        {
            if (target[part] is not JsonObject child) target[part] = child = [];
            target = child;
        }
        target[parts[^1]] = node;
    }
}
