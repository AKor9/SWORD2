using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>
/// Время тика — ресурс мира: номер тика, фиксированный шаг, время симуляции. <see cref="Time"/> — как
/// <c>DuelArena.Time</c> SWORD: <c>+= dt</c> во float в начале тика, поэтому в тике k это сумма k+1 шагов (с округлением
/// float, на котором держатся таймеры SWORD).
/// </summary>
public readonly record struct SimTime(ulong Tick, float Dt, float Time);

/// <summary>Настройки симуляции (секция <c>sim</c>).</summary>
public sealed record SimSettings
{
    /// <summary>Тиков в секунду; SWORD по умолчанию — 120 (<c>Fighter.FastTick</c>).</summary>
    public int TickRate { get; init; } = 120;

    /// <summary>Потолок тиков за кадр клиента (<c>Program.MaxTicksPerFrame</c> SWORD): дальше симуляция отстаёт от часов.</summary>
    public int MaxTicksPerFrame { get; init; } = 8;

    /// <summary>Главное зерно счётчикового ГСЧ (<see cref="SimRandom"/>); пишется в реплей.</summary>
    public ulong Seed { get; init; } = 1;
}

/// <summary>Настройки модуля (секция групп <c>modules.&lt;id&gt;</c>).</summary>
public sealed record ModuleSettings
{
    /// <summary>Выключенный модуль не регистрирует ничего (<c>SWORD_MODULES_&lt;ID&gt;_ENABLED</c>, <c>--set modules.&lt;id&gt;.enabled=false</c>).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Модуль симуляции (раздел 7 плана): сборка симуляции, подключённая к exe статически. Неактивный модуль
/// (<c>modules.&lt;id&gt;.enabled=false</c>) не регистрирует ничего.
/// </summary>
public interface IModule
{
    /// <summary>
    /// Ключ конфига (<c>modules.&lt;id&gt;</c>, <c>SWORD_MODULES_&lt;ID&gt;_ENABLED</c>), логи, реестр, токен регистраций —
    /// поэтому только <see cref="IdRule"/>: без точки (она делит путь конфига), регистра и не-латиницы.
    /// </summary>
    string Id { get; }

    const string IdRule = "строчная латиница, цифры и '_', первой — буква";

    static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] is < 'a' or > 'z') return false;
        foreach (char c in id)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

    void Configure(SimulationBuilder b);
}

/// <summary>
/// Симуляция без окна (раздел 7): мир ECS со стадиями, фиксированный тик, команды тика, модули, конфиг, экспорт тика.
/// Всё, что меняет симуляцию на ходу, — команда с номером тика (<see cref="Enqueue{T}(in T)"/>): она становится
/// событием в служебной стадии до стадий игры своего тика; мир снаружи — только для чтения (<see cref="World"/>). Собирается <see cref="Build"/> из списка стадий и модулей — у App и Headless
/// один и тот же вызов (композиция в <c>Sword.Game.Bootstrap</c>).
/// </summary>
public sealed class Simulation
{
    private readonly TickCommands _commands;
    private readonly World _world;
    private readonly IStateContributor[] _state;
    private float _time;

    private Simulation(World world, SimSettings settings, TickCommands commands, TickExport export,
        IReadOnlyList<string> active, IReadOnlyList<string> inactive, ConfigSet config, IStateContributor[] state)
    {
        _state = state;
        StateParts = [.. state.Select(c => c.Id)];
        _world = world;
        Settings = settings;
        Dt = 1f / settings.TickRate;
        _commands = commands;
        Export = export;
        ActiveModules = active;
        InactiveModules = inactive;
        Config = config;
    }

    /// <summary>
    /// Мир — только для чтения, и у владельца симуляции тоже: всё, что меняет симуляцию на ходу, — команды тика
    /// (<see cref="Enqueue{T}(in T)"/>); отдельных «тихих» путей нет (раздел 7 плана).
    /// </summary>
    public WorldReader World => _world.Reader;

    public SimSettings Settings { get; }
    public ConfigSet Config { get; }

    /// <summary>Фиксированный шаг, с.</summary>
    public float Dt { get; }

    /// <summary>Номер следующего тика (сколько тиков уже сделано).</summary>
    public ulong Tick { get; private set; }

    public IReadOnlyList<string> ActiveModules { get; }
    public IReadOnlyList<string> InactiveModules { get; }

    /// <summary>Записи тиков для клиента: срабатывания, объявленные <see cref="SimulationBuilder.Export{TTrigger}"/>.</summary>
    public TickExport Export { get; }

    /// <summary>Секции конфига движка — симуляции и клиента: Id модуля (имя его секции) с ними совпадать не может.</summary>
    public static readonly IReadOnlyList<string> EngineSections = ["sim", "modules", .. ConfigSet.ClientSections];

