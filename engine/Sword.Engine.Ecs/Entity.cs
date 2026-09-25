namespace Sword.Engine.Ecs;

/// <summary>
/// Сущность: номер и поколение. Удаление увеличивает поколение — висячий хэндл распознаётся. Живые поколения
/// начинаются с 1, поэтому <c>default</c> — не сущность (<see cref="Null"/>).
///
/// Отрицательный номер — <b>отложенное значение</b> черновика (<see cref="EntityDraft.Ref"/>): в номере — побитовое
/// отрицание (номер буфера · 2¹⁶ + номер черновика в буфере), в поле поколения — оборот буфера (все 32 бита): черновик
/// прошлого барьера отличим от нынешнего, пока буфер не сделал 2³² оборотов с черновиками. Его можно записать в
/// компонент или событие через команды буфера; на барьере оно заменяется настоящей сущностью. С живой сущностью его не
/// спутать.
/// </summary>
public readonly record struct Entity(int Index, int Generation)
{
    public static readonly Entity Null = default;

    /// <summary>Отложенное значение черновика, ещё не заменённое на барьере.</summary>
    public bool IsDeferred => Index < 0;

    public bool IsNull => Index == 0 && Generation == 0;

    /// <summary>Номер черновика в его буфере (у отложенного значения).</summary>
    public int DraftIndex => ~Index & 0xFFFF;

    /// <summary>Номер буфера черновика (у отложенного значения).</summary>
    public int DraftBuffer => ~Index >> 16;

    /// <summary>Оборот буфера, в котором создан черновик (у отложенного значения).</summary>
    public uint DraftTurn => unchecked((uint)Generation);

    public override string ToString() =>
        IsDeferred ? $"черновик {DraftIndex} буфера {DraftBuffer} (оборот {DraftTurn})" : IsNull ? "Entity.Null" : $"{Index}v{Generation}";
}

/// <summary>
/// Черновик сущности: команда создания записана, живой сущности до барьера нет. Команды и события <b>своего</b>
/// буфера могут ссылаться на черновик (<see cref="Ref"/> — отложенное значение <see cref="Entity"/>).
/// </summary>
public readonly record struct EntityDraft(int Index, int Buffer, uint Turn)
{
    /// <summary>Черновиков в буфере до барьера — не больше; буферов — не больше (номер буфера и черновика — в номере).</summary>
    public const int MaxDrafts = 1 << 16, MaxBuffers = (1 << 15) - 1;

    /// <summary>Отложенное значение: поле типа <see cref="Entity"/> в компоненте или событии; заменяется на барьере.</summary>
    public Entity Ref => new(~((Buffer << 16) | Index), unchecked((int)Turn));
}

/// <summary>
/// Причина записи команды или события — для <c>state.why</c> (раздел 8 плана). Слот есть в каждой записи с v0; вне
/// диагностического режима пуст (<see cref="None"/>) и ничего не стоит; в режиме — номер записи журнала причинности
/// (<see cref="CausalityLog"/>). В хеш состояния не входит.
/// </summary>
public readonly record struct CauseId(int Value)
{
    public static readonly CauseId None = default;
}

/// <summary>
/// Реактивный компонент: меняется только командой <c>Set</c>, поэтому каждое изменение попадает в журнал
/// <see cref="Changed{T}"/>. Прямая запись через <see cref="ChunkView.Write{T}"/> — исключение (проверки мира включены).
/// Частые данные реактивными не делаются.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ReactiveAttribute : Attribute;

/// <summary>Метка системы: по ней задаётся порядок (<c>After/Before</c>) внутри стадии. Уникальна внутри стадии.</summary>
public readonly record struct SystemLabel(string Name)
{
    public override string ToString() => Name;
}

// ── срабатывания: записи журналов ───────────────────────────────────────────

/// <summary>Компонент <typeparamref name="T"/> добавлен (сущности его не было до барьера, после — есть).</summary>
public readonly record struct Added<T>(Entity Entity, CauseId Cause) : ICaused where T : unmanaged;

/// <summary>Компонент снят или сущность удалена; <see cref="Value"/> — значение на момент снятия.</summary>
public readonly record struct Removed<T>(Entity Entity, T Value, CauseId Cause) : ICaused where T : unmanaged;

/// <summary>Реактивный компонент заменён командой <c>Set</c>; <see cref="Value"/> — итоговое значение барьера.</summary>
public readonly record struct Changed<T>(Entity Entity, T Value, CauseId Cause) : ICaused where T : unmanaged;

/// <summary>Событие <typeparamref name="E"/>.</summary>
public readonly record struct Event<E>(E Value, CauseId Cause) : ICaused where E : unmanaged;

/// <summary>Ресурс мира заменён.</summary>
public readonly record struct ResourceChanged<T>(T Value, CauseId Cause) : ICaused;
