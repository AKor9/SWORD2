using System.Numerics;

namespace Sword.Game.Contracts;

/// <summary>
/// Боец в позах тика (<see cref="IFighterPoses"/>): ключ (<see cref="FighterKey"/>), место (игрок — 0, враги — 1…32), мёртв ли и лежит ли
/// трупом по телам (<see cref="Corpse"/>: скин идёт за телами рэгдолла); суставов и тел сегментов в позе (0 — нет).
/// </summary>
public readonly record struct PosedFighter(ulong Fighter, int Slot, bool IsPlayer, bool Dead, bool Corpse, int JointCount, int SegmentCount);

/// <summary>
/// Тело сегмента в тике: капсула (ось — локальная Y) в мире — центр, поворот, система тела <see cref="World"/> (вектор слева: поворот,
/// затем перенос), радиус и полудлина оси, м. Точка в осях тела — <c>Transform(p, World⁻¹)</c>; обратно — <c>Transform(local, World)</c>.
/// </summary>
public readonly record struct SegmentBody(int Segment, Vector3 Position, Quaternion Rotation, Matrix4x4 World, float Radius, float HalfLength);

/// <summary>
/// Позы бойцов последнего сыгранного тика для клиентских модулей (сервис клиента): мировые матрицы суставов скелета — то же, что вход палитры
/// скина (живой — модельная · мир бойца на тике, труп — привязка · система тела сегмента сустава), и тела 14 сегментов (капсулы тика). В
/// наблюдателе тиков — поза ровно этого тика: якорь раны по <see cref="BloodHit.Point"/> того же тика. Снимок делается при первом обращении
/// в тике, по одному на тик; без выделений после прогрева. Поза кадра у клиента отличается только переносом корня по доле тика.
/// </summary>
public interface IFighterPoses
{
    /// <summary>Тик снимка (последний сыгранный).</summary>
    ulong Tick { get; }

    /// <summary>Бойцов в снимке (в порядке мира).</summary>
    int Count { get; }

    /// <summary>Боец <paramref name="index"/> (0 … <see cref="Count"/> − 1).</summary>
    PosedFighter Fighter(int index);

    /// <summary>Номер бойца в снимке по ключу; нет — −1.</summary>
    int Find(ulong fighter);

    /// <summary>Мировые матрицы суставов бойца <paramref name="index"/> (вектор слева); позы нет — пусто.</summary>
    ReadOnlySpan<Matrix4x4> Joints(int index);

    /// <summary>Тела сегментов бойца <paramref name="index"/> по номеру сегмента (<see cref="BodySegs"/>, 14 с клинком); тела нет — пусто.</summary>
    ReadOnlySpan<SegmentBody> Segments(int index);
}
