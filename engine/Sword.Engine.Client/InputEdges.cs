namespace Sword.Engine.Client;

/// <summary>
/// Защёлка фронтов действий до тика (раздел 7 плана, «Ввод»): клиент снимает ввод раз в кадр, а тиков в кадре 0, 1 или
/// несколько. Нажатие и отпускание копятся с кадров (<see cref="Note"/>) и достаются ровно одному тику — первому после
/// кадра, в котором случились (<see cref="ForTick"/>, затем <see cref="Consume"/>): кадр без тиков их не теряет, кадр из
/// трёх тиков не повторяет трижды. Уровень (удерживается ли действие) — по последнему кадру: по нему тик восстанавливает
/// порядок («отпустил и снова нажал» против «нажал и отпустил»). Два нажатия одного действия между двумя тиками
/// (быстрее 1/120 с) сливаются в одно. Удерживаемое клиент повторяет в каждом тике кадра командой из <c>Input</c>.
/// </summary>
public sealed class InputEdges
{
    /// <summary>Действий не больше: снимок тика — битовые маски.</summary>
    public const int MaxActions = 64;

    private readonly InputBindings _bindings;
    private ulong _pressed, _released, _down;

    public InputEdges(InputBindings bindings)
    {
        if (bindings.Actions.Count > MaxActions)
            throw new ArgumentException($"действий ввода больше {MaxActions}: снимок тика — битовые маски");
        _bindings = bindings;
    }

    /// <summary>Фронты и уровни кадра — в защёлку (до ближайшего тика).</summary>
    public void Note(in FrameInput input)
    {
        var actions = _bindings.Actions;
        ulong down = 0;
        for (int i = 0; i < actions.Count; i++)
        {
            ulong bit = 1UL << i;
            if (input.WasPressed(actions[i])) _pressed |= bit;
            if (input.WasReleased(actions[i])) _released |= bit;
            if (input.IsDown(actions[i])) down |= bit;
        }
        _down = down;
    }

    /// <summary>Снимок для ближайшего тика: его можно хранить — он не меняется следующими кадрами.</summary>
    public TickInput ForTick() => new(_bindings, _pressed, _released, _down);

    /// <summary>Тик забрал фронты: следующие тики их не видят (уровень остаётся).</summary>
    public void Consume() => _pressed = _released = 0;
}

/// <summary>
/// Фронты и уровни действий для одного тика (<see cref="IClientGame.BeforeTick"/>) — снимок: нажатие или отпускание с
/// прошлого тика видит только первый тик после кадра, где оно случилось.
/// </summary>
public readonly struct TickInput
{
    private readonly InputBindings? _bindings;
    private readonly ulong _pressed, _released, _down;

    internal TickInput(InputBindings bindings, ulong pressed, ulong released, ulong down)
    {
        _bindings = bindings;
        _pressed = pressed;
        _released = released;
        _down = down;
    }

    /// <summary>Без ввода (симуляция без клиента ввода, тесты).</summary>
    public static TickInput None => default;

    /// <summary>Действие нажато с прошлого тика.</summary>
    public bool WasPressed(InputAction action) => Has(_pressed, action);

    /// <summary>Действие отпущено с прошлого тика.</summary>
    public bool WasReleased(InputAction action) => Has(_released, action);

    /// <summary>Действие удерживается (по последнему кадру перед тиком).</summary>
    public bool IsDown(InputAction action) => Has(_down, action);

    private bool Has(ulong mask, InputAction action) => _bindings is not null && (mask >> _bindings.IndexOf(action) & 1) != 0;
}
