using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Platform;
using Sword.Engine.Platform.Win32;
using Sword.Engine.Render;
using Sword.Game.Blood;
using Sword.Game.Contracts;
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Кровь на коже и сочение (план этапа 12, К3, К4): функция свежести (C# = HLSL на GPU), штампы по сваренным вершинам (все копии на шве),
/// пятно у раны по связной коже с проверкой нормали, возраст по притоку, сброс слота; сочение сильной раны — Σ = ∫ темпа, слабая не сочится,
/// у трупа — до снятия, два прогона — одно и то же; 0 Б на шаг.
/// </summary>
public sealed class BloodSkinTests(ITestOutputHelper output)
{
    private static readonly BloodLookParams Look = BloodLookParams.Default;

    // ---------------- функция свежести ----------------

    [Fact]
    public void Look_FreshScarletGlossy_ThenMatte_ThenBrown()
    {
        var thick = BloodLook.Evaluate(0, 1, Look);
        Near(Look.Thick, thick.Albedo);
        Assert.Equal(1, thick.Wet);
        Assert.Equal(Look.WetRoughness, thick.Roughness, 5);
        Assert.True(thick.Coverage > 0.999f);
        Near(Look.Thin, BloodLook.Evaluate(0, 0, Look).Albedo);
        var old = BloodLook.Evaluate(1000, 0.05f, Look);
        Near(Look.Dry, old.Albedo);
        Assert.Equal(Look.DryRoughness, old.Roughness, 5);
        float wet = 2, dry = -1;
        for (float age = 0; age <= 400; age += 0.5f)
        {
            var r = BloodLook.Evaluate(age, 0.1f, Look);
            Assert.True(r.Wet <= wet && r.Dry >= dry, $"возраст {age}: влажность {r.Wet}, засохлость {r.Dry}");
            (wet, dry) = (r.Wet, r.Dry);
        }
        // Блеск уходит раньше потемнения; толстая сохнет дольше тонкой.
        var at40 = BloodLook.Evaluate(40, 0.01f, Look);
        Assert.True(at40.Wet < 0.5f && at40.Dry == 0);
        Assert.True(BloodLook.Evaluate(40, 1f, Look).Wet > at40.Wet);
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-6f, $"{expected} ≠ {actual}");

    // ---------------- кожа ----------------

    private static (SkinSurface Surface, BloodSkin Skin) Skin(ISkinMesh mesh, int slots = 4)
    {
        var surface = new SkinSurface(mesh);
        return (surface, new BloodSkin(surface, slots));
    }

    [Fact]
    public void Stamp_WritesAllWeldCopies_OnSeam()
    {
        var (s, skin) = Skin(BloodBodyTests.Wall(seam: true));
        int seamTriangle = -1;
        for (int t = 0; t < s.TriangleCount && seamTriangle < 0; t++)
            for (int k = 0; k < 3; k++)
                if (s.WeldCopies(s.WeldVertex(t, k)).Length > 1) seamTriangle = t;
        Assert.True(seamTriangle >= 0);
        skin.Stamp(1, seamTriangle, new Vector3(0.2f, 0.3f, 0.5f), 0.02f, 3);
        var entries = skin.Gather().ToArray();
        int copies = 0;
        for (int k = 0; k < 3; k++)
        {
            int weld = s.WeldVertex(seamTriangle, k);
            var first = entries.Single(e => e.Element == s.VertexCount + s.WeldCopies(weld)[0]);
            foreach (int v in s.WeldCopies(weld))
            {
                var e = entries.Single(x => x.Element == s.VertexCount + v);
                Assert.Equal((first.X, first.Y), (e.X, e.Y));
                copies++;
            }
        }
        Assert.True(copies > 3, "у треугольника на шве нет копий");
        Assert.Equal(copies, entries.Length);
        Assert.Empty(skin.Gather().ToArray());   // отметки сняты
    }

    /// <summary>Ладонь и два «пальца» через щель 5 мм (сетка 2.5 мм, плоскость z = 0, нормаль +Z).</summary>
    private static BloodBodyTests.SyntheticMesh Fingers()
    {
        var p = new List<Vector3>();
        var n = new List<Vector3>();
        var idx = new List<uint>();
        const float h = 0.0025f;
        int nx = 10, ny = 44;
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                p.Add(new Vector3(i * h, -0.03f + j * h, 0));
                n.Add(Vector3.UnitZ);
            }
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                if (i is 4 or 5 && j >= 12) continue;   // щель x ∈ [1, 1.5] см выше ладони (y > 0)
                uint a = (uint)(j * (nx + 1) + i), b = a + 1, c = a + (uint)(nx + 1), d = c + 1;
                idx.AddRange([a, b, d, a, d, c]);
            }
        return new BloodBodyTests.SyntheticMesh(p, n, idx);
    }

    [Fact]
    public void Spot_StaysOnItsFinger_AndItsSide()
    {
        var (s, skin) = Skin(Fingers());
        var center = new Vector3(0.005f, 0.07f, 0);
        int t = s.Nearest(BodySegs.Torso, center, out var bary, out _);
        int painted = skin.Spot(1, t, center, Vector3.UnitZ, 0.03f, 0.5f, 0);
        Assert.True(painted > 20, $"вершин пятна {painted}");
        float own = 0, neighbour = 0, palm = 0;
        for (int w = 0; w < s.WeldCount; w++)
        {
            var q = s.BindPosition(s.WeldCopies(w)[0]);
            float th = skin.Thickness(1, w);
            if (q.Y > 0.001f && q.X < 0.0099f) own += th;
            else if (q.Y > 0.001f && q.X > 0.0151f) neighbour += th;
            else palm += th;
        }
        output.WriteLine($"толщина Σ: свой палец {own:F3}, соседний {neighbour:F3}, ладонь и края щели {palm:F3} мм");
        Assert.True(own > 0);
        Assert.Equal(0, neighbour);   // в 1.5 см по воздуху, но по коже — через ладонь дальше радиуса
        // Объём сохранён: Σ толщина · площадь = объём.
        double volume = 0;
        for (int w = 0; w < s.WeldCount; w++) volume += skin.Thickness(1, w) * 1e-3 * skin.Area(w) * 1e6;
        Assert.InRange(volume, 0.5 * 0.999, 0.5 * 1.001);

        // Сложенный лист: верх (+Z) и низ (−Z) в 2 мм, соединены по краю — пятно на верху у края не идёт на низ.
        var pp = new List<Vector3>();
        var nn = new List<Vector3>();
        var ii = new List<uint>();
        BloodBodyTests.Quad(pp, nn, ii, new Vector3(0, -0.05f, 0), new Vector3(0.05f, 0, 0), new Vector3(0, 0.1f, 0), 20, 40, Vector3.UnitZ);
        BloodBodyTests.Quad(pp, nn, ii, new Vector3(0, 0.05f, -0.002f), new Vector3(0.05f, 0, 0), new Vector3(0, -0.1f, 0), 20, 40, -Vector3.UnitZ);
        BloodBodyTests.Quad(pp, nn, ii, new Vector3(0, -0.05f, -0.002f), new Vector3(0, 0, 0.002f), new Vector3(0, 0.1f, 0), 1, 40, -Vector3.UnitX);
        var (fs, fskin) = Skin(new BloodBodyTests.SyntheticMesh(pp, nn, ii));
        var c = new Vector3(0.003f, 0, 0);
        int ft = fs.Nearest(BodySegs.Torso, c, out _, out _);
        fskin.Spot(1, ft, c, Vector3.UnitZ, 0.03f, 0.5f, 0);
        float top = 0, bottom = 0;
        for (int w = 0; w < fs.WeldCount; w++)
        {
            var q = fs.BindPosition(fs.WeldCopies(w)[0]);
            if (q.Z > -0.0001f) top += fskin.Thickness(1, w);
            else if (q.Z < -0.0019f) bottom += fskin.Thickness(1, w);
        }
        Assert.True(top > 0);
        Assert.Equal(0, bottom);
    }

    [Fact]
    public void Age_GrowsWithoutInflow_FreshInflowRejuvenates_ReleaseIsClean()
    {
        var (s, skin) = Skin(BloodBodyTests.Wall(seam: false), slots: 2);
        int slot = skin.Acquire(7);
        int weld = s.WeldVertex(100, 0);
        skin.Deposit(slot, weld, 0.01f, 0);
        float last = -1;
        for (float now = 0; now <= 20; now += 0.25f)
        {
            float age = skin.Age(7, weld, now);
            Assert.True(age >= last, $"возраст убыл: {last} → {age}");
            last = age;
        }
        Assert.Equal(20, skin.Age(7, weld, 20), 4);
        skin.Deposit(slot, weld, 0.01f, 20);   // столько же свежей — возраст пополам
        Assert.Equal(10, skin.Age(7, weld, 20), 3);
        skin.Deposit(slot, weld, 0, 30);
        Assert.Equal(20, skin.Age(7, weld, 30), 3);

        // Два бойца, третьему слота нет; снятие — слот чист и ждёт обнуления на GPU, изменений старого нет.
        Assert.True(skin.Acquire(8) >= 0);
        Assert.Equal(-1, skin.Acquire(9));
        skin.Release(7);
        Assert.True(skin.PendingClear(slot, take: false));
        Assert.Equal(0, skin.Thickness(7, weld));
        Assert.Empty(skin.Gather().ToArray());
        int again = skin.Acquire(9);
        Assert.Equal(slot, again);
        for (int w = 0; w < s.WeldCount; w++) Assert.Equal(0, skin.Thickness(9, w));
    }

    // ---------------- сочение ----------------

    private sealed class OozeRig
    {
        public readonly SkinSurface Surface;
        public readonly BloodSkin Skin;
        public readonly BloodDrips Drips;
        public readonly BloodWounds Wounds = new();
        public readonly BloodOoze Ooze = new();
        public readonly BloodTuning Tuning = new();
        public bool Dead;
        private readonly Func<ulong, bool> _dead;
        private readonly BloodBodyTests.FakePoses _poses = new(new[] { Matrix4x4.Identity });
        public ulong Tick;

        public OozeRig()
        {
            Surface = new SkinSurface(BloodBodyTests.Wall(seam: false));
            Skin = new BloodSkin(Surface, 2);
            Drips = new BloodDrips(Surface, Tuning);
            _dead = _ => Dead;
        }

        public void Hit(float damage)
        {
            var hit = new BloodHit(Tick, 1, 2, BodySegs.Torso, new Vector3(0, 0.3f, 0.01f), -Vector3.UnitZ, Vector3.UnitX, 5, damage, HitKind.Slash,
                false, false, false, 0);
            Assert.True(Wounds.Record(hit, new[] { Matrix4x4.Identity }, Surface, Tuning, out _) >= 0);
        }

        /// <summary>Шаг 60 Гц при 120 тиках/с.</summary>
        public void Step()
        {
            Tick += 2;
            float now = Tick / 120f;
            Drips.Step(Tick, 1f / 60, _poses);
            Ooze.Step(Tick, 1f / 120, now, Wounds, Tuning, _dead, Skin, Drips);
        }
    }

    [Fact]
    public void Ooze_StrongWound_ReleasesIntegralOfRate_WeakDoesNot()
    {
        var rig = new OozeRig();
        rig.Hit(60);
        for (int i = 0; i < 60 * 30; i++) rig.Step();
        double expected = 60 * rig.Tuning.OozeRate * rig.Tuning.OozeSeconds / 2;
        output.WriteLine($"выпущено {rig.Ooze.Released:F4} мл, ∫ темпа {expected:F4} мл, потёков {rig.Ooze.Feeds}, на коже {rig.Skin.Deposited:F4} мл");
        Assert.InRange(rig.Ooze.Released, expected * 0.95, expected * 1.05);
        Assert.True(rig.Ooze.Feeds >= 4, $"потёков из сочения {rig.Ooze.Feeds}");
        Assert.Equal(0, rig.Ooze.Active);   // погасло

        var weak = new OozeRig();
        weak.Hit(30);
        for (int i = 0; i < 600; i++) weak.Step();
        Assert.Equal(0, weak.Ooze.Released);
    }

    [Fact]
    public void Ooze_Corpse_KeepsOozingUntilRemoved_Deterministic()
    {
        (double Released, long Feeds, float[] Thick, Vector3[] Heads) Run()
        {
            var rig = new OozeRig { Dead = true };
            rig.Hit(60);
            for (int i = 0; i < 60 * 60; i++) rig.Step();
            double atMinute = rig.Ooze.Released;
            var t = rig.Tuning;
            double expected = 60 * t.OozeRate * BloodOoze.Integral(60, t.OozeSeconds, t.OozeCorpseShare);
            Assert.InRange(atMinute, expected * 0.95, expected * 1.05);
            Assert.True(rig.Ooze.Active == 1 && rig.Ooze.Rate > 0, "у трупа сочение остановилось");
            var thick = new float[rig.Surface.WeldCount];
            for (int w = 0; w < thick.Length; w++) thick[w] = rig.Skin.Thickness(1, w);
            var heads = new List<Vector3>();
            for (int i = 0; i < rig.Drips.Capacity; i++)
                if (rig.Drips.IsAlive(i)) heads.Add(rig.Surface.BindPoint(rig.Drips.Head(i).Triangle, rig.Drips.Head(i).Bary));
            // Снят (исчез) — раны нет, сочения нет.
            rig.Wounds.Clear(1);
            rig.Skin.Release(1);
            for (int i = 0; i < 120; i++) rig.Step();
            Assert.Equal(atMinute, rig.Ooze.Released);
            return (atMinute, rig.Ooze.Feeds, thick, [.. heads]);
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Released, b.Released);
        Assert.Equal(a.Feeds, b.Feeds);
        Assert.Equal(a.Thick, b.Thick);
        Assert.Equal(a.Heads, b.Heads);
        output.WriteLine($"труп: за минуту {a.Released:F3} мл, потёков {a.Feeds}");
    }

    [Fact]
    public void SkinAndOoze_Step_NoAllocations()
    {
        var rig = new OozeRig();
        rig.Hit(60);
        rig.Hit(70);
        for (int i = 0; i < 300; i++) Stamp(rig);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++) Stamp(rig);
        long spent = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(spent == 0, $"шаг кожи и сочения выделил {spent} Б");

        static void Stamp(OozeRig rig)
        {
            rig.Step();
            foreach (var st in rig.Drips.Stamps) rig.Skin.Stamp(st.Fighter, st.Triangle, st.Bary, st.Volume, rig.Tick / 120f);
            rig.Skin.Gather();
        }
    }

    // ---------------- модуль на паке ----------------

    [Fact]
    [Trait("Category", "Content")]
    public void Module_OnPak_SkinBloodAndOoze_Replayable_ClearedOnReset()
    {
        (double Released, long Feeds, double Deposited, int Slots) Run(out BloodBodyTests.Arena arena)
        {
            var a = arena = new BloodBodyTests.Arena();
            for (int f = 0; f < 400; f++) a.Frame(f);
            var skin = a.Blood.Skin!;
            return (a.Blood.Ooze.Released, a.Blood.Ooze.Feeds, skin.Deposited, skin.UsedSlots);
        }
        var first = Run(out var arena);
        output.WriteLine($"сочение {first.Released:F3} мл, потёков {first.Feeds}; на коже {first.Deposited:F3} мл, слотов {first.Slots}");
        Assert.True(first.Released > 0 && first.Feeds > 0 && first.Deposited > 0 && first.Slots > 0, first.ToString());
        Assert.Equal(first, Run(out _));
        arena.ArenaCommands.Add(new ArenaCommand { Action = ArenaAction.Reset });
        arena.Frame(0);
        Assert.Equal(0, arena.Blood.Skin!.UsedSlots);
    }
}

