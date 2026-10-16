using System.Numerics;
using Xunit.Abstractions;
using static Sword.Engine.Fluid3D.Tests.Fluid3DTestKit;

namespace Sword.Engine.Fluid3D.Tests;

/// <summary>Свойства шага на CPU-эталоне (GPU = эталон по стадиям — <see cref="Fluid3DGpuTests"/>): окно, давление, плавучесть, вихри.</summary>
public sealed class Fluid3DReferenceTests(ITestOutputHelper output)
{
    /// <summary>Скаляры в покое (тихий газ, v = 0): после сдвига окна ячейка мира хранит те же биты, пришедшее из-за края — пусто.</summary>
    [Fact]
    public void WindowShift_KeepsWorldContentBitExact_AndEmptiesNewCells()
    {
        var p = Quiet(16, 20, 12);
        var start = RandomFields(p.Nx, p.Ny, p.Nz, 3);
        Array.Clear(start.Velocity);
        Array.Clear(start.Aux);
        var w0 = WindowAt(p);
        var cpu = new Fluid3DReference(p, start.Clone(), w0);
        var shift = (X: 3, Y: -2, Z: 5);
        cpu.Step(1, w0.Center + new Vector3(shift.X, shift.Y, shift.Z) * p.CellSize, []);
        Assert.Equal(w0.X + shift.X, cpu.Window!.Value.X);
        Assert.Equal(w0.Y + shift.Y, cpu.Window!.Value.Y);
        Assert.Equal(w0.Z + shift.Z, cpu.Window!.Value.Z);
        AssertShifted(start.Scalars, cpu.Fields.Scalars, p, shift);

        cpu.Step(2, w0.Center, []);
        Assert.Equal(w0, cpu.Window!.Value with { Target = w0.Target });
        int kept = 0;
        for (int z = 0; z < p.Nz; z++)
            for (int y = 0; y < p.Ny; y++)
                for (int x = 0; x < p.Nx; x++)
                {
                    int i = start.Index(x, y, z);
                    bool stayed = x - shift.X >= 0 && x - shift.X < p.Nx && y - shift.Y >= 0 && y - shift.Y < p.Ny && z - shift.Z >= 0 && z - shift.Z < p.Nz;
                    var expect = stayed ? (start.Scalars[2 * i], start.Scalars[2 * i + 1]) : (default(Half), default(Half));
                    Assert.Equal(expect, (cpu.Fields.Scalars[2 * i], cpu.Fields.Scalars[2 * i + 1]));
                    if (stayed) kept++;
                }
        output.WriteLine($"туда и обратно: осталось {kept} из {p.Cells} ячеек бит в бит, остальные пусты");
    }

    internal static void AssertShifted(Half[] before, Half[] after, Fluid3DParams p, (int X, int Y, int Z) s)
    {
        for (int z = 0; z < p.Nz; z++)
            for (int y = 0; y < p.Ny; y++)
                for (int x = 0; x < p.Nx; x++)
                {
                    int fx = x + s.X, fy = y + s.Y, fz = z + s.Z;
                    int i = x + p.Nx * (y + p.Ny * z);
                    bool inside = fx >= 0 && fy >= 0 && fz >= 0 && fx < p.Nx && fy < p.Ny && fz < p.Nz;
                    int j = fx + p.Nx * (fy + p.Ny * fz);
                    var expect = inside ? (BitConverter.HalfToUInt16Bits(before[2 * j]), BitConverter.HalfToUInt16Bits(before[2 * j + 1])) : ((ushort)0, (ushort)0);
                    Assert.Equal(expect, (BitConverter.HalfToUInt16Bits(after[2 * i]), BitConverter.HalfToUInt16Bits(after[2 * i + 1])));
                }
    }

    /// <summary>Скачок цели дальше стороны окна — всё пусто.</summary>
    [Fact]
    public void Teleport_EmptiesEverything()
    {
        var p = Quiet(16, 16, 16);
        var start = RandomFields(p.Nx, p.Ny, p.Nz, 4);
        Array.Clear(start.Velocity);
        var w0 = WindowAt(p);
        var cpu = new Fluid3DReference(p, start, w0);
        cpu.Step(1, w0.Center + new Vector3(0, 100, 0), []);
        Assert.All(cpu.Fields.Scalars, h => Assert.Equal(0, BitConverter.HalfToUInt16Bits(h)));
    }

