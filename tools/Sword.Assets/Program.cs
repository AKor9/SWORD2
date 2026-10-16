using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Sword.Bake;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Ui;

namespace Sword.Assets;

/// <summary>
/// sword-assets — менеджер игровых ресурсов: что из <c>Content/</c> идёт в пак, предпросмотр, сборка пака, окно пекарни.
///   pack [--out ФАЙЛ]            собрать пак из включённых файлов Content/ (по умолчанию Content.swpak в корне) и выйти
///   list [--excluded]            файлы Content/ с пометкой «в пак» (или только выключенные) и выйти
///   check [--out ФАЙЛ]           собран ли пак из текущего Content/ и pak.json; код 2 — нет (расхождения в stdout)
///   [--content КАТАЛОГ] [--bake КАТАЛОГ]   Content/ и Bake/ (по умолчанию — в корне репозитория выше текущего каталога)
/// Без команды — окно:
///   --windowed WxH     клиентская часть окна, физические пиксели (по умолчанию 1600x900)
///   --scale K          множитель масштаба интерфейса поверх масштаба монитора
///   --select ПУТЬ      выбрать файл или каталог Content/ (вкладка «Ресурсы»)
///   --source ФАЙЛ      открыть сырой файл во вкладке «Пекарня»
///   --tab N            вкладка: 0 — ресурсы, 1 — пекарня
///   --preview-mode N   вариант показа предпросмотра (для снимков; 0 — первый)
///   --shot ФАЙЛ        снимок (PNG; .bmp — BMP), когда окно затихло, и выход
///   --no-activate      окно без фокуса (внизу порядка перекрытия); с --shot — всегда; SWORD_NO_ACTIVATE=1 — так же
///   --activate         окно с фокусом, даже если задана SWORD_NO_ACTIVATE
///   --debug            отладочный слой D3D12 с GPU-валидацией
/// Рисует по событиям: без ввода спит до таймера виджетов или события фоновой работы.
/// </summary>
internal static class Program
{
    private const int ShotFrame = 5, ShotTimeoutFrames = 60 * 120;
    internal const string Title = "SWORD — ресурсы";

    /// <summary>Переменная окружения «окно без фокуса по умолчанию» — та же, что у игры (её задают настройки агентов).</summary>
    internal const string NoActivateVariable = "SWORD_NO_ACTIVATE";

    internal sealed class Options
    {
        public int Width = 1600, Height = 900;
        public float Scale = 1;
        public string? Shot, Select, Source, Content, Bake, Out, Command;
        public int Tab, PreviewMode;
        public bool Debug, ExcludedOnly;
        public bool Activate = true;

        public bool Unattended => Shot is not null;

