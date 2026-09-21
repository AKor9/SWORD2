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
/// Точка входа игры SWORD: композиция симуляции (<see cref="SwordSimulation"/> — та же, что у Headless) и клиента
/// (<see cref="ArenaClient"/>). Точка 1в: игрок идёт по заданной команде (<c>--move</c>); ввод — точка 1г.
/// </summary>
public static class Program
{
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
        public readonly List<string> Sets = [];
    }

    private const string Usage = """
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
          --shot FILE.png   снять кадр в PNG: последний кадр прогона (--frames), без него — 30-й
          --camera Y,P,D[,X,Z]  ракурс камеры: поворот и наклон (рад), дистанция (м), точка взгляда (м);
                            по умолчанию — исходный SWORD 0.6,0.95,24,0,0
          --move X,Z        команда игрока каждый тик: направление по земле (мировые оси); длина не
                            важна: ход — бегом или шагом, как WASD в SWORD
          --walk            с --move: шагом, а не бегом
          --gaze X,Z        точка взгляда игрока на земле
          --ticks-per-frame N  ровно N тиков на кадр (проверочные прогоны); без ключа — по часам
          --set секция.ключ=значение  конфиг (перекрывает sword.json и переменные SWORD_*)
          --json            итог — одной строкой JSON в stdout (для агента)
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
            var config = ConfigSet.Load(AppContext.BaseDirectory, options.Sets);
            var simulation = SwordSimulation.Create(config);
            var arena = new ArenaClient(options.Camera, options.Command);
            config.Complete();   // композиция закончена (симуляция и клиент): неизвестная секция конфига — ошибка
            using var window = new Win32Window(new WindowOptions(
                Title: "SWORD",
                Width: options.Width,
                Height: options.Height,
                Activate: options.Activate,
                Fullscreen: options.Fullscreen,
                Resizable: !options.Fullscreen && !options.Borderless));

            ClientRunReport report;
            using (var client = new ClientHost(window, new ClientOptions
            {
                Vsync = options.Vsync,
                DebugLayer = options.Debug,
                GpuValidation = options.GpuValidation,
                MaxFrames = options.Frames,
                ResizeCycle = options.ResizeCycle,
                ScreenshotPath = options.Shot,
                TicksPerFrame = options.TicksPerFrame,
            }, arena, simulation))
            {
                report = client.Run();
            }

            // Корень игрока последнего кадра — для сверки с Headless на том же числе тиков (verify.ps1, app-smoke).
            var player = arena.LastPlayer;
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true, report,
                    player = new { x = player.Position.X, z = player.Position.Z, facing = player.Facing },
                }, JsonOptions));
            else
                Log.Info("sword", $"выход: {report.ExitReason}; кадров {report.Frames}, изменений размера {report.Resizes}, " +
                    $"кадр в среднем {report.AverageFrameMs:0.###} мс, худший {report.MaxFrameMs:0.###} мс");
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
                case "--set": o.Sets.Add(Next()); break;
                case "--camera":
                {
                    var parts = Next().Split(',');
                    var v = new float[5];
                    if (parts.Length is not (3 or 5) || parts.Where((t, k) => !float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v[k])).Any())
                        throw new ArgumentException($"{a}: ожидается «поворот,наклон,дистанция[,x,z]», например 0.6,0.95,24");
                    o.Camera = new CameraPose(v[0], v[1], v[2], v[3], v[4]);
                    break;
                }
                default: throw new ArgumentException($"неизвестный ключ {a}");
            }
        }
        return o;
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
