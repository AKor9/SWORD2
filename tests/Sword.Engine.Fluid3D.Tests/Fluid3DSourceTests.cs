using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Fluid3D.Tests.Fluid3DTestKit;

namespace Sword.Engine.Fluid3D.Tests;

/// <summary>
/// Источники по траектории на CPU-эталоне (впрыск GPU = эталон — <see cref="Fluid3DGpuTests"/>): неподвижный источник, след взмаха 10 м/с,
/// ключи, порядок, детерминизм и мерцание; устойчивость тёплого старта давления в <see cref="Fluid3DParams.Validate"/>.
/// </summary>
public sealed class Fluid3DSourceTests(ITestOutputHelper output)
{
    /// <summary>Факел без мерцания.</summary>
    private static readonly Fluid3DEmitterParams Steady = Fluid3DEmitterParams.Torch with { Flicker = 0 };

    private static void Emit(Fluid3DEmitters e, ulong step, float dt, Fluid3DEmitterParams p, params (ulong Key, Vector3 At)[] points)
    {
        e.Begin(step, dt);
        foreach (var (key, at) in points) e.Add(key, at, p);
        e.End();
    }

    private static (float Density, float Temperature) ScalarAt(Half[] s, Fluid3DFields f, int x, int y, int z) =>
        ((float)s[2 * f.Index(x, y, z)], (float)s[2 * f.Index(x, y, z) + 1]);

    private static Vector3 VelocityAt(Half[] v, Fluid3DFields f, int x, int y, int z)
    {
        int i = 4 * f.Index(x, y, z);
        return new Vector3((float)v[i], (float)v[i + 1], (float)v[i + 2]);
    }

    private static double DensitySum(Fluid3DFields f)
    {
        double sum = 0;
        for (int i = 0; i < f.Cells; i++) sum += (float)f.Scalars[2 * i];
        return sum;
    }

    /// <summary>Неподвижный источник (тихий газ): ρ и T в центре растут на rate·dt за шаг, Σρ ∝ dt; вне шара — пусто.</summary>
    [Fact]
    public void StillSource_GrowsInSphere_ProportionalToDt()
    {
        var torch = Steady with { Velocity = Vector3.Zero, VelocityRate = 1000 };
        var sums = new double[2];
        float[] steps = [1f / 60, 1f / 30];
        for (int k = 0; k < 2; k++)
        {
            float dt = steps[k];
            var p = Quiet(16, 16, 16) with { PressureIterations = 0, StepSeconds = dt };
            var w = WindowAt(p);
            var centre = w.ToWorld(new Vector3(8, 8, 8));
            var e = new Fluid3DEmitters(1);
            var cpu = new Fluid3DReference(p, null, w);
            for (ulong s = 0; s < 3; s++)
            {
                Emit(e, s, dt, torch, (7, centre));
                cpu.Step(e.Step, w.Center, e.Sources);
                var (rho, t) = ScalarAt(cpu.Fields.Scalars, cpu.Fields, 8, 8, 8);
                Assert.Equal((s + 1) * torch.Density * dt, rho, (s + 1) * torch.Density * dt * 2e-3f);
                Assert.Equal((s + 1) * torch.Temperature * dt, t, (s + 1) * torch.Temperature * dt * 2e-3f);
            }
            Assert.Equal(0f, ScalarAt(cpu.Fields.Scalars, cpu.Fields, 8, 8, 12).Density);
            sums[k] = DensitySum(cpu.Fields);
            output.WriteLine($"dt {dt:F4} с: 3 шага — ρ в центре {ScalarAt(cpu.Fields.Scalars, cpu.Fields, 8, 8, 8).Density:F4}, Σρ {sums[k]:F4}");
        }
        Assert.Equal(2, sums[1] / sums[0], 0.01);
    }

