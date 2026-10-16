using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Data;

namespace Sword.Game.Combat;

/// <summary>
/// Запас буферов команд систем боя (<see cref="SystemSetup.ReserveCommands"/>): пик полной арены — касания, попадания, смерти и
/// возрождения в одном тике — без роста буферов посреди боя.
/// </summary>
public static class CombatReserve
{
    /// <summary>Касаний за тик: по два на бойца (его клинок и клинок в него) — с запасом.</summary>
    public const int Contacts = ArenaRules.MaxFighters * 2;

    /// <summary>Команд постройки места заново (<see cref="Fighters.Rebuild"/>: снять и поставить метку, снять смерть, полтора десятка Set).</summary>
    public const int RebuildCommands = 24;

    /// <summary>Байт значений одной постройки заново (компоненты бойца — с запасом).</summary>
    public const int RebuildBytes = 1024;
}

/// <summary>
/// Resolve, реактивная по <see cref="StrikeContact"/>: касания тика по порядку; убитая прошлым касанием тика жертва и неуязвимый
/// игрок не засчитываются. Иначе — урон, рана, кровотечение (<see cref="DamageRules.Apply"/>), заявка отруба
/// (<see cref="SeverRequested"/>), смерть (<see cref="Dead"/>), событие <see cref="Hit"/>. Попадание по трупу — урон и раны без
/// второй смерти; импульс удара в часть — через <see cref="HitImpulses.Apply"/>.
/// </summary>
public sealed class HitApplySystem(IPhysicsWorld physics) : IReactiveSystem<Event<StrikeContact>>
{
    private const int Max = ArenaRules.MaxFighters;
    private readonly Entity[] _victim = new Entity[Max];
    private readonly Health[] _health = new Health[Max];
    private readonly bool[] _dead = new bool[Max], _corpse = new bool[Max];
    private int _count, _requests;

    // Буфер — на пик полной арены (по два касания на бойца за тик: Set<Health>, Add<Dead>, Hit, SeverRequested, Set<HitImpulse>)
    // — без роста посреди боя.
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.HitApply).Reads<Health>().Reads<PlayerControl>().Reads<BodyRef>().Sets<Health>().Adds<Dead>().Sets<HitImpulse>()
            .Emits<Hit>().Emits<SeverRequested>()
            .ReserveCommands(CombatReserve.Contacts * 3, CombatReserve.Contacts * 2, CombatReserve.Contacts * 512);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<StrikeContact>> contacts)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        var rules = world.Resource<CombatRules>();
        _count = _requests = 0;
        foreach (var c in contacts)
        {
            Apply(ctx, c.Value, time, rules);
        }
    }

    private void Apply(in SystemContext ctx, in StrikeContact c, in SimTime time, in CombatRules rules)
    {
        var world = ctx.World;
        if (!BodySegs.IsFlesh(c.Part) || !float.IsFinite(c.Damage)) return;   // клинок — тело, но не часть тела
        int k = Victim(world, c.Victim);
        if (k < 0 || _dead[k]) return;   // не боец или убит прошлым касанием этого тика (тело ещё не труп — переход в Settle)
        if (world.Has<PlayerTag>(c.Victim) && (rules.PlayerInvulnerable || world.TryGet<PlayerControl>(c.Victim, out var control) && control.Invulnerable > 0))
            return;
        bool corpse = _corpse[k];   // мёртв до тика — труп, снова цель
        ref var h = ref _health[k];
        bool lethal = !corpse && DamageRules.LethalThrust(c.Kind, c.Part, c.Damage, c.Dir, c.Blade);
        DamageRules.Apply(ref h, c.Part, c.Damage);
        float severAt = DamageRules.SeverThreshold(c.Part, world.Has<PlayerTag>(c.Attacker), rules.PlayerSharpness);
        var cmd = ctx.Commands;
        bool killed = !corpse && DamageRules.Killed(h, lethal);
        // Попадание — первым (журналы разные: порядок событий Hit и SeverRequested между собой ничего не меняет).
        cmd.Emit(new Hit
        {
            Contact = c.Id, Attacker = c.Attacker, Victim = c.Victim, Part = c.Part, Damage = c.Damage, Point = c.Point, Dir = c.Dir, Kind = c.Kind,
            Killed = killed, LethalThrust = lethal, SeverAt = severAt, Probe = c.Probe, Corpse = corpse,
        });
        if (c.Damage > severAt)
            cmd.Emit(new SeverRequested
            {
                Id = new RequestId(time.Tick, _requests++), Contact = c.Id, Victim = c.Victim, Part = c.Part, Point = c.Point, Blade = c.Blade,
                Dir = c.Dir, Kind = c.Kind,
            });
        cmd.Set(c.Victim, h);
        if (killed)
        {
            _dead[k] = true;
            cmd.Add(c.Victim, new Dead { Time = time.Time, Cause = c.Probe ? DeathCause.Probe : DeathCause.Hit });
        }
        // Импульс удара в часть — труп (из прохода, отдача атакующему) или убитый этим ударом (ещё кинематическая часть)
        if ((corpse || killed) && world.TryGet<BodyRef>(c.Victim, out var victimBody) && world.TryGet<BodyRef>(c.Attacker, out var attackerBody)
            && HitImpulses.Apply(physics, FighterBodies.Handle(attackerBody), FighterBodies.Handle(victimBody), c, corpse, killed, out var impulse))
            cmd.Set(c.Victim, impulse);
    }

    /// <summary>
    /// Жертва в накоплении тика: здоровье — итог прошлых попаданий тика; не боец — −1.
    /// </summary>
    private int Victim(World world, Entity e)
    {
        for (int i = 0; i < _count; i++)
            if (_victim[i] == e) return i;
        if (_count == Max || !world.IsAlive(e) || !world.Has<Fighter>(e) || !world.TryGet<Health>(e, out var h)) return -1;
        _victim[_count] = e;
        _health[_count] = h;
        _corpse[_count] = world.Has<Dead>(e);
        _dead[_count] = false;
        return _count++;
    }
}

