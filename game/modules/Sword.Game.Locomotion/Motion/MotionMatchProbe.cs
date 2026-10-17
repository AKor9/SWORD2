using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Замер тика по выходу проигрывателя Motion Matching (без руки с мечом и слоя действий) и истории корня, без памяти между
/// тиками: выход прошлого тика восстанавливается из скоростей выхода (выход − скорость·dt, у поворота — обратный поворот
/// на ω·dt). Поэтому скачок задания виден, а постройка заново скачком не считается. Буферы свои; 0 Б.
/// </summary>
public sealed class MotionMatchProbe
{
    private readonly MotionMatchRuntime _runtime;
    private Matrix4x4[] _now = [], _prev = [];
    private Quaternion[] _rot = [];
    private Vector3[] _pos = [];

    public MotionMatchProbe(MotionMatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <summary>Модельные матрицы выхода тика последнего <see cref="Poses"/>.</summary>
    public ReadOnlySpan<Matrix4x4> Now => _now;

    /// <summary>Позы выхода тика и прошлого тика (<see cref="Now"/> и прошлого); выхода ещё нет — false.</summary>
    public bool Poses(MotionPlayerHandle player, float dt)
    {
        var players = _runtime.Players;
        if (!players.IsAlive(player) || !players.State(player).HasOutput) return false;
        var skeleton = _runtime.Database.Library.Skeleton;
        int n = skeleton.JointCount;
        if (_now.Length != n)
        {
            _now = new Matrix4x4[n];
            _prev = new Matrix4x4[n];
            _rot = new Quaternion[n];
            _pos = new Vector3[n];
        }
        var rot = players.OutputRotations(player);
        var pos = players.OutputPositions(player);
        skeleton.ToModel(rot, pos, _now);
        Back(rot, pos, players.AngularVelocities(player), players.LinearVelocities(player), dt);
        skeleton.ToModel(_rot, _pos, _prev);
        return true;
    }

    /// <summary>Выход прошлого тика из выхода и скоростей выхода (тело цикла по суставам — свой метод: правило OSR).</summary>
    private void Back(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, ReadOnlySpan<Vector3> angular, ReadOnlySpan<Vector3> linear, float dt)
    {
        for (int j = 0; j < rot.Length; j++)
        {
            // Скорость выхода: RotationVector(прошлый, выход) = ToRotationVector(выход · conj(прошлый)) = ω·dt.
            _rot[j] = Quaternion.Normalize(Quaternion.Conjugate(AnimMath.FromRotationVector(angular[j] * dt)) * rot[j]);
            _pos[j] = pos[j] - linear[j] * dt;
        }
    }

    /// <summary>Наибольший скачок сустава между прошлой позой и <see cref="Now"/>, м, и чей (после <see cref="Poses"/>).</summary>
    public (float Jump, int Joint) Jump()
    {
        float best = 0;
        int joint = -1;
        for (int j = 0; j < _now.Length; j++)
        {
            float d = Vector3.Distance(_now[j].Translation, _prev[j].Translation);
            if (d > best || joint < 0)
            {
                best = d;
                joint = j;
            }
        }
        return (best, joint);
    }

    /// <summary>
    /// Замер тика проигрывателя: скачок сустава и скольжение опорной стопы (суставы контакта по разметке кадра, скорость в
    /// мире по двум последним записям <paramref name="history"/>). Стопы — по курсу ног: курс корня − <paramref name="twist"/>
    /// (прошлый тик — <paramref name="twistBefore"/>).
    /// </summary>
    public MotionMatchMeasure Measure(MotionPlayerHandle player, RootHistory? history, float dt, float twist = 0, float twistBefore = 0)
    {
        if (!Poses(player, dt)) return MotionMatchMeasure.None;
        var (jump, joint) = Jump();
        var db = _runtime.Database;
        int frame = _runtime.Players.State(player).Frame;
        int bits = frame >= 0 ? db.Contacts[frame] : 0;
        var contacts = db.Schema.ContactJoints;
        int feet = 0;
        for (int k = 0; k < contacts.Length; k++) feet += bits >> k & 1;
        if (feet == 0 || history is not { Count: >= 2 } h || h[h.Count - 1].Tick != h[h.Count - 2].Tick + 1)
            return new MotionMatchMeasure(float.NaN, -1, feet, jump, joint);
        var now = h[h.Count - 1].Root with { Yaw = h[h.Count - 1].Root.Yaw - twist };
        var prev = h[h.Count - 2].Root with { Yaw = h[h.Count - 2].Root.Yaw - twistBefore };
        float slide = 0;
        int slideJoint = -1;
        for (int k = 0; k < contacts.Length; k++)
        {
            if ((bits >> k & 1) == 0) continue;
            int j = contacts[k];
            var d = now.ToParent(_now[j].Translation) - prev.ToParent(_prev[j].Translation);
            float speed = new Vector2(d.X, d.Z).Length() / dt;
            if (speed > slide || slideJoint < 0)
            {
                slide = speed;
                slideJoint = j;
            }
        }
        return new MotionMatchMeasure(slide, slideJoint, feet, jump, joint);
    }

    /// <summary>
    /// Скольжение опорной стопы по итоговой позе: суставы контакта кадра проигрывателя, мир — корень двух последних записей
    /// <paramref name="history"/> × модельные матрицы <paramref name="now"/> и <paramref name="before"/> (поза уже повёрнута по курсу ног,
    /// курс — корня). Нет данных — NaN и −1.
    /// </summary>
    public (float Slide, int Joint) PoseSlide(MotionPlayerHandle player, ReadOnlySpan<Matrix4x4> now, ReadOnlySpan<Matrix4x4> before,
        RootHistory? history, float dt)
    {
        var players = _runtime.Players;
        if (!players.IsAlive(player) || history is not { Count: >= 2 } h || h[h.Count - 1].Tick != h[h.Count - 2].Tick + 1) return (float.NaN, -1);
        var db = _runtime.Database;
        int frame = players.State(player).Frame;
        int bits = frame >= 0 ? db.Contacts[frame] : 0;
        if (bits == 0) return (float.NaN, -1);
        var contacts = db.Schema.ContactJoints;
        var root = h[h.Count - 1].Root;
        var prev = h[h.Count - 2].Root;
        float slide = 0;
        int slideJoint = -1;
        for (int k = 0; k < contacts.Length; k++)
        {
            if ((bits >> k & 1) == 0) continue;
            int j = contacts[k];
            var d = root.ToParent(now[j].Translation) - prev.ToParent(before[j].Translation);
            float speed = new Vector2(d.X, d.Z).Length() / dt;
            if (speed > slide || slideJoint < 0)
            {
                slide = speed;
                slideJoint = j;
            }
        }
        return (slide, slideJoint);
    }
}