    /// <summary>
    /// Неподвижный источник в ветре: скорость газа в шаре — к нулю (стадия источников при доле 1 — ровно 0); за 30 шагов с плавучестью
    /// горизонтальная скорость у источника — меньше 40 % ветра при умолчании (30/с, доля ½ за шаг; замер 31 %) и меньше 25 % при доле 1
    /// (60/с; 18 % — остаток дают проекция давления и адвекция вокруг «застывшего» шара); вертикальная — вверх (плавучесть).
    /// </summary>
    [Fact]
    public void StillSource_StillsTheGas_ExceptBuoyancy()
    {
        var wind = new Vector3(1.5f, 0, 1);
        var p = Small(16, 24, 16);
        var w = WindowAt(p);
        var start = new Fluid3DFields(p.Nx, p.Ny, p.Nz);
        for (int i = 0; i < start.Cells; i++)
        {
            start.Velocity[4 * i] = (Half)wind.X;
            start.Velocity[4 * i + 2] = (Half)wind.Z;
        }
        var centre = w.ToWorld(new Vector3(8, 6, 8));
        var e = new Fluid3DEmitters(1);
        Emit(e, 0, p.StepSeconds, Steady with { Velocity = Vector3.Zero, VelocityRate = 1000 }, (1, centre));
        var stage = new Fluid3DReference(p, start.Clone(), w);
        stage.Stage(Fluid3DStage.Sources, stage.Constants(0, w.Center, e.Sources, out _));
        Assert.Equal(Vector3.Zero, VelocityAt(stage.Fields.VelocityScratch, stage.Fields, 8, 6, 8));
        Assert.Equal(wind, VelocityAt(stage.Fields.VelocityScratch, stage.Fields, 8, 6, 13));

        float windSpeed = new Vector2(wind.X, wind.Z).Length();
        foreach (var (rate, limit) in new[] { (Steady.VelocityRate, 0.4f), (60f, 0.25f) })
        {
            var torch = Steady with { Velocity = Vector3.Zero, VelocityRate = rate };
            var run = new Fluid3DEmitters(1);
            var cpu = new Fluid3DReference(p, start.Clone(), w);
            for (ulong s = 0; s < 30; s++)
            {
                Emit(run, s, p.StepSeconds, torch, (1, centre));
                cpu.Step(run.Step, w.Center, run.Sources);
            }
            var v = VelocityAt(cpu.Fields.Velocity, cpu.Fields, 8, 6, 8);
            var far = VelocityAt(cpu.Fields.Velocity, cpu.Fields, 2, 20, 2);
            float horizontal = new Vector2(v.X, v.Z).Length();
            output.WriteLine($"ветер {windSpeed:F2} м/с, доля {rate}/с: у источника через 30 шагов v = {v} (горизонтально {horizontal / windSpeed:P0} ветра), вдали {far}");
            Assert.True(horizontal < limit * windSpeed, $"доля {rate}/с: горизонтальная скорость у источника {horizontal}");
            Assert.True(v.Y > 0.02f, $"плавучесть: vy {v.Y}");
        }
    }

