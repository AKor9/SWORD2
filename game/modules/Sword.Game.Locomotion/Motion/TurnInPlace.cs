using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Поворот на месте игрока на Motion Matching. Курс корня ведёт контроллер ядра (быстро, к взгляду), а ноги стоя идут за своим курсом — курсом
/// ног <see cref="MotionMatchState.FeetYaw"/>: он поворачивается только вместе с корнем проигрываемого клипа (у стойки — никак), поэтому
/// опорная стопа в мире стоит. Расхождение δ = wrap(курс корня − курс ног) берёт скручивание позы (<see cref="PosePool.Twist"/> на −δ): ноги и таз
/// — по курсу ног, корпус доворачивается суставами позвоночника (<see cref="GaspMotion.TwistSpine"/>), грудь, голова, руки и меч — по курсу
/// корня. Большое расхождение (больше порога) или малое, но курс стоит дольше выдержки, — переход в первый кадр клипа
/// <c>Stand_Turn_&lt;угол&gt;_&lt;L|R&gt;</c> (угол — ближайший к |δ|, сторона — знак δ: L — поворот к +X, δ &gt; 0); пока корень клипа
/// поворачивается, курс ног идёт за ним с подгонкой k = δ / остаток поворота клипа (клип доворачивает ноги ровно к курсу корня), а поиск MM его
/// не перебивает; дальше граф ведёт клип в стойку. Потолок δ — ноги подтягиваются (скользят). Пошёл — δ гаснет к 0 за <c>turnReturn</c>.
/// Грудь стоя держит курс корня и тогда, когда клип сам уводит её от своего корня (клип поворота — до 23°): скручивание доворачивает её к
/// курсу груди позы постройки (<see cref="MotionMatchState.Chest"/>, по выходу проигрывателя прошлого тика).
///
/// Заводится при сборке мира (<see cref="MotionMatchRuntime.Init"/>): скручивание в пуле поз, клипы поворота базы, остаток поворота корня по
/// кадрам, курс груди позы постройки. Нет сустава — ошибка старта. Клипов поворота в отборе нет — ноги держит только скручивание и потолок.
/// Тик — 0 Б.
/// </summary>
public sealed class TurnInPlace
{
    private const float Deg = MathF.PI / 180;

    private readonly MotionDatabase _db;
    private readonly float[] _remaining;
    private readonly bool[] _isTurn;
    private readonly int[] _clips;
    private readonly GaspClipName[] _names;
    private readonly int[] _parents;
    private readonly int _left, _right;
    private readonly float _chest;

    /// <param name="motion">База MM.</param>
    /// <param name="pool">Пул поз симуляции (скручивание — в нём).</param>
    /// <param name="startFrame">Кадр позы постройки: его курс груди — курс груди стоя.</param>
    /// <exception cref="InvalidDataException">В скелете нет сустава скручивания или ключицы.</exception>
    public TurnInPlace(GaspMotionBase motion, PosePool pool, int startFrame)
    {
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(pool);
        _db = motion.Database;
        var skeleton = pool.Skeleton;
        _parents = skeleton.JointParents.ToArray();
        _left = Joint(skeleton, GaspMotion.ChestLeft);
        _right = Joint(skeleton, GaspMotion.ChestRight);
        var rot = new Quaternion[skeleton.JointCount];
        var pos = new Vector3[skeleton.JointCount];
        pool.Clips.Sample(_db.Clips[_db.ClipOf[startFrame]].LibraryClip, _db.FrameInClip(startFrame) / _db.SampleRate, rot, pos);
        _chest = ChestYaw(rot, pos);
        int root = Joint(skeleton, GaspMotion.TwistRoot);
        var spine = new int[GaspMotion.TwistSpine.Count];
        var weights = new float[spine.Length];
        for (int k = 0; k < spine.Length; k++)
        {
            spine[k] = Joint(skeleton, GaspMotion.TwistSpine[k].Joint);
            weights[k] = GaspMotion.TwistSpine[k].Weight;
        }
        Twist = pool.AddTwist(root, spine, weights);
        var names = motion.Names;
        _isTurn = new bool[_db.ClipCount];
        var clips = new List<int>();
        var turnNames = new List<GaspClipName>();
        for (int c = 0; c < _db.ClipCount; c++)
            if (names[c].Kind == GaspKind.TurnInPlace)
            {
                _isTurn[c] = true;
                clips.Add(c);
                turnNames.Add(names[c]);
            }
        _clips = [.. clips];
        _names = [.. turnNames];
        _remaining = new float[_db.FrameCount];
        foreach (int c in _clips) FillRemaining(_db.Clips[c]);
    }

