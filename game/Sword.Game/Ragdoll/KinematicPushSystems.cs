using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Толчки живых: кинематические тела живого в шаге физики толкают трупы (бюджет пары за подшаг, предел скорости толчка —
/// толкающая точка + <see cref="PhysicsSettings.PopSpeed"/>), клинок в ударе — ещё и из бюджета прохода; отдача — в корень живого.
/// </summary>
public static class KinematicPush
{
    /// <summary>
    /// Масса удара, кг — тело клинка, предплечье и половина плеча правой руки: 1,4 + 2 + 0,5 · 2,5 = 4,65. Потенциал прохода —
    /// <c>PassShare · масса удара · min(скорость кончика, 16)</c> (до 37,2 Н·с).
    /// </summary>
    public static readonly float StrikeMass =
        FighterShape.Masses[BodySegs.Body(BodySegs.Weapon)] + FighterShape.Masses[BodySegs.Body(BodySegs.FArmR)]
        + 0.5f * FighterShape.Masses[BodySegs.Body(BodySegs.UArmR)];

    /// <summary>Масса бойца, кг (14 тел с клинком — 74,8): отдача делится на неё.</summary>
    public static readonly float FighterMass = Sum(FighterShape.Masses);

    private static float Sum(ReadOnlySpan<float> masses)
    {
        float m = 0;
        foreach (float x in masses) m += x;
        return m;
    }

    /// <summary>Боец в ударе толкает клинком проходом: слой действий — удар, клинок поставлен (как у атакующего проходов клинка).</summary>
    public static bool Striking(in AnimatorAction action, in Blade blade) => action.Kind == ActionKind.Attack && blade.Weapon >= 0;

    /// <summary>
    /// Прибавка скорости корня от отдачи, м/с: горизонталь <paramref name="impulse"/>, Н·с, × <paramref name="share"/> / масса бойца;
    /// уводит скорость корня от заданной <paramref name="target"/> не дальше <paramref name="maxSpeed"/> — отдача не отнимает управления.
    /// </summary>
    public static Vector3 RootDelta(Vector3 impulse, float share, Vector3 velocity, Vector3 target, float maxSpeed)
    {
        var dv = new Vector3(impulse.X, 0, impulse.Z) * (share / FighterMass);
        float len = dv.Length();
        if (!(len > 0) || !float.IsFinite(len)) return Vector3.Zero;
        var n = dv / len;
        var off = velocity - target;
        float along = off.X * n.X + off.Z * n.Z;
        float allowed = Math.Clamp(maxSpeed - along, 0, len);
        return n * allowed;
    }
}

/// <summary>
/// Отдача живого бойца за последний тик, где она была: сырой импульс от физики, Н·с, прибавка к скорости корня, м/с, и тик.
/// <c>Set</c> в React; причина — последнее попадание бойца по трупу в тике, без попадания (только толчки) — без причины.
/// </summary>
[Reactive]
public struct Recoil
{
    /// <summary>Сырой импульс отдачи тика из физики, Н·с (<see cref="IPhysicsWorld.TakeReaction"/>).</summary>
    public Vector3 Impulse;

    /// <summary>Прибавка к скорости корня, м/с.</summary>
    public Vector3 Velocity;

    /// <summary>Тик отдачи.</summary>
    public ulong Tick;

    public static Recoil None => default;
}

/// <summary>
/// PrePhysics, после постановки клинка: чем толкают тела живого в шаге — клинок в ударе (<see cref="KinematicPush.Striking"/>)
/// проходом (<see cref="KinematicSource.Pass"/>), вне удара — телом. Бьющему — удар тика в физику
/// (<see cref="IPhysicsWorld.PreparePass"/>: номер <see cref="AnimatorAction.AttackSerial"/>, масса удара, скорость кончика). Мёртвых
/// пропускает. Памяти не выделяет.
/// </summary>
public sealed class KinematicSourceSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _alive = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.KinematicSource).After(GameLabels.BladePlacement).Reads<AnimatorAction>().Reads<Blade>().Reads<BodyRef>();
        _alive = s.Query().With<Fighter>().Without<Dead>().Read<AnimatorAction>().Read<Blade>().Read<BodyRef>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_alive))
        {
            var actions = chunk.Read<AnimatorAction>();
            var blades = chunk.Read<Blade>()[..actions.Length];
            var bodies = chunk.Read<BodyRef>()[..actions.Length];
            for (int i = 0; i < actions.Length; i++) Tag(actions[i], blades[i], FighterBodies.Handle(bodies[i]), dt);
        }
    }

    private void Tag(in AnimatorAction action, in Blade blade, ArticulationHandle body, float dt)
    {
        if (!BodyPlacement.Placeable(physics, body)) return;   // негодное тело — physics.bodyRefs
        bool striking = KinematicPush.Striking(action, blade);
        physics.SetKinematicSource(physics.Body(body, BodySegs.Body(BodySegs.Weapon)), striking ? KinematicSource.Pass : KinematicSource.Body);
        if (!striking) return;
        float speed = Vector3.Distance(blade.Tip, blade.PrevTip) / dt;
        if (float.IsFinite(speed)) physics.PreparePass(body, action.AttackSerial, KinematicPush.StrikeMass, speed);
    }
}

