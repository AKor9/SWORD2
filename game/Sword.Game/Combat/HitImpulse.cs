using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Импульс удара в тело бойца — последний приложенный: попадание, кто бил, часть, точка, импульс, Н·с, желаемый, скорость удара и
/// Δv точки, м/с. Реактивный: <c>Set</c> в Resolve с причиной-попаданием. Постройка бойца — <see cref="None"/>.
/// </summary>
[Reactive]
public struct HitImpulse
{
    public ContactId Contact;
    public Entity Attacker;

    /// <summary>Сегмент тела (<see cref="BodySegs"/>); −1 — импульса ещё не было.</summary>
    public int Part;

    public Vector3 Point;

    /// <summary>Приложенный импульс, Н·с (после потолка Δv и списания из прохода).</summary>
    public Vector3 Impulse;

    /// <summary>Сколько хотел удар, Н·с: <c>min(урон · 0,25, 12)</c>.</summary>
    public float Wanted;

    /// <summary>Скорость удара (кончика клинка), м/с.</summary>
    public float Speed;

    /// <summary>Прибавка скорости точки удара вдоль удара, м/с (≤ <see cref="PhysicsSettings.HitDvShare"/> · скорость удара).</summary>
    public float Dv;

    /// <summary>Часть на физике (труп): импульс — из прохода пары, отдача — атакующему; иначе — кинематическая часть в тик смерти.</summary>
    public bool Corpse;

    public static HitImpulse None => new() { Part = -1 };
}

/// <summary>
/// Импульс удара в часть через физику: <c>min(урон · <see cref="DamageShare"/>, <see cref="Max"/>)</c> Н·с вдоль
/// <see cref="StrikeContact.Dir"/>. Труп — проход пары (<see cref="IPhysicsWorld.PassHit"/>) с потолком Δv и отдачей атакующему;
/// убитый этим ударом живой — тот же импульс без прохода. Не убитого живого и пробу импульс не двигает.
/// </summary>
public static class HitImpulses
{
    /// <summary>Доля урона в импульс, Н·с на единицу урона, и наибольший импульс удара, Н·с.</summary>
    public const float DamageShare = 0.25f, Max = 12f;

    /// <summary>Сколько хочет удар, Н·с.</summary>
    public static float Wanted(float damage) => float.IsFinite(damage) ? Math.Clamp(damage * DamageShare, 0, Max) : 0;

    /// <summary>
    /// Импульс касания <paramref name="c"/> в часть жертвы: труп (<paramref name="corpse"/>) или убитый этим касанием
    /// (<paramref name="killed"/>). true — приложено (импульс может быть нулём — проход исчерпан).
    /// </summary>
    public static bool Apply(IPhysicsWorld physics, ArticulationHandle attacker, ArticulationHandle victim, in StrikeContact c, bool corpse, bool killed,
        out HitImpulse record)
    {
        record = HitImpulse.None;
        if (c.Probe || !(c.Speed > 0) || !(corpse || killed) || !BodySegs.IsFlesh(c.Part) || !BodyPlacement.Placeable(physics, victim)) return false;
        float wanted = Wanted(c.Damage);
        if (!(wanted > 0) || !(c.Dir.LengthSquared() > 0.5f) || !Finite(c.Point)) return false;
        var part = physics.Body(victim, BodySegs.Body(c.Part));
        var before = physics.Get(part);
        bool simulated = before.Mode == BodyMode.Simulated;
        if (simulated)
        {
            // из прохода: без живого атакующего с телом прохода нет (Resolve считает касания живых ударов — атакующий был жив в Combat)
            if (!BodyPlacement.Placeable(physics, attacker) || attacker.Index == victim.Index) return false;
            physics.PassHit(attacker, victim);
        }
        var applied = physics.ApplyHit(part, c.Point, c.Dir * wanted, c.Speed, attacker);
        float dv = Vector3.Dot(physics.Get(part).PointVelocity(c.Point) - before.PointVelocity(c.Point), c.Dir);
        record = new HitImpulse
        {
            Contact = c.Id, Attacker = c.Attacker, Part = c.Part, Point = c.Point, Impulse = applied, Wanted = wanted, Speed = c.Speed, Dv = dv,
            Corpse = simulated,
        };
        return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
