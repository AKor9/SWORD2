using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Туман сцены для прозрачного поверх неё (пламя огня): линейный по расстоянию до камеры, как у освещения непрозрачной сцены, —
/// доля f = saturate((d − <see cref="Start"/>) / <see cref="Range"/>), цвет → lerp(цвет, <see cref="Colour"/>, f). Линейный цвет HDR до
/// экспозиции; день или ночь — у клиента игры.
/// </summary>
public readonly record struct SceneFog(Vector3 Colour, float Start, float Range)
{
    /// <summary>Тумана нет (сервиса нет — тесты модулей без игры): доля всюду 0.</summary>
    public static SceneFog None => new(Vector3.Zero, float.MaxValue, 1);

    /// <summary>Доля тумана на расстоянии <paramref name="distance"/> м.</summary>
    public float Factor(float distance) => Math.Clamp((distance - Start) / Range, 0f, 1f);
}

/// <summary>
/// Туман сцены кадра для клиентских модулей (сервис клиента — <c>ClientServices</c>): реализует клиентская часть игры (окружение дня или
/// ночи), отдаёт модулям exe. Только представление.
/// </summary>
public interface ISceneFog
{
    /// <summary>Туман следующего кадра.</summary>
    SceneFog Fog { get; }
}
