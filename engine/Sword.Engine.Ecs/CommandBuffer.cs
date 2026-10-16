using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

internal enum CommandOp : byte { Create, Destroy, Add, Remove, Set, SetResource }

internal struct CommandRecord
{
    public CommandOp Op;
    /// <summary>Цель: живая сущность или отложенное значение черновика этого буфера; у Create — номер черновика.</summary>
    public Entity Target;
    /// <summary>Номер типа компонента; у SetResource — номер слота ресурса (значение — в слоте, номер в PayloadOffset).</summary>
    public int Type;
    public int PayloadOffset, PayloadSize;
}

internal struct EventRecord
{
    public int Type;
    public int PayloadOffset, PayloadSize;
}

/// <summary>
/// Буфер команд системы: изменения копятся и применяются на барьере — в порядке систем стадии, затем записи. Сущность
/// создаётся черновиком (<see cref="Create"/>, <see cref="EntityDraft.Ref"/>), живой становится на барьере. Принимает
/// только объявленное в <c>Setup</c> системы (проверки мира), чтобы объявления для планировщика не расходились с делом.
/// </summary>
public sealed class CommandBuffer
{
    private readonly World _world;
    internal readonly int Id;
    internal readonly Registration? Owner;
    internal CommandRecord[] Commands = new CommandRecord[16];
    internal int CommandCount;
    internal EventRecord[] Events = new EventRecord[8];
    internal int EventCount;
    internal byte[] Payload = new byte[256];
    internal int PayloadUsed;
    /// <summary>Черновик → живая сущность после проигрывания Create на барьере.</summary>
    internal Entity[] Drafts = new Entity[8];
    internal int DraftCount;
    /// <summary>Оборот буфера (растёт на барьерах с черновиками), входит в отложенное значение: 2³² оборотов при
    /// черновике каждый тик 120 Гц — больше года.</summary>
    private uint _turn;

    internal CommandBuffer(World world, int id, Registration? owner)
    {
        _world = world;
        Id = id;
        Owner = owner;
    }

    /// <summary>Записей в буфере (команды и события) — до барьера.</summary>
    public int Pending => CommandCount + EventCount;

    /// <summary>Новая сущность: черновик до барьера, на барьере — живая, с тем, что добавили командами буфера.</summary>
    public EntityDraft Create()
    {
        if (Owner is { CreatesEntities: false }) Undeclared("Creates()", "создать сущность");
        if (DraftCount == EntityDraft.MaxDrafts)
            throw new InvalidOperationException($"{Owner?.Name ?? "мир"}: больше {EntityDraft.MaxDrafts} черновиков до барьера");
        int index = DraftCount++;
        if (index == Drafts.Length) Array.Resize(ref Drafts, Drafts.Length * 2);
        Drafts[index] = Entity.Null;
        var draft = new EntityDraft(index, Id, _turn);
        Record(CommandOp.Create, draft.Ref, -1, default);
        return draft;
    }

    public void Destroy(Entity e)
    {
        if (Owner is { DestroysEntities: false }) Undeclared("Destroys()", $"удалить {e}");
        Record(CommandOp.Destroy, CheckTarget(e), -1, default);
    }

    public void Add<T>(EntityDraft draft, in T value) where T : unmanaged => Add(draft.Ref, value);