    /// <summary>
    /// Взмах 10 м/с (16.7 см за шаг, радиус 5 см, край — ячейка): новый ключ — шар, следующий шаг — капсула от прошлой позиции; ρ по оси между
    /// позициями — ровно впрыск шага (нет «бусин»), длина следа по оси — |Δp| + 2R ± ячейка. Контроль: шары в концах — посередине пусто.
    /// Скорость газа в следе (доля 1) — ровно унос × V.
    /// </summary>
    [Fact]
    public void Swing_LeavesContinuousCapsule_AndGasTakesScaledSpeed()
    {
        var p = Quiet(64, 16, 16) with { PressureIterations = 0 };
        var w = WindowAt(p);
        float dt = p.StepSeconds, speed = 10;
        var torch = Steady with { TrailConservation = 0, Velocity = Vector3.Zero, VelocityScale = 0, VelocityRate = 0 };
        Vector3 At(int s) => w.ToWorld(new Vector3(6, 8, 8)) + new Vector3(speed * dt * s, 0, 0);

        var e = new Fluid3DEmitters(1);
        var cpu = new Fluid3DReference(p, null, w);
        Emit(e, 0, dt, torch, (1, At(0)));
        Assert.Null(e.Sources[0].From);
        Assert.Equal(Vector3.Zero, e.Points[0].Velocity);
        cpu.Step(e.Step, w.Center, e.Sources);
        Emit(e, 1, dt, torch, (1, At(1)));
        Assert.Equal(At(0), e.Sources[0].From);
        Assert.Equal(speed, e.Points[0].Velocity.X, 1e-3f);
        var spheres = e.Sources.ToArray().Select(s => s with { From = null }).ToArray();
        var beads = new Fluid3DReference(p, cpu.Fields.Clone(), cpu.Window);
        cpu.Step(e.Step, w.Center, e.Sources);
        beads.Step(e.Step, w.Center, spheres);

        float perStep = torch.Density * dt;
        float head = 6 + speed * dt / p.CellSize;
        int first = -1, last = -1;
        for (int x = 0; x < p.Nx; x++)
        {
            float rho = ScalarAt(cpu.Fields.Scalars, cpu.Fields, x, 8, 8).Density;
            if (rho > 0 && first < 0) first = x;
            if (rho > 0) last = x;
            if (x >= 6 && x <= head) Assert.True(MathF.Abs(rho - perStep) <= perStep * 2e-3f, $"x {x}: ρ {rho}, шаг {perStep}");
        }
        float rOut = torch.Radius / p.CellSize + 0.5f;
        float expected = speed * dt / p.CellSize + 2 * rOut;
        output.WriteLine($"след по оси: ячейки {first}…{last} ({last - first + 1}), ожидание |Δp| + 2R = {expected:F2}; ρ шага {perStep:F4}");
        Assert.True(MathF.Abs(last - first + 1 - expected) <= 1, $"длина {last - first + 1}, ожидание {expected}");
        Assert.Equal(0f, ScalarAt(beads.Fields.Scalars, beads.Fields, 9, 8, 8).Density);
        Assert.True(ScalarAt(cpu.Fields.Scalars, cpu.Fields, 9, 8, 8).Density > 0.99f * perStep);

        var carry = torch with { VelocityScale = 0.5f, VelocityRate = 1000 };
        var e2 = new Fluid3DEmitters(1);
        Emit(e2, 0, dt, carry, (1, At(0)));
        Emit(e2, 1, dt, carry, (1, At(1)));
        var stage = new Fluid3DReference(p, null, w);
        stage.Stage(Fluid3DStage.Sources, stage.Constants(1, w.Center, e2.Sources, out _));
        for (int x = 9; x <= 12; x++) Assert.Equal(new Vector3(0.5f * speed, 0, 0), VelocityAt(stage.Fields.VelocityScratch, stage.Fields, x, 8, 8));
    }

    /// <summary>
    /// Цепочка взмаха 10 м/с (8 шагов) с сохранением выброса: след по всей длине ровный — max/min по x в каждой точке сечения ≤ 1.02 (стыки
    /// шагов не толще середины); ρ на оси — впрыск шага × 4r/3L; выброс шага — как у неподвижного шара (± 15 %).
    /// </summary>
    [Fact]
    public void SwingChain_HasNoBeads_AndConservesEmission()
    {
        var p = Quiet(64, 16, 16) with { PressureIterations = 0 };
        var w = WindowAt(p);
        float dt = p.StepSeconds, speed = 10;
        var torch = Steady with { Velocity = Vector3.Zero, VelocityScale = 0, VelocityRate = 0 };
        var e = new Fluid3DEmitters(1);
        var cpu = new Fluid3DReference(p, null, w);
        var perStep = new double[8];
        double before = 0;
        for (int s = 0; s < 8; s++)
        {
            Emit(e, (ulong)s, dt, torch, (1, w.ToWorld(new Vector3(6, 8, 8)) + new Vector3(speed * dt * s, 0, 0)));
            cpu.Step(e.Step, w.Center, e.Sources);
            double sum = DensitySum(cpu.Fields);
            perStep[s] = sum - before;
            before = sum;
        }
        float step = speed * dt / p.CellSize, rOut = torch.Radius / p.CellSize + 0.5f;
        int x0 = (int)MathF.Ceiling(6 + rOut + 1), x1 = (int)MathF.Floor(6 + 7 * step);
        float worst = 1;
        for (int z = 6; z <= 10; z++)
            for (int y = 6; y <= 10; y++)
            {
                float lo = float.MaxValue, hi = 0;
                for (int x = x0; x <= x1; x++)
                {
                    float rho = ScalarAt(cpu.Fields.Scalars, cpu.Fields, x, y, z).Density;
                    lo = MathF.Min(lo, rho);
                    hi = MathF.Max(hi, rho);
                }
                if (hi == 0) continue;
                Assert.True(lo > 0, $"разрыв следа в сечении ({y}, {z})");
                worst = MathF.Max(worst, hi / lo);
            }
        float axis = ScalarAt(cpu.Fields.Scalars, cpu.Fields, (x0 + x1) / 2, 8, 8).Density;
        float trail = 4 * torch.Radius / (3 * speed * dt);
        output.WriteLine($"след x {x0}…{x1}: max/min по сечению ≤ {worst:F4}; ρ на оси {axis:F5} (впрыск шага × 4r/3L = {torch.Density * dt * trail:F5})");
        output.WriteLine($"выброс по шагам Σρ: {string.Join(", ", perStep.Select(v => v.ToString("F4")))}");
        Assert.True(worst <= 1.02f, $"бусины: max/min {worst}");
        Assert.Equal(torch.Density * dt * trail, axis, torch.Density * dt * trail * 5e-3f);
        for (int s = 1; s < 8; s++) Assert.True(Math.Abs(perStep[s] / perStep[0] - 1) < 0.15, $"шаг {s}: выброс {perStep[s]} против {perStep[0]}");
    }

