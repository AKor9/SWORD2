using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Туман сцены для прозрачного поверх неё (пламя огня, дым, кровь на полу): линейный по расстоянию до камеры
/// fd = saturate((d − <see cref="Start"/>) / <see cref="Range"/>) и высотный — плотность <see cref="HeightDensity"/>·e^(−<see cref="HeightFalloff"/>·y)
/// на метр вдоль луча, fh = 1 − e^(−интеграл); f = fd + (1 − fd)·fh, цвет → lerp(цвет, <see cref="Colour"/>, f). Линейный HDR до
/// экспозиции. Шейдерный двойник — <c>FogFactor</c> (common/fog.hlsli).
/// </summary>
public readonly record struct SceneFog(Vector3 Colour, float Start, float Range, float HeightDensity = 0, float HeightFalloff = 0)
{
    /// <summary>Тумана нет (сервиса нет — тесты модулей без игры): доля всюду 0.</summary>
    public static SceneFog None => new(Vector3.Zero, float.MaxValue, 1);

    /// <summary>Доля тумана точки <paramref name="point"/> для камеры в <paramref name="eye"/>.</summary>
    public float Factor(Vector3 eye, Vector3 point)
    {
        float d = Vector3.Distance(point, eye);
        float fd = Math.Clamp((d - Start) / Range, 0f, 1f);
        if (HeightDensity <= 0) return fd;
        float h = HeightFalloff * (point.Y - eye.Y);
        float along = MathF.Abs(h) > 1e-4f ? (1 - MathF.Exp(-h)) / h : 1;
        float fh = 1 - MathF.Exp(-HeightDensity * d * MathF.Exp(-HeightFalloff * eye.Y) * along);
        return fd + (1 - fd) * fh;
    }
}

/// <summary>Туман сцены кадра для клиентских модулей (сервис клиента); реализует клиентская часть игры. Только представление.</summary>
public interface ISceneFog
{
    /// <summary>Туман следующего кадра.</summary>
    SceneFog Fog { get; }
}
