using System.Numerics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;
using static Sword.Engine.Fluid2D.Tests.PainterKit;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// CPU-часть кисти и Painter на CPU-эталоне (GPU совпадает с ним — <see cref="Fluid2DPainterGpuTests"/>): единицы скорости, ключи и возраст,
/// джиттер по зерну, перестановки, бининг против полного перебора (решение (d)), точка за краем (решение (e)), палочка движущегося факела,
/// толчок в центре мазка.
/// </summary>
public sealed class Fluid2DBrushTests(ITestOutputHelper output)
{
    /// <summary>Факел на площадке 368² (кисть 184²), без джиттера.</summary>
    private static Fluid2DParams Torch(float bpr = 0) => Fluid2DParams.Torch with { BrushPositionRandom = bpr };

    private static readonly float TexelCm = 300f / 184;

    /// <summary>Шаг кисти: точки (ключ, мир) в заданном порядке.</summary>
    private static void StepBrush(Fluid2DBrush brush, ulong step, Vector3 center, params (Fluid2DBrushKey Key, Vector3 Position, float Scale)[] points)
    {
        brush.Begin(At(step), step, center);
        foreach (var (key, position, scale) in points) brush.Add(key, position, scale);
        brush.End();
    }

    private static Fluid2DPainterReference Painter(Fluid2DParams p, Fluid2DBrushTexel[]? old = null) => new(p, Noise(), null, old);

    // ── единицы и ключи ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>1 м/с: у площадки 3 м к камере — 0.1·V/E = 0.0333 UV, у мировой — 0.05; вверх по миру — к v = 0; поворот площадки — R⁻¹.</summary>
    [Theory]
    [InlineData(true, 1 / 30.0)]
    [InlineData(false, 0.05)]
    public void Velocity_Units(bool cameraFacing, double expected)
    {
        var p = Torch() with { CameraFacing = cameraFacing };
        var brush = new Fluid2DBrush(p, 1);
        for (int s = 0; s <= 20; s++) StepBrush(brush, (ulong)s, Vector3.Zero, (Key(1), new Vector3(s / 60f, 0.5f * s / 60f, 0), 0.34f));
        var v = brush.Points[0].UnitVelocity;
        output.WriteLine($"UV {v}");
        Assert.Equal(expected, v.X, 1e-5);
        Assert.Equal(-0.5 * expected, v.Y, 1e-5);

        // Площадка повёрнута на 90° вокруг мировой Y: её ось u — мировая −Z; движение по −Z даёт скорость вдоль u.
        var turned = new Fluid2DBrush(p, 1);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        for (int s = 0; s <= 20; s++)
        {
            turned.Begin(new Fluid2DStepInput((ulong)s, default, default, q), (ulong)s, Vector3.Zero);
            turned.Add(Key(1), new Vector3(0, 0, -s / 60f), 0.34f);
            turned.End();
        }
        Assert.Equal(expected, turned.Points[0].UnitVelocity.X, 1e-5);
        Assert.Equal(0, turned.Points[0].UnitVelocity.Y, 1e-6);
    }

    /// <summary>Первые 0.25 с (15 шагов) после активации V = 0, отрезок — с шестнадцатого; новый ключ, пропуск шага и Forget — без отрезка и скорости.</summary>
    [Fact]
    public void Keys_AgeAndForget()
    {
        var brush = new Fluid2DBrush(Torch(), 1);
        var moving = Key(1);
        var still = Key(2);
        void Run(int s, bool withMoving = true)
        {
            brush.Begin(At((ulong)s), (ulong)s, Vector3.Zero);
            if (withMoving) brush.Add(moving, new Vector3(s / 60f, 0, 0), 0.34f);
            brush.Add(still, new Vector3(0.3f, 0, 0), 0.34f);
            brush.End();
        }
        for (int s = 0; s < 15; s++)
        {
            Run(s);
            Assert.Equal(Vector3.Zero, Info(brush, moving).UnitVelocity);
            Assert.False(Info(brush, moving).Segment);
        }
        Run(15);
        Assert.NotEqual(Vector3.Zero, Info(brush, moving).UnitVelocity);
        Assert.False(Info(brush, moving).Segment);
        Run(16);
        Assert.True(Info(brush, moving).Segment);
        Assert.False(Info(brush, still).Segment);

        brush.Forget(moving);
        Run(17);
        Assert.Equal(Vector3.Zero, Info(brush, moving).UnitVelocity);
        Assert.False(Info(brush, moving).Segment);
        Run(18);
        Assert.True(Info(brush, moving).Segment);

        Run(19, withMoving: false);
        Assert.Equal(1, brush.Points.Length);
        Run(20);
        Assert.Equal(Vector3.Zero, Info(brush, moving).UnitVelocity);
        Run(21);
        Assert.True(Info(brush, moving).Segment);
        Assert.Throws<InvalidOperationException>(() =>
        {
            brush.Begin(At(22), 22, Vector3.Zero);
            brush.Add(still, Vector3.Zero, 0.3f);
            brush.Add(still, Vector3.Zero, 0.3f);
            brush.End();
        });
    }