    /// <summary>
    /// Давление: остаток дивергенции Σ|div| после проекции много меньше, чем до. Гладкое поле (струя и вихрь, тихий газ), холодный старт:
    /// сошедшийся SOR (40 полупроходов) — меньше 15 %; без давления — всё остаётся. Молодой факел (источник внизу, силы умолчаний, шаги 30–50):
    /// умолчания (10 полупроходов, тёплый старт, вес 1.7) — остаток меньше 55 % при полу коллокированной схемы (64 полупрохода) ≈33 %,
    /// у Epic (6 с нуля, вес по формуле) — ≈73 %. Установившийся факел — <see cref="WarmStart_IsStableAtDefaults_AndSwingsBeyondIt"/>.
    /// </summary>
    [Fact]
    public void Pressure_RemovesMostDivergence()
    {
        var q = Quiet(32, 32, 32) with { PressureWarmStart = false, SorWeight = null };
        float Swirl1(int iterations)
        {
            var cpu = new Fluid3DReference(q with { PressureIterations = iterations }, Swirl(q), WindowAt(q));
            cpu.Step(0, cpu.Window!.Value.Center, []);
            var s = Fluid3DStats.Compute(cpu.Fields, cpu.Window!.Value, 0);
            output.WriteLine($"гладкое поле, {iterations} полупроходов с нуля: Σ|div| до {s.DivergenceBefore:G4}, после {s.DivergenceAfter:G4} — {s.DivergenceRatio:P1}");
            return s.DivergenceRatio;
        }
        Assert.True(Swirl1(40) < 0.15f);
        Assert.True(Swirl1(0) > 0.9f);

        float Plume(Fluid3DParams p, string what)
        {
            var win = WindowAt(p);
            var cpu = new Fluid3DReference(p, null, win);
            var src = new Fluid3DSource(win.ToWorld(new Vector3(11.5f, 6, 11.5f)), 0.1f, 3, 5);
            double ratio = 0;
            for (int i = 0; i < 50; i++)
            {
                cpu.Step((ulong)i, win.Center, [src]);
                if (i >= 30) ratio += Fluid3DStats.Compute(cpu.Fields, cpu.Window!.Value, (ulong)i).DivergenceRatio;
            }
            output.WriteLine($"факел 24×36×24, {what}: остаток {ratio / 20:P1}");
            return (float)(ratio / 20);
        }
        var plume = Small(24, 36, 24) with { MaxSpeed = 6 };
        float defaults = Plume(plume, "умолчания (10, тёплый, 1.7)");
        Plume(plume with { PressureIterations = 6, PressureWarmStart = false, SorWeight = null }, "Epic MovingGridFire (6 с нуля, формула)");
        Plume(plume with { PressureIterations = 64, PressureWarmStart = false, SorWeight = null }, "64 с нуля, формула");
        Assert.True(defaults < 0.55f, $"остаток {defaults}");
    }

    /// <summary>
    /// Тёплый старт устойчив, пока (ω − 1)^(полупроходов/2) &lt; ⅓ (контроли за границей — без проверки <see cref="Fluid3DParams.Validate"/>). Установившийся факел (шаги 140–160): умолчания (10, 1.7) — остаток ≈75 %
    /// (как 64 полупрохода с нуля), скорость ниже предела; 4 полупрохода с весом 1.7 — остаток больше 100 % (шахматка давления);
    /// вес 1.9 — скорость упирается в предел.
    /// </summary>
    [Fact]
    public void WarmStart_IsStableAtDefaults_AndSwingsBeyondIt()
    {
        var plume = Small(16, 24, 16) with { MaxSpeed = 6 };
        (float Ratio, float Speed) Settled(Fluid3DParams p, string what)
        {
            var win = WindowAt(p);
            var cpu = new Fluid3DReference(p, null, win);
            var src = new Fluid3DSource(win.ToWorld(new Vector3(7.5f, 4, 7.5f)), 0.08f, 3, 5);
            double ratio = 0;
            float speed = 0;
            for (int i = 0; i < 160; i++)
            {
                cpu.Step((ulong)i, win.Center, [src]);
                if (i < 140) continue;
                var s = Fluid3DStats.Compute(cpu.Fields, cpu.Window!.Value, (ulong)i);
                ratio += s.DivergenceRatio / 20;
                speed = MathF.Max(speed, s.MaxSpeed);
            }
            output.WriteLine($"факел 16×24×16, {what}: остаток {ratio:P1}, max|v| {speed:F2} м/с");
            return ((float)ratio, speed);
        }
        var defaults = Settled(plume, "умолчания (10, тёплый, 1.7)");
        Assert.True(defaults.Ratio < 0.9f && defaults.Speed < 0.9f * plume.MaxSpeed, $"{defaults}");
        Assert.True(Settled(plume with { PressureIterations = 4, UncheckedPressure = true }, "4 полупрохода, 1.7").Ratio > 1f);
        Assert.True(Settled(plume with { SorWeight = 1.9f, UncheckedPressure = true }, "10 полупроходов, 1.9").Speed > 0.99f * plume.MaxSpeed);
    }

