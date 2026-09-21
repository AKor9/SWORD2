using System.Runtime.InteropServices;
using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime;

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
        void Declare(SystemSetup s);
        void EmitDue(ulong tick, CommandBuffer commands);
    }

    private sealed class Queue<T> : IQueue where T : unmanaged
    {
        private readonly List<(ulong Tick, T Command)> _pending = [];

        public void Add(ulong tick, in T command) => _pending.Add((tick, command));

        public void Declare(SystemSetup s) => s.Emits<T>();

        public void EmitDue(ulong tick, CommandBuffer commands)
        {
            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var (t, c) = _pending[i];
                if (t == tick) commands.Emit(c);
                else if (t > tick) _pending[kept++] = _pending[i];
            }
            _pending.RemoveRange(kept, _pending.Count - kept);
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

    public void Run(in SystemContext ctx)
    {
        foreach (var q in _order) q.EmitDue(ctx.Tick, ctx.Commands);
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
