namespace Sword.Engine.Ecs;

/// <summary>
/// Сущность: номер и поколение (живые — с 1, <c>default</c> — <see cref="Null"/>); удаление растит поколение.
/// Отрицательный номер — отложенное значение черновика (<see cref="EntityDraft.Ref"/>): ~(буфер · 2¹⁶ + черновик),
/// в поколении — оборот буфера; на барьере заменяется настоящей сущностью.
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

/// <summary>Черновик сущности до барьера; на него могут ссылаться команды и события <b>своего</b> буфера
/// (<see cref="Ref"/>).</summary>
public readonly record struct EntityDraft(int Index, int Buffer, uint Turn)
{
    /// <summary>Черновиков в буфере до барьера — не больше; буферов — не больше (номер буфера и черновика — в номере).</summary>
    public const int MaxDrafts = 1 << 16, MaxBuffers = (1 << 15) - 1;

    /// <summary>Отложенное значение: поле типа <see cref="Entity"/> в компоненте или событии; заменяется на барьере.</summary>
    public Entity Ref => new(~((Buffer << 16) | Index), unchecked((int)Turn));
}

/// <summary>Реактивный компонент: меняется только командой <c>Set</c>, каждое изменение — в <see cref="Changed{T}"/>.
/// Частые данные реактивными не делаются.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class ReactiveAttribute : Attribute;

/// <summary>Метка системы: по ней задаётся порядок (<c>After/Before</c>) внутри стадии. Уникальна внутри стадии.</summary>
public readonly record struct SystemLabel(string Name)
{
    public override string ToString() => Name;
}

// ── срабатывания: записи журналов ───────────────────────────────────────────

/// <summary>Компонент <typeparamref name="T"/> добавлен (сущности его не было до барьера, после — есть).</summary>
public readonly record struct Added<T>(Entity Entity) where T : unmanaged;

/// <summary>Компонент снят или сущность удалена; <see cref="Value"/> — значение на момент снятия.</summary>
public readonly record struct Removed<T>(Entity Entity, T Value) where T : unmanaged;

/// <summary>Реактивный компонент заменён командой <c>Set</c>; <see cref="Value"/> — итоговое значение барьера.</summary>
public readonly record struct Changed<T>(Entity Entity, T Value) where T : unmanaged;

/// <summary>Событие <typeparamref name="E"/>.</summary>
public readonly record struct Event<E>(E Value) where E : unmanaged;

/// <summary>Ресурс мира заменён.</summary>
public readonly record struct ResourceChanged<T>(T Value);
