using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Инварианты физики (раздел 8 плана, этап 5), их регистрирует ядро рядом с участником хеша <c>physics</c>:
/// <c>physics.finite</c> — после стадии Physics (тела ставятся при постройке в Gameplay и по позе в PrePhysics — 5б, а стадия
/// Physics до этапа 7 пуста: после неё видно ровно то, что поставил PrePhysics; шаг физики — этап 7);
/// <c>physics.kinematicPose</c> и <c>physics.kinematicSpeed</c> — после PrePhysics (5б: капсулы живого бойца стоят на суставах
/// позы тика, их скорости правдоподобны); <c>physics.bodyRefs</c> — в конце End, когда хуки всех барьеров тика отработали.
/// Проверки внутри шага — с шагом (этап 7).
/// </summary>
public static class PhysicsInvariants
{
    /// <summary>Отклонение квадрата длины поворота от 1, которое ещё не ошибка (как у поз).</summary>
    public const float UnitTolerance = 1e-3f;

    /// <summary>
    /// <c>physics.kinematicPose</c>: центр капсулы — не дальше <see cref="CenterTolerance"/> от середины её суставов, м, а
    /// косинус угла между осью и направлением между ними — не меньше 1 − <see cref="AxisTolerance"/> (постановка — те же
    /// выражения, в исправной игре отклонение 0).
    /// </summary>
    public const float CenterTolerance = 1e-4f, AxisTolerance = 1e-6f;

