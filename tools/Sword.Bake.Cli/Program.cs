using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Content;

namespace Sword.Bake.Cli;

/// <summary>
/// sword-bake — консольная пекарня: проект Bake (bake.project.json) → пак .swpak.
///   [--project ПУТЬ] — каталог проекта или каталог внутри него (по умолчанию — текущий)
///   [--json] — у status и build: итог одним JSON-объектом одной строкой в stdout (агентский вид, <see cref="CliJson"/>)
///   init [КАТАЛОГ] [--name ИМЯ]                    создать проект
///   build [шаблоны…] [--force] [-j N]              собрать устаревшие ассеты (с зависимостями) и записать пак
///   status [шаблоны…]                              актуальность ассетов и пака
///   add ID ОБРАБОТЧИК [поле=путь…] [--notes ТЕКСТ] добавить ассет; пути исходников — любые, файлы не копируются
///   remove ID                                      удалить манифест ассета (копия — в .bake/removed)
///   root ИМЯ ПУТЬ                                  задать локальный корень исходников (bake.user.json)
///   list [--content]                               проект, корни, обработчики, ассеты; --content — каталог контента
///   which ПУТЬ…                                    ассет контента по пути в паке или ID: файлы, кто делает, актуален ли
///   licenses                                       импорты по лицензиям: коллекции, единицы, файлы лицензий
///   pak [--verify] [--extract КАТАЛОГ]             содержимое пака по ассетам; --verify — целостность
///   КОМАНДА …                                      команды плагинов проекта (inspect у SWORD)
/// Шаблон — ID («anim/1hm_sword»), папка («anim»), маска («anim/*_locomotion»), import:ИМЯ, processor:ID
/// или путь ассета контента («anim/katana» — единицы, которые его пишут).
/// </summary>
internal static class Program
{
    private static readonly object ConsoleLock = new();
    private static string? _projectArg;
    /// <summary>Итог — JSON в stdout (<see cref="CliJson"/>): только у status и build.</summary>
    private static bool _json;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string command = "";
        try
        {
            var list = args.ToList();
            int p = list.IndexOf("--project");
            if (p >= 0)
            {
                if (p + 1 >= list.Count) throw new ArgumentException("--project: нужен путь");
                _projectArg = list[p + 1];
                list.RemoveRange(p, 2);
            }
            _json = list.Remove("--json");
            if (list.Count == 0) return Usage(1);
            command = list[0];
            if (_json && command is not ("status" or "build"))
                throw new ArgumentException($"--json — только у status и build, не у {command}");
            string[] rest = [.. list.Skip(1)];
            return list[0] switch
            {
                "init" => Init(rest),
                "build" => Build(rest),
                "status" => Status(rest),
                "add" => Add(rest),
                "remove" => Remove(rest),
                "root" => Root(rest),
                "list" => List(rest),
                "which" => Which(rest),
                "licenses" => Licenses(),
                "pak" => Pak(rest),
                "help" or "-h" or "--help" => Usage(0),
                _ => PluginCommand(list[0], rest),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or FormatException or JsonException or UnauthorizedAccessException)
        {
            if (_json) WriteJson(CliJson.Error(command, e.Message, 1));
            else Console.Error.WriteLine($"sword-bake: {e.Message}");
            return 1;
        }
    }

    /// <summary>Итог --json: один объект одной строкой — больше в stdout ничего не пишется.</summary>
    private static void WriteJson(JsonObject result)
    {
        lock (ConsoleLock) Console.Out.WriteLine(result.ToJsonString(CliJson.Options));
    }

