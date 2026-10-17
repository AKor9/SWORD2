using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Отделение частей тела — то, что делает ядро по событию <see cref="LimbDetached"/> модуля Cutting: деление тела сегмента на культю
/// и кусок (новое тело сочленения) со скоростями материи в момент разреза, перенос суставов детей на кусок без перепривязки, отрыв
/// сустава, отделённое у живого — на физику, привязки скина. Дерево после отделений — <see cref="Severance.ParentBody"/>.
/// </summary>
public static class Severing
{
    /// <summary>Сегмент и все его потомки по дереву тела (маска).</summary>
    public static ushort Branch(int segment)
    {
        ushort mask = 0;
        for (int s = 0; s < BodySegs.Count; s++)
            for (int k = s; k >= 0; k = CorpseJoints.Parent(k))
                if (k == segment)
                {
                    mask |= (ushort)(1 << s);
                    break;
                }
        return mask;
    }

    /// <summary>Без этой части живой не стоит и не живёт: таз, торс, голова, ноги. Без руки — бьётся дальше.</summary>
    public static bool KillsLiving(int segment) => segment is not (BodySegs.UArmL or BodySegs.FArmL or BodySegs.UArmR or BodySegs.FArmR);

    /// <summary>
    /// Суставы тела бойца по дереву после отделений (<paramref name="severance"/>): описания трупа по позе <paramref name="model"/>,
    /// родитель — по <see cref="Severance.ParentBody"/>; оторванные сегменты и клинок отделённой руки — без сустава. Вернёт число.
    /// </summary>
    public static int Joints(IPhysicsWorld physics, ArticulationHandle body, CorpseJoints joints, ReadOnlySpan<Matrix4x4> model,
        in Matrix4x4 fighterWorld, in Severance severance, Span<JointDesc> standard, Span<JointDesc> result)
    {
        joints.Describe(physics, body, model, fighterWorld, standard);
        int n = 0;
        for (int j = 0; j < CorpseJoints.Count; j++)
        {
            var d = standard[j];
            int parent = severance.ParentBody(d.Child, d.Parent);
            if (parent < 0 || parent >= physics.BodyCount(body)) continue;
            if (d.Child == BodySegs.Weapon && severance.IsDetached(BodySegs.Weapon)) continue;   // меч из отделённой руки выпадает
            result[n++] = d with { Parent = parent };
        }
        // Перелом: кусок держит слабый сустав у культи.
        for (int k = 0; k < severance.Pieces; k++)
            if (severance.Piece[k].Mode == SeverMode.Fracture)
                result[n++] = FractureJoint(physics, body, severance.Piece[k].Segment, BodySegs.Bodies + k);
        return n;
    }

    /// <summary>
    /// Суставы сочленения заменяются <paramref name="joints"/> (привязка по позе), но суставы детей из <paramref name="keep"/> — с прежним
    /// описанием и привязкой: отделённое лежит не в позе бойца, и привязка (поза покоя, ось шарнира) по нынешней позе была бы чужой.
    /// </summary>
    public static void SetJointsKeeping(IPhysicsWorld physics, ArticulationHandle body, ReadOnlySpan<JointDesc> joints, ushort keep)
    {
        Span<JointDesc> descs = stackalloc JointDesc[MaxBodies];
        Span<JointBinding> bindings = stackalloc JointBinding[MaxBodies];
        Span<bool> has = stackalloc bool[MaxBodies];
        for (int j = 0, n = physics.JointCount(body); j < n; j++)
        {
            var d = physics.Joint(body, j);
            if (d.Child >= BodySegs.Bodies || (keep & (1 << d.Child)) == 0) continue;
            descs[d.Child] = d;
            bindings[d.Child] = physics.Binding(body, j);
            has[d.Child] = true;
        }
        physics.SetJoints(body, joints);
        for (int j = 0; j < joints.Length; j++)
        {
            int child = joints[j].Child;
            if (child < MaxBodies && has[child] && descs[child].Parent == joints[j].Parent) physics.SetJoint(body, j, descs[child], bindings[child]);
        }
    }

    /// <summary>Пределы сустава перелома: кость не держит — гнётся широко, но не через себя.</summary>
    public static readonly CorpseJoints.SwingLimits Broken = new(-1.4f, 1.4f, 1.2f, 0.9f);

