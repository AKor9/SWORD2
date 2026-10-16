using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>
/// Хук добавления: завести ресурс подсистемы и записать хэндл в <paramref name="value"/>. Зовётся на барьере после
/// журналов; структуру мира не меняет. Снят на том же барьере — <paramref name="value"/> — копия, которую получит и
/// хук снятия.
/// </summary>
public delegate void AddHook<T>(Entity entity, ref T value) where T : unmanaged;

/// <summary>Хук снятия (команда или удаление сущности): освободить ресурс по хэндлу из <paramref name="value"/> —
/// значения на момент снятия.</summary>
public delegate void RemoveHook<T>(Entity entity, in T value) where T : unmanaged;

/// <summary>Хуки одного типа: типизированный вызов по байтам ячейки или копии — без упаковки и выделений.</summary>
internal abstract class ComponentHooks
{
    public abstract bool HasAdd { get; }
    public abstract bool HasRemove { get; }

    public abstract void Add(Entity e, Span<byte> value);
    public abstract void Remove(Entity e, ReadOnlySpan<byte> value);
}

internal sealed class ComponentHooks<T> : ComponentHooks where T : unmanaged
{
    public AddHook<T>? OnAdd;
    public RemoveHook<T>? OnRemove;

    public override bool HasAdd => OnAdd is not null;
    public override bool HasRemove => OnRemove is not null;

    public override void Add(Entity e, Span<byte> value)
    {
        if (OnAdd is not { } hook) return;
        if (value.IsEmpty)
        {
            T tag = default;   // тег колонки не занимает — хуку достаётся пустое значение
            hook(e, ref tag);
        }
        else hook(e, ref MemoryMarshal.AsRef<T>(value));
    }

    public override void Remove(Entity e, ReadOnlySpan<byte> value)
    {
        if (OnRemove is not { } hook) return;
        if (value.IsEmpty)
        {
            T tag = default;
            hook(e, in tag);
        }
        else hook(e, in MemoryMarshal.AsRef<T>(value));
    }
}
