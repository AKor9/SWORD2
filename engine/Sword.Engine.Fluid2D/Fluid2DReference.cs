namespace Sword.Engine.Fluid2D;

/// <summary>
/// CPU-эталон шага (float32): те же формулы, порядок операций, ручная билинейная выборка с зажимом и хранение в half, что у
/// <c>fluid2d.hlsl</c> — по нему тесты сверяют GPU. Состояние — <see cref="Fields"/>; шумы — картинки <see cref="Fluid2DNoiseImage"/>
/// (нужны, только если включены в параметрах).
/// </summary>
public sealed class Fluid2DReference
{
    private readonly Fluid2DNoiseImage? _density, _amp;

    public Fluid2DReference(Fluid2DParams parameters, Fluid2DNoiseImage? densityNoise, Fluid2DNoiseImage? ampNoise, Fluid2DFields? fields = null)
    {
        parameters.Validate();
        Params = parameters;
        _density = densityNoise;
        _amp = ampNoise;
        Fields = fields ?? new Fluid2DFields(parameters.Resolution);
        if (Fields.N != parameters.Resolution) throw new ArgumentException($"поля {Fields.N}², а сим {parameters.Resolution}²");
    }

    /// <summary>Параметры (меняются между шагами; сторона — нет).</summary>
    public Fluid2DParams Params { get; set; }

    public Fluid2DFields Fields { get; }

    /// <summary>Шаг целиком: Composite → Advection → Divergence → давление (2 прохода, у грубого решателя — 1).</summary>
    public void Step(in Fluid2DStepInput input, ReadOnlySpan<Fluid2DBrushTexel> brush = default)
    {
        var c = Constants(input, brush);
        Composite(c, brush);
        Advection(c);
        Divergence(c, brush);
        if (Params.CrudePressureSolver) Pressure(c, c.CrudePass != 0, Fields.Scratch, Fields.Pressure);
        else
        {
            Pressure(c, false, Fields.Scratch, Fields.Pressure);
            Pressure(c, true, Fields.Pressure, Fields.Scratch);
            Fields.SwapPressure();
        }
    }

    /// <summary>Одна стадия на текущих полях (буферы — как у <see cref="Fluid2DStage"/>; давление ролями не меняется).</summary>
    public void Stage(Fluid2DStage stage, in Fluid2DStepInput input, ReadOnlySpan<Fluid2DBrushTexel> brush = default)
    {
        var c = Constants(input, brush);
        switch (stage)
        {
            case Fluid2DStage.Composite: Composite(c, brush); break;
            case Fluid2DStage.Advection: Advection(c); break;
            case Fluid2DStage.Divergence: Divergence(c, brush); break;
            case Fluid2DStage.PressureX: Pressure(c, false, Fields.Scratch, Fields.Pressure); break;
            case Fluid2DStage.PressureY: Pressure(c, true, Fields.Pressure, Fields.Scratch); break;
            default: throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }
    }

    /// <summary>Выход <c>RT_Composite</c> (четыре half на тексель): (v·edge10, ρ^Contrast, max(wet·маски, 0.01)).</summary>
    public Half[] Export()
    {
        var c = Constants(new Fluid2DStepInput(0, default, default), default);
        int n = (int)c.N;
        var result = new Half[4 * n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) ExportTexel(c, x, y, result);
        return result;
    }

    internal Fluid2DConstants Constants(in Fluid2DStepInput input, ReadOnlySpan<Fluid2DBrushTexel> brush)
    {
        var p = Params;
        if (p.Resolution != Fields.N) throw new InvalidOperationException($"сторона сима — {Fields.N}, а в параметрах {p.Resolution}");
        bool hasBrush = !brush.IsEmpty;
        if (hasBrush && brush.Length != p.BrushResolution * p.BrushResolution)
            throw new ArgumentException($"кисть {brush.Length} текселей, нужно {p.BrushResolution}²", nameof(brush));
        if (p.VeloAmplitudeNoiseEnabled && _amp is null) throw new InvalidOperationException("amp-шум включён, картинки нет");
        if (p.DensityNoiseEnabled && _density is null) throw new InvalidOperationException("шум плотности включён, картинки нет");
        var amp = _amp is null ? default : new Fluid2DNoise(0, 0, _amp.Width, _amp.Height);
        var density = _density is null ? default : new Fluid2DNoise(0, 0, _density.Width, _density.Height);
        return Fluid2DConstants.Build(p, input, hasBrush, amp, density);
    }

    // ── стадии ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void Composite(in Fluid2DConstants c, ReadOnlySpan<Fluid2DBrushTexel> brush)
    {
        int n = (int)c.N;
        var output = new Half[Fields.Composite.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) CompositeTexel(c, x, y, brush, output);
        output.CopyTo(Fields.Composite, 0);
    }

