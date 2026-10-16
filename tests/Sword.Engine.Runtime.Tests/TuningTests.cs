using Sword.Engine.Ecs;
using Sword.Engine.Runtime;

namespace Sword.Engine.Runtime.Tests;

/// <summary>Правила модуля с ручками: число и флаг.</summary>
public struct KnobRules
{
    public float Speed;
    public bool Enabled;
}

/// <summary>Путь идущего объекта: скорость правил × время.</summary>
public struct KnobPath
{
    public float Value;
}

/// <summary>Что увидела система второй стадии в тике: скорость и флаг правил, сколько раз правила менялись (реактивно).</summary>
public struct KnobSeen
{
    public float Speed;
    public bool Enabled;
    public int Changes;
}

/// <summary>
/// Модуль с ручками секции <c>knobs</c> над ресурсом <see cref="KnobRules"/>: вторая стадия читает правила (в том же тике, что команда
/// ручки) и считает замены ресурса (<see cref="ResourceChanged{T}"/>), идущий объект набирает путь со скоростью правил.
/// </summary>
public sealed class KnobModule : IModule
{
    public string Id => "knobs";

    public void Configure(SimulationBuilder b)
    {
        b.Resource(new KnobRules { Speed = 1, Enabled = true }).Resource(new KnobSeen()).Resource(new KnobPath());
        b.Tuning<KnobRules>("knobs")
            .Float("speed", "скорость", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 0, 10, 1, hint: "м/с")
            .Bool("enabled", "включено", (in KnobRules r) => r.Enabled, (ref KnobRules r, bool v) => r.Enabled = v, true);
        b.System("B", new Reader()).System("B", new Counter());
    }

    private sealed class Reader : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("knobs.read")).SetsResource<KnobPath>();

        public void Run(in SystemContext ctx)
        {
            var rules = ctx.World.Resource<KnobRules>();
            ctx.Commands.SetResource(new KnobPath { Value = ctx.World.Resource<KnobPath>().Value + (rules.Enabled ? rules.Speed : 0) * ctx.World.Resource<SimTime>().Dt });
        }
    }

    private sealed class Counter : IReactiveSystem<ResourceChanged<KnobRules>>
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("knobs.count")).SetsResource<KnobSeen>();

        public void React(in SystemContext ctx, ReadOnlySpan<ResourceChanged<KnobRules>> changes)
        {
            var seen = ctx.World.Resource<KnobSeen>();
            var rules = ctx.World.Resource<KnobRules>();
            ctx.Commands.SetResource(new KnobSeen { Speed = rules.Speed, Enabled = rules.Enabled, Changes = seen.Changes + changes.Length });
        }
    }
}

/// <summary>
/// Ручки симуляции: изменение на ходу — команда <see cref="TuningCommand"/>, её применяет система секции в первой стадии того
/// же тика (реактивным — <see cref="ResourceChanged{T}"/>); пишется в реплей и воспроизводится.
/// </summary>
public class TuningTests
{
    private static readonly ReplayInfo Info = new("1.0.0+test", "tests", "none");

    private static Simulation Build(ConfigSet? config = null) => Simulation.Build(config ?? ConfigSet.Empty(), ["A", "B"], [new KnobModule()]);

    private static TuningCommand Tune(string knob, float value) => new() { Knob = TuningKey.Of(knob), Value = value };

    [Fact]
    public void Knobs_DescribedByName_ReadFromWorld()
    {
        var sim = Build();
        Assert.Equal(["knobs.speed", "knobs.enabled"], sim.Tuning.Select(k => k.Name));
        var speed = sim.Knob("KNOBS.Speed")!;
        Assert.Equal((TuningKind.Float, 0f, 10f, 1f, "Speed", typeof(KnobRules), "knobs"), (speed.Kind, speed.Min, speed.Max, speed.Default, speed.Field, speed.Resource, speed.Module));
        Assert.Equal("knobs.speed", speed.Id.Name);
        Assert.Equal(TuningKey.Of("knobs.speed"), speed.Id);
        Assert.True(TuningKey.TryParse("Knobs.Enabled", out var enabled) && enabled == sim.Knob("knobs.enabled")!.Id);
        Assert.False(TuningKey.TryParse("knobs.nope", out _));
        Assert.Equal(1f, speed.Read(sim.World));
        Assert.Equal(1f, sim.Knob("knobs.enabled")!.Read(sim.World));
        Assert.Contains(typeof(TuningCommand), sim.CommandTypes);
        // Система секции — в первой стадии игры, с токеном модуля.
        var system = sim.World.Systems.Single(s => s.Label == "tuning.knobs");
        Assert.Equal(("A", "knobs", true), (system.Stage, system.Module, system.Reactive));
    }

