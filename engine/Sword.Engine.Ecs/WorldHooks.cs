namespace Sword.Engine.Ecs;

/// <summary>
/// Хуки времени жизни: <see cref="AddHook{T}"/> и <see cref="RemoveHook{T}"/> заводят и освобождают ресурс подсистемы
/// вместе с компонентом. Хуки не нормализуются: компонент, добавленный и снятый на одном барьере, в журналах не виден,
/// но оба хука отработают над одной копией значения. Хук видит мир после всех команд барьера и структуру не меняет.
/// <c>Add</c> имеющегося компонента без проверок — замена значения, хука нет.
/// </summary>
public sealed partial class World
{
    private readonly ComponentHooks?[] _hooks;
    private readonly List<HookCall> _hookCalls = [];
    /// <summary>Добавление, чей компонент ещё у сущности на этом барьере: (сущность, тип) → номер вызова в очереди.</summary>
    private readonly Dictionary<(Entity, int), int> _pendingAdd = [];
    private byte[] _hookValues = new byte[256];
    private int _hookUsed;
    /// <summary>Идут хуки: немедленные операции, тик и переключение систем запрещены.</summary>
    private bool _inHooks;

    private struct HookCall
    {
        public Entity Entity;
        public int Type;
        public bool Add;
        /// <summary>Смещение копии значения в <see cref="_hookValues"/>; −1 — ячейка компонента у живой сущности.</summary>
        public int Value;
    }

    internal void SetHook<T>(AddHook<T>? onAdd, RemoveHook<T>? onRemove) where T : unmanaged
    {
        var t = TypeOf<T>();
        var hooks = (ComponentHooks<T>)(_hooks[t.Id] ??= new ComponentHooks<T>());
        _hooked[t.Id] = true;
        if (onAdd is not null)
        {
            if (hooks.OnAdd is not null) throw new InvalidOperationException($"хук OnAdd<{t.StableName}> дважды: у ресурса компонента один владелец");
            hooks.OnAdd = onAdd;
        }
        if (onRemove is not null)
        {
            if (hooks.OnRemove is not null) throw new InvalidOperationException($"хук OnRemove<{t.StableName}> дважды: у ресурса компонента один владелец");
            hooks.OnRemove = onRemove;
        }
    }

    /// <summary>Компонент с хуком добавлен (команда сыграна, значение в ячейке).</summary>
    private void NoteHookAdd(Entity e, int type)
    {
        _pendingAdd[(e, type)] = _hookCalls.Count;
        _hookCalls.Add(new HookCall { Entity = e, Type = type, Add = true, Value = -1 });
    }

    /// <summary>Снятие компонента с хуком: значение копируется; добавлен на этом же барьере — хук добавления получит ту же
    /// копию, и хук снятия увидит записанный в неё хэндл.</summary>
    private void NoteHookRemoval(Entity e, int type)
    {
        var dt = _types[type];
        int offset = _hookUsed;
        if (!dt.IsTag)
        {
            if (_hookUsed + dt.Size > _hookValues.Length) Array.Resize(ref _hookValues, Math.Max(_hookValues.Length * 2, _hookUsed + dt.Size));
            CellOf(e, dt).CopyTo(_hookValues.AsSpan(offset, dt.Size));
            _hookUsed += dt.Size;
        }
        if (_pendingAdd.Remove((e, type), out int added))
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hookCalls)[added].Value = offset;
        _hookCalls.Add(new HookCall { Entity = e, Type = type, Add = false, Value = offset });
    }

    /// <summary>Хуки барьера по порядку команд — после журналов, до событий.</summary>
    private void RunHooks()
    {
        _inHooks = true;
        try
        {
            for (int i = 0; i < _hookCalls.Count; i++)
            {
                var c = _hookCalls[i];
                var dt = _types[c.Type];
                var hooks = _hooks[c.Type]!;
                if (c.Add)
                {
                    var value = c.Value >= 0 ? _hookValues.AsSpan(c.Value, dt.Size) : dt.IsTag ? [] : CellOf(c.Entity, dt);
                    hooks.Add(c.Entity, value);
                }
                else hooks.Remove(c.Entity, _hookValues.AsSpan(c.Value, dt.Size));
            }
        }
        finally
        {
            _inHooks = false;
            ClearHooks();
        }
    }

    private void ClearHooks()
    {
        _hookCalls.Clear();
        _pendingAdd.Clear();
        _hookUsed = 0;
    }

    /// <summary>Из хука — ни немедленных операций, ни тика, ни переключения систем: хук структуру мира не меняет.</summary>
    private void NotInHooks(string operation)
    {
        if (_inHooks)
            throw new InvalidOperationException($"World.{operation} из хука: хуки только заводят и освобождают ресурсы подсистем — структуру мира не меняют, повторного входа нет");
    }
}
