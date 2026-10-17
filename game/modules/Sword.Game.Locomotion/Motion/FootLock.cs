using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Фиксация стопы (мир, XZ): держит ли стопа опору, точки пятки (голеностоп) и носка и курс стопы в мире; смещения от анимации —
/// цели голеностопа, курса и наклона стопы — со скоростями (инерциализация после опоры). Нули — стопа как в анимации.
/// </summary>
public struct FootLockLeg
{
    /// <summary>Стопа в опоре: точки <see cref="Heel"/>, <see cref="Ball"/> и курс <see cref="Yaw"/> держатся в мире.</summary>
    public bool Locked;

    /// <summary>Голеностоп и носок в мире (x, z) и курс стопы (голеностоп → носок), рад.</summary>
    public Vector2 Heel, Ball;

    /// <inheritdoc cref="Heel"/>
    public float Yaw;

    /// <summary>Цель голеностопа − голеностоп анимации, мир (x, z), м, и её скорость, м/с.</summary>
    public Vector2 Offset, Velocity;

    /// <summary>Курс стопы − курс стопы анимации, рад, и его скорость, рад/с.</summary>
    public float YawOffset, YawVelocity;

    /// <summary>Наклон стопы (голеностоп → носок над горизонталью) − у анимации, рад, и его скорость, рад/с.</summary>
    public float Pitch, PitchVelocity;

    /// <summary>Правка нулевая: IK этой ноги не нужен (поза — анимация бит в бит).</summary>
    public readonly bool Idle => Offset == Vector2.Zero && YawOffset == 0 && Pitch == 0;
}

/// <summary>Фиксация обеих стоп бойца (часть <see cref="MotionMatchState"/>; участник <c>ecs</c>).</summary>
public struct FootLockState
{
    public FootLockLeg Left, Right;
}

/// <summary>
/// Пределы фиксации: смещение цели голеностопа от анимации, м (дальше точки опоры тянутся за анимацией — стопа едет, но не
/// рвётся); смещение курса стопы, рад; полупериод возврата к анимации после опоры, с; высоты «у земли» голеностопа и носка в
/// модели, м (выше — не опора, даже если кадр базы говорит «стоит»: позу ещё догоняет переход); смещение цели внутрь — к другой ноге,
/// м (дальше точки опоры тянутся за анимацией: удержанная стопа не перекрещивает ноги, когда тело уходит вбок — бег по малому кругу).
/// </summary>
public readonly record struct FootLockRules(float MaxOffset, float MaxYaw, float Halflife, float HeelHeight, float BallHeight,
    float MaxInward = float.PositiveInfinity);

/// <summary>
/// Фиксация опорной стопы: стопа, которую кадр базы MM называет опорной, держит в мире место и курс, с которыми встала; голеностоп
/// тянется к цели IK ноги. Стоит на пятке — держится голеностоп; на носке (пятка поднята) — носок, стопа поворачивается вокруг
/// него; на всей стопе — оба, наклон стопы подгоняется так, чтобы носок не ехал. Опора кончилась — смещения гаснут к анимации
/// пружиной. Чистая логика без мира и пула.
/// </summary>
public static class FootLock
{
    /// <summary>Смещения меньше — ровно ноль (IK ноги больше не ставится).</summary>
    public const float Rest = 1e-4f;

    /// <summary>
    /// Шаг стопы на тик: <paramref name="knee"/>, <paramref name="ankle"/>, <paramref name="ball"/> — колено, голеностоп и носок анимации
    /// в мире (x, z; y — высота в позе); опора — <paramref name="heel"/> (пятка у земли) и <paramref name="toe"/> (носок у земли). После
    /// шага цель голеностопа — <c>ankle + Offset</c>, стопа — анимации, повёрнутая <see cref="FootRotation"/> на <c>YawOffset</c> и
    /// <c>Pitch</c>. <paramref name="inward"/> — направление к другой ноге (мир, x, z, единичное; ноль — без предела внутрь).
    /// </summary>
    public static void Step(ref FootLockLeg s, Vector3 knee, Vector3 ankle, Vector3 ball, bool heel, bool toe, float dt, in FootLockRules rules,
        Vector2 inward = default)
    {
        if (!(dt > 0)) return;
        if ((heel || toe) && rules.MaxOffset > 0)
            Hold(ref s, ankle - knee, ankle, ball, heel, toe, dt, rules, inward);
        else
        {
            Free(ref s, dt, rules.Halflife);
            // Стопа в махе тоже не заходит за предел внутрь: смещение, с которым она оторвалась, гаснет — но не поперёк к другой ноге.
            float across = Vector2.Dot(s.Offset, inward);
            if (across > rules.MaxInward)
            {
                s.Offset -= inward * (across - rules.MaxInward);
                s.Velocity -= inward * MathF.Max(0, Vector2.Dot(s.Velocity, inward));
            }
        }
    }

