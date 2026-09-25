using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Ui;

namespace Sword.Bake.Studio;

/// <summary>
/// Bake Studio — окно пекарни: ассеты проекта со статусами, исходники и параметры, добавление ассетов, сборка пака
/// в фоне с логом, содержимое и целостность пака, слежение за исходниками. Окно — <see cref="Win32Window"/>, кадр —
/// рендер-граф движка: очистка заднего буфера и UI-проход (<see cref="Renderer.Ui"/>) со шрифтами Studio
/// (<see cref="StudioFonts"/>, от открытого проекта не зависят).
///   --project ПУТЬ     проект (каталог с bake.project.json или внутри него); без ключа — от текущего каталога вверх,
///                      не нашёлся — окно выбора каталога
///   --windowed WxH     клиентская часть окна, физические пиксели (по умолчанию 1600x900)
///   --scale K          множитель масштаба интерфейса поверх масштаба монитора
///   --select ID        выбрать единицу сборки; content:ID, import:ID, processor:ID — ассет контента, импорт, обработчик
///   --tab N            вкладка (0 свойства, 1 исходники и выходы, 2 зависимости, 3 просмотр), 4 — проект и пак
///   --preview-mode N   вариант показа на вкладке «Просмотр» (для снимков; 0 — первый)
///   --build-all        сразу собрать пак
///   --confirm-delete   открыть вопрос об удалении выбранного ассета (для снимков; ничего не удаляет само)
///   --add-asset        открыть окно добавления ассета (для снимков)
///   --mouse X,Y        курсор в точке (пиксели окна) — для снимков подсказок и наведения
///   --shot ФАЙЛ        снимок (PNG; .bmp — BMP), когда статусы проверены и сборка закончена, и выход
///   --gallery КАТАЛОГ  снимки всех видов проекта (NN-вид.png) и витрины виджетов, и выход
///   --widgets          витрина виджетов вместо пекарни
///   --no-activate      окно без фокуса (внизу порядка перекрытия); с --shot и --gallery — всегда; SWORD_NO_ACTIVATE=1 — так же
///   --activate         окно с фокусом, даже если задана SWORD_NO_ACTIVATE
///   --debug            отладочный слой D3D12 с GPU-валидацией
/// Рисует по событиям: без ввода спит до таймера виджетов или события фоновой работы.
/// </summary>
internal static class Program
{
    private const int ShotFrame = 5, ShotTimeoutFrames = 60 * 120;
    internal const string Title = "Bake Studio";

    /// <summary>Переменная окружения «окно без фокуса по умолчанию» — та же, что у игры (её задают настройки агентов).</summary>
    internal const string NoActivateVariable = "SWORD_NO_ACTIVATE";

    internal sealed class Options
    {
        public int Width = 1600, Height = 900;
        public float Scale = 1;
        public string? Shot, Select, Project, Gallery;
        public int Tab, PreviewMode;
        public bool Debug, Widgets, BuildAll, ConfirmDelete, AddAsset;
        public bool Activate = true;
        /// <summary>Курсор для снимка: подсказки и наведение без живой мыши.</summary>
        public Vector2? Mouse;

        /// <summary>Снимок или витрина: окно не берёт фокус, ошибки — в консоль, а не окном сообщения.</summary>
        public bool Unattended => Shot is not null || Gallery is not null;

