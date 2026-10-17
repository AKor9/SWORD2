using System.Numerics;

namespace Sword.Game.Client.Scene;

/// <summary>
/// Шары каскадов теней солнца. Дальний — видимая часть земли (y = 0): углы и центр кадра, лучи выше горизонта — до
/// <see cref="MaxDistance"/> от глаза; центр — среднее точек, радиус — до дальней точки с запасом под высоту бойцов. Ближний —
/// вокруг точки земли в центре кадра, радиус — доля расстояния до неё (<see cref="NearShare"/>): чем ближе камера, тем мельче тексель.
/// </summary>
public static class ShadowFocus
{
    /// <summary>Дальше этого от глаза тень не нужна (туман), м.</summary>
    public const float MaxDistance = 70f;

    /// <summary>Запас радиуса: тени бойцов и колонн у края кадра, м.</summary>
    public const float Margin = 3f;

    /// <summary>Радиус ближнего каскада — такая доля расстояния от глаза до точки взгляда.</summary>
    public const float NearShare = 0.35f;

    public const float MinRadius = 4f, MaxNearRadius = 20f, MaxRadius = 50f;

    public readonly record struct Spheres(Vector3 NearCenter, float NearRadius, Vector3 FarCenter, float FarRadius);

    public static Spheres Of(in Matrix4x4 viewProj)
    {
        if (!Matrix4x4.Invert(viewProj, out var inv)) return new(Vector3.Zero, MaxNearRadius, Vector3.Zero, MaxRadius);
        Span<Vector3> points = stackalloc Vector3[5];
        ReadOnlySpan<float> xs = [-1, 1, -1, 1, 0], ys = [-1, -1, 1, 1, 0];
        var sum = Vector3.Zero;
        var eye = Vector3.Zero;
        for (int i = 0; i < 5; i++)
        {
            points[i] = Ground(inv, xs[i], ys[i], out eye);
            sum += points[i];
        }
        var center = sum / 5;
        float radius = 0;
        foreach (var p in points) radius = MathF.Max(radius, Vector3.Distance(p, center));
        var look = points[4];
        float near = Math.Clamp(NearShare * Vector3.Distance(eye, look), MinRadius, MaxNearRadius);
        return new(look, near, center, Math.Clamp(radius + Margin, MinRadius, MaxRadius));
    }

    /// <summary>
    /// Точка земли на луче через точку кадра (NDC); луч не падает на землю ближе <see cref="MaxDistance"/> — точка на этом
    /// расстоянии, опущенная на землю. <paramref name="eye"/> — начало луча (глаз).
    /// </summary>
    private static Vector3 Ground(in Matrix4x4 inv, float x, float y, out Vector3 eye)
    {
        var a = Vector4.Transform(new Vector4(x, y, 1f, 1f), inv);
        var b = Vector4.Transform(new Vector4(x, y, 0.01f, 1f), inv);
        eye = new Vector3(a.X, a.Y, a.Z) / a.W;
        var dir = Vector3.Normalize(new Vector3(b.X, b.Y, b.Z) / b.W - eye);
        float t = dir.Y < -1e-4f ? MathF.Min(-eye.Y / dir.Y, MaxDistance) : MaxDistance;
        var p = eye + dir * t;
        return new Vector3(p.X, 0, p.Z);
    }
}
