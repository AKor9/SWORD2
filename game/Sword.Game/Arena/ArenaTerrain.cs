using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Game.Arena;

/// <summary>
/// Рельеф арены — аналитическая функция высоты (перенос <c>Gameplay/Terrain.cs</c> SWORD 2eab13b: формулы, константы
/// и порядок сложения те же). Центр арены ровный, вокруг — четыре тестовых участка для локомоторики:
///   холмы (16, 0) · ступенчатая пирамида (−16, 0), ступени 1 м × 12 см ·
///   крутой холм до ~17° (0, 18) · мелкие ухабы (0, −18).
///
/// Функция — описание уровня; рендеру рельеф отдаёт <see cref="BuildField"/>: поле высот в узлах сетки отрисовки SWORD.
/// Копии функции в шейдере, как в SWORD (<c>sim_common.hlsli</c>), нет. Физике (земля трупов, 7е) — <see cref="ArenaGround"/>:
/// гладкая высота без ступеней с аналитическим градиентом (<see cref="SmoothHeightAt(float, float, out Vector2)"/>) и ярусы
/// пирамиды точными призмами (<see cref="TierHalf"/>, <see cref="TierTop"/>).
/// </summary>
public static class ArenaTerrain
{
    /// <summary>Сетка: ячеек на сторону и полуразмер области, м. Шаг 0,25 м — кратен ступеням.</summary>
    public const int Cells = 256;
    public const float Half = 32f;

    public static readonly (Vector2 Centre, string Name)[] Zones =
    [
        (new(16, 0), "холмы"),
        (new(0, -18), "ухабы"),
        (new(-16, 0), "ступени"),
        (new(0, 18), "склон"),
    ];