    /// <summary>Скручивание позы бойца в пуле поз симуляции.</summary>
    public PoseTwist Twist { get; }

    /// <summary>Курс груди позы постройки в модели, рад (+ от +Z к +X).</summary>
    public float StandChest => _chest;

    /// <summary>Курс груди локальной позы в модели, рад: по паре ключиц (<see cref="GaspMotion.ChestLeft"/>, <see cref="GaspMotion.ChestRight"/>).</summary>
    public float ChestYaw(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos)
    {
        var d = Origin(_left, rot, pos) - Origin(_right, rot, pos);
        return MathF.Atan2(-d.Z, d.X);
    }

    /// <summary>Выравнивание груди для позы: курс груди стоя − курс груди позы, в ±<see cref="GaspMotion.ChestMax"/>.</summary>
    public float ChestFix(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos) =>
        Math.Clamp(Pose2.Wrap(_chest - ChestYaw(rot, pos)), -GaspMotion.ChestMax, GaspMotion.ChestMax);

    /// <summary>Начало сустава в модели — прямой кинематикой по цепочке предков (тело цикла — свой метод: зовётся в тике).</summary>
    private Vector3 Origin(int joint, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos)
    {
        var v = pos[joint];
        for (int a = _parents[joint]; a >= 0; a = _parents[a]) v = Vector3.Transform(v, rot[a]) + pos[a];
        return v;
    }

    /// <summary>Клипов поворота на месте в базе.</summary>
    public int ClipCount => _clips.Length;

    /// <summary>Клип базы — поворот на месте.</summary>
    public bool IsTurnClip(int clip) => (uint)clip < (uint)_isTurn.Length && _isTurn[clip];

    /// <summary>Кадр базы — кадр клипа поворота на месте.</summary>
    public bool IsTurnFrame(int frame) => frame >= 0 && _isTurn[_db.ClipOf[frame]];

    /// <summary>
    /// Остаток поворота корня клипа поворота от кадра <paramref name="frame"/> до его выхода (кадр выхода или конец клипа), рад; не клип
    /// поворота — 0.
    /// </summary>
    public float Remaining(int frame) => frame >= 0 ? _remaining[frame] : 0;

    /// <summary>
    /// Клип поворота для расхождения <paramref name="delta"/>: сторона — знак (+ — L, − — R), угол имени — ближайший к |δ| (ничья — меньший);
    /// нет клипов этой стороны — −1.
    /// </summary>
    public int Choose(float delta)
    {
        var side = delta > 0 ? GaspSide.Left : GaspSide.Right;
        float degrees = MathF.Abs(delta) / Deg, best = float.PositiveInfinity;
        int clip = -1, bestDegrees = int.MaxValue;
        for (int i = 0; i < _clips.Length; i++)
        {
            if (_names[i].Side != side) continue;
            float d = MathF.Abs(_names[i].Degrees - degrees);
            if (d < best || d == best && _names[i].Degrees < bestDegrees)
            {
                best = d;
                bestDegrees = _names[i].Degrees;
                clip = _clips[i];
            }
        }
        return clip;
    }

    /// <summary>Корень клипа поворота ещё поворачивается: поворот идёт, кадр <paramref name="current"/> — в его клипе, остаток больше <see cref="GaspMotion.TurnDone"/>.</summary>
    public bool Turning(in MotionMatchState s, int current) =>
        s.TurnClip >= 0 && current >= 0 && _db.Clips[_db.ClipOf[current]].LibraryClip == s.TurnClip
        && MathF.Abs(Remaining(current)) > GaspMotion.TurnDone;

