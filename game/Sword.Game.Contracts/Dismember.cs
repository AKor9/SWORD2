using System.Numerics;
using System.Runtime.CompilerServices;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>Как часть отделилась: разрез плоскостью (клинок), отрыв по суставу (тупое, взрыв), разрыв в клочья (взрыв вблизи, молот по голове).</summary>
public enum SeverMode : byte
{
    Cut = 0,
    Tear = 1,
    Shatter = 2,

    /// <summary>Перелом (тупое): сегмент делится на культю и кусок, как разрез, но их держит слабый сустав — конечность гнётся в месте перелома.</summary>
    Fracture = 3,
}

/// <summary>
/// Часть отделена (событие Dismember, модуль Cutting) — ответ на заявку <see cref="SeverRequested"/> с тем же
/// <see cref="Request"/>; ядро применяет его в React (<see cref="Severance"/>, тела физики). Плоскость и формы — в системе тела
/// сегмента на тике отделения (ось капсулы — Y, проксимальный сустав — на −Y), поэтому клиент строит меш по записи и позе тика.
/// </summary>
public struct LimbDetached
{
    public RequestId Request;
    public Entity Victim;

    /// <summary>Сегмент, по которому прошло отделение (<see cref="BodySegs"/>).</summary>
    public int Segment;

    public SeverMode Mode;

    /// <summary>Номер куска у бойца (<see cref="Severance.Pieces"/> на момент отделения; тело <c>BodySegs.Bodies + Piece</c>); у отрыва — −1.</summary>
    public int Piece;

    /// <summary>Плоскость разреза в системе тела сегмента: <c>dot(Normal, p) = Offset</c>, нормаль — в сторону куска.</summary>
    public Vector3 Normal;
    public float Offset;

    /// <summary>
    /// Разрез и перелом: дети сегмента с потомками (маска), уходящие с куском, — их сустав с сегментом по сторону куска от плоскости
    /// (<see cref="SeverGeometry.Carried"/>); остальные дети остаются на культе.
    /// </summary>
    public ushort Carried;

    /// <summary>Культя (<see cref="SeverMode.Cut"/>): капсула от проксимального сустава до разреза — сдвиг центра по оси, полудлина, радиус, масса.</summary>
    public float StumpShift, StumpHalf, StumpRadius, StumpMass;

    /// <summary>Кусок: центр по оси (система тела сегмента), полудлина, радиус, масса. У отрыва — весь сегмент.</summary>
    public float PieceCenter, PieceHalf, PieceRadius, PieceMass;

    /// <summary>Импульс, который клинок передал куску, Н·с (мир), и точка приложения (мир).</summary>
    public Vector3 Impulse, Point;

    /// <summary>Клочья (<see cref="SeverMode.Shatter"/>): кусков, зерно ячеек (<see cref="ShatterCells"/>) и отрезок оси сегмента с ними, м.</summary>
    public int Count;
    public uint Seed;
    public float From, To;
}

/// <summary>
/// Ячейки клочьев: центры ячеек Вороного внутри сегмента — одна функция для симуляции (тела кусков) и клиента (разрез меша). Центр — в
/// системе тела сегмента (центр кости): по оси — каждая в своём слое [from, to], поперёк — в круге 0,6 радиуса.
/// </summary>
public static class ShatterCells
{
    /// <summary>Псевдослучайное [0, 1) из зерна, ячейки и оси — хеш (одинаков везде).</summary>
    public static float Unit(uint seed, int cell, int axis)
    {
        uint h = seed ^ (uint)cell * 0x9E3779B9u ^ (uint)axis * 0x85EBCA6Bu;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h >> 8) * (1f / (1 << 24));
    }

    /// <summary>Центры <paramref name="count"/> ячеек по оси — слои [from, to], каждая в своём слое.</summary>
    public static void Sites(uint seed, int count, float from, float to, float radius, Span<Vector3> sites)
    {
        float layer = (to - from) / count;
        for (int i = 0; i < count; i++)
        {
            float a = Unit(seed, i, 0), b = Unit(seed, i, 1), c = Unit(seed, i, 2);
            float r = radius * 0.6f * MathF.Sqrt(b), angle = c * MathF.Tau;
            sites[i] = new Vector3(r * MathF.Cos(angle), from + layer * (i + 0.2f + 0.6f * a), r * MathF.Sin(angle));
        }
    }
}

