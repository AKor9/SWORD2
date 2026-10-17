namespace Sword.Engine.Fluid2D;

/// <summary>
/// CPU-эталон шага (float32): те же формулы, порядок операций, ручная билинейная выборка с зажимом и хранение в half, что у
/// <c>fluid2d.hlsl</c> — по нему тесты сверяют GPU. Состояние — <see cref="Fields"/>; шумы — картинки <see cref="Fluid2DNoiseImage"/>
/// (нужны, только если включены в параметрах); формы штампов — картинки слоёв (форма k ≥ 1 — элемент k − 1, канал r).
/// </summary>
public sealed class Fluid2DReference
{
    private readonly Fluid2DNoiseImage? _density, _amp;
    private readonly IReadOnlyList<Fluid2DImage> _stampShapes;
    private float[]? _stampGrid, _rhoOut;

    public Fluid2DReference(Fluid2DParams parameters, Fluid2DNoiseImage? densityNoise, Fluid2DNoiseImage? ampNoise, Fluid2DFields? fields = null,
        IReadOnlyList<Fluid2DImage>? stampShapes = null)
    {
        parameters.Validate();
        Params = parameters;
        _density = densityNoise;
        _amp = ampNoise;
        _stampShapes = stampShapes ?? [];
        Fields = fields ?? new Fluid2DFields(parameters.Resolution);
        if (Fields.N != parameters.Resolution) throw new ArgumentException($"поля {Fields.N}², а сим {parameters.Resolution}²");
    }

    /// <summary>Параметры (меняются между шагами; сторона — нет).</summary>
    public Fluid2DParams Params { get; set; }

    public Fluid2DFields Fields { get; }

    /// <summary>Высоты рельефа для течения плёнки (как <see cref="Fluid2DSim.Heights"/>); null — уклона нет.</summary>
    public Fluid2DHeightImage? Heights { get; set; }

    /// <summary>Грид штампов последнего шага со штампами (плотность на тексель, float); штампов не было — null.</summary>
    public float[]? StampGrid => _stampGrid;

    /// <summary>Шаг целиком: [штампы] → Composite → Advection → Divergence → давление (2 прохода, у грубого решателя — 1; при DivergenceMult 0 — без них).</summary>
    public void Step(in Fluid2DStepInput input, ReadOnlySpan<Fluid2DBrushTexel> brush = default, Fluid2DStamps? stamps = null)
    {
        var c = Constants(input, brush);
        if (Stamps(ref c, stamps, input)) StampPass(c, stamps!);
        Composite(c, brush);
        Advection(c);
        if (!Params.HasPressure) return;
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
    public void Stage(Fluid2DStage stage, in Fluid2DStepInput input, ReadOnlySpan<Fluid2DBrushTexel> brush = default, Fluid2DStamps? stamps = null)
    {
        var c = Constants(input, brush);
        switch (stage)
        {
            case Fluid2DStage.Composite:
                if (Stamps(ref c, stamps, input)) StampPass(c, stamps!);
                Composite(c, brush);
                break;
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
        var c = Fluid2DConstants.Build(p, input, hasBrush, amp, density);
        if (Heights is not null) c.SetHeights(p, input, 0, Heights.Cells, Heights.Half);
        return c;
    }

    private bool Stamps(ref Fluid2DConstants c, Fluid2DStamps? stamps, in Fluid2DStepInput input)
    {
        if (stamps is null) return false;
        if (!stamps.Ready || stamps.Step != input.Step) throw new InvalidOperationException($"штампы не готовы к шагу {input.Step}");
        if (stamps.Count == 0) return false;
        c.SetStamps(stamps, 0, default);
        return true;
    }

    // ── штампы ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Грид штампов (как <c>CsStamp</c>): тексель суммирует штампы своей ячейки бининга в порядке записи, с AABB, содержащим тексель.</summary>
    private void StampPass(in Fluid2DConstants c, Fluid2DStamps stamps)
    {
        int n = (int)c.N;
        var grid = new float[n * n];
        var records = stamps.Records;
        var cells = stamps.Cells;
        var entries = stamps.Entries;
        int side = stamps.CellTexels;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) * c.InvN, v = (y + 0.5f) * c.InvN;
                uint word = cells[(y / side) * stamps.CellsPerAxis + x / side];
                var list = entries.Slice((int)(word & 0xFFFF), (int)(word >> 16));
                float sum = 0f;
                foreach (uint e in list)
                {
                    ref readonly var r = ref records[(int)e];
                    if (u < r.Min.X || u > r.Max.X || v < r.Min.Y || v > r.Max.Y) continue;
                    float dx = u - r.Center.X, dy = v - r.Center.Y;
                    float qx = r.M00 * dx + r.M01 * dy;
                    float qy = r.M10 * dx + r.M11 * dy;
                    float value = 0f;
                    if (r.Shape == 0)
                    {
                        float q2 = qx * qx + qy * qy;
                        if (q2 <= 16f) value = MathF.Pow(2f, -0.72134752f * q2);
                    }
                    else if (MathF.Abs(qx) <= 1f && MathF.Abs(qy) <= 1f)
                        value = Fluid2DPainterReference.SampleWrap(_stampShapes[(int)r.Shape - 1], qx * 0.5f + 0.5f, qy * 0.5f + 0.5f).X;
                    sum += r.Peak * value;
                }
                grid[y * n + x] = sum;
            }
        _stampGrid = grid;
    }

