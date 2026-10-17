using System.Diagnostics;
using System.Numerics;
using Sword.Game.Arena;
using Sword.Game.Blood;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Кровь на клинке (v7): попадание кладёт кровь около места касания (∝ урону), на рукоять — нет; остриём вниз кровь стекает к кончику и
/// капает (масса: упало + осталось = положено), горизонтально — стоит, засохшая — не течёт; взмах стряхивает часть; два прогона бит в бит;
/// кадр — 0 Б. Приземление капель по шагам = прежнему расчёту сразу (тик тот же, точка ≤ 1 см).
/// </summary>
public sealed class BloodBladeTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60;

    /// <summary>Лезвие 80 см по +Y меша от 20 см, ширина 5 см (ось ширины +X: нормаль −Z), тело клинка = меш.</summary>
    private static BladeBloodZone Zone() => new(new Vector3(0, 0.2f, 0), Vector3.UnitY, Vector3.UnitX, 0.8f, 0.025f, Matrix4x4.Identity);

    /// <summary>Лезвие остриём вниз на высоте 1.5 м: +Y меша → −Y мира.</summary>
    private static readonly Matrix4x4 TipDown = Matrix4x4.CreateRotationX(MathF.PI) * Matrix4x4.CreateTranslation(0, 1.5f, 0);

    /// <summary>Лезвие горизонтально вдоль +X мира (нормаль −Z), хват на высоте 1.2 м.</summary>
    private static readonly Matrix4x4 Horizontal = Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateTranslation(0, 1.2f, 0);

    private static double Centre(BloodBlade b, int slot)
    {
        double m = 0, mu = 0;
        for (int side = 0; side < 2; side++)
            for (int i = 0; i < BloodBlade.Cells; i++)
            {
                m += b.Volume(slot, side, i);
                mu += b.Volume(slot, side, i) * (i + 0.5) / BloodBlade.Cells;
            }
        return mu / m;
    }

    [Fact]
    public void Hit_DepositsNearAlong_ByDamage_NotOnGrip()
    {
        var t = new BloodTuning();
        var blade = new BloodBlade(4, t);
        var zone = Zone();
        Assert.True(blade.Deposit(1, 0, zone, 0.3f, 30 * t.BladePerDamage, 0, 7));
        Assert.True(blade.Deposit(2, 0, zone, 0.7f, 60 * t.BladePerDamage, 0, 7));
        int a = blade.SlotOf(1), b = blade.SlotOf(2);
        Assert.Equal(30 * t.BladePerDamage, blade.OnBlade(a), 5);
        Assert.Equal(60 * t.BladePerDamage, blade.OnBlade(b), 5);
        Assert.InRange(Centre(blade, a), 0.3 - 0.5 / BloodBlade.Cells, 0.3 + 0.5 / BloodBlade.Cells);
        Assert.InRange(Centre(blade, b), 0.7 - 0.5 / BloodBlade.Cells, 0.7 + 0.5 / BloodBlade.Cells);
        // Обе стороны, размазано на несколько клеток, вдали — пусто.
        int around = (int)(0.3f * BloodBlade.Cells);
        Assert.True(blade.Volume(a, 0, around) > 0 && blade.Volume(a, 1, around) > 0);
        Assert.True(blade.Volume(a, 0, around - 2) > 0 && blade.Volume(a, 0, around + 2) > 0);
        Assert.Equal(0, blade.Volume(a, 0, around + 6));
        // Рукоять и гарда (вне лезвия) — не кладётся.
        Assert.False(blade.Deposit(3, 0, zone, -0.05f, 1, 0, 7));
        Assert.Equal(-1, blade.SlotOf(3));

        // Касание в системе тела клинка настоящего меча → пространство меша → доля лезвия: та же, что у точки меша.
        var tip = new Vector3(0.05f, 0.9f, -0.1f);
        var body = BladeBody.Of(tip);
        var toMesh = ArenaWeaponBloodLayer.BodyToMesh(body);
        var axis = Vector3.Normalize(tip);
        var sword = new BladeBloodZone(axis * 0.25f, axis, Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ)), tip.Length() - 0.25f, 0.025f, toMesh);
        foreach (float y in new[] { -0.3f, 0, 0.2f, 0.4f })
        {
            var local = new Vector3(0.01f, y, 0.005f);
            var mesh = Vector3.Transform(local, toMesh);
            float expected = (Vector3.Dot(mesh, axis) - 0.25f) / (tip.Length() - 0.25f);
            Assert.Equal(expected, sword.Along(mesh), 4);
            // Ось Y тела клинка — от хвата (−полудлина) к кончику: в меше — от начала кисти по оси клинка.
            Assert.Equal(body.HalfLength + y, Vector3.Dot(mesh, axis), 3);
        }
    }

    /// <summary>Шаги слота 0 с постоянным положением лезвия; капли — в список.</summary>
    private static List<BloodDrop> Run(BloodBlade blade, Func<int, Matrix4x4> at, int steps, float now0 = 0, BloodTuning? t = null)
    {
        var look = (t ?? new BloodTuning()).Look;
        var drops = new List<BloodDrop>();
        for (int k = 0; k < steps; k++)
        {
            blade.Step(0, at(k), (ulong)(2 * k), Dt, now0 + k * Dt, look);
            drops.AddRange(blade.Drops.ToArray());
            blade.TakeDrops();
        }
        return drops;
    }

    [Fact]
    public void TipDown_FlowsAndDrips_Horizontal_Stands_Dry_DoesNotFlow()
    {
        var t = new BloodTuning();
        var blade = new BloodBlade(2, t);
        Assert.True(blade.Deposit(1, 0, Zone(), 0.3f, 6, 0, 1));
        var drops = Run(blade, _ => TipDown, 600, t: t);
        double fell = drops.Sum(d => (double)d.Volume), left = blade.OnBlade(0);
        output.WriteLine($"остриём вниз 10 с: капель {drops.Count}, упало {fell:F4} мл, осталось {left:F4} мл, середина {Centre(blade, 0):F3}");
        Assert.True(drops.Count >= 5, "не капает");
        Assert.InRange(fell + left, 0.95 * 6, 1.05 * 6);
        Assert.Equal(6, fell + left, 4);
        Assert.True(Centre(blade, 0) > 0.6, "кровь не стекла к кончику");
        var tipWorld = Vector3.Transform(Zone().Origin + Zone().AxisU * Zone().Length, TipDown);
        foreach (var d in drops) Assert.True(Vector3.Distance(d.Position, tipWorld) < 0.02f, $"капля не с кончика: {d.Position}");
        // Смачивающий слой остаётся.
        Assert.True(left > 0.05);

        var flat = new BloodBlade(2, t);
        flat.Deposit(1, 0, Zone(), 0.3f, 3, 0, 1);
        var before = Enumerable.Range(0, BloodBlade.Cells).Select(i => flat.Volume(0, 0, i)).ToArray();
        Assert.Empty(Run(flat, _ => Horizontal, 600, t: t));
        Assert.Equal(before, Enumerable.Range(0, BloodBlade.Cells).Select(i => flat.Volume(0, 0, i)).ToArray());

        // Засохшая (возраст 1000 с) остриём вниз не течёт.
        var dry = new BloodBlade(2, t);
        dry.Deposit(1, 0, Zone(), 0.3f, 3, 0, 1);
        var dryBefore = Enumerable.Range(0, BloodBlade.Cells).Select(i => dry.Volume(0, 1, i)).ToArray();
        Assert.Empty(Run(dry, _ => TipDown, 300, now0: 1000, t: t));
        Assert.Equal(dryBefore, Enumerable.Range(0, BloodBlade.Cells).Select(i => dry.Volume(0, 1, i)).ToArray());
    }

    /// <summary>Горизонтальный взмах вокруг вертикали у хвата: 8 шагов по 15 рад/с, затем резкая остановка.</summary>
    private static Matrix4x4 Swing(int k)
    {
        float angle = 15 * Dt * Math.Min(k, 8);
        return Matrix4x4.CreateRotationZ(-MathF.PI / 2) * Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(0, 1.2f, 0);
    }

    [Fact]
    public void Swing_ShakesOffPart_Deterministic()
    {
        var t = new BloodTuning();
        const float Ml = 6;
        (List<BloodDrop> Drops, double Shaken, double Left, float[] Cells) Go(Func<int, Matrix4x4> at)
        {
            var blade = new BloodBlade(2, t);
            blade.Deposit(1, 0, Zone(), 0.5f, Ml, 0, 1);
            var drops = Run(blade, at, 120, t: t);
            var cells = Enumerable.Range(0, 2 * BloodBlade.Cells).Select(i => blade.Volume(0, i / BloodBlade.Cells, i % BloodBlade.Cells)).ToArray();
            return (drops, blade.ShakenMl, blade.OnBlade(0), cells);
        }
        var still = Go(_ => Horizontal);
        var swing = Go(Swing);
        double fell = swing.Drops.Sum(d => (double)d.Volume);
        output.WriteLine($"взмах: капель {swing.Drops.Count}, упало {fell:F4} мл (стряхнуто {swing.Shaken:F4}), осталось {swing.Left:F4}; " +
            $"без взмаха — капель {still.Drops.Count}");
        Assert.Empty(still.Drops);
        Assert.True(swing.Shaken > 0.05 * Ml, "взмах не стряхнул");
        Assert.True(swing.Left > 0.05 * Ml, "стряхнуло всё");
        Assert.Equal(Ml, fell + swing.Left, 4);
        // Веер: капли с разных мест лезвия, скорости разные.
        Assert.True(swing.Drops.Select(d => d.Position).Distinct().Count() >= 2);
        // Два прогона — бит в бит.
        var again = Go(Swing);
        Assert.Equal(swing.Drops, again.Drops);
        Assert.Equal(swing.Cells, again.Cells);
    }

    [Fact]
    public void Blade_StepDeposit_NoAllocations()
    {
        var t = new BloodTuning();
        var blade = new BloodBlade(4, t);
        var look = t.Look;
        var packed = new ulong[WeaponBloodLayout.Elements(BloodBlade.Cells)];
        void Round(int r)
        {
            blade.Deposit(1, 0, Zone(), 0.4f, 2, r, (uint)r);
            for (int k = 0; k < 60; k++)
            {
                blade.Step(0, Swing(k), (ulong)k, Dt, r + k * Dt, look);
                blade.TakeDrops();
            }
            BloodBladeGpu.Pack(blade, 0, r + 1, look, t.BladeCoverMm, packed);
        }
        for (int r = 0; r < 3; r++) Round(r);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int r = 3; r < 6; r++) Round(r);
        long spent = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(spent == 0, $"кровь на клинке выделила {spent} Б");
    }

    private sealed class Terrain(Func<float, float, float> h) : ITerrainView
    {
        public float Height(float x, float z) => h(x, z);
        public float SmoothHeight(float x, float z, out Vector2 gradient)
        {
            gradient = Vector2.Zero;
            return h(x, z);
        }
        public float StepHeight(float x, float z) => 0;
        public Vector3 Normal(float x, float z) => Vector3.UnitY;
        public TerrainGrid Grid => new(256, 32);
        public int HeightsSrv => -1;
    }

    [Fact]
    public void Landing_ByStep_EqualsAtOnce_TickExact_Deterministic()
    {
        const float tickDt = 1f / 120;
        var terrains = new ITerrainView[]
        {
            new Terrain((_, _) => 0),
            new Terrain((x, z) => 0.3f * MathF.Tan(17 * MathF.PI / 180) * x + 0.05f),
            new Terrain((x, _) => MathF.Floor(Math.Clamp(x + 1, 0, 5)) * 0.12f),
            new ArenaTerrainView(),
        };
        // Удар: 200 капель брызг из одной точки веером, 1…8 м/с.
        var drops = new BloodDrop[200];
        for (int i = 0; i < drops.Length; i++)
        {
            uint seed = BloodHash.Combine(77, (uint)i);
            var dir = Vector3.Normalize(new Vector3(BloodHash.Unit(seed, 0) - 0.5f, BloodHash.Unit(seed, 1) - 0.2f, BloodHash.Unit(seed, 2) - 0.5f));
            drops[i] = new BloodDrop(3, 100, new Vector3(-2 + 0.02f * i, 1.3f, 0.5f), dir * (1 + 7 * BloodHash.Unit(seed, 3)), 0.01f);
        }
        float worst = 0;
        foreach (var terrain in terrains)
        {
            BloodFlying[] Fly(out double burstMs)
            {
                var floor = new BloodFloor();
                long started = Stopwatch.GetTimestamp();
                floor.Detach(drops, terrain, 2, tickDt);
                burstMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                floor.Advance(100 + BloodFloor.MaxFlightTicks);
                return floor.Flying.ToArray();
            }
            Fly(out _);
            var flights = Fly(out double burst);
            long started = Stopwatch.GetTimestamp();
            foreach (var d in drops) BloodFlight.Land(terrain, d.Position, d.Velocity, 2, tickDt, BloodFloor.MaxFlightTicks, out _, out _, out _);
            double atOnce = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            output.WriteLine($"{terrain.GetType().Name}: отрыв 200 капель {burst:F3} мс (расчёт приземления сразу — {atOnce:F3} мс)");
            Assert.Equal(drops.Length, flights.Length);
            for (int i = 0; i < drops.Length; i++)
            {
                var d = drops[i];
                Assert.True(BloodFlight.Land(terrain, d.Position, d.Velocity, 2, tickDt, BloodFloor.MaxFlightTicks, out int ticks, out float time, out var point));
                var f = flights[i];
                Assert.True(f.Landed);
                Assert.Equal(d.Tick + (ulong)ticks, f.LandTick);
                float gap = Vector3.Distance(point, f.Land);
                worst = MathF.Max(worst, gap);
                Assert.True(gap <= 0.01f, $"капля {i}: {f.Land} против {point}");
                Assert.Equal(time, f.LandTime);
            }
            Assert.Equal(flights, Fly(out _));   // два прогона — бит в бит
        }
        output.WriteLine($"по шагам − сразу: до {worst * 1000:F4} мм");
    }
}