    private static Fluid2DBrushPointInfo Info(Fluid2DBrush brush, Fluid2DBrushKey key)
    {
        foreach (var i in brush.Points)
            if (i.Key == key) return i;
        throw new InvalidOperationException($"нет точки {key}");
    }

    /// <summary>Джиттер: то же зерно и TickId — те же точки бит в бит; другое зерно — другие; размах ±10·BPR см по каждой оси.</summary>
    [Fact]
    public void Jitter_BySeedTickAndKey()
    {
        var p = Torch(0.5f);
        Fluid2DBrushPointInfo[] Run(ulong seed)
        {
            var b = new Fluid2DBrush(p, seed);
            var all = new List<Fluid2DBrushPointInfo>();
            for (int s = 0; s < 400; s++)
            {
                StepBrush(b, (ulong)(1000 + s), Vector3.Zero, (Key(7), Vector3.Zero, 0.34f), (Key(8), new Vector3(0.1f, 0, 0), 0.34f));
                all.AddRange(b.Points.ToArray());
            }
            return [.. all];
        }
        var a = Run(42);
        var same = Run(42);
        var other = Run(43);
        Assert.Equal(a, same);
        Assert.NotEqual(a.Select(i => i.Unit), other.Select(i => i.Unit));
        double min = double.MaxValue, max = double.MinValue;
        foreach (var i in a.Where(i => i.Key == Key(7)))
        {
            min = Math.Min(min, Math.Min(i.Unit.X, i.Unit.Y));
            max = Math.Max(max, Math.Max(i.Unit.X, i.Unit.Y));
        }
        output.WriteLine($"джиттер: {(min - 0.5) * 300:F2}…{(max - 0.5) * 300:F2} см");
        Assert.InRange((min - 0.5) * 300, -5.0, -4.8);
        Assert.InRange((max - 0.5) * 300, 4.8, 5.0);
    }

    /// <summary>
    /// Площадка к камере с камерой: центр — в центре, точка ближе к камере — дальше от центра (перенос на глубину центра), z — глубина;
    /// дальше Ez/2 по глубине и за камерой — отсеяны.
    /// </summary>
    [Fact]
    public void CameraFacing_PerspectiveToCentreDepth()
    {
        var brush = new Fluid2DBrush(Torch(), 1);
        var camera = new Vector3(0, 0, 5);
        brush.Begin(At(0), 0, Vector3.Zero, camera);
        brush.Add(Key(1), Vector3.Zero, 0.34f);
        brush.Add(Key(2), new Vector3(0.3f, 0, 1f), 0.34f);
        brush.Add(Key(3), new Vector3(0, 0, -2f), 0.34f);
        brush.Add(Key(4), new Vector3(0, 0, 6f), 0.34f);
        brush.End();
        Assert.Equal(new Vector3(0.5f), Info(brush, Key(1)).Unit);
        var near = Info(brush, Key(2)).Unit;
        Assert.Equal(0.5 + 0.3 * 1.25 / 3, near.X, 1e-5);
        Assert.Equal(0.5 + 1.0 / 3, near.Z, 1e-5);
        Assert.Equal(2, brush.Stats.Kept);
        Assert.Equal(2, brush.Stats.Culled);
    }

    // ── слияние, бининг, край ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Много точек в разном порядке добавления — те же числа точек, бининг и грид кисти бит в бит.</summary>
    [Fact]
    public void Permutation_DoesNotChangeResult()
    {
        var p = Torch(0.5f);
        var points = RandomPoints(40, 5);
        var a = PaintPermuted(p, points, 11);
        var b = PaintPermuted(p, [.. points.Reverse()], 11);
        var rng = new Random(3);
        var c = PaintPermuted(p, [.. points.OrderBy(_ => rng.Next())], 11);
        Assert.True(a.AsSpan().SequenceEqual(b), "обратный порядок");
        Assert.True(a.AsSpan().SequenceEqual(c), "случайный порядок");
    }

