using System.Runtime.ExceptionServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Render;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client;

/// <summary>
/// Клиентская часть модуля, подключённая к exe статически: регистрирует своё в <see cref="ClientBuilder"/>, только если
/// модуль включён; выключенный или не собранный не даёт ни прохода, ни ресурса, ни ручки. Игра модулей не знает.
/// </summary>
public interface IClientModule
{
    /// <summary>Тот же Id, что у симуляции модуля; у чисто клиентского — свой (объявлен симуляции как клиентский: <c>Simulation.ClientOnlyModules</c>).</summary>
    string Id { get; }

    /// <summary>Регистрации модуля: конфиг, наблюдатель тиков, извлечение кадра, проходы по фазам, GPU-симуляции, ручки, оверлеи, шейдеры.</summary>
    void ConfigureClient(ClientBuilder b);
}

/// <summary>Извлечение кадра модулем — после тиков кадра, с долей тика (<see cref="FrameView.Alpha"/>).</summary>
public delegate void FrameExtract(in FrameView view);

/// <summary>Сервисы клиентской части игры для модулей (простые данные из <c>Game.Contracts</c>) — даёт exe; модули не
/// видят ни мира ECS, ни ядра игры. Нет сервиса — <see cref="Get{T}"/> вернёт null.</summary>
public sealed class ClientServices
{
    private readonly Dictionary<Type, object> _services = [];
    private readonly List<(Type Type, Func<SimulationLink, object> Create)> _unbound = [];

    public ClientServices Add<T>(T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!_services.TryAdd(typeof(T), service) || _unbound.Any(u => u.Type == typeof(T)))
            throw new InvalidOperationException($"сервис {typeof(T).Name} дважды");
        return this;
    }

    /// <summary>Сервис над симуляцией: создаётся при композиции модулей над <see cref="SimulationLink"/>, которую даёт
    /// клиент движка.</summary>
    public ClientServices Add<T>(Func<SimulationLink, T> create) where T : class
    {
        ArgumentNullException.ThrowIfNull(create);
        if (_services.ContainsKey(typeof(T)) || _unbound.Any(u => u.Type == typeof(T)))
            throw new InvalidOperationException($"сервис {typeof(T).Name} дважды");
        _unbound.Add((typeof(T), create));
        return this;
    }

    /// <summary>Создать сервисы над симуляцией (<see cref="Add{T}(Func{SimulationLink, T})"/>) — при композиции, до настройки модулей.</summary>
    internal void Bind(SimulationLink link)
    {
        foreach (var (type, create) in _unbound)
            _services[type] = create(link) ?? throw new InvalidOperationException($"сервис {type.Name}: создан null");
        _unbound.Clear();
    }

    public T? Get<T>() where T : class => _services.TryGetValue(typeof(T), out var s) ? (T)s : null;
}

/// <summary>Оверлей модуля: имя — ключ <c>Sword.exe --overlay</c>; <see cref="Shown"/> — рисовать поверх кадра на экране.</summary>
public sealed class ClientOverlay(string module, string name)
{
    public string Module { get; } = module;
    public string Name { get; } = name;
    public bool Shown { get; set; }
}

/// <summary>Что модуль получает, когда рендер готов: рендер, камеру показанного кадра, владение ресурсами.</summary>
public sealed class ClientModuleContext
{
    private readonly Func<CameraView> _camera;
    private readonly List<IDisposable> _owned;

    internal ClientModuleContext(string module, Renderer renderer, Func<CameraView> camera, List<IDisposable> owned)
    {
        Module = module;
        Renderer = renderer;
        _camera = camera;
        _owned = owned;
    }

    public string Module { get; }

    public Renderer Renderer { get; }

    /// <summary>Камера кадра игры (<see cref="IClientObservable.Camera"/>; проходы модуля идут после проходов игры); игра без неё — нулевая.</summary>
    public CameraView Camera => _camera();

