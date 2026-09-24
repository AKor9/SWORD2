using System.Numerics;
using System.Text.Json.Nodes;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests;

public class FieldCodecTests
{
    private static byte[] Bytes<T>(T value) where T : unmanaged
    {
        var bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    private static T Value<T>(byte[] bytes) where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.Read<T>(bytes);

    [Fact]
    public void Decode_AllKinds_ByStableFieldNames()
    {
        var e = new Everything
        {
            V = new Vector3(1, 2, 3), Q = Quaternion.Identity, Gait = Gait.Run, Flag = true, Letter = 'ж', Big = ulong.MaxValue,
            Small = -5, Precise = 0.1, NaN = float.NaN, Who = new Entity(12, 3), Auto = 42,
        };
        e.Arr[0] = 1.5f;
        e.Arr[2] = -2;
        // Как агент: ответ — текстом.
        var json = JsonNode.Parse(FieldCodec.Decode(typeof(Everything), Bytes(e))!.ToJsonString())!.AsObject();
        Assert.Equal(2f, json["V"]!["Y"]!.GetValue<float>());
        Assert.Equal(1f, json["Q"]!["W"]!.GetValue<float>());
        Assert.Equal("Run", json["Gait"]!.GetValue<string>());
        Assert.True(json["Flag"]!.GetValue<bool>());
        Assert.Equal("ж", json["Letter"]!.GetValue<string>());
        Assert.Equal(ulong.MaxValue, json["Big"]!.GetValue<ulong>());
        Assert.Equal(-5, json["Small"]!.GetValue<int>());
        Assert.Equal("NaN", json["NaN"]!.GetValue<string>());        // в JSON нечисел нет
        Assert.Equal("12v3", json["Who"]!.GetValue<string>());
        Assert.Equal([1.5f, 0f, -2f], json["Arr"]!.AsArray().Select(n => n!.GetValue<float>()));
        Assert.Equal(42, json["Auto"]!.GetValue<int>());              // автосвойство — именем свойства
    }

    [Fact]
    public void Encode_RoundTrips_AndKeepsBaselineForMissingFields()
    {
        var original = new Everything { V = new Vector3(1, 2, 3), Gait = Gait.Walk, Big = 7, Who = new Entity(4, 1), NaN = float.PositiveInfinity };
        original.Arr[1] = 3;
        var json = FieldCodec.Decode(typeof(Everything), Bytes(original));
        Assert.Equal(Bytes(original), FieldCodec.Encode(typeof(Everything), json));

        // Заданные поля — из JSON (регистр не важен), остальные — из основы.
        var changed = Value<Everything>(FieldCodec.Encode(typeof(Everything),
            JsonNode.Parse("""{"v":{"x":9},"gait":"run","arr":[5]}"""), Bytes(original)));
        Assert.Equal(new Vector3(9, 2, 3), changed.V);
        Assert.Equal(Gait.Run, changed.Gait);
        Assert.Equal(5f, changed.Arr[0]);
        Assert.Equal(3f, changed.Arr[1]);
        Assert.Equal(7ul, changed.Big);
        Assert.True(float.IsPositiveInfinity(changed.NaN));

        // Без основы — нули.
        var order = Value<Order>(FieldCodec.Encode(typeof(Order), JsonNode.Parse("""{"Move":{"X":1},"Target":"3v2","Gait":2}""")));
        Assert.Equal(new Order { Move = new Vector2(1, 0), Target = new Entity(3, 2), Gait = Gait.Run }, order);
    }

    [Theory]
    [InlineData("""{"Mov":1}""", "нет поля «Mov»")]
    [InlineData("""{"Gait":"Fly"}""", "нет значения «Fly»")]
    [InlineData("""{"Gait":"Walk,Run"}""", "нет значения")]
    [InlineData("""{"Priority":300}""", "Order.Priority")]
    [InlineData("""{"Hurry":1}""", "Order.Hurry")]
    [InlineData("""{"Target":"12"}""", "12v3")]
    [InlineData("""{"Move":5}""", "Order.Move: ожидается объект")]
    [InlineData("""[1]""", "ожидается объект")]
    public void Encode_RejectsWrongFields_WithPath(string json, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => FieldCodec.Encode(typeof(Order), JsonNode.Parse(json))).Message);