    /// <summary>Ключи: новый — шар без скорости; пропал — источника нет (Σρ стоит); вернулся, пропуск шага, скачок, Forget — снова новый.</summary>
    [Fact]
    public void Keys_NewMissingReturningJumpAndForget()
    {
        var p = Quiet(24, 16, 16) with { PressureIterations = 0 };
        var w = WindowAt(p);
        float dt = p.StepSeconds;
        var torch = Steady with { Velocity = Vector3.Zero, VelocityRate = 0 };
        var a = w.ToWorld(new Vector3(5, 5, 8));
        var b = w.ToWorld(new Vector3(15, 10, 8));
        var d = new Vector3(0.05f, 0, 0);
        var e = new Fluid3DEmitters(1);
        var cpu = new Fluid3DReference(p, null, w);
        void Do(ulong step, params (ulong, Vector3)[] points)
        {
            Emit(e, step, dt, torch, points);
            cpu.Step(e.Step, w.Center, e.Sources);
        }

        Do(0, (1, a), (2, b));
        Assert.All(e.Points.ToArray(), q => Assert.True(q.From is null && q.Velocity == Vector3.Zero));
        Assert.Equal(0, e.Stats.Segments);
        Do(1, (2, b + d), (1, a + d));
        Assert.Equal([1UL, 2UL], e.Points.ToArray().Select(q => q.Key));
        Assert.Equal(2, e.Stats.Segments);
        Assert.Equal(d.X / dt, e.Points[1].Velocity.X, 1e-3f);
        Do(2, (1, a + 2 * d));
        Assert.Equal(1, e.Sources.Length);
        double withOne = DensitySum(cpu.Fields);
        Emit(e, 3, dt, torch);
        cpu.Step(e.Step, w.Center, e.Sources);
        Assert.Equal(0, e.Sources.Length);
        Assert.Equal(withOne, DensitySum(cpu.Fields));
        Do(4, (1, a + 3 * d), (2, b));
        Assert.All(e.Points.ToArray(), q => Assert.Null(q.From));
        Do(5, (1, a + 4 * d), (2, b + new Vector3(0, 0.7f, 0)));
        Assert.NotNull(e.Points[0].From);
        Assert.True(e.Points[1].Jump && e.Points[1].From is null && e.Points[1].Velocity == Vector3.Zero, "скачок 42 м/с — не отрезок");
        Do(7, (1, a + 5 * d));
        Assert.Null(e.Points[0].From);
        Do(8, (1, a + 6 * d));
        Assert.NotNull(e.Points[0].From);
        e.Forget(1);
        Do(9, (1, a + 7 * d));
        Assert.Null(e.Points[0].From);
        e.Begin(10, dt);
        e.Add(3, a, torch);
        e.Add(3, b, torch);
        Assert.Throws<InvalidOperationException>(e.End);
    }

