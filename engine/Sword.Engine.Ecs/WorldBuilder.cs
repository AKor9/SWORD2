using System.Reflection;

namespace Sword.Engine.Ecs;

/// <summary>
/// Сборка мира: компоненты, события, ресурсы, стадии и системы по стадиям. Регистрация явная; номера типов — по
/// полному имени типа, от порядка регистрации не зависят. При <see cref="Build"/> планировщик:
/// <list type="bullet">
/// <item>заводит журналы только для срабатываний, на которые кто-то подписан;</item>
/// <item>проверяет каждую пару производитель → потребитель: производитель в той же или более поздней стадии, чем
/// потребитель, — ошибка, если потребитель не объявил <see cref="SystemSetup.NextTickOk{TTrigger}"/>;</item>
/// <item>упорядочивает системы стадии по <c>After/Before</c> (порядок регистрации — только развязка), цикл — ошибка.</item>
/// </list>
/// </summary>
public sealed class WorldBuilder
{
    private readonly Dictionary<Type, Func<DataType>> _components = [];
    private readonly Dictionary<Type, Func<DataType>> _events = [];
    private readonly List<Action<World>> _resources = [];
    private readonly HashSet<Type> _resourceTypes = [];
    private readonly List<(string Stage, ISystem System, string? Module, string? Source)> _systems = [];
    private string[] _stages = [];

    public WorldOptions Options { get; set; } = new();

    public WorldBuilder Stages(params string[] stages)
    {
        if (stages.Length == 0 || stages.Distinct().Count() != stages.Length)
            throw new ArgumentException("стадии — непустой список без повторов");
        _stages = stages;
        return this;
    }

    public WorldBuilder Component<T>() where T : unmanaged
    {
        _components.TryAdd(typeof(T), DataType.Create<T>);
        return this;
    }

    public WorldBuilder Event<E>() where E : unmanaged
    {
        _events.TryAdd(typeof(E), DataType.Create<E>);
        return this;
    }

    public WorldBuilder Resource<T>(T initial)
    {
        if (!_resourceTypes.Add(typeof(T))) throw new InvalidOperationException($"ресурс {typeof(T).FullName} зарегистрирован дважды");
        _resources.Add(w => w.AddResource(initial));
        return this;
    }

    /// <summary>
    /// Вид только для чтения на данные владельца мира, которых нет в ECS (пул поз подсистемы и т. п.): наблюдатели
    /// симуляции получают его через <see cref="WorldReader.View{TView}"/>. Тип вида — интерфейс, и его члены обязаны
    /// только читать (значения и <c>ReadOnlySpan</c>): так <see cref="WorldReader"/> остаётся без путей изменить мир.
    /// </summary>
    public WorldBuilder ReadView<TView>(TView view) where TView : class, IReadView
    {
        if (!typeof(TView).IsInterface) throw new ArgumentException($"вид {typeof(TView).FullName} — не интерфейс: наблюдателю отдаётся только интерфейс");
        ArgumentNullException.ThrowIfNull(view);
        if (!_viewTypes.Add(typeof(TView))) throw new InvalidOperationException($"вид {typeof(TView).FullName} зарегистрирован дважды");
        _resources.Add(w => w.AddView(typeof(TView), view));
        return this;
    }

    private readonly HashSet<Type> _viewTypes = [];

    /// <summary>Система в стадию. Одну систему можно зарегистрировать в двух стадиях — тогда она обязана быть идемпотентной.</summary>
    /// <param name="module">Модуль-владелец (токен модуля: включение и выключение всех его регистраций).</param>
    /// <param name="source">Где система зарегистрирована (файл:строка) — для описания мира; null — неизвестно.</param>
    public WorldBuilder System(string stage, ISystem system, string? module = null, string? source = null)
    {
        _systems.Add((stage, system, module, source));
        return this;
    }

    public World Build()
    {
        if (_stages.Length == 0) throw new InvalidOperationException("мир без стадий: WorldBuilder.Stages(...)");
        var types = Number(_components.Values.Select(f => f()), "компоненты");
        var events = Number(_events.Values.Select(f => f()), "события");
        var world = new World(Options, types, events, _stages);
        foreach (var add in _resources) add(world);

        var registrations = new List<Registration>();
        foreach (var (stage, system, module, source) in _systems)
        {
            int stageIndex = Array.IndexOf(_stages, stage);
            if (stageIndex < 0) throw new InvalidOperationException($"{system.GetType().Name}: нет стадии {stage}");
            var r = new Registration
            {
                System = system, Stage = stage, StageIndex = stageIndex, Id = registrations.Count + 1, Module = module,
                Source = source,
            };
            world.AddRegistration(r);
            system.Setup(new SystemSetup(world, r));
            if (string.IsNullOrEmpty(r.Label.Name)) r.Label = new SystemLabel(system.GetType().Name);
            Bind(r);
            registrations.Add(r);
        }

        foreach (var group in registrations.GroupBy(r => (r.StageIndex, r.Label)).Where(g => g.Count() > 1))
            throw new InvalidOperationException($"метка {group.Key.Label} дважды в стадии {_stages[group.Key.StageIndex]}");

        foreach (var r in registrations)
        {
            if (r.Trigger is { } trigger) r.Cursors.Add((trigger, world.JournalFor(trigger), 0));
            foreach (var key in r.Consumes)
                if (key != r.Trigger) r.Cursors.Add((key, world.JournalFor(key), 0));
        }

        CheckProducers(world, registrations);

        for (int s = 0; s < _stages.Length; s++)
            world.SetOrder(s, Order(registrations.Where(r => r.StageIndex == s).ToList(), _stages[s]));
        return world;
    }

