using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sword.Engine.Agent;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;

namespace Sword.Game.Headless;

/// <summary>
/// <c>Sword.Headless --agent</c> (раздел 8 плана): сервер MCP на stdio — клиент (Claude Code, Codex) сам запускает
/// процесс. Симуляция стоит на паузе, время двигает агент; поток симуляции — главный: после каждого тика и на паузе он
/// обслуживает очередь запросов агента (точка обслуживания), протокол — на своём потоке. stdout занят протоколом —
/// журнал уходит в stderr и в кольцо <c>log.tail</c>. Конец stdin (клиент ушёл) или <c>session.stop</c> — выход.
/// В сборке без агента (<c>-p:WithoutAgent=true</c>) этого файла нет.
/// </summary>
public static partial class Program
{
    static partial void RunAgent(Simulation sim, Func<ConfigSet, Simulation> build, ref int? exit)
    {
        var ring = new LogRing();
        Log.SetSinks(new ConsoleLogSink(allToStandardError: true), ring);
        var stop = new CancellationTokenSource();
        var service = new ServiceQueue();
        var time = new TimeControl(paused: true, sim.Tick);
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "headless",
            ContentHash = ContentHash(),
            Catalog = new Catalog(sim.World, sim.CommandTypes),
            Time = time,
            Service = service,
            Log = ring,
            ArtifactsRoot = ArtifactsRoot(),
            Stop = () =>
            {
                stop.Cancel();
                service.Wake();
            },
        });
        SwordAgent.Register(host);
        host.Features = Features();

        var session = new AgentSimulation(sim, ReplayInfo.Current(host.ContentHash), build,
            sets => ConfigSet.Load(AppContext.BaseDirectory, sets));
        // Протокол — когда сеанс подключён: инструменты пересборки (эксперименты, приёмка, seek) с первого вызова.
        Task? protocol = null;
        SimulationLoop.Run(session, host, stop.Token, started: () =>
        {
            protocol = Task.Run(async () =>
            {
                try
                {
                    await StdioTransport.Run(host.Server, Console.OpenStandardInput(), Console.OpenStandardOutput(), stop.Token);
                }
                finally
                {
                    stop.Cancel();   // клиент ушёл — выходим
                    service.Wake();
                }
            });
            Log.Info("agent", $"MCP на stdio: сеанс {host.SessionId}, {host.Tools.Count} инструментов, симуляция на паузе (тик {sim.Tick})");
        });
        protocol?.Wait(TimeSpan.FromSeconds(2));
        // Сеанс агента воспроизводим и проверяем человеком: запись последней симуляции — в артефакты.
        if (session.Current is { Recorder: { } recorder, Tick: > 0 } last)
        {
            string path = host.ArtifactPath($"session-r{session.Run}-t{last.Tick}{Replay.Extension}");
            recorder.ToReplay().Save(path);
            Log.Info("agent", $"реплей сеанса: {path}");
        }
        Log.Info("agent", $"сеанс {host.SessionId} закончен на тике {session.Current.Tick} (симуляция №{session.Run})");
        exit = 0;
    }

    /// <summary><c>artifacts/agent</c> в корне репозитория (папка с SWORD.slnx выше exe), иначе рядом с exe.</summary>
    internal static string ArtifactsRoot() => Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "artifacts", "agent");

    /// <summary>Корень репозитория — папка с SWORD.slnx выше exe; null — exe вне репозитория.</summary>
    internal static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        return null;
    }

    // ── приёмка фич (этап 3д) ─────────────────────────────────────────────

    /// <summary>
    /// Фичи: манифесты <c>feature.json</c> под <c>game</c> и <c>engine</c> репозитория, виды — <c>Sword.exe --replay</c> той же
    /// конфигурации сборки (Debug/Release), бюджеты времени — только в оптимизированной сборке. Вне репозитория — null.
    /// </summary>
    internal static FeatureSetup? Features()
    {
        if (RepoRoot() is not { } root) return null;
        bool optimized = typeof(Program).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().FirstOrDefault()?.IsJITOptimizerDisabled != true;
        return new FeatureSetup(root, [Path.Combine(root, "game"), Path.Combine(root, "engine")], CaptureView, optimized, MeasureFrame);
    }

    /// <summary>Sword.exe той же конфигурации сборки, что этот Headless (…\bin\Debug\… → App\bin\Debug); null — не собран.</summary>
    internal static string? AppExe()
    {
        if (RepoRoot() is not { } root) return null;
        var parts = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        int bin = Array.LastIndexOf(parts, "bin");
        if (bin < 0 || bin + 1 >= parts.Length) return null;
        string dir = Path.Combine(root, "game", "Sword.Game.App", "bin", parts[bin + 1]);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "Sword.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    /// <summary>
    /// Вид фичи: Sword.exe играет записанный сценарий (<c>--replay</c>) ровно <see cref="ViewRequest.Tick"/> тиков — по K тиков
    /// на кадр (наибольший делитель тика до 8), кадр 1280×720 без фокуса; последний кадр — PNG. Хеши App сверяются с записью
    /// Headless: тот же реплей обязан дать то же состояние.
    /// </summary>
    private static ViewResult CaptureView(ViewRequest request, CancellationToken cancel)
    {
        if (AppExe() is not { } exe) return new ViewResult(false, "Sword.exe не собран (dotnet build SWORD.slnx)");
        int k = Enumerable.Range(1, 8).Last(d => request.Tick % (ulong)d == 0);
        var args = new List<string>
        {
            "--replay", request.Replay, "--windowed", "1280x720", "--no-activate", "--ticks-per-frame", k.ToString(CultureInfo.InvariantCulture),
            "--frames", (request.Tick / (ulong)k).ToString(CultureInfo.InvariantCulture), "--shot", request.Png, "--json",
        };
        if (!request.Hud) args.Add("--no-hud");
        if (request.Camera is { } camera) args.AddRange(["--camera", camera]);
        var (report, error) = RunApp(exe, args, cancel);
        if (report is null) return new ViewResult(false, error!);
        var replay = report["replay"];
        bool match = replay?["match"]?.GetValue<bool>() == true;
        if (!File.Exists(request.Png)) return new ViewResult(false, "Sword.exe отработал, а PNG нет");
        string frames = report["report"]?["frames"]?.ToJsonString() ?? "?";
        return match
            ? new ViewResult(true, $"кадр {frames} (после {request.Tick} тиков, {k} на кадр); хеши App совпали с записью Headless (сверено {replay!["hashesChecked"]})")
            : new ViewResult(false, $"хеши App разошлись с записью Headless: {replay?["divergence"]}");
    }

    /// <summary>
    /// Бюджет кадра (точка 4д): Sword.exe играет записанный сценарий бенчем (<c>--replay … --bench N</c>: первые 4 с — прогрев,
    /// vsync выключен, справка скрыта), окно 640×480 без фокуса — как бенч <c>verify.ps1</c> app-smoke. Хеши App сверяются с
    /// записью Headless: замер — ровно той игры, что сценарий.
    /// </summary>
    private static FrameResult MeasureFrame(FrameRequest request, CancellationToken cancel)
    {
        if (AppExe() is not { } exe) return new FrameResult(false, "Sword.exe не собран (dotnet build SWORD.slnx)");
        var (report, error) = RunApp(exe,
        [
            "--replay", request.Replay, "--windowed", "640x480", "--no-activate", "--json",
            "--bench", request.Seconds.ToString(CultureInfo.InvariantCulture),
        ], cancel);
        if (report is null) return new FrameResult(false, error!);
        var bench = report["report"]?["bench"];
        var replay = report["replay"];
        if (bench is null || report["report"]?["exitReason"]?.GetValue<string>() != "bench")
            return new FrameResult(false, $"Sword.exe вышел не по бенчу: {report["report"]?["exitReason"]}");
        long frames = bench["frames"]?.GetValue<long>() ?? 0;
        double avg = bench["averageMs"]?.GetValue<double>() ?? double.NaN, p99 = bench["p99Ms"]?.GetValue<double>() ?? double.NaN,
            max = bench["maxMs"]?.GetValue<double>() ?? double.NaN;
        if (replay?["match"]?.GetValue<bool>() != true)
            return new FrameResult(false, $"хеши App разошлись с записью Headless: {replay?["divergence"]}", frames, avg, p99, max);
        if (frames <= 0) return new FrameResult(false, "кадров замера нет", frames, avg, p99, max);
        return new FrameResult(true, $"кадров замера {frames}: ср {avg} мс, p99 {p99} мс, макс {max} мс; хеши App совпали с записью " +
            $"(сверено {replay["hashesChecked"]}); {report["report"]?["adapter"]}", frames, avg, p99, max);
    }

    /// <summary>Запустить Sword.exe и прочитать итог JSON (последняя строка stdout); ошибка — словами.</summary>
    private static (JsonNode? Report, string? Error) RunApp(string exe, IEnumerable<string> args, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"не запустился {exe}");
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancel.IsCancellationRequested) throw;
            return (null, "Sword.exe не закончил за 180 с");
        }
        string line = LastLine(stdout.GetAwaiter().GetResult());
        JsonNode? report = null;
        try
        {
            report = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
        }
        if (process.ExitCode != 0 || report?["ok"]?.GetValue<bool>() != true)
        {
            string err = report?["error"]?.GetValue<string>() ?? LastLine(stderr.GetAwaiter().GetResult());
            return (null, $"Sword.exe — код {process.ExitCode}: {err}");
        }
        return (report, null);
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    static partial void RunFeatures(string which, Func<ConfigSet, Simulation> build, string content, bool views, bool budgets, bool json,
        ref int? exit)
    {
        if (Features() is not { } setup)
        {
            Log.Error("feature", "--feature: exe вне репозитория (нет SWORD.slnx выше) — манифестов нет");
            exit = 2;
            return;
        }
        var manifests = FeatureManifest.Find(setup.Roots);
        var chosen = which == "all" ? manifests : manifests.Where(m => m.Id == which).ToList();
        if (chosen.Count == 0)
        {
            Log.Error("feature", $"нет фичи «{which}»; есть: {string.Join(", ", manifests.Select(m => m.Id))}, all");
            exit = 2;
            return;
        }
        var env = new FeatureEnvironment
        {
            RepoRoot = setup.RepoRoot, Build = build, Info = ReplayInfo.Current(content), Names = new SwordAgent.ArenaNames(),
            Fields = new BodyFields(), CaptureView = setup.CaptureView, Optimized = setup.Optimized, MeasureFrame = setup.MeasureFrame,
        };
        var reports = new List<FeatureReport>();
        foreach (var m in chosen)
        {
            string dir = Path.Combine(setup.RepoRoot, "artifacts", "features", m.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            var report = FeatureVerifier.Verify(m, env, new FeatureVerifyOptions { OutputDirectory = dir, Views = views, Budgets = budgets });
            reports.Add(report);
            if (report.Ok) Log.Info("feature", $"{m.Id}: ok — {Path.Combine(dir, "report.json")}");
            else
            {
                Log.Warning("feature", $"{m.Id}: провалов {report.Failures.Count} — {Path.Combine(dir, "report.json")}");
                foreach (string f in report.Failures) Log.Warning("feature", $"  {f}");
            }
        }
        if (json)
            Console.WriteLine(new JsonObject
            {
                ["ok"] = reports.All(r => r.Ok),
                ["features"] = new JsonArray([.. reports.Select(r => (JsonNode?)r.Json)]),
            }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        exit = reports.All(r => r.Ok) ? 0 : 4;
    }
}
