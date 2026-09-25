using System.Globalization;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Инварианты толчков живых и ударов по трупам: <c>physics.sources</c> (после PrePhysics — чем толкают тела живых:
/// клинок в ударе — проходом, прочее — телом), <c>ragdoll.hitSource</c> (после Resolve — «физический клинок попаданий не даёт»: попадание
/// клинком — только от кинематического клинка живого в ударе; попадание по трупу — ровно у жертвы, мёртвой до попаданий тика), <c>ragdoll.hitImpulse</c>
/// (после Resolve — импульс удара этого тика: Δv точки ≤ доли скорости удара, не больше, чем хотел удар, у трупа — оплачен проходом пары).
/// Свойства толчков по трассе физики (доли бюджетов пары и прохода, выталкивание толчком) — диагностический <c>physics.pushes</c> (
/// <see cref="CorpseInvariants"/>). Допуск — округление float.
/// </summary>
public static class KinematicPushInvariants
{
    /// <summary>Запас над пределами (округление float): доля, Н·с, м/с.</summary>
    public const float Tolerance = 1e-3f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics) => [new Sources(physics), new HitSource(physics), new Impulses(physics)];

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static int StageIndex(string stage)
    {
        var stages = GameStages.All;
        for (int i = 0; i < stages.Count; i++)
            if (stages[i] == stage) return i;
        return -1;   // между тиками
    }

    private static readonly int PrePhysicsIndex = StageIndex(GameStages.PrePhysics), ResolveIndex = StageIndex(GameStages.Resolve);

    /// <summary>
    /// <c>physics.sources</c> (после PrePhysics; проверка «сейчас» — до Resolve включительно: React может сменить действие реакцией, а метки
    /// ставит PrePhysics следующего тика) — у живого бойца с годным телом тело клинка кинематическое и толкает проходом
    /// (<see cref="KinematicSource.Pass"/>) ровно в ударе (<see cref="KinematicPush.Striking"/>), вне удара — телом. Остальные 13 тел метку
    /// не меняют (<see cref="KinematicSource.Body"/> с постройки; их метки видит тест KinematicPushTests.Sources_…) — проверяется только клинок:
    /// дёшево, каждый тик (33 чтения тела). Ловит клинок, толкающий проходом вне удара (бюджет прохода без удара), и удар, клинок которого толкает
    /// только телом (проход и попадание разошлись).
    /// </summary>
    private sealed class Sources(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _alive;

        public string Id => "physics.sources";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.PrePhysics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            int stage = StageIndex(ctx.Stage);
            if (stage < PrePhysicsIndex || stage > ResolveIndex) return;   // метки этого тика ещё не поставлены или действие могло смениться
            var world = ctx.World;
            _alive ??= world.Query().With<Fighter>().Without<Dead>().Read<AnimatorAction>().Read<Blade>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_alive))
            {
                var entities = chunk.Entities;
                var actions = chunk.Read<AnimatorAction>();
                var blades = chunk.Read<Blade>();
                var bodies = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (Problem(FighterBodies.Handle(bodies[i]), KinematicPush.Striking(actions[i], blades[i])) is not { } problem) continue;
                    violations.Report(entities[i], problem);
                    if (violations.Full) return;
                }
            }
        }

        private string? Problem(ArticulationHandle body, bool striking)
        {
            if (!BodyPlacement.Placeable(physics, body)) return null;   // негодное тело — physics.bodyRefs
            var s = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Weapon)));
            if (s.Mode != BodyMode.Kinematic) return null;   // живой не кинематический — physics.modes
            var want = striking ? KinematicSource.Pass : KinematicSource.Body;
            return s.Source == want ? null : Text($"клинок живого толкает {s.Source}, а должен {want} (удар — {striking})");
        }
    }

    /// <summary>Журнал попаданий мира (номер) — попадания тика читаются из него: последний оборот тика, возраст 1 — барьер Resolve.</summary>
    private static int HitJournal(WorldReader world)
    {
        var journals = world.Journals;
        for (int j = 0; j < journals.Count; j++)
            if (journals[j].EntryType == typeof(Event<Hit>)) return j;
        return -1;
    }

    /// <summary>
    /// <c>ragdoll.hitSource</c> (после Resolve) — физический клинок попаданий не даёт: каждое попадание клинком этого тика (не проба) — от
    /// бойца, живого до тика (убитого этим же тиком — можно: касания тика посчитаны до смертей), в ударе, чьё тело клинка кинематическое и
    /// толкает проходом (<see cref="KinematicSource.Pass"/>; у трупа тело клинка — на физике). Попадание по трупу (<see cref="Hit.Corpse"/>)
    /// — ровно у жертвы, мёртвой до попаданий тика (смерть прошлых тиков или команда сцены этого тика, не попадание этого тика); попадание по
    /// трупу никого не убивает.
    /// </summary>
    private sealed class HitSource(IPhysicsWorld physics) : IInvariant
    {
        private readonly List<Entity> _killed = [];

        public string Id => "ragdoll.hitSource";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.Resolve;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            int journal = HitJournal(world);
            if (journal < 0) return;
            float now = world.Resource<SimTime>().Time;
            DamageInvariants.Hits.Killed(world, journal, _killed);
            for (int i = 0, n = world.JournalLength(journal); i < n; i++)
            {
                var bytes = world.JournalEntry(journal, i, out long age);
                if (age != 1 || bytes.IsEmpty) continue;   // только попадания этого тика (сделаны барьером Resolve)
                var hit = MemoryMarshal.Read<Event<Hit>>(bytes).Value;
                if (Problem(world, hit, now) is not { } problem) continue;
                violations.Report(hit.Victim, problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(WorldReader world, in Hit hit, float now)
        {
            if (!world.IsAlive(hit.Victim) || !world.IsAlive(hit.Attacker)) return null;   // не бойцы — combat.hits
            // труп — мёртв до попаданий тика: смерть прошлых тиков или команда сцены этого тика, не попадание этого тика
            bool corpse = DamageInvariants.Hits.IsCorpse(world, hit.Victim, now, _killed);
            if (hit.Corpse != corpse) return Text($"попадание {hit.Attacker} → {hit.Victim}: «по трупу» {hit.Corpse}, а жертва мертва до попаданий тика — {corpse}");
            if (hit.Corpse && hit.Killed) return Text($"попадание по трупу {hit.Victim} убило его второй раз");
            if (hit.Probe) return null;
            if (DamageInvariants.Hits.IsCorpse(world, hit.Attacker, now, _killed)) return Text($"попадание от трупа {hit.Attacker}");
            if (!world.TryGet<BodyRef>(hit.Attacker, out var bodyRef)) return Text($"попадание от {hit.Attacker} без тела");
            var body = FighterBodies.Handle(bodyRef);
            if (!BodyPlacement.Placeable(physics, body)) return null;   // негодное тело — physics.bodyRefs
            var blade = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Weapon)));
            if (blade.Mode != BodyMode.Kinematic || blade.Source != KinematicSource.Pass)
                return Text($"попадание {hit.Attacker} → {hit.Victim} клинком {blade.Mode}, толчок {blade.Source} — попадание даёт только кинематический клинок в ударе (Pass)");
            return null;
        }
    }

    /// <summary>
    /// <c>ragdoll.hitImpulse</c> (после Resolve; проверка «сейчас» — с Resolve до конца тика) — импульс удара этого тика в тело бойца
    /// (<see cref="HitImpulse"/>): Δv точки вдоль удара — не больше <see cref="PhysicsSettings.HitDvShare"/> скорости удара, приложенный — не
    /// больше, чем хотел удар; у части трупа — из прохода: проход пары атакующий → труп открыт, остаток — в пределах бюджета, потрачено с
    /// открытия — не меньше приложенного (импульс за проход ≤ бюджета прохода).
    /// </summary>
    private sealed class Impulses(IPhysicsWorld physics) : IInvariant
    {
        private ReadQuery? _fighters;

        public string Id => "ragdoll.hitImpulse";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.Resolve;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            int stage = StageIndex(ctx.Stage);
            if (stage >= 0 && stage < ResolveIndex) return;   // импульсы этого тика — с Resolve
            var world = ctx.World;
            ulong tick = world.Resource<SimTime>().Tick;
            float share = physics.Settings.HitDvShare;
            _fighters ??= world.Query().With<Fighter>().Read<HitImpulse>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_fighters))
            {
                var entities = chunk.Entities;
                var impulses = chunk.Read<HitImpulse>();
                var bodies = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (impulses[i].Part < 0 || impulses[i].Contact.Tick != tick) continue;
                    if (Problem(world, impulses[i], FighterBodies.Handle(bodies[i]), share) is not { } problem) continue;
                    violations.Report(entities[i], problem);
                    if (violations.Full) return;
                }
            }
        }

        private string? Problem(WorldReader world, in HitImpulse h, ArticulationHandle victim, float share)
        {
            float j = h.Impulse.Length();
            if (!float.IsFinite(j) || !float.IsFinite(h.Dv)) return Text($"импульс удара нечисловой: {h.Impulse}, Δv {h.Dv}");
            if (h.Dv > share * h.Speed + Tolerance) return Text($"Δv точки удара {h.Dv:G4} м/с больше {share} × скорость удара {h.Speed:G4} м/с");
            if (j > h.Wanted + Tolerance) return Text($"импульс удара {j:G4} Н·с больше, чем хотел удар ({h.Wanted:G4})");
            if (!h.Corpse) return null;
            if (!world.IsAlive(h.Attacker) || !world.TryGet<BodyRef>(h.Attacker, out var bodyRef)) return null;   // атакующий снят в тике — нечем сверить
            var attacker = FighterBodies.Handle(bodyRef);
            if (!BodyPlacement.Placeable(physics, attacker) || !BodyPlacement.Placeable(physics, victim) || attacker.Index == victim.Index) return null;
            var pass = physics.Pass(attacker, victim);
            if (!pass.Inside) return Text($"импульс по трупу {j:G4} Н·с, а проход пары {attacker} → {victim} закрыт");
            if (pass.Remaining < 0 || pass.Remaining > pass.Potential + Tolerance)
                return Text($"проход пары {attacker} → {victim}: остаток {pass.Remaining:G4} вне бюджета {pass.Potential:G4} Н·с");
            if (pass.Potential - pass.Remaining < j - Tolerance)
                return Text($"импульс по трупу {j:G4} Н·с больше потраченного из прохода ({pass.Potential - pass.Remaining:G4} из {pass.Potential:G4})");
            return null;
        }
    }
}
