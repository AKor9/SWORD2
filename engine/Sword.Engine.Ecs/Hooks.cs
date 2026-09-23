using System.Runtime.InteropServices;

namespace Sword.Engine.Ecs;

/// <summary>
/// Хук добавления компонента (раздел 5 плана): компонент только что появился у сущности — завести ресурс подсистемы
/// (тело, позу) и записать его хэндл в <paramref name="value"/>. Зовётся на барьере, после журналов; структуру мира не
/// меняет (команд у хука нет, немедленные операции — исключение).
/// </summary>
/// <param name="value">
/// Компонент на месте: пока он у сущности — ячейка колонки; снят на этом же барьере — копия, которую получит и хук
/// снятия (<see cref="RemoveHook{T}"/>), поэтому заведённое здесь он же и освободит.
/// </param>
public delegate void AddHook<T>(Entity entity, ref T value) where T : unmanaged;

/// <summary>
/// Хук снятия компонента: компонент снят командой или сущность удалена — освободить ресурс подсистемы по хэндлу из
/// <paramref name="value"/> (значение на момент снятия; снят тот, что добавлен на этом же барьере, — со всем, что записал
/// его хук добавления).
/// </summary>
public delegate void RemoveHook<T>(Entity entity, in T value) where T : unmanaged;

/// <summary>Хук наружу (описание мира для агента): компонент стабильным именем, вид (<c>OnAdd</c>/<c>OnRemove</c>), модуль-владелец.</summary>
public sealed record HookInfo(string Component, string Kind, string? Module);

/// <summary>Хуки одного типа: типизированный вызов по байтам ячейки или копии — без упаковки и выделений.</summary>
internal abstract class ComponentHooks
{
    public string? AddModule, RemoveModule;

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