    /// <summary>
    /// Шаг поворота на месте в тике (до задания проигрывателя): курс ног, расхождение, выдержка, подгонка, выравнивание груди; стоя при большом
    /// расхождении (или малом после выдержки) — вернёт кадр перехода в клип поворота, иначе −1. <paramref name="clipYaw"/> — поворот корня
    /// клипа за прошлый шаг проигрывателя, <paramref name="lastFacing"/> — курс корня прошлого тика, <paramref name="current"/> — текущий кадр,
    /// <paramref name="chestFix"/> — выравнивание груди выхода прошлого шага (<see cref="ChestFix"/>).
    /// </summary>
    public int Step(ref MotionMatchState s, float facing, float lastFacing, bool standing, int current, float clipYaw, float chestFix,
        in LocomotionRules rules, float dt)
    {
        s.TwistBefore = s.Twist;
        float chest = standing && rules.TurnMax > 0 ? chestFix : 0;
        float rate = GaspMotion.ChestRate * dt;
        s.Chest = MathF.Abs(chest - s.Chest) <= rate ? chest : s.Chest + MathF.CopySign(rate, chest - s.Chest);
        float max = rules.TurnMax * Deg;
        int transition = -1;
        float delta;
        if (standing)
        {
            s.FeetYaw = Pose2.Wrap(s.FeetYaw + clipYaw * s.TurnWarp);
            delta = Math.Clamp(Pose2.Wrap(facing - s.FeetYaw), -max, max);
            s.Still = MathF.Abs(Pose2.Wrap(facing - lastFacing)) <= GaspMotion.TurnStillRate * dt ? s.Still + dt : 0;
            if (!Turning(s, current)) s.TurnClip = -1;
            float big = MathF.Abs(delta);
            bool against = s.TurnClip >= 0 && delta * Remaining(current) < 0;   // курс развернулся против идущего поворота
            if ((s.TurnClip < 0 || against) && (big > rules.TurnAngle * Deg || s.Still >= rules.TurnHold && big > rules.TurnSmallAngle * Deg)
                && Choose(delta) is var clip and >= 0)
            {
                transition = _db.Clips[clip].Start;
                current = transition;
                s.TurnClip = _db.Clips[clip].LibraryClip;
                s.Turns++;
            }
            s.TurnWarp = s.TurnClip >= 0 ? Warp(delta, Remaining(current)) : 1;
        }
        else
        {
            s.TurnClip = -1;
            s.Still = 0;
            s.TurnWarp = 1;
            delta = Math.Clamp(s.Twist, -max, max);
            float step = max / rules.TurnReturn * dt;
            delta = MathF.Abs(delta) <= step ? 0 : delta - MathF.CopySign(step, delta);
        }
        s.Twist = delta;
        s.FeetYaw = Pose2.Wrap(facing - delta);
        return transition;
    }

    /// <summary>Подгонка: доля поворота корня клипа, которая доведёт ноги ровно до курса корня, в 0…<see cref="GaspMotion.TurnWarpMax"/>.</summary>
    public static float Warp(float delta, float remaining) =>
        MathF.Abs(remaining) <= GaspMotion.TurnDone ? 1 : Math.Clamp(delta / remaining, 0, GaspMotion.TurnWarpMax);

    /// <summary>Остаток поворота корня по кадрам клипа — с конца: у кадра выхода и последнего — 0 (дальше — продолжение, не этот клип).</summary>
    private void FillRemaining(MotionClip clip)
    {
        var next = _db.Next;
        var entry = _db.Entry;
        var interval = _db.Interval;
        for (int f = clip.End - 1; f >= clip.Start; f--)
        {
            bool inside = entry[f] < 0 && next[f] > f && next[f] < clip.End;
            _remaining[f] = inside ? interval[f].Yaw + _remaining[next[f]] : 0;
        }
    }

    private static int Joint(Skeleton skeleton, string name) => skeleton.Find(name) is var j and >= 0
        ? j
        : throw new InvalidDataException($"поворот на месте игрока (locomotion.player = mm) не стартует: в скелете {skeleton.Id} нет сустава «{name}» (скручивание корпуса)");
}
