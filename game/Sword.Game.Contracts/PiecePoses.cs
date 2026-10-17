using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Отделённый кусок бойца в тике: номер куска (тело сочленения <c>BodySegs.Bodies + Piece</c>), сегмент, от которого он отделён, как, и тело —
/// центр, поворот, система тела <see cref="World"/> (вектор слева: поворот, затем перенос), радиус и полудлина, м. Ось куска — Y системы
/// сегмента на тике отделения (кусок повёрнут как сегмент, центр — <see cref="LimbDetached.PieceCenter"/> по его оси).
/// </summary>
public readonly record struct PieceBody(int Piece, int Segment, SeverMode Mode, Vector3 Position, Quaternion Rotation, Matrix4x4 World, float Radius,
    float HalfLength);

/// <summary>
/// Тела отделённых кусков бойцов последнего сыгранного тика для клиентских модулей (сервис клиента; без модуля Cutting кусков нет): в
/// наблюдателе тиков — тела ровно этого тика, как позы <see cref="IFighterPoses"/>. Снимок — при первом обращении в тике; без выделений
/// после прогрева. Только читает отделения (<see cref="Severance"/>) и тела физики.
/// </summary>
public interface IPiecePoses
{
    /// <summary>Тик снимка (последний сыгранный).</summary>
    ulong Tick { get; }

    /// <summary>Куски бойца <paramref name="fighter"/> (<see cref="FighterKey"/>) по номеру куска; нет — пусто.</summary>
    ReadOnlySpan<PieceBody> Pieces(ulong fighter);
}