    /// <summary>Сустав перелома: культя сегмента — родитель, кусок — ребёнок, точка — проксимальный конец куска (место перелома).</summary>
    public static JointDesc FractureJoint(IPhysicsWorld physics, ArticulationHandle body, int segment, int piece)
    {
        var p = physics.Get(physics.Body(body, piece));
        return JointDesc.SwingTwist(BodySegs.Body(segment), piece, new Vector3(0, -(p.HalfLength + p.Radius), 0),
            Broken.SwingXMin, Broken.SwingXMax, Broken.SwingZ, Broken.Twist);
    }

    /// <summary>
    /// Пары самоколлизии после отделений: пары трупа (<see cref="CorpseJoints.Collides"/>) и каждый кусок — со всеми телами, кроме
    /// связанных с ним суставом; клочья одного сегмента не сталкиваются между собой и с его культей (рождаются внахлёст). Вернёт число.
    /// </summary>
    public static int SelfPairs(ReadOnlySpan<JointDesc> joints, int bodies, in Severance severance, Span<BodyPair> result)
    {
        int n = 0;
        foreach (var p in CorpseJoints.SelfPairs) result[n++] = p;
        for (int piece = BodySegs.Bodies; piece < bodies; piece++)
            for (int other = 0; other < piece; other++)
                if (!Jointed(joints, piece, other) && !Siblings(severance, piece, other)) result[n++] = new BodyPair(other, piece);
        return n;
    }

    /// <summary>Клочок и культя его сегмента или два клочка одного сегмента.</summary>
    private static bool Siblings(in Severance severance, int piece, int other)
    {
        int k = piece - BodySegs.Bodies;
        if ((uint)k >= Severance.MaxPieces || severance.Piece[k].Mode != SeverMode.Shatter) return false;
        int segment = severance.Piece[k].Segment;
        if (other == BodySegs.Body(segment)) return true;
        int j = other - BodySegs.Bodies;
        return (uint)j < Severance.MaxPieces && j < severance.Pieces && severance.Piece[j].Mode == SeverMode.Shatter && severance.Piece[j].Segment == segment;
    }

    private static bool Jointed(ReadOnlySpan<JointDesc> joints, int a, int b)
    {
        foreach (var d in joints)
            if (d.Parent == a && d.Child == b || d.Parent == b && d.Child == a) return true;
        return false;
    }

    /// <summary>Плотность тела, кг/м³ (размер клочьев по массе).</summary>
    public const float Density = 1050;

    /// <summary>Разлёт клочьев от оси сегмента, м/с.</summary>
    public const float ShatterSpread = 1.0f;

    /// <summary>Радиус шара, чья инерция — нижняя граница инерции клочка, м.</summary>
    public const float MinInertiaRadius = 0.06f;

    /// <summary>Мест под суставы и пары после отделений.</summary>
    public const int MaxBodies = BodySegs.Bodies + Severance.MaxPieces, MaxPairs = MaxBodies * (MaxBodies - 1) / 2;

    /// <summary>Система тела в мире по точке оси сегмента: центр тела сейчас — точка оси <paramref name="shift"/>.</summary>
    public static Vector3 AxisPoint(in BodyState body, float shift, float y) => body.Position + Vector3.Transform(Vector3.UnitY, body.Rotation) * (y - shift);

    /// <summary>То же тело в другой точке оси: поза сдвинута целиком (и прошлые), скорость — скорость материи там.</summary>
    public static BodyState Moved(in BodyState body, Vector3 to)
    {
        var s = body;
        var delta = to - body.Position;
        s.Velocity = body.PointVelocity(to);
        s.Position += delta;
        s.PrevPosition += delta;
        s.StartPosition += delta;
        return s;
    }
}