    /// <summary>Порядок Add не меняет шаг (сортировка по ключу): источники и поля эталона — бит в бит; сверх 8 ключей — меньшие ключи.</summary>
    [Fact]
    public void AddOrder_DoesNotMatter_AndExtraKeysAreDropped()
    {
        var p = Small(24, 24, 16);
        var w = WindowAt(p);
        Fluid3DReference Run(ulong[] order)
        {
            var e = new Fluid3DEmitters(3);
            var cpu = new Fluid3DReference(p, null, w);
            for (ulong s = 0; s < 12; s++)
            {
                e.Begin(s, p.StepSeconds);
                foreach (ulong key in order)
                    e.Add(key, w.ToWorld(new Vector3(4 + key, 5 + 0.7f * s, 8 + MathF.Sin(s + key))), Fluid3DEmitterParams.Torch, 0.5f + 0.1f * key);
                e.End();
                Assert.True(e.Points.ToArray().Select(q => q.Key).SequenceEqual(order.Order()));
                cpu.Step(e.Step, w.Center, e.Sources);
            }
            return cpu;
        }
        var a = Run([5, 2, 9, 0]).Fields;
        var b = Run([9, 0, 5, 2]).Fields;
        Assert.True(SameBits(a.Velocity, b.Velocity) && SameBits(a.Scalars, b.Scalars) && SameBits(a.Aux, b.Aux), "порядок ключей изменил поля");

        var many = new Fluid3DEmitters(1);
        many.Begin(0, p.StepSeconds);
        for (ulong key = 20; key > 9; key--) many.Add(key, w.ToWorld(new Vector3(key, 5, 8)), Fluid3DEmitterParams.Torch);
        many.End();
        Assert.Equal(new Fluid3DEmitterStats(0, 11, 8, 3, 0), many.Stats);
        Assert.Equal(Enumerable.Range(10, 8).Select(k => (ulong)k), many.Points.ToArray().Select(q => q.Key));
        many.Begin(1, p.StepSeconds);
        many.Add(20, w.ToWorld(new Vector3(20.5f, 5, 8)), Fluid3DEmitterParams.Torch);
        many.End();
        Assert.NotNull(many.Points[0].From);
    }

    /// <summary>Два прогона (одно зерно) — бит в бит; другое зерно — другое мерцание и другие поля; множитель в [1 − Flicker, 1], гладкий.</summary>
    [Fact]
    public void Flicker_IsDeterministic_AndSeedChangesIt()
    {
        var p = Small(16, 24, 16);
        var w = WindowAt(p);
        var torch = Fluid3DEmitterParams.Torch;
        (Fluid3DFields Fields, float[] Flicker) Run(ulong seed)
        {
            var e = new Fluid3DEmitters(seed);
            var cpu = new Fluid3DReference(p, null, w);
            var flicker = new float[40];
            for (ulong s = 0; s < 40; s++)
            {
                Emit(e, s, p.StepSeconds, torch, (4, w.ToWorld(new Vector3(8 + 3 * MathF.Sin(0.3f * s), 5, 8))));
                flicker[s] = e.Points[0].Flicker;
                cpu.Step(e.Step, w.Center, e.Sources);
            }
            return (cpu.Fields, flicker);
        }
        var a = Run(11);
        var b = Run(11);
        var c = Run(12);
        Assert.True(SameBits(a.Fields.Velocity, b.Fields.Velocity) && SameBits(a.Fields.Scalars, b.Fields.Scalars), "два прогона разошлись");
        Assert.Equal(a.Flicker, b.Flicker);
        Assert.False(SameBits(a.Fields.Scalars, c.Fields.Scalars));
        Assert.NotEqual(a.Flicker, c.Flicker);
        Assert.All(a.Flicker, f => Assert.InRange(f, 1 - torch.Flicker, 1f));
        float jump = a.Flicker.Zip(a.Flicker.Skip(1), (x, y) => MathF.Abs(y - x)).Max();
        output.WriteLine($"мерцание (зерно 11): {string.Join(" ", a.Flicker.Select(f => f.ToString("F2")))}; наибольший скачок за шаг {jump:F3}");
        Assert.True(jump < 0.5f * torch.Flicker, $"мерцание не гладкое: скачок {jump}");
        Assert.True(a.Flicker.Max() - a.Flicker.Min() > 0.2f * torch.Flicker, "мерцания нет");
    }