    // ── стадии ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void Composite(in Fluid2DConstants c, ReadOnlySpan<Fluid2DBrushTexel> brush)
    {
        int n = (int)c.N;
        var output = new Half[Fields.Composite.Length];
        var ageOut = c.AgeEnabled != 0 ? new float[n * n] : null;
        _rhoOut = (c.FilmFlags & Fluid2DConstants.FilmConservative) != 0 ? new float[n * n] : null;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) CompositeTexel(c, x, y, brush, output, ageOut);
        output.CopyTo(Fields.Composite, 0);
        ageOut?.CopyTo(Fields.AgeComposite, 0);
        _rhoOut?.CopyTo(Fields.DensityComposite, 0);
    }

    private void CompositeTexel(in Fluid2DConstants c, int x, int y, ReadOnlySpan<Fluid2DBrushTexel> brush, Half[] output, float[]? ageOut)
    {
        var adv = Fields.Advected;
        var prs = Fields.Pressure;
        int n = (int)c.N;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        float sx = uvx + c.Wod.X, sy = uvy + c.Wod.Y;
        int hx = c.ShiftX, hy = c.ShiftY;
        float d = c.SimSpeed * c.InvN;

        var a = Bilinear4(adv, n, sx, sy, hx, hy);
        bool conservative = (c.FilmFlags & Fluid2DConstants.FilmConservative) != 0;
        if (conservative) a = a with { Z = BilinearF(Fields.DensityAdvected, n, sx, sy, hx, hy) };
        float px1 = BilinearP(prs, n, sx + d, sy, hx, hy).P, px0 = BilinearP(prs, n, sx - d, sy, hx, hy).P;
        float py1 = BilinearP(prs, n, sx, sy + d, hx, hy).P, py0 = BilinearP(prs, n, sx, sy - d, hx, hy).P;
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
        bool unbounded = (c.FilmFlags & Fluid2DConstants.FilmUnbounded) != 0;
        float rho = BoundDensity(a.Z - MathF.Min(paint.Z, c.Eraser) + ((1f - c.Eraser) * paint.Z + 0.2f * bitmap.Z) * dn, unbounded)
            * (0.5f * (c.DensityAccumulation * MathF.Max(ep, 0.7f)) + 0.5f) * ep;
        if (c.StampCells != 0) rho = rho + _stampGrid![y * n + x] * ep;
        rho = MathF.Max(rho, c.DensityFloor);
        if ((c.FilmFlags & Fluid2DConstants.FilmFlow) != 0) (vnx, vny) = FilmVelocity(c, adv, n, x, y, hx, hy, uvx, uvy, vnx, vny, rho);
        if (conservative)
        {
            vnx = Math.Clamp(vnx, -c.FilmClamp, c.FilmClamp);
            vny = Math.Clamp(vny, -c.FilmClamp, c.FilmClamp);
            _rhoOut![y * n + x] = rho;
        }

        int wx = Nearest(sx, n) + hx, wy = Nearest(sy, n) + hy;
        float wetPrev = wx >= 0 && wx < n && wy >= 0 && wy < n ? (float)adv[(wy * n + wx) * 4 + 3] : 0f;
        float wet = (0.982f * wetPrev + c.WetFeed * rho) * EdgeMask(m, c.WetInvWidth);
        Store4(output, y * n + x, vnx, vny, rho, wet);

        if (ageOut is not null)
        {
            float ageMass = conservative ? BilinearAgeMassF(Fields.DensityAdvected, Fields.AgeAdvected, n, sx, sy, hx, hy)
                : BilinearAgeMass(adv, Fields.AgeAdvected, n, sx, sy, hx, hy);
            float ageIn = a.Z > 0f ? ageMass / a.Z : 0f;
            float rhoOld = BoundDensity(a.Z, unbounded) * (0.5f * (c.DensityAccumulation * MathF.Max(ep, 0.7f)) + 0.5f) * ep;
            float fresh = rho > rhoOld ? rhoOld / rho : 1f;
            ageOut[y * n + x] = rho > 0f ? (ageIn + c.AgeStep) * fresh : 0f;
        }
    }

    private void Advection(in Fluid2DConstants c)
    {
        int n = (int)c.N;
        var output = new Half[Fields.Advected.Length];
        var ageOut = c.AgeEnabled != 0 ? new float[n * n] : null;
        _rhoOut = (c.FilmFlags & Fluid2DConstants.FilmConservative) != 0 ? new float[n * n] : null;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) AdvectionTexel(c, x, y, output, ageOut);
        output.CopyTo(Fields.Advected, 0);
        ageOut?.CopyTo(Fields.AgeAdvected, 0);
        _rhoOut?.CopyTo(Fields.DensityAdvected, 0);
    }

    private void AdvectionTexel(in Fluid2DConstants c, int x, int y, Half[] output, float[]? ageOut)
    {
        var comp = Fields.Composite;
        int n = (int)c.N;
        float uvx = (x + 0.5f) * c.InvN, uvy = (y + 0.5f) * c.InvN;
        int i = (y * n + x) * 4;
        float hx = (float)comp[i], hy = (float)comp[i + 1], hw = (float)comp[i + 3];
        float step = c.SimSpeed * c.InvN;
        float bx = uvx - hx * step, by = uvy - hy * step;
        var s = Bilinear4(comp, n, bx, by);
        if ((c.FilmFlags & Fluid2DConstants.FilmConservative) != 0)
        {
            AdvectConservative(c, x, y, s, output, ageOut);
            return;
        }
        Store4(output, y * n + x, s.X * c.VelocityFeedback, s.Y * c.VelocityFeedback, s.Z * c.VelocityFeedback, hw);
        if (ageOut is not null) ageOut[y * n + x] = s.Z > 0f ? BilinearAgeMass(comp, Fields.AgeComposite, n, bx, by, 0, 0) / s.Z : 0f;
    }

    /// <summary>Консервативная адвекция (как <c>AdvectConservative</c>): ρ — потоки через грани, возраст — потоки × возраст донора.</summary>
    private void AdvectConservative(in Fluid2DConstants c, int x, int y, in Vec4 s, Half[] output, float[]? ageOut)
    {
        var comp = Fields.Composite;
        int n = (int)c.N;
        var rc = Fields.DensityComposite;
        var here = Texel4(comp, n, x, y) with { Z = rc[y * n + x] };
        Vec4 l = Texel4(comp, n, x - 1, y) with { Z = rc[y * n + Math.Clamp(x - 1, 0, n - 1)] };
        Vec4 r = Texel4(comp, n, x + 1, y) with { Z = rc[y * n + Math.Clamp(x + 1, 0, n - 1)] };
        Vec4 u = Texel4(comp, n, x, y - 1) with { Z = rc[Math.Clamp(y - 1, 0, n - 1) * n + x] };
        Vec4 d = Texel4(comp, n, x, y + 1) with { Z = rc[Math.Clamp(y + 1, 0, n - 1) * n + x] };
        float fxm = x > 0 ? FaceFlux(l.Z, l.X, here.Z, here.X) : 0f;
        float fxp = x + 1 < n ? FaceFlux(here.Z, here.X, r.Z, r.X) : 0f;
        float fym = y > 0 ? FaceFlux(u.Z, u.Y, here.Z, here.Y) : 0f;
        float fyp = y + 1 < n ? FaceFlux(here.Z, here.Y, d.Z, d.Y) : 0f;
        float rho = MathF.Max(here.Z - ((fxp - fxm) + (fyp - fym)) * c.SimSpeed, 0f);
        int i = y * n + x;
        Store4(output, i, s.X * c.VelocityFeedback, s.Y * c.VelocityFeedback, rho, here.W);
        _rhoOut![i] = rho;
        if (ageOut is null) return;
        var age = Fields.AgeComposite;
        float ah = age[i];
        float axm = x > 0 ? FaceAge(fxm, l.X, here.X, age[i - 1], ah) : 0f;
        float axp = x + 1 < n ? FaceAge(fxp, here.X, r.X, ah, age[i + 1]) : 0f;
        float aym = y > 0 ? FaceAge(fym, u.Y, here.Y, age[i - n], ah) : 0f;
        float ayp = y + 1 < n ? FaceAge(fyp, here.Y, d.Y, ah, age[i + n]) : 0f;
        float mass = here.Z * ah - ((axp - axm) + (ayp - aym)) * c.SimSpeed;
        ageOut[i] = rho > 0f ? MathF.Max(mass / rho, 0f) : 0f;
    }

    /// <summary>Поток через грань L|R донорской схемой (как <c>FaceFlux</c>): оба текселя грани считают одно число.</summary>
    internal static float FaceFlux(float rhoL, float uL, float rhoR, float uR)
    {
        float uf = 0.5f * (uL + uR);
        return uf > 0f ? rhoL * uf : rhoR * uf;
    }

    private static float FaceAge(float flux, float uL, float uR, float ageL, float ageR) => flux * (0.5f * (uL + uR) > 0f ? ageL : ageR);

    // ── плёнка ──────────────────────────────────────────────────────────────────────────────────────────────────

    private float NeighbourRho(in Fluid2DConstants c, Half[] adv, int n, int x, int y, int sx, int sy) =>
        (c.FilmFlags & Fluid2DConstants.FilmConservative) == 0 ? Texel4(adv, n, x, y, sx, sy).Z : TexelF(Fields.DensityAdvected, n, x, y, sx, sy);

    /// <summary>Тексель float-канала через сдвиг окна, вне грида — 0 (как <c>TexelFS</c>).</summary>
    private static float TexelF(float[] g, int n, int x, int y, int sx, int sy)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n) return 0f;
        return g[py * n + px];
    }

    private static float BilinearF(float[] g, int n, float u, float v, int sx, int sy)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        float a = TexelF(g, n, x, y, sx, sy), b = TexelF(g, n, x + 1, y, sx, sy);
        float c = TexelF(g, n, x, y + 1, sx, sy), d = TexelF(g, n, x + 1, y + 1, sx, sy);
        float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
        return ab + (cd - ab) * ty;
    }

    private static float AgeMassF(float[] rho, float[] age, int n, int x, int y, int sx, int sy)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n) return 0f;
        int i = py * n + px;
        return rho[i] * age[i];
    }

    private static float BilinearAgeMassF(float[] rho, float[] age, int n, float u, float v, int sx, int sy)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        float a = AgeMassF(rho, age, n, x, y, sx, sy), b = AgeMassF(rho, age, n, x + 1, y, sx, sy);
        float c = AgeMassF(rho, age, n, x, y + 1, sx, sy), d = AgeMassF(rho, age, n, x + 1, y + 1, sx, sy);
        float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
        return ab + (cd - ab) * ty;
    }

    private static float BoundDensity(float rho, bool unbounded) => unbounded ? MathF.Max(rho, 0f) : Saturate(rho);

    /// <summary>Скорость плёнки (как <c>FilmVelocity</c>): h_min, уклон ∇(рельеф + max(h − H, 0)), трение v/(1 + k·dt/h²).</summary>
    private (float X, float Y) FilmVelocity(in Fluid2DConstants c, Half[] adv, int n, int x, int y, int sx, int sy, float u, float v, float vx, float vy,
        float rho)
    {
        float h = rho * c.FilmHeight;
        if (h < c.FilmMinThickness) return (0f, 0f);
        float el = MathF.Max(NeighbourRho(c, adv, n, x - 1, y, sx, sy) * c.FilmHeight - c.FilmPuddle, 0f);
        float er = MathF.Max(NeighbourRho(c, adv, n, x + 1, y, sx, sy) * c.FilmHeight - c.FilmPuddle, 0f);
        float eu = MathF.Max(NeighbourRho(c, adv, n, x, y - 1, sx, sy) * c.FilmHeight - c.FilmPuddle, 0f);
        float ed = MathF.Max(NeighbourRho(c, adv, n, x, y + 1, sx, sy) * c.FilmHeight - c.FilmPuddle, 0f);
        float gx = (er - el) * c.FilmInvTwoDx, gy = (ed - eu) * c.FilmInvTwoDy;
        if ((c.FilmFlags & Fluid2DConstants.FilmHeights) != 0)
        {
            var (tx, tz) = TerrainGradient(c, Heights!, u, v);
            gx = gx + tx;
            gy = gy + tz;
        }
        float ax = vx - c.FilmAccelX * gx, ay = vy - c.FilmAccelY * gy;
        float k = 1f + c.FilmFrictionDt / (h * h);
        return (ax / k, ay / k);
    }

    /// <summary>Градиент билинейного рельефа в текселе (как <c>TerrainGradient</c>).</summary>
    internal static (float X, float Z) TerrainGradient(in Fluid2DConstants c, Fluid2DHeightImage heights, float u, float v)
    {
        var hs = heights.Values;
        int cells = (int)c.HeightCells, nodes = cells + 1;
        float gx = c.HeightX0 + u * c.HeightScaleU, gz = c.HeightZ0 + v * c.HeightScaleV;
        float fx = Math.Clamp(MathF.Floor(gx), 0f, cells - 1), fz = Math.Clamp(MathF.Floor(gz), 0f, cells - 1);
        float tx = Math.Clamp(gx - fx, 0f, 1f), tz = Math.Clamp(gz - fz, 0f, 1f);
        int i = (int)fx, j = (int)fz;
        float h00 = hs[j * nodes + i], h10 = hs[j * nodes + i + 1], h01 = hs[(j + 1) * nodes + i], h11 = hs[(j + 1) * nodes + i + 1];
        float dx0 = h10 - h00, dx1 = h11 - h01, dz0 = h01 - h00, dz1 = h11 - h10;
        return ((dx0 + (dx1 - dx0) * tz) * c.HeightInvStep, (dz0 + (dz1 - dz0) * tx) * c.HeightInvStep);
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
        float p0 = BilinearP(Fields.Pressure, n, uvx + c.Wod.X, uvy + c.Wod.Y, c.ShiftX, c.ShiftY).P;
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

    /// <summary>Тексель грида прошлого шага через сдвиг окна (sx, sy): (зажатый x, y) + сдвиг, вне грида — 0 (как <c>Texel4S</c>).</summary>
    private static Vec4 Texel4(Half[] g, int n, int x, int y, int sx, int sy)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n) return default;
        int i = (py * n + px) * 4;
        return new Vec4((float)g[i], (float)g[i + 1], (float)g[i + 2], (float)g[i + 3]);
    }

    private static Vec4 Bilinear4(Half[] g, int n, float u, float v, int sx, int sy)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var ab = Lerp(Texel4(g, n, x, y, sx, sy), Texel4(g, n, x + 1, y, sx, sy), tx);
        var cd = Lerp(Texel4(g, n, x, y + 1, sx, sy), Texel4(g, n, x + 1, y + 1, sx, sy), tx);
        return Lerp(ab, cd, ty);
    }

    private static (float P, float D) TexelP(Half[] g, int n, int x, int y, int sx, int sy)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n) return (0f, 0f);
        int i = (py * n + px) * 2;
        return ((float)g[i], (float)g[i + 1]);
    }

    private static (float P, float D) BilinearP(Half[] g, int n, float u, float v, int sx, int sy)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        var ab = LerpP(TexelP(g, n, x, y, sx, sy), TexelP(g, n, x + 1, y, sx, sy), tx);
        var cd = LerpP(TexelP(g, n, x, y + 1, sx, sy), TexelP(g, n, x + 1, y + 1, sx, sy), tx);
        return LerpP(ab, cd, ty);
    }

    /// <summary>ρ·возраст текселя (ρ — канал z грида) через сдвиг окна, вне грида — 0 (как <c>AgeMassS</c>).</summary>
    private static float AgeMass(Half[] g, float[] age, int n, int x, int y, int sx, int sy)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n) return 0f;
        int i = py * n + px;
        return (float)g[i * 4 + 2] * age[i];
    }

    private static float BilinearAgeMass(Half[] g, float[] age, int n, float u, float v, int sx, int sy)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        float a = AgeMass(g, age, n, x, y, sx, sy), b = AgeMass(g, age, n, x + 1, y, sx, sy);
        float c = AgeMass(g, age, n, x, y + 1, sx, sy), d = AgeMass(g, age, n, x + 1, y + 1, sx, sy);
        float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
        return ab + (cd - ab) * ty;
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

    /// <summary>Тексель кисти прошлого шага через сдвиг окна (sx, sy), вне грида — 0 (как <c>BrushTexelS</c>).</summary>
    private static void BrushTexel(ReadOnlySpan<Fluid2DBrushTexel> g, int n, int x, int y, int sx, int sy, out Vec4 lo, out Vec4 hi)
    {
        int px = Math.Clamp(x, 0, n - 1) + sx, py = Math.Clamp(y, 0, n - 1) + sy;
        if (px < 0 || px >= n || py < 0 || py >= n)
        {
            lo = hi = default;
            return;
        }
        ref readonly var b = ref g[py * n + px];
        lo = new Vec4((float)b.VelocityX, (float)b.VelocityY, (float)b.Density, (float)b.Capture);
        hi = new Vec4((float)b.BitmapVelocityX, (float)b.BitmapVelocityY, (float)b.BitmapDensity, (float)b.Unused);
    }

    internal static void BrushBilinear(ReadOnlySpan<Fluid2DBrushTexel> g, int n, float u, float v, int sx, int sy, out Vec4 lo, out Vec4 hi)
    {
        float px = u * n - 0.5f, py = v * n - 0.5f;
        float fx = MathF.Floor(px), fy = MathF.Floor(py);
        float tx = px - fx, ty = py - fy;
        int x = (int)fx, y = (int)fy;
        BrushTexel(g, n, x, y, sx, sy, out var a0, out var a1);
        BrushTexel(g, n, x + 1, y, sx, sy, out var b0, out var b1);
        BrushTexel(g, n, x, y + 1, sx, sy, out var c0, out var c1);
        BrushTexel(g, n, x + 1, y + 1, sx, sy, out var d0, out var d1);
        lo = Lerp(Lerp(a0, b0, tx), Lerp(c0, d0, tx), ty);
        hi = Lerp(Lerp(a1, b1, tx), Lerp(c1, d1, tx), ty);
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