    /// <summary>
    /// Служебные стадии Runtime вокруг стадий игры: команды тика — до первой (их события видят все стадии игры в том же
    /// тике), экспорт — после барьера последней (в экспорт тика попадают записи всех стадий этого тика).
    /// </summary>
    public const string CommandStage = "runtime.commands", ExportStage = "runtime.export";

    /// <summary>
    /// Собрать симуляцию: секция <c>sim</c>, ядро, модули (активные — по <c>modules.&lt;id&gt;.enabled</c>, по умолчанию
    /// все), мир со стадиями игры <paramref name="stages"/> между служебными <see cref="CommandStage"/> и
    /// <see cref="ExportStage"/>. Неизвестные секции конфига здесь не проверяются: секции читает и клиент, поэтому
    /// проверку делает exe в конце композиции (<see cref="ConfigSet.Complete"/>).
    /// </summary>
    /// <param name="core">
    /// Ядро (принцип 4 плана): то, без чего игры нет, — одна реализация, не модуль и не выключается; его регистрации
    /// без токена модуля. Null — без ядра (тесты движка).
    /// </param>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, Action<SimulationBuilder>? core,
        IEnumerable<IModule> modules) => Build(config, stages, core, modules, new WorldOptions());

    /// <summary>Симуляция без ядра — модули движка и тесты.</summary>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, IEnumerable<IModule> modules) =>
        Build(config, stages, null, modules, new WorldOptions());

    /// <inheritdoc cref="Build(ConfigSet, IReadOnlyList{string}, Action{SimulationBuilder}, IEnumerable{IModule})"/>
    /// <param name="worldOptions">Настройки мира (размер чанка, проверки) — для тестов и замеров.</param>
    public static Simulation Build(ConfigSet config, IReadOnlyList<string> stages, Action<SimulationBuilder>? core,
        IEnumerable<IModule> modules, WorldOptions worldOptions)
    {
        if (stages.Count < 1) throw new ArgumentException("нужна хотя бы одна стадия игры");
        if (stages.Contains(CommandStage) || stages.Contains(ExportStage))
            throw new ArgumentException($"стадии {CommandStage} и {ExportStage} — служебные, их добавляет Runtime");
        var settings = config.Section<SimSettings>("sim");
        if (settings.TickRate is < 1 or > 10_000) throw new InvalidDataException($"sim.tickRate = {settings.TickRate}: 1…10000");
        if (settings.MaxTicksPerFrame < 1) throw new InvalidDataException($"sim.maxTicksPerFrame = {settings.MaxTicksPerFrame}: ≥ 1");
        var list = modules.ToList();
        var ids = new List<string>();
        foreach (var m in list)
        {
            if (!IModule.IsValidId(m.Id))
                throw new InvalidOperationException($"модуль {m.GetType().Name}: Id \"{m.Id}\" — нужно {IModule.IdRule}");
            if (EngineSections.Contains(m.Id))
                throw new InvalidOperationException($"модуль {m.Id}: Id совпадает с секцией конфига движка ({string.Join(", ", EngineSections)})");
            if (ids.Contains(m.Id, StringComparer.Ordinal)) throw new InvalidOperationException($"модуль {m.Id} дважды");
            ids.Add(m.Id);
        }
        var moduleSettings = config.Groups<ModuleSettings>("modules", ids);

        var world = new WorldBuilder { Options = worldOptions }.Stages([CommandStage, .. stages, ExportStage]);
        world.Resource(new SimTime(0, 1f / settings.TickRate, 0));
        world.Resource(new SimRandom(settings.Seed));
        var commands = new TickCommands();
        var export = new TickExport();
        var b = new SimulationBuilder(config, world, commands, export, stages);

        b.Module = null;
        core?.Invoke(b);

        var active = new List<string>();
        var inactive = new List<string>();
        foreach (var m in list)
        {
            if (!moduleSettings[m.Id].Enabled)
            {
                inactive.Add(m.Id);
                config.Skip(m.Id);   // секция настроек выключенного модуля — не «неизвестная»
                continue;
            }
            b.Module = m.Id;
            m.Configure(b);
            active.Add(m.Id);
        }
        b.Module = null;

        world.System(CommandStage, commands);
        world.System(ExportStage, export);
        var built = world.Build();
        // Мир собран: поздняя регистрация (например, из OnWorldBuilt) молча потерялась бы — построитель закрыт.
        b.Sealed = true;
        foreach (var init in b.WorldInits) init(built);
        // Участники хеша — по Id (раздел 7 плана): порядок не зависит от порядка регистрации.
        IStateContributor[] state = [.. b.Contributors.Append(new EcsStateContributor(built.Reader)).OrderBy(c => c.Id, StringComparer.Ordinal)];
        Log.Info("runtime", $"симуляция: {settings.TickRate} тиков/с, модули: {string.Join(", ", active)}" +
            (inactive.Count > 0 ? $"; выключены: {string.Join(", ", inactive)}" : ""));
        return new Simulation(built, settings, commands, export, active, inactive, config, state);
    }

    /// <summary>
    /// Принимает ли симуляция команду <typeparamref name="T"/>: её регистрирует модуль, и без модуля команды нет.
    /// </summary>
    public bool Accepts<T>() where T : unmanaged => _commands.Accepts<T>();

    /// <summary>Команда на следующий тик (<see cref="Tick"/>).</summary>
    public void Enqueue<T>(in T command) where T : unmanaged => _commands.Enqueue(Tick, command);

    /// <summary>Команда на тик <paramref name="tick"/> (не раньше следующего).</summary>
    public void Enqueue<T>(ulong tick, in T command) where T : unmanaged
    {
        if (tick < Tick) throw new ArgumentOutOfRangeException(nameof(tick), tick, $"тик {tick} уже прошёл (следующий — {Tick})");
        _commands.Enqueue(tick, command);
    }

    /// <summary>Типы команд тика (их регистрируют ядро и модули) — для инструментов и агента.</summary>
    public IReadOnlyList<Type> CommandTypes => _commands.Types;

    /// <summary>
    /// Команда тика байтами (<see cref="CommandTypes"/>; размер — <c>sizeof</c> типа) на тик <paramref name="tick"/>
    /// (не раньше следующего): путь инструментов и агента, которые собирают команду по полям, не зная типа. Как и
    /// <see cref="Enqueue{T}(ulong, in T)"/> — только с потока владельца симуляции, между тиками. Команды этого пути в тике
    /// выдаются после поставленных <see cref="Enqueue{T}(ulong, in T)"/> того же типа: где действует последняя команда
    /// тика своего типа, побеждает инструмент (агент), а не ввод клиента того же тика. Между типами порядок — порядок
    /// регистрации типов. <paramref name="repeat"/> — та же команда на столько тиков подряд, одной постановкой (очередь
    /// не сдвигается на каждый тик диапазона).
    /// </summary>
    public void EnqueueRaw(Type type, ulong tick, ReadOnlySpan<byte> command, int repeat = 1)
    {
        if (tick < Tick) throw new ArgumentOutOfRangeException(nameof(tick), tick, $"тик {tick} уже прошёл (следующий — {Tick})");
        if (repeat < 1 || ulong.MaxValue - tick < (ulong)(repeat - 1))
            throw new ArgumentOutOfRangeException(nameof(repeat), repeat, "повторов — от 1, последний тик — в пределах ulong");
        _commands.EnqueueRaw(type, tick, command, repeat);
    }

    /// <summary>Участники хеша состояния по Id (<c>ecs</c> и зарегистрированные <see cref="SimulationBuilder.StateContributor"/>).</summary>
    public IReadOnlyList<string> StateParts { get; }

    /// <summary>
    /// Хеш состояния между тиками (раздел 7 плана): в <paramref name="parts"/> — хеш каждого участника в порядке
    /// <see cref="StateParts"/>, итог — хеш хешей участников. Память не выделяет.
    /// </summary>
    public ulong HashState(Span<ulong> parts)
    {
        if (parts.Length < _state.Length) throw new ArgumentException($"участников {_state.Length}, места {parts.Length}", nameof(parts));
        var total = new StateHasher(0);
        for (int i = 0; i < _state.Length; i++)
        {
            var h = new StateHasher(StateHasher.Of(_state[i].Id));
            _state[i].AppendHash(ref h);
            parts[i] = h.Value;
            total.Add(parts[i]);
        }
        return total.Value;
    }

    /// <summary>Хеш состояния с участниками по Id — для инструментов и тестов (выделяет память).</summary>
    public StateHash HashState()
    {
        var parts = new ulong[_state.Length];
        ulong total = HashState(parts);
        return new StateHash(Tick, total, StateParts.Select((id, i) => (id, parts[i])).ToDictionary(p => p.id, p => p.Item2));
    }

    /// <summary>Описание состояния участника <paramref name="part"/> — то, что он хеширует (JSON).</summary>
    /// <exception cref="ArgumentException">Нет такого участника.</exception>
    public string DescribeState(string part, in DescribeQuery query)
    {
        var contributor = _state.FirstOrDefault(c => c.Id == part)
            ?? throw new ArgumentException($"нет участника хеша «{part}»: {string.Join(", ", StateParts)}", nameof(part));
        var w = new StateWriter();
        contributor.Describe(w, query);
        return w.ToJson();
    }

    /// <summary>Один тик: время (<c>DuelArena.Tick</c>: <c>Time += dt</c> первым делом), стадии с барьерами, экспорт.</summary>
    public void Step()
    {
        _time += Dt;
        _world.SetResourceImmediate(new SimTime(Tick, Dt, _time));
        _world.RunTick(Tick);
        Tick++;
    }
}