        public static Options Parse(string[] args, string? noActivate = null)
        {
            var o = new Options();
            if (noActivate is { Length: > 0 } v && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase)) o.Activate = false;
            int start = 0;
            if (args.Length > 0 && args[0] is "pack" or "list" or "check")
            {
                o.Command = args[0];
                start = 1;
            }
            for (int i = start; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a}: нет значения");
                switch (a)
                {
                    case "--windowed":
                        var parts = Next().Split('x');
                        if (parts.Length != 2) throw new ArgumentException($"{a}: ожидается WxH");
                        o.Width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        o.Height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    case "--scale": o.Scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--shot": o.Shot = Next(); break;
                    case "--select": o.Select = Next(); break;
                    case "--source": o.Source = Next(); break;
                    case "--content": o.Content = Next(); break;
                    case "--bake": o.Bake = Next(); break;
                    case "--out": o.Out = Next(); break;
                    case "--excluded": o.ExcludedOnly = true; break;
                    case "--tab": o.Tab = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--preview-mode": o.PreviewMode = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--debug": o.Debug = true; break;
                    case "--no-activate": o.Activate = false; break;
                    case "--activate": o.Activate = true; break;
                    default: throw new ArgumentException($"неизвестный ключ {a}");
                }
            }
            if (o.Unattended) o.Activate = false;
            return o;
        }

        /// <summary>Пекарня и пак: из ключей или по корню репозитория выше текущего каталога.</summary>
        public (ContentPack Pack, Baker Baker) Open()
        {
            if (Content is null && Bake is null)
            {
                var baker = Baker.OfRepository(Environment.CurrentDirectory);
                return (new ContentPack(baker.ContentDir, baker.BakeDir, Path.Combine(Path.GetDirectoryName(baker.BakeDir)!, "Content.swpak")), baker);
            }
            if (Content is null || Bake is null) throw new ArgumentException("--content и --bake задаются вместе");
            var b = new Baker(Bake, Content);
            return (new ContentPack(Content, Bake, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Content))!, "Content.swpak")), b);
        }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options? o = null;
        try
        {
            o = Options.Parse(args, Environment.GetEnvironmentVariable(NoActivateVariable));
            switch (o.Command)
            {
                case "pack": return Pack(o);
                case "list": return List(o);
                case "check": return Check(o);
            }
            Run(o);
            return 0;
        }
        catch (Exception e)
        {
            bool known = e is ArgumentException or InvalidDataException or IOException or JsonException or UnauthorizedAccessException;
            Console.Error.WriteLine(known ? "ошибка: " + e.Message : e.ToString());
            if (o is { Command: null, Unattended: false }) NativeDialogs.Error(Title, known ? e.Message : e.ToString());
            return 1;
        }
    }

    private static int Pack(Options o)
    {
        var (pack, _) = o.Open();
        Console.WriteLine(ContentPack.Describe(pack.Pack(o.Out)));
        return 0;
    }

    private static int Check(Options o)
    {
        var (pack, _) = o.Open();
        string pak = Path.GetFullPath(o.Out ?? pack.PakPath);
        if (!File.Exists(pak))
        {
            Console.WriteLine($"нет пака {pak} — sword-assets pack");
            return 2;
        }
        var problems = pack.Check(pak);
        foreach (var p in problems) Console.WriteLine(p);
        int files = pack.Files.Count(f => pack.IsIncluded(f.Path));
        Console.WriteLine(problems.Count == 0
            ? $"пак {pak} собран из текущего Content/: файлов {files}"
            : $"пак {pak} не совпадает с Content/ и pak.json — sword-assets pack");
        return problems.Count == 0 ? 0 : 2;
    }

    private static int List(Options o)
    {
        var (pack, _) = o.Open();
        foreach (var f in pack.Files)
        {
            bool on = pack.IsIncluded(f.Path);
            if (o.ExcludedOnly && on) continue;
            Console.WriteLine($"{(on ? "+" : "-")} {f.Path} {f.Size}");
        }
        return 0;
    }

    private static void Run(Options o)
    {
        Log.AddSink(new ConsoleLogSink());
        var (pack, baker) = o.Open();
        var fonts = WindowFonts.Load(Console.WriteLine);
        using var window = new Win32Window(new WindowOptions($"{Title} — {pack.ContentDir}", o.Width, o.Height, Activate: o.Activate, Resizable: true))
        {
            MinClientWidth = 800,
            MinClientHeight = 480,
        };
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height, o.Debug,
            Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: o.Debug) { UiFonts = fonts }, contract);
        var app = new AssetsApp(pack, baker, window.Wake, window.Handle);
        if (o.Select is not null) app.Selected = o.Select.Trim('/');
        if (o.Source is not null) app.SetSource(o.Source);
        var screen = new Screen(o, window, renderer, app);
        try
        {
            screen.Run();
        }
        finally
        {
            screen.Dispose();
            renderer.WaitForIdle();
        }
    }

    /// <summary>
    /// Цикл окна: прокачка сообщений, модель (<see cref="AssetsApp.Pump"/>), кадр интерфейса, рендер. Без ввода и событий
    /// фоновой работы спит. Снимок (<c>--shot</c>) — когда модель затихла и кадр после этого нарисован.
    /// </summary>
    private sealed class Screen(Options o, Win32Window window, Renderer renderer, AssetsApp app) : IDisposable
    {
        private readonly UiContext _ui = new(renderer.Ui.Fonts);
        private readonly UiInputSource _input = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private AssetsView? _view;
        private Action<RenderFrame>? _build;

        public void Run()
        {
            _build = Build;
            _input.Input.GetClipboard = window.GetClipboardText;
            _input.Input.SetClipboard = window.SetClipboardText;
            _view = new AssetsView(app, Upload, o.Tab, o.PreviewMode, still: o.Shot is not null);

            bool redraw = true;
            int frame = 0;
            while (true)
            {
                if (!redraw && o.Shot is null)
                    window.WaitForEvents(double.IsInfinity(_ui.WakeIn) ? -1 : (int)Math.Ceiling(_ui.WakeIn * 1000));
                if (!window.PumpMessages()) break;
                if (window.Resized) renderer.Resize(window.Width, window.Height);
                bool appChanged = app.Pump();
                if (window.Minimized)
                {
                    redraw = false;
                    continue;
                }

                DrawUi();
                bool settled = frame >= ShotFrame && app.Idle && !appChanged || frame >= ShotTimeoutFrames;
                string? capture = o.Shot is not null && settled ? o.Shot : null;
                renderer.Render(_build, vsync: true, capture);
                frame++;
                if (capture is not null)
                {
                    var c = renderer.LastCapture;
                    Console.WriteLine($"[assets] снимок {capture} ({c?.Width}×{c?.Height}, кадр {frame}, квадов {renderer.Ui.LastQuads}, вызовов {renderer.Ui.LastBatches})");
                    foreach (var e in app.Log) Console.WriteLine($"[assets] лог {e.Kind}: {e.Text}");
                    break;
                }
                // немедленный режим видит последствия ввода только следующим кадром: после ввода или события — ещё кадр
                redraw = _input.Input.HadEvents || window.Resized || _ui.WakeIn <= 0 || appChanged;
            }
        }

        /// <summary>Картинка предпросмотра — в видеопамять со своими мипами.</summary>
        private PreviewTexture Upload(PreviewImage image)
        {
            var gpu = renderer.CreateImage("Preview." + image.Name, image.Width, image.Height, image.Mips);
            return new PreviewTexture(gpu.Srv, gpu);
        }

        private void DrawUi()
        {
            _input.Fill(window.Input, window.MouseInside, _clock.Elapsed.TotalSeconds);
            _ui.BeginFrame(_input.Input, renderer.Width, renderer.Height, window.DpiScale * o.Scale);
            _view!.Draw(_ui);
            _ui.EndFrame();
            var cursor = UiInputSource.Cursor(_ui.Cursor);
            if (window.Cursor != cursor) window.Cursor = cursor;
        }

        /// <summary>Кадр: фон темы и интерфейс поверх.</summary>
        private void Build(RenderFrame frame)
        {
            uint bg = _ui.Theme.Background;
            frame.Graph.AddPass("assets.clear")
                .Color(frame.Graph.BackBuffer, new Vector4((bg & 0xFF) / 255f, (bg >> 8 & 0xFF) / 255f, (bg >> 16 & 0xFF) / 255f, 1f));
            renderer.Ui.Add(frame, _ui.Output, "assets.ui");
        }

        public void Dispose()
        {
            app.Dispose();
            _view?.Dispose();
        }
    }
}