/// <summary>
/// React: отдача тика — в корень живого (<see cref="IPhysicsWorld.TakeReaction"/> × доля / масса, не дальше
/// <see cref="RagdollRules.RecoilMaxSpeed"/> от заданной скорости; <see cref="KinematicPush.RootDelta"/>), запись —
/// <see cref="Recoil"/>; отдача мёртвого теряется. Затем — <see cref="IPhysicsWorld.EndKinematicTick"/>. Памяти не выделяет.
/// </summary>
public sealed class RootRecoilSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _alive = null!, _dead = null!;

    public void Setup(SystemSetup s)
    {
        // Журнал попаданий не читается: объявление ставит отдачу после попаданий тика (их импульсы дают отдачу).
        s.Label(GameLabels.RootRecoil).ReadsJournal<Event<Hit>>().Reads<BodyRef>().Reads<FighterCommand>().Reads<AnimatorAction>()
            .Writes<RootVelocity>().Sets<Recoil>()
            .ReserveCommands(ArenaRules.MaxFighters, 0, ArenaRules.MaxFighters * 128);
        _alive = s.Query().With<Fighter>().Without<Dead>().Read<BodyRef>().Read<FighterCommand>().Read<AnimatorAction>().Write<RootVelocity>();
        _dead = s.Query().With<Fighter>().With<Dead>().Read<BodyRef>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var rules = world.Resource<RagdollRules>();
        ulong tick = world.Resource<SimTime>().Tick;
        foreach (var chunk in ctx.Iterate(_alive))
        {
            var velocity = chunk.Write<RootVelocity>();
            var entities = chunk.Entities[..velocity.Length];
            var bodies = chunk.Read<BodyRef>()[..velocity.Length];
            var intents = chunk.Read<FighterCommand>()[..velocity.Length];
            var actions = chunk.Read<AnimatorAction>()[..velocity.Length];
            for (int i = 0; i < velocity.Length; i++)
                Apply(ctx, entities[i], FighterBodies.Handle(bodies[i]), intents[i], actions[i], ref velocity[i].Value, rules, tick);
        }
        foreach (var chunk in ctx.Iterate(_dead))
        {
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < bodies.Length; i++) Drop(FighterBodies.Handle(bodies[i]));
        }
        physics.EndKinematicTick();
    }

    private void Apply(in SystemContext ctx, Entity e, ArticulationHandle body, in FighterCommand intent,
        in AnimatorAction action, ref Vector3 velocity, in RagdollRules rules, ulong tick)
    {
        if (!BodyPlacement.Placeable(physics, body)) return;
        var impulse = physics.TakeReaction(body);
        if (impulse == Vector3.Zero) return;
        var world = ctx.World;
        bool player = world.Has<PlayerTag>(e);
        var target = new Vector3(intent.DesiredVelocity.X, 0, intent.DesiredVelocity.Z) * ActionLayer.MoveScale(action, player);
        var dv = KinematicPush.RootDelta(impulse, player ? rules.PlayerRecoil : rules.EnemyRecoil, velocity, target, rules.RecoilMaxSpeed);
        velocity += dv;
        ctx.Commands.Set(e, new Recoil { Impulse = impulse, Velocity = dv, Tick = tick });
    }

    private void Drop(ArticulationHandle body)
    {
        if (BodyPlacement.Placeable(physics, body)) physics.TakeReaction(body);
    }
}