/// <summary>Кровь на коже на GPU (<see cref="BloodSkinGpu"/>): вид вершин = функция свежести на CPU, снятый слот обнуляется.</summary>
[Collection("game-gpu")]
public sealed class BloodSkinGpuTests(ITestOutputHelper output)
{
    private static readonly BloodLookParams Look = BloodLookParams.Default;

    // ---------------- GPU ----------------

    private sealed class D3d12Sink : ILogSink
    {
        public readonly List<string> Messages = [];

        public void Write(in LogEntry entry)
        {
            if (entry.Category == "d3d12" && entry.Level >= LogLevel.Warning) Messages.Add(entry.Message);
        }
    }

    /// <summary>
    /// Вид вершин на GPU (<c>CsLook</c>) = <see cref="BloodLook.Evaluate"/> с упаковкой <see cref="SkinLayerTexel"/> (половинная точность и 1/255),
    /// во все копии вершины на шве; снятый слот — нули. Любое предупреждение D3D12 — провал.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_Look_MatchesCpuFormula_ClearOnRelease()
    {
        var sink = new D3d12Sink();
        Log.AddSink(sink);
        try
        {
            var s = new SkinSurface(BloodBodyTests.Wall(seam: true));
            var skin = new BloodSkin(s, 3);
            using var window = new Win32Window(new WindowOptions("Sword.Game.Tests.BloodSkin", 64, 64, Activate: false));
            var contract = new ShaderContract();
            Renderer.Contribute(contract);
            BloodSkinGpu.Contribute(contract);
            using var renderer = new Renderer(new RendererOptions(window.NativeHandle, 64, 64, DebugLayer: true,
                ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
            using var gpu = new BloodSkinGpu(renderer, skin);
            // Возрасты 0…300 с и толщины от тонкой до густой — по вершинам слота.
            int slot = skin.Acquire(5);
            for (int w = 0; w < s.WeldCount; w++)
                skin.Deposit(slot, w, 0.01f * (1 + w % 37) * skin.Area(w) * 1e3f, (w % 61) * 5f);
            const float now = 300;
            void Frame(long step)
            {
                window.PumpMessages();
                renderer.Render(f => gpu.Build(f, now, step, Look), vsync: false);
                renderer.WaitForIdle();
            }
            Frame(1);
            Assert.True(gpu.Srv(slot) >= 0);
            var data = new ulong[2 * s.VertexCount];
            renderer.ReadStorage(gpu.Storage(slot), data.AsSpan());
            float worstAlbedo = 0, worstScalar = 0;
            for (int v = 0; v < s.VertexCount; v++)
            {
                int w = s.WeldOf(v);
                var cpu = BloodLook.Evaluate(now - BirthOf(data[s.VertexCount + v]), ThickOf(data[s.VertexCount + v]), Look);
                Assert.Equal(skin.Thickness(5, w), ThickOf(data[s.VertexCount + v]));
                SkinLayerTexel.Unpack((uint)data[v], (uint)(data[v] >> 32), out var albedo, out float coverage, out float roughness);
                var d = Vector3.Abs(albedo - cpu.Albedo) / Vector3.Max(cpu.Albedo, new Vector3(1e-3f));
                worstAlbedo = MathF.Max(worstAlbedo, MathF.Max(d.X, MathF.Max(d.Y, d.Z)));
                worstScalar = MathF.Max(worstScalar, MathF.Max(MathF.Abs(coverage - cpu.Coverage), MathF.Abs(roughness - cpu.Roughness)));
            }
            output.WriteLine($"GPU − CPU: альбедо {worstAlbedo:E2} (отн.), покрытие и шероховатость {worstScalar * 255:F2}/255");
            Assert.True(worstAlbedo <= 2e-3f, $"альбедо {worstAlbedo}");
            Assert.True(worstScalar <= 1.01f / 255, $"покрытие или шероховатость {worstScalar}");

            // Кадр — 0 Б сверх пустого кадра рендера: проходы с изменениями и без.
            long Spent(Action<RenderFrame> build)
            {
                for (int i = 0; i < 20; i++) renderer.Render(build, vsync: false);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 40; i++) renderer.Render(build, vsync: false);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Action<RenderFrame> empty = _ => { };
            long step = 10;
            Action<RenderFrame> busy = f =>
            {
                skin.Stamp(5, 7, new Vector3(0.3f, 0.3f, 0.4f), 0.001f, now);
                gpu.Build(f, now, step++, Look);
            };
            long baseline = Spent(empty), withBlood = Spent(busy);
            renderer.WaitForIdle();
            output.WriteLine($"40 кадров: пустой {baseline} Б, с кровью на коже {withBlood} Б");
            Assert.True(withBlood <= baseline, $"кадр крови на коже выделяет: {withBlood} Б против {baseline} Б");

            skin.Release(5);
            Frame(2);
            renderer.ReadStorage(gpu.Storage(slot), data.AsSpan());
            Assert.All(data, x => Assert.Equal(0ul, x));
            Assert.True(sink.Messages.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", sink.Messages));
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        static float ThickOf(ulong raw) => BitConverter.UInt32BitsToSingle((uint)raw);
        static float BirthOf(ulong raw) => BitConverter.UInt32BitsToSingle((uint)(raw >> 32));
    }
}
