using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>Настройки мира.</summary>
public sealed record WorldOptions
{
    /// <summary>Байт на чанк (старт — 16 КБ; настройка, а не контракт).</summary>
    public int ChunkBytes { get; init; } = 16 * 1024;

    /// <summary>
    /// Проверки мира: необъявленное производство, черновик чужого буфера, отложенное значение мимо команд, запись в
    /// реактивный компонент мимо <c>Set</c>, <c>Add</c> уже имеющегося компонента. По умолчанию — в Debug.
    /// </summary>
    public bool Checks { get; init; } = DefaultChecks;

#if DEBUG
    public const bool DefaultChecks = true;
#else
    public const bool DefaultChecks = false;
#endif
}

/// <summary>
/// Мир ECS (раздел 5 плана): сущности в архетипах и чанках, ресурсы, журналы срабатываний, стадии и барьеры.
/// Системы меняют мир только командами (<see cref="CommandBuffer"/>) и записью колонок на месте; команды применяются
/// на барьере после стадии: проигрывание буферов в порядке систем (черновики становятся сущностями, разрешитель
/// подставляет их в поля), нормализация срабатываний компонентов (таблица раздела 5), затем события в том же порядке.
/// Исполнение последовательное (v0). Собирает мир <see cref="WorldBuilder"/>.
/// </summary>
public sealed class World
{
    // ── типы ───────────────────────────────────────────────────────────────
    private readonly DataType[] _types;
    private readonly Dictionary<Type, DataType> _typeByClr;
    private readonly DataType?[] _typeByProcessId;
    private readonly DataType[] _eventTypes;
    private readonly Dictionary<Type, DataType> _eventByClr;
    private readonly DataType?[] _eventByProcessId;
    private readonly List<ResourceSlot> _resources = [];
    private readonly Dictionary<Type, int> _resourceIndex = [];

    // ── сущности и хранение ────────────────────────────────────────────────
    internal readonly List<Archetype> Archetypes = [];
    private readonly Dictionary<string, Archetype> _archetypeByKey = [];
    private readonly Archetype _empty;
    private int[] _generations = new int[64];
    private Archetype?[] _archetypeOf = new Archetype?[64];
    private int[] _chunkOf = new int[64], _rowOf = new int[64];
    private int _entityHigh;
    private readonly Stack<int> _free = new();

    // ── стадии, системы, журналы ───────────────────────────────────────────
    private readonly string[] _stages;
    private readonly List<Registration>[] _order;
    private readonly List<Registration> _registrations = [];
    private readonly Dictionary<TriggerKey, JournalBase> _journals = [];
    private readonly CommandBuffer _immediate;
    private long _barrier;
    /// <summary>Идёт тик: немедленные операции, повторный тик и переключение систем запрещены.</summary>
    private bool _inTick;

    // ── барьер: нормализация срабатываний ──────────────────────────────────
    private readonly bool[] _tracked;
    private readonly Dictionary<(Entity, int), int> _trackIndex = [];
    private readonly List<Track> _tracks = [];
    private byte[] _removedValues = new byte[256];
    private int _removedUsed;
    private readonly List<(Archetype, Chunk, DataType)> _written = [];

    private struct Track
    {
        public Entity Entity;
        public int Type;
        public bool Before, Removed, Set;
        public int FirstRemoval, LastRemoval;
        // Причины раздельно: удаление в начале барьера и итоговое добавление или изменение приходят от разных команд.
        public CauseId FirstRemovalCause, LastRemovalCause, Cause;
    }

    internal World(WorldOptions options, DataType[] types, DataType[] events, string[] stages)
    {
        Options = options;
        Checks = options.Checks;
        _types = types;
        _typeByClr = types.ToDictionary(t => t.Type);
        _typeByProcessId = ProcessTypeTable.Build(types);
        _eventTypes = events;
        _eventByClr = events.ToDictionary(t => t.Type);
        _eventByProcessId = ProcessTypeTable.Build(events);
        _stages = stages;
        _order = new List<Registration>[stages.Length];
        for (int i = 0; i < stages.Length; i++) _order[i] = [];
        _tracked = new bool[types.Length];
        _empty = ArchetypeFor([]);
        _immediate = new CommandBuffer(this, 0, owner: null);
    }

    public WorldOptions Options { get; }

    /// <summary>Проверки мира включены (<see cref="WorldOptions.Checks"/>).</summary>
    public bool Checks { get; }

