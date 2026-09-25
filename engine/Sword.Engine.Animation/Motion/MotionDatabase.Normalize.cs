namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Нормализация признаков базы (точка 8б): среднее по признаку и разброс, общий на группу (среднее разбросов её признаков, не
/// меньше <see cref="FeatureGroup.MinStd"/>), по искомым кадрам; суммы — в double. Зовётся из <see cref="Build"/>.
/// </summary>
public sealed partial class MotionDatabase
{
}