    /// <summary>
    /// <c>physics.kinematicSpeed</c>: предел скорости капсулы живого бойца относительно самого бойца (за вычетом переноса и
    /// поворота мира бойца за тик — корень, курс, высота рельефа), м/с, и её угловой скорости, рад/с. Анимация даёт меньше
    /// (замер — README фичи «Физика и тела»); больше — капсула перескочила (постройка или сброс без постановки, чужая поза,
    /// неверный шаг). Толчки корней и колонн, ступени рельефа и поворот курса — перенос мира бойца, в предел не входят.
    /// </summary>
    public const float MaxRelativeSpeed = 30f, MaxAngularSpeed = 150f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics, PosePool poses, BodyJoints joints, StartPoseModel start) =>
        [new Finite(physics), new KinematicPose(physics, poses, joints, start), new KinematicSpeed(physics), new BodyRefs(physics)];

    /// <summary>
    /// Скорость капсулы относительно бойца, м/с: прошлая постановка, перенесённая вместе с миром бойца из прошлого корня и
    /// курса (<paramref name="prevRoot"/>, <paramref name="prevFacing"/>) в нынешние, против нынешней — за <paramref name="dt"/>.
    /// </summary>
    public static float RelativeSpeed(in BodyState s, Vector3 prevRoot, float prevFacing, Vector3 root, float facing, float dt)
    {
        var carry = Frame(prevRoot, prevFacing, root, facing);
        return Vector3.Distance(s.Position, Vector3.Transform(s.PrevPosition, carry)) / dt;
    }

    /// <summary>Перенос мира бойца за тик: из прошлого корня и курса в нынешние (вектор слева: точка · обратный прошлый · нынешний).</summary>
    public static Matrix4x4 Frame(Vector3 prevRoot, float prevFacing, Vector3 root, float facing)
    {
        Matrix4x4.Invert(BodyPlacement.World(prevRoot, prevFacing), out var back);
        return back * BodyPlacement.World(root, facing);
    }

    /// <summary>
    /// <c>physics.finite</c> — у живых тел конечны положение, поворот, прошлая постановка, скорости и форма; поворот —
    /// единичной длины; форма не отрицательна.
    /// </summary>
    private sealed class Finite(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.finite";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (исправления после Пд)
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
            if (!Finite3(s.Velocity)) return $"скорость {Text(s.Velocity)}";
            if (!Finite3(s.AngularVelocity)) return $"угловая скорость {Text(s.AngularVelocity)}";
            if (Unit(s.Rotation) is { } rot) return $"поворот {rot}";
            if (Unit(s.PrevRotation) is { } prev) return $"прошлый поворот {prev}";
            if (!float.IsFinite(s.Radius) || !float.IsFinite(s.HalfLength) || s.Radius < 0 || s.HalfLength < 0)
                return string.Create(CultureInfo.InvariantCulture, $"форма: радиус {s.Radius}, полудлина {s.HalfLength}");
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
    /// <c>physics.kinematicPose</c> — капсулы живого бойца стоят на суставах его позы этого тика (после PrePhysics): центр
    /// капсулы — середина её двух точек (<see cref="BodyPlacement.Ends"/>) в мире бойца (корень, курс, высота рельефа), ось —
    /// вдоль направления между ними. Ловит постановку, которая не прошла (капсулы остались на прошлой позе или корне),
    /// перепутанные тела (<see cref="BodyRef"/> двух бойцов обменялись) и тело, сдвинутое после постановки. Бойцы без живой позы
    /// или с негодным телом пропускаются (их ловят <c>animation.*</c>, <c>physics.bodyRefs</c>). До первого тика (проверка
    /// «сейчас» на свежем сеансе, после <c>time.seek 0</c>) капсулы сверяются с позой постройки (<see cref="StartPose"/>): по ней
    /// их поставила сборка мира (5в), а поза бойца до первого тика — поза привязки (её считает первый тик).
    /// </summary>
    private sealed class KinematicPose(IPhysicsWorld physics, PosePool poses, BodyJoints joints, StartPoseModel start) : IInvariant
    {
        private ReadQuery? _query;

        public string Id => "physics.kinematicPose";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (исправления после Пд)
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (NotPlacedYet(ctx)) return;
            var world = ctx.World;
            // До первого тика тела стоят по позе постройки (сборка мира), а не по позе бойца: её ещё не считал ни один тик.
            bool built = BeforeFirstTick(ctx);
            var buildModel = built ? start.Of(world.Resource<StartPose>()) : default;
            _query ??= world.Query().With<Fighter>().Read<Transform>().Read<PoseRef>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_query))
            {
                var entities = chunk.Entities;
                var bodies = chunk.Read<BodyRef>();
                var roots = chunk.Read<Transform>();
                var refs = chunk.Read<PoseRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var pose = new PoseHandle(refs[i].Index, refs[i].Generation);
                    var body = FighterBodies.Handle(bodies[i]);
                    if (!poses.IsAlive(pose) || !BodyPlacement.Placeable(physics, body)) continue;
                    var model = built ? buildModel : poses.Model(pose);
                    var w = BodyPlacement.World(roots[i].Position, roots[i].Facing);
                    for (int k = 0; k < BodySegs.Bodies; k++)
                    {
                        int segment = BodySegs.Segment(k);
                        var (a, b) = BodyPlacement.Ends(model, joints, w, segment);
                        var s = physics.Get(physics.Body(body, k));
                        var dir = b - a;
                        if (dir.LengthSquared() < 1e-8f) dir = Vector3.UnitY;
                        float off = Vector3.Distance(s.Position, (a + b) * 0.5f);
                        float dot = Vector3.Dot(s.Axis, Vector3.Normalize(dir));
                        // нечисловое — дело physics.finite (оно же покажет, что именно)
                        if (off <= CenterTolerance && dot >= 1 - AxisTolerance || !float.IsFinite(off) || !float.IsFinite(dot)) continue;
                        float angle = MathF.Acos(Math.Clamp(dot, -1, 1)) * 180 / MathF.PI;
                        violations.Report(entities[i], string.Create(CultureInfo.InvariantCulture,
                            $"капсула {BodySegs.Name(segment)} ({BodySegs.Title(segment)}) не на суставах позы: центр в {off:G4} м от середины, ось отклонена на {angle:G4}°"));
                        if (violations.Full) return;
                        break;   // одна строка на бойца
                    }
                }
            }
        }

        /// <summary>
        /// Постановки по позе этого тика ещё не было — сверять капсулы не с чем (проверка «сейчас», <c>invariant.run</c>):
        /// посреди тика до PrePhysics (после <c>time.break</c>: корень, курс и поза уже этого тика, капсулы — ещё прошлого). В
        /// тике проверка идёт после PrePhysics и не пропускается; до первого тика — сверка с позой постройки
        /// (<see cref="BeforeFirstTick"/>).
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
    /// <c>physics.kinematicSpeed</c> — капсулы живого бойца не перескакивают (после PrePhysics): скорость каждой капсулы
    /// относительно бойца (<see cref="RelativeSpeed"/>: перенос и поворот мира бойца за тик — корень из <see cref="PrevTransform"/>
    /// в <see cref="Transform"/> — вычтены) не больше <see cref="MaxRelativeSpeed"/>, угловая — не больше
    /// <see cref="MaxAngularSpeed"/>. Ловит скачок, который постановка приняла за движение: постройка или сброс без
    /// постановки при постройке (прошлая постановка — там, где тело стояло до постройки), чужая или испорченная поза, неверный
    /// шаг. Толчки корней при спауне внахлёст, колонны, ступени и поворот курса — движение мира бойца, их предел не видит.
    /// </summary>
    private sealed class KinematicSpeed(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _query;

        public string Id => "physics.kinematicSpeed";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (исправления после Пд)
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            float dt = world.Resource<SimTime>().Dt;
            _query ??= world.Query().With<Fighter>().Read<Transform>().Read<PrevTransform>().Read<BodyRef>();
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
                        float v = Vector3.Distance(s.Position, Vector3.Transform(s.PrevPosition, carry)) / dt, w = s.AngularVelocity.Length();
                        if (v <= MaxRelativeSpeed && w <= MaxAngularSpeed || !float.IsFinite(v) || !float.IsFinite(w)) continue;   // нечисловое — physics.finite
                        int segment = BodySegs.Segment(k);
                        violations.Report(entities[i], string.Create(CultureInfo.InvariantCulture,
                            $"капсула {BodySegs.Name(segment)} ({BodySegs.Title(segment)}): скорость относительно бойца {v:G4} м/с (предел {MaxRelativeSpeed}), угловая {w:G4} рад/с (предел {MaxAngularSpeed}); скорость {s.Velocity.Length():G4} м/с"));
                        if (violations.Full) return;
                        break;   // одна строка на бойца
                    }
                }
            }
        }
    }

    /// <summary>
    /// <c>physics.bodyRefs</c> — тела и их владельцы согласованы: у каждого бойца есть тело (<see cref="BodyRef"/>) из
    /// <see cref="BodySegs.Bodies"/> капсул; каждый <see cref="BodyRef"/> указывает на живое сочленение; у сочленения ровно
    /// один владелец — нет ни общих, ни ничьих (тело, которое никто не освободит). В конце End.
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
                        if (physics.BodyCount(a) == BodySegs.Bodies || !world.Has<Fighter>(entities[i])) continue;
                        violations.Report(entities[i], $"у бойца сочленение {a} из {physics.BodyCount(a)} тел, а нужно {BodySegs.Bodies}");
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
