namespace Sword.Engine.Ecs;

/// <summary>Система: объявляет в <see cref="Setup"/> метку, порядок, запросы и что производит.</summary>
public interface ISystem
{
    void Setup(SystemSetup s);
}

/// <summary>Непрерывная система: исполняется каждый тик в своей стадии.</summary>
public interface IContinuousSystem : ISystem
{
    void Run(in SystemContext ctx);
}

/// <summary>
/// Реактивная система: исполняется в своей стадии, только если с её прошлого запуска были срабатывания
/// <typeparamref name="TTrigger"/> (<see cref="Added{T}"/>, <see cref="Removed{T}"/>, <see cref="Changed{T}"/>,
/// <see cref="Event{E}"/>, <see cref="ResourceChanged{T}"/>), и получает только их. Без срабатываний стоит одну проверку
/// длины журнала. Одно срабатывание на систему; нужно два — две системы.
/// </summary>
public interface IReactiveSystem<TTrigger> : ISystem where TTrigger : struct
{
    void React(in SystemContext ctx, ReadOnlySpan<TTrigger> triggers);
}

/// <summary>Вид срабатывания.</summary>
public enum TriggerKind { Added, Removed, Changed, Event, ResourceChanged }

/// <summary>Срабатывание: вид и тип данных (компонент, событие или ресурс).</summary>
public readonly record struct TriggerKey(TriggerKind Kind, Type Data)
{
    public static TriggerKey Of(Type trigger)
    {
        if (!trigger.IsGenericType) throw new ArgumentException($"{trigger.Name} — не срабатывание (Added<T>, Removed<T>, Changed<T>, Event<E>, ResourceChanged<T>)");
        var def = trigger.GetGenericTypeDefinition();
        var data = trigger.GetGenericArguments()[0];
        TriggerKind kind =
            def == typeof(Added<>) ? TriggerKind.Added :
            def == typeof(Removed<>) ? TriggerKind.Removed :
            def == typeof(Changed<>) ? TriggerKind.Changed :
            def == typeof(Event<>) ? TriggerKind.Event :
            def == typeof(ResourceChanged<>) ? TriggerKind.ResourceChanged :
            throw new ArgumentException($"{trigger.Name} — не срабатывание (Added<T>, Removed<T>, Changed<T>, Event<E>, ResourceChanged<T>)");
        return new TriggerKey(kind, data);
    }

    public Type EntryType => Kind switch
    {
        TriggerKind.Added => typeof(Added<>).MakeGenericType(Data),
        TriggerKind.Removed => typeof(Removed<>).MakeGenericType(Data),
        TriggerKind.Changed => typeof(Changed<>).MakeGenericType(Data),
        TriggerKind.Event => typeof(Event<>).MakeGenericType(Data),
        _ => typeof(ResourceChanged<>).MakeGenericType(Data),
    };

    public override string ToString() => $"{Kind}<{Data.Name}>";
}

/// <summary>Ключ срабатывания по типу записи — считается один раз (отражение без выделений в горячем пути).</summary>
internal static class TriggerKeyCache<TTrigger> where TTrigger : struct
{
    public static readonly TriggerKey Key = TriggerKey.Of(typeof(TTrigger));
}

/// <summary>
/// Объявления системы при сборке мира: метка, порядок внутри стадии, запросы, что система производит (по ним
/// планировщик проверяет порядок стадий производителя и потребителя), какие журналы читает непрерывная система и где
/// задержка реакции на тик разрешена (<see cref="NextTickOk{TTrigger}"/>).
/// </summary>
public sealed class SystemSetup
{
    private readonly World _world;
    internal readonly Registration Registration;

    internal SystemSetup(World world, Registration registration)
    {
        _world = world;
        Registration = registration;
    }

    public SystemSetup Label(SystemLabel label)
    {
        Registration.Label = label;
        return this;
    }

    public SystemSetup After(SystemLabel label)
    {
        Registration.After.Add(label);
        return this;
    }

    public SystemSetup Before(SystemLabel label)
    {
        Registration.Before.Add(label);
        return this;
    }

    /// <summary>Новый запрос системы; исполняется через <see cref="SystemContext.Iterate"/>.</summary>
    public QueryBuilder Query() => new(_world);

    /// <summary>Читает компонент (объявление для проверки доступа и параллельности, v1).</summary>
    public SystemSetup Reads<T>() where T : unmanaged
    {
        Registration.Reads.Add(_world.TypeOf<T>().Id);
        return this;
    }

    /// <summary>Пишет компонент на месте (объявление для проверки доступа и параллельности, v1).</summary>
    public SystemSetup Writes<T>() where T : unmanaged
    {
        Registration.Writes.Add(_world.TypeOf<T>().Id);
        return this;
    }

