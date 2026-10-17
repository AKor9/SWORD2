using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Cutting;

/// <summary>Чем кончился удар клинком по части.</summary>
public enum SeverOutcome : byte
{
    None = 0,
    Partial = 1,
    Cut = 2,
}

/// <summary>
/// Физика разреза клинком. Энергия удара — <c>½ · m · v⊥² · острота</c>, v⊥ — скорость точки клинка поперёк его оси. Плоскость
/// разреза — через ось клинка и направление его хода (не ближе 30° к оси части), пересекает ось части в точке y₀. Укол режет так же —
/// поперечной долей хода клинка; прямой укол вдоль оси — прокол (неполный разрез). Сечение там —
/// эллипс <c>π·r²/|cos θ|</c> (θ — наклон плоскости к оси): мягкие ткани и кость со своей удельной энергией. Хватило — отруб:
/// культя от проксимального конца до y₀, кусок — дальше, массы — по объёму профиля ткани, импульс куску — работа разреза / скорость.
/// Не хватило — неполный разрез с глубиной по доле сечения.
/// </summary>
public static class SeverPhysics
{
    /// <summary>Наименьший наклон плоскости к оси части: продольный разрез доворачивается до 30°.</summary>
    public const float MinAxisCos = 0.5f;

    /// <summary>Отступ разреза от концов культи, м: не режет «в ноль» у сустава и не отрезает пустоту за концом.</summary>
    public const float EndMargin = 0.01f;

    /// <summary>Скорость клинка поперёк оси, ниже которой он не режет, м/с.</summary>
    public const float MinCutSpeed = 0.5f;

    /// <summary>Наименьшие полудлина и радиус капсулы куска, м.</summary>
    public const float MinHalf = 0.005f, MinRadius = 0.02f;

    /// <summary>
    /// Решение по заявке. <paramref name="body"/> — тело части сейчас; <paramref name="shift"/> — сдвиг центра культи по оси
    /// (<see cref="Severance.Shift"/>), центр кости — <c>центр − ось·shift</c>; <paramref name="end"/> — дистальный конец части по оси
    /// (у культи — её конец).
    /// </summary>
    public static SeverOutcome Decide(in SeverRequested r, int segment, in BodyState body, float shift, float end, TissueProfiles tissue,
        in CuttingRules rules, out LimbDetached cut, out PartialCut partial)
    {
        cut = default;
        partial = default;
        // Укол — тот же расчёт: режет только то, что клинок прошёл поперёк своей оси; прямой укол вдоль оси — прокол (неполный).
        var axisWorld = Vector3.Transform(Vector3.UnitY, body.Rotation);
        var boneCenter = body.Position - axisWorld * shift;
        var edge = r.Blade.LengthSquared() > 1e-8f ? Vector3.Normalize(r.Blade) : axisWorld;
        var across = r.Velocity - edge * Vector3.Dot(r.Velocity, edge);
        float speed = across.Length();
        if (!(speed >= MinCutSpeed))
        {
            if (r.Kind != HitKind.Thrust || !(r.Velocity.LengthSquared() > MinCutSpeed * MinCutSpeed)) return SeverOutcome.None;
            partial = new PartialCut
            {
                Request = r.Id, Victim = r.Victim, Segment = segment, Entry = r.Point, Dir = Vector3.Normalize(r.Velocity),
                Depth = 2 * body.Radius, Fraction = 0,
            };
            return SeverOutcome.Partial;
        }
        var inverse = Quaternion.Conjugate(body.Rotation);
        var n = Vector3.Transform(Vector3.Normalize(Vector3.Cross(edge, across)), inverse);
        if (!float.IsFinite(n.X + n.Y + n.Z)) return SeverOutcome.None;
        n = Tilt(n);
        // Плоскость пересекает ось части там, где через неё проходит точка касания; в пределах культи.
        var p = Vector3.Transform(r.Point - boneCenter, inverse);
        float proximal = shift - body.HalfLength;
        float lo = proximal + EndMargin, hi = end - EndMargin;
        if (!(hi > lo)) return SeverOutcome.None;
        float y0 = Math.Clamp(Vector3.Dot(n, p) / n.Y, lo, hi);

        float r0 = tissue.SliceRadius(segment, tissue.Slice(segment, y0));
        float bone = MathF.Min(tissue.Bone[segment], r0);
        float tilt = 1 / n.Y;
        float soft = MathF.PI * (r0 * r0 - bone * bone) * tilt, hard = MathF.PI * bone * bone * tilt;
        float need = rules.SoftToughness * soft + rules.BoneToughness * hard;
        float energy = 0.5f * rules.EffectiveMass * speed * speed * MathF.Max(r.Sharpness, 0);

        float start = tissue.Start[segment];
        float whole = tissue.Volume(segment, start, end), distal = tissue.Volume(segment, y0, end);
        bool tiny = !(whole > 0) || distal < rules.MinPiece * whole || whole - distal < rules.MinPiece * whole * 0.5f;
        if (energy < need || tiny)
        {
            float fraction = Math.Clamp(energy / need, 0, 1);
            partial = new PartialCut
            {
                Request = r.Id, Victim = r.Victim, Segment = segment, Entry = r.Point, Dir = across / speed,
                Depth = fraction * 2 * r0, Fraction = fraction,
            };
            return SeverOutcome.Partial;
        }

        float pieceMass = body.Mass * distal / whole;
        float pieceRadius = Math.Clamp(tissue.MeanRadius(segment, y0, end), MinRadius, MathF.Max(body.Radius * 1.5f, MinRadius));
        float pieceLength = end - y0;
        // Импульс клинка куску — работа разреза за проход со скоростью клинка (F·t = W / v), не быстрее самого клинка.
        float impulse = MathF.Min(need / speed, pieceMass * speed);
        cut = new LimbDetached
        {
            Request = r.Id, Victim = r.Victim, Segment = segment, Mode = SeverMode.Cut, Normal = n, Offset = n.Y * y0,
            StumpShift = (proximal + y0) * 0.5f, StumpHalf = MathF.Max(MinHalf, (y0 - proximal) * 0.5f), StumpRadius = body.Radius,
            StumpMass = body.Mass - pieceMass,
            PieceCenter = (y0 + end) * 0.5f, PieceRadius = pieceRadius, PieceHalf = MathF.Max(MinHalf, pieceLength * 0.5f - pieceRadius),
            PieceMass = pieceMass, Impulse = across / speed * impulse, Point = r.Point,
        };
        return SeverOutcome.Cut;
    }

