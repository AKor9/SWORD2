using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

public struct Ping { public int Value; }
public struct Pong { public ulong Tick; }
public struct Counter { public int Value; }

public sealed record ArenaSettings
{
    public float Radius { get; init; } = 62;
    public int Enemies { get; init; } = 10;
}

public sealed record Limits
{
    public int A { get; init; } = 1;
    public int B { get; init; } = 2;
}

public sealed record NestSettings
{
    public Limits Limits { get; init; } = new() { A = 7 };
    public bool Flag { get; init; }

    /// <summary>Вычисляемое — не ключ конфига.</summary>
    public int Sum => Limits.A + Limits.B;
}

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sword-config-{Guid.NewGuid():N}");

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Layers_CodeFileUserEnvSet_InOrder()
    {
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), """{ "arena": { "radius": 50, "enemies": 3 } }""");
        File.WriteAllText(Path.Combine(_dir, ConfigSet.UserFileName), """{ "arena": { "enemies": 4 } }""");
        var env = new Dictionary<string, string> { ["SWORD_ARENA_ENEMIES"] = "5", ["SWORD_CONTENT"] = "x.swpak" };
        var nested = Path.Combine(_dir, "bin", "Debug");
        Directory.CreateDirectory(nested);

        var c = ConfigSet.Load(nested, [], k => env.GetValueOrDefault(k));
        var a = c.Section<ArenaSettings>("arena");
        Assert.Equal(50, a.Radius);      // файл перекрыл код
        Assert.Equal(5, a.Enemies);      // переменная перекрыла user-файл

        var withSet = ConfigSet.Load(nested, ["arena.enemies=7"], k => env.GetValueOrDefault(k));
        Assert.Equal(7, withSet.Section<ArenaSettings>("arena").Enemies);   // --set — последний
        Assert.Contains(withSet.Sources, s => s.EndsWith(ConfigSet.FileName));
        Assert.Contains("--set arena.enemies=7", withSet.Sources);
        Assert.Equal(7, withSet.Effective["arena"]!["enemies"]!.GetValue<int>());
    }

    [Fact]
    public void NestedObjects_MergeKeyByKey_AcrossLayers()
    {
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), """{ "nest": { "limits": { "b": 5 } } }""");
        var env = new Dictionary<string, string> { ["SWORD_NEST_LIMITS_B"] = "6" };
        var c = ConfigSet.Load(_dir, [], k => env.GetValueOrDefault(k));
        var s = c.Section<NestSettings>("nest");
        Assert.Equal(7, s.Limits.A);   // значение в коде не потерялось под файлом
        Assert.Equal(6, s.Limits.B);   // переменная вложенного ключа

        var set = ConfigSet.Load(_dir, ["nest.limits.a=3"], _ => null).Section<NestSettings>("nest");
        Assert.Equal(3, set.Limits.A);
        Assert.Equal(5, set.Limits.B); // файл не потерялся под --set

        Assert.Contains("нет ключа typo", Assert.Throws<InvalidDataException>(() =>
            ConfigSet.Empty("nest.limits.typo=1").Section<NestSettings>("nest")).Message);
        Assert.Contains("нет ключа sum", Assert.Throws<InvalidDataException>(() =>
            ConfigSet.Empty("nest.sum=100").Section<NestSettings>("nest")).Message);   // вычисляемое — не ключ
    }

    [Fact]
    public void KeyCase_DoesNotMatter_LastWins()
    {
        Assert.Equal(5, ConfigSet.Empty("arena.enemies=4", "arena.Enemies=3", "arena.ENEMIES=5").Section<ArenaSettings>("arena").Enemies);
        Assert.True(ConfigSet.Empty("nest.flag=True").Section<NestSettings>("nest").Flag);

        // Другой регистр в файле пользователя — та же секция: не объект поверх объекта — ошибка, а не тихий дубль.
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), """{ "arena": { "enemies": 3 } }""");
        File.WriteAllText(Path.Combine(_dir, ConfigSet.UserFileName), """{ "ARENA": 5 }""");
        Assert.Contains("ожидается объект", Assert.Throws<InvalidDataException>(() =>
            ConfigSet.Load(_dir, [], _ => null).Section<ArenaSettings>("arena")).Message);
    }

    [Theory]
    [InlineData("""{ "sim": null }""", "ожидается объект")]
    [InlineData("""{ "sim": { "tickRate": 60, "tickRate": 30 } }""", "tickRate")]
    [InlineData("""{ "sim": { "tickRate": 60, "TickRate": 30 } }""", "дважды")]
    public void BadFileShapes_AreErrorsWithFilePath(string json, string expected)
    {
        File.WriteAllText(Path.Combine(_dir, ConfigSet.FileName), json);
        var ex = Assert.Throws<InvalidDataException>(() =>
            ConfigSet.Load(_dir, [], _ => null).Section<SimSettings>("sim"));
        Assert.Contains(expected, ex.Message);
        if (!json.Contains("null")) Assert.Contains(ConfigSet.FileName, ex.Message);
    }

    [Fact]
    public void Complete_ChecksUnknownSections_ThenFreezes()
    {
        var env = new Dictionary<string, string> { ["SWORD_ARENA_ENEMIES"] = "5" };
        var c = ConfigSet.Load(null, ["arena.radius=10"], k => env.GetValueOrDefault(k));
        c.Section<ArenaSettings>("arena");
        c.Section<ArenaSettings>("arena");
        Assert.Single(c.Sources, s => s == "SWORD_ARENA_ENEMIES=5");   // без повторов
        c.Complete();
        Assert.Throws<InvalidOperationException>(() => c.ApplySet("arena.enemies=1"));
        Assert.Throws<InvalidOperationException>(() => c.Section<ArenaSettings>("arena"));

        var unknown = ConfigSet.Empty("arnea.radius=10");
        Assert.Contains("неизвестные секции arnea", Assert.Throws<InvalidDataException>(unknown.Complete).Message);
    }

    [Fact]
    public void ExeWithoutClient_SkipsClientSections_ButNotTypos()
    {
        var c = ConfigSet.Empty("input.move_forward=Up");
        c.SkipClientSections();
        c.Complete();   // Headless: секция привязок из общего конфига — не ошибка
        Assert.Contains("input", c.Skipped);

        var typo = ConfigSet.Empty("inpt.move_forward=Up");
        typo.SkipClientSections();
        Assert.Contains("неизвестные секции inpt", Assert.Throws<InvalidDataException>(typo.Complete).Message);
    }

    [Fact]
    public void UnknownKeyAndUnknownSection_AreErrors()
    {
        var typo = ConfigSet.Empty("arena.radus=10");
        Assert.Contains("нет ключа radus", Assert.Throws<InvalidDataException>(() => typo.Section<ArenaSettings>("arena")).Message);

        var section = ConfigSet.Empty("arnea.radius=10");
        section.Section<ArenaSettings>("arena");
        Assert.Contains("неизвестные секции arnea", Assert.Throws<InvalidDataException>(section.ThrowOnUnknown).Message);
    }
}