/// <summary>
/// End, реактивная по снятию <see cref="Severance"/> (постройка бойца заново): куски уходят из сочленения в том же тике — вместе с их
/// суставами и парами (тело бойца снова 14 капсул до конца тика, как требует <c>physics.bodyRefs</c>).
/// </summary>
public sealed class SeverClearSystem(IPhysicsWorld physics) : IReactiveSystem<Removed<Severance>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.SeverClear).Reads<BodyRef>().Reads<Severance>();

    public void React(in SystemContext ctx, ReadOnlySpan<Removed<Severance>> removed)
    {
        var world = ctx.World;
        foreach (var r in removed)
        {
            if (!world.IsAlive(r.Entity) || world.Has<Severance>(r.Entity) || !world.TryGet<BodyRef>(r.Entity, out var bodyRef)) continue;
            var body = FighterBodies.Handle(bodyRef);
            if (physics.IsAlive(body) && physics.BodyCount(body) > BodySegs.Bodies) physics.RemoveBodies(body, BodySegs.Bodies);
        }
    }
}

/// <summary>
/// React, реактивная по <see cref="LimbDetached"/>: отделение части у бойца — тела, суставы, пары, привязки скина, <see cref="Severance"/>;
/// живой без ноги, головы или торса — мёртв (<see cref="DeathCause.Severed"/>). Отделения одного бойца в тике — по порядку событий.
/// </summary>
public sealed class SeverAftermathSystem(IPhysicsWorld physics, CorpseJoints joints, SkinBindings skins, int corpseProfile)
    : IReactiveSystem<Event<LimbDetached>>
{
    private readonly JointDesc[] _standard = new JointDesc[CorpseJoints.Count];
    private readonly JointDesc[] _joints = new JointDesc[Severing.MaxBodies];
    private readonly BodyPair[] _pairs = new BodyPair[Severing.MaxPairs];
    private readonly Entity[] _victims = new Entity[CombatReserve.Contacts];
    private readonly Severance[] _states = new Severance[CombatReserve.Contacts];
    private readonly bool[] _killed = new bool[CombatReserve.Contacts];
    private int _count;

    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.SeverAftermath).Reads<Severance>().Reads<Dead>().Reads<BodyRef>().Reads<PoseRef>().Reads<Transform>()
            .Sets<Severance>().Adds<Severance>().Adds<Dead>()
            .ReserveCommands(CombatReserve.Contacts * 2, CombatReserve.Contacts, CombatReserve.Contacts * 512);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<LimbDetached>> events)
    {
        var world = ctx.World;
        _count = 0;
        foreach (var e in events) Apply(world, e.Value);
        var time = world.Resource<SimTime>();
        var cmd = ctx.Commands;
        for (int i = 0; i < _count; i++)
        {
            if (world.Has<Severance>(_victims[i])) cmd.Set(_victims[i], _states[i]);
            else cmd.Add(_victims[i], _states[i]);
            if (_killed[i]) cmd.Add(_victims[i], new Dead { Time = time.Time, Cause = DeathCause.Severed });
        }
    }

    private void Apply(World world, in LimbDetached ev)
    {
        var e = ev.Victim;
        if (!world.IsAlive(e) || !world.TryGet<BodyRef>(e, out var bodyRef) || !world.TryGet<PoseRef>(e, out var poseRef)
            || !world.TryGet<Transform>(e, out var root) || !BodySegs.IsFlesh(ev.Segment))
            return;
        var body = FighterBodies.Handle(bodyRef);
        int k = Slot(world, e);
        ref var sev = ref _states[k];
        if (!physics.IsAlive(body) || physics.BodyCount(body) != BodySegs.Bodies + sev.Pieces) return;
        if (sev.IsTorn(ev.Segment) || sev.IsDetached(ev.Segment) && ev.Mode == SeverMode.Tear) return;
        if (ev.Mode is SeverMode.Cut or SeverMode.Fracture && (sev.Pieces >= Severance.MaxPieces || ev.Piece != sev.Pieces)) return;
        if (ev.Mode == SeverMode.Shatter && (ev.Count < 1 || sev.Pieces + ev.Count > Severance.MaxPieces || ev.Piece != sev.Pieces)) return;
        var pool = world.Resource<PosePool>();
        var pose = new PoseHandle(poseRef.Index, poseRef.Generation);
        if (!pool.IsAlive(pose)) return;
        var model = pool.Model(pose);
        var fighterWorld = BodyPlacement.World(root.Position, root.Facing);
        bool corpse = Corpse.IsCorpse(physics, body);
        int s = ev.Segment;
        var bs = physics.Body(body, BodySegs.Body(s));
        var before = physics.Get(bs);
        ushort branch;
        if (ev.Mode is SeverMode.Cut or SeverMode.Fracture)
        {
            int piece = sev.Pieces;
            Cut(body, bs, before, ev, sev.Shift[s], corpse, piece);
            if (corpse) skins.CaptureGhost(physics, e, body, piece, s, model, fighterWorld, corpse: true, before);
            sev.Piece[piece] = new SeveredPiece { Segment = (byte)s, Mode = ev.Mode, Normal = ev.Normal, Offset = ev.Offset };
            sev.Pieces = piece + 1;
            sev.Cut |= (ushort)(1 << s);
            sev.Shift[s] = ev.StumpShift;
            // с куском уходят дети, чей сустав по его сторону от плоскости; остальные остаются на культе
            branch = (ushort)(ev.Carried & ~(1 << s));
            for (int c = 0; c < BodySegs.Count; c++)
                if ((branch & (1 << c)) != 0 && c != BodySegs.Pelvis && BodySegs.Parent(c) == s) sev.Holder[c] = (byte)(piece + 1);
            if (corpse) skins.Rebase(body, s, before, physics.Get(bs));
            else skins.CaptureGhost(physics, e, body, piece, s, model, fighterWorld, corpse: false, before);
            if (corpse && ev.Mode == SeverMode.Fracture)
                physics.SetJoint(body, physics.JointCount(body), Severing.FractureJoint(physics, body, s, BodySegs.Bodies + piece), default(JointBinding) with
                {
                    ParentAnchor = new Vector3(0, ev.StumpHalf, 0), Rest = Quaternion.Identity,
                });
        }
        else if (ev.Mode == SeverMode.Shatter)
        {
            int first = sev.Pieces;
            Shatter(body, bs, before, ev, sev.Shift[s], first);
            for (int i = 0; i < ev.Count; i++)
            {
                if (corpse) skins.CaptureGhost(physics, e, body, first + i, s, model, fighterWorld, corpse: true, before);
                else skins.CaptureGhost(physics, e, body, first + i, s, model, fighterWorld, corpse: false, before);
                sev.Piece[first + i] = new SeveredPiece { Segment = (byte)s, Mode = SeverMode.Shatter, Normal = ev.Normal, Offset = ev.Offset };
            }
            sev.Pieces = first + ev.Count;
            sev.Cut |= (ushort)(1 << s);
            sev.Shift[s] = ev.StumpShift;
            if (corpse) skins.Rebase(body, s, before, physics.Get(bs));
            // дети сегмента — оторваны: их суставу не на чем держаться
            branch = (ushort)(Severing.Branch(s) & ~(1 << s));
            for (int c = 0; c < BodySegs.Count; c++)
                if (c != BodySegs.Pelvis && BodySegs.Parent(c) == s)
                {
                    sev.Torn |= (ushort)(1 << c);
                    if (corpse) DisableJointOf(body, c);
                }
        }
        else
        {
            sev.Torn |= (ushort)(1 << s);
            branch = Severing.Branch(s);
            if (corpse) DisableJointOf(body, s);
            Push(bs, ev.Point, ev.Impulse);
        }
        ushort fresh = (ushort)(branch & ~sev.Detached), earlier = sev.Detached;
        sev.Detached |= branch;
        if (corpse) RebuildPairs(body, sev);
        else
        {
            // Живой: отделённое — на физику, суставы — заново по позе тика (кинематическим они безразличны), привязка скина отделённого.
            for (int seg = 0; seg < BodySegs.Count; seg++)
                if ((fresh & (1 << seg)) != 0) ToPhysics(physics.Body(body, BodySegs.Body(seg)));
            int n = Severing.Joints(physics, body, joints, model, fighterWorld, sev, _standard, _joints);
            Severing.SetJointsKeeping(physics, body, _joints.AsSpan(0, n), earlier);
            physics.SetSelfContacts(body, _pairs.AsSpan(0, Severing.SelfPairs(_joints.AsSpan(0, n), physics.BodyCount(body), sev, _pairs)));
            if (fresh != 0) skins.CaptureSegments(physics, e, body, model, fighterWorld, fresh);
            if (!world.Has<Dead>(e) && Severing.KillsLiving(s)) _killed[k] = true;
        }
    }

    /// <summary>
    /// Разрез: тело сегмента — культя (форма, центр по оси, скорость материи), новое тело — кусок на физике; суставы детей сегмента —
    /// на кусок с той же привязкой (кусок повёрнут как сегмент, сдвиг — вдоль оси); импульс клинка — куску.
    /// </summary>
    private void Cut(ArticulationHandle body, BodyHandle bs, in BodyState before, in LimbDetached ev, float oldShift, bool corpse, int piece)
    {
        var stump = Severing.Moved(before, Severing.AxisPoint(before, oldShift, ev.StumpShift));
        physics.SetShape(bs, ev.StumpMass, ev.StumpRadius, ev.StumpHalf);
        var shaped = physics.Get(bs);
        stump.Mass = shaped.Mass;
        stump.InvMass = shaped.InvMass;
        stump.InvInertia = shaped.InvInertia;
        stump.Radius = shaped.Radius;
        stump.HalfLength = shaped.HalfLength;
        physics.Set(bs, stump);

        int index = physics.AddBody(body);
        var pb = physics.Body(body, index);
        physics.SetShape(pb, ev.PieceMass, ev.PieceRadius, ev.PieceHalf);
        var shape = physics.Get(pb);
        var p = Severing.Moved(before, Severing.AxisPoint(before, oldShift, ev.PieceCenter));
        p.Mass = shape.Mass;
        p.InvMass = shape.InvMass;
        p.InvInertia = shape.InvInertia;
        p.Radius = shape.Radius;
        p.HalfLength = shape.HalfLength;
        p.Mode = BodyMode.Simulated;
        p.Profile = (byte)corpseProfile;
        p.Grounded = false;
        physics.Set(pb, p);
        Push(pb, ev.Point, ev.Impulse);

        if (!corpse) return;
        int s = BodySegs.Body(ev.Segment);
        for (int j = 0, n = physics.JointCount(body); j < n; j++)
        {
            var d = physics.Joint(body, j);
            var b = physics.Binding(body, j);
            bool enabled = physics.IsJointEnabled(body, j);   // замена сустава включает его — выпавший меч и оторванное остаются свободны
            if (d.Child == s)
                physics.SetJoint(body, j, d with { ChildAnchor = d.ChildAnchor - new Vector3(0, ev.StumpShift - oldShift, 0) }, b);
            else if (d.Parent == s && (d.Child >= BodySegs.Bodies || (ev.Carried & (1 << d.Child)) != 0))
                physics.SetJoint(body, j, d with { Parent = index }, new JointBinding(b.ParentAnchor - new Vector3(0, ev.PieceCenter - oldShift, 0), b.Rest));
            else if (d.Parent == s)   // ребёнок остался на культе: та же точка сустава от нового центра культи
                physics.SetJoint(body, j, d, new JointBinding(b.ParentAnchor - new Vector3(0, ev.StumpShift - oldShift, 0), b.Rest));
            if (!enabled) physics.SetJointEnabled(body, j, false);
        }
    }

    /// <summary>
    /// Клочья: тело сегмента — тонкая культя у сустава; ячейки (<see cref="ShatterCells"/>) — новые тела-шары с массой поровну, скорость —
    /// материи в центре ячейки, импульс удара — поровну, разлёт от оси — 1 м/с.
    /// </summary>
    private void Shatter(ArticulationHandle body, BodyHandle bs, in BodyState before, in LimbDetached ev, float oldShift, int first)
    {
        var stump = Severing.Moved(before, Severing.AxisPoint(before, oldShift, ev.StumpShift));
        physics.SetShape(bs, MathF.Max(ev.StumpMass, 0.05f), ev.StumpRadius, ev.StumpHalf);
        var shaped = physics.Get(bs);
        (stump.Mass, stump.InvMass, stump.InvInertia, stump.Radius, stump.HalfLength) =
            (shaped.Mass, shaped.InvMass, shaped.InvInertia, shaped.Radius, shaped.HalfLength);
        physics.Set(bs, stump);
        Span<Vector3> sites = stackalloc Vector3[Severance.MaxPieces];
        int count = Math.Min(ev.Count, Severance.MaxPieces);
        ShatterCells.Sites(ev.Seed, count, ev.From, ev.To, ev.PieceRadius, sites);
        float mass = MathF.Max(ev.PieceMass / count, 0.05f);
        float radius = Math.Clamp(MathF.Cbrt(3 * mass / (4 * MathF.PI * Severing.Density)), 0.02f, 0.09f);
        var axis = Vector3.Transform(Vector3.UnitY, before.Rotation);
        var boneCenter = before.Position - axis * oldShift;
        for (int i = 0; i < count; i++)
        {
            int index = physics.AddBody(body);
            var pb = physics.Body(body, index);
            physics.SetShape(pb, mass, radius, 0);
            var shape = physics.Get(pb);
            var at = boneCenter + Vector3.Transform(sites[i], before.Rotation);
            var p = Severing.Moved(before, at);
            var outward = Vector3.Transform(sites[i] with { Y = 0 }, before.Rotation);
            if (outward.LengthSquared() > 1e-8f) p.Velocity += Vector3.Normalize(outward) * Severing.ShatterSpread;
            p.Velocity += ev.Impulse / MathF.Max(ev.PieceMass, 0.05f);
            // Инерция клочка — не меньше, чем у шара 6 см той же массы: мелкий шар от трения о землю раскручивался до сотен рад/с.
            float floor = 1 / (0.4f * mass * Severing.MinInertiaRadius * Severing.MinInertiaRadius);
            var inertia = Vector3.Min(shape.InvInertia, new Vector3(floor));
            (p.Mass, p.InvMass, p.InvInertia, p.Radius, p.HalfLength) = (shape.Mass, shape.InvMass, inertia, shape.Radius, shape.HalfLength);
            p.Mode = BodyMode.Simulated;
            p.Profile = (byte)corpseProfile;
            p.Grounded = false;
            physics.Set(pb, p);
        }
    }

    /// <summary>
    /// Импульс удара отделённому — в точке касания (вне шага), скорость и вращение — не выше потолков профиля трупа: шаг урезает их только
    /// внутри подшага, и лёгкий оторванный сегмент успел бы закрутиться сверх них.
    /// </summary>
    private void Push(BodyHandle b, Vector3 point, Vector3 impulse)
    {
        physics.ApplyHit(b, point, impulse, 0, ArticulationHandle.None);
        var d = physics.GetDynamics(corpseProfile);
        var s = physics.Get(b);
        float v = s.Velocity.Length(), w = s.AngularVelocity.Length();
        if (!(v > d.MaxSpeed) && !(w > d.MaxAngularSpeed)) return;
        if (v > d.MaxSpeed) s.Velocity *= d.MaxSpeed / v;
        if (w > d.MaxAngularSpeed) s.AngularVelocity *= d.MaxAngularSpeed / w;
        physics.Set(b, s);
    }

    private void DisableJointOf(ArticulationHandle body, int segment)
    {
        for (int j = 0, n = physics.JointCount(body); j < n; j++)
            if (physics.Joint(body, j).Child == BodySegs.Body(segment)) physics.SetJointEnabled(body, j, false);
    }

    /// <summary>Пары трупа после отделения: по включённым суставам сочленения.</summary>
    private void RebuildPairs(ArticulationHandle body, in Severance severance)
    {
        int n = 0;
        for (int j = 0, count = physics.JointCount(body); j < count; j++)
            if (physics.IsJointEnabled(body, j)) _joints[n++] = physics.Joint(body, j);
        physics.SetSelfContacts(body, _pairs.AsSpan(0, Severing.SelfPairs(_joints.AsSpan(0, n), physics.BodyCount(body), severance, _pairs)));
    }

    private void ToPhysics(BodyHandle b)
    {
        var s = physics.Get(b);
        if (s.Mode == BodyMode.Simulated || !(s.Mass > 0)) return;
        physics.SetMode(b, BodyMode.Simulated);
        physics.SetDynamics(b, corpseProfile);
    }

    /// <summary>Накопление тика по бойцу: состояние отделений — итог прошлых событий тика.</summary>
    private int Slot(World world, Entity e)
    {
        for (int i = 0; i < _count; i++)
            if (_victims[i] == e) return i;
        _victims[_count] = e;
        _states[_count] = world.TryGet<Severance>(e, out var s) ? s : default;
        _killed[_count] = false;
        return _count++;
    }
}