    /// <summary>Тёплая капля (β > 0) поднимается; холодная плотная (α > 0) опускается; плотный дым умолчаний (α &lt; 0) поднимается.</summary>
    [Theory]
    [InlineData(0f, 0.5f, 0f, 1f, 1)]
    [InlineData(0.5f, 0f, 1f, 0f, -1)]
    [InlineData(-0.5f, 0.5f, 1f, 0f, 1)]
    public void Buoyancy_MovesDropBySign(float alpha, float beta, float density, float temperature, int direction)
    {
        var p = Small(24, 32, 24) with { DensityBuoyancy = alpha, TemperatureBuoyancy = beta, DensityDecay = 0, TemperatureDecay = 0, TemperatureDissipation = 0 };
        var start = Blob(p, new Vector3(11.5f, 15.5f, 11.5f), 3, 1, 0);
        if (temperature > 0)
            for (int i = 0; i < start.Cells; i++) start.Scalars[2 * i + 1] = start.Scalars[2 * i];
        if (density == 0)
            for (int i = 0; i < start.Cells; i++) start.Scalars[2 * i] = (Half)(0.2f * (float)start.Scalars[2 * i]);
        var w = WindowAt(p);
        var cpu = new Fluid3DReference(p, start, w);
        float y0 = Fluid3DStats.Compute(cpu.Fields, w, 0).Centroid.Y;
        for (int i = 0; i < 20; i++) cpu.Step((ulong)i, w.Center, []);
        var s = Fluid3DStats.Compute(cpu.Fields, w, 20);
        float dy = s.Centroid.Y - y0;
        output.WriteLine($"α {alpha}, β {beta}: центроид ρ сдвинулся на {dy * 100:F2} см по y за 20 шагов, max|v| {s.MaxSpeed:F3} м/с");
        Assert.True(dy * direction > 0.01f, $"сдвиг {dy} м");
    }

    /// <summary>Вихревое удержание усиливает завихрённость: энстрофия после 20 шагов с ε = 2.5 больше, чем с ε = 0.</summary>
    [Fact]
    public void Vorticity_IncreasesEnstrophy()
    {
        var p = Quiet(24, 24, 24);
        double With(float eps)
        {
            var q = p with { VorticityConfinement = eps };
            var cpu = new Fluid3DReference(q, Swirl(q), WindowAt(q));
            for (int i = 0; i < 20; i++) cpu.Step((ulong)i, cpu.Window!.Value.Center, []);
            return Enstrophy(cpu.Fields, q.CellSize);
        }
        double start = Enstrophy(Swirl(p), p.CellSize), off = With(0), on = With(2.5f);
        output.WriteLine($"энстрофия: в начале {start:G4}, 20 шагов с ε = 0 — {off:G4}, с ε = 2.5 — {on:G4} (×{on / off:F2})");
        Assert.True(on > off * 1.05, $"{on} против {off}");
    }

    /// <summary>Источник: плотность и температура растут на rate·dt внутри шара, скорость подтягивается к скорости источника.</summary>
    [Fact]
    public void Source_AddsRateTimesDt()
    {
        var p = Quiet(16, 16, 16) with { PressureIterations = 0 };
        var w = WindowAt(p);
        var cpu = new Fluid3DReference(p, null, w);
        var centre = w.ToWorld(new Vector3(8, 8, 8));
        cpu.Step(0, w.Center, [new Fluid3DSource(centre, 3 * p.CellSize, 60, 120, new Vector3(0, 1, 0), 30)]);
        int i = cpu.Fields.Index(8, 8, 8);
        Assert.Equal(1f, (float)cpu.Fields.Scalars[2 * i], 1e-3f);
        Assert.Equal(2f, (float)cpu.Fields.Scalars[2 * i + 1], 2e-3f);
        Assert.Equal(0.5f, (float)cpu.Fields.Velocity[4 * i + 1], 1e-3f);
        int far = cpu.Fields.Index(1, 1, 1);
        Assert.Equal(0f, (float)cpu.Fields.Scalars[2 * far]);
    }
}
