using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sword.Bake;

/// <summary>
/// sword-bake — пекарня: сырой файл → конвертер по виду файла → файлы игровых форматов в <c>Content/</c>.
///   ФАЙЛ [--converter ID] [--set поле=значение]… [--yes]   испечь файл по рецепту; рецепта нет — спросить параметры
///   --all                                                   испечь все сырые файлы, у которых есть рецепт
///   --kinds                                                 таблица видов файлов
///   [--bake КАТАЛОГ] [--content КАТАЛОГ]                    каталоги пекарни и ресурсов (по умолчанию Bake/ и Content/ репозитория)
/// Коды выхода: 0 — испечено, 1 — ошибка, 2 — конвертера нет, 3 — нужны параметры (рецепт не записан).
/// </summary>
internal static class Program
{
    private const int Failed = 1, NoConverter = 2, NeedParams = 3;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var list = args.ToList();
            string? bakeDir = Take(list, "--bake"), contentDir = Take(list, "--content"), converter = Take(list, "--converter");
            var sets = new List<string>();
            while (Take(list, "--set") is { } s) sets.Add(s);
            bool yes = list.Remove("--yes"), all = list.Remove("--all"), kinds = list.Remove("--kinds");
            if (list.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) is { } unknown) throw new ArgumentException($"неизвестный ключ {unknown}");

            if (kinds)
            {
                foreach (var c in Baker.Converters) Console.WriteLine($"{c.Id,-10} {c.Kind,-40} {c.Title}");
                return 0;
            }
            var baker = bakeDir is null && contentDir is null ? Baker.OfRepository(Environment.CurrentDirectory)
                : new Baker(bakeDir ?? throw new ArgumentException("--content без --bake"), contentDir ?? throw new ArgumentException("--bake без --content"));
            if (all) return list.Count == 0 ? BakeAll(baker) : throw new ArgumentException("--all — без файлов");
            if (list.Count != 1) return Usage();
            return BakeOne(baker, Path.GetFullPath(list[0]), converter, sets, yes);
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ошибка: {e.Message}");
            return Failed;
        }
    }

    private static string? Take(List<string> list, string key)
    {
        int i = list.IndexOf(key);
        if (i < 0) return null;
        if (i + 1 >= list.Count) throw new ArgumentException($"{key}: нужно значение");
        string value = list[i + 1];
        list.RemoveRange(i, 2);
        return value;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("sword-bake ФАЙЛ [--converter ID] [--set поле=значение]… [--yes] | --all | --kinds  [--bake КАТАЛОГ --content КАТАЛОГ]");
        return Failed;
    }

    private static int BakeOne(Baker baker, string source, string? converterId, List<string> sets, bool yes)
    {
        if (!File.Exists(source)) throw new FileNotFoundException($"нет файла {source}");
        var candidates = Baker.Candidates(source);
        if (candidates.Count == 0)
        {
            Console.Error.WriteLine(Baker.NoConverter(source));
            return NoConverter;
        }
        string recipePath = baker.RecipePath(source);
        var recipe = baker.LoadRecipe(source);
        if (recipe is null || (converterId is not null && converterId != recipe.Converter))
        {
            var converter = converterId is not null
                ? candidates.FirstOrDefault(c => c.Id == converterId) ?? throw new ArgumentException(
                    $"{Path.GetFileName(source)}: конвертер {converterId} такие файлы не печёт; годятся: {string.Join(", ", candidates.Select(c => c.Id))}")
                : candidates.Count == 1 ? candidates[0] : null;
            if (converter is null)
            {
                Console.Error.WriteLine($"{Path.GetFileName(source)}: годятся несколько конвертеров — выберите --converter: " +
                                        string.Join("; ", candidates.Select(c => $"{c.Id} — {c.Title}")));
                return NeedParams;
            }
            recipe = baker.NewRecipe(source, converter);
            foreach (var s in sets) Set(recipe, s);
            if (!yes && sets.Count == 0)
            {
                Console.Error.WriteLine($"рецепта нет ({recipePath}); конвертер {converter.Id} — {converter.Title}, параметры по умолчанию:");
                Console.Error.WriteLine(recipe.Params.ToJsonString(Recipe.Json));
                Console.Error.WriteLine("задайте --set поле=значение (JSON или строка) или примите их: --yes");
                return NeedParams;
            }
        }
        else foreach (var s in sets) Set(recipe, s);

        var watch = Stopwatch.StartNew();
        var written = baker.Bake(source, recipe, Console.Out);
        baker.SaveRecipe(source, recipe);
        foreach (var w in written) Console.WriteLine($"  → Content/{w}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"испечено {Path.GetFileName(source)} ({recipe.Converter}): файлов {written.Count}, {watch.Elapsed.TotalSeconds:F1} с; рецепт {recipePath}"));
        return 0;
    }

    private static void Set(Recipe recipe, string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"--set {assignment}: нужно поле=значение");
        recipe.Set(assignment[..eq], assignment[(eq + 1)..]);
    }

    /// <summary>Все рецепты параллельно; лог каждого — одним куском после выпечки.</summary>
    private static int BakeAll(Baker baker)
    {
        var sources = baker.RecipeSources();
        if (sources.Count == 0) throw new ArgumentException($"рецептов нет в {Path.Combine(baker.BakeDir, Baker.RecipesDir)}");
        var watch = Stopwatch.StartNew();
        int files = 0, failed = 0;
        var gate = new object();
        Parallel.ForEach(sources, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, source =>
        {
            var log = new StringWriter();
            string result;
            try
            {
                var written = baker.Bake(source, baker.LoadRecipe(source)!, log);
                Interlocked.Add(ref files, written.Count);
                result = $"испечено {baker.Roots.Contract(source)}: файлов {written.Count}";
            }
            catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref failed);
                result = $"ОШИБКА {baker.Roots.Contract(source)}: {e.Message}";
            }
            lock (gate)
            {
                Console.Write(log.ToString());
                Console.WriteLine(result);
            }
        });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"рецептов {sources.Count}, ошибок {failed}, файлов {files} в {baker.ContentDir}, {watch.Elapsed.TotalSeconds:F1} с"));
        return failed == 0 ? 0 : Failed;
    }
}
