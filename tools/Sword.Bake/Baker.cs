using Sword.Bake.Converters;

namespace Sword.Bake;

/// <summary>
/// Пекарня: каталог пекарни <c>Bake/</c> (корни машины, рецепты) и каталог игровых ресурсов <c>Content/</c>. Сырой файл →
/// конвертер по виду файла (<see cref="Converters"/>) → рецепт → файлы в <c>Content/</c>. Ничего не отслеживает: печёт,
/// когда попросили.
/// </summary>
public sealed class Baker
{
    public const string RecipesDir = "recipes";

    /// <summary>Таблица видов файлов: порядок — порядок выбора в окне, когда файлу годятся несколько.</summary>
    public static IReadOnlyList<IConverter> Converters { get; } =
    [
        new AnimConverter(), new SkeletonConverter(), new WeaponConverter(), new CharacterMeshConverter(), new AnatomyConverter(),
        new TextureConverter(), new FontConverter(), new CopyConverter(),
    ];

    public string BakeDir { get; }
    public string ContentDir { get; }
    public SourceRoots Roots { get; }

    public Baker(string bakeDir, string contentDir)
    {
        BakeDir = Path.GetFullPath(bakeDir);
        ContentDir = Path.GetFullPath(contentDir);
        Roots = SourceRoots.Load(BakeDir);
    }

