using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Bodies;

/// <summary>
/// Тело бойца (этап 5а): сочленение из <see cref="BodySegs.Bodies"/> кинематических капсул в подсистеме Physics за
/// <see cref="BodyRef"/>. Время жизни — хуками ECS (раздел 5 плана), а не реактивными системами, как у позы: хук
/// добавления заводит сочленение на том же барьере, где у сущности появился <see cref="BodyRef"/>, хук снятия освобождает
/// его, когда компонент снят или сущность удалена, — и боец, построенный и снятый на одном барьере, тела не оставляет.
///
/// Боец появляется с пустым <see cref="BodyRef"/> (<c>Fighters.Create</c>); постройка заново на той же сущности (сброс R,
/// <c>Fighters.Rebuild</c>) компонент не трогает — тело то же, как тела <c>Fighter.Bodies</c> SWORD, которые
/// <c>Build</c> переставляет, а не создаёт. Форму и постановку при постройке задаёт <see cref="BodySetupSystem"/> по
/// <c>Added&lt;Fighter&gt;</c> (5б); до того новое тело без формы в начале координат (<see cref="BodyState.New"/> — как
/// тело SWORD между конструктором бойца и <c>Build</c>).
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
