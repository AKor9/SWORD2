using Sword.Engine.Ecs;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Настройки подопытного модуля сведений: «база» — строка, от которой зависит поведение, но её нет в хеше как данных.</summary>
public sealed class FactSettings
{
    public string Base { get; set; } = "A";
}

/// <summary>Счётчик подопытного модуля сведений (ресурс — в хеше <c>ecs</c>).</summary>
public struct FactCounter
{
    public int Value;
}

/// <summary>
/// Подопытный модуль: сведение реплея <c>fact.base</c> — строка настроек (как отпечаток базы Motion Matching — производное контента и
/// конфига), тик прибавляет к счётчику её длину: другая «база» — другое состояние.
/// </summary>
public sealed class FactModule : IModule
{
    public string Id => "fact";

    public void Configure(SimulationBuilder b)
    {
        string value = b.Settings<FactSettings>().Base;
        b.Resource(new FactCounter()).System("S", new Count(value.Length)).ReplayFact("fact.base", () => value);
    }

    private sealed class Count(int step) : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("fact.count")).SetsResource<FactCounter>();

        public void Run(in SystemContext ctx) => ctx.Commands.SetResource(new FactCounter { Value = ctx.World.Resource<FactCounter>().Value + step });
    }
}

/// <summary>
/// Сведения реплея (<see cref="SimulationBuilder.ReplayFact"/>, <see cref="Replay.Facts"/>): запись кладёт их в заголовок (<c>facts</c>),
/// файл — те же сведения после чтения; воспроизведение с другим значением — предупреждение и причина в тексте расхождения (а не только «на
/// тике N»); без сведений заголовок прежний (ключа <c>facts</c> нет — эталоны SWORD2 не перезаписываются); сравнение реплеев — строка
/// заголовка; имя — по правилу инварианта, дважды — ошибка.
/// </summary>
public sealed class ReplayFactsTests
{
    private static readonly ReplayInfo Info = new("1.0.0+test", "tests", "none");

    private static Simulation Build(ConfigSet config) => Simulation.Build(config, ["S"], [new WanderModule(), new FactModule()]);

    private static Replay Record(string value, int ticks = 30)
    {
        var sim = Build(ConfigSet.Empty($"fact.base={value}"));
        Assert.Equal(value, sim.Facts["fact.base"]);
        var recorder = sim.Record(Info);
        for (int t = 0; t < ticks; t++) sim.Step();
        return recorder.ToReplay();
    }

    private static ReplayPlayer Play(Replay replay, params string[] sets)
    {
        var sim = Build(replay.CreateConfig(sets));
        var player = sim.Play(replay);
        while (sim.Tick < replay.Ticks) sim.Step();
        return player;
    }

    [Fact]
    public void Facts_InHeader_SameBase_Plays()
    {
        var recorded = Record("ABC");
        string json = recorded.ToJson();
        Assert.Contains("\"facts\": {", json);
        Assert.Contains("\"fact.base\": \"ABC\"", json);
        var replay = Replay.Parse(json);
        Assert.Equal("ABC", replay.Facts!["fact.base"]);
        Assert.Equal(json, replay.ToJson());   // запись → чтение → запись — тот же текст
        Assert.Equal("ABC", replay.Head(10).Facts!["fact.base"]);
        var player = Play(replay);
        Assert.Null(player.Divergence);
        Assert.Empty(player.Warnings);
    }

    [Fact]
    public void OtherBase_WarnsAndNamesCauseInDivergence()
    {
        var replay = Replay.Parse(Record("ABC").ToJson());
        var player = Play(replay, "fact.base=ABCD");
        Assert.Contains(player.Warnings, w => w.Contains("сведения реплея: fact.base: в реплее ABC, в симуляции ABCD"));
        Assert.NotNull(player.Divergence);
        Assert.Equal(1ul, player.Divergence.Tick);   // счётчик разошёлся в первом тике
        Assert.Contains("вероятная причина: сведения реплея другие — fact.base: в реплее ABC, в симуляции ABCD", player.Divergence.ToString());
        var diff = ReplayDiff.Compare(replay, Replay.Parse(Record("ABCD").ToJson()));
        Assert.Contains("facts.fact.base: ABC ≠ ABCD", diff.Header);
    }

    [Fact]
    public void NoFacts_HeaderUnchanged_SimulationWithoutFactWarns()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new WanderModule()]);
        Assert.Empty(sim.Facts);
        var recorder = sim.Record(Info);
        for (int t = 0; t < 5; t++) sim.Step();
        var plain = recorder.ToReplay();
        Assert.Null(plain.Facts);
        Assert.DoesNotContain("facts", plain.ToJson());
        Assert.Null(Replay.Parse(plain.ToJson()).Facts);

        // Реплей со сведением — в симуляции без модуля сведения: предупреждение словами.
        var withFact = Replay.Parse(Record("ABC").ToJson());
        var other = Simulation.Build(ConfigSet.FromReplay(new System.Text.Json.Nodes.JsonObject(), []), ["S"], [new WanderModule()]);
        var player = other.Play(withFact);
        Assert.Contains(player.Warnings, w => w.Contains("сведения fact.base в симуляции нет"));
    }

    [Fact]
    public void FactName_Rule_NoDuplicates()
    {
        var bad = Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), ["S"], [new NamedFact("Bad Name")]));
        Assert.Contains("сведение реплея «Bad Name»", bad.Message);
        var twice = Assert.Throws<InvalidOperationException>(() => Simulation.Build(ConfigSet.Empty(), ["S"], [new NamedFact("a.b", twice: true)]));
        Assert.Contains("дважды", twice.Message);
    }

    private sealed class NamedFact(string name, bool twice = false) : IModule
    {
        public string Id => "named";

        public void Configure(SimulationBuilder b)
        {
            b.ReplayFact(name, () => "x");
            if (twice) b.ReplayFact(name, () => "y");
        }
    }
}
