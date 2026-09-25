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
/// Толчки живых: живой боец — кинематические тела, в шаге физики они толкают трупы (бюджет пары за подшаг, предел скорости толчка —
/// толкающая точка + <see cref="PhysicsSettings.PopSpeed"/>), клинок в ударе — ещё и из бюджета прохода; отдача толчков и ударов по трупам —
/// в корень живого. Идеи — <c>KinSource</c>, <c>PrepareKinematicContacts</c>, <c>ApplyReactions</c> SWORD 2eab13b (research-arena-tick §4) —
/// источник, не образец.
/// </summary>
public static class KinematicPush
{
    /// <summary>
    /// Масса удара, кг — во что вкладывается бьющий: тело клинка, предплечье и половина плеча правой руки (<c>StrikeMass</c> SWORD):
    /// 1,4 + 2 + 0,5 · 2,5 = 4,65. Потенциал прохода — <c>PassShare · масса удара · min(скорость кончика, 16)</c> (до 37,2 Н·с).
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
    /// Прибавка скорости корня от отдачи, м/с: горизонталь сырого импульса <paramref name="impulse"/>, Н·с, × <paramref name="share"/> /
    /// масса бойца; вдоль себя она уводит скорость корня <paramref name="velocity"/> от заданной <paramref name="target"/> не дальше
    /// <paramref name="maxSpeed"/> (уже увела — ничего). Так отдача не отнимает управления: за тик она добавляет не больше предела, а разгон
    /// бойца возвращает скорость к заданной.
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
/// Отдача живого бойца: последняя, что получил корень, — сырой импульс тика от физики (толчки трупов телами и клинком, удары по трупам),
/// Н·с, прибавка к скорости корня, м/с (горизонталь × доля ручки / масса бойца, в пределе отклонения), и тик. Реактивный: <c>Set</c> в React
/// (<c>core.root-recoil</c>) — только в тике, где отдача была; причина — последнее попадание этого бойца по трупу в тике (<c>state.why</c>:
/// «отдача ← Hit ← касание ← начало удара»), без попадания (только толчки шага физики) — без причины. Постройка бойца — <see cref="None"/>.
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
/// PrePhysics, после постановки клинка (<see cref="GameLabels.KinematicSource"/>): чем толкают тела живого в шаге этого тика
/// (<c>KinSource</c> SWORD) — тело клинка бойца в ударе (<see cref="KinematicPush.Striking"/>) толкает проходом
/// (<see cref="KinematicSource.Pass"/>: сверх бюджета пары — бюджет прохода пары), вне удара — телом, как остальные 13 тел
/// (<see cref="KinematicSource.Body"/> — их никто не меняет). Бьющему — удар тика в физику (<see cref="IPhysicsWorld.PreparePass"/>, номер удара
/// — <see cref="AnimatorAction.AttackSerial"/>, масса удара, скорость кончика |кончик − прошлый кончик| / dt — та же, что у проходов клинка):
/// новый номер — проходы «снаружи», потенциал этого тика — для всех пар бьющего, и клинки врагов толкают трупы врагов (урон — только пары
/// игрок ↔ враг). Мёртвых пропускает (их тела на физике). Памяти не выделяет.
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
/// React (<see cref="GameLabels.RootRecoil"/>): отдача тика — в корень живого (<c>ApplyReactions</c> SWORD). Физика копит у толкающего
/// минус импульс толчков (шаг) и ударов по трупам (Resolve); здесь каждый живой забирает свою (<see cref="IPhysicsWorld.TakeReaction"/>):
/// горизонталь × доля (<see cref="RagdollRules.EnemyRecoil"/>, игроку — <see cref="RagdollRules.PlayerRecoil"/>) / масса бойца — в
/// <see cref="RootVelocity"/>, не дальше предела отклонения от заданной скорости (<see cref="KinematicPush.RootDelta"/>,
/// <see cref="RagdollRules.RecoilMaxSpeed"/>); дальше скорость к заданной возвращает разгон бойца в Gameplay следующего тика. Запись — компонент
/// <see cref="Recoil"/> (причина — попадание по трупу этого тика). Отдача мёртвого (в том числе убитого в этом тике) забирается и теряется.
/// Затем — конец тика проходов (<see cref="IPhysicsWorld.EndKinematicTick"/>): после ударов и отдачи. Стадия React — после Resolve (удары тика)
/// и до Settle (переход убитых в труп). Памяти не выделяет.
/// </summary>
public sealed class RootRecoilSystem(IPhysicsWorld physics) : IContinuousSystem
{
    private Query _alive = null!, _dead = null!;

    public void Setup(SystemSetup s)
    {
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
        var hits = ctx.Journal<Event<Hit>>();
        foreach (var chunk in ctx.Iterate(_alive))
        {
            var velocity = chunk.Write<RootVelocity>();
            var entities = chunk.Entities[..velocity.Length];
            var bodies = chunk.Read<BodyRef>()[..velocity.Length];
            var intents = chunk.Read<FighterCommand>()[..velocity.Length];
            var actions = chunk.Read<AnimatorAction>()[..velocity.Length];
            for (int i = 0; i < velocity.Length; i++)
                Apply(ctx, hits, entities[i], FighterBodies.Handle(bodies[i]), intents[i], actions[i], ref velocity[i].Value, rules, tick);
        }
        foreach (var chunk in ctx.Iterate(_dead))
        {
            var bodies = chunk.Read<BodyRef>();
            for (int i = 0; i < bodies.Length; i++) Drop(FighterBodies.Handle(bodies[i]));
        }
        physics.EndKinematicTick();
    }

    private void Apply(in SystemContext ctx, ReadOnlySpan<Event<Hit>> hits, Entity e, ArticulationHandle body, in FighterCommand intent,
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
        if (ctx.Causality is not null) ctx.Cause = LastCorpseHit(hits, e);
        ctx.Commands.Set(e, new Recoil { Impulse = impulse, Velocity = dv, Tick = tick });
    }

    /// <summary>Причина отдачи — последнее попадание бойца по трупу этого тика (диагностический режим); нет — пусто (только толчки шага).</summary>
    private static CauseId LastCorpseHit(ReadOnlySpan<Event<Hit>> hits, Entity attacker)
    {
        for (int i = hits.Length - 1; i >= 0; i--)
            if (hits[i].Value.Corpse && hits[i].Value.Attacker == attacker) return hits[i].Cause;
        return CauseId.None;
    }

    private void Drop(ArticulationHandle body)
    {
        if (BodyPlacement.Placeable(physics, body)) physics.TakeReaction(body);
    }
}
