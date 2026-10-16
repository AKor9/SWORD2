using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Тело бойца: сочленение из <see cref="BodySegs.Bodies"/> капсул за <see cref="BodyRef"/>. Время жизни — хуками ECS: сочленение
/// заводится и освобождается на том же барьере, что и компонент, — боец, построенный и снятый на одном барьере, тела не оставляет.
/// Постройка заново тело не трогает (переставляет); форму задаёт <see cref="BodySetupSystem"/>, до того тело без формы в начале
/// координат.
/// </summary>
public static class FighterBodies
{
    /// <summary>Хуки тела бойца — в <c>GameCore.Configure</c>.</summary>
    public static SimulationBuilder Register(SimulationBuilder b, IPhysicsWorld physics) => b
        .OnAdd<BodyRef>((Entity e, ref BodyRef body) => Acquire(physics, ref body))
        .OnRemove<BodyRef>((Entity e, in BodyRef body) => Release(physics, body));

    /// <summary>
    /// Хук добавления: пустому <see cref="BodyRef"/> — новое сочленение бойца. Непустой не трогается: хук не чинит чужой
    /// или испорченный хэндл молча — его ловит <c>physics.bodyRefs</c>.
    /// </summary>
    public static void Acquire(IPhysicsWorld physics, ref BodyRef body)
    {
        if (!Handle(body).IsNone) return;
        var a = physics.CreateArticulation(BodySegs.Bodies);
        body = new BodyRef { Index = a.Index, Generation = a.Generation };
    }

    /// <summary>Хук снятия: живое сочленение — освободить (не живое — уже освобождено или испорчено: его ловит <c>physics.bodyRefs</c>).</summary>
    public static void Release(IPhysicsWorld physics, in BodyRef body)
    {
        var a = Handle(body);
        if (physics.IsAlive(a)) physics.DestroyArticulation(a);
    }

    public static ArticulationHandle Handle(in BodyRef body) => new(body.Index, body.Generation);
}