    /// <summary>Пекарня репозитория: <c>Bake/</c> и <c>Content/</c> в корне (каталог с SWORD.slnx) выше <paramref name="from"/>.</summary>
    /// <exception cref="DirectoryNotFoundException">Корня нет.</exception>
    public static Baker OfRepository(string from)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(from)); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx")))
                return new Baker(Path.Combine(dir.FullName, "Bake"), Path.Combine(dir.FullName, "Content"));
        throw new DirectoryNotFoundException($"не найден SWORD.slnx выше {from} — задайте --bake и --content");
    }

    public static IConverter? Find(string id) => Converters.FirstOrDefault(c => c.Id == id);

    /// <summary>Конвертеры, которым годится файл; пусто — «конвертера нет».</summary>
    public static IReadOnlyList<IConverter> Candidates(string file) => Converters.Where(c => c.Accepts(file)).ToList();

    /// <summary>Сообщение «конвертера нет» со списком известных видов.</summary>
    public static string NoConverter(string file) =>
        $"для файла {Path.GetFileName(file)} конвертера нет; известные виды: " +
        string.Join("; ", Converters.Select(c => $"{c.Kind} — {c.Title}"));

    /// <summary>Рецепт сырого файла: <c>Bake/recipes/&lt;корень&gt;/&lt;путь от корня&gt;.bake.json</c>.</summary>
    /// <exception cref="ArgumentException">Файл вне корней <c>bake.user.json</c>.</exception>
    public string RecipePath(string source)
    {
        var (root, rel) = Roots.Split(source)
            ?? throw new ArgumentException($"{source}: вне корней сырых исходников ({string.Join(", ", Roots.Roots.Select(r => $"${{{r.Key}}} = {r.Value}"))}) — " +
                                           $"добавьте корень в {Path.Combine(BakeDir, SourceRoots.UserFile)}");
        return Path.Combine(BakeDir, RecipesDir, root, rel.Replace('/', Path.DirectorySeparatorChar) + Recipe.Extension);
    }

    /// <summary>Рецепт файла; нет — null.</summary>
    public Recipe? LoadRecipe(string source) => RecipePath(source) is var path && File.Exists(path) ? Recipe.Load(path) : null;

    public void SaveRecipe(string source, Recipe recipe) => recipe.Save(RecipePath(source));

    /// <summary>Рецепт первой выпечки: конвертер и его параметры по умолчанию.</summary>
    /// <exception cref="ArgumentException">Конвертер не годится файлу.</exception>
    public Recipe NewRecipe(string source, IConverter converter)
    {
        if (!converter.Accepts(source)) throw new ArgumentException($"{Path.GetFileName(source)}: конвертер {converter.Id} такие файлы не печёт ({converter.Kind})");
        return new Recipe { Converter = converter.Id, Params = Recipe.WriteParams(converter.Defaults(new BakeJob(this, source, TextWriter.Null))) };
    }

    /// <summary>Сырые файлы всех рецептов (полные пути), по алфавиту.</summary>
    public IReadOnlyList<string> RecipeSources()
    {
        string dir = Path.Combine(BakeDir, RecipesDir);
        if (!Directory.Exists(dir)) return [];
        var result = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*" + Recipe.Extension, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            int slash = rel.IndexOf('/');
            if (slash < 0) continue;
            result.Add(Roots.Expand($"${{{rel[..slash]}}}/{rel[(slash + 1)..^Recipe.Extension.Length]}"));
        }
        return result;
    }

    /// <summary>Испечь сырой файл по рецепту.</summary>
    /// <returns>Записанные файлы — пути в <c>Content/</c>.</returns>
    /// <exception cref="ArgumentException">Неизвестный конвертер или он не годится файлу; ошибки параметров.</exception>
    public IReadOnlyList<string> Bake(string source, Recipe recipe, TextWriter log)
    {
        var converter = Find(recipe.Converter) ?? throw new ArgumentException($"рецепт {RecipePath(source)}: неизвестный конвертер «{recipe.Converter}»");
        if (!converter.Accepts(source)) throw new ArgumentException($"{Path.GetFileName(source)}: конвертер {converter.Id} такие файлы не печёт ({converter.Kind})");
        if (!File.Exists(source)) throw new FileNotFoundException($"нет сырого файла {source}", source);
        var job = new BakeJob(this, source, log);
        converter.Bake(job, recipe.ReadParams(converter.ParamsType));
        return job.Written;
    }

    /// <summary>
    /// Риг по рецепту скелета: сырой файл — поза покоя <c>skeleton.json</c>, рецепт — ID скелета, эталонный экспорт клипов
    /// и источник пальцев. На него ссылаются (поле rig) анимация, меш персонажа, оружие и анатомия.
    /// </summary>
    /// <exception cref="ArgumentException">Рецепта нет или он не скелета.</exception>
    public Rig Rig(string path)
    {
        if (path.Length == 0) throw new ArgumentException("rig: нужен рецепт скелета — путь его skeleton.json");
        string source = Roots.Expand(path);
        var recipe = LoadRecipe(source) ?? throw new ArgumentException($"rig {path}: нет рецепта {RecipePath(source)} — сначала испеките скелет");
        if (recipe.Converter != SkeletonConverter.ConverterId)
            throw new ArgumentException($"rig {path}: рецепт конвертера {recipe.Converter}, а нужен {SkeletonConverter.ConverterId}");
        var p = (SkeletonParams)recipe.ReadParams(typeof(SkeletonParams));
        return new Rig(SkeletonConverter.SkeletonId(p, source), source, Roots.Expand(p.Clips), p.FingerSrc is null ? null : Roots.Expand(p.FingerSrc));
    }

    /// <summary>Единственный рецепт скелета (поле rig по умолчанию); нет или несколько — пусто.</summary>
    public string DefaultRig()
    {
        var rigs = RecipeSources().Where(s => LoadRecipe(s)?.Converter == SkeletonConverter.ConverterId).ToList();
        return rigs.Count == 1 ? Roots.Contract(rigs[0]) : "";
    }
}

/// <param name="Id">ID скелета в библиотеке.</param>
/// <param name="RefPose">skeleton.json с позой покоя.</param>
/// <param name="Clips">Эталонный raw-экспорт на этом скелете (кости, пространство, клипы хвата).</param>
/// <param name="FingerSrc">Raw-экспорт исходного скелета тех же клипов — пальцы.</param>
public sealed record Rig(string Id, string RefPose, string Clips, string? FingerSrc);