    private static Fluid2DBrushTexel[] PaintPermuted(Fluid2DParams p, (Fluid2DBrushKey Key, Vector3 Position, float Scale)[] points, int steps)
    {
        var brush = new Fluid2DBrush(p, 9);
        var painter = Painter(p);
        for (int s = 0; s < steps; s++)
        {
            float t = s / 60f;
            brush.Begin(At((ulong)(100 + s), new Vector2(0.001f, -0.0005f)), (ulong)(100 + s), Vector3.Zero);
            foreach (var (key, position, scale) in points) brush.Add(key, position + new Vector3(t * 0.8f, -t * 0.3f, 0), scale);
            brush.End();
            painter.Paint(brush, At((ulong)(100 + s), new Vector2(0.001f, -0.0005f)));
        }
        return painter.Grid;
    }

    private static (Fluid2DBrushKey Key, Vector3 Position, float Scale)[] RandomPoints(int count, int seed)
    {
        var rng = new Random(seed);
        var points = new (Fluid2DBrushKey, Vector3, float)[count];
        for (int i = 0; i < count; i++)
            points[i] = (Key((uint)rng.Next(1, 5), (ushort)i, (Fluid2DBrushSource)(i % 3)),
                new Vector3(rng.NextSingle() * 2.4f - 1.2f, rng.NextSingle() * 2.4f - 1.2f, rng.NextSingle() * 0.2f - 0.1f), 0.2f + rng.NextSingle() * 2.5f);
        return points;
    }

    /// <summary>
    /// Решение (d): бининг по ячейкам с отсечением по AABB ±4σ·stretch против перебора всех точек каждым текселем — |ΔPaint.ρ| и
    /// |ΔPaint.v| / max|Paint.v| ≤ 10⁻³ (за 4σ f &lt; 3.4·10⁻⁴, s_h &lt; 3.4·10⁻⁷).
    /// </summary>
    [Fact]
    public void Binning_MatchesFullScan()
    {
        var p = Torch(0.5f);
        var points = RandomPoints(48, 17);
        var brush = new Fluid2DBrush(p, 5);
        var binned = Painter(p, PreviousPaint(p.BrushResolution));
        var full = Painter(p, PreviousPaint(p.BrushResolution));
        for (int s = 0; s < 30; s++)
        {
            float t = s / 60f;
            brush.Begin(At((ulong)s), (ulong)s, Vector3.Zero);
            foreach (var (key, position, scale) in points) brush.Add(key, position + new Vector3(t * 1.5f, t * 0.7f, 0), scale);
            brush.End();
            binned.Paint(brush, At((ulong)s));
            full.Paint(brush, At((ulong)s), fullScan: true);
        }
        double dRho = 0, dV = 0, maxV = 0;
        for (int i = 0; i < binned.Grid.Length; i++)
        {
            var a = binned.Grid[i];
            var b = full.Grid[i];
            dRho = Math.Max(dRho, Math.Abs((float)a.Density - (float)b.Density));
            dV = Math.Max(dV, Math.Max(Math.Abs((float)a.VelocityX - (float)b.VelocityX), Math.Abs((float)a.VelocityY - (float)b.VelocityY)));
            maxV = Math.Max(maxV, Math.Max(Math.Abs((float)b.VelocityX), Math.Abs((float)b.VelocityY)));
        }
        output.WriteLine($"{brush.Stats}; |Δρ| {dRho:G3}, |Δv| {dV:G3} при max|v| {maxV:G3}");
        Assert.True(brush.Stats.Entries > brush.Stats.Kept, "бининг не разложил точки по ячейкам");
        Assert.True(dRho <= 1e-3, $"Δρ {dRho}");
        Assert.True(dV <= 1e-3 * maxV, $"Δv {dV} при {maxV}");
    }

