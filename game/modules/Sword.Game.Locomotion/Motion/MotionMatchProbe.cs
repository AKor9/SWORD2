using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// Замер тика по проигрывателю Motion Matching (8з; <see cref="MotionMatchMeasure"/>): скольжение опорной стопы и скачок сустава — по
/// выходу проигрывателя (поза Motion Matching без руки с мечом 8ж и слоя действий: рука, удар верхом и рывок её не трогают) и истории
/// корня, без памяти между тиками. Выход прошлого тика восстанавливается из скоростей выхода (их задание пула считает разностью выхода тика
/// и прошлого за dt: прошлый = выход − скорость·dt; у поворота — обратный поворот на вектор ω·dt), обе позы — прямой кинематикой скелета в
/// пространство персонажа. Поэтому скачок, который сделало задание (переход, затухание, сэмпл), виден, а постройка заново (первый шаг —
/// скорости клипа) скачком не считается. Корень прошлого тика — прошлая запись истории (история после постройки — одна запись: скольжения
/// нет, NaN). Буферы — свои (у вида и у каждого инварианта — свой замер); 0 Б.
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

    /// <summary>Модельные матрицы восстановленного выхода прошлого тика последнего <see cref="Poses"/>.</summary>
    public ReadOnlySpan<Matrix4x4> Previous => _prev;

    /// <summary>
    /// Позы выхода тика и прошлого тика (<see cref="Now"/>, <see cref="Previous"/>) проигрывателя <paramref name="player"/>; выхода ещё нет
    /// (не начат) — false.
    /// </summary>
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

    /// <summary>Наибольший скачок сустава между <see cref="Previous"/> и <see cref="Now"/>, м, и чей (после <see cref="Poses"/>).</summary>
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
    /// Замер тика проигрывателя <paramref name="player"/> (<paramref name="history"/> — его история корня, <paramref name="dt"/> — тик, с):
    /// скачок сустава и скольжение опорной стопы — суставы контакта на земле по разметке кадра проигрывателя, горизонтальная скорость в
    /// мире (корень тика и прошлого тика — две последние записи истории, подряд по тикам).
    /// </summary>
    public MotionMatchMeasure Measure(MotionPlayerHandle player, RootHistory? history, float dt)
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
        var now = h[h.Count - 1].Root;
        var prev = h[h.Count - 2].Root;
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
}
