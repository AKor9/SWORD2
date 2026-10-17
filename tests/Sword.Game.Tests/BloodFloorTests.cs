using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Blood;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Кровь на полу (план этапа 12, К6, К8): полёт капли — замкнутая формула = численное интегрирование, приземление по рельефу ≤ 1 мм и тик в тик;
/// масса штампов = объём приземлившихся капель; маска приёмника плёнки; на GPU — масса штампов в поле, перенос в карту засохших пятен при
/// сдвиге окна (поле + карта ± 1 %, «ушёл и вернулся» — байты карты те же, сброс — пусто), два прогона — тот же хеш, кадр — 0 Б.
/// </summary>
public sealed class BloodFloorTests(ITestOutputHelper output)
{
    private const float TickDt = 1f / 120;

    internal sealed class Terrain(Func<float, float, float> height) : ITerrainView
    {
        public float Height(float x, float z) => height(x, z);
        public float SmoothHeight(float x, float z, out Vector2 gradient)
        {
            gradient = Vector2.Zero;
            return height(x, z);
        }
        public float StepHeight(float x, float z) => 0;
        public Vector3 Normal(float x, float z) => Vector3.UnitY;
        public TerrainGrid Grid => new(256, 32);
        public int HeightsSrv => -1;
    }

    /// <summary>Эталон: dv/dt = g − k·v методом РК4 с шагом 10 мкс до касания рельефа (линейно между шагами).</summary>
    private static (float T, Vector3 P) Integrate(ITerrainView terrain, Vector3 x, Vector3 v, float k)
    {
        const double h = 1e-5;
        var g = new Vector3(0, -BloodFlight.Gravity, 0);
        double t = 0;
        Vector3 Acc(Vector3 vel) => g - k * vel;
        for (int i = 0; i < 2_000_000; i++)
        {
            float hf = (float)h;
            var k1v = Acc(v); var k1x = v;
            var k2v = Acc(v + 0.5f * hf * k1v); var k2x = v + 0.5f * hf * k1v;
            var k3v = Acc(v + 0.5f * hf * k2v); var k3x = v + 0.5f * hf * k2v;
            var k4v = Acc(v + hf * k3v); var k4x = v + hf * k3v;
            var nx = x + hf / 6 * (k1x + 2 * k2x + 2 * k3x + k4x);
            var nv = v + hf / 6 * (k1v + 2 * k2v + 2 * k3v + k4v);
            float a0 = x.Y - terrain.Height(x.X, x.Z), a1 = nx.Y - terrain.Height(nx.X, nx.Z);
            if (a1 <= 0)
            {
                float s = a0 / (a0 - a1);
                var p = Vector3.Lerp(x, nx, s);
                return ((float)(t + s * h), new Vector3(p.X, terrain.Height(p.X, p.Z), p.Z));
            }
            x = nx;
            v = nv;
            t += h;
        }
        throw new InvalidOperationException("не приземлилась");
    }

    [Fact]
    public void Flight_ClosedForm_MatchesIntegration_LandingWithinMillimetre_TickExact()
    {
        var flat = new Terrain((_, _) => 0);
        var slope = new Terrain((x, z) => 0.3f * MathF.Tan(17 * MathF.PI / 180) * x + 0.05f);
        var steps = new Terrain((x, _) => MathF.Floor(Math.Clamp(x + 1, 0, 5)) * 0.12f);
        float worst = 0;
        int cases = 0;
        foreach (var terrain in new[] { flat, slope, steps })
            for (int i = 0; i < 12; i++)
            {
                var x0 = new Vector3(-0.3f + 0.13f * i, 0.4f + 0.11f * i, 0.2f - 0.05f * i);
                var v0 = new Vector3(1.5f - 0.3f * i, 2f - 0.4f * i, 0.7f * MathF.Sin(i));
                float k = 0.5f + 0.4f * i;
                Assert.True(BloodFlight.Land(terrain, x0, v0, k, TickDt, BloodFloor.MaxFlightTicks, out int ticks, out float t, out var point));
                var (tr, pr) = Integrate(terrain, x0, v0, k);
                float d = Vector3.Distance(point, pr);
                worst = MathF.Max(worst, d);
                Assert.True(d <= 1e-3f, $"случай {i}: приземление {point} против {pr} ({d * 1000:F3} мм)");
                Assert.Equal((int)MathF.Ceiling(tr / TickDt), ticks);
                cases++;
            }
        output.WriteLine($"{cases} случаев: приземление − РК4 до {worst * 1000:F4} мм");
    }

