using System.Numerics;
using Sword.Engine.Fluid2D;

namespace Sword.Game.Fire;

/// <summary>
/// Числа площадки огня без GPU (FSR §3.6, BR §1.1, §2.2): такт шагов по <c>TickId</c>, центр шишки, размер точки кисти, доля сдвига
/// буферов k, сдвиг площадки за шаг в UV (WOD) и перевод мирового смещения в UV площадки. Площадка — к камере: оси вида (x — вправо,
/// y — вверх, z — к зрителю); v в UV растёт вниз.
/// </summary>
public static class FireArea
{
    /// <summary>Шагов сима в секунду (как L2): при 120 тиках/с — каждый второй тик.</summary>
    public const int StepRate = 60;

    /// <summary>Тиков в секунду по шагу тика (1/120 → 120).</summary>
    public static int TickRate(float tickDt) => Math.Max(1, (int)Math.Round(1.0 / tickDt));

    /// <summary>
    /// Номер шага сима, в котором тик <paramref name="tick"/>: ⌊tick·60/T⌋ (при T ≤ 60 — сам тик). Детерминирован по тикам: при 120 Гц шаги —
    /// на чётных тиках, при 100 Гц — на тиках 0, 2, 4, 5, 7…
    /// </summary>
    public static ulong StepOf(ulong tick, int tickRate) => tickRate <= StepRate ? tick : tick * StepRate / (ulong)tickRate;

    /// <summary>На тике <paramref name="tick"/> начинается шаг сима: тик 0 или номер шага сменился.</summary>
    public static bool IsStep(ulong tick, int tickRate) => tick == 0 || StepOf(tick, tickRate) != StepOf(tick - 1, tickRate);

    /// <summary>Центр шишки: кончик клинка (дальний край шишки) сдвинут к основанию на <paramref name="offset"/> м вдоль клинка.</summary>
    public static Vector3 KnobCenter(Vector3 baseOfBlade, Vector3 tip, float offset)
    {
        var along = tip - baseOfBlade;
        float length = along.Length();
        return length > 1e-6f ? tip - along * (offset / length) : tip;
    }

    /// <summary>Размер точки кисти шишки (BR §2.2): s = 0.07·R·множитель, R — радиус сферы границ, см; σ = 9.496·s см.</summary>
    public static float BrushScale(FireSettings settings) => Fluid2DBrush.PrimitiveScale(settings.HeadRadiusCm, settings.BrushObjectSize);

    /// <summary>
    /// Доля сдвига буферов k (Automatic, Extremes Clamped L2): k = clamp((1 − clamp(2000/E, 0, 0.9))·(E/1000)/max(|Δ|/тексель, 0.001),
    /// kMin, 1), E — большая сторона площадки, см; тексель — E/N. Площадка 3 м, 368²: k = clamp(0.03/|Δтекс|, 0.35, 1) — быстрее
    /// ≈0.086 текселя (0.07 см) за шаг уже kMin: газ едет с огнём на (1 − k) пути.
    /// </summary>
    public static float Follow(Vector3 delta, Vector3 extents, int resolution, float kMin)
    {
        float e = 100f * MathF.Max(extents.X, extents.Y);
        float texel = e / resolution;
        float moved = 100f * delta.Length() / texel;
        float k = (1f - Math.Clamp(2000f / e, 0f, 0.9f)) * (e / 1000f) / MathF.Max(moved, 0.001f);
        return Math.Clamp(k, MathF.Min(kMin, 1f), 1f);
    }

    /// <summary>Мировое смещение <paramref name="world"/> в UV площадки с поворотом <paramref name="rotation"/>: (l.x/E.x, −l.y/E.y), l = R⁻¹·d.</summary>
    public static Vector2 ToUv(Quaternion rotation, Vector3 world, Vector3 extents)
    {
        var l = Vector3.Transform(world, Quaternion.Conjugate(rotation));
        return new Vector2(l.X / extents.X, -l.Y / extents.Y);
    }

    /// <summary>
    /// Сдвиг площадки за шаг в UV (<c>WorldOffsetDelta</c>): k·R⁻¹·Δ/E, только оси x, y вида — движение вдоль взгляда буферы не сдвигает.
    /// Буферы читаются в uv + WOD: при k = 1 газ стоит в мире, при k = 0.35 едет с огнём на 65 % пути.
    /// </summary>
    public static Vector2 OffsetDelta(float k, Quaternion rotation, Vector3 delta, Vector3 extents) => k * ToUv(rotation, delta, extents);

    /// <summary>Поворот площадки из поворота камеры: default (камеры нет) — без поворота.</summary>
    public static Quaternion Plane(Quaternion camera) => camera == default ? Quaternion.Identity : camera;
}
