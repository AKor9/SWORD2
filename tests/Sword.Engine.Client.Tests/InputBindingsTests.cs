using Sword.Engine.Runtime;

namespace Sword.Engine.Client.Tests;

/// <summary>Привязки клавиш к действиям ввода — из конфига.</summary>
public class InputBindingsTests
{
    private static readonly InputAction Forward = new("move_forward", "W");
    private static readonly InputAction Orbit = new("camera_orbit", "MouseMiddle");
    private static readonly InputAction[] Actions = [Forward, Orbit];

    [Fact]
    public void Defaults_ThenConfigLayers()
    {
        Assert.Equal("W", InputBindings.Resolve(Actions, null).Keys["move_forward"]);
        var env = new Dictionary<string, string> { ["SWORD_INPUT_CAMERA_ORBIT"] = "mouseright" };
        var config = ConfigSet.Load(null, ["input.move_forward=up"], k => env.GetValueOrDefault(k));
        var b = InputBindings.Resolve(Actions, config);
        Assert.Equal("Up", b.Keys["move_forward"]);       // имя клавиши — без учёта регистра, в итоге — каноническое
        Assert.Equal("MouseRight", b.Keys["camera_orbit"]);
        Assert.Equal("up", config.Effective["input"]!["move_forward"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("input.move_forwrd=W", "нет ключа move_forwrd")]   // неизвестное действие
    [InlineData("input.move_forward=Wx", "нет такой клавиши")]     // неизвестная клавиша
    [InlineData("input.move_forward=7", "ожидается строка")]       // не строка
    [InlineData("input.move_forward=MouseX1", "нет такой клавиши")]
    [InlineData("input.move_forward=A,B", "нет такой клавиши")]           // Enum.TryParse сложил бы в C
    [InlineData("input.move_forward=Left, Up", "нет такой клавиши")]      // …и в Right
    [InlineData("input.camera_orbit=MouseLeft,Right", "нет такой клавиши")]
    [InlineData("input.camera_orbit=Mouse0", "нет такой клавиши")]        // кнопка — по имени, не по числу
    public void Typos_AreCompositionErrors(string set, string expected) =>
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() =>
            InputBindings.Resolve(Actions, ConfigSet.Empty(set))).Message);

    [Fact]
    public void BadDefaultKey_IsGameError_NotConfigError()
    {
        var ex = Assert.Throws<ArgumentException>(() => InputBindings.Resolve([new InputAction("jump", "Bogus")], null));
        Assert.Contains("ошибка игры", ex.Message);
        Assert.Throws<ArgumentException>(() => new InputAction("jump", ""));
    }

    [Fact]
    public void HeldAction_IsDown_AndPressedInFirstFrameOnly()
    {
        var b = InputBindings.Resolve(Actions, null);
        var held = new HashSet<string> { "move_forward" };
        var first = new FrameInput(new Sword.Engine.Platform.InputState(), b, held, heldPressed: true, true, 640, 480, 0.016f);
        var next = new FrameInput(new Sword.Engine.Platform.InputState(), b, held, heldPressed: false, true, 640, 480, 0.016f);
        Assert.True(first.IsDown(Forward) && first.WasPressed(Forward));   // как клавиша, зажатая с начала
        Assert.True(next.IsDown(Forward) && !next.WasPressed(Forward));
        Assert.False(first.IsDown(Orbit));
    }

    [Fact]
    public void ActionNames_FollowConfigKeyRule_AndAreUnique()
    {
        Assert.Throws<ArgumentException>(() => new InputAction("Move.Forward", "W"));
        Assert.Throws<ArgumentException>(() => InputBindings.Resolve([Forward, new InputAction("move_forward", "S")], null));
    }
}