    private void CompositeTexel(in Fluid2DConstants c, int x, int y, ReadOnlySpan<Fluid2DBrushTexel> brush, Half[] output)
    {
        var adv = Fields.Advected;
        var prs = Fields.Pressure;
        int n = (int)c.N;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        float sx = uvx + c.Wod.X, sy = uvy + c.Wod.Y;
        float d = c.SimSpeed * c.InvN;

        var a = Bilinear4(adv, n, sx, sy);
        float px1 = BilinearP(prs, n, sx + d, sy).P, px0 = BilinearP(prs, n, sx - d, sy).P;
        float py1 = BilinearP(prs, n, sx, sy + d).P, py0 = BilinearP(prs, n, sx, sy - d).P;
        float vx = a.X - 0.5f * (px1 - px0), vy = a.Y - 0.5f * (py1 - py0);

        Vec4 paint = default, bitmap = default;
        if (c.BrushN > 0) BrushBilinear(brush, (int)c.BrushN, uvx, uvy, out paint, out bitmap);
        float bvx = c.BrushVelocity * paint.X, bvy = c.BrushVelocity * paint.Y;
        float amp = 1f;
        if (c.AmpEnabled != 0)
            amp = Saturate(1f + NoiseBilinear(_amp!, uvx * c.AmpSize + c.AmpOffset.X, uvy * c.AmpSize + c.AmpOffset.Y) - c.AmpNoise);
        float inx = c.VelocityBase.X * amp + c.VeloStrength * bitmap.X;
        float iny = c.VelocityBase.Y * amp + c.VeloStrength * bitmap.Y;
        float bb = MathF.Min(bvx * bvx + bvy * bvy, 1f);
        float carrier = a.Z + 0.1f * paint.Z * c.Eraser;
        float vaddx = carrier * (bvx + (1f - bb) * inx), vaddy = carrier * (bvy + (1f - bb) * iny);

        float nzx = 0, nzy = 0;
        if (c.DirNoiseAmp != 0)
        {
            float mask = MathF.Max(MathF.Min(0.2f * (vx * vx + vy * vy), 1f), c.DirNoiseMask);
            var (sxn, syn) = SimplexNoise.Sample(uvx * c.DirNoiseScale + c.DirNoiseOffset.X, uvy * c.DirNoiseScale + c.DirNoiseOffset.Y, c.DirNoiseTime);
            float k = c.DirNoiseAmp * mask;
            nzx = k * sxn;
            nzy = k * syn;
        }
        float m = EdgeDistance(uvx, uvy);
        float ring = Saturate(99.99f - 100f * m) >= 1f ? 1f : 0f;
        float collision = MathF.Min(MathF.Max(MathF.Max(ring, c.CollisionFloor), c.Weaken), 1f);
        float vnx = Math.Clamp(0.5f * vaddx + nzx + vx, -c.VelocityClamp, c.VelocityClamp) * collision;
        float vny = Math.Clamp(0.5f * vaddy + nzy + vy, -c.VelocityClamp, c.VelocityClamp) * collision;

        float dn = 1f;
        if (c.DensityEnabled != 0)
            dn = 1f + c.DensityAmount * (NoiseBilinear(_density!, uvx * c.DensityScale + c.DensityOffset.X, uvy * c.DensityScale + c.DensityOffset.Y) - 0.5f);
        float ep = MathF.Pow(EdgeMask(m, c.EdgeInvWidth), c.EdgePow);
        float rho = Saturate(a.Z - MathF.Min(paint.Z, c.Eraser) + ((1f - c.Eraser) * paint.Z + 0.2f * bitmap.Z) * dn)
            * (0.5f * (c.DensityAccumulation * MathF.Max(ep, 0.7f)) + 0.5f) * ep;
        rho = MathF.Max(rho, c.DensityFloor);

        int wx = Nearest(sx, n), wy = Nearest(sy, n);
        float wetPrev = wx >= 0 && wx < n && wy >= 0 && wy < n ? (float)adv[(wy * n + wx) * 4 + 3] : 0f;
        float wet = (0.982f * wetPrev + c.WetFeed * rho) * EdgeMask(m, c.WetInvWidth);
        Store4(output, y * n + x, vnx, vny, rho, wet);
    }

    private void Advection(in Fluid2DConstants c)
    {
        int n = (int)c.N;
        var output = new Half[Fields.Advected.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) AdvectionTexel(c, x, y, output);
        output.CopyTo(Fields.Advected, 0);
    }