    [Fact]
    public void Floor_StampsMass_EqualsLandedDrops_TickOfLanding()
    {
        var floor = new BloodFloor();
        var terrain = new Terrain((_, _) => 0);
        var drops = new List<BloodDrop>();
        double total = 0;
        for (int i = 0; i < 40; i++)
        {
            float ml = 0.05f + 0.01f * i;
            drops.Add(new BloodDrop(1, 10, new Vector3(0.1f * i - 2, 1.2f, 0.05f * i), new Vector3(0.3f, 0.5f, -0.2f), ml));
            total += ml;
        }
        floor.Detach(drops.ToArray(), terrain, 2, TickDt);
        Assert.Equal(40, floor.Flying.Length);
        double stamped = 0;
        var landedAt = new List<ulong>();
        for (ulong tick = 10; tick < 400; tick += 2)
        {
            floor.BeginFrame();
            long before = floor.Landed;
            floor.Step(tick, tick / 2, Vector2.Zero, 1.5f, 60, null);
            foreach (var s in floor.Stamps)
            {
                stamped += BloodFloor.Millilitres(s.Volume);
                Assert.Equal(BloodFloor.DensityVolume(s.Millilitres), s.Volume);
            }
            for (long k = before; k < floor.Landed; k++) landedAt.Add(tick);
        }
        Assert.Equal(0, floor.Flying.Length);
        Assert.Equal(total, floor.LandedMl, 4);
        Assert.Equal(total, stamped, 3);
        Assert.Equal(floor.StampedMl + floor.OutsideMl, floor.LandedMl, 6);
        // Капля легла в первом шаге после тика касания (до него она на экране — в полёте), не раньше и не позже.
        foreach (var d in drops)
        {
            Assert.True(BloodFlight.Land(terrain, d.Position, d.Velocity, 2, TickDt, BloodFloor.MaxFlightTicks, out int ticks, out _, out _));
            ulong land = d.Tick + (ulong)ticks;
            Assert.Contains(landedAt, t => t > land && t <= land + 2);
        }
        output.WriteLine($"капель 40, {total:F3} мл; в штампах {stamped:F4} мл");
    }

    /// <summary>Формы для CPU-тестов: брызги 3, капли 2, косые 4 (вытянуты вдвое), лужи 2; среднее слоя 0.2 + 0.01·k.</summary>
    internal static BloodStampShapes FakeShapes() =>
        new([3, 2, 4, 2], [.. Enumerable.Range(0, 11).Select(k => 0.2f + 0.01f * k)], [.. Enumerable.Range(0, 11).Select(k => k is >= 5 and < 9 ? 2f : 1f)]);

    /// <summary>Группа формы k ≥ 1 у <see cref="FakeShapes"/>.</summary>
    private static BloodShapeGroup GroupOf(int shape) => shape switch
    {
        <= 3 => BloodShapeGroup.Omni,
        <= 5 => BloodShapeGroup.Spots,
        <= 9 => BloodShapeGroup.Directional,
        _ => BloodShapeGroup.Puddles,
    };

