using System.Numerics;
using Sword.Engine.Geometry;

namespace Sword.Game.Arena;

/// <summary>
/// Рельеф арены — аналитическая функция высоты (перенос <c>Gameplay/Terrain.cs</c> SWORD 2eab13b: формулы, константы
/// и порядок сложения те же). Центр арены ровный, вокруг — четыре тестовых участка для локомоторики:
///   холмы (16, 0) · ступенчатая пирамида (−16, 0), ступени 1 м × 12 см ·
///   крутой холм до ~17° (0, 18) · мелкие ухабы (0, −18).
///
/// Функция — описание уровня; всем остальным (рендер, позже физика и IK) рельеф отдаёт <see cref="BuildField"/>:
/// поле высот в узлах сетки отрисовки SWORD. Копии функции в шейдере, как в SWORD (<c>sim_common.hlsli</c>), нет.
/// Контакты со ступенями (ящики-ярусы) переносятся вместе с физикой.
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

    private const float StepCentreX = -16, StepOuter = 7, StepRise = 0.12f;
    private const int StepLevels = 5;

    private static float StepRadius(float x, float z) => MathF.Max(MathF.Abs(x - StepCentreX), MathF.Abs(z));

    /// <summary>Высота без ступеней — непрерывная, с гладкой нормалью.</summary>
    public static float SmoothHeightAt(float x, float z) => Hills(x, z) + Slope(x, z) + Bumps(x, z);

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
