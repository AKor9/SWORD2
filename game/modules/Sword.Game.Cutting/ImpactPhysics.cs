using System.Numerics;
using Sword.Engine.Physics;
using Sword.Game.Contracts;

namespace Sword.Game.Cutting;

/// <summary>
/// Физика тупого удара и взрыва. Тупой: энергия <c>½ · m · v²</c> (m — масса молота с руками). Голова — в клочья выше порога;
/// конечность — отрыв по суставу, если энергии хватает на сечение у сустава, иначе перелом в точке удара, если хватает на сечение кости.
/// Импульс части — удар двух тел: <c>(1 + e) · μ · v</c>, μ — приведённая масса. Взрыв: энергия, дошедшая до части, против её массы —
/// клочья; иначе разность скоростей части и родителя от взрыва — отрыв, если энергия этой разности больше прочности сустава.
/// </summary>
public static class ImpactPhysics
{
    /// <summary>Упругость тупого удара по телу: почти неупругий.</summary>
    public const float Restitution = 0.2f;

    /// <summary>Клочьев из части.</summary>
    public const int ShatterCount = 5;

    /// <summary>Энергия взрыва на килограмм части, с которой она рвётся в клочья, Дж/кг.</summary>
    public const float ShatterPerKilogram = 80f;

    /// <summary>Отступ места перелома от концов культи, м.</summary>
    public const float FractureMargin = 0.03f;

    /// <summary>Тупой удар по части. <paramref name="freePieces"/> — сколько ещё кусков можно завести у бойца.</summary>
    public static SeverOutcome Blunt(in SeverRequested r, int segment, in BodyState body, float shift, float end, TissueProfiles tissue,
        in CuttingRules rules, int freePieces, out LimbDetached ev)
    {
        ev = default;
        float speed = r.Velocity.Length();
        if (!(speed >= SeverPhysics.MinCutSpeed) || segment == BodySegs.Pelvis || segment == BodySegs.Torso) return SeverOutcome.None;
        float energy = 0.5f * rules.BluntMass * speed * speed;
        // масса, которую молот встречает в точке: у конца сегмента меньше всей массы — часть удара уходит во вращение, а не в импульс
        float inv = body.InvMassAt(r.Point - body.Position, r.Velocity / speed);
        float mass = inv > 0 && float.IsFinite(inv) ? MathF.Min(1 / inv, body.Mass) : body.Mass;
        float mu = rules.BluntMass * mass / (rules.BluntMass + mass);
        var impulse = r.Velocity * (mu * (1 + Restitution));
        float proximal = shift - body.HalfLength;
        if (segment == BodySegs.Head && energy >= rules.HeadCrush && freePieces >= 2)
        {
            ev = Shatter(r, segment, body, shift, end, tissue, Math.Min(ShatterCount, freePieces), impulse);
            return SeverOutcome.Cut;
        }
        float joint = tissue.SliceRadius(segment, tissue.Slice(segment, proximal));
        if (energy >= rules.TearToughness * MathF.PI * joint * joint)
        {
            ev = Tear(r, segment, body, proximal, joint, impulse);
            return SeverOutcome.Cut;
        }
        if (segment == BodySegs.Head || freePieces < 1) return SeverOutcome.None;
        var p = Vector3.Transform(r.Point - (body.Position - Vector3.Transform(Vector3.UnitY, body.Rotation) * shift), Quaternion.Conjugate(body.Rotation));
        float lo = proximal + FractureMargin, hi = end - FractureMargin;
        if (!(hi > lo)) return SeverOutcome.None;
        float y0 = Math.Clamp(p.Y, lo, hi);
        float bone = tissue.Bone[segment];
        if (energy < rules.BoneFracture * MathF.PI * bone * bone) return SeverOutcome.None;
        ev = Split(r, segment, body, proximal, y0, end, tissue, SeverMode.Fracture, impulse);
        return SeverOutcome.Cut;
    }

    /// <summary>Отрыв по суставу: весь сегмент уходит с потомками; плоскость — у проксимального сустава (для меша).</summary>
    public static LimbDetached Tear(in SeverRequested r, int segment, in BodyState body, float proximal, float jointRadius, Vector3 impulse) => new()
    {
        Request = r.Id, Victim = r.Victim, Segment = segment, Mode = SeverMode.Tear, Piece = -1, Normal = Vector3.UnitY,
        Offset = proximal + SeverPhysics.EndMargin, StumpRadius = body.Radius, PieceRadius = MathF.Max(jointRadius, SeverPhysics.MinRadius),
        PieceMass = body.Mass, Impulse = impulse, Point = r.Point,
    };

