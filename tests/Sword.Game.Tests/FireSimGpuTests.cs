using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Client;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
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
/// Сим огня на шишке факела на настоящем устройстве и паке (слой отладки D3D12): клиент арены и модуль огня в одном кадре, как у
/// <c>ClientHost</c>. Шаг — каждый второй тик; на бегу k = 0.35 и шлейф ρ позади головки; поля не зависят от разбиения тиков по кадрам и
/// от кадров паузы (бит в бит); кадр модуля — 0 Б; горящего оружия нет в паке — ни сима, ни шумов, ни проходов. Категории Gpu и Content.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
[Trait("Category", "Content")]
public sealed class FireSimGpuTests : IDisposable, ILogSink
{
    private const int W = 640, H = 480;
    private readonly ITestOutputHelper output;
    private readonly Win32Window _window;
    private readonly List<string> _d3d12 = [];
    private ArenaClient? _arena;
    private bool _sink;

    public FireSimGpuTests(ITestOutputHelper output)
    {
        this.output = output;
        Log.AddSink(this);
        _sink = true;
        _window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireSim", W, H, Activate: false));
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

    /// <summary>
    /// Прогон: кадры по заданному числу тиков (0 — кадр паузы), команда игрока каждого тика; байты модуля (наблюдатель, извлечение, проходы)
    /// считаются с кадра <paramref name="countFrom"/>; осмотр — до освобождения.
    /// </summary>
    private void Run(string[] sets, IEnumerable<int> frames, Func<ulong, PlayerCommand> command, Action<Renderer, ClientModules, FireModule, long> inspect,
        Action<FireModule>? setup = null, int countFrom = 0, string? hdrShot = null, Action<ArenaClient>? arenaSetup = null)
    {
        var config = ConfigSet.Empty(["arena.enemies=0", "combat.playerWeapon=torch", .. sets]);
        var sim = SwordSimulation.Create(config, CharacterAssets.LoadFromPak());
        var fire = new FireModule();
        var modules = ClientModules.Compose(config, sim, [fire], new ClientServices().Add<IWeaponTips>(link => new ArenaWeaponTips(link)));
        setup?.Invoke(fire);
        long moduleBytes = 0;
        bool counting = countFrom == 0;
        var clock = new SimulationClock(sim, (link, _) => link.Enqueue(command(sim.Tick)), ticksPerFrame: 1, afterTick: tick =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            modules.AfterTick(tick);
            if (counting) moduleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
        });
        var arena = _arena = new ArenaClient(new CameraPose(0f, 0.95f, 8f, 0, 0), hud: false);
        arenaSetup?.Invoke(arena);
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
                long before = GC.GetAllocatedBytesForCurrentThread();
                modules.AddLights(f.Lights);
                if (counting) moduleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                arena.BuildFrame(f);
                before = GC.GetAllocatedBytesForCurrentThread();
                modules.BuildFrame(f);
                if (counting) moduleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            }
            int index = 0;
            var list = frames.ToArray();
            foreach (int ticks in list)
            {
                counting = index++ >= countFrom;
                long before = GC.GetAllocatedBytesForCurrentThread();
                modules.BeginFrame(sim.Tick);
                if (counting) moduleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                for (int t = 0; t < ticks; t++) clock.RunFrame(0);
                var view = new FrameView(1, ticks, 0, default);
                arena.Extract(view);
                before = GC.GetAllocatedBytesForCurrentThread();
                modules.Extract(view);
                if (counting) moduleBytes += GC.GetAllocatedBytesForCurrentThread() - before;
                _window.PumpMessages();
                // Снимок HDR — последнего кадра (сцена до тонмапа: пламя и bloom видны числами).
                var capture = hdrShot is not null && index == list.Length
                    ? new FrameCapture(hdrShot + ".png") { HdrPath = hdrShot + ".hdr.png", TransparentPath = hdrShot + ".transparent.png" } : null;
                renderer.Render(Build, vsync: false, capture);
            }
            renderer.WaitForIdle();
            inspect(renderer, modules, fire, moduleBytes);
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

    private static PlayerCommand Stand(ulong _) => default;
    private static PlayerCommand RunX(ulong _) => new() { Move = new Vector2(1, 0) };

