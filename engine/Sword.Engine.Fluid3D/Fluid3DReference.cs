namespace Sword.Engine.Fluid3D;

/// <summary>
/// CPU-эталон шага 3D-газа: те же выражения float и тот же порядок операций, что <c>fluid3d.hlsl</c>, на полях <see cref="Fluid3DFields"/>
/// (half-биты между стадиями — как на GPU). Для тестов на малых сетках (16³–32³): каждая стадия GPU сверяется с эталоном до округления half.
/// Окно — как у <see cref="Fluid3DSim"/>: первый шаг ставит окно у цели, дальше окно едет целыми ячейками.
/// </summary>
public sealed class Fluid3DReference
{
    private const float HalfMax = 65504f;
    private Fluid3DFields _f;

    public Fluid3DReference(Fluid3DParams p, Fluid3DFields? fields = null, Fluid3DWindow? window = null)
    {
        p.Validate();
        Params = p;
        _f = fields ?? new Fluid3DFields(p.Nx, p.Ny, p.Nz);
        if (_f.Nx != p.Nx || _f.Ny != p.Ny || _f.Nz != p.Nz) throw new ArgumentException("размеры полей и параметров различаются", nameof(fields));
        Window = window;
    }

    public Fluid3DParams Params { get; set; }

    public Fluid3DFields Fields => _f;

    /// <summary>Окно после последнего шага (или заданное); null — ещё не шагали.</summary>
    public Fluid3DWindow? Window { get; private set; }

    /// <summary>Карта источника на плоскости (клетки подряд с номера <c>PlaneOffset</c> чисел шага); null — карты нет.</summary>
    public Fluid3DMapTexel[]? Map { get; set; }

    /// <summary>Числа шага к цели <paramref name="target"/> (окно не меняется — <see cref="Advance"/>).</summary>
    internal Fluid3DConstants Constants(ulong step, System.Numerics.Vector3 target, ReadOnlySpan<Fluid3DSource> sources, out Fluid3DWindow next,
        Fluid3DPlane? plane = null, int mapOffset = 0)
    {
        next = Fluid3DWindow.Around(target, Params);
        var shift = Window is { } w ? next.ShiftFrom(w) : (0, 0, 0);
        return Fluid3DConstants.Build(Params, next, shift, step, sources, plane, mapOffset);
    }

    internal void Advance(Fluid3DWindow next) => Window = next;

    /// <summary>
    /// Шаг целиком (порядок стадий — как у <see cref="Fluid3DSim.Step"/>); <paramref name="plane"/> — источник-карта (клетки — <see cref="Map"/>
    /// с номера <paramref name="mapOffset"/>).
    /// </summary>
    public void Step(ulong step, System.Numerics.Vector3 target, ReadOnlySpan<Fluid3DSource> sources, Fluid3DPlane? plane = null, int mapOffset = 0)
    {
        var k = Constants(step, target, sources, out var next, plane, mapOffset);
        Stage(Fluid3DStage.Sources, k);
        Stage(Fluid3DStage.Curl, k);
        Stage(Fluid3DStage.Forces, k);
        Stage(Fluid3DStage.AdvectVelocity, k);
        Stage(Fluid3DStage.Divergence, k);
        for (int i = 0; i < Params.PressureIterations; i++) Stage((i & 1) == 0 ? Fluid3DStage.PressureRed : Fluid3DStage.PressureBlack, k);
        Stage(Fluid3DStage.Project, k);
        if (Params.ScalarAdvection == Fluid3DAdvection.MacCormack) Stage(Fluid3DStage.AdvectForward, k);
        Stage(Fluid3DStage.AdvectScalars, k);
        Window = next;
    }

