using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sword.Bake.Cli;

/// <summary>
/// Итоги <c>status --json</c> и <c>build --json</c> — агентский вид пекарни: один JSON-объект одной строкой в stdout
/// (как <c>Sword.Headless --json</c>), без цветного текста и событий по ходу. Поля — camelCase, кириллица не экранируется.
/// Ошибка до итога (нет проекта, неверный ключ) — объект <see cref="Error"/> с тем же кодом выхода, что у текста.
/// </summary>
internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Имя статуса в JSON: <c>upToDate</c>, <c>dirty</c>, <c>noSource</c>, <c>error</c> (и <c>stale</c>, <c>missing</c> у пака).</summary>
    public static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static JsonObject Error(string command, string message, int exitCode) => new()
    {
        ["command"] = command,
        ["ok"] = false,
        ["exitCode"] = exitCode,
        ["error"] = message,
    };

    /// <summary>Проект: имя, файлы, плагины (загружен ли каждый), проблемы проекта и неразобранные манифесты.</summary>
    public static JsonObject Project(BakeProject project) => new()
    {
        ["name"] = project.Name,
        ["file"] = project.ProjectFile,
        ["assetsDir"] = project.AssetsDir,
        ["pak"] = project.PakPath,
        ["strictLayout"] = project.StrictLayout,
        ["plugins"] = new JsonArray([.. project.PluginPaths.Select(p => (JsonNode)new JsonObject
        {
            ["path"] = p,
            ["loaded"] = project.Processors.Plugins.Any(x => x.Path == p),
        })]),
        ["processors"] = new JsonArray([.. project.Processors.Processors.Select(p => (JsonNode)p.Id)]),
        ["problems"] = Strings(project.Problems),
        ["broken"] = new JsonArray([.. project.Broken.Select(b => (JsonNode)new JsonObject { ["file"] = b.File, ["error"] = b.Error })]),
    };

    /// <summary>Итог <c>status</c>: актуальность выбранных ассетов с причинами, счётчики, происхождение, раскладка и пак.</summary>
    public static JsonObject Status(BakeProject project, Builder builder, IReadOnlyList<AssetManifest> selected,
        IReadOnlyDictionary<string, AssetCheck> checks, PakCheck pak, int exitCode)
    {
        var assets = new JsonArray();
        var counts = Enum.GetValues<AssetStatus>().ToDictionary(s => s, _ => 0);
        foreach (var a in selected)
        {
            var c = checks[a.Id];
            counts[c.Status]++;
            assets.Add(new JsonObject
            {
                ["id"] = a.Id,
                ["processor"] = a.Processor,
                ["status"] = Name(c.Status),
                ["reason"] = c.Reason,
            });
        }
        var countsJson = new JsonObject();
        foreach (var (status, n) in counts) countsJson[Name(status)] = n;
        return new JsonObject
        {
            ["command"] = "status",
            ["ok"] = pak.Status == PakStatus.UpToDate,
            ["exitCode"] = exitCode,
            ["project"] = Project(project),
            ["assets"] = assets,
            ["counts"] = countsJson,
            ["provenance"] = new JsonArray([.. project.ProvenanceIssues().Select(i => (JsonNode)new JsonObject { ["asset"] = i.AssetId, ["text"] = i.Text })]),
            ["layout"] = new JsonArray([.. builder.Catalog().Issues.Select(i => (JsonNode)new JsonObject { ["asset"] = i.AssetId, ["text"] = i.Text })]),
            ["pak"] = Pak(project, builder, pak),
        };
    }

    /// <summary>Пак: путь, актуальность с причиной и подробностями, есть ли он, размер и когда собран.</summary>
    public static JsonObject Pak(BakeProject project, Builder builder, PakCheck pak) => new()
    {
        ["path"] = project.PakPath,
        ["status"] = Name(pak.Status),
        ["reason"] = pak.Reason,
        ["details"] = Strings(pak.Details),
        ["exists"] = builder.PakExists,
        ["bytes"] = builder.PakLength,
        ["builtUtc"] = builder.BuildInfo?.BuiltUtc,
    };

    /// <summary>
    /// Итог <c>build</c>: по ассету — чем кончилось (built, upToDate, failed, skipped, cancelled), почему собирался, ошибка,
    /// время, предупреждения и лог (у упавших — целиком, у остальных — число строк); сводка, записан ли пак и актуален ли он
    /// после сборки; сообщения сборки без ассета (запись пака, почему не записан).
    /// </summary>
    public static JsonObject Build(BakeProject project, Builder builder, BuildEvents events, BuildSummary s, BuildOptions options,
        IReadOnlyList<string> patterns, PakCheck after, int exitCode)
    {
        var assets = new JsonArray();
        foreach (var (id, a) in events.Assets)
        {
            var o = new JsonObject
            {
                ["id"] = id,
                ["processor"] = project.Find(id)?.Processor,
                ["result"] = a.Result,
                ["reason"] = a.Reason,
                ["error"] = a.Error,
                ["detail"] = a.Detail,
                ["seconds"] = Math.Round(a.Elapsed.TotalSeconds, 3),
                ["warnings"] = Strings(a.Warnings),
            };
            if (a.Result == "failed") o["log"] = Strings(a.Log);
            else o["logLines"] = a.Log.Count;
            assets.Add(o);
        }
        return new JsonObject
        {
            ["command"] = "build",
            ["ok"] = s.Ok,
            ["exitCode"] = exitCode,
            ["project"] = Project(project),
            ["options"] = new JsonObject
            {
                ["force"] = options.Force,
                ["jobs"] = options.Jobs,
                ["patterns"] = Strings(patterns),
            },
            ["assets"] = assets,
            ["summary"] = new JsonObject
            {
                ["built"] = s.Built,
                ["upToDate"] = s.UpToDate,
                ["failed"] = s.Failed,
                ["skipped"] = s.Skipped,
                ["cancelled"] = s.Cancelled,
                ["seconds"] = Math.Round(s.Elapsed.TotalSeconds, 3),
            },
            ["pakWritten"] = s.PakWritten,
            ["pakUnchanged"] = s.PakUnchanged,
            ["pakFiles"] = s.PakFiles,
            ["pakBytes"] = s.PakBytes,
            ["pak"] = Pak(project, builder, after),
            ["messages"] = new JsonArray([.. events.Messages.Select(m => (JsonNode)new JsonObject { ["kind"] = m.Kind, ["text"] = m.Text })]),
        };
    }

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);
}

