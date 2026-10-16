using System.Numerics;

namespace Sword.Engine.Geometry;

/// <summary>Отрезки: ближайшие точки двух отрезков (проходы клинка против капсул тела).</summary>
public static class Segments
{
    /// <summary>
    /// Ближайшие точки отрезков p1–q1 и p2–q2 (Ericson, «Real-Time Collision Detection», 5.1.9), пороги вырожденности 1e-6;
    /// возвращает квадрат расстояния. Параллельные — первый с начала (s = 0). Вырожденный второй отрезок тоже даёт s = 0,
    /// а не ближайшую точку: в игре второй отрезок — ось капсулы тела, не вырожденная.
    /// </summary>
    public static float SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float c = Vector3.Dot(d1, r), b = Vector3.Dot(d1, d2);
        float den = a * e - b * b;
        float s = den > 1e-6f ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
        float t = (b * s + f) / MathF.Max(e, 1e-6f);
        if (t < 0)
        {
            t = 0;
            s = Math.Clamp(-c / MathF.Max(a, 1e-6f), 0, 1);
        }
        else if (t > 1)
        {
            t = 1;
            s = Math.Clamp((b - c) / MathF.Max(a, 1e-6f), 0, 1);
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
        return Vector3.DistanceSquared(c1, c2);
    }
}
