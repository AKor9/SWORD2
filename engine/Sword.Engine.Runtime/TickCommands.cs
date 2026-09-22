using System.Runtime.InteropServices;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

/// <summary>Получатель выданных команд тика — запись реплея.</summary>
internal interface ICommandSink
{
    void Command(ulong tick, int type, ReadOnlySpan<byte> command);
}

/// <summary>Источник команд тика помимо очередей — воспроизведение реплея.</summary>
internal interface IReplaySource
{
    void EmitTick(ulong tick, TickCommands commands, CommandBuffer buffer);
}

/// <summary>
/// Команды тика: очередь по номерам тиков; в служебной стадии <see cref="Simulation.CommandStage"/> — до первой стадии
/// игры — команды этого тика становятся событиями (<see cref="Event{E}"/>). После её барьера их видят системы всех
/// стадий игры, в том же тике. Отдельных «тихих» путей изменить симуляцию мимо команд нет (раздел 7 плана).
/// </summary>
internal sealed class TickCommands : IContinuousSystem
{
    public static readonly SystemLabel Label = new("runtime.commands");

    private interface IQueue
    {
        Type Type { get; }
        void Declare(SystemSetup s);
        void EmitDue(ulong tick, CommandBuffer commands, ICommandSink? sink, int type);
        void EmitRaw(ulong tick, ReadOnlySpan<byte> command, CommandBuffer commands, ICommandSink? sink, int type);
        void AddRaw(ulong tick, ReadOnlySpan<byte> command, int repeat);
    }

    /// <summary>Запись реплея (<see cref="ReplayRecorder"/>): каждая выданная команда тика — номер типа и байты.</summary>
    internal ICommandSink? Sink;

    /// <summary>Воспроизведение реплея (<see cref="ReplayPlayer"/>): его команды тика выдаются первыми.</summary>
    internal IReplaySource? Source;

    /// <summary>
    /// Ожидающие команды одного источника — по возрастанию тика (равные — в порядке постановки), голова — курсор: тик
    /// берёт только свои записи с головы, а не просматривает и не сдвигает весь хвост (агент ставит до 100 000 тиков
    /// подряд). Постановка в конец (клиент — на ближайший тик) — без сдвигов; в середину — одно слияние на весь диапазон.
    /// </summary>
    private sealed class Pending<T> where T : unmanaged
    {
        private readonly List<(ulong Tick, T Command)> _items = [];
        private int _head;

        public int Count => _items.Count - _head;

        /// <summary>Команда на тики <paramref name="first"/>…<paramref name="first"/> + <paramref name="repeat"/> − 1.</summary>
        public void Add(ulong first, in T command, int repeat)
        {
            if (_items.Count == _head || _items[^1].Tick <= first)
            {
                for (int k = 0; k < repeat; k++) _items.Add((first + (ulong)k, command));
                return;
            }
            var merged = new List<(ulong Tick, T Command)>(Count + repeat);
            int i = _head, n = 0;
            while (i < _items.Count || n < repeat)
                if (n == repeat || (i < _items.Count && _items[i].Tick <= first + (ulong)n)) merged.Add(_items[i++]);
                else merged.Add((first + (ulong)n++, command));
            _items.Clear();
            _items.AddRange(merged);
            _head = 0;
        }