/// <summary>События сборки, собранные для итога <c>build --json</c> (приходят из рабочих потоков сборки).</summary>
internal sealed class BuildEvents
{
    public sealed class Asset
    {
        public int Order;
        public string Result = "queued";
        public string? Reason, Error, Detail;
        public TimeSpan Elapsed;
        public readonly List<string> Warnings = [];
        public readonly List<string> Log = [];
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Kind, string Text)> _messages = [];

    /// <summary>Ассеты в порядке первого события (очередь сборки — топологический порядок).</summary>
    public IReadOnlyList<(string Id, Asset Asset)> Assets
    {
        get { lock (_lock) return [.. _assets.OrderBy(kv => kv.Value.Order).Select(kv => (kv.Key, kv.Value))]; }
    }

    public IReadOnlyList<(string Kind, string Text)> Messages { get { lock (_lock) return [.. _messages]; } }

    public void On(BakeEvent e)
    {
        lock (_lock)
        {
            if (e.AssetId.Length == 0)
            {
                _messages.Add((CliJson.Name(e.Kind), e.Text));
                return;
            }
            if (!_assets.TryGetValue(e.AssetId, out var a)) _assets[e.AssetId] = a = new Asset { Order = _assets.Count };
            switch (e.Kind)
            {
                case BakeEventKind.Queued: break;
                case BakeEventKind.Started: a.Result = "started"; a.Reason = e.Text; break;
                case BakeEventKind.Log: a.Log.Add(e.Text); break;
                case BakeEventKind.Warning: a.Warnings.Add(e.Text); break;
                case BakeEventKind.UpToDate: a.Result = "upToDate"; a.Reason = e.Text; break;
                case BakeEventKind.Finished: a.Result = "built"; a.Detail = e.Text; a.Elapsed = e.Elapsed; break;
                case BakeEventKind.Failed: a.Result = "failed"; a.Error = e.Text; a.Elapsed = e.Elapsed; break;
                case BakeEventKind.Skipped: a.Result = "skipped"; a.Error = e.Text; break;
                case BakeEventKind.Cancelled: a.Result = "cancelled"; a.Error = e.Text; break;
            }
        }
    }
}
