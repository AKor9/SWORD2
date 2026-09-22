using Sword.Engine.Agent;
using Sword.Engine.Agent.Client;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Introspection;
using Sword.Engine.Runtime;
using Sword.Game.Agent;

namespace Sword.Game.App;

/// <summary>
/// <c>Sword.exe --agent</c> (раздел 8 плана): живая игра с сервером MCP по HTTP на 127.0.0.1 (порт <c>--agent-port</c>,
/// по умолчанию <see cref="DefaultAgentPort"/>). Игра идёт как обычно; агент видит мир в точке обслуживания раз в кадр,
/// ставит паузу и шаги, шлёт команды на будущий тик и снимает кадр (<c>capture.bundle</c>). В сборке без агента
/// (<c>-p:WithoutAgent=true</c>) этого файла нет.
/// </summary>
public static partial class Program
{
    /// <summary>Порт агента по умолчанию (<c>.mcp.json</c> репозитория указывает на него).</summary>
    public const int DefaultAgentPort = 47813;

    static partial void StartAgent(ClientHost client, Simulation sim, int port, ref IDisposable? agent)
    {
        var ring = new LogRing();
        Log.AddSink(ring);
        var service = new ServiceQueue();
        var host = new AgentHost(new AgentHostOptions
        {
            Mode = "app",
            ContentHash = ContentHash(),
            Catalog = new Catalog(sim.World, sim.CommandTypes),
            Time = new TimeControl(paused: false, sim.Tick),
            Service = service,
            Log = ring,
            ArtifactsRoot = ArtifactsRoot(),
            Stop = client.RequestClose,
            MaxWait = HttpTransport.MaxWait,
        });
        SwordAgent.Register(host);
        host.Observe(sim);   // история событий, наблюдения, условия — с первого тика игры
        ClientAgent.Attach(host, client);
        var http = new HttpTransport(host.Server, port);
        Log.Info("agent", $"MCP: {http.Url} — сеанс {host.SessionId}, {host.Tools.Count} инструментов");
        agent = new Stopper(host, http);
    }

    /// <summary>
    /// Конец живой игры (окно закрыто): ожидающие запросы и ожидания тиков отменяются — агент сразу получает ошибку, а
    /// не ждёт свой таймаут, — затем закрывается HTTP.
    /// </summary>
    private sealed class Stopper(AgentHost host, HttpTransport http) : IDisposable
    {
        public void Dispose()
        {
            host.Service.CancelAll();
            host.Time.Stop();
            http.Dispose();
        }
    }

    /// <summary><c>artifacts/agent</c> в корне репозитория (папка с SWORD.slnx выше exe), иначе рядом с exe.</summary>
    private static string ArtifactsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return Path.Combine(dir.FullName, "artifacts", "agent");
        return Path.Combine(AppContext.BaseDirectory, "artifacts", "agent");
    }
}