    /// <summary>Нормаль в системе части — к дистальной стороне (+Y) и не ближе 30° к оси: продольный разрез доворачивается.</summary>
    public static Vector3 Tilt(Vector3 n)
    {
        if (n.Y < 0) n = -n;
        if (n.Y >= MinAxisCos) return n;
        var flat = new Vector3(n.X, 0, n.Z);
        flat = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitX;
        float sin = MathF.Sqrt(1 - MinAxisCos * MinAxisCos);
        return flat * sin + Vector3.UnitY * MinAxisCos;
    }
}

/// <summary>
/// Dismember, реактивная по <see cref="SeverRequested"/>: решение по каждой заявке тика (<see cref="SeverPhysics.Decide"/>) — событие
/// <see cref="LimbDetached"/> или <see cref="PartialCut"/>. Таз не режется (дети крепятся к нему с разных концов); вторая заявка тика на ту же
/// часть того же бойца отклоняется — часть уже отделяется.
/// </summary>
public sealed class SeverDecisionSystem : IReactiveSystem<Event<SeverRequested>>
{
    private const int Max = 64;
    private readonly Entity[] _victims = new Entity[Max];
    private readonly int[] _used = new int[Max];
    private readonly ushort[] _taken = new ushort[Max];
    private readonly SeverRequested?[] _blast = new SeverRequested?[Max * BodySegs.Count];
    private readonly bool[] _blasted = new bool[Max];
    private readonly BodyState[] _bodies = new BodyState[BodySegs.Count];
    private readonly float[] _shifts = new float[BodySegs.Count], _ends = new float[BodySegs.Count];
    private readonly LimbDetached[] _events = new LimbDetached[BodySegs.Count];
    private int _count;

