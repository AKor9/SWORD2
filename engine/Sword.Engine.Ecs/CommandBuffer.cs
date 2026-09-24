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
    public CauseId Cause;
}

internal struct EventRecord
{
    public int Type;
    public int PayloadOffset, PayloadSize;
    public CauseId Cause;
}

/// <summary>
/// Буфер команд системы: структурные изменения, замены реактивных компонентов, события и ресурсы копятся здесь и
/// применяются на ближайшем барьере — в порядке систем стадии, затем в порядке записи. Сущность создаётся
/// черновиком (<see cref="Create"/>); на него ссылаются команды и события этого же буфера (<see cref="EntityDraft.Ref"/>),
/// живой она становится на барьере.
///
/// Буфер системы принимает только объявленное в её <c>Setup</c> (<c>Adds/Removes/Sets/Emits/SetsResource/Creates/
/// Destroys</c>): при включённых проверках мира необъявленное — исключение. Так объявления, по которым планировщик
/// проверяет порядок стадий, не расходятся с тем, что система делает.
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
    /// <summary>
    /// Оборот буфера: растёт на каждом барьере, где у буфера были черновики; входит в отложенное значение (32 бита —
    /// черновик прошлого барьера отличим, пока буфер не сделал 2³² оборотов: при черновике каждый тик 120 Гц — год с лишним).
    /// </summary>
    private uint _turn;

    internal CommandBuffer(World world, int id, Registration? owner)
    {
        _world = world;
        Id = id;
        Owner = owner;
    }

    /// <summary>
    /// Причина следующих записей (<c>state.why</c>, 6д). В диагностическом режиме (<see cref="World.Causality"/>) — родитель:
    /// каждая запись получает свою причину (номер записи журнала причинности), а эта становится её родителем; реактивная
    /// система ставит её по срабатыванию, которое обрабатывает (<see cref="SystemContext.Cause"/>). Вне режима запись несёт
    /// её как есть (обычно пусто). Перед каждым запуском системы — пусто.
    /// </summary>
    public CauseId Cause { get; set; }

    /// <summary>Причина последней записи этого буфера (диагностический режим; вне режима — <see cref="Cause"/> записи).</summary>
    public CauseId LastCause { get; private set; }

    /// <summary>
    /// Запись-шаг причинности без команды (диагностический режим): <paramref name="parent"/> пусто — решение системы (корень:
    /// «ИИ бьёт по таймеру»), иначе — шаг непрерывного производителя с явным родителем («начало удара» от команды игрока).
    /// <paramref name="what"/> — пояснение (строка-константа), <paramref name="arg"/> — число шага. Вне режима — пусто, ничего не стоит.
    /// </summary>
    public CauseId Note(string what, Entity entity, CauseId parent, long arg = 0,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (_world.Causality is not { } log) return CauseId.None;
        var id = log.Append(parent.Value == 0 ? CauseKind.Decision : CauseKind.Step, parent, _world.CurrentTick, Owner?.StageIndex ?? -1,
            Owner?.Id ?? 0, entity, -1, file, line, what, arg);
        return LastCause = id;
    }

    /// <summary>Записей в буфере (команды и события) — до барьера.</summary>
    public int Pending => CommandCount + EventCount;

    /// <summary>Новая сущность: черновик до барьера, на барьере — живая, с тем, что добавили командами буфера.</summary>
    public EntityDraft Create([CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (Owner is { CreatesEntities: false }) Undeclared("Creates()", "создать сущность");
        if (DraftCount == EntityDraft.MaxDrafts)
            throw new InvalidOperationException($"{Owner?.Name ?? "мир"}: больше {EntityDraft.MaxDrafts} черновиков до барьера");
        int index = DraftCount++;
        if (index == Drafts.Length) Array.Resize(ref Drafts, Drafts.Length * 2);
        Drafts[index] = Entity.Null;
        var draft = new EntityDraft(index, Id, _turn);
        Record(CommandOp.Create, draft.Ref, -1, default, file, line);
        return draft;
    }

    public void Destroy(Entity e, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (Owner is { DestroysEntities: false }) Undeclared("Destroys()", $"удалить {e}");
        Record(CommandOp.Destroy, CheckTarget(e), -1, default, file, line);
    }

    public void Add<T>(EntityDraft draft, in T value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : unmanaged =>
        Add(draft.Ref, value, file, line);

    public void Add<T>(Entity e, in T value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Adds.Contains(t.Id)) Undeclared($"Adds<{typeof(T).Name}>()", "добавить компонент");
        RecordWithValue(CommandOp.Add, CheckTarget(e), t, value, file, line);
    }

    public void Remove<T>(Entity e, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Removes.Contains(t.Id)) Undeclared($"Removes<{typeof(T).Name}>()", "снять компонент");
        Record(CommandOp.Remove, CheckTarget(e), t.Id, default, file, line);
    }

    /// <summary>Заменить значение компонента, который у сущности уже есть. У реактивного — единственный способ.</summary>
    public void Set<T>(Entity e, in T value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : unmanaged
    {
        var t = _world.TypeOf<T>();
        if (Owner is not null && !Owner.Sets.Contains(t.Id)) Undeclared($"Sets<{typeof(T).Name}>()", "заменить компонент");
        RecordWithValue(CommandOp.Set, CheckTarget(e), t, value, file, line);
    }

    public void Emit<E>(in E value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where E : unmanaged =>
        EmitAs(value, CauseKind.Event, null, file, line);

    /// <summary>
    /// Команда тика событием (служебная стадия Runtime <c>runtime.commands</c>): в диагностическом режиме — корень причинности
    /// (<see cref="CauseKind.Command"/>, без родителя), <paramref name="origin"/> — откуда (клиент, агент, реплей). Так корнем
    /// становится любая зарегистрированная команда тика — игрока, арены, врага, пробы, ручек, агента.
    /// </summary>
    public void EmitCommand<E>(in E value, string origin, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where E : unmanaged =>
        EmitAs(value, CauseKind.Command, origin, file, line);

    private void EmitAs<E>(in E value, CauseKind kind, string? origin, string file, int line) where E : unmanaged
    {
        var t = _world.EventTypeOf<E>();
        if (Owner is not null && !Owner.Emits.Contains(t.Id)) Undeclared($"Emits<{typeof(E).Name}>()", "записать событие");
        int offset = Store(value);
        CheckDeferred(t, offset);
        if (EventCount == Events.Length) Array.Resize(ref Events, Events.Length * 2);
        var cause = Stamp(kind, Entity.Null, t.Id, file, line, origin);
        Events[EventCount++] = new EventRecord { Type = t.Id, PayloadOffset = offset, PayloadSize = t.Size, Cause = cause };
    }

    public void SetResource<T>(T value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var slot = _world.ResourceSlotOf<T>(out int index);
        if (Owner is not null && !Owner.SetsResources.Contains(slot.Type)) Undeclared($"SetsResource<{typeof(T).Name}>()", "заменить ресурс");
        Record(CommandOp.SetResource, Entity.Null, index, (slot.Stage(value), 0), file, line);
    }

    private void RecordWithValue<T>(CommandOp op, Entity target, DataType t, in T value, string file, int line) where T : unmanaged
    {
        int offset = t.IsTag ? PayloadUsed : Store(value);
        CheckDeferred(t, offset);
        Record(op, target, t.Id, (offset, t.Size), file, line);
    }

    private void Record(CommandOp op, Entity target, int type, (int Offset, int Size) payload, string file, int line)
    {
        if (CommandCount == Commands.Length) Array.Resize(ref Commands, Commands.Length * 2);
        var cause = Stamp(op switch
        {
            CommandOp.Create => CauseKind.Create,
            CommandOp.Destroy => CauseKind.Destroy,
            CommandOp.Add => CauseKind.Add,
            CommandOp.Remove => CauseKind.Remove,
            CommandOp.Set => CauseKind.Set,
            _ => CauseKind.SetResource,
        }, target, type, file, line, null);
        Commands[CommandCount++] = new CommandRecord
        {
            Op = op, Target = target, Type = type, PayloadOffset = payload.Offset, PayloadSize = payload.Size, Cause = cause,
        };
    }

    /// <summary>
    /// Причина записи: вне диагностического режима — <see cref="Cause"/> как есть (одна проверка на null); в режиме — новая запись
    /// журнала причинности с родителем <see cref="Cause"/> (у команды тика — без родителя: она корень).
    /// </summary>
    private CauseId Stamp(CauseKind kind, Entity target, int type, string file, int line, string? note)
    {
        if (_world.Causality is not { } log) return LastCause = Cause;
        return LastCause = log.Append(kind, kind == CauseKind.Command ? CauseId.None : Cause, _world.CurrentTick, Owner?.StageIndex ?? -1,
            Owner?.Id ?? 0, target, type, file, line, note, 0);
    }

    private int Store<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (PayloadUsed + size > Payload.Length) Array.Resize(ref Payload, Math.Max(Payload.Length * 2, PayloadUsed + size));
        int offset = PayloadUsed;
        MemoryMarshal.Write(Payload.AsSpan(offset, size), in value);
        PayloadUsed += size;
        return offset;
    }

    /// <summary>
    /// Цель команды: живая сущность или черновик этого буфера и этого оборота. Черновик чужого буфера или прошлого
    /// барьера — ошибка: он разрешился бы в чужую сущность. Проверка — сравнение, поэтому действует всегда.
    /// </summary>
    private Entity CheckTarget(Entity e)
    {
        if (e.IsNull) throw new ArgumentException("команда для Entity.Null");
        if (e.IsDeferred) CheckDraft(e, "команда");
        return e;
    }

    /// <summary>
    /// Отложенные значения в полях <see cref="Entity"/> — только черновики этого буфера и этого оборота. Проверяется
    /// всегда (сравнение на поле): разрешитель берёт номер черновика из значения и без проверки подставил бы чужую сущность.
    /// </summary>
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