/// <summary>
/// Построитель симуляции для модулей: компоненты, события, ресурсы, команды тика, системы по стадиям (с токеном модуля),
/// настройки из конфига, экспорт срабатываний клиенту, начальное наполнение мира.
/// </summary>
public sealed class SimulationBuilder
{
    private readonly WorldBuilder _world;
    private readonly TickCommands _commands;
    private readonly TickExport _export;
    internal readonly List<Action<World>> WorldInits = [];

    /// <summary>Мир собран — регистрировать поздно.</summary>
    internal bool Sealed;

    internal readonly List<IStateContributor> Contributors = [];

    internal SimulationBuilder(ConfigSet config, WorldBuilder world, TickCommands commands, TickExport export, IReadOnlyList<string> stages)
    {
        Config = config;
        _world = world;
        _commands = commands;
        _export = export;
        Stages = stages;
    }

    public ConfigSet Config { get; }
    public IReadOnlyList<string> Stages { get; }

    /// <summary>Модуль, который сейчас настраивается (токен его регистраций); null — ядро.</summary>
    public string? Module { get; internal set; }

    private SimulationBuilder Open()
    {
        if (Sealed) throw new InvalidOperationException("мир симуляции уже собран: регистрировать можно только в Configure");
        return this;
    }

    /// <summary>
    /// Настройки модуля — секция с его <c>Id</c> (<c>"cutting": { … }</c>): у выключенного модуля она пропускается
    /// (<see cref="ConfigSet.Skip"/>), поэтому модуль читает только её.
    /// </summary>
    public T Settings<T>() where T : new() =>
        Open().Module is { } id ? Config.Section<T>(id) : throw new InvalidOperationException("Settings<T>() — только в Configure модуля");

