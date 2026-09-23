using System.Globalization;
using System.Text;
using System.Text.Json;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.App;

/// <summary>
/// Точка входа игры SWORD: композиция симуляции (<see cref="SwordSimulation"/> — та же, что у Headless), клиента
/// (<see cref="ArenaClient"/>) и привязок ввода (секция конфига <c>input</c>: действия игры и клиента движка). Точка 1г:
/// WASD относительно камеры, курсор — взгляд, колесо — зум; <c>--move</c> заменяет ввод заданной командой (проверочные
/// прогоны). Точка 1д: HUD (FPS, справка — F1), снимок F4, бенч кадра (<c>--bench</c>). Точка 1е: <c>--agent</c> — сервер
/// MCP для агента (AgentMode.cs; в сборке без агента его нет).
/// </summary>
public static partial class Program
{
    /// <summary>Режим агента (AgentMode.cs); в сборке без агента вызова нет — <paramref name="agent"/> остаётся null.</summary>
    static partial void StartAgent(ClientHost client, Simulation sim, int port, ref IDisposable? agent);

    private sealed class Options
    {
        public int Width = 1600, Height = 900;
        public bool Fullscreen;
        public bool Borderless;
        public bool Activate = true;
        public bool Vsync;
        public bool Debug;
        public bool GpuValidation;
        public int Frames;
        public bool ResizeCycle;
        public bool Json;
        public string? Shot;
        public CameraPose? Camera;
        public PlayerCommand? Command;
        public int TicksPerFrame;
        public double FrameSeconds;
        public double MaxFps;
        public bool NoHud;
        public double Bench;
        public string? Screenshots;
        public bool Agent;
        public bool Profile;
        public bool ProfileEvents;
        public int AgentPort = 47813;
        public string? Record;
        public string? Replay;
        public int HashEvery = 1;
        public readonly List<string> Hold = [];
        public readonly List<string> Sets = [];
        public readonly List<string> Overlays = [];
    }

