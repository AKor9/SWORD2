using Sword.Engine.Platform;

namespace Sword.Engine.Client;

/// <summary>
/// Защёлка фронтов действий до тика (раздел 7 плана, «Ввод»): клиент снимает ввод раз в кадр, а тиков в кадре 0, 1 или
/// несколько. Нажатие и отпускание копятся с кадров (<see cref="Note"/>) и достаются ровно одному тику — первому после
/// кадра, в котором случились (<see cref="TickInput"/>, затем <see cref="Consume"/>): кадр без тиков их не теряет, кадр
/// из трёх тиков не повторяет трижды. Удерживаемое (уровни) сюда не входит — его клиент повторяет в каждом тике кадра.
/// </summary>
public sealed class InputEdges
{
    private readonly InputBindings _bindings;
    private readonly bool[] _pressed, _released;

    public InputEdges(InputBindings bindings)
    {
        _bindings = bindings;
        _pressed = new bool[bindings.Actions.Count];
        _released = new bool[bindings.Actions.Count];
    }

    /// <summary>Фронты кадра — в защёлку (до ближайшего тика).</summary>
    public void Note(in FrameInput input)
    {
        var actions = _bindings.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            if (input.WasPressed(actions[i])) _pressed[i] = true;
            if (input.WasReleased(actions[i])) _released[i] = true;
        }
    }

    /// <summary>Фронты для ближайшего тика.</summary>
    public TickInput ForTick() => new(this);

    /// <summary>Тик забрал фронты: следующие тики их не видят.</summary>
    public void Consume()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
    }

    internal bool Pressed(InputAction action) => _pressed[_bindings.IndexOf(action)];

    internal bool Released(InputAction action) => _released[_bindings.IndexOf(action)];
}

/// <summary>
/// Фронты действий для одного тика (<see cref="IClientGame.BeforeTick"/>): нажатие или отпускание с прошлого тика —
/// только в первом тике после кадра, где оно случилось.
/// </summary>
public readonly struct TickInput
{
    private readonly InputEdges? _edges;

    internal TickInput(InputEdges edges) => _edges = edges;

    /// <summary>Без ввода (симуляция без клиента ввода, тесты).</summary>
    public static TickInput None => default;

    /// <summary>Действие нажато с прошлого тика.</summary>
    public bool WasPressed(InputAction action) => _edges?.Pressed(action) == true;

    /// <summary>Действие отпущено с прошлого тика.</summary>
    public bool WasReleased(InputAction action) => _edges?.Released(action) == true;
}