    /// <summary>Освободить вместе с модулем (после ожидания GPU, до рендера).</summary>
    public T Own<T>(T resource) where T : IDisposable
    {
        _owned.Add(resource);
        return resource;
    }
}

/// <summary>Построитель клиентской части модуля; действует только в <see cref="IClientModule.ConfigureClient"/>.</summary>
public sealed class ClientBuilder
{
    private readonly ClientModules.Module _module;
    private readonly ConfigSet _config;
    private readonly ClientServices _services;
    private readonly ClientModules _host;
    internal bool Sealed;

    internal ClientBuilder(ClientModules host, ClientModules.Module module, ConfigSet config, ClientServices services)
    {
        _host = host;
        _module = module;
        _config = config;
        _services = services;
    }

    /// <summary>Id модуля.</summary>
    public string Module => _module.Id;

    /// <summary>Шаг тика симуляции, с (1/120): такт наблюдателя тиков — например, шаг GPU-симуляции 60 Гц по <c>TickId</c>.</summary>
    public float TickDt => _host.TickDt;

    /// <summary>Секция конфига модуля — имя секции его Id (<c>fire.weapons</c>): те же слои и строгость, что у секций симуляции.</summary>
    public T Config<T>() where T : new()
    {
        Open();
        return _config.Section<T>(_module.Id);
    }

    /// <summary>Сервис клиентской части игры (<see cref="ClientServices"/>); нет — null (модуль тогда не делает того, что на нём стоит).</summary>
    public T? Service<T>() where T : class => _services.Get<T>();

    /// <summary>Числа модуля для шейдеров — до компиляции первого шейдера.</summary>
    public ClientBuilder Shaders(Action<ShaderContract> contribute) => Add(_module.Shaders, contribute);

    /// <summary>Ресурсы GPU и контент — один раз, когда рендер готов (после игры).</summary>
    public ClientBuilder Initialize(Action<ClientModuleContext> initialize) => Add(_module.Initializers, initialize);

    /// <summary>Наблюдатель тиков: после каждого сыгранного тика ровно раз, по порядку, с <c>TickId</c> (с 0); мир — после
    /// своего тика, а не кадра. На паузе вызовов нет.</summary>
    public ClientBuilder AfterTick(Action<ulong> observer) => Add(_module.Observers, observer);

    /// <summary>Извлечение кадра: после тиков кадра и извлечения игры, с долей тика.</summary>
    public ClientBuilder Extract(FrameExtract extract) => Add(_module.Extracts, extract);

    /// <summary>Проходы фазы <paramref name="phase"/> после проходов игры; проход чужой фазы — ошибка кадра. Порядок — по
    /// фазам, в фазе — по регистрации.</summary>
    public ClientBuilder Pass(FramePhase phase, Action<RenderFrame> build)
    {
        Open();
        ArgumentNullException.ThrowIfNull(build);
        _module.Passes.Add((phase, build));
        return this;
    }

    /// <summary>Точечные источники кадра — до проходов игры, поэтому их видят константы кадра и освещение. Только источники
    /// и light functions.</summary>
    public ClientBuilder Lights(Action<PointLights> add) => Add(_module.Lights, add);

    /// <summary>GPU-симуляция модуля; вернёт её заявки CPU (добавлять — из наблюдателя тиков, с <c>TickId</c> своего тика).</summary>
    public SimRequests GpuSimulation(IGpuSimulation simulation)
    {
        Open();
        ArgumentNullException.ThrowIfNull(simulation);
        var requests = new SimRequests();
        _module.Simulations.Add((simulation, requests));
        return requests;
    }

    /// <summary>Группа ручек панели F3 (вкладка; ручки клиента — делегаты, в реплей не идут).</summary>
    public ClientBuilder Tuning(TuningGroup group) => Add(_module.Tuning, group);

    /// <summary>Оверлей модуля (имя — латиница; не совпадает с оверлеем игры и другого модуля).</summary>
    public ClientOverlay Overlay(string name)
    {
        Open();
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_'))
            throw new ArgumentException($"оверлей «{name}»: строчная латиница, цифры, «.», «_»", nameof(name));
        var overlay = new ClientOverlay(_module.Id, name);
        _host.AddOverlay(overlay);
        _module.Overlays.Add(overlay);
        return overlay;
    }