    private static ulong FieldHash(Fluid2DSim sim)
    {
        var fields = sim.ReadFields();
        var brush = sim.ReadBrush();
        return FireModule.Hash(MemoryMarshal.AsBytes(fields.Composite.AsSpan()), FireModule.Hash(MemoryMarshal.AsBytes(brush.AsSpan()), 14695981039346656037UL));
    }

    [Fact]
    public void Running_TrailBehindHead_StandingRisesAbove()
    {
        // 2 с (240 тиков, по 2 за кадр): шагов 120 — каждый второй тик; бег вдоль +x (x вида — мировая x при повороте камеры 0).
        (float Behind, float Above, float K) Measure(Func<ulong, PlayerCommand> command)
        {
            (float, float, float) result = default;
            Run([], Enumerable.Repeat(2, 120), command, (_, _, fire, _) =>
            {
                var sim = fire.Sims[0];
                Assert.Equal(FireState.Burning, sim.Slot.State);
                Assert.Equal(120, sim.GpuSteps);
                Assert.Equal(120, sim.Slot.Steps);
                var stats = Fluid2DStats.Compute(sim.Sim!.ReadFields(), sim.Sim.LastStep ?? 0);
                var p = sim.Sim.Params;
                Assert.Equal(0, stats.NonFinite);
                Assert.True(stats.MaxDensity <= 1f && stats.MaxSpeed <= p.VelocityClamp * MathF.Sqrt(2), $"ρ {stats.MaxDensity}, |v| {stats.MaxSpeed}");
                Assert.True(stats.DensitySum > 50, $"Σρ {stats.DensitySum}");
                // Центроид ρ от центра площадки (центр шишки шага) в осях вида, см: x — вправо (по бегу), y — вверх.
                float x = (stats.Centroid.X - 0.5f) * p.Extents.X * 100f, y = -(stats.Centroid.Y - 0.5f) * p.Extents.Y * 100f;
                var right = Vector3.Transform(Vector3.UnitX, sim.Rotation);
                Assert.True(right.X > 0.99f, $"ось x площадки {right}");
                output.WriteLine($"Σρ {stats.DensitySum:F1}, max|v| {stats.MaxSpeed:F3}, центроид {x:F1}, {y:F1} см, k {sim.K:F3}, Δ {sim.Delta * 100} см");
                result = (-x, y, sim.K);
            });
            return result;
        }
        var run = Measure(RunX);
        var stand = Measure(Stand);
        Assert.Equal(0.35f, run.K);
        Assert.True(run.Behind > 15, $"на бегу центроид ρ позади головки на {run.Behind:F1} см");
        Assert.True(stand.Above > 5, $"стоя центроид ρ над головкой на {stand.Above:F1} см");
        Assert.True(Math.Abs(stand.Behind) < run.Behind / 2, $"стоя по x {stand.Behind:F1} см");
    }

    [Fact]
    public void Fields_SameForAnyTicksPerFrame_AndPauses()
    {
        // 150 тиков: по 2 за кадр — и по 3 за кадр с кадрами паузы между; бег, затем стойка. Поля и кисть — бит в бит.
        static PlayerCommand RunThenStand(ulong tick) => tick < 90 ? RunX(tick) : default;
        ulong a = 0, b = 0;
        long stepsA = 0;
        Run([], Enumerable.Repeat(2, 75), RunThenStand, (_, _, fire, _) =>
        {
            a = FieldHash(fire.Sims[0].Sim!);
            stepsA = fire.Sims[0].GpuSteps;
        });
        var paused = Enumerable.Range(0, 50).SelectMany(i => i % 7 == 3 ? new[] { 3, 0, 0 } : new[] { 3 });
        Run([], paused, RunThenStand, (_, _, fire, _) =>
        {
            b = FieldHash(fire.Sims[0].Sim!);
            Assert.Equal(stepsA, fire.Sims[0].GpuSteps);
        });
        output.WriteLine($"хеш полей {a:X16} / {b:X16}, шагов {stepsA}");
        Assert.Equal(75, stepsA);
        Assert.Equal(a, b);
    }

