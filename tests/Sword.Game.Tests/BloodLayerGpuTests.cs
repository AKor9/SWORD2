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
/// Разбор слоёв крови шейдером сцены (<c>scene_layers.hlsli</c>: <c>SkinLayerOf</c>, <c>WeaponLayerOf</c>) = упаковка C#
/// (<see cref="SkinLayerTexel"/>, <see cref="WeaponBloodLayout"/>), и помощники плёнки пола <c>PsFloor</c> (<c>MapSmooth</c>,
/// <c>FieldSmooth</c>, <c>FilmProfileSlope</c>): рождение карты — в диапазоне рождений крови под выборкой, наклон мениска конечен на
/// отрицательном лепестке бикубика. Тестовый шейдер — <c>Shaders/blood_layer_tests.hlsl</c> (окно и карта по 8² текселей).
/// </summary>
[Collection("game-gpu")]
public sealed class BloodLayerGpuTests(ITestOutputHelper output)
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
        private readonly D3d12Sink _sink = new();

        public Rig()
        {
            Log.AddSink(_sink);
            Window = new Win32Window(new WindowOptions("Sword.Game.Tests.BloodLayer", 64, 64, Activate: false));
            var contract = new ShaderContract();
            Renderer.Contribute(contract);
            BloodFloorGpu.Contribute(contract, 8, 8);   // окно и карта 8² — данные теста
            BloodSkinGpu.Contribute(contract);
            contract.Add("WEAPON_BLOOD_HEADER", WeaponBloodLayout.Header);
            Renderer = new Renderer(new RendererOptions(Window.NativeHandle, 64, 64, DebugLayer: true,
                ShaderRoot: Path.Combine(AppContext.BaseDirectory, "Shaders"), GpuValidation: true), contract);
        }

        /// <summary>Проход <paramref name="entry"/> на <paramref name="count"/> потоков: входы (слой, точки, карта, поле) — SRV, выход — float4 × 2.</summary>
        public Vector4[] Run(string entry, int count, GpuStorage? layer, GpuStorage? points, GpuStorage? map = null, GpuStorage? field = null)
        {
            using var result = Renderer.CreateStorage<Vector4>("test.layer.out", (uint)(2 * count));
            Window.PumpMessages();
            Renderer.Render(f =>
            {
                var g = f.Graph;
                var o = g.Import(result);
                var pass = g.AddComputePass("test.layer", FramePhase.Simulation).Write(o);
                int Srv(GpuStorage? s)
                {
                    if (s is null) return 0;
                    var b = g.Import(s);
                    pass = pass.Read(b);
                    return g.SrvOf(b);
                }
                int layerSrv = layer is null ? 0 : Srv(layer) + 1, pointsSrv = Srv(points), mapSrv = Srv(map), fieldSrv = Srv(field);
                int uav = g.UavOf(o);
                pass.Execute(ctx =>
                {
                    ctx.SetPipeline(new ComputePipelineDesc("blood_layer_tests.hlsl", entry));
                    ctx.SetConstant(20, layerSrv);
                    ctx.SetConstant(21, uav);
                    ctx.SetConstant(22, count);
                    ctx.SetConstant(23, pointsSrv);
                    ctx.SetConstant(24, mapSrv);
                    ctx.SetConstant(25, fieldSrv);
                    ctx.Dispatch((uint)((count + 63) / 64));
                });
            }, vsync: false);
            Renderer.WaitForIdle();
            var data = new Vector4[2 * count];
            Renderer.ReadStorage(result, data.AsSpan());
            return data;
        }

        public void Dispose()
        {
            Renderer.WaitForIdle();
            Renderer.Dispose();
            Window.Dispose();
            Log.RemoveSink(_sink);
            Assert.True(_sink.Messages.Count == 0, "сообщения отладочного слоя D3D12:\n" + string.Join("\n", _sink.Messages));
        }
    }

    private static void Near(Vector4 expected, Vector4 actual, string what)
    {
        var d = Vector4.Abs(expected - actual);
        float worst = MathF.Max(MathF.Max(d.X, d.Y), MathF.Max(d.Z, d.W));
        Assert.True(worst <= 1e-6f * MathF.Max(1, Vector4.Abs(expected).Length()), $"{what}: ждали {expected}, GPU {actual}");
    }

    private static (Vector4 Add, Vector4 Mul) Unpacked(ulong texel)
    {
        SkinLayerTexel.Unpack((uint)texel, (uint)(texel >> 32), out var add, out var mul, out float coverage, out float roughness);
        return (new Vector4(add, coverage), new Vector4(mul, roughness));
    }

    private static ulong Packed(Vector3 add, Vector3 mul, float coverage, float roughness)
    {
        var (x, y) = SkinLayerTexel.Pack(add, mul, coverage, roughness);
        return WeaponBloodLayout.Cell(x, y);
    }

    /// <summary>Кожа: вид вершины на GPU = <see cref="SkinLayerTexel.Unpack"/> на 300 случайных видах; без слоя (owner 0) — A 0, M 1.</summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_SkinLayerOf_EqualsSkinLayerTexel()
    {
        const int n = 300;
        var random = new Random(12);
        var texels = new ulong[n];
        for (int i = 0; i < n; i++)
            texels[i] = Packed(new Vector3(2 * random.NextSingle(), 0.5f * random.NextSingle(), 0.1f * random.NextSingle()),
                new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()), random.NextSingle(), random.NextSingle());
        using var rig = new Rig();
        using var layer = rig.Renderer.CreateStorage<ulong>("test.layer.skin", texels);
        var gpu = rig.Run("CsSkinLayer", n, layer, null);
        for (int i = 0; i < n; i++)
        {
            var (add, mul) = Unpacked(texels[i]);
            Near(add, gpu[2 * i], $"вершина {i}: A и покрытие");
            Near(mul, gpu[2 * i + 1], $"вершина {i}: M и шероховатость");
        }
        var none = rig.Run("CsSkinLayer", 4, null, null);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Vector4.Zero, none[2 * i]);
            Assert.Equal(Vector4.One, none[2 * i + 1]);
        }
    }

    /// <summary>
    /// Кожа с разрезом меша: слой, который отдаёт модуль (<see cref="BloodSkinGpu"/>, кровь на всех вершинах, свежая и засохшая), читается
    /// шейдером скина по номерам вершин меша после разреза — основа (0…V − 1) получает свой вид, добавленные разрезом (V…2V − 1) — нейтральный
    /// (A 0, M 1, покрытие 0); все числа конечны, A не больше 1. Раньше за видом в том же буфере лежало сырое (толщина, рождение) — вершины
    /// разреза читали его как вид: A до 2·10⁴ и Inf, «гирлянда» ярких пикселей по краю среза.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_SkinLayerOf_ServedLayer_CutVerticesNeutral()
    {
        var s = new SkinSurface(BloodBodyTests.Wall(seam: true));
        var skin = new BloodSkin(s, 2);
        int slot = skin.Acquire(7);
        for (int w = 0; w < s.WeldCount; w++)
            skin.Deposit(slot, w, 0.05f * (1 + w % 23) * skin.Area(w) * 1e3f, (w % 41) * 7f);
        int v = s.VertexCount;
        using var rig = new Rig();
        using var gpu = new BloodSkinGpu(rig.Renderer, skin);
        rig.Window.PumpMessages();
        rig.Renderer.Render(f => gpu.Build(f, 300, 1, BloodLookParams.Default), vsync: false);
        rig.Renderer.WaitForIdle();
        var look = new ulong[v];
        rig.Renderer.ReadStorage(gpu.Storage(slot), look.AsSpan());
        var result = rig.Run("CsSkinLayer", 2 * v, gpu.Storage(slot), null);
        int bloody = 0;
        for (int i = 0; i < 2 * v; i++)
        {
            var (add, mul) = (result[2 * i], result[2 * i + 1]);
            string at = i < v ? $"вершина основы {i}" : $"вершина разреза {i}";
            Assert.True(float.IsFinite(add.X) && float.IsFinite(add.Y) && float.IsFinite(add.Z) && float.IsFinite(add.W), $"{at}: A {add}");
            Assert.True(float.IsFinite(mul.X) && float.IsFinite(mul.Y) && float.IsFinite(mul.Z) && float.IsFinite(mul.W), $"{at}: M {mul}");
            Assert.True(add.X is >= 0 and <= 1 && add.Y is >= 0 and <= 1 && add.Z is >= 0 and <= 1, $"{at}: A {add}");
            if (i < v)
            {
                var (a, m) = Unpacked(look[i]);
                Near(a, add, $"{at}: A и покрытие");
                Near(m, mul, $"{at}: M и шероховатость");
                if (a.W > 0) bloody++;
            }
            else
            {
                Assert.Equal(Vector4.Zero, add);
                Assert.Equal(Vector4.One, mul);
            }
        }
        output.WriteLine($"вершин основы {v}, в крови {bloody}; вершин разреза {v} — нейтральны");
        Assert.True(bloody > v / 2, $"в крови {bloody} вершин из {v}");
    }

    /// <summary>
    /// Лезвие: заголовок <see cref="WeaponBloodLayout.WriteHeader"/> разбирается так, как пишется — место вдоль оси длины (первая половина
    /// клеток стороны 0 — один вид, вторая — другой), сторона по нормали плоскости (сторона 1 — третий вид), полное покрытие посреди
    /// лезвия — вид клетки как есть; до начала лезвия и за остриём — слоя нет.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_WeaponLayerOf_ParsesHeader_AlongAndSide()
    {
        const int cells = 48;
        var axisU = Vector3.Normalize(new Vector3(0.1f, 1, 0.05f));
        var axisV = Vector3.Normalize(Vector3.Cross(axisU, Vector3.UnitZ));
        var zone = new BladeBloodZone(new Vector3(0.01f, 0.1f, -0.02f), axisU, axisV, 0.8f, 0.025f, Matrix4x4.Identity);
        ulong near = Packed(new Vector3(0.30f, 0.02f, 0.01f), new Vector3(0.2f, 0.1f, 0.1f), 1, 0.1f);
        ulong far = Packed(new Vector3(0.05f, 0.004f, 0.002f), new Vector3(0.6f, 0.3f, 0.2f), 1, 0.4f);
        ulong back = Packed(new Vector3(0.12f, 0.01f, 0.005f), new Vector3(0.4f, 0.05f, 0.05f), 1, 0.25f);
        var buffer = new ulong[WeaponBloodLayout.Elements(cells)];
        WeaponBloodLayout.WriteHeader(buffer, zone, cells, 0x1234);
        for (int i = 0; i < cells; i++)
        {
            buffer[WeaponBloodLayout.Header + i] = i < cells / 2 ? near : far;
            buffer[WeaponBloodLayout.Header + cells + i] = back;
        }
        Vector3 At(float along, float across, float side) =>
            zone.Origin + zone.AxisU * (along * zone.Length) + zone.AxisV * (across * zone.Half) + zone.Normal * side;
        (Vector3 Point, ulong? Texel, string What)[] cases =
        [
            (At(0.15f, 0.3f, 0.001f), near, "сторона 0 у гарды"),
            (At(0.20f, -0.6f, 0.002f), near, "сторона 0 у гарды, другой край"),
            (At(0.85f, 0.1f, 0.001f), far, "сторона 0 у острия"),
            (At(0.50f, 0.2f, -0.001f), back, "сторона 1"),
            (At(0.80f, -0.4f, -0.002f), back, "сторона 1 у острия"),
            (At(-0.05f, 0, 0.001f), null, "рукоять"),
            (At(1.05f, 0, 0.001f), null, "за остриём"),
        ];
        using var rig = new Rig();
        using var layer = rig.Renderer.CreateStorage<ulong>("test.layer.blade", buffer);
        using var points = rig.Renderer.CreateStorage<Vector4>("test.layer.points", cases.Select(c => new Vector4(c.Point, 0)).ToArray());
        var gpu = rig.Run("CsWeaponLayer", cases.Length, layer, points);
        for (int i = 0; i < cases.Length; i++)
        {
            var (add, mul) = cases[i].Texel is { } t ? Unpacked(t) : (Vector4.Zero, Vector4.One);
            Near(add, gpu[2 * i], $"{cases[i].What}: A и покрытие");
            Near(mul, gpu[2 * i + 1], $"{cases[i].What}: M и шероховатость");
        }
    }

    /// <summary>
    /// Плёнка пола на краю пятен: рождение карты — среднее по положительным весам бикубика, поэтому в диапазоне рождений крови под
    /// выборкой (100…200 с; рождение пустых текселей 5000 с не протекает) и конечно; ρ карты ≥ 0. Поле с пиком 40 (лужа 12 см) у
    /// пустых текселей даёт отрицательный лепесток (ρ ниже −0.3): наклон профиля мениска при мениске 0.01 мм (низ ручки) конечен и не больше
    /// толщины на ρ (иначе exp переполнялся — NaN в нормали).
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public void Gpu_FloorFilm_MapBirthInRange_MeniscusFinite()
    {
        const int n = 8;
        var map = new uint[n * n];
        var field = new ulong[n * n];
        for (int i = 0; i < map.Length; i++) map[i] = MapTexel(0, 5000);
        map[3 + 3 * n] = MapTexel(2.0f, 100);
        map[4 + 3 * n] = MapTexel(1.0f, 200);
        map[3 + 4 * n] = MapTexel(0.5f, 150);
        map[5 + 5 * n] = MapTexel(0.01f, 200);
        field[4 + 4 * n] = FieldTexel(40);
        field[5 + 4 * n] = FieldTexel(0.2f);
        field[2 + 6 * n] = FieldTexel(0.05f);
        const float thick = 3, meniscus = 0.01f;
        var samples = new List<Vector4>();
        for (float y = 0; y <= n - 1; y += 0.1f)
            for (float x = 0; x <= n - 1; x += 0.1f)
                samples.Add(new Vector4(x, y, thick, meniscus));
        using var rig = new Rig();
        using var mapBuffer = rig.Renderer.CreateStorage<uint>("test.layer.map", map);
        using var fieldBuffer = rig.Renderer.CreateStorage<ulong>("test.layer.field", field);
        using var points = rig.Renderer.CreateStorage<Vector4>("test.layer.points", samples.ToArray());
        var gpu = rig.Run("CsFloorSample", samples.Count, null, points, mapBuffer, fieldBuffer);
        int withMap = 0, negative = 0;
        float minRho = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var (a, b) = (gpu[2 * i], gpu[2 * i + 1]);
            string at = $"выборка ({samples[i].X:F1}, {samples[i].Y:F1})";
            Assert.True(float.IsFinite(a.X) && a.X >= 0, $"{at}: ρ карты {a.X}");
            Assert.True(float.IsFinite(a.Y), $"{at}: рождение {a.Y}");
            if (a.X > 0)
            {
                withMap++;
                Assert.InRange(a.Y, 100 - 0.5f, 200 + 0.5f);
            }
            Assert.True(float.IsFinite(a.Z) && float.IsFinite(b.X) && float.IsFinite(b.Y), $"{at}: ρ поля {a.Z}, градиент ({b.X}, {b.Y})");
            Assert.True(float.IsFinite(a.W) && a.W >= 0 && a.W <= thick, $"{at}: наклон профиля {a.W} при ρ {a.Z}");
            if (a.Z < 0) negative++;
            minRho = MathF.Min(minRho, a.Z);
        }
        output.WriteLine($"выборок {samples.Count}: с кровью карты {withMap}, ρ поля ниже нуля {negative} (до {minRho:F3})");
        Assert.True(withMap > 100, $"выборок с кровью карты {withMap}");
        Assert.True(minRho < -0.3f, $"отрицательный лепесток не проверен: ρ поля не ниже {minRho}");

        static uint MapTexel(float rho, float birth) =>
            BitConverter.HalfToUInt16Bits((Half)rho) | (uint)BitConverter.HalfToUInt16Bits((Half)birth) << 16;
        static ulong FieldTexel(float rho) => (ulong)BitConverter.HalfToUInt16Bits((Half)rho) << 32;
    }
}