/// <summary>
/// React, реактивная по <see cref="Hit"/>: живая жертва реагирует на последнее попадание тика — сзади <c>hit_back</c>, спереди
/// <c>hit_front1</c> или <c>hit_front2</c> (поток ГСЧ <see cref="Stream"/>). Враг — действием (<see cref="ActionLayer.StartHitReact"/>),
/// игрок — вздрагивает (<see cref="FlinchLayer.Start"/>). Мёртвые не реагируют.
/// </summary>
public sealed class HitReactionSystem : IReactiveSystem<Event<Hit>>
{
    /// <summary>Поток ГСЧ выбора клипа реакции спереди.</summary>
    public static readonly uint Stream = SimRandom.Stream("combat.hit-react");

    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.HitReaction).Reads<Transform>().Reads<AnimatorAction>().Reads<Flinch>().Sets<AnimatorAction>().Sets<Flinch>()
            .ReserveCommands(CombatReserve.Contacts, 0, CombatReserve.Contacts * 256);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<Hit>> hits)
    {
        var world = ctx.World;
        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        var clips = world.Resource<PosePool>().Clips;
        var actions = world.Resource<ActionTable>();
        for (int i = 0; i < hits.Length; i++)
        {
            var hit = hits[i].Value;
            if (Later(hits, i)) continue;   // реакция — по последнему попаданию тика
            var e = hit.Victim;
            if (!world.IsAlive(e) || world.Has<Dead>(e) || !world.TryGet<Transform>(e, out var t)) continue;
            var forward = new Vector3(MathF.Sin(t.Facing), 0, MathF.Cos(t.Facing));
            var toHit = hit.Point - t.Position;
            toHit.Y = 0;
            bool behind = Vector3.Dot(toHit, forward) < 0;
            int slot = behind ? ActionSlots.HitBack
                : rng.Index(Stream, tick, e, 2) % 2 == 0 ? ActionSlots.HitFront1 : ActionSlots.HitFront2;
            int clip = actions.Clip[slot];
            if (world.TryGet<Flinch>(e, out var flinch))
            {
                if (FlinchLayer.Start(ref flinch, clip, clips.Duration(clip))) ctx.Commands.Set(e, flinch);
            }
            else if (world.TryGet<AnimatorAction>(e, out var action))
            {
                ActionLayer.StartHitReact(ref action, clip, actions.Upper[slot], clips);
                ctx.Commands.Set(e, action);
            }
        }
    }

    private static bool Later(ReadOnlySpan<Event<Hit>> hits, int i)
    {
        for (int j = i + 1; j < hits.Length; j++)
            if (hits[j].Value.Victim == hits[i].Value.Victim) return true;
        return false;
    }
}

