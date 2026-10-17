using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Инварианты физики живых тел: <c>physics.finite</c> — после Physics; <c>physics.kinematicPose</c> и
/// <c>physics.kinematicSpeed</c> — после PrePhysics; <c>physics.bodyRefs</c> — в конце End, когда хуки всех барьеров отработали.
/// Инварианты тел на физике — <see cref="Ragdoll.CorpseInvariants"/>.
/// </summary>
public static class PhysicsInvariants
{
    /// <summary>Отклонение квадрата длины поворота от 1, которое ещё не ошибка (как у поз).</summary>
    public const float UnitTolerance = 1e-3f;

    /// <summary>
    /// <c>physics.kinematicPose</c>: центр капсулы — не дальше <see cref="CenterTolerance"/> м от середины её суставов, косинус угла
    /// оси — не меньше 1 − <see cref="AxisTolerance"/> (постановка — те же выражения, отклонение 0).
    /// </summary>
    public const float CenterTolerance = 1e-4f, AxisTolerance = 1e-6f;

    /// <summary>
    /// <c>physics.kinematicSpeed</c>: предел скорости капсулы живого бойца относительно самого бойца, м/с, и угловой поперёк оси,
    /// рад/с (вращение вокруг оси не считается: опорная ось капсулы в ударах вырождается и даёт до 333 рад/с). Больше — капсула
    /// перескочила (постройка без постановки, чужая поза, неверный шаг).
    /// </summary>
    public const float MaxRelativeSpeed = 30f, MaxAngularSpeed = 150f;

