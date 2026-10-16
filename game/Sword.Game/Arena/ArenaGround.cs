using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Sword.Engine.Physics;

namespace Sword.Game.Arena;

/// <summary>
/// Земля арены для контактов физики (README арены, «Земля, стена и колонны арены для физики»): грани с постоянными номерами —
/// <see cref="SmoothFace"/> (0) — гладкий рельеф (касательная плоскость, глубина <c>r − (y − h)·n.Y</c>), 1…<see cref="TierCount"/> —
/// ярусы пирамиды точными призмами без дна. Без состояния, 0 Б на вызов.
/// </summary>
public sealed class ArenaGround : IGround
{
    /// <summary>Единственный экземпляр: состояния нет.</summary>
    public static ArenaGround Instance { get; } = new();

    /// <summary>Грань гладкого рельефа.</summary>
    public const int SmoothFace = 0;

    /// <summary>Ярусов пирамиды — граней 1…<see cref="TierCount"/>: грань k — ярус k.</summary>
    public const int TierCount = ArenaTerrain.StepLevels;

    /// <summary>Граней всего; не больше <see cref="IGround.MaxTouches"/> — все касания одной сферы помещаются.</summary>
    public const int FaceCount = 1 + TierCount;

    /// <summary>Стена физики — окружность радиуса арены вокруг начала координат (центр тела — не дальше радиуса минус радиус тела).</summary>
    public const float WallRadius = ArenaLayout.ArenaRadius;

    /// <summary>Колонны физики: центры и радиусы колонн арены (высота физике не нужна — бесконечные цилиндры).</summary>
    public static ReadOnlySpan<Pillar> Pillars => s_pillars;

    private static readonly Pillar[] s_pillars = BuildPillars();

    /// <summary>
    /// Запас отсева ярусов, м: без него округление отсева и глубины могло бы разойтись на ульп (касания — ровно грани с глубиной &gt; 0).
    /// </summary>
    private const float CullMargin = 1e-3f;

    private ArenaGround()
    {
    }

    /// <summary>
    /// Касания сферы по возрастанию грани: гладкий рельеф, затем ярусы. Ярусы отсеиваются по квадрату пирамиды (ярусы выше
    /// — уже, дальше не смотрим) и по высоте (сфера над ярусом с запасом). Нечисловой центр — ни одного касания.
    /// </summary>
    public int Touches(Vector3 center, float radius, Span<GroundTouch> touches)
    {
        int limit = Math.Min(touches.Length, IGround.MaxTouches);
        if (limit <= 0) return 0;
        int count = 0;
        float depth = SmoothDepth(center, radius, out var normal);
        if (depth > 0) touches[count++] = new GroundTouch(SmoothFace, normal, depth);
        float spread = MathF.Max(MathF.Abs(center.X - ArenaTerrain.StepCentreX), MathF.Abs(center.Z));
        for (int tier = 1; tier <= TierCount && count < limit; tier++)
        {
            if (spread >= ArenaTerrain.TierHalf(tier) + radius + CullMargin) break;
            if (center.Y - radius >= ArenaTerrain.TierTop(tier) + CullMargin) continue;
            depth = TierDepth(center, radius, tier, out normal);
            if (depth > 0) touches[count++] = new GroundTouch(tier, normal, depth);
        }
        return count;
    }

    /// <summary>Глубина сферы в грань <paramref name="face"/> (0…<see cref="TierCount"/>), м, и нормаль грани из земли.</summary>
    public float Depth(Vector3 center, float radius, int face, out Vector3 normal)
    {
        if (face == SmoothFace) return SmoothDepth(center, radius, out normal);
        if ((uint)face > TierCount) ThrowFace(face);
        return TierDepth(center, radius, face, out normal);
    }

    /// <summary>Гладкий рельеф: касательная плоскость в точке под центром, нормаль — по градиенту высоты.</summary>
    private static float SmoothDepth(Vector3 center, float radius, out Vector3 normal)
    {
        float height = ArenaTerrain.SmoothHeightAt(center.X, center.Z, out var gradient);
        // Ровное место (градиент 0 — центр арены, между участками) — нормаль уже единичная, без корня (бит в бит та же)
        normal = gradient.X == 0 && gradient.Y == 0 ? new Vector3(-gradient.X, 1, -gradient.Y) : Vector3.Normalize(new Vector3(-gradient.X, 1, -gradient.Y));
        return radius - (center.Y - height) * normal.Y;
    }

    /// <summary>
    /// Ярус <paramref name="tier"/> — призма без дна: точное знаковое расстояние до неё (снаружи — до ближайшей точки
    /// поверхности, нормаль от неё; внутри — до ближайшей грани, при равенстве — вверх, затем X, затем Z).
    /// </summary>
    private static float TierDepth(Vector3 center, float radius, int tier, out Vector3 normal)
    {
        float half = ArenaTerrain.TierHalf(tier);
        float dx = center.X - ArenaTerrain.StepCentreX, dz = center.Z;
        float qx = MathF.Abs(dx) - half, qz = MathF.Abs(dz) - half, qy = center.Y - ArenaTerrain.TierTop(tier);
        float ox = MathF.Max(qx, 0), oy = MathF.Max(qy, 0), oz = MathF.Max(qz, 0);
        float outside = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
        if (outside > 0)
        {
            normal = new Vector3(MathF.CopySign(ox, dx), oy, MathF.CopySign(oz, dz)) / outside;
            return radius - outside;
        }
        // Внутри (или нечисловой центр: максимум несёт NaN дальше — глубина NaN, касания нет).
        float inside = MathF.Max(qy, MathF.Max(qx, qz));
        normal = qy >= inside ? Vector3.UnitY
            : qx >= inside ? new Vector3(MathF.CopySign(1, dx), 0, 0)
            : new Vector3(0, 0, MathF.CopySign(1, dz));
        return radius - inside;
    }

    [DoesNotReturn]
    private static void ThrowFace(int face) =>
        throw new ArgumentOutOfRangeException(nameof(face), face, $"грань земли арены — 0…{TierCount}");

    private static Pillar[] BuildPillars()
    {
        var pillars = new Pillar[ArenaLayout.PillarCount];
        for (int i = 0; i < pillars.Length; i++)
        {
            var p = ArenaLayout.Pillars[i];
            pillars[i] = new Pillar(p.X, p.Y, p.Z);
        }
        return pillars;
    }
}