/// <summary>
/// Aftermath: кровопотеря живых — очки жизни минус кровотечение·dt, кровотечение гаснет на 0,2·dt; очки кончились — смерть.
/// <see cref="Health"/> — командой <c>Set</c> только у кровоточащих (у каждого изменения — причина; 0 байт).
/// </summary>
public sealed class BleedSystem : IContinuousSystem
{
    private Query _query = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.Bleed).Reads<Health>().Sets<Health>().Adds<Dead>()
            .ReserveCommands(ArenaRules.MaxFighters * 2, 0, ArenaRules.MaxFighters * 128);
        _query = s.Query().With<Fighter>().Without<Dead>().Read<Health>();
    }

    public void Run(in SystemContext ctx)
    {
        var time = ctx.World.Resource<SimTime>();
        foreach (var chunk in ctx.Iterate(_query))
        {
            var health = chunk.Read<Health>();
            var entities = chunk.Entities[..health.Length];
            for (int i = 0; i < health.Length; i++)
            {
                if (!(health[i].Bleed > 0)) continue;
                var h = health[i];
                DamageRules.Bleed(ref h, time.Dt);
                ctx.Commands.Set(entities[i], h);
                if (h.Hp <= 0) ctx.Commands.Add(entities[i], new Dead { Time = time.Time, Cause = DeathCause.Bleed });
            }
        }
    }
}

/// <summary>
/// Aftermath, после кровопотери: враг, мёртвый дольше 8 с, строится заново на своём месте (<see cref="Fighters.Rebuild"/>, партия
/// ГСЧ <see cref="ArenaRules.RespawnBatch"/>) командами на барьере — следующий тик видит нового бойца.
/// </summary>
public sealed class RespawnSystem(int[] weapons) : IContinuousSystem
{
    private Query _dead = null!, _player = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.Respawn).After(GameLabels.Bleed).Reads<Dead>().Reads<FighterSlot>().Reads<Transform>().Reads<AnimatorAction>()
            .DeclaresRebuild()
            .ReserveCommands(ArenaRules.MaxFighters * CombatReserve.RebuildCommands, 0, ArenaRules.MaxFighters * CombatReserve.RebuildBytes);
        _dead = s.Query().With<Fighter>().With<EnemyTag>().Read<Dead>().Read<FighterSlot>();
        _player = s.Query().With<Fighter>().With<PlayerTag>().Read<Transform>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        foreach (var chunk in ctx.Iterate(_dead))
        {
            var dead = chunk.Read<Dead>();
            var entities = chunk.Entities[..dead.Length];
            var slots = chunk.Read<FighterSlot>()[..dead.Length];
            for (int i = 0; i < dead.Length; i++)
                if (time.Time - dead[i].Time > DamageRules.RespawnDelay) Respawn(ctx, entities[i], slots[i].Index, time);
        }
    }

    private void Respawn(in SystemContext ctx, Entity e, int slot, in SimTime time)
    {
        if (slot is < 1 or > ArenaState.MaxEnemies) return;   // место вне арены — нарушение arena.slots
        var center = Vector3.Zero;
        foreach (var chunk in ctx.Iterate(_player))
            if (chunk.Count > 0) center = chunk.Read<Transform>()[0].Position with { Y = 0 };
        var world = ctx.World;
        var spawn = ArenaRules.Enemy(world.Resource<SimRandom>(), time.Tick, ArenaRules.RespawnBatch, slot, center, weapons);
        var action = world.TryGet<AnimatorAction>(e, out var a) ? a : AnimatorAction.None;
        Fighters.Rebuild(ctx.Commands, e, spawn, Fighters.Standing(spawn), default, action, dead: true);
    }
}

/// <summary>
/// Settle: статистика арены (<see cref="ArenaStats"/>) — попадания и смерти врагов тика (удары и кровопотеря). Попадание по трупу —
/// в <see cref="ArenaStats.CorpseHits"/>, не в <c>Hits</c>. Непрерывная: читает журналы своим курсором — каждая запись один раз.
/// </summary>
public sealed class ArenaStatsSystem : IContinuousSystem
{
    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.ArenaStats).ReadsJournal<Event<Hit>>().ReadsJournal<Added<Dead>>().SetsResource<ArenaStats>();

    public void Run(in SystemContext ctx)
    {
        var hits = ctx.Journal<Event<Hit>>();
        var deaths = ctx.Journal<Added<Dead>>();
        if (hits.IsEmpty && deaths.IsEmpty) return;
        var world = ctx.World;
        var stats = world.Resource<ArenaStats>();
        foreach (var h in hits)
        {
            if (h.Value.Corpse) stats.CorpseHits++;
            else stats.Hits++;
            if (!world.Has<PlayerTag>(h.Value.Attacker)) continue;
            stats.LastPart = h.Value.Part;
            stats.LastDamage = h.Value.Damage;
            stats.LastSeverAt = h.Value.SeverAt;
        }
        foreach (var d in deaths)
            if (world.Has<EnemyTag>(d.Entity)) stats.Kills++;
        ctx.Commands.SetResource(stats);
    }
}
