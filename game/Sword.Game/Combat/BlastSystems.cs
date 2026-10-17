using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Взрыв (<see cref="BlastCommand"/>): энергия, дошедшая до части, — доля сферы, которую часть закрывает силуэтом капсулы
/// (<c>E · S / (4π d²)</c>, d — не ближе 0,25 м), ослабление к краю радиуса — линейное. Скорость части — от этой энергии:
/// <c>√(2·E·k/m)</c> (k — доля энергии в движение). Сумма энергии частей выше порога — живой мёртв.
/// </summary>
public static class Blasts
{
    /// <summary>Ближе центра взрыва часть не считается, м (вне — точечный источник).</summary>
    public const float MinDistance = 0.25f;

    /// <summary>Доля дошедшей энергии, уходящая в движение части.</summary>
    public const float Coupling = 0.35f;

    /// <summary>Энергия взрыва в тело живого, с которой он мёртв, Дж.</summary>
    public const float Lethal = 150f;

    /// <summary>Энергия взрыва в часть: силуэт капсулы — <c>2r·(2h + 2r)</c>.</summary>
    public static float Energy(in BlastCommand b, in BodyState s)
    {
        if (!(b.Energy > 0) || !(b.Radius > 0)) return 0;
        float d = MathF.Max(Vector3.Distance(s.Position, b.Position), MinDistance);
        if (d >= b.Radius) return 0;
        float area = 2 * s.Radius * (2 * s.HalfLength + 2 * s.Radius);
        return b.Energy * area / (4 * MathF.PI * d * d) * (1 - d / b.Radius);
    }

    /// <summary>Скорость части от взрыва: от центра (и чуть вверх), по энергии и массе.</summary>
    public static Vector3 Velocity(in BlastCommand b, in BodyState s, float energy)
    {
        if (!(energy > 0) || !(s.Mass > 0)) return Vector3.Zero;
        var dir = s.Position - b.Position + new Vector3(0, 0.3f, 0);
        dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitY;
        return dir * MathF.Sqrt(2 * energy * Coupling / s.Mass);
    }
}

/// <summary>
/// Resolve, реактивная по команде <see cref="BlastCommand"/>: по каждой части каждого бойца в радиусе — заявка
/// <see cref="SeverRequested"/> вида <see cref="HitKind.Blast"/> (энергия и скорость части; решает модуль Cutting); живой, в которого
/// пришло больше <see cref="Blasts.Lethal"/>, — мёртв. Толкает тела <see cref="BlastPushSystem"/> в Settle, когда убитые уже трупы.
/// </summary>
public sealed class BlastSystem(IPhysicsWorld physics) : IReactiveSystem<Event<BlastCommand>>
{
    private Query _fighters = null!;
    private int _requests;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.Blast).After(GameLabels.HitApply).Reads<BodyRef>().Reads<Dead>().Adds<Dead>().Emits<SeverRequested>()
            .ReserveCommands(ArenaRules.MaxFighters, ArenaRules.MaxFighters * BodySegs.Count, ArenaRules.MaxFighters * BodySegs.Count * 160);
        _fighters = s.Query().With<Fighter>().Read<BodyRef>();
    }

    public void React(in SystemContext ctx, ReadOnlySpan<Event<BlastCommand>> blasts)
    {
        var time = ctx.World.Resource<SimTime>();
        _requests = 1 << 16;   // номера заявок взрыва — после заявок ударов тика
        foreach (var b in blasts)
            foreach (var chunk in ctx.Iterate(_fighters))
            {
                var entities = chunk.Entities;
                var bodies = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++) Fighter(ctx, b.Value, entities[i], bodies[i], time);
            }
    }

    private void Fighter(in SystemContext ctx, in BlastCommand b, Entity e, BodyRef bodyRef, in SimTime time)
    {
        var body = FighterBodies.Handle(bodyRef);
        if (!BodyPlacement.Placeable(physics, body)) return;
        bool dead = ctx.World.Has<Dead>(e);
        float total = 0;
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            if (!BodySegs.IsFlesh(k)) continue;
            var s = physics.Get(physics.Body(body, k));
            float energy = Blasts.Energy(b, s);
            if (!(energy > 0)) continue;
            total += energy;
            ctx.Commands.Emit(new SeverRequested
            {
                Id = new RequestId(time.Tick, _requests++), Victim = e, Part = k, Point = s.Position, Kind = HitKind.Blast,
                Velocity = Blasts.Velocity(b, s, energy), Energy = energy, Sharpness = 1, Corpse = dead, Dir = Vector3.Normalize(s.Position - b.Position),
            });
        }
        if (!dead && total >= Blasts.Lethal) ctx.Commands.Add(e, new Dead { Time = time.Time, Cause = DeathCause.Blast });
    }
}

/// <summary>
/// Settle (после перехода в труп), реактивная по <see cref="BlastCommand"/>: толчок взрыва всем телам на физике в радиусе — части
/// трупов, куски, отделённое у живых: импульс <c>m · v</c> по <see cref="Blasts.Velocity"/>.
/// </summary>
public sealed class BlastPushSystem(IPhysicsWorld physics) : IReactiveSystem<Event<BlastCommand>>
{
    public void Setup(SystemSetup s) => s.Label(GameLabels.BlastPush).After(GameLabels.DeathToRagdoll);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<BlastCommand>> blasts)
    {
        foreach (var b in blasts)
            for (int a = 0, n = physics.ArticulationCapacity; a < n; a++) Push(b.Value, physics.ArticulationAt(a));
    }

    private void Push(in BlastCommand b, ArticulationHandle a)
    {
        if (a.IsNone) return;
        for (int k = 0, n = physics.BodyCount(a); k < n; k++)
        {
            var handle = physics.Body(a, k);
            var s = physics.Get(handle);
            if (s.Mode != BodyMode.Simulated) continue;
            float energy = Blasts.Energy(b, s);
            if (energy > 0) physics.ApplyHit(handle, s.Position, Blasts.Velocity(b, s, energy) * s.Mass, 0, ArticulationHandle.None);
        }
    }
}
