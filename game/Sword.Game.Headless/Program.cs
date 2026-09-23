using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;
using Sword.Game;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;
using Sword.Game.Movement;

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
          --units N           врагов при старте (0…32, больше и меньше — упирается в предел, как --units SWORD):
                              то же, что --set arena.enemies=N (в sword.json — 10)
          --set секция.ключ=значение   конфиг (перекрывает sword.json и переменные SWORD_*)
          --record FILE       записать реплей (.swreplay): конфиг, модули, команды по тикам, хеши состояния
          --hash-every K      хеш состояния в реплее каждые K тиков (по умолчанию 1)
          --replay FILE       воспроизвести реплей (свой конфиг, свои команды; --move, --walk, --gaze нельзя): тиков —
                              сколько в реплее (или --ticks), хеши сверяются по участникам; первое расхождение —
                              тик и участник, код выхода 3. С --record — перезапись (новые хеши тех же команд: эталон
                              после намеренного изменения, хеши сценария без хешей); --set поверх — эксперимент
          --json              итог — одной строкой JSON в stdout (для агента)
          --profile           профайлер CPU: время и выделения тика, стадий, систем, барьеров, инвариантов (после
                              прогрева — 120 тиков); на тик — CPU потока, GC, JIT; отчёт — artifacts/profile/<прогон>/
                              profile.json, в --json — секция profile; с --agent — включён с начала сеанса
                              (profile.stages), отчёт — при выходе
          --profile runtime   то же и диагностический режим: события среды (выделения по типам, сборки, JIT с
                              уровнями) с тиком и зоной — секция events отчёта; дорогой (JIT медленнее)
          --profile-threshold MS  порог аномалии тика, мс (без ключа — медиана последних 128 тиков + max(1 мс,
                              0.75 медианы)); аномалии — файлы artifacts/profile/<прогон>/anomaly-tick-N.json и строки журнала
          --profile-trace     то же, что --profile, и кольцо профайлера в Chrome Trace (trace.json рядом с
                              profile.json: Perfetto, chrome://tracing)
                              Без ключа итог --json всё равно несёт machine: машина за тики после прогрева (поток на
                              CPU, загрузка системы другими процессами, частота, GC, JIT) и вердикт «код или машина»
          --agent             сервер MCP на stdio для агента (Claude Code, Codex): симуляция на паузе, время
                              двигает агент; stdout — только протокол, журнал — в stderr
          --feature ID|all    приёмка фичи по манифесту feature.json (feature.verify): сценарии, инварианты,
                              метрики, бюджеты, виды (кадры Sword.exe --replay); артефакты — artifacts/features/ID;
                              код выхода 4 — провал. --no-views — без видов, --no-budgets — без бюджетов
        """;

    /// <summary>
    /// Режим агента (AgentMode.cs); в сборке без агента вызова нет — <paramref name="exit"/> остаётся null.
    /// <paramref name="build"/> — симуляция из конфига той же композицией (<c>session.start</c>, <c>time.seek</c> назад).
    /// </summary>
    static partial void RunAgent(Simulation sim, Func<ConfigSet, Simulation> build, bool profile, bool profileEvents, double profileThreshold,
        ref int? exit);

    /// <summary>Приёмка фич (<c>--feature</c>, AgentMode.cs); в сборке без агента вызова нет — <paramref name="exit"/> остаётся null.</summary>
    static partial void RunFeatures(string which, Func<ConfigSet, Simulation> build, string content, bool views, bool budgets, bool json,
        ref int? exit);

    /// <summary>Тиков прогрева перед замером выделений: ёмкости журналов, буферов и очередей выросли.</summary>
    private const int WarmupTicks = 120;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int ticks = 600, sample = 60, hashEvery = 1;
        bool ticksSet = false, commandSet = false;
        var command = new PlayerCommand();
        bool json = false, agent = false, profile = false, profileEvents = false, profileTrace = false;
        double profileThreshold = 0;
        string? record = null, replayPath = null, feature = null;
        bool views = true, budgets = true;
        var sets = new List<string>();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a}: нет значения");
                switch (a)
                {
                    case "--ticks":
                        ticks = Count(a, Next(), min: 0);
                        ticksSet = true;
                        break;
                    case "--sample": sample = Count(a, Next(), min: 1); break;
                    case "--move":
                        command.Move = Pair(a, Next());
                        commandSet = true;
                        break;
                    case "--walk":
                        command.Walk = true;
                        commandSet = true;
                        break;
                    case "--gaze":
                        var g = Pair(a, Next());
                        command.HasGaze = true;
                        command.Gaze = new Vector3(g.X, 0, g.Y);
                        commandSet = true;
                        break;
                    case "--record": record = Next(); break;
                    case "--hash-every": hashEvery = Count(a, Next(), min: 1); break;
                    case "--replay": replayPath = Next(); break;
                    case "--set": sets.Add(Next()); break;
                    case "--units": sets.Add(UnitsSet(a, Next())); break;
                    case "--json": json = true; break;
                    case "--agent": agent = true; break;
                    case "--profile":
                        profile = true;
                        if (i + 1 < args.Length && args[i + 1] == "runtime")
                        {
                            profileEvents = true;
                            i++;
                        }
                        break;
                    case "--profile-trace":
                        profile = true;
                        profileTrace = true;
                        break;
                    case "--profile-threshold":
                        if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out profileThreshold)
                            || !(profileThreshold >= 0 && profileThreshold <= 60_000))
                            throw new ArgumentException($"{a}: ожидается порог в мс 0…60000 (0 — по медиане)");
                        break;
                    case "--feature": feature = Next(); break;
                    case "--no-views": views = false; break;
                    case "--no-budgets": budgets = false; break;
                    default: throw new ArgumentException($"неизвестный ключ {a}");
                }
            }
            if (replayPath is not null && commandSet) throw new ArgumentException("--replay: команды — из реплея; --move, --walk, --gaze нельзя");
            if (agent && (replayPath is not null || record is not null))
                throw new ArgumentException("--agent: реплеи — инструментами агента (session.start, replay.save), не ключами");
            if (feature is not null && (agent || replayPath is not null || record is not null || commandSet || sets.Count > 0 || profile))
                throw new ArgumentException("--feature: сценарии и конфиг — из манифеста; --agent, --replay, --record, --move, --set, --units, --profile нельзя " +
                    "(время систем бюджеты мерят сами — отдельным прогоном с профайлером)");
            if (feature is null && (!views || !budgets)) throw new ArgumentException("--no-views и --no-budgets — только с --feature");
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
            string content = ContentHash();
            Replay? replay = replayPath is null ? null : Replay.Load(replayPath);
            var warnings = new List<string>();
            if (replay is not null) CheckReplay(replay, content, warnings);
            var config = replay?.CreateConfig(sets) ?? ConfigSet.Load(AppContext.BaseDirectory, sets);
            if (replay is not null && sets.Count > 0) warnings.Add($"конфиг реплея изменён: {string.Join(", ", sets)} — эксперимент, хеши могут разойтись");
            var assets = Animation.CharacterAssets.LoadFromPak();
            // Та же композиция для каждой симуляции процесса: ассеты общие и неизменяемые.
            Simulation Build(ConfigSet c)
            {
                var s = SwordSimulation.Create(c, assets);
                c.SkipClientSections();   // клиента нет: привязки клавиш из общего sword.json не читаются
                c.Complete();             // композиция закончена: неизвестная секция конфига — ошибка
                return s;
            }
            if (feature is not null)
            {
                int? done = null;
                RunFeatures(feature, Build, content, views, budgets, json, ref done);
                if (done is { } code) return code;
                Log.Error("headless", "--feature: сборка без агента (-p:WithoutAgent=true) — приёмка фич — инструмент агента");
                return 2;
            }
            var sim = Build(config);
            var playback = replay is null ? null : sim.Play(replay);
            if (playback is not null) warnings.AddRange(playback.Warnings);
            foreach (var w in warnings) Log.Warning("replay", w);
            var recorder = record is null ? null : sim.Record(ReplayInfo.Current(content), hashEvery);
            if (replay is not null && !ticksSet) ticks = checked((int)replay.Ticks);
            if (agent)
            {
                int? exit = null;
                RunAgent(sim, Build, profile, profileEvents, profileThreshold, ref exit);
                if (exit is { } code) return code;
                Log.Error("headless", "--agent: сборка без агента (-p:WithoutAgent=true)");
                return 2;
            }
            var trajectory = new List<object>();
            // Выделения — только тиков (без записи траектории) и после прогрева: раздел 12 плана — 0 байт на тик.
            int warmup = Math.Min(WarmupTicks, ticks / 2);
            // Профайлер CPU (точка Па): итоги — тики после прогрева, как байты на тик; байты тика с ним — те же 0.
            var profiler = profile ? new CpuProfiler() : null;
            string profileDir = ProfileDir("headless");
            if (profiler is not null)
            {
                sim.AttachProfiler(profiler);
                // Захват аномалий тика (точка Пв): файлы — в каталог прогона профайлера, рядом с profile.json.
                profiler.CaptureAnomalies(new AnomalyOptions
                {
                    Directory = profileDir, AbsoluteMs = profileThreshold,
                    Run = new Dictionary<string, string>
                    {
                        ["process"] = "headless", ["args"] = string.Join(" ", args), ["runtime"] = Environment.Version.ToString(),
                    },
                });
                profiler.SetEnabled(true);
                if (profileEvents) profiler.SetEvents(true);
            }
            long allocated = 0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            // Машина за тики после прогрева (точка Пб): снимки — на границах, вне байтов тиков.
            MachineSample? machineStart = null;
            for (int t = 0; t < ticks; t++)
            {
                if (t == warmup)
                {
                    profiler?.Reset();
                    machineStart = MachineSample.Take();
                }
                long before = GC.GetAllocatedBytesForCurrentThread();
                // Клиента нет — очередь кадра экспорта тика живёт один тик и не растёт.
                sim.Export.BeginFrame();
                if (playback is null) sim.Enqueue(command);
                sim.Step();
                if (t >= warmup) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if ((t + 1) % sample == 0 || t + 1 == ticks) trajectory.Add(State(sim));
            }
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var machine = machineStart is { } m0 ? MachineReport.Between(m0, MachineSample.Take(), computeBound: true) : null;
            Replay? recorded = recorder?.ToReplay();
            recorded?.Save(record!);
            string? profileFile = profiler?.Write(Path.Combine(profileDir, "profile.json"), ProfileHeader("headless", args));
            if (profileFile is not null && !json) Log.Info("profile", $"профайлер: {profileFile}");
            // Точка Пв: кольцо в Chrome Trace (--profile-trace) и итог захвата аномалий.
            string? traceFile = profileTrace
                ? profiler!.WriteTrace(Path.Combine(profileDir, "trace.json"), new Dictionary<string, string> { ["process"] = "headless" })
                : null;
            if (traceFile is not null && !json) Log.Info("profile", $"трасса (Perfetto, chrome://tracing): {traceFile}");
            if (profiler?.Anomalies is { Detected: > 0 } anomalies && !json) Log.Info("profile", anomalies.Summary());

            var report = new
            {
                ok = true,
                ticks = sim.Tick,
                tickRate = sim.Settings.TickRate,
                seconds = Math.Round(sim.Tick * (double)sim.Dt, 6),
                msPerTick = ticks > 0 ? Math.Round(ms / ticks, 4) : 0,
                allocatedBytesPerTick = ticks > warmup ? Math.Round(allocated / (double)(ticks - warmup), 3) : 0,
                command = new { move = new[] { command.Move.X, command.Move.Y }, command.Walk, gaze = command.HasGaze ? new[] { command.Gaze.X, command.Gaze.Z } : null },
                player = State(sim),
                pose = Pose(sim, GameCore.Player(sim.World)),
                arena = ArenaJson(sim),
                stateHash = StateHashJson(sim),
                replay = playback is null ? null : ReplayJson(replayPath!, playback),
                // Инварианты (этап 3г): сколько проверялось и нарушений за прогон; первые — строками.
                invariants = new
                {
                    enabled = sim.Invariants.List().Count(i => i.Enabled),
                    violations = sim.Invariants.Total,
                    first = sim.Invariants.Recent().Take(5).Select(v => v.ToString()),
                    // Стоимость (раздел 8 плана: инвариант ничего не меняет, его стоимость измеряется) — мкс на проверку.
                    microseconds = sim.Invariants.List().Where(i => i.Evaluations > 0)
                        .ToDictionary(i => i.Id, i => Math.Round(i.MeanMicroseconds, 3)),
                },
                recorded = recorded is null ? null : new
                {
                    file = Path.GetFullPath(record!), ticks = recorded.Ticks, hashes = recorded.Hashes.Count, runs = recorded.Runs.Count,
                    commands = recorded.CommandCount,
                },
                warnings,
                // Профайлер (--profile, точка Па): файл отчёта и главные зоны тика; без ключа — null.
                profile = profiler is null ? null : ProfileJson(profiler, profileFile!, traceFile),
                // Машина за тики после прогрева (точка Пб): поток на CPU, загрузка системы, частота, GC, JIT и вердикт.
                machine,
                trajectory,
                modules = sim.ActiveModules,
                inactiveModules = sim.InactiveModules,
                entities = sim.World.EntityCount,
                systems = sim.World.Systems.Select(s => s.Name),
                config = sim.Config.Effective,
                configSources = sim.Config.Sources,
            };
            profiler?.StopAnomalies();   // писатель аномалий — дописан (Write ждал его) и остановлен
            if (json) Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            else
            {
                Log.Info("headless", $"{sim.Tick} тиков ({report.seconds} с): игрок в ({Fmt(report.player.x)}, {Fmt(report.player.z)}), " +
                    $"курс {Fmt(report.player.facing)} рад, скорость {Fmt(report.player.speed)} м/с; {report.msPerTick} мс/тик" +
                    (machine is not null ? $"; {machine.Text}" : ""));
                if (playback is not null)
                    Log.Info("replay", playback.Divergence is { } d ? d.ToString()
                        : playback.Replay.Hashes.Count == 0 ? "в реплее нет хешей (сценарий) — сверять нечего; хеши даёт --record"
                        : $"реплей совпал: сверено хешей {playback.HashesChecked}, последний — после {playback.LastMatch?.ToString() ?? "—"} тиков");
                if (recorded is not null) Log.Info("replay", $"записан {Path.GetFullPath(record!)}: {recorded.Ticks} тиков, хешей {recorded.Hashes.Count}");
                if (sim.Invariants.Total > 0) Log.Warning("invariant", $"нарушений инвариантов за прогон: {sim.Invariants.Total}");
            }
            return playback?.Divergence is null ? 0 : 3;
        }
        catch (Exception ex)
        {
            Log.Error("headless", ex.ToString());
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            return 1;
        }
    }

    /// <summary>
    /// Реплей подходит этой сборке: модули реплея в ней есть (иначе команды и конфиг не привяжутся), контент тот же
    /// (иначе хеши не значат ничего); другая сборка — предупреждение (хеши скажут, изменилась ли логика).
    /// </summary>
    private static void CheckReplay(Replay replay, string content, List<string> warnings)
    {
        var known = SwordSimulation.Modules().Select(m => m.Id).ToList();
        var missing = (replay.Modules ?? []).Concat(replay.InactiveModules ?? []).Where(m => !known.Contains(m)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"реплей записан с модулями {string.Join(", ", missing)}, а в этой сборке их нет (есть: {string.Join(", ", known)})");
        if (replay.Content != "none" && content != "none" && replay.Content != content)
            throw new InvalidDataException($"реплей записан на контенте {replay.Content}, а пак здесь — {content}: хеши сравнивать бессмысленно");
        var current = ReplayInfo.Current(content).Build;
        string commit = Replay.CommitOf(replay.Build);
        if (commit.Length > 0 && commit != Replay.CommitOf(current)) warnings.Add($"реплей записан сборкой {replay.Build}, это — {current}");
    }

    private static object ReplayJson(string path, ReplayPlayer player) => new
    {
        file = Path.GetFullPath(path),
        ticks = player.Length,
        recordedBy = player.Replay.RecordedBy,
        build = player.Replay.Build,
        match = player.Divergence is null,
        hashesChecked = player.HashesChecked,
        hashesLeft = player.HashesLeft,
        lastMatch = player.LastMatch,
        divergence = player.Divergence is { } d ? new
        {
            tick = d.Tick,
            lastMatch = d.LastMatch,
            parts = d.Parts.Select(p => new { part = p.Part, expected = $"{p.Expected:X16}", actual = $"{p.Actual:X16}" }),
        } : null,
    };

    /// <summary>Корень репозитория — папка с SWORD.slnx выше exe; null — exe вне репозитория.</summary>
    internal static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// Каталог прогона профайлера (<c>--profile</c>, точки Па и Пв): <c>artifacts/profile/&lt;дата-время старта&gt;-&lt;процесс&gt;-&lt;pid&gt;</c>
    /// в корне репозитория, вне репозитория — рядом с exe: файлы аномалий (по ходу), <c>profile.json</c> и <c>trace.json</c> (в конце).
    /// Создаётся при первой записи.
    /// </summary>
    internal static string ProfileDir(string process) =>
        Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "artifacts", "profile",
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{process}-{Environment.ProcessId}");


    /// <summary>Поля прогона в начале отчёта профайлера: процесс, ключи запуска, время, среда.</summary>
    internal static System.Text.Json.Nodes.JsonObject ProfileHeader(string process, string[] args) => new()
    {
        ["process"] = process,
        ["args"] = new System.Text.Json.Nodes.JsonArray([.. args.Select(a => (System.Text.Json.Nodes.JsonNode?)a)]),
        ["finished"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
        ["runtime"] = Environment.Version.ToString(),
        ["optimized"] = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>().FirstOrDefault()?.IsJITOptimizerDisabled != true,
    };

    /// <summary>Секция <c>profile</c> итога <c>--json</c>: файл отчёта, трасса и коротко — главные зоны, аномалии.</summary>
    internal static System.Text.Json.Nodes.JsonObject ProfileJson(CpuProfiler profiler, string file, string? trace)
    {
        var json = profiler.Summary(12);
        json.Insert(0, "file", file);
        if (trace is not null) json.Insert(1, "trace", trace);
        return json;
    }

    /// <summary>Отпечаток пака, если он есть (симуляции контент не нужен, реплей и квитанции называют его всё равно).</summary>
    internal static string ContentHash()
    {
        try
        {
            if (Sword.Engine.Content.ContentLocator.Find() is not { } path) return "none";
            using var pak = Sword.Engine.Content.PakSource.Open(path);
            return pak.ContentHash;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return "none";
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

    /// <summary>
    /// Корень игрока после тика. Игрока ищем каждый раз, а не держим сущность с начала прогона (сброс арены — команда R
    /// в реплее — строит его заново, сейчас на той же сущности).
    /// </summary>
    private static PlayerState State(Simulation sim)
    {
        var player = GameCore.Player(sim.World);
        var t = sim.World.Get<Transform>(player);
        var v = sim.World.Get<RootVelocity>(player).Value;
        return new PlayerState(sim.Tick, t.Position.X, t.Position.Z, t.Facing, v.Length());
    }

    private sealed record PlayerState(ulong tick, float x, float z, float facing, float speed);

    /// <summary>
    /// Арена после прогона (этап 4а): заданное число врагов, бойцов в мире и враги по местам — корень, курс, расстояние
    /// до игрока (кольцо спауна 5–9 м); с 4б — скорость корня и ИИ последнего тика: подход и покачивание, м/с, соседей
    /// ближе 1,4 м (<see cref="EnemyAiTerms"/>); с 4в — толчки столкновений последнего тика (<see cref="RootContacts"/>:
    /// пар, сдвиг корня парами и от игрока, м, наибольшее перекрытие и с кем) и ближайшая пара бойцов на арене
    /// (<c>closestPair</c>: расстояние корней, м, и места; ближе 0,7 м — перекрытие, инвариант <c>movement.rootOverlap</c>);
    /// с 4г — слой действий последнего тика (<see cref="AnimatorAction"/>: действие, вес, поворот курса клипом за тик).
    /// </summary>
    private static object ArenaJson(Simulation sim)
    {
        var world = sim.World;
        var player = world.Get<Transform>(GameCore.Player(world)).Position;
        var enemies = new List<(int Slot, object Row)>();
        var roots = new List<(int Slot, Vector3 Root)>();
        int fighters = 0;
        foreach (var chunk in world.Iterate(world.Query().With<Fighter>()))
            foreach (var e in chunk.Entities)
            {
                fighters++;
                if (!world.TryGet<FighterSlot>(e, out var slot)) continue;
                var t = world.Get<Transform>(e);
                roots.Add((slot.Index, t.Position));
                if (!world.Has<EnemyTag>(e)) continue;
                var ai = world.TryGet<EnemyAiTerms>(e, out var terms) ? terms : default;
                var push = world.TryGet<RootContacts>(e, out var contacts) ? contacts : RootContacts.None;
                var action = world.TryGet<AnimatorAction>(e, out var act) ? act : AnimatorAction.None;
                enemies.Add((slot.Index, new
                {
                    slot = slot.Index, entity = e.ToString(), x = t.Position.X, z = t.Position.Z, facing = t.Facing,
                    distance = Vector3.Distance(t.Position with { Y = 0 }, player with { Y = 0 }),
                    speed = world.Get<RootVelocity>(e).Value.Length(), approach = ai.ApproachSpeed, sway = ai.SwaySpeed,
                    neighbours = ai.Neighbours, contacts = push.Contacts, push = push.Push.Length(), fromPlayer = push.FromPlayer.Length(),
                    deepest = push.Deepest, deepestWith = push.DeepestWith, obstacle = push.Obstacle.Length(),
                    action = action.Kind.ToString(), actionWeight = action.Weight, yawDelta = action.YawDelta,
                }));
            }
        (float Distance, int A, int B)? closest = null;
        for (int i = 0; i < roots.Count; i++)
            for (int j = i + 1; j < roots.Count; j++)
            {
                float d = (roots[i].Root - roots[j].Root).Length();
                if (closest is null || d < closest.Value.Distance)
                    closest = (d, Math.Min(roots[i].Slot, roots[j].Slot), Math.Max(roots[i].Slot, roots[j].Slot));
            }
        return new
        {
            enemies = world.Resource<ArenaState>().Enemies, fighters,
            closestPair = closest is { } c ? new { distance = c.Distance, slots = new[] { c.A, c.B } } : null,
            places = enemies.OrderBy(p => p.Slot).Select(p => p.Row),
        };
    }

    /// <summary><c>--units N</c> SWORD: врагов при старте, в пределах 0…32 (<c>Math.Clamp</c>, как <c>Program.cs</c> SWORD).</summary>
    internal static string UnitsSet(string key, string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)
            ? $"arena.enemies={Math.Clamp(n, 0, ArenaState.MaxEnemies)}" : throw new ArgumentException($"{key}: ожидается целое число");

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

    /// <summary>Хеш состояния после прогона (этап 3): итог и участники — шестнадцатеричными строками.</summary>
    private static object StateHashJson(Simulation sim)
    {
        var hash = sim.HashState();
        return new { total = $"{hash.Total:X16}", parts = hash.Parts.ToDictionary(p => p.Key, p => $"{p.Value:X16}") };
    }

    private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
