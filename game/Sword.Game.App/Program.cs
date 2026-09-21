using System.Globalization;
using System.Text;
using System.Text.Json;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;

namespace Sword.Game.App;

/// <summary>
/// Точка входа игры SWORD. Точка 1а прототипа: окно, устройство D3D12, очистка кадра, закрытие и изменение размера.
/// Композиция симуляции (<c>Sword.Game.Bootstrap</c>) подключится на точке 1в.
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
        public int Frames;
        public bool ResizeCycle;
        public bool Json;
    }

    private const string Usage = """
        Ключи:
          --windowed WxH    размер окна (по умолчанию 1600x900); окно с рамой, размер меняется мышью
          --borderless      окно без рамы заданного размера по центру (как игровое окно SWORD)
          --fullscreen      окно без рамы на весь монитор
          --vsync           вертикальная синхронизация
          --debug           отладочный слой D3D12
          --frames N        выйти после N кадров (проверочный прогон)
          --resize-cycle    менять размер окна по кругу (проверка пересоздания задних буферов)
          --no-activate     не отбирать фокус: окно показано внизу порядка перекрытия
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
                MaxFrames = options.Frames,
                ResizeCycle = options.ResizeCycle,
            }))
            {
                report = client.Run();
            }

            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, report }, JsonOptions));
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

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
                case "--frames":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out o.Frames))
                        throw new ArgumentException($"{a}: ожидается число кадров");
                    break;
                case "--resize-cycle": o.ResizeCycle = true; break;
                case "--no-activate": o.Activate = false; break;
                case "--json": o.Json = true; break;
                default: throw new ArgumentException($"неизвестный ключ {a}");
            }
        }
        return o;
    }
}