    [Fact]
    public void Command_AppliedSameTick_Clamped_ResourceChangedOnce()
    {
        var sim = Build();
        sim.Step();
        sim.Enqueue(Tune("knobs.speed", 25));   // выше предела — прижимается к 10
        sim.Enqueue(Tune("knobs.enabled", 0));
        sim.Step();
        var rules = sim.World.Resource<KnobRules>();
        Assert.Equal((10f, false), (rules.Speed, rules.Enabled));
        // Вторая стадия того же тика видела новые правила, реактивная — одну замену (две команды — одна замена ресурса).
        Assert.Equal(new KnobSeen { Speed = 10, Enabled = false, Changes = 1 }, sim.World.Resource<KnobSeen>());
        Assert.Equal(10f, sim.Knob("knobs.speed")!.Read(sim.World));
        Assert.Equal(0f, sim.Knob("knobs.enabled")!.Read(sim.World));

        sim.Enqueue(Tune("knobs.speed", -3));
        sim.Step();
        Assert.Equal(0f, sim.World.Resource<KnobRules>().Speed);
        Assert.Equal(2, sim.World.Resource<KnobSeen>().Changes);
    }

    [Fact]
    public void NotANumber_OrUnknownKnob_ChangesNothing()
    {
        var sim = Build();
        sim.Enqueue(Tune("knobs.speed", float.NaN));
        sim.Enqueue(Tune("knobs.speed", float.PositiveInfinity));
        sim.Enqueue(new TuningCommand { Knob = new TuningKey(12345), Value = 3 });
        sim.Step();
        Assert.Equal(new KnobRules { Speed = 1, Enabled = true }, sim.World.Resource<KnobRules>());
        Assert.Equal(0, sim.World.Resource<KnobSeen>().Changes);   // замены ресурса не было
    }

    [Fact]
    public void Replay_KnobChangedMidRun_SameHashes_AndChangesTheRun()
    {
        static (Replay Replay, float Path) Run(bool tune)
        {
            var sim = Build();
            var recorder = sim.Record(Info);
            for (int t = 0; t < 240; t++)
            {
                if (tune && t == 100) sim.Enqueue(Tune("knobs.speed", 4));
                if (tune && t == 180) sim.Enqueue(Tune("knobs.enabled", 0));
                sim.Step();
            }
            return (recorder.ToReplay(), sim.World.Resource<KnobPath>().Value);
        }

        var (tuned, path) = Run(tune: true);
        var (plain, plainPath) = Run(tune: false);
        Assert.NotEqual(plainPath, path);
        Assert.Equal(2, tuned.CommandCount);
        Assert.Contains(tuned.CommandTypes, t => t.Name == typeof(TuningCommand).FullName);
        Assert.NotEqual(plain.Hashes[^1].Parts[0], tuned.Hashes[^1].Parts[0]);

        // Файл → воспроизведение: команды ручек реплея — те же хеши на каждом тике.
        var replay = Replay.Parse(tuned.ToJson());
        var sim = Build(replay.CreateConfig());
        var player = sim.Play(replay);
        while (sim.Tick < replay.Ticks) sim.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(241, player.HashesChecked);
        Assert.Equal(path, sim.World.Resource<KnobPath>().Value);
        Assert.Equal((4f, false), (sim.World.Resource<KnobRules>().Speed, sim.World.Resource<KnobRules>().Enabled));
    }

    [Fact]
    public void Registration_Errors()
    {
        static Simulation With(Action<SimulationBuilder> configure) =>
            Simulation.Build(ConfigSet.Empty(), ["A"], b => { b.Resource(new KnobRules()); configure(b); }, []);

        Assert.Throws<InvalidOperationException>(() => With(b => { b.Tuning<KnobRules>("x"); b.Tuning<KnobRules>("x"); }));
        Assert.Throws<ArgumentException>(() => With(b => b.Tuning<KnobRules>("Bad.Section")));
        Assert.Throws<ArgumentException>(() => With(b => b.Tuning<KnobRules>("x")
            .Float("speed", "с", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 5, 5, 5)));
        Assert.Throws<ArgumentException>(() => With(b => b.Tuning<KnobRules>("x")
            .Float("sped", "с", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 0, 5, 1)));   // поля Sped нет
        Assert.Throws<ArgumentException>(() => With(b => b.Tuning<KnobRules>("x")
            .Float("bad-key", "с", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 0, 5, 1, field: "Speed")));
        Assert.Throws<InvalidOperationException>(() => With(b => b.Tuning<KnobRules>("x")
            .Float("speed", "с", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 0, 5, 1)
            .Float("Speed", "с", (in KnobRules r) => r.Speed, (ref KnobRules r, float v) => r.Speed = v, 0, 5, 1)));
        // Без ручек команды ручки нет (симуляция движка без ручек — как была).
        Assert.DoesNotContain(typeof(TuningCommand), With(_ => { }).CommandTypes);
    }

    [Fact]
    public void Clamp_And_Text()
    {
        var sim = Build();
        var speed = sim.Knob("knobs.speed")!;
        var enabled = sim.Knob("knobs.enabled")!;
        Assert.Equal((0f, 10f, 3.5f), (speed.Clamp(-1), speed.Clamp(11), speed.Clamp(3.5f)));
        Assert.True(float.IsNaN(speed.Clamp(float.NaN)));
        Assert.Equal((1f, 0f), (enabled.Clamp(0.2f), enabled.Clamp(0)));
        Assert.Equal(("3.5", "да", "нет"), (speed.Text(3.5f), enabled.Text(1), enabled.Text(0)));
    }
}