    [Fact]
    public void Schema_DescribesFieldsForAgent()
    {
        var s = FieldCodec.Schema(typeof(Order)).AsObject();
        Assert.Equal("number", s["Move"]!["X"]!.GetValue<string>());
        Assert.Equal("enum: Stand | Walk | Run", s["Gait"]!.GetValue<string>());
        Assert.StartsWith("entity", s["Target"]!.GetValue<string>());
        Assert.Equal(["Move", "Hurry", "Gait", "Target", "Priority"], FieldCodec.Fields(typeof(Order)).Select(f => f.Name));
    }
}

public class CatalogAndQueryTests
{
    private readonly Simulation _sim = TestModule.Build();
    private Catalog Catalog => new(_sim.World, _sim.CommandTypes);

    [Fact]
    public void Names_FullOrShort_CaseInsensitive_WithHelpfulErrors()
    {
        Assert.Equal(typeof(Body), Catalog.Component("body").Type);
        Assert.Equal(typeof(Body), Catalog.Component(typeof(Body).FullName!).Type);
        Assert.Equal(typeof(Order), Catalog.Command("Order"));
        var e = Assert.Throws<ArgumentException>(() => Catalog.Component("Bodie"));
        Assert.Contains("Body", e.Message);
        Assert.Equal("Added<Body>", Catalog.ShortName(typeof(Added<Body>)));
    }

    [Fact]
    public void Query_SelectsPagesByIndex_ProjectsFields()
    {
        var ctx = new ServiceContext(_sim);
        var body = Catalog.Component("Body").Id;
        var marker = Catalog.Component("Marker").Id;
        var page = WorldQuery.Copy(ctx, [body], [marker], null, skip: 1, take: 1, names: null);
        Assert.Equal(2, page.Total);                       // без метки — сущности 1 и 3
        var row = Assert.Single(page.Rows);
        Assert.Equal(3, row.Entity.Index);
        var json = WorldQuery.ToJson(row, Catalog, ["Body.Position.X"]);
        Assert.Equal(3f, json["components"]!["Body"]!["Position.X"]!.GetValue<float>());

        var tagged = WorldQuery.CopyOne(ctx, new Entity(0, 1), null, names: null)!;
        var all = WorldQuery.ToJson(tagged, Catalog);
        Assert.True(all["components"]!["Marker"]!.GetValue<bool>());   // тег — true
        Assert.Null(WorldQuery.CopyOne(ctx, new Entity(99, 1), null, null));
    }
}

public class ArchitectureMapTests
{
    private readonly Simulation _sim = TestModule.Build();