    public void Setup(SystemSetup s) =>
        s.Label(new SystemLabel("cutting.decide")).Reads<BodyRef>().Reads<Severance>().Emits<LimbDetached>().Emits<PartialCut>()
            .ReserveCommands(0, 64, 64 * 200);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<SeverRequested>> requests)
    {
        var world = ctx.World;
        var physics = world.Resource<IPhysicsWorld>();
        var tissue = world.Resource<TissueProfiles>();
        var rules = world.Resource<CuttingRules>();
        _count = 0;
        foreach (var e in requests) Decide(ctx, world, physics, tissue, rules, e.Value);
        for (int v = 0; v < _count; v++)
            if (_blasted[v]) Blast(ctx, world, physics, tissue, rules, v);
    }

    /// <summary>Боец в накоплении тика: сколько кусков заведено, какие части уже отделяются.</summary>
    private int Victim(Entity e)
    {
        for (int i = 0; i < _count; i++)
            if (_victims[i] == e) return i;
        if (_count == Max) return -1;
        _victims[_count] = e;
        _used[_count] = 0;
        _taken[_count] = 0;
        _blasted[_count] = false;
        _blast.AsSpan(_count * BodySegs.Count, BodySegs.Count).Clear();
        return _count++;
    }

    private void Decide(in SystemContext ctx, World world, IPhysicsWorld physics, TissueProfiles tissue, in CuttingRules rules, in SeverRequested r)
    {
        int part = r.Part;
        if (!BodySegs.IsFlesh(part) || !world.IsAlive(r.Victim) || !world.TryGet<BodyRef>(r.Victim, out var bodyRef)) return;
        var a = new ArticulationHandle(bodyRef.Index, bodyRef.Generation);
        if (!physics.IsAlive(a) || physics.BodyCount(a) <= part) return;
        int v = Victim(r.Victim);
        if (v < 0) return;
        if (r.Kind == HitKind.Blast)
        {
            _blast[v * BodySegs.Count + part] = r;
            _blasted[v] = true;
            return;
        }
        if (part == BodySegs.Pelvis || (_taken[v] & (1 << part)) != 0) return;   // часть уже отделяется в этом тике
        var severance = world.TryGet<Severance>(r.Victim, out var sv) ? sv : default;
        if (severance.IsTorn(part)) return;
        int free = Severance.MaxPieces - severance.Pieces - _used[v];
        var body = physics.Get(physics.Body(a, BodySegs.Body(part)));
        float shift = severance.Shift[part];
        float end = severance.IsCut(part) ? shift + body.HalfLength : tissue.End[part];
        LimbDetached cut = default;
        PartialCut partial = default;
        var outcome = r.Kind == HitKind.Blunt
            ? ImpactPhysics.Blunt(r, part, body, shift, end, tissue, rules, free, out cut)
            : free >= 1 ? SeverPhysics.Decide(r, part, body, shift, end, tissue, rules, out cut, out partial) : SeverOutcome.None;
        if (outcome == SeverOutcome.Partial) ctx.Commands.Emit(partial);
        if (outcome == SeverOutcome.Cut) Emit(ctx, v, severance, cut, physics, a);
    }

    /// <summary>
    /// Событие отделения: номер куска — по кускам бойца и заведённым в этом тике; у разреза и перелома — дети, уходящие с куском
    /// (по телам тика).
    /// </summary>
    private void Emit(in SystemContext ctx, int v, in Severance severance, LimbDetached ev, IPhysicsWorld physics, ArticulationHandle a)
    {
        if (ev.Mode is SeverMode.Cut or SeverMode.Fracture) ev.Carried = Carried(physics, a, severance, ev);
        int pieces = ev.Mode switch { SeverMode.Cut or SeverMode.Fracture => 1, SeverMode.Shatter => ev.Count, _ => 0 };
        ev.Piece = pieces > 0 ? severance.Pieces + _used[v] : -1;
        _used[v] += pieces;
        _taken[v] |= (ushort)(1 << ev.Segment);
        ctx.Commands.Emit(ev);
    }

    private void Blast(in SystemContext ctx, World world, IPhysicsWorld physics, TissueProfiles tissue, in CuttingRules rules, int v)
    {
        var e = _victims[v];
        if (!world.TryGet<BodyRef>(e, out var bodyRef)) return;
        var a = new ArticulationHandle(bodyRef.Index, bodyRef.Generation);
        var severance = world.TryGet<Severance>(e, out var sv) ? sv : default;
        for (int s = 0; s < BodySegs.Count; s++)
        {
            _bodies[s] = physics.Get(physics.Body(a, BodySegs.Body(s)));
            _shifts[s] = severance.Shift[s];
            _ends[s] = severance.IsCut(s) ? _shifts[s] + _bodies[s].HalfLength : tissue.End[s];
        }
        var blast = _blast.AsSpan(v * BodySegs.Count, BodySegs.Count);
        // части, которые уже отделяются этим тиком, взрыв не трогает
        for (int s = 0; s < BodySegs.Count; s++)
            if ((_taken[v] & (1 << s)) != 0) blast[s] = null;
        var used = severance with { Pieces = severance.Pieces + _used[v] };
        int n = ImpactPhysics.Blast(blast, _bodies, _shifts, _ends, used, tissue, rules, _events);
        for (int i = 0; i < n; i++) Emit(ctx, v, severance, _events[i], physics, a);
    }

    private static ushort Carried(IPhysicsWorld physics, ArticulationHandle a, in Severance severance, in LimbDetached ev)
    {
        Span<Vector3> positions = stackalloc Vector3[BodySegs.Count];
        Span<Quaternion> rotations = stackalloc Quaternion[BodySegs.Count];
        Span<float> halves = stackalloc float[BodySegs.Count];
        for (int s = 0; s < BodySegs.Count; s++)
        {
            var b = physics.Get(physics.Body(a, BodySegs.Body(s)));
            (positions[s], rotations[s], halves[s]) = (b.Position, b.Rotation, b.HalfLength);
        }
        int seg = ev.Segment;
        var boneCenter = positions[seg] - Vector3.Transform(Vector3.UnitY, rotations[seg]) * severance.Shift[seg];
        return SeverGeometry.Carried(seg, ev.Normal, ev.Offset, boneCenter, rotations[seg], positions, rotations, halves, severance.OnBody(seg));
    }
}
