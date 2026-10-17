using System.Globalization;
using System.Text;
using System.Text.Json;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;

namespace Sword.Game.App;

/// <summary>
/// Точка входа игры: композиция симуляции (<see cref="SwordSimulation"/> — та же, что у Headless), клиента (<see cref="ArenaClient"/>)
/// и привязок ввода (секция <c>input</c>). <c>--move</c> заменяет ввод заданной командой, <c>--bench</c> — бенч кадра.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// Панели DevTools (DevToolsMode.cs); в сборке без DevTools вызова нет — список пуст.
    /// </summary>
    static partial void AddDevTools(List<IClientPanel> panels, bool bench);

    /// <summary>
    /// Связать панели DevTools с игрой: группы ручек клиента и клиентских модулей (заводятся в <c>Initialize</c> клиента), сохранение,
    /// край под HUD. В сборке без DevTools вызова нет.
    /// </summary>
    static partial void ConnectDevTools(IReadOnlyList<IClientPanel> panels, ClientHost client, ArenaClient arena, ClientModules modules);

    /// <summary>
    /// Переменная окружения: окно без фокуса по умолчанию (как <c>--no-activate</c>) — окна автоматических прогонов не встают поверх
    /// приложений пользователя. <c>--activate</c> / <c>--no-activate</c> главнее.
    /// </summary>
    internal const string NoActivateVariable = "SWORD_NO_ACTIVATE";

    internal sealed class Options
    {
        public int Width = 1600, Height = 900;
        public bool Fullscreen;
        public bool Borderless;
        public bool Activate = true;
        /// <summary>Откуда <see cref="Activate"/>: <c>default</c>, <c>SWORD_NO_ACTIVATE</c>, <c>--activate</c>, <c>--no-activate</c>.</summary>
        public string ActivateBy = "default";
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
        public bool Profile;
        public bool ProfileTrace;
        public string? Record;
        public string? Replay;
        public int HashEvery = 1;
        public readonly List<string> Hold = [];
        public readonly List<string> Sets = [];
        public readonly List<string> Overlays = [];
        /// <summary>Тестовых точечных источников «факел» у игрока (<c>--test-light [N]</c>): свет без огня, только представление.</summary>
        public int TestLights;
        /// <summary>Ручки клиента с первого кадра (<c>--tune light.ключ=значение</c>): ключ группы «Свет» → значение.</summary>
        public readonly List<(string Group, string Key, double Value)> Tunes = [];
        /// <summary>Окно показано свёрнутым, без фокуса (<c>--minimized</c>): проверка, что прогон идёт и в свёрнутом окне.</summary>
        public bool Minimized;
        /// <summary>Сторож кадров проверочного прогона (<c>--stall</c>): нет кадров столько секунд — выход с ошибкой.</summary>
        public double StallSeconds = 60;
    }

    private const string Usage = """
        Управление (привязки — секция конфига input, например --set input.move_forward=Up):
          WASD — ход относительно камеры, Shift — шагом, курсор на земле — куда смотреть;
          колесо, +/− — зум; Z/C — поворот камеры; средняя кнопка — поворот и наклон мышью;
          F — камера за игроком / свободная (стрелки — панорама); F1 — справка; F2 — профайлер (зоны кадра, проходы GPU;
          включает профайлер); F4 — снимок; V — vsync вкл/выкл; Esc — выход;
          PageUp/PageDown — врагов ±1, R — сброс арены, T — следующее оружие (команды арены — в реплее); F7 — капсулы тел
          и клинки поверх персонажей; J — суставы тел, K — контакты физики (включает трассу физики, пока показаны);
          M — траектории Motion Matching игрока (запрос поиска, найденный кадр, история, стопы на земле);
          ЛКМ — удар, Q — укол, Пробел — рывок; I — инвентарь (1–9 и щелчок — взять меч);
          F3 — ручки «Бой» (урон, острота, враги нападают, игрок неуязвим, рывок) и «Рэгдолл» (трение, выталкивание,
          мягкость суставов, потолки трупа, скорости при смерти, толчок; трупы в кадре); изменение — команда в реплее,
          «сохранить» — секция combat или ragdoll в sword.user.json; «Свет» (ночь, тестовый факел, экспозиция, bloom) —
          ручки рендера, «Огонь» (оверлей, вид поля, скорость сима, затухание, кисть, k) — модуля огня, «Графика» (vsync, SMAA) — показа:
          в реплей не идут и не сохраняются.
        Ключи:
          --windowed WxH    размер окна (по умолчанию 1600x900); окно с рамой, размер меняется мышью
          --borderless      окно без рамы заданного размера по центру (как игровое окно SWORD)
          --fullscreen      окно без рамы на весь монитор
          --vsync           вертикальная синхронизация (на ходу — V или вкладка «Графика» панели F3)
          --debug           отладочный слой D3D12
          --gpu-validation  отладочный слой с GPU-валидацией (состояния ресурсов из bindless-кучи; медленно)
          --frames N        выйти после N кадров (проверочный прогон)
          --resize-cycle    менять размер окна по кругу (проверка пересоздания задних буферов)
          --no-activate     не отбирать фокус: окно показано внизу порядка перекрытия (и с --fullscreen, --borderless)
          --activate        окно с фокусом, даже если задана SWORD_NO_ACTIVATE (из двух ключей действует последний)
          --minimized       окно показано свёрнутым, без фокуса: проверка, что прогон (--frames, --bench) идёт и в свёрнутом окне —
                            проверочный прогон рисует и свёрнутым (итог --json — report.minimizedFrames)
          --stall N         сторож проверочного прогона (--frames, --bench): нет кадров N с (по умолчанию 60) — итог-ошибка
                            «нет кадров N с» и выход с кодом 5, а не прогон без конца
          --shot FILE.png   снять кадр в PNG: последний кадр прогона (--frames), без него — 30-й; с --bench — последний
                            кадр замера
          --screenshots DIR куда класть снимки F4 (по умолчанию screenshots в текущем каталоге)
          --no-hud          без HUD (FPS, справка): снимки, сравнимые между прогонами
          --overlay NAME    отладочный оверлей поверх кадра с первого кадра (можно несколько раз): capsules — капсулы тел
                            бойцов (как F7), blades — клинки (отрезок тика, прошлого тика и пройденное за тик), joints —
                            суставы тел (как J), contacts — контакты физики (как K; включает трассу физики), mm —
                            траектории Motion Matching игрока (как M), lights — точечные источники (крест, круги радиуса R
                            и половины затухания), fire — головка горящего оружия игрока по тикам последнего кадра, рамка площадки
                            сима огня, центр шага, точка кисти (модуль Fire; горящее оружие — fire.weapons, по умолчанию факел:
                            --set combat.playerWeapon=torch), fire.field — поле огня (ρ тепловой шкалой на площадке)
          --test-light [N]  тестовые точечные источники у игрока — факел LIVE-2 (цвет 1, 0.038, 0; интенсивность 100;
                            экспонента 400; R 20 м) на высоте 1.4 м, N > 1 — ещё N − 1 по кругу 3 м (замер; сверх 64 —
                            отброшены): свет без огня, симуляция и реплей их не видят; итог --json — report.lights
          --tune G.K=V      клиентская ручка F3 с первого кадра, можно несколько раз: группа light («Свет») — night (0/1) — ночь,
                            testLight (0/1) — тестовый факел, exposure — экспозиция, bloom (0/1), bloomIntensity, bloomThreshold;
                            группа fire («Огонь») — flame, flameLayer, smoke, smokeLayer, light, lightFunction, overlay, field
                            (0/1), smokeDensity, simSpeed, accumulation, brushStrength, brushSize, kMin;
                            только представление (реплей и хеш те же); итог --json — light, fire
          --bench N         бенч кадра: N с игрового времени (первые 4 — прогрев), затем выход с итогом (кадр ср.,
                            p50, p99, макс, CPU); vsync выключен, справка скрыта, F4 не действует
          --camera Y,P,D[,X,Z]  неподвижная камера (проверочные снимки): поворот и наклон (рад), дистанция (м),
                            точка взгляда (м); управление камерой выключено; наклон 0.45…1.45, дистанция 6…110;
                            без ключа — камера SWORD за игроком, 0.6,0.95,24
          --move X,Z        заданная команда игрока каждый тик вместо клавиатуры и мыши: направление по земле
                            (мировые оси); длина не важна — бегом или шагом, как WASD в SWORD
          --walk            заданная команда: шагом (без --move — стоять)
          --gaze X,Z        заданная команда: точка взгляда на земле
          --attack          заданная команда: удар каждый тик (связка без конца — бой без мыши, проверочные снимки)
                            (любой из четырёх ключей заменяет ввод игрока заданной командой; камера управляется)
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
                            Sword.Headless --replay FILE
          --hash-every K    хеш состояния в реплее каждые K тиков (по умолчанию 1)
          --replay FILE     воспроизвести реплей (.swreplay): его конфиг (--set — поверх) и его команды игрока — клавиатура
                            и мышь игроком не управляют, камера — как обычно; хеши сверяются по участникам (итог --json —
                            replay); после конца реплея игрок стоит
          --json            итог — одной строкой JSON в stdout (для скриптов)
          --profile         профайлер CPU с первого кадра: время и выделения фаз кадра, тиков, стадий, систем,
                            инвариантов, проходов графа, зон игры; отчёт — artifacts/profile/<прогон>/profile.json, в --json —
                            секция profile; с --bench — только кадры замера
          --profile-trace   то же, что --profile, и кольцо профайлера в Chrome Trace (trace.json рядом с profile.json:
                            Perfetto, chrome://tracing)
        Переменные:
          SWORD_NO_ACTIVATE=1 (или true)  окно без фокуса по умолчанию, как --no-activate (так запускают агенты: она задана
                            в настройках Claude Code проекта); пусто, 0, false — как без неё; --activate и --no-activate главнее
        """;

    public static int Main(string[] args)
    {
        // Вывод — UTF-8.
        Console.OutputEncoding = Encoding.UTF8;

        Options options;
        try
        {
            options = Parse(args, Environment.GetEnvironmentVariable(NoActivateVariable));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // При --json stdout — только машинный итог; журнал уходит в stderr.
        if (options.Json) Log.SetSinks(new ConsoleLogSink(allToStandardError: true));
        // Почему окно не взяло фокус, когда ключа не было: переменная окружения.
        if (options.ActivateBy == NoActivateVariable)
            Log.Info("sword", $"окно без фокуса: {NoActivateVariable}={Environment.GetEnvironmentVariable(NoActivateVariable)} (фокус — --activate)");

        try
        {
            // Бенч кадра — без инвариантов (их стоимость меряется отдельно); --set перекрывает.
            List<string> sets = options.Bench > 0 ? ["sim.invariants=false", .. options.Sets] : options.Sets;
            // Реплей: только его конфиг (ни файлов, ни переменных) и --set поверх — как у Sword.Headless --replay.
            string content = ContentHash();
            Replay? replay = options.Replay is null ? null : Replay.Load(options.Replay);
            if (replay is not null && replay.Content != "none" && content != "none" && replay.Content != content)
                throw new InvalidDataException($"реплей записан на контенте {replay.Content}, а пак здесь — {content}: хеши сравнивать бессмысленно");
            var config = replay?.CreateConfig(sets) ?? ConfigSet.Load(AppContext.BaseDirectory, sets);
            var simulation = SwordSimulation.Create(config, Sword.Game.Animation.CharacterAssets.LoadFromPak());
            var playback = replay is null ? null : simulation.Play(replay);
            // Живая игра: тяжёлые кодовые инварианты — раз в 4 тика (InvariantCost.Heavy); сверка реплея — каждый тик.
            simulation.Invariants.LivePeriods = replay is null;
            var arena = new ArenaClient(options.Camera, options.Command, hud: !options.NoHud, playerCommands: replay is null,
                showBodies: options.Overlays.Contains(ArenaClient.CapsulesOverlay), showBlades: options.Overlays.Contains(ArenaClient.BladesOverlay),
                showJoints: options.Overlays.Contains(ArenaClient.JointsOverlay), showContacts: options.Overlays.Contains(ArenaClient.ContactsOverlay),
                showMotion: options.Overlays.Contains(ArenaClient.MotionOverlay), showLights: options.Overlays.Contains(ArenaClient.LightsOverlay),
                testLights: options.TestLights);
            // Клиентские модули (огонь, кровь) — до Complete: читают свои секции; выключенные не настраиваются вовсе. Сервисы — от клиента арены.
            var clientModules = ClientModuleList.Compose(config, simulation, arena);
            IReadOnlyList<string> overlays = [.. ArenaClient.KnownOverlays, .. clientModules.Overlays.Select(o => o.Name)];
            if (options.Overlays.FirstOrDefault(o => !overlays.Contains(o)) is { } unknown)
                throw new ArgumentException($"--overlay {unknown}: такого оверлея нет; есть: {string.Join(", ", overlays)}");
            foreach (string name in options.Overlays)
                if (clientModules.Overlay(name) is { } moduleOverlay) moduleOverlay.Shown = true;
            // Панели поверх кадра: инвентарь игры (I — открыть, мышь над ним не бьёт) и DevTools (F3 — ручки).
            var panels = new List<IClientPanel> { arena.Inventory };
            AddDevTools(panels, bench: options.Bench > 0);
            var bindings = InputBindings.Resolve([.. arena.Actions, .. ClientHost.Actions, .. panels.SelectMany(p => p.Actions)], config);
            arena.UseBindings(bindings.Keys);
            if (options.Bench > 0) arena.Hud.ShowHelp = false;   // в бенче справки нет
            config.Complete();   // композиция закончена (симуляция, клиент, ввод): неизвестная секция конфига — ошибка
            // Реплей — с тика 0, по ключу --record.
            var recorder = options.Record is not null ? simulation.Record(ReplayInfo.Current(content), options.HashEvery) : null;
            using var window = new Win32Window(new WindowOptions(
                Title: "SWORD",
                Width: options.Width,
                Height: options.Height,
                Activate: options.Activate && !options.Minimized,
                Fullscreen: options.Fullscreen,
                Resizable: !options.Fullscreen && !options.Borderless,
                Minimized: options.Minimized));

            ClientRunReport report;
            CpuProfiler profiler;
            bool panelsShown;
            // Каталог прогона профайлера — один на прогон: profile.json, trace.json (в конце).
            string profileDir = ProfileDir();
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
                // Проверочный прогон (кадры, бенч) — со сторожем: нет кадров StallSeconds — итог-ошибка и выход (код StalledExit),
                // а не бенч без конца. Свёрнутое окно такой прогон и так рисует (ClientHost).
                StallSeconds = options.Frames > 0 || options.Bench > 0 ? options.StallSeconds : 0,
                Stalled = message => Stalled(message, options.Json),
            }, arena, simulation, clientModules))
            {
                profiler = client.Profiler;
                foreach (var panel in panels) client.AddPanel(panel);
                if (client.Panels is { } layer) layer.Scale = Hud.Scale;   // панели — в масштабе HUD
                ConnectDevTools(panels, client, arena, clientModules);
                // Ручки «Свет» с первого кадра (--tune): те же делегаты, что у панели F3.
                if (options.Tunes.Count > 0) ApplyTunes(arena, clientModules, options.Tunes);
                report = client.Run();
                panelsShown = client.Panels?.Shown ?? false;
            }

            Replay? recorded = null;
            if (options.Record is not null)
            {
                recorded = recorder!.ToReplay();
                recorded.Save(options.Record);
                if (!options.Json) Log.Info("replay", $"записан {Path.GetFullPath(options.Record)}: {recorded.Ticks} тиков, хешей {recorded.Hashes.Count}");
            }

            // Профайлер (--profile): отчёт — в артефакты прогона.
            string? profileFile = options.Profile
                ? profiler.Write(Path.Combine(profileDir, "profile.json"), new System.Text.Json.Nodes.JsonObject
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
            // Кольцо в Chrome Trace (--profile-trace).
            string? traceFile = options.ProfileTrace
                ? profiler.WriteTrace(Path.Combine(profileDir, "trace.json"), new Dictionary<string, string> { ["process"] = "app" })
                : null;
            if (traceFile is not null && !options.Json) Log.Info("profile", $"трасса (Perfetto, chrome://tracing): {traceFile}");

            // Корень игрока последнего кадра — для сверки с Headless на том же числе тиков (verify.ps1, app-smoke).
            // Показанный кадром игрок (интерполированный) и камера — что видел пользователь.
            var player = arena.LastPlayer;
            var shown = arena.View.Player;
            if (options.Json)
            {
                var json = JsonSerializer.SerializeToNode(new
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
                    // Окно: взяло ли фокус при показе и почему (--activate, --no-activate, SWORD_NO_ACTIVATE, default).
                    window = new { activate = options.Activate, activateBy = options.ActivateBy },
                    // Арена последнего тика: заданное число врагов, бойцов в мире и отданных скиннингу в кадре.
                    arena = new
                    {
                        enemies = simulation.World.Resource<ArenaState>().Enemies,
                        fighters = arena.View.Fighters.Count,
                        skinned = arena.SkinnedFighters,
                    },
                    hud = new { shown = arena.ShowsHud, help = arena.Hud.ShowHelp, profiler = arena.Hud.ShowProfiler, quads = arena.HudQuads, lines = arena.HelpLines },
                    // Бой в HUD последнего кадра: здоровье и смерть игрока, враги, убито, попаданий, последний удар, меч.
                    combat = arena.Combat,
                    // Панели поверх кадра: видна ли хоть одна в последнем кадре (F3 — ручки, I — инвентарь), открыт ли инвентарь, вкладки
                    // ручек игры.
                    panels = new { shown = panelsShown, inventory = arena.Inventory.Open, tuning = arena.TuningGroups.Select(g => g.Title).ToArray() },
                    // Капсулы тел: показаны ли поверх персонажей (F7, --overlay capsules), сколько нарисовано в последнем
                    // кадре и сколько капсул у бойцов кадра.
                    bodies = new { shown = arena.ShowBodies, drawn = arena.DrawnCapsules, capsules = arena.View.BodyCount },
                    // Оружие: мечей в руках бойцов кадра и отданных рендеру, клинков оверлея (F7, --overlay blades) — показаны ли и
                    // сколько нарисовано.
                    weapons = new { armed = arena.View.WeaponCount, drawn = arena.DrawnWeapons, player = arena.PlayerWeapon },
                    blades = new { shown = arena.ShowBodies || arena.ShowBlades, drawn = arena.DrawnBlades },
                    // Трупы кадра (по телам), суставы (J, --overlay joints) и контакты физики (K, --overlay contacts; трасса физики).
                    corpses = arena.View.CorpseCount,
                    joints = new { shown = arena.ShowJoints, drawn = arena.DrawnJoints },
                    contacts = new { shown = arena.ShowContacts, drawn = arena.DrawnContacts, trace = arena.View.TraceOn },
                    // Траектории Motion Matching игрока (M, --overlay mm): показаны ли, игрок на MM, примитивов нарисовано, стоп на земле.
                    motion = new { shown = arena.ShowMotion, available = arena.Motion.Available, drawn = arena.DrawnMotion, points = arena.Motion.Points, feetOnGround = arena.Motion.FeetOnGround },
                    // Точечный свет арены: тестовых источников (--test-light N), оверлей (--overlay lights) — показан ли, источников
                    // нарисовано; сами источники кадра — report.lights.
                    lightsOverlay = new { testLights = arena.TestLights, shown = arena.ShowLights, drawn = arena.DrawnLights },
                    // Ручки «Свет» (F3, --tune light.*) в конце прогона: ночь, тестовый факел, экспозиция, bloom — ключ → число (флаг — 1/0).
                    light = arena.Light?.Values(),
                    recorded = recorded is null ? null : new
                    {
                        file = Path.GetFullPath(options.Record!), ticks = recorded.Ticks, hashes = recorded.Hashes.Count,
                        runs = recorded.Runs.Count, commands = recorded.CommandCount,
                        stateHash = recorded.Hashes.Count > 0 ? string.Join(' ', recorded.Hashes[^1].Parts.Select(p => p.ToString("X16"))) : null,
                    },
                    // Профайлер (--profile): файл отчёта и главные зоны кадра; без ключа — null.
                    profile = profileFile is null ? null : ProfileJson(profiler, profileFile, traceFile),
                    // Профайлер кадра и без ключа: включён ли (ключом, F2), кадров в итогах.
                    profiler = new { enabled = profiler.Enabled, frames = profiler.Count(ProfilePeriod.Frame).Periods },
                    // Воспроизведение (--replay): совпали ли хеши App с записанными — тот же реплей, что у Headless.
                    replay = playback is null ? null : new
                    {
                        file = Path.GetFullPath(options.Replay!), ticks = playback.Length, match = playback.Divergence is null,
                        hashesChecked = playback.HashesChecked, lastMatch = playback.LastMatch,
                        divergence = playback.Divergence?.ToString(), warnings = playback.Warnings,
                    },
                }, JsonOptions)!.AsObject();
                // Разделы включённых клиентских модулей — под их Id (fire: огни, головки, оружие); сами модули — report.modules.
                foreach (var (id, value) in clientModules.Sections()) json[id] = JsonSerializer.SerializeToNode(value, JsonOptions);
                Console.WriteLine(json.ToJsonString(JsonOptions));
            }
            else
            {
                Log.Info("sword", $"выход: {report.ExitReason}; кадров {report.Frames}, изменений размера {report.Resizes}, " +
                    $"кадр в среднем {report.AverageFrameMs:0.###} мс, худший {report.MaxFrameMs:0.###} мс" +
                    (report.Bench is { } b ? $"; бенч: {b}" : ""));
                if (playback is not null)
                    Log.Info("replay", playback.Divergence is { } d ? d.ToString()
                        : $"реплей: сверено хешей {playback.HashesChecked}, последний — после {playback.LastMatch?.ToString() ?? "—"} тиков");
            }
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

    /// <summary>
    /// <c>--tune группа.ключ=значение</c>: клиентские ручки F3 (ручки симуляции — <c>--set</c>, они идут в конфиг и реплей). Значение —
    /// число или true/false; группа и ключ проверяются, когда группы собраны (<see cref="ApplyTunes"/>).
    /// </summary>
    internal static (string Group, string Key, double Value) ParseTune(string option, string text)
    {
        int eq = text.IndexOf('=');
        int dot = text.IndexOf('.');
        if (dot <= 0 || eq <= dot + 1)
            throw new ArgumentException($"{option} {text}: ожидается группа.ключ=значение ({LightTuning.Id}.night=1)");
        string group = text[..dot].Trim(), key = text[(dot + 1)..eq].Trim(), value = text[(eq + 1)..].Trim();
        double number = value.ToLowerInvariant() switch
        {
            "true" => 1,
            "false" => 0,
            _ => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v
                : throw new ArgumentException($"{option} {text}: значение — число или true/false"),
        };
        return (group, key, number);
    }

    /// <summary>Значения <c>--tune</c> — в группы клиента (числа прижаты к пределам ручки), итог — в журнал; нет группы или ключа — ошибка.</summary>
    private static void ApplyTunes(ArenaClient arena, ClientModules modules, IReadOnlyList<(string Group, string Key, double Value)> tunes)
    {
        var groups = new List<TuningGroup>();
        if (arena.Light is { } light) groups.Add(light);
        groups.AddRange(modules.TuningGroups);
        foreach (var byGroup in tunes.GroupBy(t => t.Group, StringComparer.OrdinalIgnoreCase))
        {
            var group = groups.FirstOrDefault(g => string.Equals(g.Id, byGroup.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"--tune {byGroup.Key}.…: группы «{byGroup.Key}» нет — есть {string.Join(", ", groups.Select(g => g.Id))} " +
                    "(ручки симуляции — --set секция.ключ=значение)");
            var values = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (_, key, value) in byGroup)
            {
                string known = group.Items.Select(i => i switch { TuningFloat f => f.Key, TuningBool b => b.Key, _ => null })
                    .FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"--tune {group.Id}.{key}: ключа «{key}» нет; есть: " + string.Join(", ",
                        group.Items.Select(i => i switch { TuningFloat f => f.Key, TuningBool b => b.Key, _ => null }).OfType<string>()));
                values[known] = value;
            }
            group.Apply(values);
            Log.Info("tuning", $"{group.Id}: {group.Describe()} (--tune)");
        }
    }

    /// <summary>Код выхода прогона, снятого сторожем кадров: кадров нет дольше <c>--stall</c> секунд.</summary>
    internal const int StalledExit = 5;

    /// <summary>
    /// Сторож кадров сработал (поток таймера): итог-ошибка и выход с кодом <see cref="StalledExit"/> — главный поток может стоять в
    /// ожидании GPU, ждать его нельзя.
    /// </summary>
    private static void Stalled(string message, bool json)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message, stalled = true }, JsonOptions));
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(StalledExit);
    }

    // Кириллица в сообщениях — как есть, без \uXXXX, перечисления — словами: итог читают и люди.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Опции из ключей командной строки и <paramref name="noActivate"/> (null — переменной нет): ключи фокуса главнее переменной.
    /// </summary>
    internal static Options Parse(string[] args, string? noActivate = null)
    {
        var o = new Options();
        if (NoActivateFlag(noActivate))
        {
            o.Activate = false;
            o.ActivateBy = NoActivateVariable;
        }
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
                case "--no-activate": (o.Activate, o.ActivateBy) = (false, a); break;
                case "--activate": (o.Activate, o.ActivateBy) = (true, a); break;
                case "--minimized": o.Minimized = true; break;
                case "--stall":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out o.StallSeconds)
                        || !(o.StallSeconds >= 1 && o.StallSeconds <= 3600))
                        throw new ArgumentException($"{a}: ожидается число секунд 1…3600");
                    break;
                case "--json": o.Json = true; break;
                case "--shot": o.Shot = Next(); break;
                case "--screenshots": o.Screenshots = Next(); break;
                case "--no-hud": o.NoHud = true; break;
                case "--overlay": o.Overlays.Add(Next()); break;
                case "--test-light":
                    o.TestLights = 1;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lights))
                    {
                        if (lights < 1 || lights > PointLights.MaxQueued)
                            throw new ArgumentException($"{a}: ожидается число источников 1…{PointLights.MaxQueued}");
                        o.TestLights = lights;
                        i++;
                    }
                    break;
                case "--tune": o.Tunes.Add(ParseTune(a, Next())); break;
                case "--profile": o.Profile = true; break;
                case "--profile-trace":
                    o.Profile = true;
                    o.ProfileTrace = true;
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
                case "--attack": o.Command = (o.Command ?? default) with { Attack = true }; break;
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
        if (o.Replay is not null && (o.Command is not null || o.Hold.Count > 0))
            throw new ArgumentException("--replay: команды игрока — из реплея; --move, --walk, --gaze, --attack, --hold нельзя");
        return o;
    }

    /// <summary>
    /// Значение <see cref="NoActivateVariable"/>: <c>1</c>, <c>true</c> — без фокуса; нет, пусто, <c>0</c>, <c>false</c> — как без
    /// переменной. Иное — ошибка запуска: опечатка не должна молча вернуть окну фокус.
    /// </summary>
    internal static bool NoActivateFlag(string? value)
    {
        string v = value?.Trim() ?? "";
        if (v is "" or "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (v is "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        throw new ArgumentException($"{NoActivateVariable}={value}: ожидается 1 или true (окно без фокуса), 0, false или пусто");
    }

    /// <summary>Корень репозитория — папка с SWORD.slnx выше exe; null — exe вне репозитория.</summary>
    internal static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// Каталог прогона профайлера: <c>artifacts/profile/&lt;дата-время&gt;-app-&lt;pid&gt;</c> в корне репозитория, вне его — рядом с exe.
    /// Создаётся при первой записи.
    /// </summary>
    private static string ProfileDir() =>
        Path.Combine(RepoRoot() ?? AppContext.BaseDirectory, "artifacts", "profile",
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-app-{Environment.ProcessId}");

    /// <summary>Секция <c>profile</c> итога <c>--json</c>: файл отчёта, трасса и коротко — главные зоны кадра.</summary>
    private static System.Text.Json.Nodes.JsonObject ProfileJson(CpuProfiler profiler, string file, string? trace)
    {
        var json = profiler.Summary(12);
        json.Insert(0, "file", file);
        if (trace is not null) json.Insert(1, "trace", trace);
        return json;
    }

    /// <summary>Отпечаток пака (реплей называет его).</summary>
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
