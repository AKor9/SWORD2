using System.Numerics;
using Sword.Game.Contracts;

namespace Sword.Game.Blood;

/// <summary>
/// Полёт капли с линейным сопротивлением (dv/dt = g − k·v): замкнутая формула x(t) = x0 + v∞t + (v0 − v∞)(1 − e^(−kt))/k, v∞ = g/k (считается без вычитания близких чисел) —
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

    /// <summary>x(t) = x0 + v0·t·φ(kt) + g·t²·ψ(kt) — та же формула без вычитания близких чисел (v∞·t против v∞·(1 − e^(−kt))/k при малом k).</summary>
    public static Vector3 Position(Vector3 x0, Vector3 v0, float k, float t)
    {
        k = MathF.Max(k, MinDrag);
        Terms(k * t, out float phi, out float psi, out _);
        return x0 + v0 * (t * phi) + new Vector3(0, -Gravity * (t * t * psi), 0);
    }

    /// <summary>v(t) = v0·e^(−kt) + g·t·φ(kt).</summary>
    public static Vector3 Velocity(Vector3 v0, float k, float t)
    {
        k = MathF.Max(k, MinDrag);
        Terms(k * t, out float phi, out _, out float decay);
        return v0 * decay + new Vector3(0, -Gravity * (t * phi), 0);
    }

    /// <summary>Скорость вылета, с которой капля через <paramref name="t"/> с окажется в <paramref name="to"/> от точки вылета.</summary>
    public static Vector3 Launch(Vector3 to, float k, float t)
    {
        k = MathF.Max(k, MinDrag);
        Terms(k * t, out float phi, out float psi, out _);
        return (to - new Vector3(0, -Gravity * (t * t * psi), 0)) / (t * phi);
    }

    /// <summary>φ(z) = (1 − e^(−z))/z, ψ(z) = (z − 1 + e^(−z))/z², e^(−z); при z &lt; 0.1 — ряды Тейлора (HLSL <c>BloodFlightTerms</c>).</summary>
    private static void Terms(float z, out float phi, out float psi, out float decay)
    {
        decay = MathF.Exp(-z);
        if (z < 0.1f)
        {
            phi = 1 - z * (0.5f - z * (1f / 6 - z * (1f / 24)));
            psi = 0.5f - z * (1f / 6 - z * (1f / 24 - z * (1f / 120)));
        }
        else
        {
            phi = (1 - decay) / z;
            psi = (z - 1 + decay) / (z * z);
        }
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