    [Fact]
    public void ExplainSystem_ShowsDeclarations_SourceAndNeighbours()
    {
        var systems = _sim.World.Systems;
        var mover = ArchitectureMap.ExplainSystem(systems, "mover");
        Assert.Equal("test.mover@S1", mover["system"]!.GetValue<string>());
        Assert.Equal("test", mover["module"]!.GetValue<string>());
        Assert.Contains(typeof(Body).FullName!, mover["writes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.EndsWith("TestWorld.cs", mover["source"]!.GetValue<string>().Split(':')[0]);   // место регистрации
        // Вход — команды тика (раньше в этом тике), выход — чтение test.reader (позже в этом тике).
        var up = mover["upstream"]!.AsArray().Select(n => (n!["system"]!.GetValue<string>(), n["when"]!.GetValue<string>())).ToList();
        Assert.Contains(("runtime.commands@runtime.commands", "в этом тике, раньше"), up);
        var down = mover["downstream"]!.AsArray().Select(n => (n!["system"]!.GetValue<string>(), n["when"]!.GetValue<string>())).ToList();
        Assert.Contains(("test.reader@S2", "в этом тике, позже"), down);
    }

    [Fact]
    public void FindSystem_ExactThenUniquePart_ElseListsCandidates()
    {
        var systems = _sim.World.Systems;
        Assert.Equal("test.reader@S2", ArchitectureMap.FindSystem(systems, "Reader").Name);   // тип — точно
        Assert.Contains("неоднозначна", Assert.Throws<ArgumentException>(() => ArchitectureMap.FindSystem(systems, "test.")).Message);
        Assert.Contains("нет системы", Assert.Throws<ArgumentException>(() => ArchitectureMap.FindSystem(systems, "zzz")).Message);
    }

    [Fact]
    public void ComponentUsage_Plan_Modules()
    {
        var systems = _sim.World.Systems;
        var usage = ArchitectureMap.ComponentUsage(systems, typeof(Order).FullName!)["systems"]!.AsArray();
        Assert.Contains(usage, u => u!["system"]!.GetValue<string>() == "test.mover@S1" && u["roles"]![0]!.GetValue<string>() == "consumes");
        Assert.Contains(usage, u => u!["system"]!.GetValue<string>() == "runtime.commands@runtime.commands");
        var hooks = ArchitectureMap.ComponentUsage(systems, typeof(Marker).FullName!, _sim.World.Hooks)["hooks"]!.AsArray();
        Assert.Equal(("OnAdd", "test"), (hooks.Single()!["hook"]!.GetValue<string>(), hooks.Single()!["module"]!.GetValue<string>()));
        Assert.Empty(ArchitectureMap.ComponentUsage(systems, typeof(Body).FullName!, _sim.World.Hooks)["hooks"]!.AsArray());
        var plan = ArchitectureMap.Plan(_sim.World.Stages, systems)["stages"]!.AsArray();
        Assert.Equal(["runtime.commands", "S1", "S2", "runtime.export"], plan.Select(s => s!["stage"]!.GetValue<string>()));
        var modules = ArchitectureMap.ModuleGraph(systems, _sim.ActiveModules, _sim.InactiveModules);
        Assert.Equal("test", modules["active"]![0]!.GetValue<string>());
    }

    [Fact]
    public void RelativeSource_FromRepositoryRoot()
    {
        string file = Path.Combine(Path.GetTempPath(), "x.cs");
        Assert.Equal(file + ":7", ArchitectureMap.RelativeSource(file + ":7"));   // вне репозитория — как есть
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SWORD.slnx"))) repo = d.FullName;
        if (repo is null) return;
        Assert.Equal("engine/X.cs:3", ArchitectureMap.RelativeSource(Path.Combine(repo, "engine", "X.cs") + ":3"));
    }
}

public class ServiceAndTimeTests
{
    [Fact]
    public async Task ServiceQueue_RunsOnServingThread_PropagatesErrors_RespectsBudget()
    {
        var sim = TestModule.Build();
        var queue = new ServiceQueue();
        int servingThread = Environment.CurrentManagedThreadId;
        var a = queue.Post(ctx => (Environment.CurrentManagedThreadId, ctx.Tick));
        var b = queue.Post<int>(_ => throw new InvalidOperationException("boom"));
        var slow = queue.Post(_ =>
        {
            Thread.Sleep(150);
            return 1;
        });
        var late = queue.Post(_ => 2);
        Assert.Equal(4, queue.Pending);
        // Бюджет с запасом: первое исключение в процессе (b) бывает медленным — бюджет должен кончиться на медленном, а не раньше.
        Assert.Equal(3, queue.Serve(new ServiceContext(sim), TimeSpan.FromMilliseconds(75)));   // после медленного бюджет кончился
        Assert.Equal((servingThread, 0ul), await a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal(1, await slow);
        Assert.False(late.IsCompleted);
        queue.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
    }

    [Fact]
    public void ServiceContext_EnqueuesOnlyFutureTicks()
    {
        var sim = TestModule.Build();
        var ctx = new ServiceContext(sim);
        byte[] order = FieldCodec.Encode(typeof(Order), JsonNode.Parse("""{"Move":{"X":1}}"""));
        ctx.Enqueue(typeof(Order), sim.Tick, order);
        sim.Step();
        Assert.Throws<ArgumentOutOfRangeException>(() => ctx.Enqueue(typeof(Order), 0, order));
        Assert.Equal(1f, sim.World.Get<Body>(new Entity(0, 1)).Position.X);
    }

    [Fact]
    public async Task TimeControl_PauseStepRunUntil()
    {
        var time = new TimeControl(paused: true);
        Assert.Equal(0, time.Allow(8));                  // пауза — стоим
        ulong target = time.Step(5);
        Assert.Equal(5ul, target);
        Assert.Equal(5, time.Allow(8));
        Assert.Equal(3, time.Allow(3));
        var reached = time.WhenReached(5);
        time.Ran(3);
        Assert.False(reached.IsCompleted);
        Assert.Equal(2, time.Allow(8));
        time.Ran(5);
        Assert.Equal(5ul, await reached);
        Assert.Equal(0, time.Allow(8));

        time.RunUntil(9);
        Assert.Equal(4ul, time.Remaining);
        time.Pause();                                    // пауза снимает запас
        Assert.Equal(0ul, time.Remaining);
        time.Resume();
        Assert.Equal(8, time.Allow(8));                  // без паузы — по часам
        Assert.True(time.WhenReached(1).IsCompleted);    // прошедший тик — сразу
    }
}