    private static void Hold(ref FootLockLeg s, Vector3 shin, Vector3 ankle, Vector3 ball, bool heel, bool toe, float dt, in FootLockRules rules,
        Vector2 inward)
    {
        var foot = ball - ankle;
        float flat = new Vector2(foot.X, foot.Z).Length(), length = foot.Length();
        float animYaw = flat > 1e-6f ? MathF.Atan2(foot.X, foot.Z) : s.Yaw - s.YawOffset;
        float animPitch = MathF.Atan2(foot.Y, flat);
        if (!s.Locked)
        {
            // Встала — с видимого места: анимация + смещения, которые ещё не погасли.
            s.Locked = true;
            s.Yaw = animYaw + s.YawOffset;
            s.Heel = new Vector2(ankle.X, ankle.Z) + s.Offset;
            s.Ball = s.Heel + Turned(foot, shin, s.YawOffset, s.Pitch);
        }
        float yaw, pitch;
        if (heel && toe)
        {
            // На всей стопе: держатся обе точки — курс и наклон стопы из них (длина по горизонтали та же: носок не едет).
            var held = s.Ball - s.Heel;
            yaw = held.LengthSquared() > 1e-12f ? Pose2.Wrap(MathF.Atan2(held.X, held.Y) - animYaw) : s.YawOffset;
            float cos = length > 1e-6f ? Math.Clamp(held.Length() / length, 0, 0.9999f) : 1;
            pitch = MathF.CopySign(MathF.Acos(cos), animPitch + s.Pitch) - animPitch;
        }
        else if (heel)
        {
            // На пятке: держится курс стопы, наклон — анимации (носок опускается или поднимается сам).
            yaw = Pose2.Wrap(s.Yaw - animYaw);
            pitch = Decay(s.Pitch, s.PitchVelocity, rules.Halflife, dt);
        }
        else
        {
            // На носке: стопа поворачивается вокруг него, как в анимации, — курс и наклон гаснут (у почти отвесной стопы курс
            // по горизонтали шумит, держать его — дёргать голеностоп вокруг носка).
            yaw = Decay(s.YawOffset, s.YawVelocity, rules.Halflife, dt);
            pitch = Decay(s.Pitch, s.PitchVelocity, rules.Halflife, dt);
        }
        yaw = Math.Clamp(yaw, -rules.MaxYaw, rules.MaxYaw);
        s.Yaw = animYaw + yaw;
        var turned = Turned(foot, shin, yaw, pitch);   // голеностоп → носок с курсом и наклоном опоры
        if (heel)
            s.Ball = s.Heel + turned;
        else
            s.Heel = s.Ball - turned;
        var offset = s.Heel - new Vector2(ankle.X, ankle.Z);
        float across = Vector2.Dot(offset, inward);
        if (across > rules.MaxInward)
        {
            // Внутрь — не дальше предела: точки опоры тянутся за анимацией поперёк, вдоль хода держатся.
            var shift = -inward * (across - rules.MaxInward);
            s.Heel += shift;
            s.Ball += shift;
            offset += shift;
        }
        float distance = offset.Length();
        if (distance > rules.MaxOffset)
        {
            // Дальше предела точки опоры тянутся за анимацией: стопа едет со скоростью остатка, без срыва и скачка.
            var shift = offset * (rules.MaxOffset / distance) - offset;
            s.Heel += shift;
            s.Ball += shift;
            offset += shift;
        }
        s.Velocity = (offset - s.Offset) / dt;
        s.YawVelocity = (yaw - s.YawOffset) / dt;
        s.PitchVelocity = (pitch - s.Pitch) / dt;
        s.Offset = offset;
        s.YawOffset = yaw;
        s.Pitch = pitch;
    }

    /// <summary>Голеностоп → носок по горизонтали (x, z) у стопы анимации, повёрнутой <see cref="FootRotation"/> — как её повернёт IK.</summary>
    private static Vector2 Turned(Vector3 foot, Vector3 shin, float yaw, float pitch)
    {
        var v = Vector3.Transform(foot, FootRotation(foot, shin, yaw, pitch));
        return new Vector2(v.X, v.Z);
    }