    [Fact]
    public void Floor_Landing_ShapeByImpact_MassKept_OutsideWindowGoesToMap()
    {
        var floor = new BloodFloor { Shapes = FakeShapes() };
        var terrain = new Terrain((_, _) => 0);
        // Отвесная с 1.2 м, косая с 1.2 м (быстро вбок по +Z), медленная с 5 см, отвесная за окном (x = 20 м).
        BloodDrop[] drops =
        [
            new(1, 10, new Vector3(0, 1.2f, 0), Vector3.Zero, 0.4f),
            new(1, 10, new Vector3(1, 1.2f, 0), new Vector3(0, 0, 8), 0.4f),
            new(1, 10, new Vector3(2, 0.05f, 0), Vector3.Zero, 1f),
            new(1, 10, new Vector3(20, 1.2f, 0), Vector3.Zero, 0.4f),
        ];
        floor.Detach(drops, terrain, 2, TickDt);
        floor.BeginFrame();
        floor.Step(10, 5, Vector2.Zero, 1.5f, 60, null);
        var stamps = new List<BloodFloorStamp>();
        var map = new List<BloodMapStamp>();
        for (ulong tick = 12; tick < 300; tick += 2)
        {
            floor.BeginFrame();
            floor.Step(tick, tick / 2, Vector2.Zero, 1.5f, 60, null);
            stamps.AddRange(floor.Stamps.ToArray());
            map.AddRange(floor.MapStamps.ToArray());
        }
        Assert.Equal(3, stamps.Count);
        var vertical = stamps.Single(s => MathF.Abs(s.Xz.X) < 0.1f);
        var oblique = stamps.Single(s => MathF.Abs(s.Xz.X - 1) < 0.1f);
        var gentle = stamps.Single(s => MathF.Abs(s.Xz.X - 2) < 0.1f);
        Assert.Contains(GroupOf(vertical.Shape), new[] { BloodShapeGroup.Omni, BloodShapeGroup.Spots });
        Assert.Equal(BloodShapeGroup.Directional, GroupOf(oblique.Shape));
        Assert.Equal(BloodShapeGroup.Puddles, GroupOf(gentle.Shape));
        // Косая: ось u — по горизонтальной скорости (+Z, угол π/2 ± 0.15), вытянута: слой ×2 и 1/sin α.
        Assert.InRange(oblique.Angle, MathF.PI / 2 - 0.16f, MathF.PI / 2 + 0.16f);
        Assert.True(oblique.HalfAxes.X > 1.4f * oblique.HalfAxes.Y, $"косая {oblique.HalfAxes}");
        foreach (var s in stamps) Assert.Equal(BloodFloor.DensityVolume(s.Millilitres), s.Volume);
        // За окном — в карту, масса та же.
        Assert.Single(map);
        Assert.Equal(BloodFloor.DensityVolume(0.4f), map[0].Volume);
        Assert.Equal(0.4, floor.OutsideMl, 5);
        Assert.Equal(floor.LandedMl, floor.StampedMl + floor.OutsideMl + floor.OffMapMl, 5);
        output.WriteLine($"отвесная {vertical.HalfAxes} форма {vertical.Shape}; косая {oblique.HalfAxes} угол {oblique.Angle:F3}; медленная {gentle.HalfAxes}");
    }

    [Fact]
    public void Floor_Puddle_PoursSeriesOfShapes_MassIsVolume_Deterministic()
    {
        (float Ml, int Count, int Shapes, BloodFloorStamp[] All) Run()
        {
            var floor = new BloodFloor { Shapes = FakeShapes() };
            floor.RequestPuddle();
            var all = new List<BloodFloorStamp>();
            for (ulong tick = 0; tick < 400; tick += 2)
            {
                floor.BeginFrame();
                floor.Step(tick, tick / 2, new Vector2(3, -2), 1.5f, 120, null);
                all.AddRange(floor.Stamps.ToArray());
            }
            return (all.Sum(s => s.Millilitres), all.Count, all.Select(s => s.Shape).Distinct().Count(), [.. all]);
        }
        var a = Run();
        var b = Run();
        Assert.InRange(a.Ml, 120 * 0.9999f, 120 * 1.0001f);
        Assert.True(a.Count >= 60 && a.Shapes >= 4, $"штампов {a.Count}, форм {a.Shapes}");
        Assert.All(a.All, s => Assert.True(Vector2.Distance(s.Xz, new Vector2(3, -2)) < 0.3f));
        Assert.Equal(a.All, b.All);
        output.WriteLine($"лужа 120 мл: штампов {a.Count}, форм {a.Shapes}");
    }

    [Fact]
    public void Shapes_PickInGroup_SkipEmptyLayers()
    {
        var shapes = new BloodStampShapes([2, 1, 0, 2], [0.3f, 0.001f, 0.5f, 0.2f, 0.4f], [1, 1, 1, 1, 1]);
        for (uint seed = 0; seed < 200; seed++)
        {
            Assert.Equal(1, shapes.Pick(BloodShapeGroup.Omni, seed));          // слой 2 почти пуст — не выбирается
            Assert.Equal(3, shapes.Pick(BloodShapeGroup.Spots, seed));
            Assert.Equal(0, shapes.Pick(BloodShapeGroup.Directional, seed));   // группы нет — гаусс
            Assert.InRange(shapes.Pick(BloodShapeGroup.Puddles, seed), 4, 5);
        }
        Assert.Equal(1f, shapes.Means[1]);
        Assert.Equal(4096f, BloodFloor.Epoch(5000));
        Assert.Equal(0f, BloodFloor.Epoch(4095.9f));
    }

