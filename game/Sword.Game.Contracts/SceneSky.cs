using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Солнце и небо сцены для объёмов клиентских модулей (дым огня): <see cref="Sun"/> — множитель альбедо·N·L солнца (у UE-света
/// той же силы освещённость в π раз больше), <see cref="Sky"/> — цвет неба для вертикальной поверхности. Линейный HDR до экспозиции.
/// </summary>
public readonly record struct SceneSky(Vector3 Sun, Vector3 Sky)
{
    /// <summary>Ни солнца, ни неба (сервиса нет — тесты модулей без игры).</summary>
    public static SceneSky None => default;
}

/// <summary>Солнце и небо сцены кадра для клиентских модулей (сервис клиента — <c>ClientServices</c>): реализует клиентская часть игры.</summary>
public interface ISceneSky
{
    /// <summary>Солнце и небо следующего кадра.</summary>
    SceneSky Sky { get; }
}
