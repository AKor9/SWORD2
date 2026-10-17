using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Impact;

/// <summary>
/// Dismember, реактивная по <see cref="SeverRequested"/>: попадание по живому (не труп, не взрыв) — импульс удара (<see cref="ImpactPhysics"/>)
/// от скорости точки оружия и точки касания относительно центра масс (таз), сдвиг баланса (<see cref="Balance"/>; несколько
/// попаданий тика копятся по порядку) и отклик <see cref="HitResponse"/>. Памяти не выделяет.
/// </summary>
public sealed class ImpactSystem : IReactiveSystem<Event<SeverRequested>>
{
    private const int Max = 64;
    private readonly Entity[] _victims = new Entity[Max];
    private readonly Balance[] _balances = new Balance[Max];
    private readonly bool[] _had = new bool[Max];
    private int _count;

    public void Setup(SystemSetup s) =>
        s.Label(new SystemLabel("impact.decide")).Reads<BodyRef>().Reads<Balance>().Reads<Dead>().Reads<PlayerTag>().Sets<Balance>()
            .Adds<Balance>().Emits<HitResponse>().ReserveCommands(Max, Max, Max * 160);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<SeverRequested>> requests)
    {
        var world = ctx.World;
        var physics = world.Resource<IPhysicsWorld>();
        var rules = world.Resource<ImpactRules>();
        var time = world.Resource<SimTime>();
        _count = 0;
        foreach (var e in requests) Decide(ctx, world, physics, rules, time.Time, e.Value);
        for (int i = 0; i < _count; i++)
            if (_had[i]) ctx.Commands.Set(_victims[i], _balances[i]);
            else ctx.Commands.Add(_victims[i], _balances[i]);
    }

    private void Decide(in SystemContext ctx, World world, IPhysicsWorld physics, in ImpactRules rules, float now, in SeverRequested r)
    {
        if (r.Corpse || r.Kind == HitKind.Blast || !BodySegs.IsFlesh(r.Part)) return;
        var e = r.Victim;
        if (!world.IsAlive(e) || world.Has<Dead>(e) || !world.TryGet<BodyRef>(e, out var bodyRef)) return;
        var body = new ArticulationHandle(bodyRef.Index, bodyRef.Generation);
        if (!physics.IsAlive(body) || physics.BodyCount(body) < BodySegs.Bodies) return;
        int k = Slot(world, e);
        if (k < 0) return;
        bool blunt = r.Kind == HitKind.Blunt;
        var com = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis))).Position;
        var impulse = ImpactPhysics.Impulse(r.Velocity, r.Point - com, blunt, rules, out float momentum);
        float shift = ImpactPhysics.CaptureShift(impulse, rules) * (ImpactPhysics.IsLeg(r.Part) ? rules.LegFactor : 1);
        ref var balance = ref _balances[k];
        balance.Shift = ImpactPhysics.Decay(balance.Shift, now - balance.Time, rules) + shift;
        balance.Time = now;
        var level = ImpactPhysics.Level(balance.Shift, world.Has<PlayerTag>(e), rules, out float strength);
        var push = new Vector3(impulse.X, 0, impulse.Z) / rules.BodyMass;
        float pushSpeed = push.Length();
        if (pushSpeed > rules.MaxPush) push *= rules.MaxPush / pushSpeed;
        ctx.Commands.Emit(new HitResponse
        {
            Attacker = r.Attacker, Victim = e, Part = r.Part, Level = level, Strength = strength, Impulse = impulse, Point = r.Point, Push = push,
            Recoil = momentum > 1e-4f ? impulse.Length() / momentum : 0, Blunt = blunt,
        });
    }

    /// <summary>Жертва в накоплении тика: баланс — итог прошлых попаданий тика.</summary>
    private int Slot(World world, Entity e)
    {
        for (int i = 0; i < _count; i++)
            if (_victims[i] == e) return i;
        if (_count == Max) return -1;
        _victims[_count] = e;
        _had[_count] = world.TryGet<Balance>(e, out var b);
        _balances[_count] = _had[_count] ? b : default;
        return _count++;
    }
}
