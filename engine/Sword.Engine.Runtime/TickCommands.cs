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
/// Команды тика: очередь по номерам тиков; в стадии <see cref="Simulation.CommandStage"/> команды тика становятся
/// событиями (<see cref="Event{E}"/>), видимыми всем стадиям игры того же тика. Других путей изменить симуляцию нет.
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
    }

    /// <summary>Запись реплея (<see cref="ReplayRecorder"/>): каждая выданная команда тика — номер типа и байты.</summary>
    internal ICommandSink? Sink;

    /// <summary>Воспроизведение реплея (<see cref="ReplayPlayer"/>): его команды тика выдаются первыми.</summary>
    internal IReplaySource? Source;

    /// <summary>Ожидающие команды по возрастанию тика (равные — в порядке постановки), голова — курсор: тик берёт свои с
    /// головы без сдвига хвоста; постановка в конец — без сдвигов.</summary>
    private sealed class Pending<T> where T : unmanaged
    {
        private readonly List<(ulong Tick, T Command)> _items = [];
        private int _head;

        public void Add(ulong tick, in T command)
        {
            int i = _items.Count;
            while (i > _head && _items[i - 1].Tick > tick) i--;
            _items.Insert(i, (tick, command));
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

    private sealed class Queue<T> : IQueue where T : unmanaged
    {
        private readonly Pending<T> _pending = new();

        public Type Type => typeof(T);

        public void Add(ulong tick, in T command) => _pending.Add(tick, command);

        public void Declare(SystemSetup s) => s.Emits<T>();

        public void EmitDue(ulong tick, CommandBuffer commands, ICommandSink? sink, int type) =>
            _pending.EmitDue(tick, commands, sink, type);

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
        // Реплей — первым: команды клиента того же тика идут после и, где действует последняя команда тика своего
        // типа, перебивают его (ветвление от реплея).
        Source?.EmitTick(ctx.Tick, this, ctx.Commands);
        for (int i = 0; i < _order.Count; i++) _order[i].EmitDue(ctx.Tick, ctx.Commands, Sink, i);
    }
}

/// <summary>
/// Экспорт тика: в стадии <see cref="Simulation.ExportStage"/> (после барьера последней стадии игры) записи объявленных
/// срабатываний копируются с номером тика в очередь кадра. Кадр (<see cref="BeginFrame"/>) получает записи всех своих
/// тиков; получатель берёт копии и на журналы симуляции не влияет.
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