    /// <summary>Раздел итога <c>Sword.exe --json</c> под Id модуля (сериализуется в конце прогона, не каждый кадр).</summary>
    public ClientBuilder Report(Func<object?> report)
    {
        Open();
        _module.Report = report ?? throw new ArgumentNullException(nameof(report));
        return this;
    }

    private ClientBuilder Add<T>(List<T> list, T item)
    {
        Open();
        ArgumentNullException.ThrowIfNull(item);
        list.Add(item);
        return this;
    }

    private void Open()
    {
        if (Sealed) throw new InvalidOperationException($"модуль {_module.Id}: регистрации — только в ConfigureClient");
    }
}

/// <summary>Клиентский модуль в итоге прогона: включён ли, его проходы (за прогон), оверлеи, ручки, GPU-симуляции, наблюдатели и тики.</summary>
public sealed record ClientModuleReport(string Id, bool Enabled, IReadOnlyList<string> Passes, IReadOnlyList<string> Overlays,
    IReadOnlyList<string> Tuning, IReadOnlyList<string> GpuSimulations, int Observers, ulong ObservedTicks, int LastFramePasses);

/// <summary>
/// Клиентские модули: композиция (<see cref="Compose"/>) и место в кадре — <see cref="BeginFrame"/> → тики с
/// <see cref="AfterTick"/> → <see cref="Extract"/> → <see cref="AddLights"/> → <see cref="BuildFrame"/>. Без окна и GPU.
/// </summary>
public sealed class ClientModules : IDisposable
{
    internal sealed class Module(string id, IClientModule source)
    {
        public readonly string Id = id;
        public readonly IClientModule Source = source;
        public readonly List<Action<ShaderContract>> Shaders = [];
        public readonly List<Action<ClientModuleContext>> Initializers = [];
        public readonly List<Action<ulong>> Observers = [];
        public readonly List<FrameExtract> Extracts = [];
        public readonly List<Action<PointLights>> Lights = [];
        public readonly List<(FramePhase Phase, Action<RenderFrame> Build)> Passes = [];
        public readonly List<(IGpuSimulation Simulation, SimRequests Requests)> Simulations = [];
        public readonly List<TuningGroup> Tuning = [];
        public readonly List<ClientOverlay> Overlays = [];
        public readonly List<IDisposable> Owned = [];
        public readonly List<string> PassNames = [];
        public Func<object?>? Report;
        public ulong ObservedTicks;
        public int LastFramePasses;
    }

    private readonly List<Module> _active = [];
    private readonly List<string> _inactive = [];
    private readonly List<ClientOverlay> _overlays = [];
    private readonly float _dt;
    private ulong _firstTick;
    private int _ticks;
    private bool _initialized, _disposed;

    private ClientModules(float dt) => _dt = dt;

    /// <summary>Шаг тика симуляции, с (1/120).</summary>
    public float TickDt => _dt;

    /// <summary>Без модулей.</summary>
    public static ClientModules None(float dt = 1f / 120) => new(dt);