        /// <summary>Выдать команды тика <paramref name="tick"/>; прошедшие (раньше него) — отбросить.</summary>
        public void EmitDue(ulong tick, CommandBuffer commands, ICommandSink? sink, int type)
        {
            while (_head < _items.Count && _items[_head].Tick <= tick)
            {
                var (t, c) = _items[_head++];
                if (t != tick) continue;
                commands.Emit(c);
                sink?.Command(tick, type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in c)));
            }
            // Сжатие — когда отработанная голова не меньше хвоста: в среднем O(1) на команду.
            if (_head > 0 && _head * 2 >= _items.Count)
            {
                _items.RemoveRange(0, _head);
                _head = 0;
            }
        }
    }

    /// <summary>
    /// Ожидающих команд инструментов одного типа — не больше: память и слияние при постановке в середину растут с
    /// очередью, а столько тиков вперёд команды всё равно не ставятся (≈ 2,3 ч при 120 Гц).
    /// </summary>
    public const int MaxToolCommands = 1_000_000;

    private sealed class Queue<T> : IQueue where T : unmanaged
    {
        private readonly Pending<T> _pending = new();
        // Команды инструментов (агента) — после команд клиента того же тика: где действует последняя команда тика,
        // побеждает поставленная инструментом (человек в живой игре и агент не перебивают друг друга случайно).
        private readonly Pending<T> _tools = new();

        public Type Type => typeof(T);

        public void Add(ulong tick, in T command) => _pending.Add(tick, command, 1);

        public void AddRaw(ulong tick, ReadOnlySpan<byte> command, int repeat)
        {
            if (command.Length != System.Runtime.CompilerServices.Unsafe.SizeOf<T>())
                throw new ArgumentException($"{typeof(T).Name}: {command.Length} Б при размере {System.Runtime.CompilerServices.Unsafe.SizeOf<T>()} Б");
            if (repeat < 1) throw new ArgumentOutOfRangeException(nameof(repeat), repeat, "repeat — не меньше 1");
            if ((long)_tools.Count + repeat > MaxToolCommands)
                throw new InvalidOperationException($"{typeof(T).Name}: в очереди инструментов {_tools.Count} команд, с новыми " +
                    $"{repeat} — больше предела {MaxToolCommands}; дождитесь, пока очередь отыграет");
            _tools.Add(tick, System.Runtime.InteropServices.MemoryMarshal.Read<T>(command), repeat);
        }

        public void Declare(SystemSetup s) => s.Emits<T>();

        public void EmitDue(ulong tick, CommandBuffer commands, ICommandSink? sink, int type)
        {
            _pending.EmitDue(tick, commands, sink, type);
            _tools.EmitDue(tick, commands, sink, type);
        }

        public void EmitRaw(ulong tick, ReadOnlySpan<byte> command, CommandBuffer commands, ICommandSink? sink, int type)
        {
            var c = MemoryMarshal.Read<T>(command);
            commands.Emit(c);
            sink?.Command(tick, type, command);
        }
    }

    private readonly Dictionary<Type, IQueue> _queues = [];
    private readonly List<IQueue> _order = [];

    public void Register<T>() where T : unmanaged
    {
        if (_queues.ContainsKey(typeof(T))) return;
        var q = new Queue<T>();
        _queues[typeof(T)] = q;
        _order.Add(q);
    }

    public bool Accepts<T>() where T : unmanaged => _queues.ContainsKey(typeof(T));

    /// <summary>Типы команд тика в порядке регистрации.</summary>
    public IReadOnlyList<Type> Types => [.. _order.Select(q => q.Type)];

    public void EnqueueRaw(Type type, ulong tick, ReadOnlySpan<byte> command, int repeat)
    {
        if (!_queues.TryGetValue(type, out var q))
            throw new InvalidOperationException($"{type.Name} — не команда тика (SimulationBuilder.Command<T>)");
        q.AddRaw(tick, command, repeat);
    }

    public void Enqueue<T>(ulong tick, in T command) where T : unmanaged
    {
        if (!_queues.TryGetValue(typeof(T), out var q))
            throw new InvalidOperationException($"{typeof(T).Name} — не команда тика (SimulationBuilder.Command<T>)");
        ((Queue<T>)q).Add(tick, command);
    }

    public void Setup(SystemSetup s)
    {
        s.Label(Label);
        foreach (var q in _order) q.Declare(s);
    }

    /// <summary>Выдать команду реплея: <paramref name="type"/> — номер в <see cref="Types"/>, размер байтов проверен при привязке.</summary>
    internal void EmitRaw(ulong tick, int type, ReadOnlySpan<byte> command, CommandBuffer commands) =>
        _order[type].EmitRaw(tick, command, commands, Sink, type);

    public void Run(in SystemContext ctx)
    {
        // Реплей — первым: команды клиента и инструментов того же тика идут после и, где действует последняя команда
        // тика своего типа, перебивают его (ветвление от реплея).
        Source?.EmitTick(ctx.Tick, this, ctx.Commands);
        for (int i = 0; i < _order.Count; i++) _order[i].EmitDue(ctx.Tick, ctx.Commands, Sink, i);
    }
}

/// <summary>
/// Экспорт тика (раздел 5 плана): в служебной стадии <see cref="Simulation.ExportStage"/> — после барьера последней
/// стадии игры, поэтому в тот же тик попадают и записи самой последней стадии — записи объявленных срабатываний
/// копируются с номером тика в очередь кадра. Кадр начинается с <see cref="BeginFrame"/>: кадр из трёх тиков получает
/// записи всех трёх, кадр без тиков — ничего. Получатель берёт копии и на освобождение журналов симуляции не влияет.
/// История наблюдений для агента (<c>ObservationHistory</c>) — второй получатель тех же копий, с этапом 3.
/// </summary>
public sealed class TickExport : IContinuousSystem
{
    public static readonly SystemLabel Label = new("runtime.export");

    private interface IChannel
    {
        void Declare(SystemSetup s);
        void Copy(in SystemContext ctx);
        void Clear();
    }

    private sealed class Channel<T> : IChannel where T : struct
    {
        public readonly List<(ulong Tick, T Record)> Records = [];

        // Экспорт — отдельная стадия после всех стадий игры: все производители раньше него, задержки на тик нет.
        public void Declare(SystemSetup s) => s.ReadsJournal<T>();

        public void Copy(in SystemContext ctx)
        {
            foreach (var r in ctx.Journal<T>()) Records.Add((ctx.Tick, r));
        }

        public void Clear() => Records.Clear();
    }

    private readonly Dictionary<Type, IChannel> _channels = [];
    private readonly List<IChannel> _order = [];

    internal void Register<T>() where T : struct
    {
        if (_channels.ContainsKey(typeof(T))) return;
        var c = new Channel<T>();
        _channels[typeof(T)] = c;
        _order.Add(c);
    }

    /// <summary>Новый кадр: очередь прошлого кадра очищается (её записи обработаны или не нужны).</summary>
    public void BeginFrame()
    {
        foreach (var c in _order) c.Clear();
    }

    /// <summary>Экспортируется ли срабатывание <typeparamref name="T"/> (без модуля, который его объявил, — нет).</summary>
    public bool Exports<T>() where T : struct => _channels.ContainsKey(typeof(T));

    /// <summary>Записи тиков этого кадра — с номерами тиков, по порядку; до следующего <see cref="BeginFrame"/>.</summary>
    public ReadOnlySpan<(ulong Tick, T Record)> Frame<T>() where T : struct =>
        _channels.TryGetValue(typeof(T), out var c)
            ? CollectionsMarshal.AsSpan(((Channel<T>)c).Records)
            : throw new InvalidOperationException($"{typeof(T).Name} не экспортируется (SimulationBuilder.Export<T>)");

    public void Setup(SystemSetup s)
    {
        s.Label(Label);
        foreach (var c in _order) c.Declare(s);
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var c in _order) c.Copy(ctx);
    }
}
