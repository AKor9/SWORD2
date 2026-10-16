using Xunit.Abstractions;
using static Sword.Engine.Fluid3D.Tests.Fluid3DTestKit;

namespace Sword.Engine.Fluid3D.Tests;

/// <summary>
/// Вихревой шум газа на CPU: средняя сила = параметр, поле бездивергентно (численно; контроль с перевёрнутой компонентой — нет), узор стоит
/// в мире (сдвиг окна целыми ячейками не меняет силу в мировой точке, в том числе за километр от начала координат), подъём узора — ровно на
/// подъём·время, маска плотностью — пустые ячейки без шума бит в бит.
/// </summary>
public sealed class Fluid3DCurlNoiseTests(ITestOutputHelper output)
{
    [Fact]
    public void CurlNoise_RmsIsAmplitude()
    {
        var rng = new Random(5);
        double sum = 0;
        const int n = 200_000;
        for (int i = 0; i < n; i++)
        {
            var c = Fluid3DCurlNoise.Curl(rng.NextSingle() * 256, rng.NextSingle() * 256, rng.NextSingle() * 256);
            sum += (double)c.X * c.X + (double)c.Y * c.Y + (double)c.Z * c.Z;
        }
        double rms = Math.Sqrt(sum / n);
        output.WriteLine($"среднеквадратичная |curl ψ| {rms:F4} (единицы решётки), с нормировкой {rms * Fluid3DCurlNoise.Normalization:F4}");
        Assert.InRange(rms * Fluid3DCurlNoise.Normalization, 0.97, 1.03);
    }