    private void AdvectionTexel(in Fluid2DConstants c, int x, int y, Half[] output)
    {
        var comp = Fields.Composite;
        int n = (int)c.N;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        int i = (y * n + x) * 4;
        float hx = (float)comp[i], hy = (float)comp[i + 1], hw = (float)comp[i + 3];
        float step = c.SimSpeed * c.InvN;
        var s = Bilinear4(comp, n, uvx - hx * step, uvy - hy * step);
        Store4(output, y * n + x, s.X * c.VelocityFeedback, s.Y * c.VelocityFeedback, s.Z * c.VelocityFeedback, hw);
    }

    private void Divergence(in Fluid2DConstants c, ReadOnlySpan<Fluid2DBrushTexel> brush)
    {
        int n = (int)c.N;
        var output = new Half[Fields.Scratch.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) DivergenceTexel(c, x, y, brush, output);
        output.CopyTo(Fields.Scratch, 0);
    }

    private void DivergenceTexel(in Fluid2DConstants c, int x, int y, ReadOnlySpan<Fluid2DBrushTexel> brush, Half[] output)
    {
        var adv = Fields.Advected;
        int n = (int)c.N;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        float dvx = Axis4(adv, n, x, y, c.SimSpeed, false).X - Axis4(adv, n, x, y, -c.SimSpeed, false).X;
        float dvy = Axis4(adv, n, x, y, c.SimSpeed, true).Y - Axis4(adv, n, x, y, -c.SimSpeed, true).Y;
        float paintRho = 0;
        if (c.BrushN > 0)
        {
            int bn = (int)c.BrushN;
            int bx = Math.Clamp(Nearest(uvx, bn), 0, bn - 1), by = Math.Clamp(Nearest(uvy, bn), 0, bn - 1);
            paintRho = (float)brush[by * bn + bx].Density;
        }
        float div = Math.Clamp(c.DivergenceMult * (c.Puncture * paintRho + (dvx + dvy)), -2f, 2f);
        float p0 = BilinearP(Fields.Pressure, n, uvx + c.Wod.X, uvy + c.Wod.Y).P;
        Store2(output, y * n + x, p0, div);
    }

    private void Pressure(in Fluid2DConstants c, bool vertical, Half[] source, Half[] destination)
    {
        int n = (int)c.N;
        var output = new Half[destination.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) PressureTexel(c, x, y, vertical, source, output);
        output.CopyTo(destination, 0);
    }

    private static void PressureTexel(in Fluid2DConstants c, int x, int y, bool vertical, Half[] source, Half[] output)
    {
        int n = (int)c.N;
        float accP = 0, accD = 0;
        for (int j = 0; j <= (int)c.Taps; j++)
        {
            var s = AxisP(source, n, x, y, (j - c.TapCenter) * c.SimSpeed, vertical);
            accP += s.P * c.WeightP[j];
            accD += s.D * c.WeightD[j];
        }
        float rp = (vertical ? accP - accD : accP) * 0.999f, rd = accD * 0.999f;
        if (c.PressureMask != 0)
        {
            float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
            float mask = MathF.Pow(EdgeMask(EdgeDistance(uvx, uvy), c.PressureInvWidth), c.PressurePow);
            rp *= mask;
            rd *= mask;
        }
        Store2(output, y * n + x, rp * c.PressureFeedback, rd * c.DivergenceFeedback);
    }

    private void ExportTexel(in Fluid2DConstants c, int x, int y, Half[] output)
    {
        int n = (int)c.N;
        int i = (y * n + x) * 4;
        var comp = Fields.Composite;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        float sx = (float)comp[i], sy = (float)comp[i + 1], sz = (float)comp[i + 2], sw = (float)comp[i + 3];
        float edge10 = Saturate(9.99f - MathF.Max(MathF.Abs(20f * uvx - 10f), MathF.Abs(20f * uvy - 10f)));
        float rc = MathF.Pow(MathF.Max(sz, 0f), c.Contrast);
        float inverted = 1f - rc;
        float outRho = (c.ExportFlags & Fluid2DConstants.ExportInvert) != 0 ? inverted : rc;
        float wet = (c.ExportFlags & Fluid2DConstants.ExportInvertedAlpha) != 0 ? inverted
            : sw * EdgeMask(EdgeDistance(uvx, uvy), c.ExportInvWidth) * edge10;
        Store4(output, y * n + x, sx * edge10, sy * edge10, outRho, MathF.Max(wet, 0.01f));
    }

    // ── выборка (как в fluid2d.hlsl) ────────────────────────────────────────────────────────────────────────────

    internal readonly record struct Vec4(float X, float Y, float Z, float W);