    /// <summary>Поле высот арены: функция <see cref="Height"/> в узлах сетки <see cref="Cells"/>×<see cref="Cells"/>.</summary>
    public static HeightField BuildField() => HeightField.FromFunction(Cells, Half, Height);

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>
    /// То же сглаживание (значение — бит в бит <see cref="Smooth(float, float, float)"/>) и его производная по
    /// <paramref name="x"/>: <c>6t(1 − t)/(b − a)</c> в переходе, 0 вне его.
    /// </summary>
    private static float Smooth(float a, float b, float x, out float slope)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        slope = t > 0 && t < 1 ? 6 * t * (1 - t) / (b - a) : 0;
        return t * t * (3 - 2 * t);
    }

    public static float Height(float x, float z)
    {
        float h = 0;
        h += Hills(x, z);
        h += Steps(x, z);
        h += Slope(x, z);
        h += Bumps(x, z);
        return h;
    }

    private static float Hills(float x, float z)
    {
        float d1 = MathF.Sqrt((x - 16) * (x - 16) + z * z);
        return Smooth(9, 6, d1) * (0.25f + 0.25f * MathF.Sin(x * 1.3f) * MathF.Cos(z * 1.1f));
    }

    /// <summary>Ступенчатая пирамида: ярус k (1…5) — квадрат max(|x+16|, |z|) ≤ 7 − k высотой k · 12 см.</summary>
    private static float Steps(float x, float z) => Math.Clamp(MathF.Floor(StepOuter - StepRadius(x, z)), 0, StepLevels) * StepRise;

    private static float Slope(float x, float z)
    {
        float d3 = MathF.Sqrt(x * x + (z - 18) * (z - 18));
        return 1.6f * Smooth(9, 1, d3);
    }

    private static float Bumps(float x, float z)
    {
        float d4 = MathF.Sqrt(x * x + (z + 18) * (z + 18));
        return Smooth(8, 6, d4) * 0.045f * (MathF.Sin(x * 3.1f + z * 1.7f) + MathF.Sin(z * 2.9f - x * 1.3f) + 2);
    }

    /// <summary>
    /// Ступенчатая пирамида: центр (<see cref="StepCentreX"/>, 0), ярусов <see cref="StepLevels"/>; ярус k (1…5) — квадрат
    /// <c>max(|x − StepCentreX|, |z|) ≤ </c><see cref="TierHalf"/>(k) с верхом на <see cref="TierTop"/>(k) — те же числа, что
    /// у <see cref="Steps"/> (<c>floor(StepOuter − R) ≥ k ⇔ R ≤ StepOuter − k</c>).
    /// </summary>
    internal const float StepCentreX = -16, StepOuter = 7, StepRise = 0.12f;

    internal const int StepLevels = 5;

    /// <summary>Полуширина яруса <paramref name="tier"/> (1…<see cref="StepLevels"/>) по X и Z, м.</summary>
    internal static float TierHalf(int tier) => StepOuter - tier;

    /// <summary>Высота верха яруса <paramref name="tier"/> (1…<see cref="StepLevels"/>), м — бит в бит <see cref="Height"/> на проступи.</summary>
    internal static float TierTop(int tier) => tier * StepRise;

    private static float StepRadius(float x, float z) => MathF.Max(MathF.Abs(x - StepCentreX), MathF.Abs(z));

    /// <summary>Высота без ступеней — непрерывная, с гладкой нормалью.</summary>
    public static float SmoothHeightAt(float x, float z) => Hills(x, z) + Slope(x, z) + Bumps(x, z);

    /// <summary>
    /// Высота без ступеней (бит в бит <see cref="SmoothHeightAt(float, float)"/>) и её градиент
    /// <c>(∂h/∂x, ∂h/∂z)</c> — аналитически, по тем же формулам (7е, земля физики: касательная плоскость и нормаль
    /// <c>normalize(−∂h/∂x, 1, −∂h/∂z)</c>). Вне участков — 0 без синусов: слагаемое, у которого сглаживание 0, не считается.
    /// </summary>
    public static float SmoothHeightAt(float x, float z, out Vector2 gradient)
    {
        gradient = default;
        float hills = Hills(x, z, ref gradient);
        float slope = Slope(x, z, ref gradient);
        float bumps = Bumps(x, z, ref gradient);
        return hills + slope + bumps;
    }

    // Слагаемые с градиентом: значение — те же выражения в том же порядке, что у Hills/Slope/Bumps (бит в бит); при
    // сглаживании 0 значение — +0 (второй множитель не отрицателен), градиент не меняется. Производная расстояния до центра
    // участка — только в переходе сглаживания (там расстояние > 0). 7з-2 (земля физики — на каждом торце каждого подшага): дальше
    // края участка по квадрату расстояния — сразу 0, без корня (корень монотонен: квадрат ≥ края² ⇒ расстояние ≥ края — сглаживание 0).

    private static float Hills(float x, float z, ref Vector2 gradient)
    {
        float q = (x - 16) * (x - 16) + z * z;
        if (q >= 9 * 9) return 0;
        float d1 = MathF.Sqrt(q);
        float s = Smooth(9, 6, d1, out float ds);
        if (s == 0) return 0;
        float sx = MathF.Sin(x * 1.3f), cz = MathF.Cos(z * 1.1f);
        float g = 0.25f + 0.25f * sx * cz;
        float k = ds != 0 ? ds / d1 : 0;
        gradient.X += k * (x - 16) * g + s * (0.25f * 1.3f) * MathF.Cos(x * 1.3f) * cz;
        gradient.Y += k * z * g - s * (0.25f * 1.1f) * sx * MathF.Sin(z * 1.1f);
        return s * g;
    }

    private static float Slope(float x, float z, ref Vector2 gradient)
    {
        float q = x * x + (z - 18) * (z - 18);
        if (q >= 9 * 9) return 0;
        float d3 = MathF.Sqrt(q);
        float s = Smooth(9, 1, d3, out float ds);
        if (s == 0) return 0;
        if (ds != 0)
        {
            float k = 1.6f * ds / d3;
            gradient.X += k * x;
            gradient.Y += k * (z - 18);
        }
        return 1.6f * s;
    }

    private static float Bumps(float x, float z, ref Vector2 gradient)
    {
        float q = x * x + (z + 18) * (z + 18);
        if (q >= 8 * 8) return 0;
        float d4 = MathF.Sqrt(q);
        float s = Smooth(8, 6, d4, out float ds);
        if (s == 0) return 0;
        float a = x * 3.1f + z * 1.7f, b = z * 2.9f - x * 1.3f;
        float wave = MathF.Sin(a) + MathF.Sin(b) + 2;
        float w = s * 0.045f;
        float ca = MathF.Cos(a), cb = MathF.Cos(b);
        float k = ds != 0 ? ds / d4 * 0.045f * wave : 0;
        gradient.X += k * x + w * (3.1f * ca - 1.3f * cb);
        gradient.Y += k * (z + 18) + w * (1.7f * ca + 2.9f * cb);
        return w * wave;
    }

    /// <summary>
    /// Нормаль по конечным разностям высоты (<c>Terrain.Normal</c> SWORD 2eab13b без изменений); на ребре ступени резко
    /// наклоняется в полосе ±5 см. Нужна форме тела при постройке (ступня по склону, 5б).
    /// </summary>
    public static Vector3 Normal(float x, float z)
    {
        const float e = 0.05f;
        float dx = Height(x + e, z) - Height(x - e, z);
        float dz = Height(x, z + e) - Height(x, z - e);
        return Vector3.Normalize(new Vector3(-dx / (2 * e), 1, -dz / (2 * e)));
    }

    /// <summary>
    /// Зона рельефа точки для метрик — по границам составляющих высоты, а не по ближайшему центру: индекс в
    /// <see cref="Zones"/> внутри зоны (холмы и склон — ближе 8.5 м к центру, ухабы — 7.5 м, ярусы — max(|x+16|,|z|) &lt; 6.5,
    /// кромка первого яруса в зоне); −1 — ровно (дальше 0.5 м от границ всех зон, все составляющие нулевые);
    /// −2 — переходная полоса у границы, в метрики не идёт.
    /// </summary>
    public static int ZoneAt(float x, float z)
    {
        const float band = 0.5f;
        float hills = 8.5f - MathF.Sqrt((x - 16) * (x - 16) + z * z);
        float bumps = 7.5f - MathF.Sqrt(x * x + (z + 18) * (z + 18));
        float steps = StepOuter - 0.5f - StepRadius(x, z);
        float slope = 8.5f - MathF.Sqrt(x * x + (z - 18) * (z - 18));
        if (hills > 0) return 0;
        if (bumps > 0) return 1;
        if (steps > 0) return 2;
        if (slope > 0) return 3;
        return MathF.Max(MathF.Max(hills, bumps), MathF.Max(steps, slope)) > -band ? -2 : -1;
    }
}