    /// <summary>
    /// Композиция (до <see cref="ConfigSet.Complete"/>): модуль включён, когда включена его часть симуляции или он чисто
    /// клиентский и включён; Id, которого симуляция не знает, — ошибка. Включённые настраиваются по порядку.
    /// </summary>
    public static ClientModules Compose(ConfigSet config, Simulation simulation, IEnumerable<IClientModule> modules, ClientServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(simulation);
        services ??= new ClientServices();
        services.Bind(new SimulationLink(simulation));
        var host = new ClientModules(simulation.Dt);
        var active = host._active;
        var inactive = host._inactive;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in modules)
        {
            string id = m.Id;
            if (!IModule.IsValidId(id)) throw new InvalidOperationException($"клиентский модуль {m.GetType().Name}: Id \"{id}\" — нужно {IModule.IdRule}");
            if (!ids.Add(id)) throw new InvalidOperationException($"клиентский модуль {id} дважды");
            bool on = simulation.ActiveModules.Contains(id) || simulation.ClientOnlyModules.Contains(id);
            if (!on && !simulation.InactiveModules.Contains(id) && !simulation.InactiveClientOnlyModules.Contains(id))
                throw new InvalidOperationException($"клиентский модуль {id}: симуляция его не знает — нет ни модуля симуляции {id}, ни клиентского " +
                    $"Id в композиции (модули: {string.Join(", ", simulation.ActiveModules.Concat(simulation.InactiveModules))}; клиентские: " +
                    $"{string.Join(", ", simulation.ClientOnlyModules.Concat(simulation.InactiveClientOnlyModules))})");
            if (!on)
            {
                inactive.Add(id);
                continue;
            }
            var module = new Module(id, m);
            var b = new ClientBuilder(host, module, config, services);
            m.ConfigureClient(b);
            b.Sealed = true;
            active.Add(module);
        }
        if (active.Count + inactive.Count > 0)
            Log.Info("client", $"клиентские модули: {(active.Count > 0 ? string.Join(", ", active.Select(a => a.Id)) : "—")}" +
                (inactive.Count > 0 ? $"; выключены: {string.Join(", ", inactive)}" : ""));
        return host;
    }

    internal void AddOverlay(ClientOverlay overlay)
    {
        if (_overlays.Any(o => o.Name == overlay.Name))
            throw new InvalidOperationException($"оверлей {overlay.Name} дважды (модуль {overlay.Module})");
        _overlays.Add(overlay);
    }

    /// <summary>Id включённых модулей по порядку композиции.</summary>
    public IReadOnlyList<string> Active => [.. _active.Select(m => m.Id)];

    /// <summary>Id выключенных (собранных, но не настроенных).</summary>
    public IReadOnlyList<string> Inactive => _inactive;

    /// <summary>Оверлеи включённых модулей.</summary>
    public IReadOnlyList<ClientOverlay> Overlays => _overlays;

    /// <summary>Оверлей по имени; нет — null.</summary>
    public ClientOverlay? Overlay(string name) => _overlays.FirstOrDefault(o => o.Name == name);

    /// <summary>Группы ручек включённых модулей (вкладки панели F3).</summary>
    public IReadOnlyList<TuningGroup> TuningGroups => [.. _active.SelectMany(m => m.Tuning)];

    /// <summary>Числа модулей для шейдеров.</summary>
    public void ContributeShaders(ShaderContract contract)
    {
        foreach (var m in _active)
            foreach (var c in m.Shaders) c(contract);
    }

    /// <summary>Рендер готов (после игры): инициализация модулей, затем их GPU-симуляции.</summary>
    public void Initialize(Renderer renderer, Func<CameraView> camera)
    {
        if (_initialized) throw new InvalidOperationException("клиентские модули уже инициализированы");
        _initialized = true;
        foreach (var m in _active)
        {
            var context = new ClientModuleContext(m.Id, renderer, camera, m.Owned);
            foreach (var init in m.Initializers) init(context);
            foreach (var (sim, _) in m.Simulations) sim.Create(new GpuSimContext(m.Id, sim, renderer, m.Owned));
        }
    }

    /// <summary>Начало кадра до тиков; <paramref name="nextTick"/> — <c>TickId</c> первого тика кадра.</summary>
    public void BeginFrame(ulong nextTick)
    {
        _firstTick = nextTick;
        _ticks = 0;
        foreach (var m in _active)
            foreach (var (_, requests) in m.Simulations) requests.Clear();
    }

    /// <summary>После каждого тика кадра (<see cref="SimulationClock"/>): наблюдатели модулей с <c>TickId</c> сыгранного тика.</summary>
    public void AfterTick(ulong tick)
    {
        if (tick != _firstTick + (ulong)_ticks)
            throw new InvalidOperationException($"тик {tick} вне порядка кадра: ждали {_firstTick + (ulong)_ticks} (кадр не начат BeginFrame?)");
        _ticks++;
        foreach (var m in _active)
        {
            if (m.Observers.Count == 0) continue;
            m.ObservedTicks++;
            foreach (var observe in m.Observers) observe(tick);
        }
    }

    /// <summary>Тики этого кадра (с начала <see cref="BeginFrame"/>) и заявки <paramref name="requests"/> GPU-симуляции.</summary>
    public SimFrame Frame(SimRequests? requests = null) => new(_firstTick, _ticks, requests);

    /// <summary>Извлечение кадра модулями — после извлечения игры.</summary>
    public void Extract(in FrameView view)
    {
        foreach (var m in _active)
            foreach (var extract in m.Extracts) extract(in view);
    }

    /// <summary>Точечные источники модулей в кадр — до проходов игры (<see cref="ClientBuilder.Lights"/>).</summary>
    public void AddLights(PointLights lights)
    {
        foreach (var m in _active)
            foreach (var add in m.Lights) add(lights);
    }

    /// <summary>Проходы модулей после проходов игры: шаги GPU-симуляций (фаза Simulation), затем проходы своих фаз.</summary>
    public void BuildFrame(RenderFrame frame)
    {
        foreach (var m in _active)
        {
            int before = frame.Graph.Registered;
            foreach (var (sim, requests) in m.Simulations)
            {
                int from = frame.Graph.Registered;
                sim.AddPasses(frame, Frame(requests));
                Check(frame, m, from, FramePhase.Simulation, sim.Name);
            }
            foreach (var (phase, build) in m.Passes)
            {
                int from = frame.Graph.Registered;
                build(frame);
                Check(frame, m, from, phase, null);
            }
            m.LastFramePasses = frame.Graph.Registered - before;
        }
    }

    private static void Check(RenderFrame frame, Module m, int from, FramePhase phase, string? simulation)
    {
        for (int i = from; i < frame.Graph.Registered; i++)
        {
            var (name, actual) = frame.Graph.RegisteredPass(i);
            if (actual != phase)
                throw new InvalidOperationException($"модуль {m.Id}{(simulation is null ? "" : $", GPU-симуляция {simulation}")}: проход {name} — фаза {actual}, " +
                    $"а зарегистрирован для {phase}");
            if (!m.PassNames.Contains(name)) m.PassNames.Add(name);
        }
    }

    /// <summary>Модули в итоге прогона: включённые, затем выключенные.</summary>
    public IReadOnlyList<ClientModuleReport> Report() =>
    [
        .. _active.Select(m => new ClientModuleReport(m.Id, true, [.. m.PassNames], [.. m.Overlays.Select(o => o.Name)],
            [.. m.Tuning.Select(t => t.Id)], [.. m.Simulations.Select(s => s.Simulation.Name)], m.Observers.Count, m.ObservedTicks, m.LastFramePasses)),
        .. _inactive.Select(id => new ClientModuleReport(id, false, [], [], [], [], 0, 0, 0)),
    ];

    /// <summary>Разделы итога <c>--json</c> включённых модулей (<see cref="ClientBuilder.Report"/>): Id → значение.</summary>
    public IEnumerable<(string Id, object? Value)> Sections() =>
        _active.Where(m => m.Report is not null).Select(m => (m.Id, m.Report!()));

    /// <summary>Ресурсы модулей — после ожидания GPU, в обратном порядке. Исключение одного не мешает освободить остальные:
    /// бросается после всех.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? errors = null;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var owned = _active[i].Owned;
            for (int k = owned.Count - 1; k >= 0; k--)
                try
                {
                    owned[k].Dispose();
                }
                catch (Exception e)
                {
                    (errors ??= []).Add(e);
                }
            owned.Clear();
        }
        if (errors is [var single]) ExceptionDispatchInfo.Throw(single);
        if (errors is not null) throw new AggregateException("освобождение клиентских модулей", errors);
    }
}