public class SimulationTests
{
    private static readonly string[] Stages = ["Begin", "Intent", "Move", "End"];

    /// <summary>Модуль-проба: копит пинги, увиденные в Intent, со своим тиком.</summary>
    private sealed class PingModule(string id = "ping") : IModule, IContinuousSystem
    {
        public readonly List<(ulong Tick, int Value)> Seen = [];

        public string Id => id;

        public void Configure(SimulationBuilder b)
        {
            b.Command<Ping>().System("Intent", this).Export<Event<Ping>>();
        }

        public void Setup(SystemSetup s) => s.Label(new SystemLabel(id)).ReadsJournal<Event<Ping>>();

        public void Run(in SystemContext ctx)
        {
            foreach (var p in ctx.Journal<Event<Ping>>()) Seen.Add((ctx.Tick, p.Value.Value));
        }
    }

    [Fact]
    public void TickCommand_BecomesEventOfItsTick()
    {
        var m = new PingModule();
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [m]);
        sim.Enqueue(new Ping { Value = 1 });            // тик 0
        sim.Enqueue(3, new Ping { Value = 3 });         // тик 3
        for (int i = 0; i < 5; i++) sim.Step();
        Assert.Equal([(0ul, 1), (3ul, 3)], m.Seen);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Enqueue(2, new Ping()));
        Assert.Equal(5ul, sim.Tick);
        // DuelArena.Tick SWORD: Time += dt во float в начале тика — в последнем (4-м) тике сумма пяти шагов.
        float time = 0;
        for (int i = 0; i < 5; i++) time += sim.Dt;
        Assert.Equal(time, sim.World.Resource<SimTime>().Time);
        Assert.Equal(4ul, sim.World.Resource<SimTime>().Tick);
    }

    [Fact]
    public void RawCommand_OfTool_ComesAfterClientCommands_OfItsTick()
    {
        // Путь инструментов (агент): команда байтами по типу. В тике она выдаётся после команд клиента — где действует
        // последняя команда тика, побеждает инструмент.
        var m = new PingModule();
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [m]);
        Assert.Equal([typeof(Ping)], sim.CommandTypes);
        byte[] tool = BitConverter.GetBytes(99);
        sim.EnqueueRaw(typeof(Ping), 0, tool);
        sim.Enqueue(new Ping { Value = 1 });            // клиент — позже по времени постановки, но раньше в тике
        sim.EnqueueRaw(typeof(Ping), 1, tool);
        sim.Step();
        sim.Step();
        Assert.Equal([(0ul, 1), (0ul, 99), (1ul, 99)], m.Seen);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.EnqueueRaw(typeof(Ping), 1, tool));        // тик прошёл
        Assert.Throws<ArgumentException>(() => sim.EnqueueRaw(typeof(Ping), 5, new byte[3]));            // не тот размер
        Assert.Throws<InvalidOperationException>(() => sim.EnqueueRaw(typeof(Pong), 5, new byte[8]));    // не команда
    }

    [Fact]
    public void SystemRegistration_RecordsCallerSource()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [new PingModule()]);
        var ping = sim.World.Systems.Single(s => s.Label == "ping");
        Assert.Contains("RuntimeTests.cs:", ping.Source);   // строка вызова SimulationBuilder.System
        Assert.Equal("ping", ping.Module);
    }

    [Fact]
    public void Export_FramesWithZeroAndThreeTicks()
    {
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [new PingModule()]);
        // Кадр без тиков: экспортировать нечего.
        sim.Export.BeginFrame();
        Assert.True(sim.Export.Frame<Event<Ping>>().IsEmpty);

        // Кадр из трёх тиков: записи всех трёх с номерами тиков.
        sim.Export.BeginFrame();
        for (int i = 0; i < 3; i++)
        {
            sim.Enqueue(new Ping { Value = 10 + i });
            sim.Step();
        }
        Assert.Equal([(0ul, 10), (1ul, 11), (2ul, 12)], Records(sim.Export.Frame<Event<Ping>>()));

        // Следующий кадр без тиков — пусто: записи прошлого кадра не повторяются.
        sim.Export.BeginFrame();
        Assert.True(sim.Export.Frame<Event<Ping>>().IsEmpty);
    }

    private static List<(ulong, int)> Records(ReadOnlySpan<(ulong Tick, Event<Ping> Record)> frame)
    {
        var list = new List<(ulong, int)>();
        foreach (var (tick, r) in frame) list.Add((tick, r.Value.Value));
        return list;
    }

    /// <summary>Модуль на краях тика: команду читает первая стадия игры, событие пишет последняя.</summary>
    private sealed class EdgeModule : IModule
    {
        public readonly List<(ulong Tick, int Value)> SeenInFirstStage = [];

        public string Id => "edge";

        public void Configure(SimulationBuilder b) =>
            b.Command<Ping>().Event<Pong>()
                .System("Begin", new Probe(this))
                .System("End", new Producer())
                .Export<Event<Pong>>();

        private sealed class Probe(EdgeModule m) : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("probe")).ReadsJournal<Event<Ping>>();

            public void Run(in SystemContext ctx)
            {
                foreach (var p in ctx.Journal<Event<Ping>>()) m.SeenInFirstStage.Add((ctx.Tick, p.Value.Value));
            }
        }

        private sealed class Producer : IContinuousSystem
        {
            public void Setup(SystemSetup s) => s.Label(new SystemLabel("producer")).Emits<Pong>();

            public void Run(in SystemContext ctx) => ctx.Commands.Emit(new Pong { Tick = ctx.Tick });
        }
    }

    [Fact]
    public void TickEdges_CommandSeenByFirstStage_LastStageEventExportedInItsOwnTick()
    {
        var m = new EdgeModule();
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, [m]);

        // Один тик и остановка: событие последней стадии игры уже в экспорте, с номером своего тика.
        sim.Export.BeginFrame();
        sim.Enqueue(new Ping { Value = 7 });
        sim.Step();
        var first = sim.Export.Frame<Event<Pong>>();
        Assert.Equal(1, first.Length);
        Assert.Equal(0ul, first[0].Tick);
        Assert.Equal(0ul, first[0].Record.Value.Tick);
        // Команду тика 0 первая стадия игры увидела в тике 0.
        Assert.Equal([(0ul, 7)], m.SeenInFirstStage);

        // Кадр из трёх тиков: номер тика в очереди совпадает с тиком, в котором событие записано.
        sim.Export.BeginFrame();
        for (int i = 0; i < 3; i++) sim.Step();
        foreach (var (tick, record) in sim.Export.Frame<Event<Pong>>()) Assert.Equal(record.Value.Tick, tick);
        Assert.Equal(3, sim.Export.Frame<Event<Pong>>().Length);
    }

    /// <summary>Модуль, который держит построитель и регистрирует после сборки мира.</summary>
    private sealed class LateModule : IModule
    {
        public string Id => "late";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(_ => b.Command<Pong>());
    }

    /// <summary>Модуль, который пишет в журнал порядок начального наполнения.</summary>
    private sealed class OrderModule(List<string> order) : IModule
    {
        public string Id => "order";

        public void Configure(SimulationBuilder b) =>
            b.OnWorldReady(_ => order.Add("module.ready")).OnWorldBuilt(_ => order.Add("module.built"));
    }

    [Fact]
    public void OnWorldReady_RunsAfterEveryOnWorldBuilt_OfCoreAndModules()
    {
        // Ядро ставит тело бойца по ресурсу, который модуль задаёт при сборке мира (5в): готовность — после всех сборок.
        var order = new List<string>();
        var sim = Simulation.Build(ConfigSet.Empty(), Stages, b =>
        {
            b.Component<Counter>();
            b.OnWorldReady(w => order.Add("core.ready")).OnWorldBuilt(w => order.Add("core.built"));
        }, [new OrderModule(order)]);
        Assert.Equal(["core.built", "module.built", "core.ready", "module.ready"], order);
        Assert.Equal(0UL, sim.Tick);
    }

    [Fact]
    public void Builder_IsSealedAfterWorldBuild()
    {
        Assert.Contains("уже собран", Assert.Throws<InvalidOperationException>(() =>
            Simulation.Build(ConfigSet.Empty(), Stages, [new LateModule()])).Message);
    }

    [Fact]
    public void OwnerSeesWorldReadOnly()
    {
        // У владельца симуляции тоже только чтение: менять её на ходу — командами тика.
        Assert.Equal(typeof(WorldReader), typeof(Simulation).GetProperty(nameof(Simulation.World))!.PropertyType);
    }

    [Fact]
    public void RuntimeStages_AreNotForModules()
    {
        Assert.Throws<ArgumentException>(() => Simulation.Build(ConfigSet.Empty(), [Simulation.CommandStage, "Begin"], []));
        Assert.Contains("нет стадии игры", Assert.Throws<ArgumentException>(() =>
            Simulation.Build(ConfigSet.Empty(), Stages, [new StrayModule()])).Message);
    }

    private sealed class StrayModule : IModule, IContinuousSystem
    {
        public string Id => "stray";

        public void Configure(SimulationBuilder b) => b.System(Simulation.ExportStage, this);

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("stray"));

        public void Run(in SystemContext ctx) { }
    }

    [Fact]
    public void DisabledModule_RegistersNothing_UnknownModuleIsAnError()
    {
        var on = new PingModule("on");
        var off = new PingModule("off");
        var sim = Simulation.Build(ConfigSet.Empty("modules.off.enabled=false"), Stages, [on, off]);
        Assert.Equal(["on"], sim.ActiveModules);
        Assert.Equal(["off"], sim.InactiveModules);
        Assert.DoesNotContain(sim.World.Systems, s => s.Module == "off");
        Assert.Contains(sim.World.Systems, s => s.Module == "on");

        Assert.Contains("modules.missing", Assert.Throws<InvalidDataException>(() =>
            Simulation.Build(ConfigSet.Empty("modules.missing.enabled=false"), Stages, [new PingModule()])).Message);
    }

    [Fact]
    public void ModuleSwitch_GoesThroughAllLayers_AndIsStrict()
    {
        // Переменная окружения — тот же слой, что у типизированных секций.
        var env = new Dictionary<string, string> { ["SWORD_MODULES_OFF_ENABLED"] = "false" };
        var sim = Simulation.Build(ConfigSet.Load(null, [], k => env.GetValueOrDefault(k)), Stages,
            [new PingModule("on"), new PingModule("off")]);
        Assert.Equal(["off"], sim.InactiveModules);
        Assert.Contains("SWORD_MODULES_OFF_ENABLED=false", sim.Config.Sources);
        Assert.False(sim.Config.Effective["modules"]!["off"]!["enabled"]!.GetValue<bool>());
        Assert.True(sim.Config.Effective["modules"]!["on"]!["enabled"]!.GetValue<bool>());

        // --set перекрывает переменную.
        var back = Simulation.Build(ConfigSet.Load(null, ["modules.off.enabled=true"], k => env.GetValueOrDefault(k)), Stages,
            [new PingModule("on"), new PingModule("off")]);
        Assert.Empty(back.InactiveModules);

        // Опечатка в ключе модуля — ошибка, а не молча включённый модуль.
        Assert.Contains("нет ключа enabeld", Assert.Throws<InvalidDataException>(() =>
            Simulation.Build(ConfigSet.Empty("modules.on.enabeld=false"), Stages, [new PingModule("on")])).Message);
        // Не объект ключей — тоже ошибка.
        Assert.Contains("ожидается объект", Assert.Throws<InvalidDataException>(() =>
            Simulation.Build(ConfigSet.Empty("modules.on=false"), Stages, [new PingModule("on")])).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]      // точка делит путь конфига: modules.a.b.enabled не нашёл бы модуль
    [InlineData("Core")]     // регистр: ключи конфига без учёта регистра
    [InlineData("ядро")]
    [InlineData("1st")]
    [InlineData("a-b")]      // дефис не годится в имя переменной окружения
    [InlineData("_a")]
    [InlineData("sim")]      // секции движка
    [InlineData("modules")]
    [InlineData("input")]    // секция клиента движка
    public void ModuleId_OutsideRule_IsAnError(string id) =>
        Assert.Contains("Id", Assert.Throws<InvalidOperationException>(() =>
            Simulation.Build(ConfigSet.Empty(), Stages, [new PingModule(id)])).Message);

    [Fact]
    public void ModuleId_WithDigitsAndUnderscore_IsSwitchable()
    {
        var sim = Simulation.Build(ConfigSet.Empty("modules.ping_2.enabled=false"), Stages, [new PingModule("ping_2")]);
        Assert.Equal(["ping_2"], sim.InactiveModules);
    }

    /// <summary>Модуль с настройками — секция с его Id.</summary>
    private sealed class ArenaModule : IModule
    {
        public ArenaSettings? Settings;

        public string Id => "arena";

        public void Configure(SimulationBuilder b) => Settings = b.Settings<ArenaSettings>();
    }

    [Fact]
    public void ModuleSettings_SectionIsItsId_DisabledModuleSectionIsSkipped()
    {
        var on = new ArenaModule();
        Simulation.Build(ConfigSet.Empty("arena.enemies=3"), Stages, [on]);
        Assert.Equal(3, on.Settings!.Enemies);

        // Выключенный модуль с настройками: запуск не падает на «неизвестной секции».
        var off = new ArenaModule();
        var sim = Simulation.Build(ConfigSet.Empty("arena.enemies=3", "modules.arena.enabled=false"), Stages, [off]);
        Assert.Null(off.Settings);
        Assert.Contains("arena", sim.Config.Skipped);

        // Опечатка в ключе включённого модуля по-прежнему ошибка.
        Assert.Throws<InvalidDataException>(() => Simulation.Build(ConfigSet.Empty("arena.enemis=3"), Stages, [new ArenaModule()]));
    }

    [Fact]
    public void Accepts_OnlyCommandsOfActiveModules()
    {
        var sim = Simulation.Build(ConfigSet.Empty("modules.ping.enabled=false"), Stages, [new PingModule()]);
        Assert.False(sim.Accepts<Ping>());
        Assert.Throws<InvalidOperationException>(() => sim.Enqueue(new Ping()));
        Assert.True(Simulation.Build(ConfigSet.Empty(), Stages, [new PingModule()]).Accepts<Ping>());
    }

    [Fact]
    public void TickRate_FromConfig()
    {
        var sim = Simulation.Build(ConfigSet.Empty("sim.tickRate=60"), Stages, []);
        Assert.Equal(1f / 60, sim.Dt);
        Assert.Contains(sim.World.Systems, s => s.Name == "runtime.commands@" + Simulation.CommandStage);
        Assert.Contains(sim.World.Systems, s => s.Name == "runtime.export@" + Simulation.ExportStage);
        Assert.Equal([Simulation.CommandStage, .. Stages, Simulation.ExportStage], sim.World.Stages);
    }
}