    /// <summary>
    /// Раскачка: 0 — скорость газа в источнике — ровно заданная (как без неё); Sway > 0 — к ней горизонталь в ±Sway по x и z (вертикаль та же),
    /// гладко по шагам, два прогона — бит в бит, ряды x и z и мерцание — разные шумы; отрицательная — исключение.
    /// </summary>
    [Fact]
    public void Sway_AddsSmoothBoundedHorizontalVelocity_ZeroChangesNothing()
    {
        var w = WindowAt(Small(16, 24, 16));
        var at = w.ToWorld(new Vector3(8, 5, 8));
        var sway = Steady with { Sway = 0.4f, SwaySteps = 8 };
        Vector3[] Run(Fluid3DEmitterParams p, ulong seed)
        {
            var e = new Fluid3DEmitters(seed);
            var v = new Vector3[48];
            for (ulong s = 0; s < 48; s++)
            {
                Emit(e, s, 1f / 60, p, (4, at));
                v[s] = e.Sources[0].Velocity;
            }
            return v;
        }
        Assert.All(Run(Steady, 3), v => Assert.Equal(Steady.Velocity, v));
        var a = Run(sway, 3);
        Assert.Equal(a, Run(sway, 3));
        Assert.All(a, v =>
        {
            Assert.Equal(Steady.Velocity.Y, v.Y);
            Assert.InRange(v.X - Steady.Velocity.X, -sway.Sway, sway.Sway);
            Assert.InRange(v.Z - Steady.Velocity.Z, -sway.Sway, sway.Sway);
        });
        float spanX = a.Max(v => v.X) - a.Min(v => v.X), spanZ = a.Max(v => v.Z) - a.Min(v => v.Z);
        float jump = a.Zip(a.Skip(1), (p, q) => MathF.Max(MathF.Abs(q.X - p.X), MathF.Abs(q.Z - p.Z))).Max();
        output.WriteLine($"раскачка: размах x {spanX:F3}, z {spanZ:F3} м/с, наибольший скачок за шаг {jump:F3}");
        Assert.True(spanX > 0.2f * sway.Sway && spanZ > 0.2f * sway.Sway, "раскачки нет");
        Assert.True(jump < 0.5f * sway.Sway, $"раскачка не гладкая: скачок {jump}");
        Assert.NotEqual(a.Select(v => v.X), a.Select(v => v.Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => (Steady with { Sway = -1 }).Validate());
    }

    /// <summary>
    /// Validate отвергает тёплый старт с (ω − 1)^(полупроходов/2) ≥ ⅓ (исключение с числами и подсказкой); WithStablePressure — наибольший
    /// устойчивый вес при тех же полупроходах (следующий float — уже неустойчив); устойчивые — те же параметры.
    /// </summary>
    [Theory]
    [InlineData(1.7f, 4)]
    [InlineData(1.7f, 6)]
    [InlineData(1.9f, 10)]
    [InlineData(1.81f, 10)]
    [InlineData(1.5f, 1)]
    [InlineData(null, 10)]
    public void Validate_RejectsSwingingWarmStart_AndClampFixesIt(float? weight, int halfPasses)
    {
        var p = Fluid3DParams.Smoke with { SorWeight = weight, PressureIterations = halfPasses };
        var error = Assert.Throws<ArgumentOutOfRangeException>(p.Validate);
        output.WriteLine(error.Message);
        Assert.Contains("раскачивается", error.Message);
        Assert.False(p.PressureStable);
        var fixedP = p.WithStablePressure();
        fixedP.Validate();
        float w = fixedP.SorWeightValue;
        Assert.True(w < p.SorWeightValue && w > 1);
        Assert.False(Fluid3DParams.IsPressureStable(MathF.BitIncrement(w), halfPasses, true));
        Assert.Equal(p with { SorWeight = w }, fixedP);
        output.WriteLine($"ω {p.SorWeightValue} при {halfPasses} → {w} ((ω − 1)^(N/2) = {Fluid3DParams.PressureSwing(w, halfPasses):F4})");
    }

    [Theory]
    [InlineData(1.7f, 10, true)]
    [InlineData(1.7f, 7, true)]
    [InlineData(1.8f, 10, true)]
    [InlineData(0.8f, 2, true)]
    [InlineData(1.94f, 10, false)]
    [InlineData(1.99f, 0, true)]
    public void Validate_AcceptsStablePressure(float weight, int halfPasses, bool warm)
    {
        var p = Fluid3DParams.Smoke with { SorWeight = weight, PressureIterations = halfPasses, PressureWarmStart = warm };
        p.Validate();
        Assert.True(p.PressureStable);
        Assert.Same(p, p.WithStablePressure());
    }

    /// <summary>
    /// Источник-карта (эталон, стадия источников): однородная карта (ρ, T, доля 0.4, скорость V) на плоскости наискось — плотность растёт только
    /// в слое у плоскости (на плоскости — на значение клетки, за слоем с краем — 0), за пределами карты — 0; скорость ячеек на плоскости
    /// сходится к V; ветер не меняет стадию источников.
    /// </summary>
    [Fact]
    public void PlaneSource_InjectsOnlyInLayer_PullsVelocityToMap()
    {
        var p = Small(32, 32, 32) with { Wind = new Vector3(0.7f, 0, -0.2f) };
        var w = WindowAt(p);
        var ax = Vector3.Normalize(new Vector3(1, 0, 0.5f));
        var ay = Vector3.Normalize(new Vector3(-0.2f, 1, 0.4f) - Vector3.Dot(new Vector3(-0.2f, 1, 0.4f), ax) * ax);
        var plane = new Fluid3DPlane(w.ToWorld(new Vector3(15.5f, 15.5f, 15.5f)), ax, ay, 10, p.CellSize, 1.5f * p.CellSize);
        var velocity = new Vector3(0.3f, 1.2f, -0.4f);
        var map = Enumerable.Repeat(Fluid3DMapTexel.Pack(0.25f, 0.5f, 0.4f, velocity), plane.Texels).ToArray();
        var gas = new Fluid3DReference(p, null, w) { Map = map };
        gas.Stage(Fluid3DStage.Sources, gas.Constants(1, w.Center, [], out _, plane));
        var f = gas.Fields;
        var n = Vector3.Normalize(plane.Normal);
        int layer = 0;
        for (int z = 0; z < 32; z++)
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var d = (w.ToWorld(new Vector3(x, y, z)) - plane.Center) / p.CellSize;
                    float dn = MathF.Abs(Vector3.Dot(d, n)), du = MathF.Abs(Vector3.Dot(d, ax)), dv = MathF.Abs(Vector3.Dot(d, ay));
                    float rho = (float)f.ScalarScratch[2 * f.Index(x, y, z)];
                    if (dn >= 2.5f || du >= 5.5f || dv >= 5.5f) Assert.Equal(0f, rho);
                    else if (dn < 0.4f && du < 4f && dv < 4f)
                    {
                        layer++;
                        Assert.InRange(rho, 0.249f, 0.251f);
                    }
                }
        output.WriteLine($"ячеек на плоскости с полным впрыском: {layer}");
        Assert.True(layer > 20, $"{layer}");
        for (int s = 2; s < 40; s++)
        {
            f.VelocityScratch.CopyTo(f.Velocity, 0);
            f.ScalarScratch.CopyTo(f.Scalars, 0);
            gas.Stage(Fluid3DStage.Sources, gas.Constants((ulong)s, w.Center, [], out _, plane));
        }
        var c = w.ToIndex(plane.Center);
        int at = f.Index((int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z));
        var v = new Vector3((float)f.VelocityScratch[4 * at], (float)f.VelocityScratch[4 * at + 1], (float)f.VelocityScratch[4 * at + 2]);
        output.WriteLine($"скорость у центра плоскости после 39 шагов источника: {v} (карта {velocity})");
        Assert.True(Vector3.Distance(v, velocity) < 0.01f, $"{v}");
    }
}