    /// <summary>
    /// Решение (e): точка на 5 см за краем площадки остаётся (AABB штампа пересекает площадку) и оставляет полумазок; у L2 её отбросил бы
    /// строгий отбор 0 &lt; U &lt; 1. За 4σ + запас от края — отсеяна.
    /// </summary>
    [Fact]
    public void PointBeyondEdge_LeavesHalfStroke()
    {
        var p = Torch() with { CameraFacing = false, PainterEdgeMaskWidth = 0 };
        var brush = new Fluid2DBrush(p, 1);
        const float s = 0.633f;                  // стопа песка: σ 6.0 см
        brush.Begin(At(0), 0, Vector3.Zero);
        brush.Add(Key(1), new Vector3(-1.55f, 0, 0), s);
        brush.Add(Key(2), new Vector3(1.9f, 0, 0), s);
        brush.End();
        Assert.Equal(1, brush.Stats.Kept);
        Assert.Equal(1, brush.Stats.Culled);
        Assert.True(brush.Points[0].Unit.X < 0, "голова за краем");
        var painter = Painter(p);
        painter.Paint(brush, At(0));
        int bn = p.BrushResolution, row = bn / 2;
        float edge = (float)painter.Grid[row * bn].Density;
        float at20 = (float)painter.Grid[row * bn + (int)(20 / TexelCm)].Density;
        float at35 = (float)painter.Grid[row * bn + (int)(35 / TexelCm)].Density;
        output.WriteLine($"Paint.ρ у края {edge:F3}, на 20 см {at20:G3}, на 35 см {at35:G3}; σ {brush.Points[0].SigmaCm:F2} см");
        Assert.True(edge > 0.3f, $"у края {edge}");
        Assert.True(at20 < edge && at35 == 0, $"{at20}, {at35}");
    }

    // ── факел ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Факел на своей площадке (U — центр, мировая скорость ≠ 0), джиттер ±5 см: мазок — палочка между дрожащими точками, в среднем 5.2 см
    /// (до 14); площадь Paint.ρ ≥ 0.5 за шаг (прошлая краска 0) — как офлайн-модель BR <c>torch_stick.out.txt</c>: s 0.3285 — 83.7 см²
    /// в движении, 42.5 стоя; s 0.35 — 89.6 и 48.4. Стоящий факел — круг σ = 9.496·s см.
    /// </summary>
    [Theory]
    [InlineData(0.3285f, 83.7, 42.5)]
    [InlineData(0.35f, 89.6, 48.4)]
    public void Torch_MovingLeavesStick_StandingLeavesCircle(float s, double movingArea, double standingArea)
    {
        var p = Torch(0.5f) with { BrushDensityNoise = 0 };
        var (moving, stick, maxStick) = TorchArea(p, s, 1f);
        var (standing, _, _) = TorchArea(p, s, 0f);
        output.WriteLine($"s {s}: в движении {moving:F1} см² (палочка {stick:F2} см, до {maxStick:F1}), стоя {standing:F1} см²");
        Assert.InRange(stick, 4.7, 5.7);
        Assert.InRange(maxStick, 9, 14.15);
        Assert.InRange(moving, 0.94 * movingArea, 1.06 * movingArea);
        Assert.InRange(standing, 0.92 * standingArea, 1.08 * standingArea);
        var brush = new Fluid2DBrush(p, 1);
        StepBrush(brush, 0, Vector3.Zero, (Key(1), Vector3.Zero, s));
        Assert.Equal(9.496 * s, brush.Points[0].SigmaCm, 0.01);
    }

    private (double Area, double Stick, double MaxStick) TorchArea(Fluid2DParams p, float s, float speed)
    {
        var brush = new Fluid2DBrush(p, 77);
        const int warm = 20, steps = 240;
        double area = 0, stick = 0, maxStick = 0;
        int bn = p.BrushResolution;
        for (int k = 0; k < warm + steps; k++)
        {
            var position = new Vector3(speed * k / 60f, 0, 0);
            StepBrush(brush, (ulong)k, position, (Key(1), position, s));
            if (k < warm) continue;
            var info = brush.Points[0];
            double len = Math.Sqrt(Math.Pow(info.Unit.X - info.Prev.X, 2) + Math.Pow(info.Unit.Y - info.Prev.Y, 2)) * 300;
            stick += len;
            maxStick = Math.Max(maxStick, len);
            var painter = Painter(p);
            painter.Paint(brush, At((ulong)k));
            int count = 0;
            foreach (var t in painter.Grid)
                if ((float)t.Density >= 0.5f) count++;
            area += count * TexelCm * TexelCm;
        }
        return (area / steps, speed > 0 ? stick / steps : 0, maxStick);
    }