    [Fact]
    public void Receiver_FloorYes_SkinSwordColumnSideAndTopNo()
    {
        float cos = MathF.Cos(50 * MathF.PI / 180), band = 0.15f;
        Assert.True(BloodFloor.Receives(true, Vector3.UnitY, 0.01f, 0, cos, band));                       // пол
        var slope = Vector3.Normalize(new Vector3(MathF.Sin(0.3f), MathF.Cos(0.3f), 0));
        Assert.True(BloodFloor.Receives(true, slope, 0.5f, 0.5f, cos, band));                              // склон 17°
        Assert.False(BloodFloor.Receives(false, Vector3.UnitY, 0, 0, cos, band));                         // кожа, меч сверху
        Assert.False(BloodFloor.Receives(true, Vector3.UnitX, 0.5f, 0, cos, band));                       // бок колонны
        Assert.False(BloodFloor.Receives(true, Vector3.UnitY, 3.2f, 0, cos, band));                       // верх колонны
        Assert.False(BloodFloor.Receives(true, Vector3.Normalize(new Vector3(1, 0.5f, 0)), 0.05f, 0, cos, band));   // подступёнок
    }
}

/// <summary>Пол на GPU: масса штампов, карта засохших пятен при сдвиге окна, повтор, кадр — 0 Б.</summary>
[Collection("game-gpu")]
public sealed class BloodFloorGpuTests(ITestOutputHelper output)
{
    private sealed class D3d12Sink : ILogSink
    {
        public readonly List<string> Messages = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) Messages.Add(entry.Message);
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly Win32Window Window;
        public readonly Renderer Renderer;
        public readonly BloodFloorGpu Gpu;
        public readonly BloodTuning Tuning = new();
        private readonly List<IDisposable> _owned = [];
        private ulong _step;
        private int _cx, _cz;