    /// <summary>
    /// <c>physics.kinematicSpeed</c>: предел скорости тела клинка относительно бойца, м/с — в ударе он быстрее руки (до 49,4 м/с);
    /// ~2× к худшему.
    /// </summary>
    public const float MaxBladeSpeed = 100f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics, PosePool poses, BodyJoints joints, StartPoseModel start, WeaponTable weapons,
        int hand) =>
        [new Finite(physics), new KinematicPose(physics, poses, joints, start, weapons, hand), new KinematicSpeed(physics), new BodyRefs(physics)];

    /// <summary>Перенос мира бойца за тик: из прошлого корня и курса в нынешние (вектор слева: точка · обратный прошлый · нынешний).</summary>
    public static Matrix4x4 Frame(Vector3 prevRoot, float prevFacing, Vector3 root, float facing)
    {
        Matrix4x4.Invert(BodyPlacement.World(prevRoot, prevFacing), out var back);
        return back * BodyPlacement.World(root, facing);
    }

    /// <summary>
    /// <c>physics.finite</c> — у живых тел конечны положение, поворот, прошлая постановка, поза начала тика, скорости и форма; повороты —
    /// единичной длины; форма не отрицательна; масса, обратная масса и обратная инерция конечны и не отрицательны.
    /// </summary>
    private sealed class Finite(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.finite";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            for (int i = 0, n = physics.BodyCapacity; i < n; i++)
            {
                var b = physics.BodyAt(i);
                if (b.IsNone) continue;
                var s = physics.Get(b);
                if (Problem(s) is not { } problem) continue;
                violations.Report($"тело {b} (сочленение {physics.ArticulationOf(b)}): {problem}");
                if (violations.Full) return;
            }
        }

        private static string? Problem(in BodyState s)
        {
            if (!Finite3(s.Position)) return $"положение {Text(s.Position)}";
            if (!Finite3(s.PrevPosition)) return $"прошлое положение {Text(s.PrevPosition)}";
            if (!Finite3(s.StartPosition)) return $"положение начала тика {Text(s.StartPosition)}";
            if (!Finite3(s.Velocity)) return $"скорость {Text(s.Velocity)}";
            if (!Finite3(s.AngularVelocity)) return $"угловая скорость {Text(s.AngularVelocity)}";
            if (Unit(s.Rotation) is { } rot) return $"поворот {rot}";
            if (Unit(s.PrevRotation) is { } prev) return $"прошлый поворот {prev}";
            if (Unit(s.StartRotation) is { } start) return $"поворот начала тика {start}";
            if (!float.IsFinite(s.Radius) || !float.IsFinite(s.HalfLength) || s.Radius < 0 || s.HalfLength < 0)
                return string.Create(CultureInfo.InvariantCulture, $"форма: радиус {s.Radius}, полудлина {s.HalfLength}");
            if (!(float.IsFinite(s.Mass) && s.Mass >= 0 && float.IsFinite(s.InvMass) && s.InvMass >= 0) || !Finite3(s.InvInertia)
                || s.InvInertia.X < 0 || s.InvInertia.Y < 0 || s.InvInertia.Z < 0)
                return string.Create(CultureInfo.InvariantCulture, $"масса {s.Mass}, обратная {s.InvMass}, обратная инерция {Text(s.InvInertia)}");
            return null;
        }

        private static bool Finite3(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        private static string? Unit(Quaternion q)
        {
            float len = q.LengthSquared();
            return float.IsFinite(len) && MathF.Abs(len - 1) <= UnitTolerance ? null
                : string.Create(CultureInfo.InvariantCulture, $"({q.X}, {q.Y}, {q.Z}, {q.W}), |q|² = {len:G6}");
        }

        private static string Text(Vector3 v) => string.Create(CultureInfo.InvariantCulture, $"({v.X}, {v.Y}, {v.Z})");
    }

    /// <summary>
    /// <c>physics.kinematicPose</c> — капсулы живого бойца стоят на суставах его позы тика (<see cref="BodyPlacement.Ends"/>), тело
    /// клинка — от хвата до кончика меча в руке (<see cref="BladePlacement.BodyOf"/>). Ловит непрошедшую постановку и перепутанные
    /// тела. Мёртвые и бойцы без живой позы пропускаются; до первого тика — сверка с позой постройки (<see cref="FighterStartPose"/>
    /// или <see cref="StartPose"/>).
    /// </summary>
    private sealed class KinematicPose(IPhysicsWorld physics, PosePool poses, BodyJoints joints, StartPoseModel start, WeaponTable weapons,
        int hand) : IInvariant
    {
        private ReadQuery? _query;

        public string Id => "physics.kinematicPose";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (NotPlacedYet(ctx)) return;
            var world = ctx.World;
            // До первого тика тела стоят по позе постройки (сборка мира), а не по позе бойца: её ещё не считал ни один тик.
            bool built = BeforeFirstTick(ctx);
            _query ??= world.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<Transform>().Read<PoseRef>().Read<BodyRef>().Read<WeaponSlot>();
            foreach (var chunk in world.Iterate(_query))
            {
                var entities = chunk.Entities;
                var bodies = chunk.Read<BodyRef>();
                var roots = chunk.Read<Transform>();
                var refs = chunk.Read<PoseRef>();
                var slots = chunk.Read<WeaponSlot>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var pose = new PoseHandle(refs[i].Index, refs[i].Generation);
                    var body = FighterBodies.Handle(bodies[i]);
                    if (!poses.IsAlive(pose) || !BodyPlacement.Placeable(physics, body)) continue;
                    // Поза постройки — своя у бойца (FighterStartPose) или общая
                    var model = built ? start.Of(StartPoseModel.For(world, entities[i])) : poses.Model(pose);
                    var severance = world.TryGet<Severance>(entities[i], out var sv) ? sv : default;
                    if (Problem(model, roots[i], body, slots[i].Index, severance) is not { } problem) continue;
                    violations.Report(entities[i], problem);   // одна строка на бойца
                    if (violations.Full) return;
                }
            }
        }

        /// <summary>Первая капсула бойца не на позе (тело цикла по бойцам — свой метод); все на месте — null.</summary>
        private string? Problem(ReadOnlySpan<Matrix4x4> model, in Transform root, ArticulationHandle body, int weapon, in Severance severance)
        {
            var w = BodyPlacement.World(root.Position, root.Facing);
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                int segment = BodySegs.Segment(k);
                var s = physics.Get(physics.Body(body, k));
                if (s.Mode != BodyMode.Kinematic) continue;   // тело на физике ставит шаг, не поза
                Vector3 a, b;
                if (BodySegs.IsFlesh(segment)) (a, b) = BodyPlacement.Ends(model, joints, w, segment);
                else
                {
                    if ((uint)weapon >= (uint)weapons.Count) continue;   // меч вне таблицы — ловит combat.weapons
                    var handWorld = model[hand] * w;
                    (a, b) = (handWorld.Translation, Vector3.Transform(weapons.Weapons[weapon].BladeTip, handWorld));
                    float want = weapons.Bodies[weapon].HalfLength;
                    if (MathF.Abs(s.HalfLength - want) > CenterTolerance)   // нечисловая — дело physics.finite
                        return string.Create(CultureInfo.InvariantCulture,
                            $"капсула blade (клинок): полудлина {s.HalfLength:G6} м, а у меча в руке ({weapons.Weapons[weapon].Name}) — {want:G6}");
                }
                var dir = b - a;
                if (dir.LengthSquared() < 1e-8f) dir = Vector3.UnitY;
                // культя — центр сдвинут по оси сегмента (Severance.Shift)
                var mid = (a + b) * 0.5f + (severance.IsCut(segment) ? Vector3.Normalize(dir) * severance.Shift[segment] : Vector3.Zero);
                float off = Vector3.Distance(s.Position, mid);
                float dot = Vector3.Dot(s.Axis, Vector3.Normalize(dir));
                // нечисловое — дело physics.finite (оно же покажет, что именно)
                if (off <= CenterTolerance && dot >= 1 - AxisTolerance || !float.IsFinite(off) || !float.IsFinite(dot)) continue;
                float angle = MathF.Acos(Math.Clamp(dot, -1, 1)) * 180 / MathF.PI;
                return string.Create(CultureInfo.InvariantCulture,
                    $"капсула {BodySegs.Name(segment)} ({BodySegs.Title(segment)}) не на суставах позы: центр в {off:G4} м от середины, ось отклонена на {angle:G4}°");
            }
            return null;
        }

        /// <summary>
        /// Постановки по позе этого тика ещё не было — сверять не с чем: проверка «сейчас» посреди тика до PrePhysics.
        /// </summary>
        private static bool NotPlacedYet(in InvariantContext ctx)
        {
            var stages = GameStages.All;
            for (int i = 0; i < stages.Count; i++)
                if (stages[i] == ctx.Stage) return i < PrePhysicsIndex;
            return false;   // между тиками
        }

        /// <summary>Между тиками (не стадия игры) до первого тика: тела бойцов сборки мира стоят по позе постройки.</summary>
        private static bool BeforeFirstTick(in InvariantContext ctx)
        {
            var stages = GameStages.All;
            for (int i = 0; i < stages.Count; i++)
                if (stages[i] == ctx.Stage) return false;
            return ctx.Tick == 0;
        }

        private static readonly int PrePhysicsIndex = IndexOf(GameStages.PrePhysics);

        private static int IndexOf(string stage)
        {
            for (int i = 0; i < GameStages.All.Count; i++)
                if (GameStages.All[i] == stage) return i;
            throw new InvalidOperationException($"нет стадии {stage}");
        }
    }

    /// <summary>
    /// <c>physics.kinematicSpeed</c> — скорость капсулы живого бойца относительно бойца (перенос и поворот бойца вычтены) не больше
    /// <see cref="MaxRelativeSpeed"/> (клинок — <see cref="MaxBladeSpeed"/>), угловая поперёк оси (<see cref="Swing"/>) — не больше
    /// <see cref="MaxAngularSpeed"/>. Только кинематические тела.
    /// </summary>
    private sealed class KinematicSpeed(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _query;

        /// <summary>
        /// Угловая скорость капсулы поперёк её оси, рад/с: вращение вокруг оси капсулу не сдвигает.
        /// </summary>
        public static float Swing(in BodyState s)
        {
            var axis = s.Axis;
            return (s.AngularVelocity - axis * Vector3.Dot(s.AngularVelocity, axis)).Length();
        }

        public string Id => "physics.kinematicSpeed";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            float dt = world.Resource<SimTime>().Dt;
            // мёртвый замер: его капсулы хранят скорости последнего тика жизни (для рэгдолла) — это не скачок постановки
            _query ??= world.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<Transform>().Read<PrevTransform>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_query))
            {
                var entities = chunk.Entities;
                var bodies = chunk.Read<BodyRef>();
                var roots = chunk.Read<Transform>();
                var prevs = chunk.Read<PrevTransform>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var body = FighterBodies.Handle(bodies[i]);
                    if (!BodyPlacement.Placeable(physics, body)) continue;
                    var carry = Frame(prevs[i].Position, prevs[i].Facing, roots[i].Position, roots[i].Facing);
                    for (int k = 0; k < BodySegs.Bodies; k++)
                    {
                        var s = physics.Get(physics.Body(body, k));
                        if (s.Mode != BodyMode.Kinematic) continue;   // тело на физике — physics.simulatedSpeed
                        float v = Vector3.Distance(s.Position, Vector3.Transform(s.PrevPosition, carry)) / dt, w = Swing(s);
                        int segment = BodySegs.Segment(k);
                        float limit = BodySegs.IsFlesh(segment) ? MaxRelativeSpeed : MaxBladeSpeed;
                        if (v <= limit && w <= MaxAngularSpeed || !float.IsFinite(v) || !float.IsFinite(w)) continue;   // нечисловое — physics.finite
                        violations.Report(entities[i], string.Create(CultureInfo.InvariantCulture,
                            $"капсула {BodySegs.Name(segment)} ({BodySegs.Title(segment)}): скорость относительно бойца {v:G4} м/с (предел {limit}), угловая {w:G4} рад/с (предел {MaxAngularSpeed}); скорость {s.Velocity.Length():G4} м/с"));
                        if (violations.Full) return;
                        break;   // одна строка на бойца
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>physics.bodyRefs</c> — у каждого бойца есть тело из <see cref="BodySegs.Bodies"/> капсул, каждый <see cref="BodyRef"/>
    /// указывает на живое сочленение, у сочленения ровно один владелец. В конце End.
    /// </summary>
    private sealed class BodyRefs(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _refs, _bare;
        private Entity[] _owner = [];

        public string Id => "physics.bodyRefs";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            _refs ??= world.Query().Read<BodyRef>();
            _bare ??= world.Query().With<Fighter>().Without<BodyRef>();
            int capacity = physics.ArticulationCapacity;
            if (_owner.Length < capacity) _owner = new Entity[capacity];
            else Array.Clear(_owner, 0, capacity);

            foreach (var chunk in world.Iterate(_bare))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "боец без тела (BodyRef)");
                    if (violations.Full) return;
                }
            foreach (var chunk in world.Iterate(_refs))
            {
                var entities = chunk.Entities;
                var refs = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var a = FighterBodies.Handle(refs[i]);
                    if (!physics.IsAlive(a)) violations.Report(entities[i], $"BodyRef {a} — сочленение не живо");
                    else if (!_owner[a.Index].IsNull) violations.Report(entities[i], $"сочленение {a} — ещё и у {_owner[a.Index]}");
                    else
                    {
                        _owner[a.Index] = entities[i];
                        int need = BodySegs.Bodies + (world.TryGet<Severance>(entities[i], out var severance) ? severance.Pieces : 0);
                        if (physics.BodyCount(a) == need || !world.Has<Fighter>(entities[i])) continue;
                        violations.Report(entities[i], $"у бойца сочленение {a} из {physics.BodyCount(a)} тел, а нужно {need} (кусков — {need - BodySegs.Bodies})");
                    }
                    if (violations.Full) return;
                }
            }
            for (int i = 0; i < capacity; i++)
            {
                var a = physics.ArticulationAt(i);
                if (a.IsNone || !_owner[i].IsNull) continue;
                violations.Report($"сочленение {a} ({physics.BodyCount(a)} тел) без владельца — его никто не освободит");
                if (violations.Full) return;
            }
        }
    }
}
