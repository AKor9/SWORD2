using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;

namespace Sword.Engine.Agent;

/// <summary>
/// Где манифесты фич и чем снимать виды (этап 3д): корень репозитория, каталоги поиска <c>feature.json</c>, снимок вида в
/// App (null — App нет), оптимизирована ли сборка (бюджеты времени — только в Release). Задаёт exe игры.
/// </summary>
public sealed record FeatureSetup(string RepoRoot, IReadOnlyList<string> Roots, Func<ViewRequest, CancellationToken, ViewResult>? CaptureView,
    bool Optimized);

/// <summary>
/// Приёмка фич (этап 3д, раздел 8 плана — «Контракт фичи»): <c>feature.list</c> — манифесты, <c>feature.verify</c> —
/// одним вызовом сценарии, инварианты, метрики, бюджеты и виды (<see cref="FeatureVerifier"/>). Отдельные симуляции той же
/// композиции — сеанс не трогается; только Headless (пересборка симуляции).
/// </summary>
internal static class FeatureTools
{
    public static void Register(AgentHost host)
    {
        host.Add(new AgentTool
        {
            Name = "feature.list", Title = "Фичи",
            Description = "Манифесты фич (feature.json рядом с md фичи): id, название, md, сценарии (реплей или команды, проверки), " +
                "инварианты, метрики (источник — sword или sword2), виды, бюджеты, чек-лист «что проверить в игре». id — только эта " +
                "фича, целиком.",
            Arguments = CoreTools.Props(("id", CoreTools.Str("только эта фича — манифест целиком"))),
            Run = call =>
            {
                var manifests = Manifests(host);
                string? id = call.String("id");
                if (id is not null)
                {
                    var m = manifests.FirstOrDefault(x => x.Id == id)
                        ?? throw new ArgumentException($"нет фичи «{id}»; есть: {string.Join(", ", manifests.Select(x => x.Id))}");
                    var full = JsonNode.Parse(File.ReadAllText(m.Path), documentOptions: new System.Text.Json.JsonDocumentOptions
                    {
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    })!.AsObject();
                    full["path"] = m.Path;
                    return Task.FromResult(new ToolOutput(full));
                }
                return Task.FromResult(new ToolOutput(new JsonObject
                {
                    ["features"] = new JsonArray([.. manifests.Select(m => (JsonNode?)new JsonObject
                    {
                        ["id"] = m.Id, ["title"] = m.Title, ["manifest"] = m.Path, ["doc"] = m.Doc,
                        ["requiredModules"] = new JsonArray([.. m.RequiredModules.Select(x => (JsonNode?)x)]),
                        ["scenarios"] = new JsonArray([.. m.Scenarios.Select(s => (JsonNode?)new JsonObject
                        {
                            ["id"] = s.Id, ["replay"] = s.Replay, ["ticks"] = s.Ticks, ["commands"] = s.Commands.Count, ["checks"] = s.Checks.Count,
                        })]),
                        ["invariants"] = new JsonArray([.. m.Invariants.Select(x => (JsonNode?)x)]),
                        ["metrics"] = new JsonArray([.. m.Metrics.Select(x => (JsonNode?)$"{x.Id} ({x.Source})")]),
                        ["views"] = m.Views.Count, ["budgets"] = m.Budgets.Count, ["checklist"] = m.Checklist.Count,
                    })]),
                }));
            },
        });

        host.Add(new AgentTool
        {
            Name = "feature.verify", Title = "Проверить фичу",
            Description = "Приёмка фичи одним вызовом (раздел 12 плана): сценарии манифеста в отдельных симуляциях (хеши реплеев, " +
                "проверки-условия, нарушения инвариантов — провал), инварианты манифеста, метрики против эталонов SWORD и SWORD2, " +
                "бюджеты (медиана повторов без инвариантов; время — только в Release), виды — кадры Sword.exe --replay по записи " +
                "сценария. Ответ — вердикт ok, провалы словами, по разделам; всё — в каталоге артефактов (report.json, записи " +
                "сценариев, PNG видов). Вердикт — по манифесту и эталонам, не по мнению агента. Только Headless.",
            Arguments = CoreTools.Props(
                ("id", CoreTools.Str("фича (feature.list)")),
                ("views", new JsonObject { ["type"] = "boolean", ["description"] = "снимать виды в App (по умолчанию true)" }),
                ("budgets", new JsonObject { ["type"] = "boolean", ["description"] = "мерить бюджеты (по умолчанию true)" }),
                ("timeoutSeconds", CoreTools.Num("сколько ждать, с (по умолчанию 600)", 1, 3600))),
            Required = ["id"],
            Cost = ToolCost.Heavy,
            Run = async call =>
            {
                var session = ReplayTools.RequireRebuild(host);
                var setup = host.Features ?? throw new InvalidOperationException("фич нет: exe не задал каталоги манифестов");
                string id = call.RequiredString("id");
                var manifest = Manifests(host).FirstOrDefault(m => m.Id == id)
                    ?? throw new ArgumentException($"нет фичи «{id}»; есть: {string.Join(", ", Manifests(host).Select(m => m.Id))}");
                var options = new FeatureVerifyOptions
                {
                    OutputDirectory = Path.Combine(host.Artifacts, $"feature-{id}-{DateTime.UtcNow:HHmmss}"),
                    Views = call.Bool("views", true),
                    Budgets = call.Bool("budgets", true),
                };
                var env = Environment(host, session, setup);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(call.Cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(call.Number("timeoutSeconds", 600, 1, 3600), host.MaxWaitSeconds)));
                var report = await Task.Run(() => FeatureVerifier.Verify(manifest, env, options, timeout.Token), timeout.Token).ConfigureAwait(false);
                Log.Info("agent", $"feature.verify {id}: {(report.Ok ? "ok" : $"провалов {report.Failures.Count}")} — {report.Directory}");
                return new ToolOutput(report.Json) { ArtifactId = Path.GetFileName(report.Directory) };
            },
        });
    }

    /// <summary>Окружение проверки: построитель сеанса, имена, сборка и контент сеанса.</summary>
    internal static FeatureEnvironment Environment(AgentHost host, AgentSimulation session, FeatureSetup setup) => new()
    {
        RepoRoot = setup.RepoRoot, Build = session.Build, Info = session.Info, Names = host.Names, CaptureView = setup.CaptureView,
        Optimized = setup.Optimized,
    };

    private static IReadOnlyList<FeatureManifest> Manifests(AgentHost host)
    {
        var setup = host.Features ?? throw new InvalidOperationException("фич нет: exe не задал каталоги манифестов");
        try
        {
            return FeatureManifest.Find(setup.Roots);
        }
        catch (InvalidDataException e)
        {
            throw new ArgumentException($"манифест с ошибкой: {e.Message}");
        }
    }
}