    private const string Usage = """
        Управление (привязки — секция конфига input, например --set input.move_forward=Up):
          WASD — ход относительно камеры, Shift — шагом, курсор на земле — куда смотреть;
          колесо, +/− — зум; Z/C — поворот камеры; средняя кнопка — поворот и наклон мышью;
          F — камера за игроком / свободная (стрелки — панорама); F1 — справка; F4 — снимок; Esc — выход;
          PageUp/PageDown — врагов ±1, R — сброс арены (команды арены — в реплее); F7 — капсулы тел поверх персонажей.
        Ключи:
          --windowed WxH    размер окна (по умолчанию 1600x900); окно с рамой, размер меняется мышью
          --borderless      окно без рамы заданного размера по центру (как игровое окно SWORD)
          --fullscreen      окно без рамы на весь монитор
          --vsync           вертикальная синхронизация
          --debug           отладочный слой D3D12
          --gpu-validation  отладочный слой с GPU-валидацией (состояния ресурсов из bindless-кучи; медленно)
          --frames N        выйти после N кадров (проверочный прогон)
          --resize-cycle    менять размер окна по кругу (проверка пересоздания задних буферов)
          --no-activate     не отбирать фокус: окно показано внизу порядка перекрытия
          --shot FILE.png   снять кадр в PNG: последний кадр прогона (--frames), без него — 30-й; с --bench — последний
                            кадр замера
          --screenshots DIR куда класть снимки F4 (по умолчанию screenshots в текущем каталоге)
          --no-hud          без HUD (FPS, справка): снимки, сравнимые между прогонами
          --overlay NAME    отладочный оверлей поверх кадра с первого кадра (можно несколько раз): capsules — капсулы тел
                            бойцов (как F7); так снимаются виды фичи с оверлеем (views[].overlays манифеста)
          --agent           сервер MCP для агента (Claude Code, Codex) по HTTP на 127.0.0.1: мир, время, команды,
                            снимок кадра; порт — --agent-port N (по умолчанию 47813)
          --bench N         бенч кадра: N с игрового времени (первые 4 — прогрев), затем выход с итогом (кадр ср.,
                            p50, p99, макс, CPU); vsync выключен, справка скрыта, F4 не действует
          --camera Y,P,D[,X,Z]  неподвижная камера (проверочные снимки): поворот и наклон (рад), дистанция (м),
                            точка взгляда (м); управление камерой выключено; наклон 0.45…1.45, дистанция 6…110;
                            без ключа — камера SWORD за игроком, 0.6,0.95,24
          --move X,Z        заданная команда игрока каждый тик вместо клавиатуры и мыши: направление по земле
                            (мировые оси); длина не важна — бегом или шагом, как WASD в SWORD
          --walk            заданная команда: шагом (без --move — стоять)
          --gaze X,Z        заданная команда: точка взгляда на земле
                            (любой из трёх ключей заменяет ввод игрока заданной командой; камера управляется)
          --ticks-per-frame N  ровно N тиков на кадр (проверочные прогоны); без ключа — по часам
          --hold A[,B…]     удерживать действия ввода весь прогон (имена — как в секции input, например
                            move_forward,walk): управление без клавиатуры; в первом кадре действие ещё и нажато
          --max-fps N       не больше N кадров в секунду — проверить плавность при низкой частоте кадров вживую
          --fps N           шаг кадра для симуляции — как при N кадрах в секунду, а не по часам: тики и доля тика
                            (интерполяция) повторяются от прогона к прогону
          --units N         врагов при старте (0…32, больше и меньше — упирается в предел, как --units SWORD): то же,
                            что --set arena.enemies=N (в sword.json — 10)
          --set секция.ключ=значение  конфиг (перекрывает sword.json и переменные SWORD_*)
          --record FILE     записать реплей (.swreplay) — команды по тикам и хеши состояния; воспроизводит его
                            Sword.Headless --replay FILE (с агентом реплей пишется всегда — replay.save)
          --hash-every K    хеш состояния в реплее каждые K тиков (по умолчанию 1)
          --replay FILE     воспроизвести реплей (.swreplay): его конфиг (--set — поверх) и его команды игрока — клавиатура
                            и мышь игроком не управляют, камера — как обычно; хеши сверяются по участникам (итог --json —
                            replay); после конца реплея игрок стоит. Так снимаются виды фичи (feature.verify)
          --json            итог — одной строкой JSON в stdout (для агента)
          --profile         профайлер CPU с первого кадра: время и выделения фаз кадра, тиков, стадий, систем,
                            инвариантов, проходов графа, зон игры; на кадр и тик — CPU потока, GC, JIT; машина и
                            вердикт «код или машина»; отчёт — artifacts/profile/<прогон>/profile.json, в --json — секция
                            profile; с --bench — только кадры замера. Агент включает его и без ключа (profile.stages)
          --profile runtime то же и диагностический режим: события среды (выделения по типам, сборки, JIT с уровнями)
                            с кадром, тиком и зоной — секция events отчёта; дорогой (JIT медленнее)
        """;

