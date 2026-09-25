namespace Sword.Engine.Animation.Motion;

/// <summary>
/// Граф переходов базы (точка 8б): внутри клипа f → f + 1, шов цикла, продолжение нециклического клипа по политике
/// (<see cref="IMotionGraphPolicy"/>) с ценой шва, ранний выход в окне <see cref="MotionBuildParams.ExitWindow"/>, явный разрыв
/// ничьих (<see cref="MotionBuildParams.SeamTie"/>); траектории будущего по графу и прошлого по политике. Зовётся из
/// <see cref="Build"/>.
/// </summary>
public sealed partial class MotionDatabase
{
}
