using Sword.Engine.Platform;
using Sword.Engine.Runtime;

namespace Sword.Engine.Client.Tests;

/// <summary>Нажатие и отпускание достаются ровно одному тику — первому после кадра: кадр без тиков их не теряет, из нескольких — не повторяет.</summary>
public class InputEdgesTests
{
    private static readonly InputAction Attack = new("attack", "Space");
    private static readonly InputAction Block = new("block", "MouseRight");
    private static readonly InputBindings Bindings = InputBindings.Resolve([Attack, Block], null);

    private readonly InputState _state = new();
    private readonly InputEdges _edges = new(Bindings);
    private readonly List<(ulong Tick, bool Pressed, bool Released, bool BlockPressed)> _seen = [];
    private readonly SimulationClock _clock;

    public InputEdgesTests()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], []);
        _clock = new SimulationClock(sim, (link, input) =>
            _seen.Add((link.Tick, input.WasPressed(Attack), input.WasReleased(Attack), input.WasPressed(Block))), edges: _edges);
    }

    /// <summary>Кадр: платформа начала кадр, клавиши, ввод кадра в защёлку, тики.</summary>
    private int Frame(int ticks, Action<InputState>? keys = null)
    {
        _state.BeginFrame();
        keys?.Invoke(_state);
        _edges.Note(new FrameInput(_state, Bindings, null, false, true, 640, 480, 1f / 60));
        return _clock.RunFrame(ticks / 120.0);
    }

    [Fact]
    public void PressInFrameWithoutTicks_ReachesFirstTickOfNextFrame_Once()
    {
        Assert.Equal(0, Frame(0, s => s.SetKey((int)Key.Space, true)));   // кадр без тиков — нажатие не теряется
        Assert.Equal(3, Frame(3));                                         // кадр из трёх тиков — только первый
        Assert.Equal([true, false, false], _seen.Select(x => x.Pressed));
    }

    [Fact]
    public void PressInFrameWithThreeTicks_IsNotRepeated()
    {
        Frame(3, s => s.SetKey((int)Key.Space, true));
        Frame(2);   // клавиша удерживается — новых нажатий нет
        Assert.Equal([true, false, false, false, false], _seen.Select(x => x.Pressed));
    }

    [Fact]
    public void TapWithinOneFrame_GivesPressAndRelease_ToOneTick()
    {
        Frame(2, s =>
        {
            s.SetKey((int)Key.Space, true);
            s.SetKey((int)Key.Space, false);
        });
        Assert.Equal([(true, true), (false, false)], _seen.Select(x => (x.Pressed, x.Released)));
    }

    [Fact]
    public void TickInput_IsASnapshot_AndCarriesLevel()
    {
        // Снимок первого тика не меняется от следующих кадров; уровень отличает «отпустил и снова нажал» от «нажал и
        // отпустил» между двумя тиками.
        TickInput kept = default;
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], []);
        var clock = new SimulationClock(sim, (_, input) => kept = kept.Equals(default(TickInput)) ? input : kept, edges: _edges);
        _state.BeginFrame();
        _state.SetKey((int)Key.Space, true);
        _edges.Note(new FrameInput(_state, Bindings, null, false, true, 640, 480, 1f / 60));
        clock.RunFrame(1 / 120.0);
        Frame(0, s => s.SetKey((int)Key.Space, false));
        Assert.True(kept.WasPressed(Attack) && !kept.WasReleased(Attack) && kept.IsDown(Attack));

        // Между тиками: отпустил (кадр без тиков), снова нажал (кадр без тиков) — тик видит оба фронта и уровень «нажато».
        Frame(0, s => s.SetKey((int)Key.Space, true));
        Frame(1);
        var last = _seen[^1];
        Assert.True(last.Pressed && last.Released);
        Assert.True(_edges.ForTick().IsDown(Attack));
    }

    [Fact]
    public void Release_AndMouseButton_FollowTheSameRule()
    {
        Frame(1, s =>
        {
            s.SetKey((int)Key.Space, true);
            s.SetMouseButton(MouseButton.Right, true);
        });
        Frame(0, s => s.SetKey((int)Key.Space, false));   // отпущена в кадре без тиков
        Frame(2);
        Assert.Equal([(true, false, true), (false, true, false), (false, false, false)],
            _seen.Select(x => (x.Pressed, x.Released, x.BlockPressed)));
    }
}