    public SystemSetup Creates()
    {
        Registration.CreatesEntities = true;
        return this;
    }

    /// <summary>Удаляет сущности — производит <see cref="Removed{T}"/> по каждому их компоненту.</summary>
    public SystemSetup Destroys()
    {
        Registration.DestroysEntities = true;
        return this;
    }

    public SystemSetup Adds<T>() where T : unmanaged
    {
        Registration.Adds.Add(_world.TypeOf<T>().Id);
        return this;
    }

    public SystemSetup Removes<T>() where T : unmanaged
    {
        Registration.Removes.Add(_world.TypeOf<T>().Id);
        return this;
    }

    public SystemSetup Sets<T>() where T : unmanaged
    {
        Registration.Sets.Add(_world.TypeOf<T>().Id);
        return this;
    }

    public SystemSetup Emits<E>() where E : unmanaged
    {
        Registration.Emits.Add(_world.EventTypeOf<E>().Id);
        return this;
    }

    public SystemSetup SetsResource<T>()
    {
        _world.ResourceSlotOf<T>(out _);
        Registration.SetsResources.Add(typeof(T));
        return this;
    }

    /// <summary>
    /// Система читает журнал срабатываний своим курсором (<see cref="SystemContext.Journal{T}"/>). Непрерывная видит каждую
    /// запись ровно один раз. У реактивной это контекст срабатывания: в тике, где она сработала, — записи с её прошлого
    /// места в тике; записи тиков без срабатывания ей не адресованы и пропускаются.
    /// </summary>
    public SystemSetup ReadsJournal<TTrigger>() where TTrigger : struct
    {
        Registration.Consumes.Add(TriggerKey.Of(typeof(TTrigger)));
        return this;
    }

    /// <summary>
    /// Реакция на срабатывания производителей в той же или более поздней стадии приходит в следующем тике — и это
    /// задумано. Без объявления такая пара — ошибка сборки мира.
    /// </summary>
    public SystemSetup NextTickOk<TTrigger>() where TTrigger : struct
    {
        Registration.NextTickOk.Add(TriggerKey.Of(typeof(TTrigger)));
        return this;
    }
}

/// <summary>Что видит система во время исполнения: мир (чтение), свой буфер команд, номер тика, свои журналы.</summary>
public readonly struct SystemContext
{
    private readonly Registration _registration;

    internal SystemContext(World world, Registration registration, ulong tick)
    {
        World = world;
        _registration = registration;
        Tick = tick;
    }

    public World World { get; }

    /// <summary>Буфер команд этой регистрации: применяется на барьере после стадии.</summary>
    public CommandBuffer Commands => _registration.Buffer;

    /// <summary>
    /// Причина — родитель всего, что система запишет дальше (<see cref="CommandBuffer.Cause"/>; <c>state.why</c>, 6д): реактивная
    /// система ставит её по срабатыванию, которое обрабатывает (<c>ctx.Cause = c.Cause</c>), непрерывный производитель — явно
    /// (причина начала удара из <see cref="CausalityLog.Linked"/>). Перед запуском системы — пусто; у реактивной с одним
    /// срабатыванием в диагностическом режиме — его причина.
    /// </summary>
    public CauseId Cause
    {
        get => _registration.Buffer.Cause;
        set => _registration.Buffer.Cause = value;
    }

    /// <summary>Журнал причинности (диагностический режим); null — выключен: всё, что только для него, пропускается.</summary>
    public CausalityLog? Causality => World.Causality;

    public ulong Tick { get; }

    /// <summary>Чанки запроса; внутри — <see cref="ChunkView.Read{T}"/> и <see cref="ChunkView.Write{T}"/>.</summary>
    public QueryIterator Iterate(Query query) => new(World, World.OwnQuery(query), _registration);

    /// <summary>Непрочитанные записи журнала, объявленного <see cref="SystemSetup.ReadsJournal{TTrigger}"/>.</summary>
    public ReadOnlySpan<TTrigger> Journal<TTrigger>() where TTrigger : struct
    {
        var key = TriggerKeyCache<TTrigger>.Key;
        for (int i = 0; i < _registration.Cursors.Count; i++)
        {
            var c = _registration.Cursors[i];
            if (c.Key == key) return ((Journal<TTrigger>)c.Journal).Since(c.Cursor);
        }
        throw new InvalidOperationException($"{_registration.Name}: журнал {key} не объявлен (ReadsJournal)");
    }
}

