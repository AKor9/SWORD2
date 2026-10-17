using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;
using Sword.Game.Movement;
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
        s.Label(GameLabels.HitApply).Reads<Health>().Reads<PlayerControl>().Reads<BodyRef>().Reads<Downed>().Sets<Health>().Adds<Dead>().Sets<HitImpulse>()
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
        var cmd = ctx.Commands;
        bool killed = !corpse && DamageRules.Killed(h, lethal);
        // Попадание — первым (журналы разные: порядок событий Hit и SeverRequested между собой ничего не меняет).
        cmd.Emit(new Hit
        {
            Contact = c.Id, Attacker = c.Attacker, Victim = c.Victim, Part = c.Part, Damage = c.Damage, Point = c.Point, Dir = c.Dir, Kind = c.Kind,
            Killed = killed, LethalThrust = lethal, Probe = c.Probe, Corpse = corpse,
        });
        cmd.Emit(new SeverRequested
        {
            Id = new RequestId(time.Tick, _requests++), Contact = c.Id, Attacker = c.Attacker, Victim = c.Victim, Part = c.Part, Point = c.Point,
            Blade = c.Probe ? ProbeEdge(c.Dir) : c.Blade, Dir = c.Dir, Kind = c.Kind, Velocity = PointVelocity(world, c), Corpse = corpse,
            Sharpness = world.Has<PlayerTag>(c.Attacker) ? MathF.Max(rules.PlayerSharpness, DamageRules.MinSharpness) : 1,
        });
        cmd.Set(c.Victim, h);
        if (killed)
        {
            _dead[k] = true;
            cmd.Add(c.Victim, new Dead { Time = time.Time, Cause = c.Probe ? DeathCause.Probe : DeathCause.Hit });
        }
        // Импульс удара в часть — труп (из прохода, отдача атакующему) или убитый этим ударом (ещё кинематическая часть)
        bool lying = corpse || world.Has<Downed>(c.Victim);   // сбитый с ног — тела на физике, как у трупа
        if ((lying || killed) && world.TryGet<BodyRef>(c.Victim, out var victimBody) && world.TryGet<BodyRef>(c.Attacker, out var attackerBody)
            && HitImpulses.Apply(physics, FighterBodies.Handle(attackerBody), FighterBodies.Handle(victimBody), c, lying, killed, out var impulse))
            cmd.Set(c.Victim, impulse);
    }

    /// <summary>
    /// Скорость точки клинка атакующего в месте касания (тело клинка кинематическое: скорость и угловая — постановки тика). Проба
    /// (клинка нет) — вдоль направления, по урону: <c>урон · 0,25</c> м/с (60 урона — 15 м/с).
    /// </summary>
    private Vector3 PointVelocity(World world, in StrikeContact c)
    {
        if (!c.Probe && world.TryGet<BodyRef>(c.Attacker, out var bodyRef))
        {
            var body = FighterBodies.Handle(bodyRef);
            if (physics.IsAlive(body) && physics.BodyCount(body) > BodySegs.Weapon)
            {
                var blade = physics.Get(physics.Body(body, BodySegs.Body(BodySegs.Weapon)));
                var v = blade.PointVelocity(c.Point);
                if (float.IsFinite(v.X + v.Y + v.Z) && v.LengthSquared() > 1e-6f) return v;
            }
        }
        return c.Dir * (c.Damage * ProbeSpeedPerDamage);
    }

    /// <summary>Скорость пробы на единицу урона, м/с.</summary>
    public const float ProbeSpeedPerDamage = 0.25f;

    /// <summary>Кромка пробы (клинка нет): горизонтальная поперёк направления — проба рубит части поперёк вертикали.</summary>
    public static Vector3 ProbeEdge(Vector3 dir)
    {
        var edge = Vector3.Cross(Vector3.UnitY, dir);
        return edge.LengthSquared() > 1e-8f ? Vector3.Normalize(edge) : Vector3.UnitX;
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
/// React, реактивная по <see cref="HitResponse"/> (решает модуль Impact): исполнение отклика. Жертва — толчок корня (<see cref="Shove"/>,
/// сумма откликов тика) и реакция по сильнейшему отклику тика (равные — последний): вздрагивание добавкой с силой удара — сзади
/// <c>hit_back</c>, спереди <c>hit_front1</c> или <c>hit_front2</c> (поток ГСЧ <see cref="Stream"/>); враг ещё и действием реакции тем же
/// клипом (прерывает удар), пошатывание с толчком от <see cref="BigPush"/> — сильным клипом <c>hit_big</c>. Игрок только вздрагивает.
/// Атакующий — отдача удара (<see cref="ActionLayer.Recoil"/>). Мёртвые не реагируют. Без выделений.
/// </summary>
public sealed class HitReactionSystem : IReactiveSystem<Event<HitResponse>>
{
    /// <summary>Поток ГСЧ выбора клипа реакции спереди.</summary>
    public static readonly uint Stream = SimRandom.Stream("combat.hit-react");

    /// <summary>Толчок, с которого пошатывание спереди — сильным клипом, м/с.</summary>
    public const float BigPush = 0.8f;

    private const int Max = CombatReserve.Contacts * 2;
    private readonly Entity[] _entities = new Entity[Max];
    private readonly AnimatorAction[] _actions = new AnimatorAction[Max];
    private readonly Flinch[] _flinches = new Flinch[Max];
    private readonly Shove[] _shoves = new Shove[Max];
    private readonly byte[] _state = new byte[Max];
    private int _count;

    private const byte HasAction = 1, HasFlinch = 2, HasShove = 4, ActionSet = 8, FlinchSet = 16, ShoveSet = 32, Down = 64;

    public void Setup(SystemSetup s) =>
        s.Label(GameLabels.HitReaction).Reads<Transform>().Reads<AnimatorAction>().Reads<Flinch>().Reads<Shove>().Reads<Dead>().Reads<PlayerTag>()
            .Reads<Downed>().ReadsJournal<Event<LimbDetached>>().Sets<AnimatorAction>().Sets<Flinch>().Sets<Shove>().Adds<Shove>().Adds<Downed>()
            .ReserveCommands(Max * 3, 0, Max * 3 * 256);

    public void React(in SystemContext ctx, ReadOnlySpan<Event<HitResponse>> responses)
    {
        var world = ctx.World;
        var rng = world.Resource<SimRandom>();
        ulong tick = world.Resource<SimTime>().Tick;
        float now = world.Resource<SimTime>().Time;
        var clips = world.Resource<PosePool>().Clips;
        var actions = world.Resource<ActionTable>();
        _count = 0;
        // перелом ноги этого тика: живого валит отделение (без встать), реакции не нужны
        foreach (var d in ctx.Journal<Event<LimbDetached>>())
            if (Ragdoll.Severing.Cripples(d.Value.Mode, d.Value.Segment) && world.IsAlive(d.Value.Victim) && Slot(world, d.Value.Victim) is var c and >= 0)
                _state[c] = Down;
        for (int i = 0; i < responses.Length; i++)
        {
            var r = responses[i].Value;
            if (world.IsAlive(r.Attacker) && !world.Has<Dead>(r.Attacker) && Slot(world, r.Attacker) is var a and >= 0 && (_state[a] & HasAction) != 0
                && ActionLayer.Recoil(ref _actions[a], r.Recoil))
                _state[a] |= ActionSet;
            var e = r.Victim;
            if (!world.IsAlive(e) || world.Has<Dead>(e) || world.Has<Downed>(e) || !world.TryGet<Transform>(e, out var t)
                || Slot(world, e) is not (var k and >= 0) || (_state[k] & Down) != 0)
                continue;
            // сбит с ног (враг): тела — на физику в Settle, реакций нет
            if (r.Level == HitLevel.Knockdown && !world.Has<PlayerTag>(e))
            {
                _state[k] = Down;
                ctx.Commands.Add(e, new Downed { Time = now, Impulse = r.Impulse, Point = r.Point, Part = r.Part });
                continue;
            }
            _shoves[k].Velocity = ShoveRules.Add(_shoves[k].Velocity, r.Push);
            _state[k] |= ShoveSet;
            if (!Strongest(responses, i)) continue;
            var forward = new Vector3(MathF.Sin(t.Facing), 0, MathF.Cos(t.Facing));
            var toHit = r.Point - t.Position;
            toHit.Y = 0;
            bool behind = Vector3.Dot(toHit, forward) < 0;
            bool react = !world.Has<PlayerTag>(e) && (_state[k] & HasAction) != 0;
            int slot = behind ? ActionSlots.HitBack
                : react && r.Level != HitLevel.Flinch && r.Push.Length() >= BigPush ? ActionSlots.HitBig
                : rng.Index(Stream, tick, e, 2) % 2 == 0 ? ActionSlots.HitFront1 : ActionSlots.HitFront2;
            int clip = actions.Clip[slot];
            // враг реагирует действием на каждое попадание (прерывает удар), пошатывание — сильным клипом; сила удара видна добавкой
            if (react)
            {
                ActionLayer.StartHitReact(ref _actions[k], clip, actions.Upper[slot], clips);
                _state[k] |= ActionSet;
            }
            if ((_state[k] & HasFlinch) != 0 && FlinchLayer.Start(ref _flinches[k], clip, clips.Duration(clip), r.Strength))
                _state[k] |= FlinchSet;
        }
        var cmd = ctx.Commands;
        for (int k = 0; k < _count; k++)
        {
            var e = _entities[k];
            if ((_state[k] & ActionSet) != 0) cmd.Set(e, _actions[k]);
            if ((_state[k] & FlinchSet) != 0) cmd.Set(e, _flinches[k]);
            if ((_state[k] & ShoveSet) == 0) continue;
            if ((_state[k] & HasShove) != 0) cmd.Set(e, _shoves[k]);
            else cmd.Add(e, _shoves[k]);
        }
    }

    /// <summary>Боец в накоплении тика: действие, вздрагивание и толчок — итог прошлых откликов тика.</summary>
    private int Slot(World world, Entity e)
    {
        for (int i = 0; i < _count; i++)
            if (_entities[i] == e) return i;
        if (_count == Max) return -1;
        int k = _count++;
        _entities[k] = e;
        _state[k] = 0;
        if (world.TryGet<AnimatorAction>(e, out _actions[k])) _state[k] |= HasAction;
        if (world.TryGet<Flinch>(e, out _flinches[k])) _state[k] |= HasFlinch;
        if (world.TryGet<Shove>(e, out _shoves[k])) _state[k] |= HasShove;
        else _shoves[k] = default;
        return k;
    }

    /// <summary>Отклик <paramref name="i"/> — сильнейший у своей жертвы в тике (равные — последний).</summary>
    private static bool Strongest(ReadOnlySpan<Event<HitResponse>> responses, int i)
    {
        var victim = responses[i].Value.Victim;
        var level = responses[i].Value.Level;
        for (int j = 0; j < responses.Length; j++)
        {
            if (j == i || responses[j].Value.Victim != victim) continue;
            var other = responses[j].Value.Level;
            if (other > level || other == level && j > i) return false;
        }
        return true;
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
        }
        foreach (var d in deaths)
            if (world.Has<EnemyTag>(d.Entity)) stats.Kills++;
        ctx.Commands.SetResource(stats);
    }
}