        public static Options Parse(string[] args, string? noActivate = null)
        {
            var o = new Options();
            if (noActivate is { Length: > 0 } v && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase)) o.Activate = false;
            for (int i = 0; i < args.Length; i++)
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
                    case "--gallery": o.Gallery = Next(); break;
                    case "--select": o.Select = Next(); break;
                    case "--project": o.Project = Next(); break;
                    case "--tab": o.Tab = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--preview-mode": o.PreviewMode = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--build-all": o.BuildAll = true; break;
                    case "--confirm-delete": o.ConfirmDelete = true; break;
                    case "--add-asset": o.AddAsset = true; break;
                    case "--widgets": o.Widgets = true; break;
                    case "--debug": o.Debug = true; break;
                    case "--no-activate": o.Activate = false; break;
                    case "--activate": o.Activate = true; break;
                    case "--mouse":
                        var xy = Next().Split(',');
                        if (xy.Length != 2) throw new ArgumentException($"{a}: ожидается X,Y");
                        o.Mouse = new(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
                        break;
                    default: throw new ArgumentException($"неизвестный ключ {a}");
                }
            }
            if (o.Unattended) o.Activate = false;
            return o;
        }
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options? o = null;
        try
        {
            o = Options.Parse(args, Environment.GetEnvironmentVariable(NoActivateVariable));
            Run(o);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            if (o is not { Unattended: true })
            {
                bool known = e is ArgumentException or InvalidDataException or IOException or JsonException;
                NativeDialogs.Error(Title, known ? e.Message : e.ToString());
            }
            return 1;
        }
    }

    /// <summary>Каталог проекта: из ключа или текущего каталога; не нашёлся — выбор каталога (и создание проекта в нём).</summary>
    private static string? ResolveProject(Options o)
    {
        if (o.Project is not null)
            return BakeProject.FindRoot(o.Project) ?? throw new DirectoryNotFoundException($"нет проекта пекарни ({BakeProject.FileName}) в {Path.GetFullPath(o.Project)} и выше");
        if (BakeProject.FindRoot(Directory.GetCurrentDirectory()) is { } here) return here;
        if (o.Unattended) throw new DirectoryNotFoundException($"нет проекта пекарни ({BakeProject.FileName}) в текущем каталоге и выше — нужен --project");
        return ChooseProject(0);
    }

    /// <summary>Выбрать каталог проекта; в нём нет проекта — предложить создать. Отмена — null.</summary>
    internal static string? ChooseProject(nint owner)
    {
        string? dir = NativeDialogs.PickFolder(owner, "Каталог проекта Bake");
        if (dir is null) return null;
        if (BakeProject.FindRoot(dir) is { } root) return root;
        if (!NativeDialogs.Ask(Title, $"В {dir} нет проекта пекарни ({BakeProject.FileName}).\n\nСоздать новый проект в этом каталоге?", owner)) return null;
        return BakeProject.Create(dir).Root;
    }

    private static void Run(Options o)
    {
        Log.AddSink(new ConsoleLogSink());
        BakeProject? project = null;
        if (!o.Widgets)
        {
            string? root = ResolveProject(o);
            if (root is null) return;
            project = BakeProject.Open(root);
        }
        var fonts = StudioFonts.Load(Console.WriteLine);

        using var window = new Win32Window(new WindowOptions(WindowTitle(project), o.Width, o.Height, Activate: o.Activate, Resizable: true))
        {
            MinClientWidth = 800,
            MinClientHeight = 480,
        };
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, window.Width, window.Height, o.Debug,
            Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: o.Debug) { UiFonts = fonts }, contract);
        var screen = new StudioScreen(o, window, renderer, project);
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

    internal static string WindowTitle(BakeProject? project) =>
        project is null ? $"{Title} — витрина" : $"{Title} — {project.Name} ({project.Root})";

    /// <summary>Что снять: вид (выбор, вкладка, открытые окна) или витрина виджетов — в файл.</summary>
    internal sealed record Shot(string File, string? Select, int Tab, bool ConfirmDelete = false, bool AddAsset = false, bool Widgets = false,
        int PreviewMode = 0);

    /// <summary>
    /// Снимки всех видов проекта для <c>--gallery</c>: узел проекта и пака, ассет контента (свойства и просмотр), единица сборки
    /// на каждой вкладке (единица — <paramref name="select"/> или та, у которой больше всего параметров), импорт, обработчик,
    /// окна добавления и удаления, витрина виджетов. Пустой проект — только то, что в нём есть.
    /// </summary>
    internal static List<Shot> GalleryShots(StudioApp app, string dir, string? select)
    {
        var project = app.Project;
        string unit = select is not null && project.Find(select) is { } chosen ? chosen.Id
            : project.Assets.OrderByDescending(ParamCount).ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Id ?? "";
        string? content = app.Builder.Catalog().Assets.FirstOrDefault()?.Id;
        string? import = project.Imports.Keys.Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        string? processor = project.Processors.Processors.Where(p => p.Id != "file.copy").Select(p => p.Id).FirstOrDefault()
                            ?? project.Processors.Processors.FirstOrDefault()?.Id;
        var shots = new List<(string Name, string? Select, int Tab, bool Delete, bool Add, bool Widgets, int Mode)> { ("project", null, 4, false, false, false, 0) };
        if (content is not null)
        {
            shots.Add(("content", "content:" + content, 0, false, false, false, 0));
            shots.Add(("content-preview", "content:" + content, 3, false, false, false, 0));
        }
        if (unit.Length > 0)
        {
            shots.Add(("unit-params", unit, 0, false, false, false, 0));
            shots.Add(("unit-io", unit, 1, false, false, false, 0));
            shots.Add(("unit-deps", unit, 2, false, false, false, 0));
            shots.Add(("unit-preview", unit, 3, false, false, false, 0));
        }
        foreach (var (name, id, mode) in PreviewShots(project))
            shots.Add((name, id, 3, false, false, false, mode));
        if (import is not null) shots.Add(("import", "import:" + import, 0, false, false, false, 0));
        if (processor is not null) shots.Add(("processor", "processor:" + processor, 0, false, false, false, 0));
        shots.Add(("add-asset", unit.Length > 0 ? unit : null, 0, false, true, false, 0));
        if (unit.Length > 0) shots.Add(("confirm-delete", unit, 0, true, false, false, 0));
        shots.Add(("widgets", null, 0, false, false, true, 0));
        return shots.Select((s, i) => new Shot(Path.Combine(dir, $"{i + 1:00}-{s.Name}.png"), s.Select, s.Tab, s.Delete, s.Add, s.Widgets, s.Mode)).ToList();
    }

    /// <summary>Второй вариант показа, который стоит снять (у последней единицы обработчика): под кожей, набор знаков, кости, клетки.</summary>
    private static readonly Dictionary<string, int> ExtraPreviewMode = new(StringComparer.Ordinal)
    {
        ["anatomy.mesh"] = 2, ["font.vector"] = 1, ["anim.pack"] = 1, ["anim.skeleton"] = 1, ["weapon.static"] = 1,
    };

    /// <summary>
    /// Предпросмотры для <c>--gallery</c>: у каждого обработчика с <see cref="IAssetPreview"/> — первая по ID единица в первом
    /// варианте показа и, если есть второй интересный вариант (<see cref="ExtraPreviewMode"/>), последняя — в нём.
    /// </summary>
    internal static List<(string Name, string Id, int Mode)> PreviewShots(BakeProject project)
    {
        var list = new List<(string, string, int)>();
        foreach (var p in project.Processors.Processors.Where(p => p is IAssetPreview).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var units = project.Assets.Where(a => a.Processor == p.Id).Select(a => a.Id).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (units.Count == 0) continue;
            string name = "preview-" + p.Id.Replace('.', '-');
            list.Add((name, units[0], 0));
            if (ExtraPreviewMode.TryGetValue(p.Id, out int mode)) list.Add(($"{name}-{mode}", units[^1], mode));
        }
        return list;
    }

    private static int ParamCount(AssetManifest a) =>
        a.Params.ValueKind == JsonValueKind.Object ? a.Params.EnumerateObject().Count() : 0;

    /// <summary>
    /// Цикл окна: прокачка сообщений, модель (<see cref="StudioApp.Pump"/>), кадр интерфейса, рендер. Без ввода и событий
    /// фоновой работы спит. Снимки (<c>--shot</c>, <c>--gallery</c>) идут очередью: вид снимка, кадры до затишья, снимок, следующий.
    /// </summary>
    private sealed class StudioScreen(Options o, Win32Window window, Renderer renderer, BakeProject? project) : IDisposable
    {
        private readonly UiContext _ui = new(renderer.Ui.Fonts);
        private readonly UiInputSource _input = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private StudioApp? _app = project is null ? null : new StudioApp(project, window.Wake, window.Handle);
        private StudioView? _view;
        private Gallery? _gallery;
        private readonly Queue<Shot> _shots = new();
        private Shot? _shot;
        private Action<RenderFrame>? _build;

        public void Run()
        {
            _build = Build;
            _input.Input.GetClipboard = window.GetClipboardText;
            _input.Input.SetClipboard = window.SetClipboardText;
            if (o.Gallery is not null)
            {
                Directory.CreateDirectory(o.Gallery);
                foreach (var s in _app is null ? [new Shot(Path.Combine(o.Gallery, "01-widgets.png"), null, 0, Widgets: true)] : GalleryShots(_app, o.Gallery, o.Select))
                    _shots.Enqueue(s);
            }
            else if (o.Shot is not null) _shots.Enqueue(new Shot(o.Shot, o.Select, o.Tab, o.ConfirmDelete, o.AddAsset, o.Widgets || _app is null, o.PreviewMode));
            bool unattended = _shots.Count > 0;
            if (_app is not null && o.BuildAll) _app.BuildAll();
            if (unattended) Next();
            else Show(new Shot("", o.Select, o.Tab, o.ConfirmDelete, o.AddAsset, o.Widgets || _app is null, o.PreviewMode));

            bool redraw = true;
            int frame = 0;
            while (true)
            {
                if (!redraw && !unattended)
                {
                    double wake = Math.Min(_ui.WakeIn, _app?.WakeIn ?? double.PositiveInfinity);
                    window.WaitForEvents(double.IsInfinity(wake) ? -1 : (int)Math.Ceiling(wake * 1000));
                }
                if (!window.PumpMessages()) break;
                if (window.Resized) renderer.Resize(window.Width, window.Height);
                bool appChanged = _app?.Pump() ?? false;

                // открыт другой проект: прежняя модель закрывается (дожидаясь начатой сборки), окно остаётся
                if (_app?.NextProject is { } next)
                {
                    _app.Dispose();
                    _view?.Dispose();
                    _app = new StudioApp(next, window.Wake, window.Handle);
                    _view = new StudioView(_app, Upload, null, 0);
                    window.SetTitle(WindowTitle(next));
                    appChanged = true;
                }
                if (window.Minimized)
                {
                    redraw = false;
                    continue;
                }

                DrawUi();
                // снимок — когда фоновая работа улеглась (статусы проверены, сборка закончена) и кадр после неё нарисован;
                // с --mouse ждём задержку подсказки (0,5 с)
                bool settled = frame >= ShotFrame && (o.Mouse is null || _clock.Elapsed.TotalSeconds > 1.2) && (_app is null || _app.Idle && !appChanged)
                               || frame >= ShotTimeoutFrames;
                string? capture = _shot is not null && settled ? _shot.File : null;
                renderer.Render(_build, vsync: true, capture);
                frame++;
                if (capture is not null)
                {
                    var c = renderer.LastCapture;
                    Console.WriteLine($"[studio] снимок {capture} ({c?.Width}×{c?.Height}, кадр {frame}, квадов {renderer.Ui.LastQuads}, вызовов {renderer.Ui.LastBatches})");
                    if (frame > ShotTimeoutFrames && _app is { Idle: false })
                        Console.WriteLine($"[studio] снимок по таймауту: модель не затихла ({_app.Activity})");
                    if (_shots.Count == 0)
                    {
                        // лог модели — агенту в консоль: что проверилось, собралось, что не так с проектом
                        if (_app is not null)
                            foreach (var e in _app.Log) Console.WriteLine($"[studio] лог {e.Kind} {e.Asset}: {e.Text}");
                        break;
                    }
                    Next();
                    frame = 0;
                    continue;
                }

                // Немедленный режим видит последствия ввода (открытый список, фокус) только следующим кадром:
                // после ввода или события фоновой работы кадр рисуется ещё раз, дальше — сон.
                redraw = _input.Input.HadEvents || window.Resized || _ui.WakeIn <= 0 || appChanged;
            }
        }

        /// <summary>Картинка предпросмотра — в видеопамять (<see cref="Renderer.CreateImage(string, int, int, IReadOnlyList{byte[]}, bool)"/>) со своими мипами.</summary>
        private PreviewTexture Upload(PreviewImage image)
        {
            var gpu = renderer.CreateImage("Preview." + image.Name, image.Width, image.Height, image.Mips);
            return new PreviewTexture(gpu.Srv, gpu);
        }

        /// <summary>Следующий снимок очереди: свой вид с его выбором и вкладкой.</summary>
        private void Next()
        {
            _shot = _shots.Dequeue();
            Show(_shot);
        }

        private void Show(Shot s)
        {
            _view?.Dispose();
            _view = null;
            _gallery = null;
            if (s.Widgets || _app is null) _gallery = new Gallery(showcase: s.File.Length > 0);
            else _view = new StudioView(_app, Upload, s.Select, s.Tab, s.ConfirmDelete, s.AddAsset, s.PreviewMode, still: s.File.Length > 0);
        }

        private void DrawUi()
        {
            _input.Fill(window.Input, window.MouseInside, _clock.Elapsed.TotalSeconds);
            if (o.Mouse is { } mouse)
            {
                _input.Input.Mouse = mouse;
                _input.Input.MouseInside = true;
            }
            _ui.BeginFrame(_input.Input, renderer.Width, renderer.Height, window.DpiScale * o.Scale);
            if (_view is not null) _view.Draw(_ui);
            else _gallery?.Draw(_ui);
            _ui.EndFrame();
            var cursor = UiInputSource.Cursor(_ui.Cursor);
            if (window.Cursor != cursor) window.Cursor = cursor;
        }

        /// <summary>Кадр: фон темы и интерфейс поверх.</summary>
        private void Build(RenderFrame frame)
        {
            uint bg = _ui.Theme.Background;
            frame.Graph.AddPass("studio.clear")
                .Color(frame.Graph.BackBuffer, new Vector4((bg & 0xFF) / 255f, (bg >> 8 & 0xFF) / 255f, (bg >> 16 & 0xFF) / 255f, 1f));
            renderer.Ui.Add(frame, _ui.Output, "studio.ui");
        }

        public void Dispose()
        {
            _app?.Dispose();
            _view?.Dispose();
        }
    }
}