    /// <summary>
    /// Участник хеша состояния (раздел 7 плана): подсистема, чьё состояние лежит вне ECS. Id — как у модуля
    /// (<see cref="IModule.IsValidId"/>), не <c>ecs</c> (его заводит Runtime) и не повторяется.
    /// </summary>
    public SimulationBuilder StateContributor(IStateContributor contributor)
    {
        Open();
        ArgumentNullException.ThrowIfNull(contributor);
        if (!IModule.IsValidId(contributor.Id) || contributor.Id == "ecs")
            throw new InvalidOperationException($"участник хеша «{contributor.Id}»: нужно {IModule.IdRule}, не «ecs»");
        if (Contributors.Any(c => c.Id == contributor.Id)) throw new InvalidOperationException($"участник хеша «{contributor.Id}» дважды");
        Contributors.Add(contributor);
        return this;
    }

    public SimulationBuilder Component<T>() where T : unmanaged
    {
        Open()._world.Component<T>();
        return this;
    }

    public SimulationBuilder Event<E>() where E : unmanaged
    {
        Open()._world.Event<E>();
        return this;
    }

    public SimulationBuilder Resource<T>(T initial)
    {
        Open()._world.Resource(initial);
        return this;
    }

    /// <summary>Вид только для чтения для наблюдателей симуляции (клиент, агент) — <see cref="WorldBuilder.ReadView{TView}"/>.</summary>
    public SimulationBuilder ReadView<TView>(TView view) where TView : class, IReadView
    {
        Open()._world.ReadView(view);
        return this;
    }

    /// <summary>Тип команды тика: команда, поставленная на тик, — событие <see cref="Event{E}"/> первой стадии этого тика.</summary>
    public SimulationBuilder Command<T>() where T : unmanaged
    {
        Open()._world.Event<T>();
        _commands.Register<T>();
        return this;
    }

    /// <summary>Система в стадию игры (служебные стадии Runtime модулям недоступны).</summary>
    /// <remarks>Место регистрации (файл и строка вызова) попадает в описание системы — карта архитектуры агента.</remarks>
    public SimulationBuilder System(string stage, ISystem system,
        [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!Open().Stages.Contains(stage))
            throw new ArgumentException($"{system.GetType().Name}: нет стадии игры {stage}; есть: {string.Join(", ", Stages)}");
        _world.System(stage, system, Module, file.Length > 0 ? $"{file}:{line}" : null);
        return this;
    }

    /// <summary>Срабатывание уходит клиенту: экспорт тика копирует записи с номером тика (<see cref="Simulation.Export"/>).</summary>
    public SimulationBuilder Export<TTrigger>() where TTrigger : struct
    {
        Open()._export.Register<TTrigger>();
        return this;
    }

    /// <summary>Начальное наполнение мира после сборки (немедленные изменения: уровень, игрок).</summary>
    public SimulationBuilder OnWorldBuilt(Action<World> init)
    {
        Open().WorldInits.Add(init);
        return this;
    }
}
