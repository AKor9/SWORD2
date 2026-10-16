using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Инварианты урона: <c>combat.health</c>, <c>combat.dead</c>, <c>combat.respawn</c> — в конце End; <c>combat.hits</c> — после Resolve.
/// </summary>
public static class DamageInvariants
{
    public static IEnumerable<IInvariant> For(IPhysicsWorld physics) => [new HealthState(), new DeadFrozen(physics), new Hits(), new Respawn()];

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>combat.health</c> — у каждого бойца есть <see cref="Health"/>; числа конечны, раны 0…1,5; у живого очки жизни больше 0 и
    /// рана торса меньше 1,4; у мёртвого время смерти не позже нынешнего.
    /// </summary>
    private sealed class HealthState : IInvariant
    {
        private ReadQuery? _bare, _fighters;

        public string Id => "combat.health";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            float now = world.Resource<SimTime>().Time;
            _bare ??= world.Query().With<Fighter>().Without<Health>();
            foreach (var chunk in world.Iterate(_bare))
                foreach (var e in chunk.Entities)
                {
                    violations.Report(e, "боец без здоровья (Health)");
                    if (violations.Full) return;
                }
            _fighters ??= world.Query().With<Fighter>().Read<Health>();
            foreach (var chunk in world.Iterate(_fighters))
            {
                var entities = chunk.Entities;
                var health = chunk.Read<Health>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    bool dead = world.TryGet<Dead>(e, out var d);
                    if (Problem(health[i], dead, d, now) is not { } problem) continue;
                    violations.Report(e, problem);
                    if (violations.Full) return;
                }
            }
        }

        private static string? Problem(in Health h, bool dead, in Dead d, float now)
        {
            if (!float.IsFinite(h.Hp) || !float.IsFinite(h.Bleed) || h.Bleed < 0) return Text($"очки жизни {h.Hp}, кровотечение {h.Bleed}");
            for (int s = 0; s < BodySegs.Count; s++)
                if (!(h.Wounds[s] is >= 0 and <= DamageRules.WoundMax)) return Text($"рана {BodySegs.Name(s)} {h.Wounds[s]} вне 0…{DamageRules.WoundMax}");
            if (dead) return float.IsFinite(d.Time) && d.Time <= now ? null : Text($"время смерти {d.Time} (сейчас {now})");
            if (h.Hp <= 0) return Text($"жив, а очков жизни {h.Hp}");
            float torso = h.Wounds[BodySegs.Torso];
            return torso >= DamageRules.TorsoWoundDeath ? Text($"жив, а рана торса {torso} (смерть — {DamageRules.TorsoWoundDeath})") : null;
        }
    }

    /// <summary>
    /// <c>combat.dead</c> — мёртвый не действует: с конца тика смерти курс, действие и клинок-отрезок те же, пока его не построят заново;
    /// (x, z) корня равны (x, z) таза трупа со следующего тика после смерти. Память — по номеру сущности.
    /// </summary>
    private sealed class DeadFrozen(IPhysicsWorld physics) : IInvariant
    {
        private record struct Frozen(Entity Entity, float Facing, ActionKind Kind, float Time, float Weight, int Serial, Vector3 Tip);

        private ReadQuery? _dead;
        private Frozen[] _memory = [];

        public string Id => "combat.dead";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            float time = world.Resource<SimTime>().Time;
            // корень за тазом сверяется, когда шаг физики этого тика прошёл (проверка «сейчас» до Physics — таз мог сдвинуть сдвиг сцены)
            bool followed = PhysicsDone(ctx.Stage);
            _dead ??= world.Query().With<Fighter>().Read<Dead>().Read<Transform>().Read<AnimatorAction>().Read<Blade>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_dead))
            {
                var entities = chunk.Entities;
                var dead = chunk.Read<Dead>();
                var roots = chunk.Read<Transform>();
                var actions = chunk.Read<AnimatorAction>();
                var blades = chunk.Read<Blade>();
                var bodies = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    if (followed && dead[i].Time < time && Pelvis(bodies[i], roots[i]) is { } problem)
                    {
                        violations.Report(e, problem);
                        if (violations.Full) return;
                    }
                    var now = new Frozen(e, roots[i].Facing, actions[i].Kind, actions[i].Time, actions[i].Weight, actions[i].AttackSerial, blades[i].Tip);
                    if ((uint)e.Index >= (uint)_memory.Length) Array.Resize(ref _memory, Math.Max(e.Index + 1, _memory.Length * 2));
                    ref var before = ref _memory[e.Index];
                    // Первый тик смерти (или другой боец на этом номере) — запомнить; иначе должен совпасть с запомненным.
                    if (before.Entity != e)
                    {
                        before = now;
                        continue;
                    }
                    if (now == before) continue;
                    violations.Report(e, Text($"мёртвый (с {dead[i].Time} с) действует: курс {before.Facing} → {now.Facing}, действие {before.Kind} {before.Time} → {now.Kind} {now.Time}, номер удара {before.Serial} → {now.Serial}, кончик {before.Tip} → {now.Tip}"));
                    before = now;
                    if (violations.Full) return;
                }
            }
            // ожившие (построены заново) — память по номеру сотрётся при следующей смерти: сущность та же, но запись — прошлой смерти
            for (int i = 0; i < _memory.Length; i++)
                if (!_memory[i].Entity.IsNull && !world.Has<Dead>(_memory[i].Entity)) _memory[i] = default;
        }

        /// <summary>Корень мёртвого не за тазом трупа (таз на физике и числовой) — что не так; иначе null.</summary>
        private string? Pelvis(BodyRef bodyRef, in Transform root)
        {
            var body = FighterBodies.Handle(bodyRef);
            if (!BodyPlacement.Placeable(physics, body)) return null;   // негодное тело — physics.bodyRefs
            var pelvis = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Pelvis)));
            if (pelvis.Mode != BodyMode.Simulated || !pelvis.IsFinite) return null;   // не труп — physics.modes, нечисловой — physics.finite
            if (root.Position.X == pelvis.Position.X && root.Position.Z == pelvis.Position.Z) return null;
            return Text($"корень мёртвого ({root.Position.X}, {root.Position.Z}) не за тазом трупа ({pelvis.Position.X}, {pelvis.Position.Z})");
        }

        /// <summary>Шаг физики этого тика прошёл: проверка в конце тика или посреди него после Physics.</summary>
        private static bool PhysicsDone(string stage)
        {
            var stages = GameStages.All;
            for (int i = 0; i < stages.Count; i++)
                if (stages[i] == stage) return i >= PhysicsIndex;
            return true;   // между тиками
        }

        private static readonly int PhysicsIndex = IndexOf(GameStages.Physics);

        private static int IndexOf(string stage)
        {
            for (int i = 0; i < GameStages.All.Count; i++)
                if (GameStages.All[i] == stage) return i;
            throw new InvalidOperationException($"нет стадии {stage}");
        }
    }

    /// <summary>
    /// <c>combat.hits</c> — попадания тика по правилам: атакующий — не труп; жертва живая (убившее попадание — последнее по ней) или
    /// труп (никого не убивает), не неуязвима. Касание клинком: пара игрок ↔ враг, атакующий в ударе, проход засчитан этим тиком
    /// (в прошлом клинок был вне тела или удар другой) и прошлое касание пары — не ближе 0,1 с.
    /// </summary>
    internal sealed class Hits : IInvariant
    {
        private record struct Pass(Entity Enemy, StrikePairs Pairs);

        private ReadQuery? _enemies;
        private Pass[] _before = [], _now = [];
        private ulong _tick = ulong.MaxValue;
        private readonly List<(Entity Victim, bool Killed)> _seen = [];

        public string Id => "combat.hits";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.Resolve;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            var time = world.Resource<SimTime>();
            bool scheduled = ctx.Stage == After;
            bool continuous = scheduled && ctx.Tick == _tick + 1;
            _enemies ??= world.Query().With<Fighter>().With<EnemyTag>().Read<StrikePairs>();
            if (scheduled)
            {
                (_before, _now) = (_now, _before);
                if (_now.Length < _before.Length) _now = new Pass[_before.Length];
                Array.Clear(_now);
                foreach (var chunk in world.Iterate(_enemies))
                {
                    var entities = chunk.Entities;
                    var pairs = chunk.Read<StrikePairs>();
                    for (int i = 0; i < entities.Length; i++)
                    {
                        if ((uint)entities[i].Index >= (uint)_now.Length) Array.Resize(ref _now, Math.Max(entities[i].Index + 1, _now.Length * 2));
                        _now[entities[i].Index] = new Pass(entities[i], pairs[i]);
                    }
                }
                _tick = ctx.Tick;
            }
            int journal = Journal(world);
            if (journal < 0) return;
            var rules = world.Resource<CombatRules>();
            _seen.Clear();
            Killed(world, journal, _killed);
            for (int i = 0, n = world.JournalLength(journal); i < n; i++)
            {
                var bytes = world.JournalEntry(journal, i, out long age);
                if (age != 1 || bytes.IsEmpty) continue;   // только попадания этого тика (сделаны барьером Resolve)
                var hit = MemoryMarshal.Read<Event<Hit>>(bytes).Value;
                if (Problem(world, hit, time, rules, continuous) is { } problem)
                {
                    violations.Report(hit.Victim, problem);
                    if (violations.Full) return;
                }
            }
        }

        private static int Journal(WorldReader world)
        {
            var journals = world.Journals;
            for (int j = 0; j < journals.Count; j++)
                if (journals[j].EntryType == typeof(Event<Hit>)) return j;
            return -1;
        }

        private readonly List<Entity> _killed = [];

        /// <summary>Жертвы, которых убили попадания этого тика (журнал попаданий <paramref name="journal"/>, возраст 1) — в <paramref name="into"/>.</summary>
        internal static void Killed(WorldReader world, int journal, List<Entity> into)
        {
            into.Clear();
            for (int i = 0, n = world.JournalLength(journal); i < n; i++)
            {
                var bytes = world.JournalEntry(journal, i, out long age);
                if (age != 1 || bytes.IsEmpty) continue;
                var hit = MemoryMarshal.Read<Event<Hit>>(bytes).Value;
                if (hit.Killed) into.Add(hit.Victim);
            }
        }

        /// <summary>
        /// Жертва — труп для попаданий этого тика: мертва до Resolve, а не от попадания этого тика (<paramref name="killed"/>).
        /// </summary>
        internal static bool IsCorpse(WorldReader world, Entity victim, float now, List<Entity> killed) =>
            world.TryGet<Dead>(victim, out var dead) && (dead.Time < now || !killed.Contains(victim));
        private string? Problem(WorldReader world, in Hit hit, in SimTime time, in CombatRules rules, bool continuous)
        {
            if (!world.IsAlive(hit.Attacker) || !world.Has<Fighter>(hit.Attacker) || !world.IsAlive(hit.Victim) || !world.Has<Fighter>(hit.Victim))
                return Text($"попадание {hit.Attacker} → {hit.Victim}: не бойцы");
            // жертва, убитая прошлым попаданием тика, — не цель (тело ещё не труп); мёртвая до тика — труп, снова цель
            foreach (var (victim, killed) in _seen)
                if (victim == hit.Victim && killed) return Text($"попадание в {hit.Victim} после убившего его попадания того же тика");
            _seen.Add((hit.Victim, hit.Killed));
            bool corpse = IsCorpse(world, hit.Victim, time.Time, _killed);
            if (corpse != hit.Corpse) return Text($"попадание в {hit.Victim}: «по трупу» {hit.Corpse}, а мёртв до попаданий тика — {corpse}");
            if (hit.Corpse && hit.Killed) return Text($"попадание по трупу {hit.Victim} убило его второй раз");
            if (hit.Killed && !world.Has<Dead>(hit.Victim)) return Text($"попадание убило {hit.Victim}, а метки смерти нет");
            if (world.TryGet<PlayerControl>(hit.Victim, out var control) && world.Has<PlayerTag>(hit.Victim)
                && (rules.PlayerInvulnerable || control.Invulnerable > 0))
                return Text($"попадание в неуязвимого игрока (combat.playerInvulnerable {rules.PlayerInvulnerable}, рывок {control.Invulnerable} с)");
            if (hit.Probe) return null;   // проба — синтетическое касание: атакующий может быть и мёртвым
            if (IsCorpse(world, hit.Attacker, time.Time, _killed)) return Text($"попадание клинком от трупа {hit.Attacker}");
            bool fromPlayer = world.Has<PlayerTag>(hit.Attacker), toPlayer = world.Has<PlayerTag>(hit.Victim);
            if (fromPlayer == toPlayer) return Text($"попадание {hit.Attacker} → {hit.Victim}: пары — только игрок ↔ враг");
            var action = world.Get<AnimatorAction>(hit.Attacker);
            if (action.Kind != ActionKind.Attack) return Text($"попадание от {hit.Attacker} не в ударе ({action.Kind})");
            var enemy = fromPlayer ? hit.Victim : hit.Attacker;
            if (!world.TryGet<StrikePairs>(enemy, out var pairs)) return Text($"у врага {enemy} нет пар удара (StrikePairs)");
            var pass = fromPlayer ? pairs.FromPlayer : pairs.ToPlayer;
            if (!pass.InContact || pass.LastHit != time.Time || pass.Serial != action.AttackSerial)
                return Text($"проход не засчитан этим тиком: клинок в теле {pass.InContact}, касание в {pass.LastHit} с (сейчас {time.Time}), удар {pass.Serial} (у атакующего {action.AttackSerial})");
            if (!continuous || (uint)enemy.Index >= (uint)_before.Length || _before[enemy.Index].Enemy != enemy) return null;
            var old = fromPlayer ? _before[enemy.Index].Pairs.FromPlayer : _before[enemy.Index].Pairs.ToPlayer;
            if (old.InContact && old.Serial == pass.Serial) return Text($"второе попадание за проход клинка (удар {pass.Serial})");
            if (time.Time - old.LastHit < DamageRules.HitCooldown - 1e-4f)
                return Text($"попадания пары чаще 0,1 с: {old.LastHit} → {time.Time} с");
            return null;
        }

    }

    /// <summary>
    /// <c>combat.respawn</c> — мёртвый враг не лежит дольше 8 с (возрождение — в тике, где прошло больше 8 с). В конце End.
    /// </summary>
    private sealed class Respawn : IInvariant
    {
        private ReadQuery? _dead;

        public string Id => "combat.respawn";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            float now = world.Resource<SimTime>().Time;
            _dead ??= world.Query().With<Fighter>().With<EnemyTag>().Read<Dead>();
            foreach (var chunk in world.Iterate(_dead))
            {
                var entities = chunk.Entities;
                var dead = chunk.Read<Dead>();
                for (int i = 0; i < entities.Length; i++)
                {
                    if (!(now - dead[i].Time > DamageRules.RespawnDelay)) continue;
                    violations.Report(entities[i], Text($"враг мёртв {now - dead[i].Time} с — возрождение через {DamageRules.RespawnDelay} с не случилось"));
                    if (violations.Full) return;
                }
            }
        }
    }
}