    /// <summary>Одна стадия с готовыми числами шага (<see cref="Constants"/>); окно не меняется.</summary>
    internal void Stage(Fluid3DStage stage, in Fluid3DConstants k)
    {
        switch (stage)
        {
            case Fluid3DStage.Sources: Sources(k); break;
            case Fluid3DStage.Curl: ForCells(k, Curl); break;
            case Fluid3DStage.Forces: ForCells(k, Forces); break;
            case Fluid3DStage.AdvectVelocity: ForCells(k, AdvectVelocity); break;
            case Fluid3DStage.Divergence: ForCells(k, Divergence); break;
            case Fluid3DStage.PressureRed: Pressure(k, 0); break;
            case Fluid3DStage.PressureBlack: Pressure(k, 1); break;
            case Fluid3DStage.Project: ForCells(k, Project); break;
            case Fluid3DStage.AdvectForward: ForCells(k, AdvectForward); break;
            case Fluid3DStage.AdvectScalars:
                if (Params.ScalarAdvection == Fluid3DAdvection.MacCormack) ForCells(k, AdvectMacCormack);
                else ForCells(k, AdvectScalars);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(stage), stage, null);
        }
    }

    private delegate void CellOp(in Fluid3DConstants k, int x, int y, int z);

    private static void ForCells(in Fluid3DConstants k, CellOp op)
    {
        for (int z = 0; z < k.Nz; z++)
            for (int y = 0; y < k.Ny; y++)
                for (int x = 0; x < k.Nx; x++) op(k, x, y, z);
    }

    // ── выборка ──────────────────────────────────────────────────────────────────────────────────────────────────

    private readonly struct F3(float x, float y, float z)
    {
        public readonly float X = x, Y = y, Z = z;
    }

    private static int Index(in Fluid3DConstants k, int x, int y, int z) => x + (int)k.Nx * (y + (int)k.Ny * z);

    private static bool Inside(in Fluid3DConstants k, int x, int y, int z) =>
        x >= 0 && y >= 0 && z >= 0 && x < k.Nx && y < k.Ny && z < k.Nz;

    private static F3 Vel(Half[] v, in Fluid3DConstants k, int x, int y, int z)
    {
        x = Math.Clamp(x, 0, (int)k.Nx - 1);
        y = Math.Clamp(y, 0, (int)k.Ny - 1);
        z = Math.Clamp(z, 0, (int)k.Nz - 1);
        int i = 4 * Index(k, x, y, z);
        return new F3((float)v[i], (float)v[i + 1], (float)v[i + 2]);
    }

    private static (float A, float B) Scal(Half[] s, in Fluid3DConstants k, int x, int y, int z)
    {
        if (!Inside(k, x, y, z)) return (0, 0);
        int i = 2 * Index(k, x, y, z);
        return ((float)s[i], (float)s[i + 1]);
    }

    private static float Pres(Half[] a, in Fluid3DConstants k, int x, int y, int z) => Inside(k, x, y, z) ? (float)a[2 * Index(k, x, y, z)] : 0f;

    private static float CurlAt(Half[] a, in Fluid3DConstants k, int x, int y, int z)
    {
        x = Math.Clamp(x, 0, (int)k.Nx - 1);
        y = Math.Clamp(y, 0, (int)k.Ny - 1);
        z = Math.Clamp(z, 0, (int)k.Nz - 1);
        return (float)a[2 * Index(k, x, y, z) + 1];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static F3 Lerp(F3 a, F3 b, float t) => new(Lerp(a.X, b.X, t), Lerp(a.Y, b.Y, t), Lerp(a.Z, b.Z, t));

    private static (float A, float B) Lerp((float A, float B) a, (float A, float B) b, float t) => (Lerp(a.A, b.A, t), Lerp(a.B, b.B, t));

    private static float Dot(F3 a, F3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static F3 SampleVel(Half[] v, in Fluid3DConstants k, float qx, float qy, float qz)
    {
        float fx = MathF.Floor(qx), fy = MathF.Floor(qy), fz = MathF.Floor(qz);
        float tx = qx - fx, ty = qy - fy, tz = qz - fz;
        int x = (int)fx, y = (int)fy, z = (int)fz;
        var x00 = Lerp(Vel(v, k, x, y, z), Vel(v, k, x + 1, y, z), tx);
        var x10 = Lerp(Vel(v, k, x, y + 1, z), Vel(v, k, x + 1, y + 1, z), tx);
        var x01 = Lerp(Vel(v, k, x, y, z + 1), Vel(v, k, x + 1, y, z + 1), tx);
        var x11 = Lerp(Vel(v, k, x, y + 1, z + 1), Vel(v, k, x + 1, y + 1, z + 1), tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private static (float A, float B) SampleScal(Half[] s, in Fluid3DConstants k, float qx, float qy, float qz)
    {
        float fx = MathF.Floor(qx), fy = MathF.Floor(qy), fz = MathF.Floor(qz);
        float tx = qx - fx, ty = qy - fy, tz = qz - fz;
        int x = (int)fx, y = (int)fy, z = (int)fz;
        var x00 = Lerp(Scal(s, k, x, y, z), Scal(s, k, x + 1, y, z), tx);
        var x10 = Lerp(Scal(s, k, x, y + 1, z), Scal(s, k, x + 1, y + 1, z), tx);
        var x01 = Lerp(Scal(s, k, x, y, z + 1), Scal(s, k, x + 1, y, z + 1), tx);
        var x11 = Lerp(Scal(s, k, x, y + 1, z + 1), Scal(s, k, x + 1, y + 1, z + 1), tx);
        return Lerp(Lerp(x00, x10, ty), Lerp(x01, x11, ty), tz);
    }

    private static void CornerRange(Half[] s, in Fluid3DConstants k, float qx, float qy, float qz, out (float A, float B) lo, out (float A, float B) hi)
    {
        int x = (int)MathF.Floor(qx), y = (int)MathF.Floor(qy), z = (int)MathF.Floor(qz);
        lo = Scal(s, k, x, y, z);
        hi = lo;
        for (int i = 1; i < 8; i++)
        {
            var c = Scal(s, k, x + (i & 1), y + ((i >> 1) & 1), z + (i >> 2));
            lo = (MathF.Min(lo.A, c.A), MathF.Min(lo.B, c.B));
            hi = (MathF.Max(hi.A, c.A), MathF.Max(hi.B, c.B));
        }
    }

    private static (float A, float B) Decay((float A, float B) s, in Fluid3DConstants k) =>
        (MathF.Min(MathF.Max(s.A * k.DensityKeep - k.DensityLoss, 0), HalfMax), MathF.Min(MathF.Max(s.B * k.TemperatureKeep - k.TemperatureLoss, 0), HalfMax));

    private static void Put(Half[] v, int i, F3 x)
    {
        v[4 * i] = (Half)x.X;
        v[4 * i + 1] = (Half)x.Y;
        v[4 * i + 2] = (Half)x.Z;
        v[4 * i + 3] = default;
    }

    private static void Put(Half[] s, int i, (float A, float B) x)
    {
        s[2 * i] = (Half)x.A;
        s[2 * i + 1] = (Half)x.B;
    }

    private static F3 CurlOf(F3 xm, F3 xp, F3 ym, F3 yp, F3 zm, F3 zp, float halfInvDx) => new(
        ((yp.Z - ym.Z) - (zp.Y - zm.Y)) * halfInvDx, ((zp.X - zm.X) - (xp.Z - xm.Z)) * halfInvDx, ((xp.Y - xm.Y) - (yp.X - ym.X)) * halfInvDx);

    // ── стадии ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void Sources(in Fluid3DConstants k)
    {
        var f = _f;
        if (k.PlaneSide > 0 && (Map is null || Map.Length < k.PlaneOffset + k.PlaneSide * k.PlaneSide))
            throw new InvalidOperationException($"источник-карта {k.PlaneSide}² с клетки {k.PlaneOffset}: карты {Map?.Length ?? 0} клеток");
        for (int z = 0; z < k.Nz; z++)
            for (int y = 0; y < k.Ny; y++)
                for (int x = 0; x < k.Nx; x++) SourceCell(k, f, Map, x, y, z);
        f.SwapAux();
    }

    /// <summary>Пишет V₁, S₁ и второй буфер Aux (<see cref="Fluid3DFields.AuxPrevious"/> до смены ролей).</summary>
    private static void SourceCell(in Fluid3DConstants k, Fluid3DFields f, Fluid3DMapTexel[]? map, int x, int y, int z)
    {
        int i = Index(k, x, y, z);
        int fx = x + k.ShiftX, fy = y + k.ShiftY, fz = z + k.ShiftZ;
        Half vx = default, vy = default, vz = default, rho = default, t = default, p = default;
        if (Inside(k, fx, fy, fz))
        {
            int j = Index(k, fx, fy, fz);
            vx = f.Velocity[4 * j];
            vy = f.Velocity[4 * j + 1];
            vz = f.Velocity[4 * j + 2];
            rho = f.Scalars[2 * j];
            t = f.Scalars[2 * j + 1];
            if (k.WarmStart != 0) p = f.Aux[2 * j];
        }
        if ((k.Sources > 0 || k.PlaneSide > 0) && x >= k.SourceMinX && y >= k.SourceMinY && z >= k.SourceMinZ && x <= k.SourceMaxX && y <= k.SourceMaxY &&
            z <= k.SourceMaxZ)
        {
            float velX = (float)vx, velY = (float)vy, velZ = (float)vz, a = (float)rho, b = (float)t;
            bool touched = false;
            for (int n = 0; n < k.Sources; n++)
            {
                var src = k.Source[n];
                float dx = x - src.Tail.X, dy = y - src.Tail.Y, dz = z - src.Tail.Z;
                float along = Saturate((dx * src.Axis.X + dy * src.Axis.Y + dz * src.Axis.Z) * src.Axis.W);
                float ex = dx - src.Axis.X * along, ey = dy - src.Axis.Y * along, ez = dz - src.Axis.Z * along;
                float hx = dx - src.Axis.X, hy = dy - src.Axis.Y, hz = dz - src.Axis.Z;
                float wTail = Saturate((src.Tail.W - MathF.Sqrt(dx * dx + dy * dy + dz * dz)) * src.Rates.W);
                float wSegment = Saturate((src.Tail.W - MathF.Sqrt(ex * ex + ey * ey + ez * ez)) * src.Rates.W);
                float wHead = Saturate((src.Tail.W - MathF.Sqrt(hx * hx + hy * hy + hz * hz)) * src.Rates.W);
                float w = MathF.Max(wHead, wSegment - wTail);
                if (w > 0)
                {
                    touched = true;
                    a += src.Rates.X * w;
                    b += src.Rates.Y * w;
                    float blend = src.Rates.Z * w;
                    velX += (src.Velocity.X - velX) * blend;
                    velY += (src.Velocity.Y - velY) * blend;
                    velZ += (src.Velocity.Z - velZ) * blend;
                }
            }
            if (k.PlaneSide > 0 && PlaneCell(k, map!, x, y, z, ref a, ref b, ref velX, ref velY, ref velZ)) touched = true;
            if (touched)
            {
                vx = (Half)velX;
                vy = (Half)velY;
                vz = (Half)velZ;
                rho = (Half)MathF.Min(MathF.Max(a, 0), HalfMax);
                t = (Half)MathF.Min(MathF.Max(b, 0), HalfMax);
            }
        }
        f.VelocityScratch[4 * i] = vx;
        f.VelocityScratch[4 * i + 1] = vy;
        f.VelocityScratch[4 * i + 2] = vz;
        f.VelocityScratch[4 * i + 3] = default;
        f.ScalarScratch[2 * i] = rho;
        f.ScalarScratch[2 * i + 1] = t;
        f.AuxPrevious[2 * i] = p;
        f.AuxPrevious[2 * i + 1] = default;
    }

    private void Curl(in Fluid3DConstants k, int x, int y, int z)
    {
        var v = _f.VelocityScratch;
        var w = CurlOf(Vel(v, k, x - 1, y, z), Vel(v, k, x + 1, y, z), Vel(v, k, x, y - 1, z), Vel(v, k, x, y + 1, z), Vel(v, k, x, y, z - 1),
            Vel(v, k, x, y, z + 1), k.HalfInvDx);
        _f.Aux[2 * Index(k, x, y, z) + 1] = (Half)MathF.Sqrt(Dot(w, w));
    }

    private void Forces(in Fluid3DConstants k, int x, int y, int z)
    {
        var v1 = _f.VelocityScratch;
        var aux = _f.Aux;
        int i = Index(k, x, y, z);
        var v = new F3((float)v1[4 * i], (float)v1[4 * i + 1], (float)v1[4 * i + 2]);
        float rho = (float)_f.ScalarScratch[2 * i], t = (float)_f.ScalarScratch[2 * i + 1];
        float b = k.Alpha * rho - k.Beta * t;
        float fx = b * k.GravityX, fy = b * k.GravityY, fz = b * k.GravityZ;
        if (k.Vorticity != 0)
        {
            var w = CurlOf(Vel(v1, k, x - 1, y, z), Vel(v1, k, x + 1, y, z), Vel(v1, k, x, y - 1, z), Vel(v1, k, x, y + 1, z), Vel(v1, k, x, y, z - 1),
                Vel(v1, k, x, y, z + 1), k.HalfInvDx);
            var g = new F3((CurlAt(aux, k, x + 1, y, z) - CurlAt(aux, k, x - 1, y, z)) * k.HalfInvDx,
                (CurlAt(aux, k, x, y + 1, z) - CurlAt(aux, k, x, y - 1, z)) * k.HalfInvDx,
                (CurlAt(aux, k, x, y, z + 1) - CurlAt(aux, k, x, y, z - 1)) * k.HalfInvDx);
            float len = MathF.Sqrt(Dot(g, g));
            if (len > 1e-5f && Dot(v, v) > 1e-10f)
            {
                var n = new F3(g.X / len, g.Y / len, g.Z / len);
                fx += (n.Y * w.Z - n.Z * w.Y) * k.Vorticity;
                fy += (n.Z * w.X - n.X * w.Z) * k.Vorticity;
                fz += (n.X * w.Y - n.Y * w.X) * k.Vorticity;
            }
        }
        if (k.NoiseAmp != 0)
        {
            float m = k.NoiseInvDensity > 0 ? MathF.Min(rho * k.NoiseInvDensity, 1f) : 1f;
            if (m > 0)
            {
                var (cx, cy, cz) = NoiseForce(k, x, y, z);
                float a = k.NoiseAmp * m;
                fx += cx * a;
                fy += cy * a;
                fz += cz * a;
            }
        }
        Put(_f.Velocity, i, new F3(v.X + fx * k.Dt, v.Y + fy * k.Dt, v.Z + fz * k.Dt));
    }

    /// <summary>curl ψ вихревого шума в ячейке (x, y, z) окна шага <paramref name="k"/> — без силы и маски (единицы решётки шума).</summary>
    internal static (float X, float Y, float Z) NoiseForce(in Fluid3DConstants k, int x, int y, int z) =>
        Fluid3DCurlNoise.Curl(x * k.NoiseStep + k.NoiseX, y * k.NoiseStep + k.NoiseY, z * k.NoiseStep + k.NoiseZ);

    /// <summary>Источник-карта в ячейке (как <c>CsSources</c>): вес слоя, билинейная выборка карты в проекции ячейки; true — что-то впрыснуто.</summary>
    private static bool PlaneCell(in Fluid3DConstants k, Fluid3DMapTexel[] map, int x, int y, int z, ref float a, ref float b, ref float velX,
        ref float velY, ref float velZ)
    {
        float dx = x - k.PlaneX, dy = y - k.PlaneY, dz = z - k.PlaneZ;
        float dn = MathF.Abs(dx * k.PlaneNx + dy * k.PlaneNy + dz * k.PlaneNz);
        float ws = Saturate((k.PlaneThick - dn) * k.PlaneInvEdge);
        if (!(ws > 0)) return false;
        float mu = (dx * k.PlaneUx + dy * k.PlaneUy + dz * k.PlaneUz) + k.PlaneHalf;
        float mv = (dx * k.PlaneVx + dy * k.PlaneVy + dz * k.PlaneVz) + k.PlaneHalf;
        float fu = MathF.Floor(mu), fv = MathF.Floor(mv);
        float tu = mu - fu, tv = mv - fv;
        int i = (int)fu, j = (int)fv;
        var y0 = LerpMap(MapAt(k, map, i, j), MapAt(k, map, i + 1, j), tu);
        var y1 = LerpMap(MapAt(k, map, i, j + 1), MapAt(k, map, i + 1, j + 1), tu);
        var m = LerpMap(y0, y1, tv);
        if (!(m.R > 0 || m.T > 0 || m.Blend > 0)) return false;
        a += m.R * ws;
        b += m.T * ws;
        float blend = m.Blend * ws;
        velX += (m.Vx - velX) * blend;
        velY += (m.Vy - velY) * blend;
        velZ += (m.Vz - velZ) * blend;
        return true;
    }

    private readonly record struct MapValue(float R, float T, float Blend, float Vx, float Vy, float Vz);

    private static MapValue MapAt(in Fluid3DConstants k, Fluid3DMapTexel[] map, int i, int j)
    {
        int n = (int)k.PlaneSide;
        if (i < 0 || j < 0 || i >= n || j >= n) return default;
        var t = map[k.PlaneOffset + (uint)(i + n * j)];
        var v = t.Velocity;
        return new MapValue(t.Density, t.Temperature, t.BlendValue, v.X, v.Y, v.Z);
    }

    private static MapValue LerpMap(MapValue a, MapValue b, float t) => new(a.R + (b.R - a.R) * t, a.T + (b.T - a.T) * t,
        a.Blend + (b.Blend - a.Blend) * t, a.Vx + (b.Vx - a.Vx) * t, a.Vy + (b.Vy - a.Vy) * t, a.Vz + (b.Vz - a.Vz) * t);

    private void AdvectVelocity(in Fluid3DConstants k, int x, int y, int z)
    {
        var v0 = _f.Velocity;
        int i = Index(k, x, y, z);
        float qx = x - ((float)v0[4 * i] * k.BackStep + k.WindX), qy = y - ((float)v0[4 * i + 1] * k.BackStep + k.WindY);
        float qz = z - ((float)v0[4 * i + 2] * k.BackStep + k.WindZ);
        Put(_f.VelocityScratch, i, SampleVel(v0, k, qx, qy, qz));
    }

    private void Divergence(in Fluid3DConstants k, int x, int y, int z)
    {
        var v = _f.VelocityScratch;
        F3 xm = Vel(v, k, x - 1, y, z), xp = Vel(v, k, x + 1, y, z), ym = Vel(v, k, x, y - 1, z), yp = Vel(v, k, x, y + 1, z);
        F3 zm = Vel(v, k, x, y, z - 1), zp = Vel(v, k, x, y, z + 1);
        float div = ((xp.X - xm.X) + (yp.Y - ym.Y) + (zp.Z - zm.Z)) * k.HalfInvDx;
        int i = Index(k, x, y, z);
        if (k.WarmStart == 0) _f.Aux[2 * i] = default;
        _f.Aux[2 * i + 1] = (Half)div;
    }

    private void Pressure(in Fluid3DConstants k, int parity)
    {
        var a = _f.Aux;
        for (int z = 0; z < k.Nz; z++)
            for (int y = 0; y < k.Ny; y++)
                for (int x = (y + z + parity) & 1; x < k.Nx; x += 2) PressureCell(k, a, x, y, z);
    }

    private static void PressureCell(in Fluid3DConstants k, Half[] a, int x, int y, int z)
    {
        int i = Index(k, x, y, z);
        float p = (float)a[2 * i], div = (float)a[2 * i + 1];
        float sum = Pres(a, k, x - 1, y, z) + Pres(a, k, x + 1, y, z) + Pres(a, k, x, y - 1, z) + Pres(a, k, x, y + 1, z) + Pres(a, k, x, y, z - 1) +
            Pres(a, k, x, y, z + 1);
        a[2 * i] = (Half)(k.SorKeep * p + (sum - div * k.PressureRhs) * k.SorWeight6);
    }

    private void Project(in Fluid3DConstants k, int x, int y, int z)
    {
        var a = _f.Aux;
        var v1 = _f.VelocityScratch;
        int i = Index(k, x, y, z);
        float gx = (Pres(a, k, x + 1, y, z) - Pres(a, k, x - 1, y, z)) * k.GradScale;
        float gy = (Pres(a, k, x, y + 1, z) - Pres(a, k, x, y - 1, z)) * k.GradScale;
        float gz = (Pres(a, k, x, y, z + 1) - Pres(a, k, x, y, z - 1)) * k.GradScale;
        var v = new F3((float)v1[4 * i] - gx, (float)v1[4 * i + 1] - gy, (float)v1[4 * i + 2] - gz);
        float m2 = Dot(v, v);
        if (m2 > k.MaxSpeed2)
        {
            float s = k.MaxSpeed / MathF.Sqrt(m2);
            v = new F3(v.X * s, v.Y * s, v.Z * s);
        }
        Put(_f.Velocity, i, v);
    }

    private (float X, float Y, float Z, int I) Back(in Fluid3DConstants k, int x, int y, int z, float sign)
    {
        var v0 = _f.Velocity;
        int i = Index(k, x, y, z);
        float bx = (float)v0[4 * i] * k.BackStep + k.WindX, by = (float)v0[4 * i + 1] * k.BackStep + k.WindY;
        float bz = (float)v0[4 * i + 2] * k.BackStep + k.WindZ;
        return sign < 0 ? (x - bx, y - by, z - bz, i) : (x + bx, y + by, z + bz, i);
    }

    private void AdvectScalars(in Fluid3DConstants k, int x, int y, int z)
    {
        var (qx, qy, qz, i) = Back(k, x, y, z, -1);
        Put(_f.Scalars, i, Decay(SampleScal(_f.ScalarScratch, k, qx, qy, qz), k));
    }

    private void AdvectForward(in Fluid3DConstants k, int x, int y, int z)
    {
        var (qx, qy, qz, i) = Back(k, x, y, z, -1);
        Put(_f.ScalarForward, i, SampleScal(_f.ScalarScratch, k, qx, qy, qz));
    }

    private void AdvectMacCormack(in Fluid3DConstants k, int x, int y, int z)
    {
        var (qx, qy, qz, i) = Back(k, x, y, z, -1);
        var (bx, by, bz, _) = Back(k, x, y, z, 1);
        var s1 = _f.ScalarScratch;
        var s2 = _f.ScalarForward;
        float fa = (float)s2[2 * i], fb = (float)s2[2 * i + 1];
        var back = SampleScal(s2, k, bx, by, bz);
        float ra = fa + ((float)s1[2 * i] - back.A) * 0.5f, rb = fb + ((float)s1[2 * i + 1] - back.B) * 0.5f;
        CornerRange(s1, k, qx, qy, qz, out var lo, out var hi);
        if (ra < lo.A || ra > hi.A) ra = fa;
        if (rb < lo.B || rb > hi.B) rb = fb;
        Put(_f.Scalars, i, Decay((ra, rb), k));
    }
}