    public void Add<T>(Entity e, in T value) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Adds.Contains(t.Id)) Undeclared($"Adds<{typeof(T).Name}>()", "добавить компонент");
        RecordWithValue(CommandOp.Add, CheckTarget(e), t, value);
    }

    public void Remove<T>(Entity e) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Removes.Contains(t.Id)) Undeclared($"Removes<{typeof(T).Name}>()", "снять компонент");
        Record(CommandOp.Remove, CheckTarget(e), t.Id, default);
    }

    /// <summary>Заменить значение компонента, который у сущности уже есть. У реактивного — единственный способ.</summary>
    public void Set<T>(Entity e, in T value) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Sets.Contains(t.Id)) Undeclared($"Sets<{typeof(T).Name}>()", "заменить компонент");
        RecordWithValue(CommandOp.Set, CheckTarget(e), t, value);
    }

    public void Emit<E>(in E value) where E : unmanaged
    {
        var t = _world.EventTypeOf<E>();
        if (Owner is not null && !Owner.Emits.Contains(t.Id)) Undeclared($"Emits<{typeof(E).Name}>()", "записать событие");
        int offset = Store(value);
        CheckDeferred(t, offset);
        if (EventCount == Events.Length) Array.Resize(ref Events, Events.Length * 2);
        Events[EventCount++] = new EventRecord { Type = t.Id, PayloadOffset = offset, PayloadSize = t.Size };
    }

    public void SetResource<T>(T value)
    {
        var slot = _world.ResourceSlotOf<T>(out int index);
        if (Owner is not null && !Owner.SetsResources.Contains(slot.Type)) Undeclared($"SetsResource<{typeof(T).Name}>()", "заменить ресурс");
        Record(CommandOp.SetResource, Entity.Null, index, (slot.Stage(value), 0));
    }

    private void RecordWithValue<T>(CommandOp op, Entity target, DataType t, in T value) where T : unmanaged
    {
        int offset = t.IsTag ? PayloadUsed : Store(value);
        CheckDeferred(t, offset);
        Record(op, target, t.Id, (offset, t.Size));
    }

    private void Record(CommandOp op, Entity target, int type, (int Offset, int Size) payload)
    {
        if (CommandCount == Commands.Length) Array.Resize(ref Commands, Commands.Length * 2);
        Commands[CommandCount++] = new CommandRecord
        {
            Op = op, Target = target, Type = type, PayloadOffset = payload.Offset, PayloadSize = payload.Size,
        };
    }

    /// <summary>Ёмкость заранее (<see cref="SystemSetup.ReserveCommands"/>): пик за барьер не растит буфер посреди игры;
    /// больше запаса — рост удвоением.</summary>
    internal void Reserve(int commands, int events, int payloadBytes)
    {
        if (commands > Commands.Length) Array.Resize(ref Commands, commands);
        if (events > Events.Length) Array.Resize(ref Events, events);
        if (payloadBytes > Payload.Length) Array.Resize(ref Payload, payloadBytes);
    }

    /// <summary>Ёмкость буфера: записей команд, событий, байт значений (тесты).</summary>
    internal (int Commands, int Events, int PayloadBytes) Capacity => (Commands.Length, Events.Length, Payload.Length);

    private int Store<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (PayloadUsed + size > Payload.Length) Array.Resize(ref Payload, Math.Max(Payload.Length * 2, PayloadUsed + size));
        int offset = PayloadUsed;
        MemoryMarshal.Write(Payload.AsSpan(offset, size), in value);
        PayloadUsed += size;
        return offset;
    }

    /// <summary>Цель команды — живая сущность или черновик этого буфера и оборота: чужой черновик разрешился бы в чужую
    /// сущность. Проверка дешёвая и действует всегда.</summary>
    private Entity CheckTarget(Entity e)
    {
        if (e.IsNull) throw new ArgumentException("команда для Entity.Null");
        if (e.IsDeferred) CheckDraft(e, "команда");
        return e;
    }

    /// <summary>Отложенные значения в полях <see cref="Entity"/> — только черновики этого буфера и оборота; проверяется
    /// всегда, иначе разрешитель подставил бы чужую сущность.</summary>
    private void CheckDeferred(DataType t, int offset)
    {
        if (!t.HasEntityFields) return;
        foreach (int o in t.EntityOffsets)
        {
            var e = MemoryMarshal.Read<Entity>(Payload.AsSpan(offset + o));
            if (e.IsDeferred) CheckDraft(e, t.StableName);
        }
    }

    private void CheckDraft(Entity e, string where)
    {
        if (e.DraftBuffer == Id && e.DraftTurn == _turn && e.DraftIndex < DraftCount) return;
        throw new InvalidOperationException(e.DraftBuffer != Id
            ? $"{where}: {e} — черновик чужого буфера (этот — {Id}); ссылаться можно только на свои"
            : e.DraftTurn != _turn
                ? $"{where}: {e} — черновик прошлого барьера (сейчас оборот {_turn}): черновик живёт до ближайшего барьера, " +
                  "дальше — живая сущность"
                : $"{where}: {e} — такого черновика в буфере нет");
    }

    /// <summary>Запись без объявления: текст собирается только при нарушении — горячий путь команд строк не строит.</summary>
    private void Undeclared(string declaration, string what)
    {
        if (!_world.Checks) return;
        throw new InvalidOperationException($"{Owner!.Name}: {what} без объявления {declaration} в Setup — планировщик не знает об этой записи");
    }

    internal void Clear()
    {
        if (DraftCount > 0) _turn = unchecked(_turn + 1);
        CommandCount = EventCount = PayloadUsed = DraftCount = 0;
    }
}
