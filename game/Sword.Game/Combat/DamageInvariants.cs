using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Инварианты урона (раздел 8 плана; этап 6г), их регистрирует ядро: <c>combat.health</c> и <c>combat.dead</c> — в конце End (здоровье
/// согласовано, мёртвый замер), <c>combat.hits</c> — после Resolve (попадания тика по правилам прохода клинка), <c>combat.respawn</c> — в
/// конце End (мёртвый враг не лежит дольше 8 с).
/// </summary>
public static class DamageInvariants
{
    public static IEnumerable<IInvariant> For() => [new HealthState(), new DeadFrozen(), new Hits(), new Respawn()];

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>combat.health</c> — у каждого бойца есть <see cref="Health"/>; очки жизни и кровотечение конечны, кровотечение не меньше 0, раны —
    /// 0…1,5; у живого очки жизни больше 0 и рана торса меньше 1,4 (иначе он мёртв — <c>ApplyHit</c>, <c>AfterTick</c>); у мёртвого время
    /// смерти конечно и не позже нынешнего. В конце End.
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
    /// <c>combat.dead</c> — мёртвый не действует и не движется (до рэгдолла — этап 7): с конца тика смерти у него те же корень и курс,
    /// действие (вид, время, вес, номер удара) и клинок, пока метку не снимет постройка заново. Память — по номеру сущности. В конце End.
    /// </summary>
    private sealed class DeadFrozen : IInvariant
    {
        private record struct Frozen(Entity Entity, Vector3 Position, float Facing, ActionKind Kind, float Time, float Weight, int Serial, Vector3 Tip);

        private ReadQuery? _dead;
        private Frozen[] _memory = [];

        public string Id => "combat.dead";
        public InvariantCost Cost => InvariantCost.EveryTick;
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            _dead ??= world.Query().With<Fighter>().With<Dead>().Read<Transform>().Read<AnimatorAction>().Read<Blade>();
            foreach (var chunk in world.Iterate(_dead))
            {
                var entities = chunk.Entities;
                var roots = chunk.Read<Transform>();
                var actions = chunk.Read<AnimatorAction>();
                var blades = chunk.Read<Blade>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    var now = new Frozen(e, roots[i].Position, roots[i].Facing, actions[i].Kind, actions[i].Time, actions[i].Weight, actions[i].AttackSerial, blades[i].Tip);
                    if ((uint)e.Index >= (uint)_memory.Length) Array.Resize(ref _memory, Math.Max(e.Index + 1, _memory.Length * 2));
                    ref var before = ref _memory[e.Index];
                    // Первый тик смерти (или другой боец на этом номере) — запомнить; иначе должен совпасть с запомненным.
                    if (before.Entity != e)
                    {
                        before = now;
                        continue;
                    }
                    if (now == before) continue;
                    violations.Report(e, Text($"мёртвый (с {world.Get<Dead>(e).Time} с) изменился: корень {before.Position} → {now.Position}, курс {before.Facing} → {now.Facing}, действие {before.Kind} {before.Time} → {now.Kind} {now.Time}, номер удара {before.Serial} → {now.Serial}, кончик {before.Tip} → {now.Tip}"));
                    before = now;
                    if (violations.Full) return;
                }
            }
            // ожившие (построены заново) — память по номеру сотрётся при следующей смерти: сущность та же, но запись — прошлой смерти
            for (int i = 0; i < _memory.Length; i++)
                if (!_memory[i].Entity.IsNull && !world.Has<Dead>(_memory[i].Entity)) _memory[i] = default;
        }
    }

    /// <summary>
    /// <c>combat.hits</c> — попадания тика (<see cref="Hit"/>, после Resolve) по правилам: атакующий и жертва — живые до тика бойцы
    /// (жертва могла умереть этим попаданием — тогда оно последнее по ней в тике и единственное убившее); жертва не неуязвима
    /// (<c>combat.playerInvulnerable</c>, неуязвимость рывка). Касание клинка (не проба): пара игрок ↔ враг (враг не бьёт врага),
    /// атакующий — в ударе; проход клинка засчитан этим тиком (пара врага <see cref="StrikePairs"/>: клинок в теле, касание сейчас,
    /// номер удара — атакующего), а в прошлом тике клинок был вне тела или удар был другой, и прошлое касание пары — не ближе 0,1 с.
    /// Память пар — с прошлого тика (пропуск тиков — без сверки с прошлым).
    /// </summary>
    private sealed class Hits : IInvariant
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

        private string? Problem(WorldReader world, in Hit hit, in SimTime time, in CombatRules rules, bool continuous)
        {
            if (!world.IsAlive(hit.Attacker) || !world.Has<Fighter>(hit.Attacker) || !world.IsAlive(hit.Victim) || !world.Has<Fighter>(hit.Victim))
                return Text($"попадание {hit.Attacker} → {hit.Victim}: не бойцы");
            // труп — не цель: жертва мертва до этого тика или уже убита прошлым попаданием тика
            foreach (var (victim, killed) in _seen)
                if (victim == hit.Victim && killed) return Text($"попадание в {hit.Victim} после убившего его попадания того же тика");
            _seen.Add((hit.Victim, hit.Killed));
            if (world.TryGet<Dead>(hit.Victim, out var dead) && dead.Time < time.Time)
                return Text($"попадание в труп {hit.Victim} (мёртв с {dead.Time} с)");
            if (hit.Killed && !world.Has<Dead>(hit.Victim)) return Text($"попадание убило {hit.Victim}, а метки смерти нет");
            if (world.TryGet<PlayerControl>(hit.Victim, out var control) && world.Has<PlayerTag>(hit.Victim)
                && (rules.PlayerInvulnerable || control.Invulnerable > 0))
                return Text($"попадание в неуязвимого игрока (combat.playerInvulnerable {rules.PlayerInvulnerable}, рывок {control.Invulnerable} с)");
            if (hit.Probe) return null;
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
    /// <c>combat.respawn</c> — мёртвый враг не лежит дольше 8 с: возрождение — в тике, где с его смерти прошло больше 8 с
    /// (<c>Time − DeadTime &gt; 8</c>), поэтому в конце любого тика у мёртвого врага прошло не больше 8 с. В конце End.
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