    [Fact]
    public void CurlNoise_IsDivergenceFree_ControlIsNot()
    {
        // Центральные разности с шагом 0.025 узла по кубу в узел: Σ|div F| (остаток — ошибка разностей, ∝ h²) против Σ(|∂Fx/∂x| + |∂Fy/∂y| + |∂Fz/∂z|). Контроль —
        // (Fx, Fy, −Fz): его дивергенция −2·∂Fz/∂z.
        const float h = 0.025f;
        const int n = 40;
        double div = 0, scale = 0, control = 0;
        for (int k = 0; k < n; k++)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = 3.3f + i * h, y = 5.7f + j * h, z = 1.1f + k * h;
                    float dx = (Fluid3DCurlNoise.Curl(x + h, y, z).X - Fluid3DCurlNoise.Curl(x - h, y, z).X) / (2 * h);
                    float dy = (Fluid3DCurlNoise.Curl(x, y + h, z).Y - Fluid3DCurlNoise.Curl(x, y - h, z).Y) / (2 * h);
                    float dz = (Fluid3DCurlNoise.Curl(x, y, z + h).Z - Fluid3DCurlNoise.Curl(x, y, z - h).Z) / (2 * h);
                    div += Math.Abs(dx + dy + dz);
                    control += Math.Abs(dx + dy - dz);
                    scale += Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                }
        output.WriteLine($"Σ|div| / Σ|∂| = {div / scale:E3}; контроль {control / scale:F3}");
        Assert.True(div / scale < 0.002, $"дивергенция {div / scale:E3}");
        Assert.True(control / scale > 0.3, $"контроль {control / scale:F3}");
    }

    [Fact]
    public void CurlNoise_StandsInWorld_WindowShiftKeepsForce_RiseMovesPatternUp()
    {
        // Окно за 1.2 км от начала координат; второе окно сдвинуто на (3, −2, 5) ячеек; подъём 0.18 м/с — 6 ячеек за 60 шагов (1 с).
        var p = Fluid3DParams.Smoke with { Nx = 20, Ny = 24, Nz = 16, CurlNoise = 2, CurlNoiseScale = 0.2f, CurlNoiseRise = 0.18f };
        var a = new Fluid3DWindow(40_000, -37, 12_345, p.Nx, p.Ny, p.Nz, p.CellSize, default);
        var b = a with { X = a.X + 3, Y = a.Y - 2, Z = a.Z + 5 };
        var ka = Fluid3DConstants.Build(p, a, (0, 0, 0), 600, []);
        var kb = Fluid3DConstants.Build(p, b, (3, -2, 5), 600, []);
        var later = Fluid3DConstants.Build(p, a, (0, 0, 0), 660, []);
        var still = Fluid3DConstants.Build(p with { CurlNoiseRise = 0 }, a, (0, 0, 0), 660, []);
        double sum = 0, shiftErr = 0, riseErr = 0, stillDiff = 0;
        int count = 0;
        for (int z = 5; z < p.Nz; z++)
            for (int y = 0; y < p.Ny - 6; y++)
                for (int x = 3; x < p.Nx; x++)
                {
                    var fa = Fluid3DReference.NoiseForce(ka, x, y, z);
                    var fb = Fluid3DReference.NoiseForce(kb, x - 3, y + 2, z - 5);
                    var up = Fluid3DReference.NoiseForce(later, x, y + 6, z);
                    var fixedUp = Fluid3DReference.NoiseForce(still, x, y + 6, z);
                    sum += Len2(fa);
                    shiftErr = Math.Max(shiftErr, Math.Sqrt(Len2((fa.X - fb.X, fa.Y - fb.Y, fa.Z - fb.Z))));
                    riseErr = Math.Max(riseErr, Math.Sqrt(Len2((fa.X - up.X, fa.Y - up.Y, fa.Z - up.Z))));
                    stillDiff += Math.Sqrt(Len2((fa.X - fixedUp.X, fa.Y - fixedUp.Y, fa.Z - fixedUp.Z)));
                    count++;
                }
        double rms = Math.Sqrt(sum / count);
        output.WriteLine($"|curl| rms {rms:F3}; сдвиг окна — наибольшее расхождение {shiftErr:E2}; подъём за 1 с на 6 ячеек — {riseErr:E2}; " +
            $"без подъёма те же ячейки расходятся в среднем на {stillDiff / count:F3}");
        Assert.True(shiftErr < 1e-3 * rms, $"сдвиг окна меняет силу: {shiftErr}");
        Assert.True(riseErr < 1e-3 * rms, $"подъём не на 6 ячеек: {riseErr}");
        Assert.True(stillDiff / count > 0.5 * rms, "контроль: без подъёма узор на 6 ячеек выше — другой");
    }

    [Fact]
    public void Forces_NoiseMaskedByDensity_EmptyCellsBitExact_OffIsNoNoise()
    {
        // Случайные поля, ρ = 0 в левой половине: там силы с шумом = без шума бит в бит; в правой (ρ до 1, маска 0.05) — почти везде другие.
        var p = Small(16, 16, 16) with { CurlNoise = 5, CurlNoiseScale = 0.12f, CurlNoiseDensity = 0.05f };
        var off = p with { CurlNoise = 0 };
        var w = WindowAt(p);
        var fields = RandomFields(16, 16, 16, 3);
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 8; x++) fields.ScalarScratch[2 * fields.Index(x, y, z)] = default;
        var with = new Fluid3DReference(p, fields.Clone(), w);
        var without = new Fluid3DReference(off, fields.Clone(), w);
        with.Stage(Fluid3DStage.Forces, with.Constants(40, w.Target, [], out _));
        without.Stage(Fluid3DStage.Forces, without.Constants(40, w.Target, [], out _));
        int same = 0, differ = 0;
        for (int z = 0; z < 16; z++)
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int i = fields.Index(x, y, z);
                    bool equal = SameBits(with.Fields.Velocity.AsSpan(4 * i, 4), without.Fields.Velocity.AsSpan(4 * i, 4));
                    if (x < 8) Assert.True(equal, $"пустая ячейка ({x}, {y}, {z}) получила шум");
                    else if (equal) same++;
                    else differ++;
                }
        output.WriteLine($"ρ > 0: шум изменил {differ} ячеек из {same + differ}");
        Assert.True(differ > 0.9 * (same + differ), $"шум изменил только {differ} из {same + differ}");
    }

    private static double Len2((float X, float Y, float Z) v) => (double)v.X * v.X + (double)v.Y * v.Y + (double)v.Z * v.Z;
}