    /// <summary>Деление сегмента поперёк оси в <paramref name="y0"/> (перелом; разрез — <see cref="SeverPhysics.Decide"/>): массы по объёму.</summary>
    public static LimbDetached Split(in SeverRequested r, int segment, in BodyState body, float proximal, float y0, float end, TissueProfiles tissue,
        SeverMode mode, Vector3 impulse)
    {
        float start = tissue.Start[segment];
        float whole = tissue.Volume(segment, start, end), distal = tissue.Volume(segment, y0, end);
        float pieceMass = whole > 0 ? body.Mass * distal / whole : body.Mass * 0.5f;
        float pieceRadius = Math.Clamp(tissue.MeanRadius(segment, y0, end), SeverPhysics.MinRadius, MathF.Max(body.Radius * 1.5f, SeverPhysics.MinRadius));
        return new LimbDetached
        {
            Request = r.Id, Victim = r.Victim, Segment = segment, Mode = mode, Normal = Vector3.UnitY, Offset = y0,
            StumpShift = (proximal + y0) * 0.5f, StumpHalf = MathF.Max(SeverPhysics.MinHalf, (y0 - proximal) * 0.5f), StumpRadius = body.Radius,
            StumpMass = body.Mass - pieceMass,
            PieceCenter = (y0 + end) * 0.5f, PieceRadius = pieceRadius, PieceHalf = MathF.Max(SeverPhysics.MinHalf, (end - y0) * 0.5f - pieceRadius),
            PieceMass = pieceMass, Impulse = impulse, Point = r.Point,
        };
    }

    /// <summary>
    /// Клочья: у сустава остаётся тонкая культя (2 см), остальное — <paramref name="count"/> ячеек Вороного (<see cref="ShatterCells"/>),
    /// масса — поровну; дети сегмента отрываются.
    /// </summary>
    public static LimbDetached Shatter(in SeverRequested r, int segment, in BodyState body, float shift, float end, TissueProfiles tissue, int count,
        Vector3 impulse)
    {
        float proximal = shift - body.HalfLength, y0 = proximal + 2 * SeverPhysics.EndMargin;
        var ev = Split(r, segment, body, proximal, y0, end, tissue, SeverMode.Shatter, impulse);
        uint seed = (uint)(r.Id.Tick * 2654435761UL) ^ (uint)r.Id.Index * 0x9E3779B9u ^ (uint)segment;
        return ev with { Count = count, Seed = seed, From = y0, To = end };
    }

    /// <summary>
    /// Взрыв по бойцу: заявки всех частей одного тика (<paramref name="requests"/>, по номеру сегмента; нет заявки — части не досталось).
    /// Решение — в <paramref name="result"/>; вернёт число событий. Импульсы взрыва даёт ядро — у событий импульс нулевой.
    /// </summary>
    public static int Blast(ReadOnlySpan<SeverRequested?> requests, ReadOnlySpan<BodyState> bodies, ReadOnlySpan<float> shifts, ReadOnlySpan<float> ends,
        in Severance severance, TissueProfiles tissue, in CuttingRules rules, Span<LimbDetached> result)
    {
        int n = 0, free = Severance.MaxPieces - severance.Pieces;
        ushort gone = 0;
        for (int s = 0; s < BodySegs.Count; s++)
        {
            if (requests[s] is not { } r || !BodySegs.IsFlesh(s) || s == BodySegs.Pelvis || severance.IsTorn(s)) continue;
            if (((gone >> BodySegs.Parent(s)) & 1) != 0) continue;   // родителя уже порвало — часть ушла с ним
            var body = bodies[s];
            float proximal = shifts[s] - body.HalfLength;
            if (s != BodySegs.Torso && r.Energy >= ShatterPerKilogram * body.Mass && free >= 2)
            {
                int count = Math.Min(ShatterCount, free);
                result[n++] = Shatter(r, s, body, shifts[s], ends[s], tissue, count, Vector3.Zero) with { Piece = severance.Pieces + (Severance.MaxPieces - severance.Pieces - free) };
                free -= count;
                gone |= (ushort)(1 << s);
                continue;
            }
            int parent = BodySegs.Parent(s);
            if (s == BodySegs.Torso || parent < 0) continue;
            var dv = r.Velocity - (requests[parent] is { } pr ? pr.Velocity : Vector3.Zero);
            float mp = bodies[parent].Mass, mu = body.Mass * mp / (body.Mass + mp);
            float relative = 0.5f * mu * dv.LengthSquared();
            float joint = tissue.SliceRadius(s, tissue.Slice(s, proximal));
            if (relative < rules.TearToughness * MathF.PI * joint * joint) continue;
            result[n++] = Tear(r, s, body, proximal, joint, Vector3.Zero);
            gone |= (ushort)(1 << s);
        }
        return n;
    }
}
