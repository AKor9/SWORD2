namespace Sword.Engine.Fluid3D;

/// <summary>
/// Вихревой шум газа (CPU-сторона <c>GasCurlNoise</c> в <c>fluid3d.hlsl</c>, те же выражения float и тот же порядок): ротор векторного
/// потенциала ψ = три градиентных шума Перлина (одна решётка, три градиента на узел), производные аналитические (квинтика), так что поле
/// бездивергентно по построению. Решётка периодична с периодом <see cref="Period"/> узлов: координаты шума, сведённые по модулю периода на CPU
/// в double, не теряют точности ни на краю мира, ни через часы игры.
/// </summary>
public static class Fluid3DCurlNoise
{
    /// <summary>Период решётки, узлов.</summary>
    public const int Period = 256;

    /// <summary>
    /// Множитель ротора: среднеквадратичная |curl ψ| в координатах шума ≈ 1.729 = 1/<see cref="Normalization"/> (замер по 200 000 точкам, тест
    /// <c>CurlNoise_RmsIsAmplitude</c>). Сила A·Normalization·curl ψ в среднем по объёму — A м/с².
    /// </summary>
    public const float Normalization = 0.578f;

    // 12 рёбер куба и 4 повтора (градиенты Перлина): компоненты — 0 и ±1, скалярное произведение точное.
    private static readonly float[] Grad =
    [
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0,
        1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1,
        0, 1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1,
        1, 1, 0, 0, -1, 1, -1, 1, 0, 0, -1, -1,
    ];

    /// <summary>Хеш узла решётки (pcg3d Jarzynski и Olano по координатам по модулю периода) — три слова: градиенты ψx, ψy, ψz.</summary>
    public static (uint X, uint Y, uint Z) Hash(int x, int y, int z)
    {
        uint a = (uint)x & (Period - 1), b = (uint)y & (Period - 1), c = (uint)z & (Period - 1);
        a = a * 1664525u + 1013904223u;
        b = b * 1664525u + 1013904223u;
        c = c * 1664525u + 1013904223u;
        a += b * c;
        b += c * a;
        c += a * b;
        a ^= a >> 16;
        b ^= b >> 16;
        c ^= c >> 16;
        a += b * c;
        b += c * a;
        c += a * b;
        return (a, b, c);
    }

    /// <summary>curl ψ в точке шума (qx, qy, qz) — в единицах решётки (производные по координатам шума).</summary>
    public static (float X, float Y, float Z) Curl(float qx, float qy, float qz)
    {
        float fx = MathF.Floor(qx), fy = MathF.Floor(qy), fz = MathF.Floor(qz);
        float tx = qx - fx, ty = qy - fy, tz = qz - fz;
        int cx = (int)fx, cy = (int)fy, cz = (int)fz;
        float ux = Fade(tx), uy = Fade(ty), uz = Fade(tz);
        float dux = FadeDerivative(tx), duy = FadeDerivative(ty), duz = FadeDerivative(tz);
        // Градиенты ψx, ψy, ψz.
        float ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0, gx = 0, gy = 0, gz = 0;
        for (int n = 0; n < 8; n++)
        {
            int ox = n & 1, oy = (n >> 1) & 1, oz = n >> 2;
            var h = Hash(cx + ox, cy + oy, cz + oz);
            float dx = tx - ox, dy = ty - oy, dz = tz - oz;
            float wx = ox != 0 ? ux : 1 - ux, wy = oy != 0 ? uy : 1 - uy, wz = oz != 0 ? uz : 1 - uz;
            float ex = ox != 0 ? dux : -dux, ey = oy != 0 ? duy : -duy, ez = oz != 0 ? duz : -duz;
            float w = wx * wy * wz;
            float sx = ex * wy * wz, sy = wx * ey * wz, sz = wx * wy * ez;
            Corner(h.X >> 28, dx, dy, dz, w, sx, sy, sz, ref ax, ref ay, ref az);
            Corner(h.Y >> 28, dx, dy, dz, w, sx, sy, sz, ref bx, ref by, ref bz);
            Corner(h.Z >> 28, dx, dy, dz, w, sx, sy, sz, ref gx, ref gy, ref gz);
        }
        return (gy - bz, az - gx, bx - ay);
    }

    /// <summary>Вклад узла в градиент одной компоненты: ∇(w·(g·d)) = ∇w·(g·d) + w·g.</summary>
    private static void Corner(uint h, float dx, float dy, float dz, float w, float sx, float sy, float sz, ref float jx, ref float jy, ref float jz)
    {
        int i = 3 * (int)h;
        float g0 = Grad[i], g1 = Grad[i + 1], g2 = Grad[i + 2];
        float v = g0 * dx + g1 * dy + g2 * dz;
        jx = jx + (sx * v + g0 * w);
        jy = jy + (sy * v + g1 * w);
        jz = jz + (sz * v + g2 * w);
    }

    /// <summary>Квинтика 6t⁵ − 15t⁴ + 10t³.</summary>
    public static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>Её производная 30t²(t² − 2t + 1).</summary>
    public static float FadeDerivative(float t) => t * t * 30f * (t * (t - 2f) + 1f);

    /// <summary>Координата шума по модулю периода (double → float; ровно период после округления — 0).</summary>
    public static float Wrap(double q)
    {
        double r = q - Period * Math.Floor(q / Period);
        float f = (float)r;
        return f >= Period ? 0f : f;
    }
}