    public static int Main(string[] args)
    {
        // Вывод — UTF-8: его читают и люди, и агенты.
        Console.OutputEncoding = Encoding.UTF8;

        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // При --json stdout — только итог для агента; журнал уходит в stderr.
        if (options.Json) Log.SetSinks(new ConsoleLogSink(allToStandardError: true));

        try
        {
            // Бенч кадра — без инвариантов (раздел 8 плана: их стоимость меряется отдельно); --set перекрывает.
            List<string> sets = options.Bench > 0 ? ["sim.invariants=false", .. options.Sets] : options.Sets;
            // Реплей: только его конфиг (ни файлов, ни переменных) и --set поверх — как у Sword.Headless --replay.
            string content = ContentHash();
            Replay? replay = options.Replay is null ? null : Replay.Load(options.Replay);
            if (replay is not null && replay.Content != "none" && content != "none" && replay.Content != content)
                throw new InvalidDataException($"реплей записан на контенте {replay.Content}, а пак здесь — {content}: хеши сравнивать бессмысленно");
            var config = replay?.CreateConfig(sets) ?? ConfigSet.Load(AppContext.BaseDirectory, sets);
            var simulation = SwordSimulation.Create(config, Sword.Game.Animation.CharacterAssets.LoadFromPak());
            var playback = replay is null ? null : simulation.Play(replay);
            if (options.Overlays.FirstOrDefault(o => !ArenaClient.KnownOverlays.Contains(o)) is { } unknown)
                throw new ArgumentException($"--overlay {unknown}: такого оверлея нет; есть: {string.Join(", ", ArenaClient.KnownOverlays)}");
            var arena = new ArenaClient(options.Camera, options.Command, hud: !options.NoHud, playerCommands: replay is null,
                showBodies: options.Overlays.Contains(ArenaClient.CapsulesOverlay));
            var bindings = InputBindings.Resolve([.. arena.Actions, .. ClientHost.Actions], config);
            arena.UseBindings(bindings.Keys);
            if (options.Bench > 0) arena.Hud.ShowHelp = false;   // SWORD: ShowHelp = !bench
            config.Complete();   // композиция закончена (симуляция, клиент, ввод): неизвестная секция конфига — ошибка
            // Реплей — с тика 0: по ключу --record и всегда с агентом (replay.save).
            var recorder = options.Record is not null || options.Agent
                ? simulation.Record(ReplayInfo.Current(content), options.HashEvery) : null;
            using var window = new Win32Window(new WindowOptions(
                Title: "SWORD",
                Width: options.Width,
                Height: options.Height,
                Activate: options.Activate,
                Fullscreen: options.Fullscreen,
                Resizable: !options.Fullscreen && !options.Borderless));

            ClientRunReport report;
            CpuProfiler profiler;
            using (var client = new ClientHost(window, new ClientOptions
            {
                Vsync = options.Vsync,
                DebugLayer = options.Debug,
                GpuValidation = options.GpuValidation,
                MaxFrames = options.Frames,
                ResizeCycle = options.ResizeCycle,
                ScreenshotPath = options.Shot,
                ScreenshotDirectory = options.Screenshots,
                BenchSeconds = options.Bench,
                TicksPerFrame = options.TicksPerFrame,
                FrameSeconds = options.FrameSeconds,
                MaxFps = options.MaxFps,
                Bindings = bindings,
                HeldActions = options.Hold,
                Profile = options.Profile,
                ProfileEvents = options.ProfileEvents,
            }, arena, simulation))
            {
                profiler = client.Profiler;
                IDisposable? agent = null;
                if (options.Agent)
                {
                    StartAgent(client, simulation, options.AgentPort, ref agent);
                    if (agent is null) throw new InvalidOperationException("--agent: сборка без агента (-p:WithoutAgent=true)");
                }
                try
                {
                    report = client.Run();
                }
                finally
                {
                    agent?.Dispose();
                }
            }

            Replay? recorded = null;
            if (options.Record is not null)
            {
                recorded = recorder!.ToReplay();
                recorded.Save(options.Record);
                if (!options.Json) Log.Info("replay", $"записан {Path.GetFullPath(options.Record)}: {recorded.Ticks} тиков, хешей {recorded.Hashes.Count}");
            }

            // Профайлер (--profile, точка Па): отчёт — в артефакты прогона.
            string? profileFile = options.Profile
                ? profiler.Write(ProfilePath(), new System.Text.Json.Nodes.JsonObject
                {
                    ["process"] = "app",
                    ["args"] = new System.Text.Json.Nodes.JsonArray([.. args.Select(a => (System.Text.Json.Nodes.JsonNode?)a)]),
                    ["finished"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                    ["runtime"] = Environment.Version.ToString(),
                    ["adapter"] = report.Adapter,
                    ["exitReason"] = report.ExitReason,
                })
                : null;
            if (profileFile is not null && !options.Json) Log.Info("profile", $"профайлер: {profileFile}");

            // Корень игрока последнего кадра — для сверки с Headless на том же числе тиков (verify.ps1, app-smoke).
            // Показанный кадром игрок (интерполированный) и камера — что видел пользователь.
            var player = arena.LastPlayer;
            var shown = arena.View.Player;
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true, report,
                    player = player is { } t ? new { x = t.Position.X, z = t.Position.Z, facing = t.Facing } : null,
                    shown = shown is { } v ? new { x = v.Root.X, z = v.Root.Z, facing = v.Facing, height = v.Height } : null,
                    camera = new
                    {
                        follow = arena.Camera.Follow, yaw = arena.Camera.Yaw, pitch = arena.Camera.Pitch,
                        distance = arena.Camera.Distance, target = new[] { arena.Camera.Target.X, arena.Camera.Target.Z },
                    },
                    input = bindings.Keys,
                    // Арена последнего тика (этап 4а): заданное число врагов, бойцов в мире и отданных скиннингу в кадре.
                    arena = new
                    {
                        enemies = simulation.World.Resource<ArenaState>().Enemies,
                        fighters = arena.View.Fighters.Count,
                        skinned = arena.SkinnedFighters,
                    },
                    hud = new { shown = arena.ShowsHud, help = arena.Hud.ShowHelp, quads = arena.HudQuads, lines = arena.HelpLines },
                    // Капсулы тел (5в): показаны ли поверх персонажей (F7, --overlay capsules), сколько нарисовано в последнем
                    // кадре и сколько капсул у бойцов кадра.
                    bodies = new { shown = arena.ShowBodies, drawn = arena.DrawnCapsules, capsules = arena.View.BodyCount },
                    recorded = recorded is null ? null : new
                    {
                        file = Path.GetFullPath(options.Record!), ticks = recorded.Ticks, hashes = recorded.Hashes.Count,
                        runs = recorded.Runs.Count, commands = recorded.CommandCount,
                        stateHash = recorded.Hashes.Count > 0 ? string.Join(' ', recorded.Hashes[^1].Parts.Select(p => p.ToString("X16"))) : null,
                    },
                    // Профайлер (--profile, точка Па): файл отчёта и главные зоны кадра; без ключа — null.
                    profile = profileFile is null ? null : ProfileJson(profiler, profileFile),
                    // Воспроизведение (--replay): совпали ли хеши App с записанными — тот же реплей, что у Headless.
                    replay = playback is null ? null : new
                    {
                        file = Path.GetFullPath(options.Replay!), ticks = playback.Length, match = playback.Divergence is null,
                        hashesChecked = playback.HashesChecked, lastMatch = playback.LastMatch,
                        divergence = playback.Divergence?.ToString(), warnings = playback.Warnings,
                    },
                }, JsonOptions));
            else
            {
                Log.Info("sword", $"выход: {report.ExitReason}; кадров {report.Frames}, изменений размера {report.Resizes}, " +
                    $"кадр в среднем {report.AverageFrameMs:0.###} мс, худший {report.MaxFrameMs:0.###} мс" +
                    (report.Bench is { } b ? $"; бенч: {b}" : "") + (report.Machine is { } m ? $"; {m.Text}" : ""));
                if (playback is not null)
                    Log.Info("replay", playback.Divergence is { } d ? d.ToString()
                        : $"реплей: сверено хешей {playback.HashesChecked}, последний — после {playback.LastMatch?.ToString() ?? "—"} тиков");
            }
            profiler.SetEvents(false);   // диагностический режим (--profile runtime): слушатель событий среды закрыт после итога
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("sword", ex.ToString());
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            return 1;
        }
    }

    // Кириллица в сообщениях — как есть, без \uXXXX, перечисления — словами: итог читают и люди.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a}: нет значения");
            switch (a)
            {
                case "--windowed":
                {
                    var parts = Next().Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out o.Width)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out o.Height))
                        throw new ArgumentException($"{a}: ожидается размер вида 1600x900");
                    o.Fullscreen = false;
                    break;
                }
                case "--fullscreen": o.Fullscreen = true; break;
                case "--borderless": o.Borderless = true; break;
                case "--vsync": o.Vsync = true; break;
                case "--debug": o.Debug = true; break;
                case "--gpu-validation": o.GpuValidation = true; break;
                case "--frames":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out o.Frames))
                        throw new ArgumentException($"{a}: ожидается число кадров");
                    break;
                case "--resize-cycle": o.ResizeCycle = true; break;
                case "--no-activate": o.Activate = false; break;
                case "--json": o.Json = true; break;
                case "--shot": o.Shot = Next(); break;
                case "--screenshots": o.Screenshots = Next(); break;
                case "--no-hud": o.NoHud = true; break;
                case "--overlay": o.Overlays.Add(Next()); break;
                case "--agent": o.Agent = true; break;
                case "--profile":
                    o.Profile = true;
                    if (i + 1 < args.Length && args[i + 1] == "runtime")
                    {
                        o.ProfileEvents = true;
                        i++;
                    }
                    break;
                case "--agent-port":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out o.AgentPort) || o.AgentPort is < 1 or > 65535)
                        throw new ArgumentException($"{a}: ожидается порт 1…65535");
                    o.Agent = true;
                    break;
                case "--bench":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out o.Bench) || !(o.Bench > 4 && o.Bench <= 3600))
                        throw new ArgumentException($"{a}: ожидается число секунд игрового времени больше 4 (прогрев) и не больше 3600");
                    break;
                case "--move":
                {
                    var m = Pair(a, Next());
                    o.Command = (o.Command ?? default) with { Move = m };
                    break;
                }
                case "--walk": o.Command = (o.Command ?? default) with { Walk = true }; break;
                case "--gaze":
                {
                    var g = Pair(a, Next());
                    o.Command = (o.Command ?? default) with { HasGaze = true, Gaze = new System.Numerics.Vector3(g.X, 0, g.Y) };
                    break;
                }
                case "--ticks-per-frame":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out o.TicksPerFrame))
                        throw new ArgumentException($"{a}: ожидается число тиков");
                    break;
                case "--hold": o.Hold.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--max-fps":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out o.MaxFps) || !(o.MaxFps >= 1 && o.MaxFps <= 10000))
                        throw new ArgumentException($"{a}: ожидается число кадров в секунду 1…10000");
                    break;
                case "--fps":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) || !(fps >= 1 && fps <= 10000))
                        throw new ArgumentException($"{a}: ожидается число кадров в секунду 1…10000");
                    o.FrameSeconds = 1 / fps;
                    break;
                case "--set": o.Sets.Add(Next()); break;
                case "--units":
                    o.Sets.Add(int.TryParse(Next(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int units)
                        ? $"arena.enemies={Math.Clamp(units, 0, ArenaState.MaxEnemies)}" : throw new ArgumentException($"{a}: ожидается целое число"));
                    break;
                case "--record": o.Record = Next(); break;
                case "--replay": o.Replay = Next(); break;
                case "--hash-every":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out o.HashEvery) || o.HashEvery < 1)
                        throw new ArgumentException($"{a}: ожидается целое число тиков ≥ 1");
                    break;
                case "--camera":
                {
                    var parts = Next().Split(',');
                    var v = new float[5];
                    if (parts.Length is not (3 or 5)
                        || parts.Where((t, k) => !float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || !float.IsFinite(v[k])).Any())
                        throw new ArgumentException($"{a}: ожидается «поворот,наклон,дистанция[,x,z]», например 0.6,0.95,24");
                    o.Camera = new CameraPose(v[0], v[1], v[2], v[3], v[4]);
                    break;
                }
                default: throw new ArgumentException($"неизвестный ключ {a}");
            }
        }
        if (o.TicksPerFrame > 0 && o.FrameSeconds > 0) throw new ArgumentException("--ticks-per-frame и --fps вместе не задаются");
        if (o.Bench > 0 && o.Frames > 0) throw new ArgumentException("--bench и --frames вместе не задаются: бенч кончается сам");
        if (o.Bench > 0 && o.MaxFps > 0) throw new ArgumentException("--bench и --max-fps вместе не задаются: ограничитель исказил бы замер");
        if (o.Bench > 0 && o.Agent) throw new ArgumentException("--bench и --agent вместе не задаются: пауза и снимки агента исказили бы замер");
        if (o.Replay is not null && (o.Command is not null || o.Hold.Count > 0))
            throw new ArgumentException("--replay: команды игрока — из реплея; --move, --walk, --gaze, --hold нельзя");
        return o;
    }

    /// <summary>Корень репозитория — папка с SWORD.slnx выше exe; null — exe вне репозитория.</summary>
    internal static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// Отчёт профайлера прогона (<c>--profile</c>): <c>artifacts/profile/&lt;дата-время&gt;-app-&lt;pid&gt;/profile.json</c> в корне
    /// репозитория, вне репозитория — рядом с exe.
    /// </summary>
    private static string ProfilePath() =>
        Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "artifacts", "profile",
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-app-{Environment.ProcessId}", "profile.json");

    /// <summary>Секция <c>profile</c> итога <c>--json</c>: файл отчёта и коротко — главные зоны кадра.</summary>
    private static System.Text.Json.Nodes.JsonObject ProfileJson(CpuProfiler profiler, string file)
    {
        var json = profiler.Summary(12);
        json.Insert(0, "file", file);
        return json;
    }

    /// <summary>Отпечаток пака (реплей и квитанции агента называют его).</summary>
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

    private static System.Numerics.Vector2 Pair(string key, string text)
    {
        var p = text.Split(',');
        if (p.Length != 2
            || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            || !float.IsFinite(x) || !float.IsFinite(z))
            throw new ArgumentException($"{key}: ожидается X,Z — два конечных числа");
        return new System.Numerics.Vector2(x, z);
    }
}