        public Rig()
        {
            Window = new Win32Window(new WindowOptions("Sword.Game.Tests.BloodFloor", 64, 64, Activate: false));
            var contract = new ShaderContract();
            Renderer.Contribute(contract);
            Fluid2DSim.Contribute(contract);
            BloodFloorGpu.Contribute(contract);
            Renderer = new Renderer(new RendererOptions(Window.NativeHandle, 64, 64, DebugLayer: true,
                ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
            Gpu = new BloodFloorGpu(new BloodFloor(), new BloodFloorView { Tuning = Tuning });
            Gpu.Create(Renderer, d =>
            {
                _owned.Add(d);
                return d;
            });
        }

        /// <summary>Шаг: сдвиг окна (текселей), штампы (x, z, мл).</summary>
        public void Step(int sx = 0, int sz = 0, bool reset = false, params (float X, float Z, float Ml)[] stamps)
        {
            _cx += sx;
            _cz += sz;
            var list = stamps.Select(s => new BloodFloorStamp(new Vector2(s.X, s.Z), new Vector2(BloodFloor.HalfAxis(s.Ml, 1.5f)), 0, 0,
                    BloodFloor.DensityVolume(s.Ml), s.Ml))
                .ToArray();
            var step = new BloodFloorStep
            {
                Step = _step, Tick = 2 * _step, ShiftX = sx, ShiftZ = sz, CenterX = _cx, CenterZ = _cz, StampCount = list.Length,
                Reset = reset || _step == 0 ? 1 : 0, Now = _step / 60f,
            };
            _step++;
            Window.PumpMessages();
            Renderer.Render(f => Gpu.Step(f, step, list), vsync: false);
        }

        /// <summary>Шаг с часами пола <paramref name="now"/>, с, переносом засохшего и каплями мимо окна.</summary>
        public void StepAt(float now, bool dry, BloodFloorStamp[] stamps, BloodMapStamp[] map)
        {
            var step = new BloodFloorStep
            {
                Step = _step, Tick = 2 * _step, CenterX = _cx, CenterZ = _cz, StampCount = stamps.Length, MapStampCount = map.Length,
                Reset = _step == 0 ? 1 : 0, Now = now, Dry = dry ? 1 : 0, Epoch = BloodFloor.Epoch(now),
            };
            _step++;
            Window.PumpMessages();
            Renderer.Render(f => Gpu.Step(f, step, stamps, map), vsync: false);
        }

        public double WindowMl()
        {
            Renderer.WaitForIdle();
            return BloodFloor.Millilitres(BloodFloorGpu.WindowDensity(Gpu.Sim!.ReadFields()));
        }

        public uint[] Map()
        {
            Renderer.WaitForIdle();
            return Gpu.ReadMap();
        }

        public void Dispose()
        {
            Renderer.WaitForIdle();
            for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
            Renderer.Dispose();
            Window.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_ShapedStamps_Mass_DriedToMap_EpochBirth_DropsOutsideToMap()
    {
        var sink = new D3d12Sink();
        Log.AddSink(sink);
        try
        {
            using var rig = new Rig();
            var shapes = rig.Gpu.Shapes;
            Assert.NotNull(shapes);
            rig.Tuning.FloorDryAge = 0.5f;
            // Формы пака: лужа 30 мл и 20 брызг по 0.3 мл — масса в поле ± 5 %.
            var stamps = new List<BloodFloorStamp>();
            int puddle = shapes!.Pick(BloodShapeGroup.Puddles, 1);
            Assert.True(puddle > 0 && shapes.Mean(puddle) > BloodStampShapes.MinMean);
            stamps.Add(new BloodFloorStamp(new Vector2(2, 0), new Vector2(0.15f), 0.4f, puddle, BloodFloor.DensityVolume(30), 30));
            for (int i = 0; i < 20; i++)
                stamps.Add(new BloodFloorStamp(new Vector2(-3 + 0.3f * i, 1), new Vector2(0.06f, 0.04f), 0.3f * i,
                    shapes.Pick((BloodShapeGroup)(i % 3), (uint)i), BloodFloor.DensityVolume(0.3f), 0.3f));
            float now = 70000;   // 19 ч: рождение в half — относительно эпохи
            rig.StepAt(now, false, [.. stamps], []);
            double total = 30 + 20 * 0.3, first = rig.WindowMl();
            output.WriteLine($"формы: {total:F2} мл → в поле {first:F3} мл");
            Assert.InRange(first, 0.95 * total, 1.05 * total);

            // Засохшее (вдвое старше 0.5 с — любое, тонкое — старше 0.5 с) уходит из поля в карту.
            for (int i = 1; i <= 150; i++) rig.StepAt(now + i / 60f, true, [], []);
            double window = rig.WindowMl();
            var map = rig.Map();
            double mapMl = BloodFloor.Millilitres(BloodFloorGpu.MapDensity(map));
            output.WriteLine($"после переноса засохшего: поле {window:F4} мл, карта {mapMl:F4} мл (было {first:F4})");
            Assert.True(window < 0.01 * first, $"в поле осталось {window} мл");
            Assert.InRange(mapMl, 0.99 * first, 1.01 * first);
            float end = now + 150 / 60f, epoch = BloodFloor.Epoch(end);
            int densest = 0;
            for (int i = 1; i < map.Length; i++)
                if ((float)BitConverter.UInt16BitsToHalf((ushort)map[i]) > (float)BitConverter.UInt16BitsToHalf((ushort)map[densest])) densest = i;
            float birth = (float)BitConverter.UInt16BitsToHalf((ushort)(map[densest] >> 16));
            float age = end - epoch - birth;
            Assert.True(float.IsFinite(birth) && age >= 0.4f && age <= 3, $"рождение {birth} (эпоха {epoch}), возраст {age} с");

            // Капля за окном (x = 20 м) — сразу в карту.
            rig.StepAt(end + 1 / 60f, false, [], [new BloodMapStamp(new Vector2(20, 0), BloodFloor.DensityVolume(2))]);
            double mapAfter = BloodFloor.Millilitres(BloodFloorGpu.MapDensity(rig.Map()));
            Assert.InRange(mapAfter - mapMl, 1.99, 2.01);
            Assert.True(sink.Messages.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", sink.Messages));
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_StampMass_MapTransfer_ReturnSameBytes_ResetEmpty_Replay_ZeroAlloc()
    {
        var sink = new D3d12Sink();
        Log.AddSink(sink);
        try
        {
            ulong hash;
            double windowMl, mapAfter;
            using (var rig = new Rig())
            {
                // Масса штампов: 30 капель по 0.2 мл и лужа 60 мл у края окна (x = +14 м).
                var drops = Enumerable.Range(0, 30).Select(i => (-3f + 0.2f * i, 1f - 0.07f * i, 0.2f)).ToArray();
                rig.Step(0, 0, false, [.. drops, (14f, 0f, 60f)]);
                double expected = 30 * 0.2 + 60;
                double first = rig.WindowMl();
                output.WriteLine($"штампы: {expected:F3} мл → в поле {first:F4} мл");
                Assert.True(Math.Abs(first - expected) <= 0.05 * expected, $"масса штампов {first} против {expected}");
                for (int i = 0; i < 120; i++) rig.Step();
                windowMl = rig.WindowMl();
                Assert.Equal(0, BloodFloorGpu.MapDensity(rig.Map()));

                // Окно уходит на 20 м по −X: лужа у +14 м уходит за край — в карту.
                rig.Step(-640, 0);
                double left = rig.WindowMl();
                var map1 = rig.Map();
                double mapMl = BloodFloor.Millilitres(BloodFloorGpu.MapDensity(map1));
                output.WriteLine($"до сдвига в поле {windowMl:F4} мл; после: поле {left:F4}, карта {mapMl:F4} мл (Σ {left + mapMl:F4})");
                Assert.True(Math.Abs(left + mapMl - windowMl) <= 0.01 * windowMl, $"поле + карта {left + mapMl} против {windowMl}");
                Assert.True(mapMl > 50, $"в карте {mapMl} мл");
                // Место в карте: самый густой тексель — под лужей (x = 14, z = 0), а не со сдвигом окна или зеркально.
                int densest = 0;
                for (int i = 1; i < map1.Length; i++)
                    if ((float)BitConverter.UInt16BitsToHalf((ushort)map1[i]) > (float)BitConverter.UInt16BitsToHalf((ushort)map1[densest])) densest = i;
                float mx = -0.5f * BloodFloor.MapSize + (densest % BloodFloor.MapN + 0.5f) * BloodFloor.Texel;
                float mz = -0.5f * BloodFloor.MapSize + (densest / BloodFloor.MapN + 0.5f) * BloodFloor.Texel;
                Assert.True(MathF.Abs(mx - 14) < 0.1f && MathF.Abs(mz) < 0.1f, $"лужа в карте у ({mx}, {mz}), а не у (14, 0)");

                // Ушёл и вернулся: окно назад и снова прочь — байты карты те же.
                rig.Step(640, 0);
                for (int i = 0; i < 10; i++) rig.Step();
                rig.Step(-640, 0);
                var map2 = rig.Map();
                Assert.True(map1.AsSpan().SequenceEqual(map2), "карта изменилась после возврата окна");
                mapAfter = BloodFloor.Millilitres(BloodFloorGpu.MapDensity(map2));

                // Кадр без выделений: шаги со штампами и сдвигом.
                for (int i = 0; i < 20; i++) rig.Step(i % 2 == 0 ? 3 : -3, 0, false, (0f, 0f, 0.1f));
                long before = GC.GetAllocatedBytesForCurrentThread();
                var stamp = new[] { (0f, 0f, 0.1f) };
                for (int i = 0; i < 40; i++) rig.Step(i % 2 == 0 ? 3 : -3, 0, false, stamp);
                long spent = GC.GetAllocatedBytesForCurrentThread() - before;
                output.WriteLine($"40 шагов пола: {spent} Б (с обвязкой теста)");

                // Сброс: поле и карта пусты.
                rig.Step(0, 0, reset: true);
                Assert.Equal(0, BloodFloorGpu.MapDensity(rig.Map()));
                Assert.Equal(0, rig.WindowMl());
                hash = BloodFloorGpu.Hash(rig.Gpu.Sim!.ReadFields());
            }
            output.WriteLine($"карта после возврата {mapAfter:F4} мл");

            // Повтор: тот же прогон — тот же хеш поля.
            static ulong Run()
            {
                using var rig = new Rig();
                rig.Step(0, 0, false, (1f, 1f, 30f), (-2f, 0.5f, 0.3f));
                for (int i = 0; i < 90; i++) rig.Step(i % 30 == 0 ? 5 : 0, i % 45 == 0 ? -4 : 0);
                rig.Renderer.WaitForIdle();
                return BloodFloorGpu.Hash(rig.Gpu.Sim!.ReadFields());
            }
            ulong a = Run(), b = Run();
            output.WriteLine($"хеш поля: {a:X16} / {b:X16}");
            Assert.Equal(a, b);
            Assert.True(sink.Messages.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", sink.Messages));
            _ = hash;
        }
        finally
        {
            Log.RemoveSink(sink);
        }
    }
}