    private static int Usage(int code)
    {
        Console.WriteLine("""
            sword-bake — пекарня: исходники ассетов → пак .swpak (проект — bake.project.json)
              --project ПУТЬ                       проект (по умолчанию — ищется от текущего каталога вверх)
              --json                               у status и build: итог одним JSON-объектом одной строкой (для агентов)
              init [КАТАЛОГ] [--name ИМЯ]          создать проект
              build [шаблоны…] [--force] [-j N]    собрать устаревшие ассеты с зависимостями и записать пак
              status [шаблоны…]                    актуальность ассетов и пака
              add ID ОБРАБОТЧИК [поле=путь…]       добавить ассет (--notes ТЕКСТ — пояснение)
              remove ID                            удалить манифест ассета
              root ИМЯ ПУТЬ                        задать локальный корень исходников (bake.user.json, вне git)
              list [--content]                     проект, корни, обработчики, ассеты; --content — каталог контента
              which ПУТЬ…                          ассет контента по пути в паке или ID: файлы, кто делает, актуален ли
              licenses                             импорты по лицензиям: коллекции, единицы, файлы лицензий
              pak [--verify] [--extract КАТАЛОГ]   содержимое пака; --verify — целостность; --extract — распаковать
              КОМАНДА …                            команды плагинов проекта (см. list)
            шаблон — ID (anim/1hm_sword), папка (anim), маска (anim/*_locomotion), import:ИМЯ, processor:ID
            или путь ассета контента (anim/katana — единицы, которые его пишут)
            """);
        return code;
    }

    private static BakeProject OpenProject()
    {
        var project = BakeProject.Open(_projectArg);
        if (_json) return project;   // проблемы проекта и неразобранные манифесты — в итоге JSON
        foreach (var problem in project.Problems) Console.Error.WriteLine($"проект: {problem}");
        foreach (var (file, error) in project.Broken)
            Console.Error.WriteLine($"манифест не разобран: {file}: {error}");
        return project;
    }

