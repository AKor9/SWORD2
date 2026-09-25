using System.Text.Json.Nodes;

namespace Sword.Engine.Introspection.Tests;

public unsafe struct Samples
{
    public fixed int Values[4];
    public float Scale;
}

public struct UndeclaredGait
{
    public Gait Gait;
}

/// <summary>Проверки: float вне диапазона, fixed-буферы, необъявленные значения, остановка времени.</summary>
public class FifthReviewTests
{
    [Theory]
    [InlineData("1e39")]
    [InlineData("-1e39")]
    public void Float_OutOfRange_IsError_NotInfinity(string value) =>
        Assert.Contains("вне диапазона float",
            Assert.Throws<ArgumentException>(() => FieldCodec.Encode(typeof(Samples), JsonNode.Parse($$"""{"Scale":{{value}}}"""))).Message);

    [Fact]
    public void Float_ExplicitInfinity_StillAccepted()
    {
        var json = FieldCodec.Decode(typeof(Samples), FieldCodec.Encode(typeof(Samples), JsonNode.Parse("""{"Scale":"Infinity"}""")))!;
        Assert.Equal("Infinity", json["Scale"]!.GetValue<string>());
    }

    [Fact]
    public void FixedBuffer_IsArray_AllElements_BothWays()
    {
        var bytes = FieldCodec.Encode(typeof(Samples), JsonNode.Parse("""{"Values":[1,2,3,4]}"""));
        var json = FieldCodec.Decode(typeof(Samples), bytes)!;
        Assert.Equal([1m, 2m, 3m, 4m], json["Values"]!.AsArray().Select(v => v!.GetValue<decimal>()));
        Assert.Throws<ArgumentException>(() => FieldCodec.Encode(typeof(Samples), JsonNode.Parse("""{"Values":[1,2,3,4,5]}""")));
        Assert.Equal("[\"integer (Int32)\"]", FieldCodec.Schema(typeof(Samples))["Values"]!.ToJsonString());
    }

    [Fact]
    public void Enum_UndeclaredValue_DecodesAsNumber()
    {
        // Мир может держать необъявленное значение (побитовая запись модуля): разбор показывает его числом, а не
        // строкой, похожей на имя. Записать его обратно нельзя — сборка принимает только объявленное.
        var json = FieldCodec.Decode(typeof(UndeclaredGait), BitConverter.GetBytes(7))!;
        Assert.Equal(System.Text.Json.JsonValueKind.Number, json["Gait"]!.GetValueKind());
        Assert.Equal(7m, json["Gait"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task TimeControl_AfterStop_NewWaitsAreCancelledAtOnce()
    {
        var time = new TimeControl(paused: true);
        time.Stop();
        var wait = time.WhenReached(5);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, time.Waiters);
        Assert.Equal(0ul, await time.WhenReached(0));   // достигнутый — по-прежнему ответ
    }
}