    /// <summary>
    /// Номера типов — по полному имени: одинаковые при любом порядке регистрации. Два разных типа с одним полным именем
    /// (из разных сборок) — ошибка: номер зависел бы от порядка регистрации, а стабильное имя было бы неоднозначным.
    /// </summary>
    private static DataType[] Number(IEnumerable<DataType> types, string kind)
    {
        var sorted = types.OrderBy(t => t.StableName, StringComparer.Ordinal).ToArray();
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i].StableName == sorted[i - 1].StableName)
                throw new InvalidOperationException($"{kind}: два типа с одним стабильным именем {sorted[i].StableName} — " +
                    $"{sorted[i - 1].Type.AssemblyQualifiedName} и {sorted[i].Type.AssemblyQualifiedName}; переименуйте один");
        for (int i = 0; i < sorted.Length; i++) sorted[i].Id = i;
        return sorted;
    }

    /// <summary>Как исполнять регистрацию: непрерывная или реактивная (одно срабатывание).</summary>
    private static void Bind(Registration r)
    {
        var reactive = r.System.GetType().GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReactiveSystem<>)).ToArray();
        bool continuous = r.System is IContinuousSystem;
        if (reactive.Length > 1) throw new InvalidOperationException($"{r.Name}: у реактивной системы одно срабатывание; нужно два — две системы");
        if (reactive.Length == 1 && continuous) throw new InvalidOperationException($"{r.Name}: система либо непрерывная, либо реактивная");
        if (reactive.Length == 0 && !continuous) throw new InvalidOperationException($"{r.Name}: не IContinuousSystem и не IReactiveSystem<T>");

        if (continuous)
        {
            r.Run = RunContinuous;
            return;
        }
        var triggerType = reactive[0].GetGenericArguments()[0];
        r.Trigger = TriggerKey.Of(triggerType);
        r.Consumes.Add(r.Trigger.Value);
        r.Run = (Action<World, Registration, ulong>)typeof(WorldBuilder)
            .GetMethod(nameof(RunReactive), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(triggerType)
            .CreateDelegate(typeof(Action<World, Registration, ulong>));
    }

    private static void RunContinuous(World world, Registration r, ulong tick)
    {
        ((IContinuousSystem)r.System).Run(new SystemContext(world, r, tick));
        r.AdvanceCursors();
    }

    private static void RunReactive<TTrigger>(World world, Registration r, ulong tick) where TTrigger : struct
    {
        var c = r.Cursors[0];
        var triggers = ((Journal<TTrigger>)c.Journal).Since(c.Cursor);
        if (triggers.IsEmpty)
        {
            // Дополнительные журналы — контекст срабатывания: тик без срабатывания их записи пропускает, иначе в
            // следующем срабатывании часть старых записей была бы видна, а часть — уже вычищена.
            if (r.Cursors.Count > 1) r.AdvanceCursors();
            return;
        }
        ((IReactiveSystem<TTrigger>)r.System).React(new SystemContext(world, r, tick), triggers);
        r.AdvanceCursors();
    }

    /// <summary>
    /// Пара производитель → потребитель: стадия производителя раньше стадии потребителя — реакция в том же тике;
    /// иначе — в следующем, и без <c>NextTickOk</c> это ошибка сборки мира.
    /// </summary>
    private void CheckProducers(World world, List<Registration> registrations)
    {
        var errors = new List<string>();
        foreach (var consumer in registrations)
            foreach (var key in consumer.Consumes)
            {
                if (consumer.NextTickOk.Contains(key)) continue;
                foreach (var producer in registrations)
                    if (producer.StageIndex >= consumer.StageIndex && producer.Produces(key, world))
                        errors.Add($"{consumer.Name} реагирует на {key}, а {producer.Name} производит его в стадии {producer.Stage} — " +
                            $"не раньше потребителя ({consumer.Stage}): реакция придёт в следующем тике. Объявите NextTickOk<{key}>() " +
                            "или перенесите системы по стадиям");
            }
        if (errors.Count > 0) throw new InvalidOperationException("порядок стадий:\n" + string.Join("\n", errors));
    }

    /// <summary>Топологический порядок по After/Before; при равенстве — порядок регистрации. Метки вне стадии не связывают.</summary>
    private static List<Registration> Order(List<Registration> stage, string stageName)
    {
        var byLabel = stage.ToDictionary(r => r.Label);
        var edges = stage.ToDictionary(r => r, _ => new List<Registration>());
        var indegree = stage.ToDictionary(r => r, _ => 0);
        void Edge(Registration from, Registration to)
        {
            edges[from].Add(to);
            indegree[to]++;
        }
        foreach (var r in stage)
        {
            foreach (var l in r.After)
                if (byLabel.TryGetValue(l, out var before)) Edge(before, r);
            foreach (var l in r.Before)
                if (byLabel.TryGetValue(l, out var after)) Edge(r, after);
        }
        var ready = new PriorityQueue<Registration, int>();
        foreach (var r in stage)
            if (indegree[r] == 0) ready.Enqueue(r, r.Id);
        var order = new List<Registration>(stage.Count);
        while (ready.TryDequeue(out var r, out _))
        {
            order.Add(r);
            foreach (var next in edges[r])
                if (--indegree[next] == 0) ready.Enqueue(next, next.Id);
        }
        if (order.Count != stage.Count)
            throw new InvalidOperationException($"цикл After/Before в стадии {stageName}: " +
                string.Join(", ", stage.Where(r => indegree[r] > 0).Select(r => r.Label)));
        return order;
    }
}
