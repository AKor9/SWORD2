using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;
using Sword.Game;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;

namespace Sword.Game.Headless;

/// <summary>
/// Симуляция SWORD без окна и D3D12 — та же композиция, что у игры (<see cref="SwordSimulation"/>). Точка 1в: прогон
/// заданного числа тиков с постоянной командой игрока и итог — где игрок, куда смотрит, с какой скоростью. Точка 1е:
/// <c>--agent</c> — сервер MCP на stdio для агента (AgentMode.cs; в сборке без агента его нет).
/// </summary>
public static partial class Program
{
    private const string Usage = """
        Ключи:
          --ticks N           сколько тиков (по умолчанию 600)
          --move X,Z          команда игрока каждый тик: направление по земле (мировые оси); длина не
                              важна: ход — бегом или шагом, как WASD в SWORD
          --walk              шагом, а не бегом
          --gaze X,Z          точка взгляда на земле
          --sample N          точка траектории каждые N тиков (по умолчанию 60)
          --set секция.ключ=значение   конфиг (перекрывает sword.json и переменные SWORD_*)
          --json              итог — одной строкой JSON в stdout (для агента)
          --agent             сервер MCP на stdio для агента (Claude Code, Codex): симуляция на паузе, время
                              двигает агент; stdout — только протокол, журнал — в stderr
        """;

    /// <summary>Режим агента (AgentMode.cs); в сборке без агента вызова нет — <paramref name="exit"/> остаётся null.</summary>
    static partial void RunAgent(Simulation sim, ref int? exit);

    /// <summary>Тиков прогрева перед замером выделений: ёмкости журналов, буферов и очередей выросли.</summary>
    private const int WarmupTicks = 120;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int ticks = 600, sample = 60;
        var command = new PlayerCommand();
        bool json = false, agent = false;
        var sets = new List<string>();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a}: нет значения");
                switch (a)
                {
                    case "--ticks": ticks = Count(a, Next(), min: 0); break;
                    case "--sample": sample = Count(a, Next(), min: 1); break;
                    case "--move": command.Move = Pair(a, Next()); break;
                    case "--walk": command.Walk = true; break;
                    case "--gaze":
                        var g = Pair(a, Next());
                        command.HasGaze = true;
                        command.Gaze = new Vector3(g.X, 0, g.Y);
                        break;
                    case "--set": sets.Add(Next()); break;
                    case "--json": json = true; break;
                    case "--agent": agent = true; break;
                    default: throw new ArgumentException($"неизвестный ключ {a}");
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            return 2;
        }

        if (json || agent) Log.SetSinks(new ConsoleLogSink(allToStandardError: true));
        try
        {
            var config = ConfigSet.Load(AppContext.BaseDirectory, sets);
            var sim = SwordSimulation.Create(config, Animation.CharacterAssets.LoadFromPak());
            config.SkipClientSections();   // клиента нет: привязки клавиш из общего sword.json не читаются
            config.Complete();             // композиция закончена: неизвестная секция конфига — ошибка
            if (agent)
            {
                int? exit = null;
                RunAgent(sim, ref exit);
                if (exit is { } code) return code;
                Log.Error("headless", "--agent: сборка без агента (-p:WithoutAgent=true)");
                return 2;
            }
            var player = GameCore.Player(sim.World);
            var trajectory = new List<object>();
            // Выделения — только тиков (без записи траектории) и после прогрева: раздел 12 плана — 0 байт на тик.
            int warmup = Math.Min(WarmupTicks, ticks / 2);
            long allocated = 0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int t = 0; t < ticks; t++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                // Клиента нет — очередь кадра экспорта тика живёт один тик и не растёт.
                sim.Export.BeginFrame();
                sim.Enqueue(command);
                sim.Step();
                if (t >= warmup) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if ((t + 1) % sample == 0 || t + 1 == ticks) trajectory.Add(State(sim, player));
            }
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            var report = new
            {
                ok = true,
                ticks = sim.Tick,
                tickRate = sim.Settings.TickRate,
                seconds = Math.Round(sim.Tick * (double)sim.Dt, 6),
                msPerTick = ticks > 0 ? Math.Round(ms / ticks, 4) : 0,
                allocatedBytesPerTick = ticks > warmup ? Math.Round(allocated / (double)(ticks - warmup), 3) : 0,
                command = new { move = new[] { command.Move.X, command.Move.Y }, command.Walk, gaze = command.HasGaze ? new[] { command.Gaze.X, command.Gaze.Z } : null },
                player = State(sim, player),
                pose = Pose(sim, player),
                trajectory,
                modules = sim.ActiveModules,
                inactiveModules = sim.InactiveModules,
                entities = sim.World.EntityCount,
                systems = sim.World.Systems.Select(s => s.Name),
                config = sim.Config.Effective,
                configSources = sim.Config.Sources,
            };
            if (json) Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            else
                Log.Info("headless", $"{sim.Tick} тиков ({report.seconds} с): игрок в ({Fmt(report.player.x)}, {Fmt(report.player.z)}), " +
                    $"курс {Fmt(report.player.facing)} рад, скорость {Fmt(report.player.speed)} м/с; {report.msPerTick} мс/тик");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("headless", ex.ToString());
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            return 1;
        }
    }

    /// <summary>X,Z — конечные числа (NaN и бесконечность команда не несёт).</summary>
    private static Vector2 Pair(string key, string text)
    {
        var p = text.Split(',');
        if (p.Length != 2
            || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            || !float.IsFinite(x) || !float.IsFinite(z))
            throw new ArgumentException($"{key}: ожидается X,Z — два конечных числа");
        return new Vector2(x, z);
    }

    private static int Count(string key, string text, int min) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min
            ? n : throw new ArgumentException($"{key}: ожидается целое число не меньше {min}");

    private static PlayerState State(Simulation sim, Sword.Engine.Ecs.Entity player)
    {
        var t = sim.World.Get<Transform>(player);
        var v = sim.World.Get<RootVelocity>(player).Value;
        return new PlayerState(sim.Tick, t.Position.X, t.Position.Z, t.Facing, v.Length());
    }

    private sealed record PlayerState(ulong tick, float x, float z, float facing, float speed);

    /// <summary>
    /// Поза игрока на последнем тике: суставов, наибольшее отклонение сустава от позы привязки (м) и признак «поза
    /// привязки» — без модуля локомоции поза остаётся ею, с модулем — нет (проверка verify.ps1).
    /// </summary>
    private static PoseState? Pose(Simulation sim, Sword.Engine.Ecs.Entity player)
    {
        if (!sim.World.TryView<IAnimationView>(out var view) || !sim.World.TryGet<PoseRef>(player, out var poseRef)) return null;
        var model = view.Model(poseRef);
        var bind = view.ReferenceModel();
        float max = 0;
        for (int j = 0; j < model.Length; j++) max = MathF.Max(max, Vector3.Distance(model[j].Translation, bind[j].Translation));
        // Пустая поза (освобождена или не выделена) — не поза привязки: иначе проверка «без модуля» прошла бы впустую.
        bool bindPose = model.Length > 0 && model.Length == bind.Length && max < 1e-4f;
        return new PoseState(model.Length, max, bindPose);
    }

    private sealed record PoseState(int joints, float maxBindDeviation, bool bindPose);

    private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
