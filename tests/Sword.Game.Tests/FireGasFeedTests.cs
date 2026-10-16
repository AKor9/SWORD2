using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Diagnostics;
using Sword.Engine.Fluid2D;
using Sword.Engine.Fluid3D;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Fire;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Дым из пламени: карта источника 3D-газа по полю 2D-сима пламени (<see cref="FireGasFeed"/>). CPU — нулевое поле даёт нулевую карту, вне
/// зоны у шишки и ниже её низа — ни клетки, скорость клетки — скорость поля в м/с (x — по оси площадки, y — по вертикали; газ у площадки
/// приходит к скорости клетки — Fluid3DSourceTests). GPU (слой отладки, GPU-валидация) — проход <c>fire.gas.feed</c> = эталон в пределах 2 ULP half;
/// контроль — поле с перевёрнутыми рядами.
/// </summary>
[Collection("game-gpu")]
[Trait("Category", "Gpu")]
public sealed class FireGasFeedTests : IDisposable, ILogSink
{
    private const double Ulps = 2;
    private static readonly Fluid2DParams Sim = FireTuning.DefaultParams;
    private static readonly Quaternion Rotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.7f, -0.35f, 0f));
    private static readonly Vector3 Knob = new(0.4f, 1.1f, -0.3f);
    private readonly ITestOutputHelper _output;
    private readonly List<string> _d3d12 = [];
    private bool _sink;

    public FireGasFeedTests(ITestOutputHelper output)
    {
        _output = output;
        Log.AddSink(this);
        _sink = true;
    }

    void ILogSink.Write(in LogEntry entry)
    {
        if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) _d3d12.Add(entry.Message);
    }

    public void Dispose()
    {
        if (_sink) Log.RemoveSink(this);
        _sink = false;
    }

    private static FireFeedConstants Numbers(FireGasFeedParams? feed = null) =>
        FireFeedConstants.Build(Sim, Sim.Extents, Rotation, FlameParams.Torch4, feed ?? FireGasFeedParams.Torch, 1f / 60f, 0.1f, 0);

    /// <summary>
    /// Поле, похожее на факел: ядро ρ ≈ 1 у шишки, хвост вверх по площадке с ρ, спадающей до 0.1 к 25 см, струя скорости вверх и вбок с
    /// шумом (детерминированно).
    /// </summary>
    private static Half[] Flame(int n, int seed = 3)
    {
        var rng = new Random(seed);
        var c = new Half[4 * n * n];
        float texel = Sim.Extents.X / n;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = (x + 0.5f - 0.5f * n) * texel, py = (0.5f * n - y - 0.5f) * texel;
                float core = MathF.Exp(-(px * px + py * py) / (2 * 0.03f * 0.03f));
                float tail = py > 0 ? MathF.Exp(-px * px / (2 * 0.04f * 0.04f)) * MathF.Exp(-py / 0.12f) * 0.8f : 0;
                float rho = MathF.Min(1f, core + tail);
                int i = 4 * (y * n + x);
                c[i] = (Half)(0.4f * MathF.Sin(py * 30) + 0.2f * ((float)rng.NextDouble() - 0.5f));
                c[i + 1] = (Half)(-1.5f * rho + 0.2f * ((float)rng.NextDouble() - 0.5f));
                c[i + 2] = (Half)rho;
                c[i + 3] = (Half)0.1f;
            }
        return c;
    }

    private static Half[] Uniform(int n, float rho, Vector2 velocity)
    {
        var c = new Half[4 * n * n];
        for (int i = 0; i < n * n; i++)
        {
            c[4 * i] = (Half)velocity.X;
            c[4 * i + 1] = (Half)velocity.Y;
            c[4 * i + 2] = (Half)rho;
        }
        return c;
    }

    private static int Side => FireGasFeedParams.Torch.Side;

    private static float TexelRadius(int i, int j) =>
        new Vector2((i + 0.5f - 0.5f * Side) * FireGasFeed.CellSize, (j + 0.5f - 0.5f * Side) * FireGasFeed.CellSize).Length();

    private static float TexelHeight(int j) => (j + 0.5f - 0.5f * Side) * FireGasFeed.CellSize;

    [Fact]
    public void ZeroField_ZeroMap_NothingOutsideZoneOrBelowFloor_FlameCoreGivesLessThanTail()
    {
        var k = Numbers();
        var zero = FireGasFeed.ReferenceMap(new Half[4 * Sim.Resolution * Sim.Resolution], k);
        Assert.All(zero, t => Assert.Equal(default, t));
        var feed = FireGasFeedParams.Torch;
        var map = FireGasFeed.ReferenceMap(Flame(Sim.Resolution), k);
        int fed = 0;
        for (int j = 0; j < Side; j++)
            for (int i = 0; i < Side; i++)
            {
                var t = map[i + Side * j];
                bool outside = TexelRadius(i, j) >= feed.Radius || TexelHeight(j) <= feed.Floor - 0.5f * feed.ZoneEdge;
                if (outside) Assert.Equal(default, t);
                else if (t.Density > 0) fed++;
            }
        // Ядро у шишки (видимое пламя) даёт меньше, чем хвост над ним, где пламя гаснет.
        int mid = Side / 2;
        float core = map[mid + Side * (mid + 1)].Density, tail = map[mid + Side * (mid + 5)].Density;
        _output.WriteLine($"клеток с впрыском {fed}; у ядра {core:G3}, хвост +15 см {tail:G3}");
        Assert.True(fed > 40, $"клеток {fed}");
        Assert.True(tail > 2 * core, $"хвост {tail}, ядро {core}");
    }

    [Fact]
    public void MapSide_IsParameter_ZoneNotBeyondHalfMap()
    {
        var feed = FireGasFeedParams.Torch;
        Assert.InRange(feed.Side * FireGasFeed.CellSize, 0.95f, 1.05f);
        feed.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => (feed with { Radius = feed.HalfMap + 0.01f }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (feed with { Side = FireGasFeed.MaxSide + 1 }).Validate());
        // Меньшая карта — те же клетки у шишки, что большая в своей середине (зона внутри обеих).
        var small = feed with { Side = 22, Radius = 0.3f };
        var big = FireGasFeed.ReferenceMap(Flame(Sim.Resolution), Numbers(small with { Side = feed.Side }));
        var part = FireGasFeed.ReferenceMap(Flame(Sim.Resolution), Numbers(small));
        int d = (feed.Side - small.Side) / 2;
        for (int j = 0; j < small.Side; j++)
            for (int i = 0; i < small.Side; i++) Assert.Equal(big[i + d + feed.Side * (j + d)], part[i + small.Side * j]);
    }

    [Fact]
    public void Velocity_IsFieldVelocityInMetresPerSecond()
    {
        // ρ 0.3 всюду (пламя почти невидимо), скорость поля (0.5, −1) ед.: 1 ед. = SimSpeed/N·E за шаг 1/60 с = 2/368·3·60 = 0.978 м/с;
        // x — по оси x площадки, y (вверх, v растёт вниз) — по мировой вертикали, делённой на долю вертикали в оси y площадки.
        var k = Numbers();
        var map = FireGasFeed.ReferenceMap(Uniform(Sim.Resolution, 0.3f, new Vector2(0.5f, -1f)), k);
        float unit = 2f / 368f * 3f * 60f;
        var ay = Vector3.Transform(Vector3.UnitY, Rotation);
        var expected = Vector3.Transform(Vector3.UnitX, Rotation) * (0.5f * unit) + Vector3.UnitY * (unit / MathF.Max(ay.Y, 0.5f));
        int mid = Side / 2;
        var t = map[mid + Side * (mid + 2)];
        _output.WriteLine($"скорость клетки {t.Velocity} м/с, ожидание {expected}; доля {t.BlendValue:G3}, ρ за шаг {t.Density:G3}");
        Assert.True(Vector3.Distance(t.Velocity, expected) < 2e-3f * expected.Length(), $"{t.Velocity} против {expected}");
    }

    [Fact]
    public void Gpu_MatchesReference_FlippedRowsFail()
    {
        int n = Sim.Resolution;
        var field = Flame(n);
        var flipped = new Half[field.Length];
        for (int y = 0; y < n; y++) Array.Copy(field, 4 * n * y, flipped, 4 * n * (n - 1 - y), 4 * n);
        var reference = FireGasFeed.ReferenceMap(field, Numbers());
        using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.FireGasFeed", 64, 64, Activate: false));
        var contract = new ShaderContract();
        Renderer.Contribute(contract);
        FireGasFeed.Contribute(contract);
        using var renderer = new Renderer(new RendererOptions(window.NativeHandle, 64, 64, DebugLayer: true,
            ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        using var feed = new FireGasFeed(renderer, "test", Side);
        Fluid3DMapTexel[] Run(Half[] c)
        {
            using var storage = renderer.CreateStorage<ulong>("test.field", MemoryMarshal.Cast<Half, ulong>(c.AsSpan()));
            FireGasFeed.Entry entry = default;
            window.PumpMessages();
            renderer.Render(f =>
            {
                // Вторая карта кадра — с клетки Side²: проверка кольца.
                feed.Add(f, 4, storage, Knob, Rotation, Numbers(), FireGasFeedParams.Torch);
                entry = feed.Add(f, 5, storage, Knob, Rotation, Numbers(), FireGasFeedParams.Torch);
            }, vsync: false);
            renderer.WaitForIdle();
            Assert.Equal(Side * Side, entry.Offset);
            var all = new Fluid3DMapTexel[FireGasFeed.Ring * Side * Side];
            renderer.ReadStorage(feed.Map, all.AsSpan());
            return all.AsSpan(entry.Offset, Side * Side).ToArray();
        }
        double same = Worst(Run(field), reference), control = Worst(Run(flipped), reference);
        _output.WriteLine($"карта GPU против эталона: {same:F2} ULP half; поле с перевёрнутыми рядами: {control:G4}");
        Assert.True(same <= Ulps, $"{same} ULP");
        Assert.True(control > 100 * Ulps, $"контроль {control}");
        Assert.True(_d3d12.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _d3d12));
    }

    /// <summary>Наибольшее расхождение клеток в ULP half по всем каналам (знак учтён).</summary>
    private static double Worst(Fluid3DMapTexel[] gpu, Fluid3DMapTexel[] cpu)
    {
        double worst = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            var g = gpu[i];
            var c = cpu[i];
            worst = Math.Max(worst, Math.Max(Math.Max(Diff(g.Rates, c.Rates), Diff(g.Rates >> 16, c.Rates >> 16)), Diff(g.Blend, c.Blend)));
            worst = Math.Max(worst, Math.Max(Math.Max(Diff(g.VelocityXY, c.VelocityXY), Diff(g.VelocityXY >> 16, c.VelocityXY >> 16)),
                Diff(g.VelocityZ, c.VelocityZ)));
        }
        return worst;
    }

    private static double Diff(uint a, uint b) => Math.Abs(Ordered((ushort)a) - Ordered((ushort)b));

    private static int Ordered(ushort h) => (h & 0x8000) != 0 ? -(h & 0x7FFF) : h;
}