/// <summary>
/// Взрыв (команда тика): в точке <see cref="Position"/> (мир) с энергией <see cref="Energy"/>, Дж, и радиусом действия <see cref="Radius"/>,
/// м. Ядро толкает тела и убивает живых в зоне, модуль Cutting решает, что рвётся. Сейчас — клавиша проверки «взрыв у курсора»; позже —
/// умения и снаряды.
/// </summary>
public struct BlastCommand
{
    public Vector3 Position;
    public float Energy;
    public float Radius;
}

/// <summary>
/// Неполный разрез (событие Dismember, модуль Cutting): энергии удара не хватило на всё сечение — клинок дошёл до глубины
/// <see cref="Depth"/> от входа. Запись для ран и крови; бой не меняет.
/// </summary>
public struct PartialCut
{
    public RequestId Request;
    public Entity Victim;
    public int Segment;

    /// <summary>Точка входа и направление клинка в ткани (мир), глубина, м, и доля сечения, которую клинок прошёл.</summary>
    public Vector3 Entry, Dir;
    public float Depth, Fraction;
}

/// <summary>Отделённый кусок бойца: тело сочленения <c>BodySegs.Bodies + номер куска</c>, отрезанное от сегмента <see cref="Segment"/>.</summary>
public struct SeveredPiece
{
    public byte Segment;
    public SeverMode Mode;

    /// <summary>Плоскость разреза в системе тела сегмента до сдвига культи (как в <see cref="LimbDetached"/>).</summary>
    public Vector3 Normal;
    public float Offset;
}

[InlineArray(Severance.MaxPieces)]
public struct SeveredPieces
{
    private SeveredPiece _element;
}

[InlineArray(BodySegs.Count)]
public struct SegmentShifts
{
    private float _element;
}

[InlineArray(BodySegs.Count)]
public struct SegmentHolders
{
    private byte _element;
}

/// <summary>
/// Отделённые части бойца (ядро, по <see cref="LimbDetached"/>; без модуля Cutting компонента нет). Тело куска <c>k</c> —
/// <c>BodySegs.Bodies + k</c>. Ребёнок разрезанного сегмента висит на куске, с которым ушёл (<see cref="Holder"/>), или на культе;
/// оторванный сегмент (<see cref="Torn"/>) — без сустава с родителем. <see cref="Detached"/> — сегменты, чья цепочка до таза
/// разорвана: у живого они на физике.
/// </summary>
public struct Severance
{
    /// <summary>Кусков у бойца не больше: место в сочленении и привязках скина.</summary>
    public const int MaxPieces = 16;

    /// <summary>Сегменты-культи (тело сегмента — укороченная капсула у сустава с родителем).</summary>
    public ushort Cut;

    /// <summary>Сегменты, оторванные по суставу с родителем.</summary>
    public ushort Torn;

    /// <summary>Сегменты, не связанные с тазом (висят на куске или оторваны).</summary>
    public ushort Detached;

    public int Pieces;
    public SeveredPieces Piece;

    /// <summary>Сдвиг центра капсулы культи по оси сегмента от места по позе, м (0 — не резан).</summary>
    public SegmentShifts Shift;

    /// <summary>Кусок, на котором висит сегмент (номер + 1): ушёл с ним при разрезе родителя; 0 — на теле родителя.</summary>
    public SegmentHolders Holder;

    public readonly bool IsCut(int segment) => (Cut & (1 << segment)) != 0;
    public readonly bool IsTorn(int segment) => (Torn & (1 << segment)) != 0;
    public readonly bool IsDetached(int segment) => (Detached & (1 << segment)) != 0;

    /// <summary>Первый кусок, отрезанный от сегмента; нет — −1.</summary>
    public readonly int FirstPiece(int segment)
    {
        for (int k = 0; k < Pieces; k++)
            if (Piece[k].Segment == segment) return k;
        return -1;
    }

    /// <summary>
    /// Родитель тела <paramref name="body"/> в дереве после отделений: сегмент — родитель по дереву (или его культя) либо кусок, с которым
    /// сегмент ушёл; оторванный сегмент и кусок — −1 (свободны).
    /// </summary>
    public readonly int ParentBody(int body, int standardParent)
    {
        if (body >= BodySegs.Bodies || standardParent < 0 || IsTorn(body)) return -1;
        return Holder[body] > 0 ? BodySegs.Bodies + Holder[body] - 1 : standardParent;
    }