    /// <summary>Смещение со скоростью — на шаг критической пружины к нулю.</summary>
    private static float Decay(float x, float v, float halflife, float dt)
    {
        var p = new Vector3(x, 0, 0);
        var q = new Vector3(v, 0, 0);
        AnimMath.DecaySpring(ref p, ref q, halflife, dt);
        return p.X;
    }

    /// <summary>Не в опоре: смещения гаснут к нулю критической пружиной (инерциализация), мелкие — ровно ноль.</summary>
    private static void Free(ref FootLockLeg s, float dt, float halflife)
    {
        s.Locked = false;
        if (s.Idle) return;
        var x = new Vector3(s.Offset.X, s.YawOffset, s.Offset.Y);
        var v = new Vector3(s.Velocity.X, s.YawVelocity, s.Velocity.Y);
        var r = new Vector3(s.Pitch, 0, 0);
        var rv = new Vector3(s.PitchVelocity, 0, 0);
        AnimMath.DecaySpring(ref x, ref v, halflife, dt);
        AnimMath.DecaySpring(ref r, ref rv, halflife, dt);
        if (x.Length() + MathF.Abs(r.X) < Rest && v.Length() + MathF.Abs(rv.X) < Rest / dt)
        {
            x = v = r = rv = Vector3.Zero;
        }
        s.Offset = new Vector2(x.X, x.Z);
        s.YawOffset = x.Y;
        s.Velocity = new Vector2(v.X, v.Z);
        s.YawVelocity = v.Y;
        s.Pitch = r.X;
        s.PitchVelocity = rv.X;
    }

    /// <summary>Вектор (x, z) длины <paramref name="length"/> с курсом <paramref name="yaw"/> (вперёд — (sin, cos)).</summary>
    public static Vector2 Along(float yaw, float length) => new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)) * length;

    /// <summary>Вектор (x, z) — поворотом на курс <paramref name="yaw"/> вокруг вертикали (как <see cref="Pose2.Rotate"/>).</summary>
    public static Vector2 Rotate(Vector2 v, float yaw)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector2(v.X * c + v.Y * s, -v.X * s + v.Y * c);
    }

    /// <summary>
    /// Поворот стопы для IK (модель): курс <paramref name="yaw"/> вокруг вертикали, затем наклон на <paramref name="pitch"/> (носок вверх)
    /// вокруг горизонтальной оси голеностопа — проекции оси, поперечной голени <paramref name="shin"/> (колено → голеностоп) и стопе
    /// <paramref name="foot"/> (голеностоп → носок). У стоящей стопы она — горизонталь поперёк стопы (наклон — угол над горизонталью),
    /// носком вниз в переносе не вырождается и не перескакивает: голень со стопой не бывают параллельны.
    /// </summary>
    public static Quaternion FootRotation(Vector3 foot, Vector3 shin, float yaw, float pitch)
    {
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        if (pitch == 0) return q;
        var hinge = Vector3.Cross(shin, Vector3.Transform(foot, q)) with { Y = 0 };
        if (hinge.LengthSquared() < 1e-12f) return q;
        return Quaternion.Concatenate(q, Quaternion.CreateFromAxisAngle(Vector3.Normalize(hinge), pitch));
    }
}

/// <summary>Нога для фиксации: нога в пуле поз (IK), колено, голеностоп и носок в скелете, биты пятки и носка в контактах базы MM.</summary>
public readonly record struct FootLockLegJoints(PoseLeg Rig, int Knee, int Ankle, int Ball, int HeelBit, int ToeBit);

/// <summary>
/// Ноги игрока на MM: суставы UE5 Manny (только здесь), ноги в пуле поз для IK и номера контактов базы. Порядок в тике:
/// Movement, после <c>core.root-collision</c> (корень тика окончательный) и до <c>core.pose-post-tick</c> — фиксация по итоговой
/// позе тика и задания IK второму проходу пула.
/// </summary>
public sealed class FootLockLegs
{
    /// <summary>Суставы ноги: бедро, голень, голеностоп, носок.</summary>
    public static readonly IReadOnlyList<string> LeftJoints = ["thigh_l", "calf_l", "foot_l", "ball_l"], RightJoints = ["thigh_r", "calf_r", "foot_r", "ball_r"];

