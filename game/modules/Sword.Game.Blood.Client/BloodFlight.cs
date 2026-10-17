using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Полёт капли с линейным сопротивлением (dv/dt = g − k·v): замкнутая формула x(t) = x0 + v∞t + (v0 − v∞)(1 − e^(−kt))/k, v∞ = g/k —
/// та же в HLSL (<c>Shaders/blood/blood_flight.hlsli</c>, <c>BloodFlightPosition</c>). Приземление — на CPU по рельефу <see cref="ITerrainView"/>:
/// первый тик, на котором капля не выше рельефа, затем точка — делением отрезка этого тика пополам.
/// </summary>
public static class BloodFlight
{
    public const string ShaderInclude = "blood/blood_flight.hlsli";

    /// <summary>Ускорение свободного падения, м/с².</summary>
    public const float Gravity = 9.81f;

    /// <summary>Сопротивление не меньше, 1/с (формула делит на k).</summary>
    public const float MinDrag = 1e-3f;

    public static Vector3 Position(Vector3 x0, Vector3 v0, float k, float t)
    {
        k = MathF.Max(k, MinDrag);
        var vInf = new Vector3(0, -Gravity / k, 0);
        float e = (1 - MathF.Exp(-k * t)) / k;
        return x0 + vInf * t + (v0 - vInf) * e;
    }

    public static Vector3 Velocity(Vector3 v0, float k, float t)
    {
        k = MathF.Max(k, MinDrag);
        var vInf = new Vector3(0, -Gravity / k, 0);
        return vInf + (v0 - vInf) * MathF.Exp(-k * t);
    }

    /// <summary>
    /// Приземление: <paramref name="ticks"/> — тиков после отрыва до первого тика, на котором капля уже на рельефе (0 — отрыв под рельефом),
    /// <paramref name="t"/> — время касания, с, <paramref name="point"/> — точка касания (y — высота рельефа). Не коснулась за
    /// <paramref name="maxTicks"/> — false.
    /// </summary>
    public static bool Land(ITerrainView? terrain, Vector3 x0, Vector3 v0, float k, float tickDt, int maxTicks, out int ticks, out float t,
        out Vector3 point)
    {
        if (Down(terrain, x0, v0, k, tickDt, 0))
        {
            ticks = 0;
            t = 0;
            point = new Vector3(x0.X, Ground(terrain, x0.X, x0.Z), x0.Z);
            return true;
        }
        for (int j = 1; j <= maxTicks; j++)
        {
            if (!Down(terrain, x0, v0, k, tickDt, j)) continue;
            ticks = j;
            Touch(terrain, x0, v0, k, tickDt, j, out t, out point);
            return true;
        }
        ticks = 0;
        t = 0;
        point = default;
        return false;
    }

    /// <summary>Капля на тике <paramref name="j"/> после отрыва уже не выше рельефа (j = 0 — точка отрыва).</summary>
    public static bool Down(ITerrainView? terrain, Vector3 x0, Vector3 v0, float k, float tickDt, int j) =>
        Above(terrain, j == 0 ? x0 : Position(x0, v0, k, j * tickDt)) <= 0;

    /// <summary>
    /// Касание внутри тика <paramref name="j"/> ≥ 1 (на j − 1 капля выше рельефа, на j — нет): время — делением отрезка тика пополам, точка
    /// (y — высота рельефа).
    /// </summary>
    public static void Touch(ITerrainView? terrain, Vector3 x0, Vector3 v0, float k, float tickDt, int j, out float t, out Vector3 point)
    {
        float a = (j - 1) * tickDt, b = j * tickDt;
        for (int i = 0; i < 40; i++)
        {
            float m = 0.5f * (a + b);
            if (Above(terrain, Position(x0, v0, k, m)) > 0) a = m;
            else b = m;
        }
        t = b;
        var p = Position(x0, v0, k, b);
        point = new Vector3(p.X, Ground(terrain, p.X, p.Z), p.Z);
    }

    private static float Ground(ITerrainView? terrain, float x, float z) => terrain?.Height(x, z) ?? 0;

    private static float Above(ITerrainView? terrain, Vector3 p) => p.Y - Ground(terrain, p.X, p.Z);
}