    public IReadOnlyList<string> Stages => _stages;

    /// <summary>Живых сущностей.</summary>
    public int EntityCount { get; private set; }

    /// <summary>
    /// Номер барьера: растёт на каждом барьере стадии. Немедленные изменения вне тика его не двигают — их записи
    /// числятся за ближайшим барьером, поэтому срок жизни записей всегда ровно один оборот тика.
    /// </summary>
    public long Barrier => _barrier;

    /// <summary>Команд, отброшенных на барьере: цель к тому времени удалена, <c>Set</c> несуществующего компонента.</summary>
    public long IgnoredCommands { get; private set; }

    /// <summary>Номеров сущностей, выведенных из оборота: их поколение дошло до предела (2³¹ − 1 удалений).</summary>
    public long RetiredIndices { get; private set; }

    /// <summary>Регистрации систем в порядке исполнения по стадиям.</summary>
    public IReadOnlyList<SystemInfo> Systems =>
        [.. _order.SelectMany(o => o).Select(r => new SystemInfo(r.Name, r.Stage, r.Label.Name, r.Id, r.Module, r.Active,
            IsReactive(r.System), [.. r.Consumes.Select(k => k.ToString())]))];

    private static bool IsReactive(ISystem s) =>
        s.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReactiveSystem<>));

    // ── типы ───────────────────────────────────────────────────────────────

    /// <summary>Тип мира по статическому номеру типа в процессе — горячий путь обхода и команд.</summary>
    internal DataType TypeOf<T>() where T : unmanaged
    {
        int id = ProcessTypeId<T>.Value;
        var table = _typeByProcessId;
        return (uint)id < (uint)table.Length && table[id] is { } t ? t : TypeOf(typeof(T));
    }

    internal DataType TypeOf(Type type) =>
        _typeByClr.TryGetValue(type, out var t) ? t
            : throw new InvalidOperationException($"компонент {type.FullName} не зарегистрирован (WorldBuilder.Component<T>)");

    internal DataType EventTypeOf<E>() where E : unmanaged
    {
        int id = ProcessTypeId<E>.Value;
        var table = _eventByProcessId;
        return (uint)id < (uint)table.Length && table[id] is { } t ? t : EventTypeOf(typeof(E));
    }

    internal DataType EventTypeOf(Type type) =>
        _eventByClr.TryGetValue(type, out var t) ? t
            : throw new InvalidOperationException($"событие {type.FullName} не зарегистрировано (WorldBuilder.Event<E>)");

    internal ResourceSlot<T> ResourceSlotOf<T>(out int index)
    {
        if (!_resourceIndex.TryGetValue(typeof(T), out index))
            throw new InvalidOperationException($"ресурс {typeof(T).FullName} не зарегистрирован (WorldBuilder.Resource<T>)");
        return (ResourceSlot<T>)_resources[index];
    }

    internal void AddResource<T>(T initial) => _resourceIndex[typeof(T)] = AddSlot(new ResourceSlot<T> { Value = initial, Set = true });

    private int AddSlot(ResourceSlot slot)
    {
        _resources.Add(slot);
        return _resources.Count - 1;
    }

    // ── чтение ─────────────────────────────────────────────────────────────

    /// <summary>Мир только для чтения — для наблюдателей симуляции (клиентская часть игры, инструменты).</summary>
    public WorldReader Reader => new(this);

    /// <summary>Зарегистрирован ли компонент <typeparamref name="T"/>.</summary>
    public bool Knows<T>() where T : unmanaged => _typeByClr.ContainsKey(typeof(T));

    public bool IsAlive(Entity e)
    {
        CheckNotDeferred(e);
        return e.Index >= 0 && e.Index < _entityHigh && _generations[e.Index] == e.Generation && _archetypeOf[e.Index] is not null;
    }

    public bool Has<T>(Entity e) where T : unmanaged => IsAlive(e) && _archetypeOf[e.Index]!.Has[TypeOf<T>().Id];

    /// <summary>Значение компонента (копия).</summary>
    /// <exception cref="InvalidOperationException">Сущность мертва или компонента нет.</exception>
    public T Get<T>(Entity e) where T : unmanaged =>
        TryGet<T>(e, out var value) ? value : throw new InvalidOperationException(IsAlive(e)
            ? $"у {e} нет {typeof(T).Name}" : $"{e} не жива");

    public bool TryGet<T>(Entity e, out T value) where T : unmanaged
    {
        value = default;
        if (!IsAlive(e)) return false;
        var a = _archetypeOf[e.Index]!;
        var t = TypeOf<T>();
        if (!a.Has[t.Id]) return false;
        if (!t.IsTag) value = MemoryMarshal.Read<T>(a.Cell(_chunkOf[e.Index], _rowOf[e.Index], a.ColumnOf[t.Id]));
        return true;
    }

    public T Resource<T>() => ResourceSlotOf<T>(out _).Value;

    /// <summary>Новый запрос (вне систем — для инструментов и тестов).</summary>
    public QueryBuilder Query() => new(this);

    /// <summary>Обход запроса вне систем (инструменты, тесты, Extract клиента).</summary>
    public QueryIterator Iterate(Query query) => new(this, OwnQuery(query), null);

    /// <summary>Запрос чужого мира — ошибка: номера типов и архетипы у миров свои.</summary>
    internal Query OwnQuery(Query query) =>
        ReferenceEquals(query.World, this) ? query : throw new ArgumentException("запрос построен другим миром", nameof(query));

    /// <summary>Журнал срабатывания, если на него кто-то подписан (иначе журнала нет).</summary>
    public Journal<TTrigger>? JournalOf<TTrigger>() where TTrigger : struct =>
        _journals.TryGetValue(TriggerKeyCache<TTrigger>.Key, out var j) ? (Journal<TTrigger>)j : null;

    private void CheckNotDeferred(Entity e)
    {
        if (e.IsDeferred)
            throw new InvalidOperationException($"обращение к миру с {e}: отложенное значение разрешается только на барьере");
    }

    // ── немедленные изменения (сборка мира, тесты) ─────────────────────────
    // Каждое — отдельный маленький барьер: срабатывания попадают в журналы как от команд.

    public Entity CreateImmediate()
    {
        NotInTick(nameof(CreateImmediate));
        var draft = _immediate.Create();
        ApplyImmediate();
        return _immediate.Drafts[draft.Index];
    }

    public void AddImmediate<T>(Entity e, in T value) where T : unmanaged
    {
        NotInTick(nameof(AddImmediate));
        _immediate.Add(e, value);
        ApplyImmediate();
    }

    public void SetImmediate<T>(Entity e, in T value) where T : unmanaged
    {
        NotInTick(nameof(SetImmediate));
        _immediate.Set(e, value);
        ApplyImmediate();
    }

    public void RemoveImmediate<T>(Entity e) where T : unmanaged
    {
        NotInTick(nameof(RemoveImmediate));
        _immediate.Remove<T>(e);
        ApplyImmediate();
    }

    public void DestroyImmediate(Entity e)
    {
        NotInTick(nameof(DestroyImmediate));
        _immediate.Destroy(e);
        ApplyImmediate();
    }

    public void EmitImmediate<E>(in E value) where E : unmanaged
    {
        NotInTick(nameof(EmitImmediate));
        _immediate.Emit(value);
        ApplyImmediate();
    }

    /// <summary>Заменить ресурс сразу; подписчики <see cref="ResourceChanged{T}"/> увидят замену.</summary>
    public void SetResourceImmediate<T>(T value)
    {
        NotInTick(nameof(SetResourceImmediate));
        var slot = ResourceSlotOf<T>(out _);
        slot.Value = value;
        slot.Journal?.Append(new ResourceChanged<T>(value, CauseId.None), _barrier);
    }

    private void ApplyImmediate()
    {
        try
        {
            PlayBuffers([_immediate]);
        }
        finally
        {
            // Ошибка посреди немедленной операции не оставляет её команду висеть до следующей.
            _immediate.Clear();
            _tracks.Clear();
            _trackIndex.Clear();
            _removedUsed = 0;
        }
    }

    // ── тик ────────────────────────────────────────────────────────────────

    /// <summary>Тик: стадии по порядку, после каждой — барьер.</summary>
    public void RunTick(ulong tick)
    {
        NotInTick(nameof(RunTick));
        _inTick = true;
        try
        {
            for (int s = 0; s < _stages.Length; s++)
            {
                var order = _order[s];
                for (int i = 0; i < order.Count; i++)
                {
                    var r = order[i];
                    if (!r.Active) continue;
                    r.Run(this, r, tick);
                    if (Checks && _written.Count > 0) CheckWrittenDeferred(r);
                }
                PlayBuffers(order);
                _barrier++;
                TrimJournals();
            }
        }
        catch
        {
            AbortBarrier();
            throw;
        }
        finally
        {
            _inTick = false;
        }
    }

    /// <summary>
    /// Тик прерван исключением: несыгранные команды и события, отслеживание и отложенные значения барьера отбрасываются —
    /// иначе следующий тик доставил бы их с опозданием или повторял бы ту же ошибку каждый тик. Сыгранное до ошибки
    /// остаётся в мире: тик, прерванный исключением, — ошибка программы, а не откатываемая транзакция.
    /// </summary>
    private void AbortBarrier()
    {
        foreach (var r in _registrations) r.Buffer.Clear();
        _tracks.Clear();
        _trackIndex.Clear();
        _removedUsed = 0;
        _written.Clear();
        for (int i = 0; i < _resources.Count; i++) _resources[i].ClearStaged();
    }

    /// <summary>
    /// Системы меняют мир только командами своего буфера: немедленная операция посреди тика обошла бы объявления,
    /// барьер и нормализацию. Проверка — одно сравнение, поэтому действует и без <see cref="Checks"/>.
    /// </summary>
    private void NotInTick(string operation)
    {
        if (_inTick)
            throw new InvalidOperationException($"World.{operation} во время тика: системы меняют мир только командами (ctx.Commands)");
    }

    /// <summary>
    /// Включить или выключить регистрацию. Выключенная не исполняется и журналы не держит; при включении её курсоры
    /// ставятся на конец журналов — прошлое не проигрывается.
    /// </summary>
    public void SetActive(string registrationName, bool active)
    {
        NotInTick(nameof(SetActive));
        var r = _registrations.Find(x => x.Name == registrationName)
            ?? throw new ArgumentException($"нет регистрации {registrationName}");
        SetActive(r, active);
    }

    /// <summary>Включить или выключить все регистрации модуля (токен модуля).</summary>
    public int SetModuleActive(string module, bool active)
    {
        NotInTick(nameof(SetModuleActive));
        int n = 0;
        foreach (var r in _registrations)
            if (r.Module == module)
            {
                SetActive(r, active);
                n++;
            }
        return n;
    }

    private static void SetActive(Registration r, bool active)
    {
        if (r.Active == active) return;
        r.Active = active;
        if (active) r.AdvanceCursors();
    }

    private void TrimJournals()
    {
        long upTo = _barrier - _stages.Length - 1;
        if (upTo < 0) return;
        foreach (var j in _journals.Values) j.TrimUpTo(upTo);
    }

    // ── сборка (WorldBuilder) ──────────────────────────────────────────────

    internal void AddRegistration(Registration r)
    {
        // Номер буфера входит в отложенное значение черновика (Entity.DraftBuffer); 0 — немедленные операции.
        if (_registrations.Count >= EntityDraft.MaxBuffers)
            throw new InvalidOperationException($"регистраций систем больше {EntityDraft.MaxBuffers}");
        _registrations.Add(r);
        r.Buffer = new CommandBuffer(this, _registrations.Count, r);
    }

    internal IReadOnlyList<Registration> Registrations => _registrations;

    internal void SetOrder(int stage, List<Registration> order) => _order[stage] = order;

    internal JournalBase JournalFor(TriggerKey key)
    {
        if (_journals.TryGetValue(key, out var journal)) return journal;
        journal = (JournalBase)Activator.CreateInstance(typeof(Journal<>).MakeGenericType(key.EntryType))!;
        _journals[key] = journal;
        switch (key.Kind)
        {
            case TriggerKind.Added or TriggerKind.Removed or TriggerKind.Changed:
            {
                var t = TypeOf(key.Data);
                if (key.Kind == TriggerKind.Changed && !t.Reactive)
                    throw new InvalidOperationException($"{key}: {t.StableName} не реактивный — Changed только у [Reactive]");
                t.Writer ??= (IJournalWriter)Activator.CreateInstance(typeof(ComponentJournals<>).MakeGenericType(key.Data))!;
                t.Writer.Attach(key.Kind, journal);
                _tracked[t.Id] = true;
                break;
            }
            case TriggerKind.Event:
            {
                var t = EventTypeOf(key.Data);
                t.Writer ??= (IJournalWriter)Activator.CreateInstance(typeof(EventJournal<>).MakeGenericType(key.Data))!;
                t.Writer.Attach(key.Kind, journal);
                break;
            }
            default:
            {
                if (!_resourceIndex.TryGetValue(key.Data, out int index))
                    throw new InvalidOperationException($"{key}: ресурс {key.Data.FullName} не зарегистрирован");
                _resources[index].Attach(journal);
                break;
            }
        }
        return journal;
    }

    // ── хранение ───────────────────────────────────────────────────────────

    private Archetype ArchetypeFor(int[] sorted)
    {
        string key = string.Join(',', sorted);
        if (_archetypeByKey.TryGetValue(key, out var a)) return a;
        a = new Archetype(Archetypes.Count, sorted, _types, Options.ChunkBytes);
        Archetypes.Add(a);
        _archetypeByKey[key] = a;
        return a;
    }

    private Archetype WithType(Archetype a, int type)
    {
        if (a.AddEdge.TryGetValue(type, out var next)) return next;
        var set = a.Components.Append(type).Order().ToArray();
        next = ArchetypeFor(set);
        a.AddEdge[type] = next;
        next.RemoveEdge[type] = a;
        return next;
    }

    private Archetype WithoutType(Archetype a, int type) =>
        a.RemoveEdge.TryGetValue(type, out var next) ? next : NewRemoveEdge(a, type);

    // Отдельно от быстрого пути: лямбда захватывает type, и объект замыкания создавался бы на входе в метод — на каждом
    // Remove, даже когда ребро уже есть.
    private Archetype NewRemoveEdge(Archetype a, int type)
    {
        var next = ArchetypeFor([.. a.Components.Where(c => c != type)]);
        a.RemoveEdge[type] = next;
        next.AddEdge[type] = a;
        return next;
    }

    private Entity AllocateEntity()
    {
        int index;
        if (_free.Count > 0) index = _free.Pop();
        else
        {
            index = _entityHigh++;
            if (index == _generations.Length)
            {
                int n = _generations.Length * 2;
                Array.Resize(ref _generations, n);
                Array.Resize(ref _archetypeOf, n);
                Array.Resize(ref _chunkOf, n);
                Array.Resize(ref _rowOf, n);
            }
            _generations[index] = 1;
        }
        var e = new Entity(index, _generations[index]);
        Place(e, _empty);
        EntityCount++;
        return e;
    }

    private void Place(Entity e, Archetype a)
    {
        var (chunk, row) = a.AddRow(e);
        _archetypeOf[e.Index] = a;
        _chunkOf[e.Index] = chunk;
        _rowOf[e.Index] = row;
    }

    private void Unplace(Entity e)
    {
        var a = _archetypeOf[e.Index]!;
        var moved = a.RemoveRow(_chunkOf[e.Index], _rowOf[e.Index]);
        if (!moved.IsNull)
        {
            _chunkOf[moved.Index] = _chunkOf[e.Index];
            _rowOf[moved.Index] = _rowOf[e.Index];
        }
        _archetypeOf[e.Index] = null;
    }

    /// <summary>Перенести сущность в другой архетип, сохранив общие компоненты.</summary>
    private void Move(Entity e, Archetype to)
    {
        var from = _archetypeOf[e.Index]!;
        int fromChunk = _chunkOf[e.Index], fromRow = _rowOf[e.Index];
        var (chunk, row) = to.AddRow(e);
        foreach (var t in to.Columns)
        {
            int src = from.ColumnOf[t.Id];
            if (src >= 0) from.Cell(fromChunk, fromRow, src).CopyTo(to.Cell(chunk, row, to.ColumnOf[t.Id]));
        }
        var moved = from.RemoveRow(fromChunk, fromRow);
        if (!moved.IsNull)
        {
            _chunkOf[moved.Index] = fromChunk;
            _rowOf[moved.Index] = fromRow;
        }
        _archetypeOf[e.Index] = to;
        _chunkOf[e.Index] = chunk;
        _rowOf[e.Index] = row;
    }

    private Span<byte> CellOf(Entity e, DataType t)
    {
        var a = _archetypeOf[e.Index]!;
        return a.Cell(_chunkOf[e.Index], _rowOf[e.Index], a.ColumnOf[t.Id]);
    }

    // ── барьер ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Барьер: 1) команды буферов по порядку (черновики → сущности, разрешитель, отслеживание срабатываний);
    /// 2) нормализованные срабатывания компонентов в журналы; 3) события буферов в том же порядке.
    /// </summary>
    private void PlayBuffers(IReadOnlyList<Registration> order) => PlayBuffers(order, useRegistrations: true, null);

    private void PlayBuffers(CommandBuffer[] buffers) => PlayBuffers(null, useRegistrations: false, buffers);

    private void PlayBuffers(IReadOnlyList<Registration>? order, bool useRegistrations, CommandBuffer[]? buffers)
    {
        int count = useRegistrations ? order!.Count : buffers!.Length;
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            var b = useRegistrations ? order![i].Buffer : buffers![i];
            if (b.CommandCount == 0 && b.EventCount == 0) continue;
            any = true;
            for (int c = 0; c < b.CommandCount; c++) Play(b, ref b.Commands[c]);
        }
        if (!any) return;

        for (int i = 0; i < _resources.Count; i++) _resources[i].ClearStaged();
        FlushTracks();

        for (int i = 0; i < count; i++)
        {
            var b = useRegistrations ? order![i].Buffer : buffers![i];
            for (int k = 0; k < b.EventCount; k++)
            {
                ref var ev = ref b.Events[k];
                var t = _eventTypes[ev.Type];
                var payload = b.Payload.AsSpan(ev.PayloadOffset, ev.PayloadSize);
                Resolve(b, t, payload);
                t.Writer?.Event(payload, ev.Cause, _barrier);
            }
            b.Clear();
        }
    }

    private void Play(CommandBuffer b, ref CommandRecord c)
    {
        if (c.Op == CommandOp.Create)
        {
            b.Drafts[c.Target.DraftIndex] = AllocateEntity();
            return;
        }
        if (c.Op == CommandOp.SetResource)
        {
            _resources[c.Type].ApplyStaged(c.PayloadOffset, c.Cause, _barrier);
            return;
        }

        var e = c.Target.IsDeferred ? b.Drafts[c.Target.DraftIndex] : c.Target;
        if (!IsAlive(e))
        {
            IgnoredCommands++;
            return;
        }
        var a = _archetypeOf[e.Index]!;
        switch (c.Op)
        {
            case CommandOp.Destroy:
                foreach (int type in a.Components)
                    if (_tracked[type]) NoteRemoval(e, type, c.Cause);
                Unplace(e);
                // Поколение исчерпано — номер больше не выдаётся: иначе новое поколение совпало бы со старым хэндлом.
                if (_generations[e.Index] == int.MaxValue) RetiredIndices++;
                else
                {
                    _generations[e.Index]++;
                    _free.Push(e.Index);
                }
                EntityCount--;
                break;

            case CommandOp.Add:
            {
                var t = _types[c.Type];
                var payload = b.Payload.AsSpan(c.PayloadOffset, c.PayloadSize);
                Resolve(b, t, payload);
                if (a.Has[t.Id])
                {
                    if (Checks)
                        throw new InvalidOperationException($"Add<{t.Type.Name}>({e}): компонент уже есть — замена только командой Set");
                    if (!t.IsTag) payload.CopyTo(CellOf(e, t));
                    if (_tracked[t.Id]) NoteSet(e, t.Id, c.Cause);
                    break;
                }
                if (_tracked[t.Id]) Touch(e, t.Id).Cause = c.Cause;
                Move(e, WithType(a, t.Id));
                if (!t.IsTag) payload.CopyTo(CellOf(e, t));
                break;
            }

            case CommandOp.Remove:
            {
                var t = _types[c.Type];
                if (!a.Has[t.Id]) break;
                if (_tracked[t.Id]) NoteRemoval(e, t.Id, c.Cause);
                Move(e, WithoutType(a, t.Id));
                break;
            }

            case CommandOp.Set:
            {
                var t = _types[c.Type];
                if (!a.Has[t.Id])
                {
                    IgnoredCommands++;
                    break;
                }
                var payload = b.Payload.AsSpan(c.PayloadOffset, c.PayloadSize);
                Resolve(b, t, payload);
                if (!t.IsTag) payload.CopyTo(CellOf(e, t));
                if (_tracked[t.Id]) NoteSet(e, t.Id, c.Cause);
                break;
            }
        }
    }

    /// <summary>Разрешитель: отложенные значения черновиков буфера в полях <see cref="Entity"/> → живые сущности.</summary>
    private static void Resolve(CommandBuffer b, DataType t, Span<byte> payload)
    {
        if (!t.HasEntityFields) return;
        foreach (int o in t.EntityOffsets)
        {
            var field = payload.Slice(o, 8);
            var e = MemoryMarshal.Read<Entity>(field);
            if (!e.IsDeferred) continue;
            var live = b.Drafts[e.DraftIndex];
            MemoryMarshal.Write(field, in live);
        }
    }

    /// <summary>Отслеживание пары (сущность, тип) на этом барьере; состояние «до» снимается при первом касании.</summary>
    private ref Track Touch(Entity e, int type)
    {
        if (!_trackIndex.TryGetValue((e, type), out int i))
        {
            i = _tracks.Count;
            _trackIndex[(e, type)] = i;
            _tracks.Add(new Track
            {
                Entity = e, Type = type, Before = _archetypeOf[e.Index]!.Has[type], FirstRemoval = -1, LastRemoval = -1,
            });
        }
        return ref CollectionsMarshal.AsSpan(_tracks)[i];
    }

    private void NoteSet(Entity e, int type, CauseId cause)
    {
        ref var t = ref Touch(e, type);
        t.Set = true;
        t.Cause = cause;
    }

    private void NoteRemoval(Entity e, int type, CauseId cause)
    {
        ref var t = ref Touch(e, type);
        var dt = _types[type];
        int offset = _removedUsed;
        if (!dt.IsTag)
        {
            if (_removedUsed + dt.Size > _removedValues.Length) Array.Resize(ref _removedValues, Math.Max(_removedValues.Length * 2, _removedUsed + dt.Size));
            CellOf(e, dt).CopyTo(_removedValues.AsSpan(offset, dt.Size));
            _removedUsed += dt.Size;
        }
        if (t.FirstRemoval < 0)
        {
            t.FirstRemoval = offset;
            t.FirstRemovalCause = cause;
        }
        t.LastRemoval = offset;
        t.LastRemovalCause = cause;
        t.Removed = true;
    }

    /// <summary>Нормализация по паре (сущность, тип): состояние до барьера против состояния после — таблица раздела 5.</summary>
    private void FlushTracks()
    {
        foreach (var t in _tracks)
        {
            var dt = _types[t.Type];
            var w = dt.Writer!;
            bool after = IsAlive(t.Entity) && _archetypeOf[t.Entity.Index]!.Has[t.Type];
            ReadOnlySpan<byte> Removal(int offset) => dt.IsTag ? [] : _removedValues.AsSpan(offset, dt.Size);
            if (t.Before && after)
            {
                if (t.Removed)
                {
                    w.Removed(t.Entity, Removal(t.FirstRemoval), t.FirstRemovalCause, _barrier);
                    w.Added(t.Entity, t.Cause, _barrier);
                }
                else if (t.Set && dt.Reactive)
                    w.Changed(t.Entity, dt.IsTag ? [] : CellOf(t.Entity, dt), t.Cause, _barrier);
            }
            else if (t.Before) w.Removed(t.Entity, Removal(t.LastRemoval), t.LastRemovalCause, _barrier);
            else if (after) w.Added(t.Entity, t.Cause, _barrier);
        }
        _tracks.Clear();
        _trackIndex.Clear();
        _removedUsed = 0;
    }

    // ── проверки ───────────────────────────────────────────────────────────

    internal void NoteWritten(Archetype a, Chunk c, DataType t) => _written.Add((a, c, t));

    /// <summary>Отложенное значение черновика, записанное в компонент мимо команд, — ошибка системы.</summary>
    private void CheckWrittenDeferred(Registration r)
    {
        foreach (var (a, c, t) in _written)
        {
            var column = c.Columns[a.ColumnOf[t.Id]];
            for (int row = 0; row < c.Count; row++)
                foreach (int o in t.EntityOffsets)
                {
                    var e = MemoryMarshal.Read<Entity>(column.AsSpan(row * t.Size + o));
                    if (e.IsDeferred)
                    {
                        _written.Clear();
                        throw new InvalidOperationException(
                            $"{r.Name}: в {t.StableName} у {c.Entities[row]} записан {e} мимо команд — черновик разрешается только командами");
                    }
                }
        }
        _written.Clear();
    }
}