    private static Vec4 Texel4(Half[] g, int n, int x, int y)
    {
        int i = (Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)) * 4;
        return new Vec4((float)g[i], (float)g[i + 1], (float)g[i + 2], (float)g[i + 3]);
    }

    private static Vec4 Lerp(in Vec4 a, in Vec4 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);

    internal static Vec4 Bilinear4(Half[] g, int n, float u, float v)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var ab = Lerp(Texel4(g, n, x, y), Texel4(g, n, x + 1, y), tx);
        var cd = Lerp(Texel4(g, n, x, y + 1), Texel4(g, n, x + 1, y + 1), tx);
        return Lerp(ab, cd, ty);
    }

    private static (float P, float D) TexelP(Half[] g, int n, int x, int y)
    {
        int i = (Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)) * 2;
        return ((float)g[i], (float)g[i + 1]);
    }

    private static (float P, float D) LerpP((float P, float D) a, (float P, float D) b, float t) =>
        (a.P + (b.P - a.P) * t, a.D + (b.D - a.D) * t);

    private static (float P, float D) BilinearP(Half[] g, int n, float u, float v)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var ab = LerpP(TexelP(g, n, x, y), TexelP(g, n, x + 1, y), tx);
        var cd = LerpP(TexelP(g, n, x, y + 1), TexelP(g, n, x + 1, y + 1), tx);
        return LerpP(ab, cd, ty);
    }

    private static Vec4 Axis4(Half[] g, int n, int x, int y, float offset, bool vertical)
    {
        float p = (vertical ? y : x) + offset;
        float f = MathF.Floor(p);
        float t = p - f;
        int i = (int)f;
        var a = vertical ? Texel4(g, n, x, i) : Texel4(g, n, i, y);
        var b = vertical ? Texel4(g, n, x, i + 1) : Texel4(g, n, i + 1, y);
        return Lerp(a, b, t);
    }

    private static (float P, float D) AxisP(Half[] g, int n, int x, int y, float offset, bool vertical)
    {
        float p = (vertical ? y : x) + offset;
        float f = MathF.Floor(p);
        float t = p - f;
        int i = (int)f;
        var a = vertical ? TexelP(g, n, x, i) : TexelP(g, n, i, y);
        var b = vertical ? TexelP(g, n, x, i + 1) : TexelP(g, n, i + 1, y);
        return LerpP(a, b, t);
    }

    private static void BrushTexel(ReadOnlySpan<Fluid2DBrushTexel> g, int n, int x, int y, out Vec4 lo, out Vec4 hi)
    {
        ref readonly var b = ref g[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)];
        lo = new Vec4((float)b.VelocityX, (float)b.VelocityY, (float)b.Density, (float)b.Capture);
        hi = new Vec4((float)b.BitmapVelocityX, (float)b.BitmapVelocityY, (float)b.BitmapDensity, (float)b.Unused);
    }

    internal static void BrushBilinear(ReadOnlySpan<Fluid2DBrushTexel> g, int n, float u, float v, out Vec4 lo, out Vec4 hi)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        BrushTexel(g, n, x, y, out var a0, out var a1);
        BrushTexel(g, n, x + 1, y, out var b0, out var b1);
        BrushTexel(g, n, x, y + 1, out var c0, out var c1);
        BrushTexel(g, n, x + 1, y + 1, out var d0, out var d1);
        lo = Lerp(Lerp(a0, b0, tx), Lerp(c0, d0, tx), ty);
        hi = Lerp(Lerp(a1, b1, tx), Lerp(c1, d1, tx), ty);
    }

    private static int Nearest(float u, int n) => (int)MathF.Round(u * n - 0.5f, MidpointRounding.ToEven);

    private static float NoiseTexel(Fluid2DNoiseImage img, int x, int y) =>
        img.Values[Wrap(y, img.Height) * img.Width + Wrap(x, img.Width)];

    private static int Wrap(int i, int size) => ((i % size) + size) % size;

    private static float NoiseBilinear(Fluid2DNoiseImage img, float u, float v)
    {
        float px = u * img.Width - 0.5f, py = v * img.Height - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        float a = NoiseTexel(img, x, y), b = NoiseTexel(img, x + 1, y);
        float c = NoiseTexel(img, x, y + 1), d = NoiseTexel(img, x + 1, y + 1);
        float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
        return ab + (cd - ab) * ty;
    }

    private static float EdgeDistance(float u, float v) => MathF.Max(MathF.Abs(2f * u - 1f), MathF.Abs(2f * v - 1f));

    private static float EdgeMask(float m, float invWidth) => Saturate(invWidth - 0.01f - m * invWidth);

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static void Store4(Half[] g, int texel, float x, float y, float z, float w)
    {
        int i = texel * 4;
        g[i] = (Half)x;
        g[i + 1] = (Half)y;
        g[i + 2] = (Half)z;
        g[i + 3] = (Half)w;
    }

    private static void Store2(Half[] g, int texel, float p, float d)
    {
        g[texel * 2] = (Half)p;
        g[texel * 2 + 1] = (Half)d;
    }
}
