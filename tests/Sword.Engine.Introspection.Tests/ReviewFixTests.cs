using System.Text.Json.Nodes;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Introspection.Tests
{
    [Flags]
    public enum Parts : ushort { None = 0, A = 1, B = 2, C = 4 }

    public struct Odd
    {
        public nint Handle;
        public nuint Size;
        public Parts Parts;
        public long Count;
    }

    public struct WithNullable
    {
        public int? Maybe;
    }

    public struct Empty;

    /// <summary>Проверки: отмена, время, разбор, связи систем, имена, журнал.</summary>
    public class ReviewFixTests
    {
        [Fact]
        public async Task CancelledWork_NeverRuns_AndQueueClosesOnCancelAll()
        {
            var sim = TestModule.Build();
            var queue = new ServiceQueue();
            int runs = 0;
            using var cts = new CancellationTokenSource();
            var cancelled = queue.Post(_ => ++runs, cts.Token);
            cts.Cancel();                                   // таймаут агента — до точки обслуживания
            queue.Serve(new ServiceContext(sim), TimeSpan.FromSeconds(1));
            Assert.Equal(0, runs);                          // команда, на которую агент получил ошибку, не прошла
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            queue.CancelAll();
            var late = queue.Post(_ => ++runs);
            Assert.True(late.IsCanceled);                   // симуляция остановлена — сразу отказ, не ожидание
        }

        [Fact]
        public async Task Time_ResumeDropsOldBudget_GateIsAtomic_CancelledWaitLeaves()
        {
            var time = new TimeControl(paused: true);
            time.Step(1000);
            time.Ran(20);
            time.Resume();
            Assert.Null(time.Gate(8));                      // без паузы — по часам
            Assert.Equal(0ul, time.Remaining);
            Assert.Equal(25ul, time.Step(5));               // от сыгранного, а не от старой цели 1000
            time.Pause();
            Assert.Equal(0, time.Gate(8));

            using var cts = new CancellationTokenSource();
            var wait = time.WhenReached(1_000_000, cts.Token);
            Assert.Equal(1, time.Waiters);
            cts.Cancel();                                   // таймаут вызывающего
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            Assert.Equal(0, time.Waiters);                  // ожидание убрано сразу
        }

        [Fact]
        public void FieldCodec_Integers_Flags_NativeInts_PlainDataOnly()
        {
            Assert.Contains("не целое", Assert.Throws<ArgumentException>(() =>
                FieldCodec.Encode(typeof(Order), JsonNode.Parse("""{"Priority":2.5}"""))).Message);
            var bytes = FieldCodec.Encode(typeof(Odd), JsonNode.Parse("""{"Handle":-7,"Size":9,"Parts":"A, C","Count":5.0}"""));
            var json = FieldCodec.Decode(typeof(Odd), bytes)!;
            Assert.Equal(-7, json["Handle"]!.GetValue<long>());
            Assert.Equal("A, C", json["Parts"]!.GetValue<string>());   // [Flags] — списком, и обратно
            Assert.Equal(bytes, FieldCodec.Encode(typeof(Odd), JsonNode.Parse(json.ToJsonString())));
            Assert.Contains("не чистые данные", Assert.Throws<ArgumentException>(() => FieldCodec.Decode(typeof(WithNullable), new byte[8])).Message);
            Assert.Throws<ArgumentException>(() => FieldCodec.Encode(typeof(string), null));
            Assert.Equal("{}", FieldCodec.Decode(typeof(Empty), [])!.ToJsonString());   // тег: 0 байт в мире
        }

        [Fact]
        public void ExplainSystem_LinksByJournalKind()
        {
            // Запись колонки не порождает Removed, снятие не порождает Added: соседи — по виду входа.
            string body = typeof(Body).FullName!;
            var writer = new SystemInfo("w@S1", "S1", "w", 1, null, true, false, []) { Order = 0, Writes = [body] };
            var remover = new SystemInfo("r@S1", "S1", "r", 2, null, true, false, []) { Order = 1, Removes = [body] };
            var watcher = new SystemInfo("x@S2", "S2", "x", 3, null, true, true, ["Removed<Body>"])
            {
                ConsumesTriggers = [new TriggerInfo("Removed", body)], ConsumesData = [body],
            };
            var up = ArchitectureMap.ExplainSystem([writer, remover, watcher], "x")["upstream"]!.AsArray();
            var link = Assert.Single(up)!;
            Assert.Equal("r@S1", link["system"]!.GetValue<string>());
            Assert.Equal($"Removed:{body}", link["via"]![0]!.GetValue<string>());
            Assert.Equal("Order", Catalog.ShortName(typeof(Order)));
        }

        [Fact]
        public void LogRing_StaleCursorFromOtherProcess_ReadsFromStart()
        {
            var ring = new LogRing(4);
            for (int i = 0; i < 3; i++) ring.Write(new LogEntry(DateTime.UtcNow, LogLevel.Info, "t", $"m{i}"));
            var records = ring.Tail(after: 500, limit: 10, LogLevel.Debug, null, out long missed);
            Assert.Equal(3, records.Count);
            Assert.Equal(0, missed);
        }
    }
}

namespace Sword.Engine.Introspection.Tests.Other
{
    /// <summary>Второй Body — короткие имена совпали.</summary>
    public struct Body
    {
        public int Mark;
    }

    public class DuplicateNameTests
    {
        private sealed class Both : IModule
        {
            public string Id => "both";

            public void Configure(SimulationBuilder b) => b.Component<Tests.Body>().Component<Body>().OnWorldBuilt(w =>
            {
                var e = w.CreateImmediate();
                w.AddImmediate(e, new Tests.Body());
                w.AddImmediate(e, new Body { Mark = 7 });
            });
        }

        [Fact]
        public void SameShortName_ShownByFullName_NotOverwritten()
        {
            var sim = Simulation.Build(ConfigSet.Empty(), ["S"], [new Both()]);
            var catalog = new Catalog(sim.World, sim.CommandTypes);
            var row = WorldQuery.CopyOne(new ServiceContext(sim), new Entity(0, 1), null, null)!;
            var json = JsonNode.Parse(WorldQuery.ToJson(row, catalog)["components"]!.ToJsonString())!.AsObject();   // как агент — текстом
            Assert.Equal(2, json.Count);
            Assert.Equal(7, json[typeof(Body).FullName!]!["Mark"]!.GetValue<int>());
            Assert.Throws<ArgumentException>(() => catalog.Component("Body"));   // ввод — неоднозначен, с перечнем
        }
    }
}
