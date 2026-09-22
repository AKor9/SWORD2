using Sword.Engine.Agent;
using Sword.Engine.Content;
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
    static partial void RunAgent(Simulation sim, ref int? exit)
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

        var protocol = Task.Run(async () =>
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

        SimulationLoop.Run(sim, host, stop.Token);
        protocol.Wait(TimeSpan.FromSeconds(2));
        Log.Info("agent", $"сеанс {host.SessionId} закончен на тике {sim.Tick}");
        exit = 0;
    }

    /// <summary>Отпечаток пака, если он есть (симуляции контент не нужен, квитанции называют его всё равно).</summary>
    private static string ContentHash()
    {
        try
        {
            if (ContentLocator.Find() is not { } path) return "none";
            using var pak = PakSource.Open(path);
            return pak.ContentHash;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return "none";
        }
    }

    /// <summary><c>artifacts/agent</c> в корне репозитория (папка с SWORD.slnx выше exe), иначе рядом с exe.</summary>
    internal static string ArtifactsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return Path.Combine(dir.FullName, "artifacts", "agent");
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "agent");
    }
}
