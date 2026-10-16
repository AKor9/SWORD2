using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid3D;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Client;
using Sword.Game.Client.Scene;
using Sword.Game.Contracts;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// 3D-дым огня на настоящем устройстве и паке (слой отладки D3D12): клиент арены и модуль огня в одном кадре, как у <c>ClientHost</c>. Шишка —
/// по заданному пути (подменённое оружие игрока) или настоящий факел арены. Шишка по кругу — дым стоит в мире (центроид почти не движется);
/// бег — дым позади, окно впереди с запасом позади; погасший огонь — источник выключен, окно стоит, дым поднимается и рассеивается,
/// экземпляр освобождается; поле газа не зависит от разбиения тиков по кадрам и кадров паузы (бит в бит). Категории Gpu и Content.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class FireGasGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private const float TickRate = 120f;
    private readonly ITestOutputHelper output;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private bool _sink;

    public FireGasGpuTests(ITestOutputHelper output)
    {
        this.output = output;
        Log.AddSink(this);
        _sink = true;
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireGas", W, H, Activate: false));
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        _window.Dispose();
        if (_sink) Log.RemoveSink(this);
        _sink = false;
    }

    /// <summary>Оружие игрока по заданному пути: центр шишки тика (клинок вертикален, кончик на 2 см выше), выпало ли, построен ли заново.</summary>
    private sealed class PathTips(SimulationLink link, Func<ulong, Vector3> knob, Func<ulong, bool>? dropped, Func<ulong, bool>? built) : IWeaponTips
    {
        public int WeaponCount => 1;
        public string WeaponName(int weapon) => "torch";
        public int FindWeapon(string name) => string.Equals(name, "torch", StringComparison.OrdinalIgnoreCase) ? 0 : -1;

        public int Read(Span<WeaponTip> into, bool playerOnly)
        {
            if (link.Tick == 0) return 0;
            ulong tick = link.Tick - 1;
            var center = knob(tick);
            bool drop = dropped?.Invoke(tick) ?? false;
            if (into.Length > 0)
                into[0] = new WeaponTip(1, 0, true, 0, center - new Vector3(0, 0.6f, 0), center + new Vector3(0, 0.02f, 0), !drop, drop,
                    built?.Invoke(tick) ?? false);
            return 1;
        }
    }

    /// <summary>
    /// Прогон: кадры по заданному числу тиков (0 — кадр паузы); шишка — по пути <paramref name="knob"/> (null — факел арены) и команде игрока;
    /// <paramref name="each"/> — после каждого кадра; осмотр — до освобождения.
    /// </summary>
    private void Run(IEnumerable<int> frames, Func<ulong, Vector3>? knob, Action<Renderer, ClientModules, FireModule> inspect,
        Func<ulong, PlayerCommand>? command = null, Func<ulong, bool>? dropped = null, Func<ulong, bool>? built = null,
        Action<FireModule>? setup = null, Action<FireModule>? each = null)
    {
        var config = ConfigSet.Empty("arena.enemies=0", "combat.playerWeapon=torch");
        var sim = SwordSimulation.Create(config, CharacterAssets.LoadFromPak());
        var fire = new FireModule();
        var services = new ClientServices();
        if (knob is null) services.Add<IWeaponTips>(link => new ArenaWeaponTips(link));
        else services.Add<IWeaponTips>(link => new PathTips(link, knob, dropped, built));
        var modules = ClientModules.Compose(config, sim, [fire], services);
        setup?.Invoke(fire);
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(command?.Invoke(sim.Tick) ?? default), ticksPerFrame: 1,
            afterTick: modules.AfterTick);
        var arena = new ArenaClient(new CameraPose(0f, 0.95f, 8f, 0, 0), hud: false);
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        arena.ContributeShaders(contract);
        modules.ContributeShaders(contract);
        var renderer = new Renderer(new RendererOptions(_window.NativeHandle, W, H, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders")), contract);
        try
        {
            arena.Initialize(renderer, clock.Link);
            modules.Initialize(renderer, () => ((IClientObservable)arena).Camera);
            void Build(RenderFrame f)
            {
                modules.AddLights(f.Lights);
                arena.BuildFrame(f);
                modules.BuildFrame(f);
            }
            foreach (int ticks in frames)
            {
                modules.BeginFrame(sim.Tick);
                for (int t = 0; t < ticks; t++) clock.RunFrame(0);
                var view = new FrameView(1, ticks, 0, default);
                arena.Extract(view);
                modules.Extract(view);
                _window.PumpMessages();
                renderer.Render(Build, vsync: false);
                each?.Invoke(fire);
            }
            renderer.WaitForIdle();
            inspect(renderer, modules, fire);
        }
        finally
        {
            renderer.WaitForIdle();
            modules.Dispose();
            arena.Dispose();
            renderer.Dispose();
        }
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    private static float Seconds(ulong tick) => tick / TickRate;

    [Fact]
    public void Circling_GasStandsInWorld_CentroidBarelyMoves()
    {
        // Шишка по кругу радиуса 0.7 м, 2 об/с (8.8 м/с), 2 с по 2 тика за кадр: за последнюю секунду центроид дыма в мире проходит по горизонтали
        // малую долю пути шишки — дым стоит в мире. Слой, привязанный к шишке (2D-дым у площадки), шёл бы с ней: доля ≈ 1.
        var center = new Vector3(0.3f, 1.2f, -0.2f);
        Vector3 Knob(ulong tick)
        {
            float a = 2f * 2f * MathF.PI * Seconds(tick);
            return center + 0.7f * new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
        }
        FireGasDriftStats drift = default;
        Run(Enumerable.Repeat(2, 120), Knob, (_, _, fire) =>
        {
            var gas = fire.GasSims[0];
            Assert.Equal(FireGasState.Emitting, gas.Slot.State);
            Assert.Equal(120, gas.GpuSteps);
            var stats = Assert.IsType<Fluid3DStats>(gas.Stats);
            Assert.Equal(0, stats.NonFinite);
            Assert.True(stats.DensitySum > 10, $"Σρ {stats.DensitySum}");
            drift = Assert.IsType<FireGasDriftStats>(gas.Drift.Compute(FireModule.DriftWindow));
            // Окно не уезжает за шишкой по кругу дальше, чем на радиус круга с упреждением.
            var w = gas.Sim!.Window!.Value;
            Assert.True(Vector3.Distance(new Vector3(w.Center.X, 0, w.Center.Z), new Vector3(center.X, 0, center.Z)) < 1.7f, $"окно {w.Center}");
            output.WriteLine($"Σρ {stats.DensitySum:F1}, центроид {stats.Centroid} (центр круга {center}), окно {w.Center}");
        });
        output.WriteLine($"за {drift.Seconds:F2} с ({drift.Samples} чтений): шишка {drift.KnobPath:F2} м ({drift.KnobSpeed:F2} м/с), центроид {drift.CentroidPath:F3} м " +
            $"({drift.CentroidSpeed:F3} м/с, сдвиг {drift.CentroidShift:F3} м), доля {drift.Ratio:F3}");
        Assert.True(drift.Seconds > 0.9f && drift.Samples > 50, $"окно метрики {drift.Seconds} с, чтений {drift.Samples}");
        Assert.InRange(drift.KnobPath, 7.5f, 9.5f);
        // Замер: 0.118 (центроид 1.04 м/с против 8.78 м/с шишки, сдвиг за секунду 6 см).
        Assert.True(drift.Ratio < 0.2f, $"центроид прошёл {drift.Ratio:P1} пути шишки");
    }

    [Fact]
    public void Running_SmokeBehind_WindowLeads_NothingCutWithinMetreBehind()
    {
        // Бег по +x 5 м/с 2 с: окно упреждает (шишка впереди центра окна, позади ≥ 2 м), ближе 1 м позади шишки ничего не обрезается — грань
        // окна позади каждый кадр дальше 1 м; центроид дыма позади шишки.
        Vector3 Knob(ulong tick) => new(-4f + 5f * Seconds(tick), 1.2f, 0.5f);
        float minBehind = float.MaxValue;
        Run(Enumerable.Repeat(2, 120), Knob, (_, _, fire) =>
        {
            var gas = fire.GasSims[0];
            var s = gas.Slot;
            var w = gas.Sim!.Window!.Value;
            var stats = Assert.IsType<Fluid3DStats>(gas.Stats);
            output.WriteLine($"шишка {s.Knob}, скорость {s.Velocity}, цель {s.Target}, окно {w.Min}…{w.Max}, центроид {stats.Centroid}, Σρ {stats.DensitySum:F1}; " +
                $"позади не меньше {minBehind:F2} м");
            Assert.InRange(s.Velocity.X, 4.5f, 5.1f);
            // Упреждение в пределе: шишка впереди центра окна на GasMaxLead (с точностью до ячейки).
            Assert.InRange(s.Knob.X - w.Center.X, 1.05f, 1.15f);
            Assert.True(s.Knob.X - stats.Centroid.X > 0.3f, $"центроид позади шишки на {s.Knob.X - stats.Centroid.X:F2} м");
            Assert.True(stats.Centroid.Y > s.Knob.Y, $"центроид {stats.Centroid.Y:F2} ниже шишки {s.Knob.Y:F2}");
        }, each: fire =>
        {
            var gas = fire.GasSims[0];
            if (gas.Sim?.Window is not { } w || gas.GpuSteps < 30) return;
            minBehind = MathF.Min(minBehind, gas.Slot.Knob.X - w.Min.X);
        });
        Assert.True(minBehind > 1.3f, $"позади шишки в окне {minBehind:F2} м");
    }

    [Fact]
    public void Dropped_SourceOff_WindowStays_SmokeRisesAndFades_Released()
    {
        // Шишка стоит 2 с и гаснет (оружие выпало, тик 240): источник выключен, окно не двигается, центроид поднимается, Σρ падает; экземпляр
        // свободен по Σρ раньше предела времени. Кадры по 4 тика.
        var at = new Vector3(0.2f, 1.1f, 0.1f);
        const ulong drop = 240;
        Fluid3DWindow? frozen = null;
        float riseFrom = float.NaN, peakRise = 0, densityAtDrop = 0;
        ulong releasedAt = 0;
        Run(Enumerable.Repeat(4, 280), _ => at, (_, _, fire) =>
        {
            var slots = fire.GasSlots!;
            Assert.Equal(FireGasState.Free, slots[0].State);
            Assert.Equal((1L, 0L), (slots.ReleasedByDensity, slots.ReleasedByTime));
        }, dropped: tick => tick >= drop, each: fire =>
        {
            var gas = fire.GasSims[0];
            var s = gas.Slot;
            if (s.State == FireGasState.Fading && gas.Stats is { } st && st.Step > s.FadeFrom)
            {
                Assert.Equal(FireGasFlags.None, s.StepFlags);   // источник выключен
                var w = gas.Sim!.Window!.Value;
                frozen ??= w;
                Assert.Equal((frozen.Value.X, frozen.Value.Y, frozen.Value.Z), (w.X, w.Y, w.Z));
                if (float.IsNaN(riseFrom))
                {
                    riseFrom = st.Centroid.Y;
                    densityAtDrop = st.DensitySum;
                }
                peakRise = MathF.Max(peakRise, st.Centroid.Y - riseFrom);
            }
            if (s.State == FireGasState.Free && releasedAt == 0 && frozen is not null) releasedAt = fire.GasSlots![0].Step;
        });
        output.WriteLine($"Σρ при гашении {densityAtDrop:F1}, подъём центроида до {peakRise:F2} м, свободен после шага {releasedAt}");
        Assert.True(densityAtDrop > 10, $"Σρ {densityAtDrop}");
        // Дым из пламени: Σρ при гашении меньше (21.6 против > 30 у шара), экземпляр свободен по Σρ раньше — подъём 0.25 м (у шара 0.67).
        Assert.True(peakRise > 0.2f, $"центроид поднялся на {peakRise:F2} м");
    }

    [Fact]
    public void GasField_SameForAnyTicksPerFrame_AndPauses_Twice()
    {
        // Факел арены: бег, затем стойка, 150 тиков — по 2 за кадр и по 3 с кадрами паузы; поле газа (скорость, ρ, T) — бит в бит, и ещё раз
        // тот же прогон — тот же хеш.
        static PlayerCommand RunThenStand(ulong tick) => tick < 90 ? new PlayerCommand { Move = new Vector2(1, 0) } : default;
        ulong Hash(IEnumerable<int> frames)
        {
            ulong h = 0;
            Run(frames, null, (_, _, fire) =>
            {
                var gas = fire.GasSims[0];
                Assert.Equal(75, gas.GpuSteps);
                h = FireModule.GasHash(gas.Sim!.ReadFields());
            }, command: RunThenStand);
            return h;
        }
        ulong a = Hash(Enumerable.Repeat(2, 75));
        ulong b = Hash(Enumerable.Range(0, 50).SelectMany(i => i % 7 == 3 ? new[] { 3, 0, 0 } : new[] { 3 }));
        ulong c = Hash(Enumerable.Repeat(2, 75));
        output.WriteLine($"хеш поля газа {a:X16} / {b:X16} / {c:X16}");
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    /// <summary>
    /// Стык пламени и дыма у стоящей шишки (5 с по 2 тика, камера арены неподвижна; с 2 с каждые 0.5 с — по полям пламени и газа,
    /// <see cref="FireMetrics.Joint"/>): дым из пламени начинается у верха пламени, струя у стыка идёт туда же, куда языки пламени, пламя без
    /// бокового сноса горит вверх. Контроль — прежний источник (шар у шишки) и снос L2 (−0.1): зазор и разница направлений больше.
    /// </summary>
    [Fact]
    public void Standing_SmokeStartsAtFlameTop_AlongTheFlame_ControlSphereAndDriftWorse()
    {
        var at = new Vector3(0.2f, 1.1f, 0.1f);
        List<FireJoint> Joints(bool control)
        {
            var joints = new List<FireJoint>();
            int frame = 0;
            Run(Enumerable.Repeat(2, 300), _ => at, (_, _, _) => { }, setup: fire =>
            {
                if (!control) return;
                fire.GasTuning!.Feed = fire.GasTuning.Feed with { Mix = 0 };
                fire.Tuning!.Params = fire.Tuning.Params with { DirectVelocity = Sword.Engine.Fluid2D.Fluid2DParams.Torch.DirectVelocity };
            }, each: fire =>
            {
                if (++frame < 120 || frame % 30 != 0) return;
                var sim = fire.Sims[0];
                var gas = fire.GasSims[0];
                var flame = sim.Sim!.ReadFields();
                var grid = SmokeGrid.FromScalars(gas.Sim!.ReadFields().Scalars, gas.Sim.Window!.Value);
                joints.Add(FireMetrics.Joint(flame.Composite, flame.N, new Vector2(3, 3), fire.FlameParams, FireMetricsParams.Default, grid,
                    sim.Center, sim.Rotation, fire.SmokeParams));
            });
            return joints;
        }
        static string Line(FireJoint j) => $"верх {j.FlameTopCm:F1}, зазор {j.GapCm:F1}, пламя {j.FlameAngleDeg:F1}°, струя {j.SmokeAngleDeg:F1}°";
        static float Median(IEnumerable<float> x)
        {
            var a = x.Where(float.IsFinite).Order().ToArray();
            return a.Length == 0 ? float.NaN : a[a.Length / 2];
        }
        var fed = Joints(false);
        var control = Joints(true);
        foreach (var j in fed) output.WriteLine("из пламени: " + Line(j));
        foreach (var j in control) output.WriteLine("контроль: " + Line(j));
        float gap = fed.Average(j => j.Valid ? j.GapCm : 100f), gapControl = control.Average(j => j.Valid ? j.GapCm : 100f);
        float diff = Median(fed.Select(j => MathF.Abs(j.AngleDifferenceDeg))), diffControl = Median(control.Select(j => MathF.Abs(j.AngleDifferenceDeg)));
        float lean = MathF.Abs(fed.Average(j => j.FlameAngleDeg)), leanControl = MathF.Abs(control.Average(j => j.FlameAngleDeg));
        output.WriteLine($"зазор {gap:F1} см (контроль {gapControl:F1}), |струя − пламя| медиана {diff:F1}° ({diffControl:F1}°), снос пламени {lean:F1}° ({leanControl:F1}°)");
        Assert.Equal(7, fed.Count);
        Assert.All(fed, j => Assert.True(j.Valid, Line(j)));
        Assert.True(gap <= 2f, $"зазор {gap} см");
        Assert.True(diff <= 15f, $"разница направлений {diff}°");
        Assert.True(lean <= 6f, $"снос пламени {lean}°");
        Assert.True(gapControl > gap + 2f || diffControl > diff, $"контроль: зазор {gapControl}, разница {diffControl}");
        Assert.True(leanControl > lean, $"снос пламени L2 {leanControl}° против {lean}°");
    }
}