    /// <summary>Дети сегмента, висящие на его теле (не оторваны и не ушли с куском) — маска.</summary>
    public readonly ushort OnBody(int segment)
    {
        ushort mask = 0;
        for (int c = 0; c < BodySegs.Count; c++)
            if (c != BodySegs.Pelvis && BodySegs.Parent(c) == segment && !IsTorn(c) && Holder[c] == 0) mask |= (ushort)(1 << c);
        return mask;
    }
}

/// <summary>Геометрия отделения, общая для симуляции и клиента.</summary>
public static class SeverGeometry
{
    /// <summary>
    /// Дети сегмента <paramref name="segment"/> из <paramref name="candidates"/>, уходящие с куском, с потомками (маска): сустав ребёнка с
    /// сегментом — проксимальный конец его тела — по сторону куска от плоскости <c>dot(normal, p) = offset</c> в системе кости сегмента
    /// (центр <paramref name="boneCenter"/>, поворот <paramref name="rotation"/>). Клинок держит кисть — он всегда уходит с куском
    /// предплечья. Тела — по сегментам: центр, поворот, полудлина (ось — Y, проксимальный конец на −Y).
    /// </summary>
    public static ushort Carried(int segment, Vector3 normal, float offset, Vector3 boneCenter, Quaternion rotation, ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Quaternion> rotations, ReadOnlySpan<float> halves, ushort candidates)
    {
        var back = Quaternion.Conjugate(rotation);
        ushort mask = 0;
        for (int c = 0; c < BodySegs.Count; c++)
        {
            if ((candidates & (1 << c)) == 0 || c == BodySegs.Pelvis || BodySegs.Parent(c) != segment) continue;
            var joint = positions[c] - Vector3.Transform(Vector3.UnitY, rotations[c]) * halves[c];
            if (c == BodySegs.Weapon || Vector3.Dot(normal, Vector3.Transform(joint - boneCenter, back)) > offset) mask |= BodySegs.Branch(c);
        }
        return mask;
    }
}

/// <summary>
/// Профили ткани сегментов (ресурс ядра): по оси капсулы сегмента в позе привязки — радиус тела по мешу и радиус кости. Ось — Y
/// тела сегмента: от <see cref="Start"/> до <see cref="End"/> (проксимальный сустав — около −полудлины), <see cref="Samples"/>
/// равных слоёв. Нужен модулю Cutting для энергии сечения и масс кусков.
/// </summary>
public sealed class TissueProfiles
{
    public const int Samples = 32;

    /// <summary>По сегменту: начало и конец профиля по оси, м; полудлина капсулы по позе привязки, м.</summary>
    public readonly float[] Start = new float[BodySegs.Count], End = new float[BodySegs.Count], Half = new float[BodySegs.Count];

    /// <summary>Радиус тела в слое (сегмент · <see cref="Samples"/> + слой), м; 0 — слой пуст.</summary>
    public readonly float[] Radius = new float[BodySegs.Count * Samples];

    /// <summary>Радиус кости сегмента, м.</summary>
    public readonly float[] Bone = new float[BodySegs.Count];

    public float SliceRadius(int segment, int slice) => Radius[segment * Samples + slice];

    /// <summary>Слой, в котором точка оси <paramref name="y"/> (сжато к краям).</summary>
    public int Slice(int segment, float y)
    {
        float len = End[segment] - Start[segment];
        if (!(len > 0)) return 0;
        return Math.Clamp((int)((y - Start[segment]) / len * Samples), 0, Samples - 1);
    }

    /// <summary>Объём сегмента между точками оси <paramref name="from"/> и <paramref name="to"/>, м³ (тела вращения по слоям).</summary>
    public float Volume(int segment, float from, float to)
    {
        float s0 = Start[segment], len = End[segment] - s0;
        if (!(len > 0) || !(to > from)) return 0;
        float dy = len / Samples, v = 0;
        for (int i = 0; i < Samples; i++)
        {
            float a = s0 + i * dy, b = a + dy;
            float overlap = MathF.Min(b, to) - MathF.Max(a, from);
            if (overlap <= 0) continue;
            float r = Radius[segment * Samples + i];
            v += MathF.PI * r * r * overlap;
        }
        return v;
    }

    /// <summary>Средний радиус тела между точками оси (по объёму), м; пусто — 0.</summary>
    public float MeanRadius(int segment, float from, float to)
    {
        float v = Volume(segment, from, to);
        return to > from && v > 0 ? MathF.Sqrt(v / (MathF.PI * (to - from))) : 0;
    }
}
