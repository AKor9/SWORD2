using System.Numerics;

namespace Sword.Engine.Physics;

// 7б: динамика тел — форма с массой, режим, профили динамики, запись состояния, настройки.
public sealed partial class PhysicsWorld
{
    /// <summary>Наибольшее число профилей динамики: номер профиля в теле — байт.</summary>
    public const int MaxDynamics = 256;

    /// <summary>Допуск единичности поворота при записи (<see cref="Set"/>): |q|² − 1, по модулю.</summary>
    private const float UnitTolerance = 1e-3f;

    private BodyDynamics[] _dynamics = [new BodyDynamics()];
    private int _dynamicsCount = 1;
    private PhysicsSettings _settings = PhysicsSettings.Default;

    public int DynamicsCount => _dynamicsCount;

    public PhysicsSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Problem() is { } problem) throw new ArgumentException($"настройки физики: {problem}", nameof(value));
            _settings = value;
        }
    }

    public void SetShape(BodyHandle body, float mass, float radius, float halfLength)
    {
        Check(body);
        if (!float.IsFinite(mass) || !(mass > 0)) throw new ArgumentOutOfRangeException(nameof(mass), mass, "масса — конечная, больше 0");
        if (!float.IsFinite(radius) || !(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "радиус тела с массой — конечный, больше 0");
        if (!float.IsFinite(halfLength) || halfLength < 0)
            throw new ArgumentOutOfRangeException(nameof(halfLength), halfLength, "полудлина — конечная, не меньше 0");
        Dynamics.SetShape(ref _bodies[body.Index], mass, radius, halfLength);
    }

    public void SetMode(BodyHandle body, BodyMode mode)
    {
        Check(body);
        if (mode is not (BodyMode.Kinematic or BodyMode.Simulated)) throw new ArgumentOutOfRangeException(nameof(mode), mode, "режим — Kinematic или Simulated");
        ref var s = ref _bodies[body.Index];
        if (mode == BodyMode.Simulated && !(s.Mass > 0))
            throw new InvalidOperationException($"тело {body} без массы не может быть на физике: сначала SetShape с массой");
        s.Mode = mode;
        StepMarkNonFinite(body.Index);   // 7з-2: нечисловое кинематическое тело, ставшее телом на физике, — в счёт нечисловых
    }

    public void SetDynamics(BodyHandle body, int profile)
    {
        Check(body);
        CheckProfile(profile);
        _bodies[body.Index].Profile = (byte)profile;
    }

    public int RegisterDynamics(in BodyDynamics dynamics)
    {
        if (dynamics.Problem() is { } problem) throw new ArgumentException($"профиль динамики: {problem}", nameof(dynamics));
        if (_dynamicsCount == MaxDynamics) throw new InvalidOperationException($"профилей динамики — не больше {MaxDynamics}");
        if (_dynamicsCount == _dynamics.Length) Array.Resize(ref _dynamics, Math.Min(_dynamics.Length * 2, MaxDynamics));
        _dynamics[_dynamicsCount] = dynamics;
        return _dynamicsCount++;
    }

    public void ReplaceDynamics(int profile, in BodyDynamics dynamics)
    {
        CheckProfile(profile);
        if (dynamics.Problem() is { } problem) throw new ArgumentException($"профиль динамики: {problem}", nameof(dynamics));
        _dynamics[profile] = dynamics;
    }

    public BodyDynamics GetDynamics(int profile)
    {
        CheckProfile(profile);
        return _dynamics[profile];
    }

    public void Set(BodyHandle body, in BodyState state)
    {
        Check(body);
        if (Problem(state) is { } problem) throw new ArgumentException($"состояние тела {body}: {problem}", nameof(state));
        bool simulated = _bodies[body.Index].Mode == BodyMode.Simulated;
        _bodies[body.Index] = state;
        if (simulated || state.Mode == BodyMode.Simulated) StepMarkNonFinite(body.Index);   // 7з-2: режим мог смениться
    }

    public void SetKinematicSource(BodyHandle body, KinematicSource source)
    {
        Check(body);
        if (source is not (KinematicSource.Body or KinematicSource.Pass or KinematicSource.NoReaction))
            throw new ArgumentOutOfRangeException(nameof(source), source, "толчок — Body, Pass или NoReaction");
        _bodies[body.Index].Source = source;
    }

    private void CheckProfile(int profile)
    {
        if ((uint)profile >= (uint)_dynamicsCount)
            throw new ArgumentOutOfRangeException(nameof(profile), profile, $"профилей динамики {_dynamicsCount}");
    }

    /// <summary>Что не так с записываемым состоянием (null — годно). Строки — постоянные: проверка годного не выделяет памяти.</summary>
    private string? Problem(in BodyState s)
    {
        if (s.Mode is not (BodyMode.Kinematic or BodyMode.Simulated)) return "режим — Kinematic или Simulated";
        if (s.Source is not (KinematicSource.Body or KinematicSource.Pass or KinematicSource.NoReaction)) return "толчок — Body, Pass или NoReaction";
        if (s.Profile >= _dynamicsCount) return "профиль динамики — не из таблицы мира";
        if (!Finite(s.Position) || !Finite(s.PrevPosition) || !Finite(s.StartPosition)) return "положения — конечные";
        if (!Finite(s.Velocity) || !Finite(s.AngularVelocity)) return "скорости — конечные";
        if (!Unit(s.Rotation) || !Unit(s.PrevRotation) || !Unit(s.StartRotation)) return "повороты — конечные, единичной длины";
        if (!NonNegative(s.Radius) || !NonNegative(s.HalfLength)) return "радиус и полудлина — конечные, не меньше 0";
        if (!NonNegative(s.Mass) || !NonNegative(s.InvMass)) return "масса и обратная масса — конечные, не меньше 0";
        if (!NonNegative(s.InvInertia.X) || !NonNegative(s.InvInertia.Y) || !NonNegative(s.InvInertia.Z))
            return "обратная инерция — конечная, не меньше 0";
        if (s.Mode == BodyMode.Simulated && !(s.Mass > 0 && s.InvMass > 0)) return "тело на физике — с массой и обратной массой больше 0";
        return null;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool Unit(Quaternion q)
    {
        float len = q.LengthSquared();
        return float.IsFinite(len) && MathF.Abs(len - 1) <= UnitTolerance;
    }

    private static bool NonNegative(float v) => float.IsFinite(v) && v >= 0;
}
