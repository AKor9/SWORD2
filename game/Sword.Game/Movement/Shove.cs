using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Movement;

/// <summary>
/// Толчок корня ударом (хешируется): скорость, м/с, гаснет за <see cref="ShoveRules.Time"/>. Управление (<see cref="RootVelocity"/>) не
/// трогает — боец отъезжает, но идёт, куда шёл.
/// </summary>
public struct Shove
{
    public Vector3 Velocity;
}

public static class ShoveRules
{
    /// <summary>Время, за которое толчок гаснет линейно, с; наибольший толчок, м/с.</summary>
    public const float Time = 0.25f, MaxSpeed = 3f;

    /// <summary>Толчок плюс прибавка <paramref name="push"/> (по земле), не быстрее <see cref="MaxSpeed"/>.</summary>
    public static Vector3 Add(Vector3 shove, Vector3 push)
    {
        var v = shove + push with { Y = 0 };
        float speed = v.Length();
        return speed > MaxSpeed ? v * (MaxSpeed / speed) : v;
    }

    /// <summary>Шаг тика: скорость гаснет на <c>dt / Time</c> от себя; малая — ноль.</summary>
    public static Vector3 Fade(Vector3 v, float dt)
    {
        v *= MathF.Max(0, 1 - dt / Time);
        return v.LengthSquared() < 1e-4f ? Vector3.Zero : v;
    }
}

/// <summary>Movement, после <see cref="GameLabels.RootControl"/>, до столкновений корня: корень — на скорость толчка; толчок гаснет. Без выделений.</summary>
public sealed class ShoveSystem : IContinuousSystem
{
    private Query _shoved = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.Shove).After(GameLabels.RootControl).Before(GameLabels.RootCollision).Writes<Transform>().Writes<Shove>();
        _shoved = s.Query().With<Fighter>().Without<Dead>().Without<Downed>().Write<Transform>().Write<Shove>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.World.Resource<SimTime>().Dt;
        foreach (var chunk in ctx.Iterate(_shoved))
        {
            var shoves = chunk.Write<Shove>();
            var t = chunk.Write<Transform>()[..shoves.Length];
            for (int i = 0; i < shoves.Length; i++)
            {
                if (shoves[i].Velocity == Vector3.Zero) continue;
                t[i].Position += shoves[i].Velocity * dt;
                t[i].Position.Y = 0;
                shoves[i].Velocity = ShoveRules.Fade(shoves[i].Velocity, dt);
            }
        }
    }
}