    private static int Init(string[] args)
    {
        string? dir = null, name = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
            else if (!args[i].StartsWith('-') && dir is null) dir = args[i];
            else throw new ArgumentException($"init: неизвестный аргумент {args[i]}");
        }
        var project = BakeProject.Create(dir ?? _projectArg ?? Directory.GetCurrentDirectory(), name);
        Console.WriteLine($"проект {project.Name}: {project.ProjectFile}");
        Console.WriteLine($"ассеты — {project.AssetsDir}, пак — {project.PakPath}");
        return 0;
    }

    private static int Build(string[] args)
    {
        bool force = false;
        int jobs = new BuildOptions().Jobs;
        var patterns = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--force" or "-f") force = true;
            else if (a == "-j" && i + 1 < args.Length) jobs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (a.StartsWith('-')) throw new ArgumentException($"build: неизвестный ключ {a}");
            else patterns.Add(a);
        }

        var project = OpenProject();
        var builder = new Builder(project);
        var assets = project.Select(patterns);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            lock (ConsoleLock) Console.Error.WriteLine("отмена: начатые ассеты доделываются, остальные не начнутся, пак не пишется");
        };
        var options = new BuildOptions { Force = force, Jobs = jobs };
        if (_json)
        {
            var events = new BuildEvents();
            var summary = builder.Build(patterns.Count == 0 ? null : assets, options, events.On, cts.Token);
            int exit = summary.Ok ? 0 : 1;
            WriteJson(CliJson.Build(project, builder, events, summary, options, patterns, builder.CheckPak(), exit));
            return exit;
        }
        Console.WriteLine($"проект {project.Name}; пак {project.PakPath}");
        var s = builder.Build(patterns.Count == 0 ? null : assets, options, Print, cts.Token);
        lock (ConsoleLock)
            Console.WriteLine($"готово за {s.Elapsed.TotalSeconds:F1} с: запечено {s.Built}, актуальны {s.UpToDate}, ошибок {s.Failed}, пропущено {s.Skipped}" +
                              (s.Cancelled ? ", отменено" : "") + (s.PakWritten ? $"; пак записан: файлов {s.PakFiles}" : "; пак не записан"));
        return s.Ok ? 0 : 1;
    }

    private static void Print(BakeEvent e)
    {
        // события задачи целиком (пак, сводки) идут без ассета — тогда и скобок нет
        string tag = e.AssetId.Length > 0 ? $"[{e.AssetId}] " : "";
        lock (ConsoleLock)
        {
            switch (e.Kind)
            {
                case BakeEventKind.Started:
                    Line(ConsoleColor.Cyan, $"▶ {e.AssetId}", $" — {e.Text}");
                    break;
                case BakeEventKind.Log:
                    Console.WriteLine($"  {tag}{e.Text}");
                    break;
                case BakeEventKind.Warning:
                    Line(ConsoleColor.Yellow, $"  {tag}ВНИМАНИЕ: {e.Text}", "");
                    break;
                case BakeEventKind.UpToDate:
                    Line(ConsoleColor.DarkGray, $"= {e.AssetId}", " — актуален");
                    break;
                case BakeEventKind.Finished:
                    Line(ConsoleColor.Green, $"✓ {e.AssetId}", $" за {e.Elapsed.TotalSeconds:F1} с: {e.Text}");
                    break;
                case BakeEventKind.Failed:
                    Line(ConsoleColor.Red, e.AssetId.Length > 0 ? $"✗ {e.AssetId}" : "✗", $" {e.Text}");
                    break;
                case BakeEventKind.Skipped or BakeEventKind.Cancelled:
                    Line(ConsoleColor.DarkYellow, e.AssetId.Length > 0 ? $"- {e.AssetId}" : "-", $" {e.Text}");
                    break;
            }
        }
    }

    private static void Line(ConsoleColor colour, string head, string tail)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(head);
        Console.ForegroundColor = prev;
        Console.WriteLine(tail);
    }

    private static int Status(string[] args)
    {
        var project = OpenProject();
        var builder = new Builder(project);
        var checks = builder.CheckAll();
        var selected = project.Select(args);
        if (_json)
        {
            var check = builder.CheckPak(checks);
            int exit = check.Status == PakStatus.UpToDate ? 0 : 2;
            WriteJson(CliJson.Status(project, builder, selected, checks, check, exit));
            return exit;
        }
        foreach (var a in selected)
        {
            var c = checks[a.Id];
            var colour = c.Status switch
            {
                AssetStatus.UpToDate => ConsoleColor.Green,
                AssetStatus.Dirty => ConsoleColor.Yellow,
                _ => ConsoleColor.Red,
            };
            string label = c.Status switch
            {
                AssetStatus.UpToDate => "актуален ",
                AssetStatus.Dirty => "устарел  ",
                AssetStatus.NoSource => "нет исх. ",
                _ => "ошибка   ",
            };
            lock (ConsoleLock) Line(colour, label, $" {a.Id,-28} {a.Processor,-12} {c.Reason}");
        }
        PrintProvenance(project);
        PrintIssues(builder.Catalog().Issues, project.StrictLayout);
        var pak = builder.CheckPak(checks);
        Line(pak.Status == PakStatus.UpToDate ? ConsoleColor.Green : pak.Status == PakStatus.Error ? ConsoleColor.Red : ConsoleColor.Yellow,
            "пак      ", $" {project.PakPath}: {pak.Reason}");
        foreach (var d in pak.Details.Where(d => d.StartsWith("манифест", StringComparison.Ordinal) || d.Contains("удалён из проекта", StringComparison.Ordinal)))
            Console.WriteLine($"          {d}");
        return pak.Status == PakStatus.UpToDate ? 0 : 2;
    }

    private static int Add(string[] args)
    {
        string? id = null, processor = null, notes = "";
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--notes" && i + 1 < args.Length) notes = args[++i];
            else if (a.Contains('=') && id is not null && processor is not null)
            {
                int eq = a.IndexOf('=');
                string field = a[..eq], value = a[(eq + 1)..];
                // полный путь от текущего каталога: так его понимает пользователь в консоли
                sources[field] = value.Length == 0 ? "" : string.Join('|', value.Split('|').Select(Path.GetFullPath));
            }
            else if (id is null) id = a;
            else if (processor is null) processor = a;
            else throw new ArgumentException($"add: неизвестный аргумент {a}");
        }
        if (id is null || processor is null) throw new ArgumentException("add: нужны ID и обработчик, например: add fonts/body font.vector font=C:/Fonts/body.ttf");
        var project = OpenProject();
        var asset = project.AddAsset(id, processor, sources, notes);
        Console.WriteLine($"добавлен {asset.Id}: {asset.File}");
        foreach (var s in project.SourcesOf(asset))
            Console.WriteLine($"  {s.Field} = {s.Value}{(s.Problem is null ? "" : "  — " + s.Problem)}");
        return 0;
    }

    private static int Remove(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("remove: нужен ID ассета");
        var project = OpenProject();
        var asset = project.Find(args[0]) ?? throw new ArgumentException($"нет ассета {args[0]}");
        string backup = project.RemoveAsset(asset);
        Console.WriteLine($"удалён {asset.Id}; копия манифеста — {backup}. Пак обновит следующая сборка.");
        return 0;
    }

    private static int Root(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("root: нужны ИМЯ и ПУТЬ");
        var project = OpenProject();
        SourceRoots.SetUserRoot(project.Root, args[0], Path.GetFullPath(args[1]));
        project.Reload();
        Console.WriteLine($"${{{args[0]}}} = {project.Roots.Roots[args[0]]}  ({Path.Combine(project.Root, SourceRoots.UserFile)})");
        return 0;
    }

    private static int List(string[] args)
    {
        bool content = false;
        foreach (var a in args)
            if (a == "--content") content = true;
            else throw new ArgumentException($"list: неизвестный аргумент {a}");
        var project = OpenProject();
        var builder = new Builder(project);
        if (content)
        {
            var catalog = builder.Catalog();
            var checks = builder.CheckAll();
            Console.WriteLine($"каталог контента: ассетов {catalog.Assets.Count}");
            foreach (var c in catalog.Assets)
            {
                var worst = c.Producers.Select(p => checks.GetValueOrDefault(p).Status).DefaultIfEmpty(AssetStatus.UpToDate).Max();
                Line(StatusColour(worst), $"  {c.Id,-40}",
                    $" {(c.IsSet ? $"набор, файлов {c.Files.Count}" : string.Join(", ", c.Files.Select(Path.GetExtension))),-22} ← {string.Join(", ", c.Producers)}");
            }
            PrintIssues(catalog.Issues, project.StrictLayout);
            return 0;
        }
        Console.WriteLine($"проект      {project.Name}: {project.ProjectFile}");
        Console.WriteLine($"ассеты      {project.AssetsDir}");
        Console.WriteLine($"пак         {project.PakPath}{(builder.PakExists ? $"  ({builder.PakLength / 1048576.0:F1} МБ, {builder.PakTimeUtc.ToLocalTime():dd.MM.yyyy HH:mm})" : "  (не собран)")}");
        foreach (var (name, path) in project.Roots.Roots)
            Console.WriteLine($"${{{name}}}{new string(' ', Math.Max(1, 10 - name.Length))}{path}{(Directory.Exists(path) ? "" : "  (нет каталога)")}");
        foreach (var name in project.MissingRoots())
            Console.WriteLine($"${{{name}}}{new string(' ', Math.Max(1, 10 - name.Length))}не задан — sword-bake root {name} ПУТЬ");
        foreach (var plugin in project.PluginPaths)
            Console.WriteLine($"плагин      {plugin}{(project.Processors.Plugins.Any(p => p.Path == plugin) ? "" : "  (не загружен)")}");
        Console.WriteLine("обработчики:");
        foreach (var p in project.Processors.Processors)
            Console.WriteLine($"  {p.Id,-12} {p.Title}");
        if (project.Processors.Commands.Count > 0)
        {
            Console.WriteLine("команды плагинов:");
            foreach (var c in project.Processors.Commands)
                Console.WriteLine($"  {c.Name,-12} {c.Summary}");
        }
        Console.WriteLine("ассеты:");
        foreach (var a in project.Assets)
            Console.WriteLine($"  {a.Id,-28} {a.Processor}");
        return 0;
    }

    private static int Which(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("which: нужен путь в паке или ID ассета контента, например weapons/katana.swmesh");
        var project = OpenProject();
        var builder = new Builder(project);
        var catalog = builder.Catalog();
        var checks = builder.CheckAll();
        int code = 0;
        foreach (var arg in args)
        {
            if (catalog.Find(arg) is not { } c)
            {
                Line(ConsoleColor.Red, $"{arg}", " — нет в каталоге контента (ни в объявленных выходах, ни в паке)");
                code = 1;
                continue;
            }
            Console.WriteLine($"{c.Id}  ({(c.IsSet ? "набор" : "ассет контента")}, файлов {c.Files.Count})");
            foreach (var f in c.Files.Take(c.IsSet ? 5 : int.MaxValue)) Console.WriteLine($"  файл     {f}");
            if (c.IsSet && c.Files.Count > 5) Console.WriteLine($"  …        ещё {c.Files.Count - 5}");
            foreach (var p in c.Producers)
            {
                var check = checks.GetValueOrDefault(p);
                Line(StatusColour(check.Status), $"  делает   {p}",
                    $"  {project.Find(p)?.Processor}  {check.Reason}  ({ManifestRel(project, p)})");
                if (project.Find(p) is { } unit)
                    Console.WriteLine($"  импорт   {Provenance(project, unit)}");
            }
        }
        return code;
    }

    private static int Licenses()
    {
        var project = OpenProject();
        var units = project.Assets.ToLookup(a => a.Import, StringComparer.OrdinalIgnoreCase);
        foreach (var byLicense in project.Imports.GroupBy(kv => kv.Value.License.Length > 0 ? kv.Value.License : "(лицензия не указана)")
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Line(ConsoleColor.Cyan, byLicense.Key, "");
            foreach (var (id, r) in byLicense.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {id,-18} {r.Collection}{(r.Source.Length > 0 ? " — " + r.Source : "")}{(r.Url.Length > 0 ? "  " + r.Url : "")}");
                Console.WriteLine($"  {"",-18} единицы: {(units[id].Any() ? string.Join(", ", units[id].Select(a => a.Id)) : "нет")}");
                foreach (var f in r.LicenseFiles) Console.WriteLine($"  {"",-18} файл лицензии: {f}");
            }
        }
        var unknown = project.Assets.Where(a => a.Import.Length == 0 || !project.Imports.ContainsKey(a.Import)).ToList();
        if (unknown.Count > 0)
            Line(ConsoleColor.Yellow, $"без известного происхождения: {unknown.Count}", $" — {string.Join(", ", unknown.Select(a => a.Id))}");
        PrintProvenance(project, withUnits: false);
        return 0;
    }

    private static string Provenance(BakeProject project, AssetManifest unit) =>
        unit.Import.Length == 0 ? "не указан"
        : project.Imports.TryGetValue(unit.Import, out var r) ? $"{unit.Import} — {r.Collection}, {(r.License.Length > 0 ? r.License : "лицензия не указана")}"
        : $"{unit.Import} — нет в {ImportRegistry.FileName}";

    /// <summary>Замечания о происхождении: единицы без импорта — сводкой, остальное — строками.</summary>
    private static void PrintProvenance(BakeProject project, bool withUnits = true)
    {
        var issues = project.ProvenanceIssues();
        var missing = issues.Where(i => i.AssetId is not null && project.Find(i.AssetId)?.Import.Length == 0).ToList();
        var rest = issues.Except(missing).Where(i => withUnits || i.AssetId is null).ToList();
        var colour = project.StrictLayout ? ConsoleColor.Red : ConsoleColor.Yellow;
        if (withUnits && missing.Count > 0)
            Line(colour, $"происхождение не указано у {missing.Count} из {project.Assets.Count}", $" (поле import; реестр — {ImportRegistry.FileName})");
        foreach (var i in rest) Line(colour, "происхождение: ", i.AssetId is null ? i.Text : $"{i.AssetId}: {i.Text}");
    }

    private static string ManifestRel(BakeProject project, string id) =>
        Path.GetRelativePath(Directory.GetCurrentDirectory(), project.ManifestPath(id)).Replace('\\', '/');

    private static ConsoleColor StatusColour(AssetStatus s) => s switch
    {
        AssetStatus.UpToDate => ConsoleColor.Green,
        AssetStatus.Dirty => ConsoleColor.Yellow,
        _ => ConsoleColor.Red,
    };

    /// <summary>Нарушения раскладки: при strictLayout — ошибки ассетов, иначе предупреждения.</summary>
    private static void PrintIssues(IReadOnlyList<LayoutIssue> issues, bool strict)
    {
        if (issues.Count == 0) return;
        Line(strict ? ConsoleColor.Red : ConsoleColor.Yellow, $"раскладка: нарушений {issues.Count}", strict ? "" : " (предупреждения; строгой раскладка станет с strictLayout)");
        foreach (var i in issues) Console.WriteLine($"  {i.AssetId,-28} {i.Text}");
    }

    private static int Pak(string[] args)
    {
        bool verify = false;
        string? extract = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--verify") verify = true;
            else if (args[i] == "--extract" && i + 1 < args.Length) extract = args[++i];
            else throw new ArgumentException($"pak: неизвестный аргумент {args[i]}");
        }
        var project = OpenProject();
        var builder = new Builder(project);
        if (builder.PakError is { } error) throw new InvalidDataException(error);
        if (!builder.PakExists) throw new FileNotFoundException($"пак не собран: {project.PakPath}");
        var contents = builder.Contents();
        var info = builder.BuildInfo;
        Console.WriteLine($"{project.PakPath}: {builder.PakLength / 1048576.0:F1} МБ, файлов {contents.Count}" +
                          (info is null ? ", сведений о сборке нет" : $", собран {info.BuiltUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}"));
        foreach (var group in contents.GroupBy(c => c.Owner ?? "(без ассета)").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {group.Key,-28} файлов {group.Count(),5}  {group.Sum(e => e.Size) / 1048576.0,8:F2} МБ");

        int code = 0;
        if (verify)
        {
            var report = builder.Verify(Console.WriteLine);
            foreach (var p in report.Problems) Line(ConsoleColor.Red, "≠ ", p);
            Line(report.Ok ? ConsoleColor.Green : ConsoleColor.Red, report.Ok ? "целостность: в порядке" : $"целостность: проблем {report.Problems.Count}", "");
            if (!report.Ok) code = 2;
        }
        if (extract is not null)
        {
            using var pak = PakSource.Open(project.PakPath);
            foreach (var e in pak.Entries)
            {
                string full = Path.Combine(extract, e.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var source = pak.OpenRead(e.Path);
                using var file = File.Create(full);
                source.CopyTo(file);
            }
            Console.WriteLine($"распаковано в {Path.GetFullPath(extract)}: файлов {pak.Entries.Count}");
        }
        return code;
    }

    private static int PluginCommand(string name, string[] args)
    {
        BakeProject project;
        try
        {
            project = OpenProject();
        }
        catch (DirectoryNotFoundException)
        {
            throw new ArgumentException($"неизвестная команда {name}: init | build | status | add | remove | root | list | which | licenses | pak (команды плагинов — только в проекте)");
        }
        var command = project.Processors.Commands.FirstOrDefault(c => c.Name == name)
                      ?? throw new ArgumentException($"неизвестная команда {name}: init | build | status | add | remove | root | list | which | licenses | pak" +
                                                     (project.Processors.Commands.Count > 0 ? " | " + string.Join(" | ", project.Processors.Commands.Select(c => c.Name)) : ""));
        return command.Run(args, Console.Out);
    }
}
