using System.Text.Json;

namespace Sword.Bake;

/// <summary>
/// Импорт — партия исходников из одной коллекции (покупной пак, гарнитура, свой контент), выгруженная одним
/// конвейером. Несёт происхождение и лицензию; манифест ссылается на импорт полем <c>import</c>, поэтому
/// происхождение записано один раз и существует данными, а не прозой в notes.
/// </summary>
public sealed class ImportRecord
{
    /// <summary>Название коллекции: «Combat Master Anim Bundle».</summary>
    public string Collection { get; set; } = "";
    /// <summary>Откуда взята: магазин, автор, «свой».</summary>
    public string Source { get; set; } = "";
    public string Url { get; set; } = "";
    public string License { get; set; } = "";
    /// <summary>Тексты лицензии (<c>${корень}/…</c>): пропавший файл — проблема проекта.</summary>
    public List<string> LicenseFiles { get; set; } = [];
    /// <summary>Конвейер выгрузки короткими метками: «ue4_to_ue5_manny», «ue_mesh_export».</summary>
    public List<string> Pipeline { get; set; } = [];
    public string Notes { get; set; } = "";
}

/// <summary>
/// Реестр импортов — <c>bake.imports.json</c> рядом с файлом проекта (в git): объект «ID импорта → запись».
/// Происхождение не входит в хеш параметров: правка лицензии ничего не пересобирает.
/// </summary>
public static class ImportRegistry
{
    public const string FileName = "bake.imports.json";

    private static readonly JsonSerializerOptions Json = new(AssetManifest.Json)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Реестр проекта; файла нет — пусто.</summary>
    /// <exception cref="InvalidDataException">Файл не разбирается.</exception>
    public static IReadOnlyDictionary<string, ImportRecord> Load(string projectDir)
    {
        string file = Path.Combine(projectDir, FileName);
        if (!File.Exists(file)) return new Dictionary<string, ImportRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, ImportRecord>>(File.ReadAllText(file), Json) ?? [];
            foreach (var id in map.Keys)
                if (!BakeProject.IsValidAssetId(id) || id.Contains('/'))
                    throw new InvalidDataException($"{file}: ID импорта «{id}» — одно слово из a-z, 0-9, _ . -");
            return new Dictionary<string, ImportRecord>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{file}: {e.Message}", e);
        }
    }
}

/// <summary>
/// Поле параметров — ссылка на другую единицу сборки (её ID, «skeletons/ue5_manny»): общая инфраструктура (риг,
/// скелет) описывается один раз. Обработчик читает параметры цели через <see cref="BakeContext.Ref{T}"/> и сам
/// регистрирует её исходники своими входами; хеш параметров ссылающегося включает параметры цели. Ссылка — не
/// зависимость сборки: цель не обязана собираться раньше, и её пересборка не пересобирает ссылающихся.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AssetRefAttribute(params string[] processors) : Attribute
{
    /// <summary>Обработчики, чьи единицы годятся целью; пусто — любые.</summary>
    public IReadOnlyList<string> Processors { get; } = processors;
}

/// <summary>Ссылка единицы на другую: поле, ID цели, найденная цель и что с ней не так (null — в порядке).</summary>
public sealed record AssetReference(string Field, string TargetId, AssetManifest? Target, string? Problem);

/// <summary>Замечание о происхождении: единица без импорта, неизвестный импорт, пропавший файл лицензии.</summary>
public sealed record ProvenanceIssue(string? AssetId, string Text);
