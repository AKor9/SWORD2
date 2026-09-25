using System.Text.Json.Nodes;

namespace Sword.Engine.Introspection.Tests;

public struct Modes
{
    public Gait Gait;
    public Parts Parts;
}

/// <summary>Проверки: значения перечислений.</summary>
public class SecondReviewTests
{
    [Theory]
    [InlineData("""{"Gait":999}""", "нет значения «999»")]    // число вне объявленных — не значение
    [InlineData("""{"Gait":-1}""", "нет значения «-1»")]
    [InlineData("""{"Parts":8}""", "нет значения «8»")]       // [Flags]: неизвестный бит
    [InlineData("""{"Parts":9}""", "нет значения «9»")]
    public void Enum_UndeclaredNumbers_AreErrors(string json, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => FieldCodec.Encode(typeof(Modes), JsonNode.Parse(json))).Message);

    [Fact]
    public void Enum_DeclaredNumbersAndBits_Accepted()
    {
        var bytes = FieldCodec.Encode(typeof(Modes), JsonNode.Parse("""{"Gait":2,"Parts":5}"""));
        var json = FieldCodec.Decode(typeof(Modes), bytes)!;
        Assert.Equal("Run", json["Gait"]!.GetValue<string>());
        Assert.Equal("A, C", json["Parts"]!.GetValue<string>());
        Assert.Equal("None", FieldCodec.Decode(typeof(Modes), FieldCodec.Encode(typeof(Modes), JsonNode.Parse("""{"Parts":0}""")))!["Parts"]!.GetValue<string>());
    }
}

/// <summary>Проверки: производители Changed, пути полей.</summary>
public class ThirdReviewTests
{
    [Fact]
    public void ExplainSystem_AddsFeedChanged_LikeEcs()
    {
        // Add уже имеющегося компонента заменяет значение — Registration.Produces считает это Changed; карта — тоже.
        string body = typeof(Body).FullName!;
        var adder = new Sword.Engine.Ecs.SystemInfo("a@S1", "S1", "a", 1, null, true, false, []) { Order = 0, Adds = [body] };
        var watcher = new Sword.Engine.Ecs.SystemInfo("x@S2", "S2", "x", 2, null, true, true, ["Changed<Body>"])
        {
            ConsumesTriggers = [new Sword.Engine.Ecs.TriggerInfo("Changed", body)], ConsumesData = [body],
        };
        var link = Assert.Single(ArchitectureMap.ExplainSystem([adder, watcher], "x")["upstream"]!.AsArray())!;
        Assert.Equal("a@S1", link["system"]!.GetValue<string>());
        Assert.Equal($"Changed:{body}", link["via"]![0]!.GetValue<string>());
        var down = Assert.Single(ArchitectureMap.ExplainSystem([adder, watcher], "a")["downstream"]!.AsArray())!;
        Assert.Equal("x@S2", down["system"]!.GetValue<string>());
    }

    [Fact]
    public void CheckPath_ExactNames_TyposAndLeavesAreErrors()
    {
        Assert.Equal("Move.X", FieldCodec.CheckPath(typeof(Order), "move.x", "Order"));
        Assert.Contains("Order.Move: нет поля «Z»; есть: X, Y", Assert.Throws<ArgumentException>(() => FieldCodec.CheckPath(typeof(Order), "Move.Z", "Order")).Message);
        Assert.Contains("вложенных полей нет", Assert.Throws<ArgumentException>(() => FieldCodec.CheckPath(typeof(Order), "Gait.Run", "Order")).Message);
        Assert.Contains("нет поля «»", Assert.Throws<ArgumentException>(() => FieldCodec.CheckPath(typeof(Order), "Move.", "Order")).Message);
    }
}

/// <summary>Проверки: когда связь видна — по стадии и виду связи.</summary>
public class StageTimingTests
{
    private static Sword.Engine.Ecs.SystemInfo Sys(string name, string stage, int order) =>
        new(name, stage, name.Split('@')[0], order, null, true, false, []) { Order = order };

    private static List<(string System, string When)> Up(IReadOnlyList<Sword.Engine.Ecs.SystemInfo> all, string name) =>
        [.. ArchitectureMap.ExplainSystem(all, name)["upstream"]!.AsArray().Select(n => (n!["system"]!.GetValue<string>(), n["when"]!.GetValue<string>()))];

    private static List<(string System, string When)> Down(IReadOnlyList<Sword.Engine.Ecs.SystemInfo> all, string name) =>
        [.. ArchitectureMap.ExplainSystem(all, name)["downstream"]!.AsArray().Select(n => (n!["system"]!.GetValue<string>(), n["when"]!.GetValue<string>()))];

    [Fact]
    public void SameStage_CommandsReachNextTick_DirectWritesThisTick()
    {
        string body = typeof(Body).FullName!, gait = typeof(Gait).FullName!;
        var writer = Sys("w@S1", "S1", 0) with { Writes = [body] };
        var adder = Sys("a@S1", "S1", 1) with { Adds = [body] };
        var emitter = Sys("e@S1", "S1", 2) with { Emits = [gait] };
        var reader = Sys("r@S1", "S1", 3) with
        {
            Reads = [body], ConsumesTriggers = [new Sword.Engine.Ecs.TriggerInfo("Event", gait)], ConsumesData = [gait],
        };
        var late = Sys("l@S2", "S2", 0) with
        {
            Reads = [body], ConsumesTriggers = [new Sword.Engine.Ecs.TriggerInfo("Event", gait)], ConsumesData = [gait],
        };
        var all = new[] { writer, adder, emitter, reader, late };
        var up = Up(all, "r@S1");
        Assert.Contains(("w@S1", "в этом тике, раньше"), up);   // прямая запись, раньше по порядку
        Assert.Contains(("a@S1", "с прошлого тика"), up);       // Add — на барьере после стадии
        Assert.Contains(("e@S1", "с прошлого тика"), up);       // событие той же стадии — в следующем тике
        Assert.DoesNotContain(("a@S1", "в этом тике, раньше"), up);
        var lateUp = Up(all, "l@S2");
        Assert.Contains(("a@S1", "в этом тике, раньше"), lateUp);
        Assert.Contains(("e@S1", "в этом тике, раньше"), lateUp);
        Assert.Contains(("r@S1", "в следующем тике"), Down(all, "e@S1"));
        Assert.Contains(("l@S2", "в этом тике, позже"), Down(all, "e@S1"));
    }
}
