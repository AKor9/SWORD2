using System.Globalization;
using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Animation.Motion;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Locomotion.Motion;

/// <summary>
/// <c>locomotion.mmDecay</c> (тяжёлый, после End): смещения инерциализации не растут без перехода. Мера — энергия
/// <c>E = |v|² + y²·|x|²</c>, <c>y = HalflifeToDamping(полупериод)/2</c>: у критически демпфированной пружины
/// <c>dE/dt = −4y·|v|² ≤ 0</c>. Сравнивает с прошлой своей проверкой, только если хэндл, число переходов и полупериод
/// те же; допуск — округление вектора поворота (10⁻⁴ относительный и 10⁻⁹). Мир и хеш не трогает.
/// </summary>
public sealed class MotionMatchDecayInvariant(MotionMatchRuntime runtime) : IInvariant
{
    public const float Relative = 1e-4f, Absolute = 1e-9f;

    private ReadQuery? _query;
    private float[] _energy = [];
    private int _jc;
    private MotionPlayerHandle _player;
    private int _transitions;
    private float _halflife;
    private bool _remembered;

    public string Id => "locomotion.mmDecay";
    public InvariantCost Cost => InvariantCost.Heavy;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        if (!runtime.Ready) return;
        var world = ctx.World;
        _query ??= world.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<MotionMatchState>();
        float halflife = world.Resource<LocomotionRules>().InertialHalflife;
        bool seen = false;
        foreach (var chunk in world.Iterate(_query))
        {
            var entities = chunk.Entities;
            var states = chunk.Read<MotionMatchState>();
            // Игрок на MM один: память — у первого бойца на MM (остальные, если бы были, проверялись бы без памяти).
            for (int i = 0; i < entities.Length && !seen; i++)
            {
                if (states[i].Restart || !runtime.Players.IsAlive(states[i].Player)) continue;
                seen = true;
                if (Check(states[i].Player, halflife) is { } problem) violations.Report(entities[i], problem);
            }
        }
        if (!seen) _remembered = false;
    }

    /// <summary>Проверка проигрывателя и новая память; нарушение — текст.</summary>
    private string? Check(MotionPlayerHandle player, float halflife)
    {
        var players = runtime.Players;
        int jc = players.JointCount;
        if (_energy.Length != 2 * jc)
        {
            _energy = new float[2 * jc];
            _jc = jc;
            _remembered = false;
        }
        int transitions = players.State(player).Transitions;
        bool compare = _remembered && player == _player && transitions == _transitions && halflife == _halflife;
        float y = AnimMath.HalflifeToDamping(halflife) * 0.5f;
        string? problem = Energies(players.RotationOffsets(player), players.RotationOffsetVelocities(player), players.PositionOffsets(player),
            players.PositionOffsetVelocities(player), y, compare);
        _player = player;
        _transitions = transitions;
        _halflife = halflife;
        _remembered = true;
        return problem;
    }

    /// <summary>Энергии суставов (поворот, затем позиция) — сравнение с памятью и новая память (тело цикла — свой метод).</summary>
    private string? Energies(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> rotVelocity, ReadOnlySpan<Vector3> pos,
        ReadOnlySpan<Vector3> posVelocity, float y, bool compare)
    {
        string? problem = null;
        float y2 = y * y;
        for (int j = 0; j < _jc; j++)
        {
            float er = rotVelocity[j].LengthSquared() + y2 * AnimMath.ToRotationVector(rot[j]).LengthSquared();
            float ep = posVelocity[j].LengthSquared() + y2 * pos[j].LengthSquared();
            if (compare && problem is null)
            {
                if (Grew(_energy[j], er)) problem = Text(j, "поворота", _energy[j], er, rot[j], rotVelocity[j]);
                else if (Grew(_energy[_jc + j], ep)) problem = Text(j, "позиции", _energy[_jc + j], ep, pos[j], posVelocity[j]);
            }
            _energy[j] = er;
            _energy[_jc + j] = ep;
        }
        return problem;
    }

    private static bool Grew(float before, float now) => now > before * (1 + Relative) + Absolute;

    private string Text(int joint, string what, float before, float now, object offset, Vector3 velocity) =>
        string.Create(CultureInfo.InvariantCulture,
            $"сустав {runtime.Database.Library.Skeleton.JointNames[joint]}: энергия смещения {what} выросла без перехода {before:G5} → {now:G5} " +
            $"(смещение {offset}, скорость {velocity})");
}

/// <summary>
/// <c>locomotion.mmContinuity</c> (тяжёлый, после End): наибольший скачок сустава выхода проигрывателя за тик
/// (<see cref="MotionMatchProbe"/>) не больше <see cref="Limit"/> (~2× к худшему обычному движению). Постройка заново
/// и мёртвые не проверяются.
/// </summary>
public sealed class MotionMatchContinuityInvariant(MotionMatchRuntime runtime) : IInvariant
{
    /// <summary>Предел скачка сустава за тик, м (замер: худший на сценариях — README модуля).</summary>
    public const float Limit = 0.25f;

    private readonly MotionMatchProbe _probe = new(runtime);
    private ReadQuery? _query;

    public string Id => "locomotion.mmContinuity";
    public InvariantCost Cost => InvariantCost.Heavy;
    public InvariantScope Scope => InvariantScope.Entity;
    public string After => GameStages.End;

    public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
    {
        if (!runtime.Ready) return;
        var world = ctx.World;
        _query ??= world.Query().With<Fighter>().Without<Dead>().Without<Downed>().Read<MotionMatchState>();
        float dt = world.Resource<SimTime>().Dt;
        foreach (var chunk in world.Iterate(_query))
        {
            var entities = chunk.Entities;
            var states = chunk.Read<MotionMatchState>();
            for (int i = 0; i < entities.Length; i++)
            {
                if (Problem(states[i], dt) is not { } problem) continue;
                violations.Report(entities[i], problem);
                if (violations.Full) return;
            }
        }
    }

    private string? Problem(in MotionMatchState s, float dt)
    {
        if (s.Restart || !_probe.Poses(s.Player, dt)) return null;
        var (jump, joint) = _probe.Jump();
        if (!(jump > Limit) && float.IsFinite(jump)) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"сустав {runtime.Database.Library.Skeleton.JointNames[joint]} прыгнул за тик на {jump:0.###} м (предел {Limit} м): кадр {runtime.Players.State(s.Player).Frame}");
    }
}
