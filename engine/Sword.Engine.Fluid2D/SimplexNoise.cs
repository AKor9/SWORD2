namespace Sword.Engine.Fluid2D;

/// <summary>
/// Шум скорости сима — 3D-симплекс с двумя независимыми наборами градиентов (компоненты x, y), как направленный шум L2 (FSR §3.4):
/// решётка тетраэдров по открытому описанию (Gustavson, «Simplex noise demystified»; McEwan и др., 2011 — поиск углов через сравнение
/// координат), вклад угла — (1 − 2|f|²)³·1024/375 · (g·f), градиент — (±1, ±1, ±1) из старших битов хеша PCG3D (Jarzynski, Olano, 2020)
/// координат угла ×6, завёрнутых с периодом 300 (50 ячеек решётки). Та же формула — <c>Simplex2</c> в <c>fluid2d.hlsl</c>; отличие от
/// <c>JacobianSimplex_ALU</c> UE — только порядок операций float (сумма скоса одним умножением), якобиан не считается.
/// </summary>
public static class SimplexNoise
{
    private const float Scale = 1024f / 375f;

    /// <summary>Две компоненты шума в точке <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>; каждая в [−1, 1].</summary>
    public static (float X, float Y) Sample(float x, float y, float z)
    {
        float skew = (x + y + z) * (1f / 3f);
        float cx = MathF.Floor(x + skew), cy = MathF.Floor(y + skew), cz = MathF.Floor(z + skew);
        float unskew = (cx + cy + cz) * (1f / 6f);
        float bx = cx - unskew, by = cy - unskew, bz = cz - unskew;
        float fx = x - bx, fy = y - by, fz = z - bz;

        // Порядок компонент f внутри ячейки: g — «x ≥ y», «y ≥ z», «z ≥ x»; второй и третий углы — min и max с 1 − g.zxy.
        float gx = fx >= fy ? 1 : 0, gy = fy >= fz ? 1 : 0, gz = fz >= fx ? 1 : 0;
        float lx = 1 - gz, ly = 1 - gx, lz = 1 - gy;
        float ax = MathF.Min(gx, lx) - 1f / 6f, ay = MathF.Min(gy, ly) - 1f / 6f, az = MathF.Min(gz, lz) - 1f / 6f;
        float qx = MathF.Max(gx, lx) - 1f / 3f, qy = MathF.Max(gy, ly) - 1f / 3f, qz = MathF.Max(gz, lz) - 1f / 3f;

        float nx = 0, ny = 0;
        Corner(x, y, z, bx, by, bz, ref nx, ref ny);
        Corner(x, y, z, bx + ax, by + ay, bz + az, ref nx, ref ny);
        Corner(x, y, z, bx + qx, by + qy, bz + qz, ref nx, ref ny);
        Corner(x, y, z, bx + 0.5f, by + 0.5f, bz + 0.5f, ref nx, ref ny);
        return (nx, ny);
    }

    private static void Corner(float x, float y, float z, float tx, float ty, float tz, ref float nx, ref float ny)
    {
        float fx = x - tx, fy = y - ty, fz = z - tz;
        float s = Math.Clamp(2 * (fx * fx + fy * fy + fz * fz), 0f, 1f);
        float r = 1 - s;
        float w = r * r * r * Scale;
        var (hx, hy) = Hash(Wrap(tx), Wrap(ty), Wrap(tz));
        nx += w * Dot(hx, fx, fy, fz);
        ny += w * Dot(hy, fx, fy, fz);
    }

    /// <summary>Координата угла (кратна 1/6) → целое 6·t по модулю 300.</summary>
    private static int Wrap(float t)
    {
        float v = (6 * t + 0.5f) * (1f / 300f);
        return (int)MathF.Floor((v - MathF.Floor(v)) * 300f);
    }

    /// <summary>Градиент (±1, ±1, ±1) из битов 15, 14, 13 шестнадцатибитного хеша.</summary>
    private static float Dot(uint h, float fx, float fy, float fz) =>
        ((h & 0x8000) != 0 ? fx : -fx) + ((h & 0x4000) != 0 ? fy : -fy) + ((h & 0x2000) != 0 ? fz : -fz);

    /// <summary>PCG3D: две первые компоненты, старшие 16 бит.</summary>
    internal static (uint X, uint Y) Hash(int ix, int iy, int iz)
    {
        uint x = unchecked((uint)ix * 1664525u + 1013904223u);
        uint y = unchecked((uint)iy * 1664525u + 1013904223u);
        uint z = unchecked((uint)iz * 1664525u + 1013904223u);
        unchecked
        {
            x += y * z;
            y += z * x;
            z += x * y;
            x += y * z;
            y += z * x;
        }
        return (x >> 16, y >> 16);
    }
}