    public static readonly SystemLabel Step = new("locomotion.mm-feet");

    /// <summary>Левая и правая нога (после <see cref="Init"/>).</summary>
    public FootLockLegJoints Left { get; private set; }

    /// <inheritdoc cref="Left"/>
    public FootLockLegJoints Right { get; private set; }

    public void Configure(SimulationBuilder b, MotionMatchRuntime runtime) => b.System(GameStages.Movement, new FootLockSystem(this, runtime));

    /// <summary>Сборка мира (после <see cref="MotionMatchRuntime.Init"/>): ноги в пуле, суставы и биты контактов.</summary>
    /// <exception cref="InvalidDataException">Нет сустава ноги, суставы не цепочка или голеностоп и носок — не суставы контакта базы.</exception>
    public void Init(PosePool pool, MotionMatchRuntime runtime)
    {
        Left = Leg(pool, runtime, LeftJoints);
        Right = Leg(pool, runtime, RightJoints);
    }

    private static FootLockLegJoints Leg(PosePool pool, MotionMatchRuntime runtime, IReadOnlyList<string> names)
    {
        var skeleton = pool.Skeleton;
        int Joint(string name) => skeleton.Find(name) is >= 0 and var j ? j
            : throw new InvalidDataException($"фиксация стоп игрока (locomotion.player = mm) не стартует: в скелете {skeleton.Id} нет сустава «{name}»");
        int Bit(int joint) => runtime.Database.Schema.ContactJoints.IndexOf(joint) is >= 0 and var k ? k
            : throw new InvalidDataException($"фиксация стоп: сустав «{skeleton.JointNames[joint]}» — не контакт базы MM");
        int knee = Joint(names[1]), ankle = Joint(names[2]), ball = Joint(names[3]);
        PoseLeg rig;
        try
        {
            rig = pool.AddLeg(Joint(names[0]), knee, ankle);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"фиксация стоп игрока (locomotion.player = mm) не стартует: {e.Message}", e);
        }
        return new FootLockLegJoints(rig, knee, ankle, ball, Bit(ankle), Bit(ball));
    }
}