    /// <summary>
    /// Толчок в центре мазка (шум плотности выкл., как у таблиц BR §4.4): Δv Composite = 0.5·20·VFBM·Paint.x = 0.065 при 1 м/с и 0.377 при
    /// 3 м/с (s 0.57, единицы факела: (V/30)^1.6·15).
    /// </summary>
    [Theory]
    [InlineData(1f, 0.0651)]
    [InlineData(3f, 0.3768)]
    public void Torch_PushAtHead(float speed, double expected)
    {
        var p = Torch() with { BrushDensityNoise = 0 };
        var brush = new Fluid2DBrush(p, 1);
        int bn = p.BrushResolution;
        const int last = 30;
        float headX = 3f * ((bn / 2 + 0.5f) / bn - 0.5f);
        for (int k = 0; k <= last; k++)
            StepBrush(brush, (ulong)k, Vector3.Zero, (Key(1), new Vector3(headX + speed * (k - last) / 60f, 3f * (0.5f - (bn / 2 + 0.5f) / bn), 0), 0.57f));
        var painter = Painter(p);
        painter.Paint(brush, At(last));
        var head = painter.Grid[(bn / 2) * bn + bn / 2];
        double push = 0.5 * 20 * p.VeloFromBrushMotion * (float)head.VelocityX;
        output.WriteLine($"{speed} м/с: Paint {(float)head.VelocityX:G4}, толчок {push:F4} ед., ρ {(float)head.Density:F3}");
        Assert.Equal(expected, push, expected * 0.01);
        Assert.Equal(0, (float)head.VelocityY, 1e-4);
    }

    // ── прочее ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Сверх BrushMaxPoints остаются меньшие ключи, остальные — «отброшено»; Z-полоса отсекает точку дальше Ez/2 от плоскости.</summary>
    [Fact]
    public void Limits_DropLargestKeys_AndZBand()
    {
        var p = Torch() with { BrushMaxPoints = 4, BrushMaxEntries = 16 };
        var brush = new Fluid2DBrush(p, 1);
        brush.Begin(At(0), 0, Vector3.Zero);
        for (uint i = 10; i > 0; i--) brush.Add(Key(i), new Vector3(0.01f * i, 0, 0), 0.34f);
        brush.Add(Key(99), new Vector3(0, 0, 1.6f), 0.34f);
        brush.End();
        Assert.Equal(4, brush.Stats.Kept);
        Assert.Equal(6, brush.Stats.Dropped);
        Assert.Equal(1, brush.Stats.Culled);
        Assert.Equal([1u, 2u, 3u, 4u], brush.Points.ToArray().Select(i => i.Key.Id));
        Assert.True(brush.Stats.Entries <= 16);
    }

    /// <summary>Кадр кисти (Begin/Add/End ×64 точки) после прогрева — 0 Б.</summary>
    [Fact]
    public void BrushStep_AllocatesNothing()
    {
        var p = Torch(0.5f);
        var brush = new Fluid2DBrush(p, 1);
        var points = RandomPoints(64, 3);
        void Run(int s)
        {
            brush.Begin(At((ulong)s), (ulong)s, Vector3.Zero);
            foreach (var (key, position, scale) in points) brush.Add(key, position + new Vector3(s * 0.01f, 0, 0), scale);
            brush.End();
        }
        for (int s = 0; s < 30; s++) Run(s);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int s = 30; s < 130; s++) Run(s);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes == 0, $"шаг кисти выделил {bytes / 100} Б");
        Assert.Equal(20 * 4, Marshal.SizeOf<Fluid2DBrushPoint>());
    }

    /// <summary>
    /// Контраст выхода ≤ 0 (pow(0, 0) на GPU — NaN, pow(0, &lt;0) — Inf) и размер направленного шума ≤ 0 (деление на ноль в масштабе шума)
    /// отвергаются до создания ресурсов; малые положительные — допустимы.
    /// </summary>
    [Theory]
    [InlineData(0f, 0.5f)]
    [InlineData(-1f, 0.5f)]
    [InlineData(float.NaN, 0.5f)]
    [InlineData(1f, 0f)]
    [InlineData(1f, -0.5f)]
    [InlineData(1f, float.PositiveInfinity)]
    public void Validate_RejectsNonPositiveContrastAndDirNoiseSize(float contrast, float dirNoiseSize)
    {
        var p = Torch() with { FluidDensityContrast = contrast, VeloDirNoiseSize = dirNoiseSize };
        var error = Assert.Throws<ArgumentOutOfRangeException>(p.Validate);
        Assert.Equal(contrast > 0 ? nameof(Fluid2DParams.VeloDirNoiseSize) : nameof(Fluid2DParams.FluidDensityContrast), error.ParamName);
        (Torch() with { FluidDensityContrast = 0.01f, VeloDirNoiseSize = 0.001f }).Validate();
    }
}