    [Fact]
    public void SteadyFrames_ModuleDoesNotAllocate_WithFieldViewAndOverlay()
    {
        // Прогрев 60 кадров, затем 120 кадров бега с видом поля и оверлеем на экране: кадр модуля (наблюдатель, извлечение, шаги, выход поля,
        // проходы вида) — 0 Б.
        long bytes = -1;
        Run([], Enumerable.Repeat(2, 180), RunX, (_, modules, fire, counted) =>
        {
            bytes = counted;
            var report = Assert.Single(modules.Report());
            Assert.Contains("fluid.export", report.Passes);
            Assert.Contains("debug.fire.field", report.Passes);
            Assert.Contains(FireFlame.PassName, report.Passes);
            Assert.Equal(1, fire.Flame!.LastQuads);
            Assert.Equal(1, fire.DrawnFields);
        }, setup: fire =>
        {
            fire.FieldOverlay!.Shown = true;
            fire.Overlay!.Shown = true;
        }, countFrom: 60);
        output.WriteLine($"модуль за 120 кадров: {bytes} Б");
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void Flame_SameFrameTwice_SameHdr_CoreBrighterThanOne()
    {
        // Факел стоя, 120 кадров по 2 тика — дважды: последний кадр (время flow-map — по тикам) в HDR бит в бит; пламя — одна площадка,
        // ядро ярче 1 (даёт bloom), после экспозиции и Filmic у самого яркого пикселя синий канал > 0 при Color1.B = 0.
        string dir = Path.Combine(Path.GetTempPath(), $"sword-flame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] Shot(string name)
            {
                string path = Path.Combine(dir, name);
                Run([], Enumerable.Repeat(2, 120), Stand, (_, modules, fire, _) =>
                {
                    Assert.Equal(1, fire.Flame!.LastQuads);
                    Assert.Contains(FireFlame.PassName, Assert.Single(modules.Report()).Passes);
                }, hdrShot: path);
                return File.ReadAllBytes(path + ".hdr.bin");
            }
            var a = Shot("a");
            var b = Shot("b");
            Assert.True(a.AsSpan().SequenceEqual(b), "тот же кадр прогона дважды — HDR не бит в бит");
            var halves = MemoryMarshal.Cast<byte, Half>(a);
            Vector3 brightest = default;
            for (int i = 0; i + 3 < halves.Length; i += 4)
            {
                var c = new Vector3((float)halves[i], (float)halves[i + 1], (float)halves[i + 2]);
                if (c.X > brightest.X) brightest = c;
            }
            var (r, g, bl) = FilmicTonemap.DisplayBytes(brightest, Renderer.DefaultExposure);
            output.WriteLine($"ярчайший пиксель HDR ({brightest.X:0.00}, {brightest.Y:0.00}, {brightest.Z:0.00}) → sRGB без bloom ({r}, {g}, {bl})");
            Assert.True(brightest.X > 1, $"ядро не ярче 1: {brightest}");
            Assert.True(bl > 0, $"синий канал ядра 0 после тонмапа: ({r}, {g}, {bl})");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Smoke_InHalfLayer_LightFunctionDoesNotReachIt_ZeroDensityLeavesFrame()
    {
        // Факел стоя, 60 кадров по 2 тика: дым — один объём в слое ½ с источником огня (без LF) и статистикой марша. Мерцание света (LF)
        // выключено — слой ½ дыма бит в бит тот же (LF — только поверхности); плотность 0 — HDR кадра бит в бит как без дыма.
        string dir = Path.Combine(Path.GetTempPath(), $"sword-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            (byte[] Hdr, byte[]? Half) Shot(string name, Action<FireModule>? setup)
            {
                string path = Path.Combine(dir, name);
                Run([], Enumerable.Repeat(2, 60), Stand, (_, modules, fire, bytes) =>
                {
                    var smoke = fire.Smoke!;
                    if (!fire.SmokeShown) return;
                    Assert.Equal((1, 1), (smoke.LastVolumes, smoke.LastLights));
                    var passes = Assert.Single(modules.Report()).Passes;
                    Assert.Contains(FireSmoke.PassName, passes);
                    Assert.Contains(FireSmoke.StatsPassName, passes);
                    var stats = Assert.IsType<SmokeStats>(smoke.Stats);
                    Assert.True(stats.Hit > 0 && stats.MaxSteps is > 0 and <= 196 && (stats.Coverage > 0 || fire.SmokeParams.Density == 0), $"статистика дыма: {stats}");
                }, setup: setup, hdrShot: path);
                string half = path + ".transparent.bin";
                return (File.ReadAllBytes(path + ".hdr.bin"), File.Exists(half) ? File.ReadAllBytes(half) : null);
            }
            var lit = Shot("lf", null);
            var flat = Shot("nolf", fire => fire.Light!.FunctionShown = false);
            Assert.NotNull(lit.Half);
            Assert.True(lit.Half.AsSpan().SequenceEqual(flat.Half), "LF выключена — слой ½ дыма изменился");
            Assert.False(lit.Hdr.AsSpan().SequenceEqual(flat.Hdr), "LF выключена — кадр не изменился (проверка не видит LF)");
            var zero = Shot("zero", fire => fire.SmokeParams = SmokeParams.Torch with { Density = 0 });
            var off = Shot("off", fire => fire.SmokeShown = false);
            Assert.Null(off.Half);
            Assert.True(zero.Hdr.AsSpan().SequenceEqual(off.Hdr), "плотность 0 — кадр не тот же, что без дыма");
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("SWORD_TEST_KEEP") != "1") Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NoBurningWeaponInPack_NoSim_NoNoise_NoPasses()
    {
        Run(["fire.weapons=[\"axe\"]"], Enumerable.Repeat(2, 10), Stand, (renderer, modules, fire, _) =>
        {
            Assert.Empty(fire.Sims);
            Assert.Equal(["axe"], fire.Fires!.Missing);
            Assert.DoesNotContain(renderer.Textures, t => t.Path.StartsWith("effects/fire", StringComparison.Ordinal));
            var report = Assert.Single(modules.Report());
            Assert.Empty(report.GpuSimulations);
            Assert.DoesNotContain(report.Passes, p => p.StartsWith("fluid.", StringComparison.Ordinal));
            Assert.Null(fire.Light);
            Assert.Equal(0, renderer.Lights.Report().Shown);
        });
    }

    [Fact]
    public void Light_InFrameBuffer_AtShownKnob_OneFunction_ArenaOverlaySeesIt()
    {
        // Факел стоя: источник огня — в буфере кадра рендера в показанном центре шишки со смещением, с одной LF, рассеяние в объёмах 0.15;
        // он добавлен до проходов игры — оверлей lights арены его рисует. Шум T_NoiseTemplate2 — одна текстура на кисть и LF. Ручка LF
        // выкл. — источник без LF (ровно затухание).
        Run([], Enumerable.Repeat(2, 30), Stand, (renderer, _, fire, _) =>
        {
            var light = fire.Light!;
            Assert.True(light.Ready, light.Missing);
            var report = renderer.Lights.Report();
            var shown = Assert.Single(report.Lights).Light;
            Assert.Equal(light.Light(0)!.Value with { Function = 0 }, shown);
            Assert.Equal(fire.Display(0).Center + FireLightParams.Torch.Offset, shown.Position);
            Assert.Equal((1, 0.15f, 100f), (report.Functions, shown.VolumetricScattering, shown.Intensity));
            Assert.InRange(light.FrameFunction, 0f, 1f);
            Assert.Equal(1, _arena!.DrawnLights);
            Assert.Single(renderer.Textures, t => t.Path == FireLight.NoisePathA);
            Assert.Single(renderer.Textures, t => t.Path == FireLight.NoisePathB);
        }, arenaSetup: arena => arena.ShowLights = true);
        Run([], Enumerable.Repeat(2, 10), Stand, (renderer, _, fire, _) =>
        {
            var report = renderer.Lights.Report();
            Assert.Equal((-1, 0), (Assert.Single(report.Lights).Light.Function, report.Functions));
        }, setup: fire => fire.Light!.FunctionShown = false);
    }
}
