using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

namespace Sword.Engine.Agent.Tests;

/// <summary>
/// Время жизни сервера агента: конец входа не ждёт вызов, не отвечающий на отмену; простой считается только
/// по вызовам инструментов; родитель и дети процесса — <see cref="ProcessLifetime"/>, <see cref="KillOnCloseJob"/>.
/// Процессы самого Headless (конец stdin, снятая обёртка, простой) проверяет <c>verify.ps1</c> шаг <c>agent-smoke</c>.
/// </summary>
public class LifetimeTests
{
    /// <summary>Инструмент, который ждёт ворота и не смотрит на отмену.</summary>
    private static AgentTool Stuck(Task gate) => new()
    {
        Name = "test.stuck",
        Title = "застрявший вызов",
        Description = "ждёт ворота, отмену не проверяет",
        Run = async _ =>
        {
            await gate.ConfigureAwait(false);
            return new ToolOutput(new JsonObject { ["done"] = true });
        },
    };

    [Fact]
    public async Task StdioEof_DoesNotWaitForCallIgnoringCancel()
    {
        using var f = new AgentFixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Add(Stuck(gate.Task));
        var input = new Pipe();
        var run = StdioTransport.Run(f.Host.Server, input.Reader.AsStream(), new MemoryStream(), CancellationToken.None);
        await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"test.stuck"}}""" + "\n"));
        await Task.Delay(100);
        var watch = Stopwatch.StartNew();
        await input.Writer.CompleteAsync();                     // клиент ушёл, вызов висит
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(watch.Elapsed < StdioTransport.EofGrace + StdioTransport.CancelGrace + TimeSpan.FromSeconds(2), $"{watch.Elapsed}");
        gate.SetResult();
    }

    [Fact]
    public async Task Idle_CountsOnlyToolCalls()
    {
        using var f = new AgentFixture();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Host.Add(Stuck(gate.Task));
        var call = f.Tool("test.stuck");
        await Task.Delay(300);
        Assert.Equal(TimeSpan.Zero, f.Host.Server.IdleFor);     // вызов исполняется — простоя нет
        gate.SetResult();
        await call;
        await Task.Delay(300);
        var idle = f.Host.Server.IdleFor;
        Assert.True(idle >= TimeSpan.FromMilliseconds(250), $"{idle}");
        await f.Rpc("ping");                                    // ping и tools/list — не работа агента
        await f.Rpc("tools/list");
        Assert.True(f.Host.Server.IdleFor >= idle);
        await f.Host.Server.WhenIdle(TimeSpan.FromMilliseconds(400), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await f.Tool("session.status");
        Assert.True(f.Host.Server.IdleFor < TimeSpan.FromMilliseconds(250));
    }

    private static Process Ping() => Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    })!;

    [Fact]
    public async Task Parent_And_Exit_OfChildProcess()
    {
        using var child = Ping();
        try
        {
            Assert.Equal(Environment.ProcessId, ProcessLifetime.ParentId(child));
            var started = child.StartTime;
            var exited = ProcessLifetime.WhenExited(child.Id, started, out string watching);
            Assert.Contains(child.Id.ToString(), watching);
            await Task.Delay(200);
            Assert.False(exited.IsCompleted);
            child.Kill();
            await exited.WaitAsync(TimeSpan.FromSeconds(5));
            // Номер занят процессом, запущенным позже того, за кем следили, — того уже нет; процесса нет — тоже.
            using var other = Ping();
            try
            {
                Assert.True(ProcessLifetime.WhenExited(other.Id, other.StartTime.AddMinutes(-1), out _).IsCompleted);
            }
            finally
            {
                other.Kill();
            }
            Assert.True(ProcessLifetime.WhenExited(child.Id, started, out _).IsCompleted);
            // Родитель тестового процесса жив — ожидание его конца не завершено.
            var parent = ProcessLifetime.WhenParentExited(out string parentName);
            await Task.Delay(100);
            Assert.False(parent.IsCompleted, parentName);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public async Task KillOnCloseJob_EndsItsProcesses()
    {
        using var child = Ping();
        try
        {
            var job = KillOnCloseJob.Create();
            Assert.True(job is not null, KillOnCloseJob.LastError);
            Assert.True(job.Add(child), KillOnCloseJob.LastError);
            await Task.Delay(100);
            Assert.False(child.HasExited);
            job.Dispose();                                      // последняя ручка задания — процессы завершаются
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
