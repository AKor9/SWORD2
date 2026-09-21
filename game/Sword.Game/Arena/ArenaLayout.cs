using System.Numerics;

namespace Sword.Game.Arena;

/// <summary>
/// Геометрия арены: радиус и колонны (перенос <c>Gameplay/ArenaLayout.cs</c> SWORD 2eab13b). Её читают бой, мир
/// и сцена; в шейдеры она уходит через константы кадра.
/// </summary>
public static class ArenaLayout
{
    public const float ArenaRadius = 62f;
    public const int PillarCount = 8;

    /// <summary>Колонны: xy — центр на полу (x, z), z — радиус, w — высота.</summary>
    public static readonly Vector4[] Pillars = BuildPillars();

    private static Vector4[] BuildPillars()
    {
        var p = new Vector4[PillarCount];
        for (int i = 0; i < p.Length; i++)
        {
            float a = i * MathF.Tau / p.Length + 0.39f;
            float r = i % 2 == 0 ? 30f : 44f;
            p[i] = new Vector4(MathF.Cos(a) * r, MathF.Sin(a) * r, 1.2f, 6.5f);
        }
        return p;
    }
}
