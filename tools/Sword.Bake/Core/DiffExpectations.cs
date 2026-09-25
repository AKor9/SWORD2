using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sword.Bake;

/// <summary>Что ожидается у расхождения: файл или ассет есть только в одном паке — или есть в обоих, но отличается.</summary>
public enum ExpectKind
{
    /// <summary>Только в одном паке: файл, ассет сведений о сборке, выход ассета.</summary>
    Absent,
    /// <summary>Есть в обоих, но отличается: размер или содержимое файла, поле записи ассета в сведениях о сборке.</summary>
    Different,
}

/// <summary>
/// Правило файла ожидаемых расхождений: путь в паке (<see cref="Path"/>) или ID ассета сведений о сборке (<see cref="Asset"/>)
/// — точный, папка (всё под ней) или маска со «*» (любые знаки, в том числе «/»), без учёта регистра.
/// </summary>
public sealed class ExpectationRule
{
    /// <summary>Номер правила в файле, с 1.</summary>
    public int Index { get; init; }
    public string? Path { get; init; }
    public string? Asset { get; init; }
    public ExpectKind Expect { get; init; }
    public string Reason { get; init; } = "";

    /// <summary>Правило файла пака: расхождения файлов и выходов ассетов в сведениях о сборке.</summary>
    public bool Matches(string path, ExpectKind kind) => Path is not null && Expect == kind && DiffExpectations.MaskMatches(path, Path);

    /// <summary>Правило ассета: расхождения его записи в сведениях о сборке.</summary>
    public bool MatchesAsset(string assetId, ExpectKind kind) => Asset is not null && Expect == kind && DiffExpectations.MaskMatches(assetId, Asset);

    public override string ToString() => $"{(Path is not null ? "path " + Path : "asset " + Asset)} ({DiffExpectations.Name(Expect)}): {Reason}";
}

/// <summary>
/// Файл ожидаемых расхождений <c>sword-bake diff --expect</c>: JSON-объект с одним полем <c>expected</c> — списком правил
/// <c>{ "path" | "asset": маска, "expect": "absent" | "different", "reason": "почему" }</c>. Незнакомое поле, оба или ни
/// одного из <c>path</c>/<c>asset</c>, пустая причина — ошибка разбора. Первое подходящее правило делает расхождение
/// ожидаемым; правило, не подошедшее ни к одному, diff называет (список устарел).
/// </summary>
public sealed class DiffExpectations
{
    public string Source { get; }
    public IReadOnlyList<ExpectationRule> Rules { get; }

    private DiffExpectations(string source, IReadOnlyList<ExpectationRule> rules)
    {
        Source = source;
        Rules = rules;
    }

    public static DiffExpectations None { get; } = new("", []);

    /// <exception cref="InvalidDataException">Файл не разбирается или правило неполное.</exception>
    /// <exception cref="FileNotFoundException">Нет файла.</exception>
    public static DiffExpectations Load(string file)
    {
        file = System.IO.Path.GetFullPath(file);
        if (!File.Exists(file)) throw new FileNotFoundException($"нет файла ожидаемых расхождений {file}", file);
        return Parse(File.ReadAllText(file), file);
    }

    /// <param name="source">Откуда текст — для ошибок.</param>
    /// <exception cref="InvalidDataException">Текст не разбирается или правило неполное.</exception>
    public static DiffExpectations Parse(string json, string source)
    {
        Shape shape;
        try
        {
            shape = JsonSerializer.Deserialize<Shape>(json, AssetManifest.ParamsJson) ?? throw new InvalidDataException($"{source}: пусто");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{source}: {e.Message}", e);
        }
        if (shape.Expected is null) throw new InvalidDataException($"{source}: нет поля expected — списка правил");
        var rules = new List<ExpectationRule>();
        for (int i = 0; i < shape.Expected.Count; i++)
        {
            var r = shape.Expected[i] ?? throw new InvalidDataException($"{source}: правило {i + 1}: пусто");
            string where = $"{source}: правило {i + 1}";
            if ((r.Path is null) == (r.Asset is null)) throw new InvalidDataException($"{where}: нужно ровно одно из path (файл пака) и asset (ассет сведений о сборке)");
            string mask = (r.Path ?? r.Asset)!.Replace('\\', '/').Trim('/');
            if (mask.Length == 0) throw new InvalidDataException($"{where}: пустая маска");
            ExpectKind expect = r.Expect switch
            {
                "absent" => ExpectKind.Absent,
                "different" => ExpectKind.Different,
                _ => throw new InvalidDataException($"{where}: expect «{r.Expect}» — ожидается absent (только в одном паке) или different (отличается)"),
            };
            if (string.IsNullOrWhiteSpace(r.Reason)) throw new InvalidDataException($"{where}: нужна причина (reason)");
            rules.Add(new ExpectationRule
            {
                Index = i + 1,
                Path = r.Path is null ? null : mask,
                Asset = r.Asset is null ? null : mask,
                Expect = expect,
                Reason = r.Reason.Trim(),
            });
        }
        return new DiffExpectations(source, rules);
    }

    /// <summary>Первое правило файла пака для расхождения вида <paramref name="kind"/>; нет — null.</summary>
    public ExpectationRule? ForPath(string path, ExpectKind kind) => Rules.FirstOrDefault(r => r.Matches(path, kind));

    /// <summary>Первое правило ассета для расхождения вида <paramref name="kind"/>; нет — null.</summary>
    public ExpectationRule? ForAsset(string assetId, ExpectKind kind) => Rules.FirstOrDefault(r => r.MatchesAsset(assetId, kind));

    /// <summary>Как в шаблонах проекта: точный путь, папка (всё под ней) или маска со «*» (любые знаки), без учёта регистра.</summary>
    public static bool MaskMatches(string path, string mask)
    {
        if (mask.Contains('*'))
            return Regex.IsMatch(path, "^" + Regex.Escape(mask).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return path.Equals(mask, StringComparison.OrdinalIgnoreCase) || path.StartsWith(mask + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Имя вида в файле и в итоге: <c>absent</c>, <c>different</c>.</summary>
    public static string Name(ExpectKind kind) => kind == ExpectKind.Absent ? "absent" : "different";

    private sealed class Shape
    {
        public List<RuleShape?>? Expected { get; set; }
    }

    private sealed class RuleShape
    {
        public string? Path { get; set; }
        public string? Asset { get; set; }
        public string? Expect { get; set; }
        public string? Reason { get; set; }
    }
}