/// <summary>
/// Movement (<c>locomotion.mm-feet</c>): фиксация стоп живого игрока на MM по итоговой позе тика в окончательном корне и задания
/// <see cref="PosePool.LegIk"/> (второй проход пула, <c>core.pose-post-tick</c>). Опора — по кадру базы проигрывателя и высоте
/// стопы в позе; под действием всем телом опоры нет (смещения гаснут). Скачок корня больше <see cref="GaspMotion.FootTeleport"/>
/// — фиксация с нуля. Тик — 0 Б.
/// </summary>
public sealed class FootLockSystem(FootLockLegs legs, MotionMatchRuntime runtime) : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(FootLockLegs.Step).After(GameLabels.RootCollision).After(MotionMatchSystems.History).Before(GameLabels.PosePostTick)
            .Reads<Transform>().Reads<PrevTransform>().Reads<AnimatorMotion>().Reads<PoseRef>().Writes<MotionMatchState>();
        _query = s.Query().With<Fighter>().Without<Dead>().Read<Transform>().Read<PrevTransform>().Read<AnimatorMotion>().Read<PoseRef>()
            .Write<MotionMatchState>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var pool = world.Resource<PosePool>();
        var locomotion = world.Resource<LocomotionRules>();
        var rules = locomotion.FootLock;
        float kneeLimit = locomotion.KneeLimit * MathF.PI / 180;
        float dt = world.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_query))
        {
            var state = chunk.Write<MotionMatchState>();
            var t = chunk.Read<Transform>()[..state.Length];
            var prev = chunk.Read<PrevTransform>()[..state.Length];
            var motion = chunk.Read<AnimatorMotion>()[..state.Length];
            var pose = chunk.Read<PoseRef>()[..state.Length];
            for (int i = 0; i < state.Length; i++)
            {
                var handle = new PoseHandle(pose[i].Index, pose[i].Generation);
                if (pool.IsAlive(handle)) Tick(ref state[i], t[i], prev[i], motion[i].Search, handle, pool, rules, kneeLimit, dt);
            }
        }
    }

    /// <summary>Шаг бойца (тело цикла — свой метод: правило OSR).</summary>
    private void Tick(ref MotionMatchState s, in Transform t, in PrevTransform prev, bool search, PoseHandle pose, PosePool pool,
        in FootLockRules rules, float kneeLimit, float dt)
    {
        var players = runtime.Players;
        if (s.Restart || !players.IsAlive(s.Player)) return;
        var jump = new Vector2(t.Position.X - prev.Position.X, t.Position.Z - prev.Position.Z);
        if (jump.LengthSquared() > GaspMotion.FootTeleport * GaspMotion.FootTeleport) s.Feet = default;
        int frame = players.State(s.Player).Frame;
        // Шаг поворота на месте — без опоры: клип поворота подогнан к оставшемуся углу и переставляет стопы сам, удержанная стопа
        // оставалась бы позади и ноги перекрещивались.
        int bits = search && frame >= 0 && s.TurnClip < 0 ? runtime.Database.Contacts[frame] : 0;
        var root = new Pose2(t.Position, t.Facing);
        var model = pool.Model(pose);
        Leg(ref s.Feet.Left, legs.Left, model, root, bits, rules, dt, -1);
        Leg(ref s.Feet.Right, legs.Right, model, root, bits, rules, dt, 1);
        Ik(s.Feet.Left, legs.Left, pose, pool, root.Yaw);
        Ik(s.Feet.Right, legs.Right, pose, pool, root.Yaw);
        // Колено над стопой — только где стопы ведёт локомоция: под действием всем телом и в шаге поворота ноги — клипа.
        if (search && s.TurnClip < 0)
        {
            Knee(legs.Left, pose, pool, kneeLimit);
            Knee(legs.Right, pose, pool, kneeLimit);
        }
    }

    /// <summary>
    /// Колено над опорной стопой (<see cref="PosePool.KneeAlign"/>) после IK ноги; доля — по высоте носка в позе тика (в махе — без
    /// правки). Предел 180° — без задания.
    /// </summary>
    private static void Knee(in FootLockLegJoints joints, PoseHandle pose, PosePool pool, float limit)
    {
        if (limit >= MathF.PI) return;
        float ball = pool.Model(pose)[joints.Ball].Translation.Y;
        float k = Math.Clamp((GaspMotion.KneeAir - ball) / (GaspMotion.KneeAir - GaspMotion.KneeGround), 0, 1);
        float weight = k * k * (3 - 2 * k);
        if (weight > 0) pool.KneeAlign(pose, joints.Rig, joints.Ball, limit, GaspMotion.KneeSoft * MathF.PI / 180, weight);
    }

    /// <summary>
    /// Шаг фиксации ноги: голеностоп и носок итоговой позы в мире, опора — бит кадра и высота сустава в позе; <paramref name="inward"/> —
    /// знак оси X модели к другой ноге (левая нога — на +X: −1).
    /// </summary>
    private static void Leg(ref FootLockLeg leg, in FootLockLegJoints joints, ReadOnlySpan<Matrix4x4> model, Pose2 root, int bits,
        in FootLockRules rules, float dt, float inward)
    {
        var k = model[joints.Knee].Translation;
        var a = model[joints.Ankle].Translation;
        var b = model[joints.Ball].Translation;
        var kw = root.ToParent(k);
        var aw = root.ToParent(a);
        var bw = root.ToParent(b);
        bool heel = (bits >> joints.HeelBit & 1) != 0 && a.Y <= rules.HeelHeight;
        bool toe = (bits >> joints.ToeBit & 1) != 0 && b.Y <= rules.BallHeight;
        var across = Pose2.Rotate(new Vector3(inward, 0, 0), root.Yaw);
        FootLock.Step(ref leg, kw with { Y = k.Y }, aw with { Y = a.Y }, bw with { Y = b.Y }, heel, toe, dt, rules, new Vector2(across.X, across.Z));
    }

    /// <summary>Задание IK ноги со смещениями фиксации (мир → модель); нулевая правка — без задания.</summary>
    private static void Ik(in FootLockLeg leg, in FootLockLegJoints joints, PoseHandle pose, PosePool pool, float yaw)
    {
        if (leg.Idle) return;
        var model = pool.Model(pose);
        var ankle = model[joints.Ankle].Translation;
        var target = ankle + Pose2.Rotate(new Vector3(leg.Offset.X, 0, leg.Offset.Y), -yaw);
        var foot = FootLock.FootRotation(model[joints.Ball].Translation - ankle, ankle - model[joints.Knee].Translation, leg.YawOffset, leg.Pitch);
        pool.LegIk(pose, joints.Rig, target, foot, GaspMotion.FootSoft);
    }
}
