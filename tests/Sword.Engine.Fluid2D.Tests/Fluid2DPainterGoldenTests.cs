using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Xunit.Abstractions;

namespace Sword.Engine.Fluid2D.Tests;

/// <summary>
/// Одиночная точка: CPU-эталон Painter (числа точки <c>Fluid2DBrushPoint.Build</c> + тексель <c>Fluid2DPainterReference.Texel</c>) = офлайн-модель
/// BR (<c>painter_model.py</c>, сверенная с hex-DXBC <c>SM000_0012</c> на 2000 текселях × 500 наборах). Золотые векторы —
/// <c>Data/painter_golden.json</c> (генератор <c>artifacts/research/fluidninja/brush/painter/golden_vectors.py</c>): 13 наборов — факел
/// (стоит, движется, молодой, у края), кровь (мышь 1 и 3 м/с), песок, «всё сразу» (IF 0.8, отрицательный прокол, FadeZ, Kill, битмап, шум в
/// UV площадки), частица, обломок «только скорость», телепорт, painter-режим, сложение.
/// </summary>
/// <remarks>
/// Допуск — 2·10⁻⁵ + 2·10⁻⁴·|x|: модель считает в double, эталон — в float (exp2, pow, деление; координаты шумов по модулю 1 — сдвиг
/// веса выборки ~10⁻⁶); скорость радиального члена ×100·pz усиливает ошибку UV до ~10⁻⁵. Это на порядок меньше ULP half у 0.1.
/// </remarks>
public sealed class Fluid2DPainterGoldenTests(ITestOutputHelper output)
{
    private static readonly string[] Layers = ["vx", "vy", "ρ", "MF.w", "bitmap vx", "bitmap vy", "bitmap ρ"];

    [Fact]
    public void SinglePoint_MatchesOfflineModel()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "painter_golden.json")));
        var noise = PainterKit.Noise();
        var bitmap = PainterKit.Bitmap();
        double worst = 0;
        int texels = 0;
        Span<float> o = stackalloc float[7];
        foreach (var c in json.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            var p = Params(c.GetProperty("params"));
            int bn = p.BrushResolution;
            var input = new Fluid2DStepInput(c.GetProperty("step").GetUInt64(), Vec2(c.GetProperty("worldOffset")), Vec2(c.GetProperty("worldOffsetDelta")));
            float age = (float)(c.GetProperty("ageSteps").GetInt64() * p.StepSeconds);
            var pt = c.GetProperty("point");
            var record = Fluid2DBrushPoint.Build(p, (Fluid2DBrushSource)pt.GetProperty("src").GetInt32(), false, Vec3(pt.GetProperty("pos")),
                Vec2(pt.GetProperty("prev")), Vec3(pt.GetProperty("vel")), Vec3(pt.GetProperty("prevvel")), (float)pt.GetProperty("scale").GetDouble(),
                age, input.WorldOffsetDelta, out _);
            bool hasBitmap = c.GetProperty("bitmap").GetBoolean();
            var k = Fluid2DConstants.Build(p, input, true, default, default);
            k.SetPainter(p, input, 1, 1, 1, 0, new Fluid2DNoise(0, 0, noise.Width, noise.Height),
                hasBitmap ? new Fluid2DNoise(0, 0, bitmap.Width, bitmap.Height) : default);
            var old = PainterKit.PreviousPaint(bn);
            double caseWorst = 0;
            string where = "";
            foreach (var t in c.GetProperty("texels").EnumerateArray())
            {
                int x = t.GetProperty("x").GetInt32(), y = t.GetProperty("y").GetInt32();
                Fluid2DPainterReference.Texel(k, [record], [0u], false, x, y, old, noise, hasBitmap ? bitmap : null, o);
                int layer = 0;
                foreach (var expected in t.GetProperty("out").EnumerateArray())
                {
                    double e = expected.GetDouble(), got = o[layer];
                    double err = Math.Abs(got - e) / (2e-5 + 2e-4 * Math.Abs(e));
                    if (err > caseWorst) (caseWorst, where) = (err, $"({x}, {y}) {Layers[layer]}: {got} против {e}");
                    layer++;
                }
                texels++;
            }
            output.WriteLine($"{name}: худшее {caseWorst:F3} допуска — {where}");
            worst = Math.Max(worst, caseWorst);
            Assert.True(caseWorst <= 1, $"{name}: {where}");
        }
        output.WriteLine($"текселей {texels}, худшее {worst:F3} допуска");
        Assert.True(texels >= 600);
    }

    private static Fluid2DParams Params(JsonElement e)
    {
        float extents = (float)e.GetProperty("ExtentsMetres").GetDouble();
        int bn = e.GetProperty("BrushResolution").GetInt32();
        object p = Fluid2DParams.Torch with { Resolution = 2 * bn, PaintBufferDownScaleFactor = 2, Extents = new Vector3(extents) };
        foreach (var prop in e.EnumerateObject())
        {
            if (prop.Name is "ExtentsMetres" or "BrushResolution") continue;
            var info = typeof(Fluid2DParams).GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"нет параметра {prop.Name}");
            object value = info.PropertyType == typeof(bool) ? prop.Value.GetBoolean()
                : info.PropertyType == typeof(int) ? (object)prop.Value.GetInt32()
                : (float)prop.Value.GetDouble();
            info.SetValue(p, value);
        }
        return (Fluid2DParams)p;
    }

    private static Vector2 Vec2(JsonElement e) => new((float)e[0].GetDouble(), (float)e[1].GetDouble());

    private static Vector3 Vec3(JsonElement e) => new((float)e[0].GetDouble(), (float)e[1].GetDouble(), (float)e[2].GetDouble());
}