/// <summary>Регистрация системы: стадия, метка, номер; идентичность — (Stage, Label, RegistrationId).</summary>
internal sealed class Registration
{
    public required ISystem System { get; init; }
    public required string Stage { get; init; }
    public required int StageIndex { get; init; }
    public required int Id { get; init; }
    public string? Module { get; init; }
    /// <summary>Где система зарегистрирована (файл:строка) — для людей и агента; null — неизвестно.</summary>
    public string? Source { get; init; }
    public SystemLabel Label;
    public readonly List<SystemLabel> After = [];
    public readonly List<SystemLabel> Before = [];
    public readonly HashSet<int> Reads = [], Writes = [], Adds = [], Removes = [], Sets = [], Emits = [];
    public readonly HashSet<Type> SetsResources = [];
    public readonly HashSet<TriggerKey> Consumes = [];
    public readonly HashSet<TriggerKey> NextTickOk = [];
    public bool CreatesEntities, DestroysEntities;
    public bool Active = true;
    public CommandBuffer Buffer = null!;
    /// <summary>Курсоры журналов: срабатывание реактивной системы и журналы непрерывной.</summary>
    public readonly List<(TriggerKey Key, JournalBase Journal, long Cursor)> Cursors = [];
    public Action<World, Registration, ulong> Run = null!;
    /// <summary>Срабатывание реактивной системы (его курсор — первый в <see cref="Cursors"/>); null — непрерывная.</summary>
    public TriggerKey? Trigger;

    public string Name => $"{Label}@{Stage}";

    /// <summary>Номер зоны профайлера CPU (<see cref="World.AttachProfiler"/>): имя строится один раз, не в тике.</summary>
    public int Zone;

    /// <summary>Производит ли регистрация это срабатывание (по объявлениям).</summary>
    public bool Produces(TriggerKey key, World world) => key.Kind switch
    {
        TriggerKind.Added => Adds.Contains(world.TypeOf(key.Data).Id),
        TriggerKind.Removed => DestroysEntities || Removes.Contains(world.TypeOf(key.Data).Id),
        // Add уже имеющегося компонента без проверок мира заменяет значение — это тоже Changed.
        TriggerKind.Changed => Sets.Contains(world.TypeOf(key.Data).Id) || Adds.Contains(world.TypeOf(key.Data).Id),
        TriggerKind.Event => Emits.Contains(world.EventTypeOf(key.Data).Id),
        _ => SetsResources.Contains(key.Data),
    };

    public void AdvanceCursors()
    {
        for (int i = 0; i < Cursors.Count; i++)
        {
            var c = Cursors[i];
            Cursors[i] = (c.Key, c.Journal, c.Journal.End);
        }
    }
}

/// <summary>Потребляемый журнал: вид срабатывания и данные (стабильное имя компонента, события или ресурса).</summary>
public sealed record TriggerInfo(string Kind, string Data);

/// <summary>
/// Регистрация системы наружу — для описания мира, профайлера, агента (карта архитектуры, раздел 8 плана): что система
/// читает и пишет, какие события порождает и какие журналы потребляет, где стоит в стадии и где зарегистрирована.
/// Компоненты и события — стабильными именами.
/// </summary>
public sealed record SystemInfo(
    string Name, string Stage, string Label, int RegistrationId, string? Module, bool Active, bool Reactive,
    IReadOnlyList<string> Consumes)
{
    /// <summary>Место в порядке исполнения своей стадии (с 0).</summary>
    public int Order { get; init; }

    /// <summary>Тип системы (полное имя).</summary>
    public string SystemType { get; init; } = "";

    /// <summary>Срабатывание реактивной системы (<c>Added&lt;T&gt;</c>…); у непрерывной — null.</summary>
    public string? Trigger { get; init; }

    public IReadOnlyList<string> Reads { get; init; } = [];
    public IReadOnlyList<string> Writes { get; init; } = [];
    public IReadOnlyList<string> Adds { get; init; } = [];
    public IReadOnlyList<string> Removes { get; init; } = [];
    public IReadOnlyList<string> Sets { get; init; } = [];
    public IReadOnlyList<string> Emits { get; init; } = [];
    public IReadOnlyList<string> SetsResources { get; init; } = [];
    public IReadOnlyList<string> NextTickOk { get; init; } = [];

    /// <summary>Данные потребляемых журналов (<see cref="Consumes"/> и срабатывание) — стабильными именами.</summary>
    public IReadOnlyList<string> ConsumesData { get; init; } = [];

    /// <summary>Потребляемые журналы с видом: <c>Added</c>, <c>Removed</c>, <c>Changed</c>, <c>Event</c>, <c>ResourceChanged</c>.</summary>
    public IReadOnlyList<TriggerInfo> ConsumesTriggers { get; init; } = [];
    public IReadOnlyList<string> After { get; init; } = [];
    public IReadOnlyList<string> Before { get; init; } = [];
    public bool CreatesEntities { get; init; }
    public bool DestroysEntities { get; init; }

    /// <summary>Где зарегистрирована (файл:строка); null — неизвестно.</summary>
    public string? Source { get; init; }
}
