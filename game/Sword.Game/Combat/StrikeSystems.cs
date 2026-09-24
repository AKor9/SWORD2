using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Geometry;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Combat;

/// <summary>
/// Проход клинка по паре бойцов (<c>_contactSerial</c>, <c>_inContact</c>, <c>_cooldown</c> пары SWORD 2eab13b; этап 6г): удар атакующего,
/// к которому относится проход, клинок сейчас в теле, время последнего касания пары и касаний за жизнь пары.
/// </summary>
public struct StrikePass
{
    /// <summary>Номер удара атакующего (<c>AnimatorAction.AttackSerial</c>), к которому относится проход; новый удар — проход заново.</summary>
    public int Serial;

    /// <summary>Клинок сейчас в теле: касание уже засчитано, следующее — только после выхода клинка из тела.</summary>
    public bool InContact;

    /// <summary>Время последнего касания пары, с (не чаще 0,1 с).</summary>
    public float LastHit;

    /// <summary>Касаний пары за жизнь врага.</summary>
    public int Contacts;

    public static StrikePass None => new() { LastHit = -1e3f };
}

/// <summary>
/// Пары удара врага (этап 6г): пары — только игрок ↔ враг (враги друг друга не рубят), поэтому обе пары врага — у него: клинок
/// игрока против этого врага и клинок врага против игрока. Постройка врага — пары с нуля (в SWORD сброс арены чистил пары, а
/// возрождение — нет). Хешируется, видно агенту (<c>entity.get "enemy N"</c>).
/// </summary>
public struct StrikePairs
{
    public StrikePass FromPlayer, ToPlayer;

    public static StrikePairs None => new() { FromPlayer = StrikePass.None, ToPlayer = StrikePass.None };
}

/// <summary>
/// Combat (<see cref="GameLabels.StrikeDetection"/>): проходы клинка (<c>KinematicStrikes</c>, <c>SweepBlade</c> SWORD 2eab13b). Атакующие —
/// по местам 0…32; живой боец в ударе (<see cref="ActionKind.Attack"/>) с поставленным клинком; пары — игрок против каждого живого врага
/// и враг против живого игрока. Скорость кончика — |кончик − прошлый кончик|/dt; медленнее 3,5 м/с или кончик дальше радиуса жертвы
/// + 1,5 м (центр и радиус — по капсулам этого тика) — клинок вне тела. Развёртка — 4 положения клинка от прошлого к нынешнему против
/// 13 капсул жертвы (<see cref="Segments.SegSeg"/>, радиус + 0,02): первое положение с касанием, в нём ближайшая капсула; точка — на
/// поверхности капсулы. Одно касание за проход клинка сквозь тело (вышел и вошёл — новое), не чаще 0,1 с на пару; урон меньше 4 —
/// не касание и проход не тратит (исправление SWORD). Касание — событие <see cref="StrikeContact"/> (номер — порядок тика). Затем пробы
/// удара командой (<see cref="StrikeProbe"/>) — тем же событием. Состояние пар — <see cref="StrikePairs"/> врагов. Трупы — не цели
/// (до этапа 7). Памяти в тике не выделяет.
/// </summary>
public sealed class StrikeDetectionSystem(IPhysicsWorld physics, StrikeTable strikes) : IContinuousSystem
{
    private const int Slots = ArenaRules.MaxFighters, Bodies = BodySegs.Bodies;

    private Query _fighters = null!, _dead = null!, _pairs = null!;
    private readonly Entity[] _entity = new Entity[Slots];
    private readonly bool[] _present = new bool[Slots], _alive = new bool[Slots], _placeable = new bool[Slots], _hasPairs = new bool[Slots],
        _bounded = new bool[Slots];
    private readonly Vector3[] _root = new Vector3[Slots], _center = new Vector3[Slots];
    private readonly float[] _facing = new float[Slots], _radius = new float[Slots];
    private readonly ActionKind[] _kind = new ActionKind[Slots];
    private readonly int[] _attack = new int[Slots], _serial = new int[Slots];
    private readonly Blade[] _blade = new Blade[Slots];
    private readonly ArticulationHandle[] _body = new ArticulationHandle[Slots];
    private readonly StrikePairs[] _pairState = new StrikePairs[Slots];
    private readonly Vector3[] _endA = new Vector3[Slots * Bodies], _endB = new Vector3[Slots * Bodies], _bodyCenter = new Vector3[Slots * Bodies];
    private readonly float[] _bodyRadius = new float[Slots * Bodies], _bodyHalf = new float[Slots * Bodies];
    private int _contacts;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.StrikeDetection).ReadsJournal<Event<StrikeProbe>>().Reads<FighterSlot>().Reads<Transform>().Reads<AnimatorAction>()
            .Reads<Blade>().Reads<BodyRef>().Writes<StrikePairs>().Emits<StrikeContact>()
            .ReserveCommands(0, CombatReserve.Contacts, CombatReserve.Contacts * 128);
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>().Read<Transform>().Read<AnimatorAction>().Read<Blade>().Read<BodyRef>();
        _dead = s.Query().With<Fighter>().With<Dead>().Read<FighterSlot>();
        _pairs = s.Query().With<Fighter>().With<EnemyTag>().Without<Dead>().Read<FighterSlot>().Write<StrikePairs>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        var rules = world.Resource<CombatRules>();
        var probes = ctx.Journal<Event<StrikeProbe>>();
        Gather(ctx);
        _contacts = 0;
        for (int i = 0; i < Slots; i++)
            if (_alive[i] && _kind[i] == ActionKind.Attack && _blade[i].Weapon >= 0) Attacker(ctx, i, time, rules);
        foreach (var p in probes)
        {
            ctx.Cause = p.Cause;   // касание пробы — от её команды (state.why, 6д)
            Probe(ctx, p.Value, time.Tick);
        }
        foreach (var chunk in ctx.Iterate(_pairs))
        {
            var pairs = chunk.Write<StrikePairs>();
            var entities = chunk.Entities[..pairs.Length];
            var slots = chunk.Read<FighterSlot>()[..pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
                if (Owns(slots[i].Index, entities[i], out int slot) && _hasPairs[slot]) pairs[i] = _pairState[slot];
        }
    }

    /// <summary>Бойцы по местам: живые, мёртвые, клинок, удар, тело; пары живых врагов.</summary>
    private void Gather(in SystemContext ctx)
    {
        Array.Clear(_present);
        Array.Clear(_alive);
        Array.Clear(_hasPairs);
        Array.Clear(_bounded);
        foreach (var chunk in ctx.Iterate(_fighters))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            var roots = chunk.Read<Transform>()[..entities.Length];
            var actions = chunk.Read<AnimatorAction>()[..entities.Length];
            var blades = chunk.Read<Blade>()[..entities.Length];
            var bodies = chunk.Read<BodyRef>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                int slot = slots[i].Index;
                if ((uint)slot >= Slots || _present[slot]) continue;   // второй на месте — нарушение arena.slots
                _present[slot] = _alive[slot] = true;
                _entity[slot] = entities[i];
                (_root[slot], _facing[slot]) = (roots[i].Position, roots[i].Facing);
                (_kind[slot], _attack[slot], _serial[slot]) = (actions[i].Kind, actions[i].Attack, actions[i].AttackSerial);
                _blade[slot] = blades[i];
                _body[slot] = FighterBodies.Handle(bodies[i]);
                _placeable[slot] = BodyPlacement.Placeable(physics, _body[slot]);
            }
        }
        foreach (var chunk in ctx.Iterate(_dead))
        {
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..entities.Length];
            for (int i = 0; i < entities.Length; i++)
                if (Owns(slots[i].Index, entities[i], out int slot)) _alive[slot] = false;
        }
        foreach (var chunk in ctx.Iterate(_pairs))
        {
            var pairs = chunk.Read<StrikePairs>();
            var entities = chunk.Entities[..pairs.Length];
            var slots = chunk.Read<FighterSlot>()[..pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
                if (Owns(slots[i].Index, entities[i], out int slot))
                {
                    _pairState[slot] = pairs[i];
                    _hasPairs[slot] = true;
                }
        }
    }

    private bool Owns(int slot, Entity e, out int index)
    {
        index = slot;
        return (uint)slot < Slots && _present[slot] && _entity[slot] == e;
    }

    /// <summary>
    /// Атакующий места <paramref name="i"/> (тело цикла <c>KinematicStrikes</c> по атакующим — свой метод: пар до 32 за тик): игрок —
    /// против каждого живого врага, враг — против живого игрока.
    /// </summary>
    private void Attacker(in SystemContext ctx, int i, in SimTime time, in CombatRules rules)
    {
        var blade = _blade[i];
        float speed = Vector3.Distance(blade.Tip, blade.PrevTip) / time.Dt;
        var attacks = strikes.Attacks;
        if ((uint)_attack[i] >= (uint)attacks.Length) return;   // удар вне таблицы — ловит animation.actions
        bool thrust = attacks[_attack[i]].Thrust;
        if (i == 0)
        {
            for (int j = 1; j < Slots; j++)
                if (_alive[j] && _hasPairs[j] && _placeable[j]) Pair(ctx, ref _pairState[j].FromPlayer, 0, j, blade, speed, thrust, time, rules);
        }
        else if (_alive[0] && _hasPairs[i] && _placeable[0]) Pair(ctx, ref _pairState[i].ToPlayer, i, 0, blade, speed, thrust, time, rules);
    }

    /// <summary>Пара атакующий <paramref name="att"/> → жертва <paramref name="vic"/>: проход клинка и касание (порядок проверок SWORD).</summary>
    private void Pair(in SystemContext ctx, ref StrikePass pass, int att, int vic, in Blade blade, float speed, bool thrust, in SimTime time,
        in CombatRules rules)
    {
        // Одно попадание за проход клинка сквозь тело: вышел и вошёл снова — новое. Новый удар — новый проход.
        if (pass.Serial != _serial[att])
        {
            pass.Serial = _serial[att];
            pass.InContact = false;
        }
        Bound(vic);
        if (speed < DamageRules.MinStrikeSpeed || Vector3.Distance(_center[vic], blade.Tip) > _radius[vic] + DamageRules.BoundMargin
            || !Sweep(blade, vic, out int body, out var point))
        {
            pass.InContact = false;
            return;
        }
        if (pass.InContact) return;
        // кулдаун не гасит проход: клинок ещё в теле — касание засчитается позже
        if (time.Time - pass.LastHit < DamageRules.HitCooldown) return;
        float damage = DamageRules.StrikeDamage(thrust, speed, att == 0, rules.PlayerDamage);
        if (damage < DamageRules.MinDamage) return;   // SWORD2: слабый удар не засчитан — и проход не тратит
        pass.InContact = true;
        pass.LastHit = time.Time;
        pass.Contacts++;
        var axis = Vector3.Normalize(blade.Tip - blade.Base);
        var move = blade.Tip - blade.PrevTip;
        var dir = move.LengthSquared() > 1e-8f ? Vector3.Normalize(move) : axis;
        // Проход клинка — непрерывный производитель: родитель касания — начало удара этого номера (побочная таблица, 6д).
        if (ctx.Causality is { } log) ctx.Cause = log.Linked(_entity[att], CombatCauses.Attack, _serial[att]);
        ctx.Commands.Emit(new StrikeContact
        {
            Id = new ContactId(time.Tick, _contacts++), Attacker = _entity[att], Victim = _entity[vic], Part = BodySegs.Segment(body),
            Point = point, Dir = dir, Blade = axis, Speed = speed, Kind = thrust ? HitKind.Thrust : HitKind.Slash, Damage = damage,
            AttackSerial = _serial[att],
        });
    }

    /// <summary>Капсулы жертвы этого тика, их центр и радиус охвата (<c>Fighter.UpdateBounds</c> по 13 капсулам; раз за тик).</summary>
    private void Bound(int vic)
    {
        if (_bounded[vic]) return;
        _bounded[vic] = true;
        var sum = Vector3.Zero;
        for (int k = 0; k < Bodies; k++)
        {
            var s = physics.Get(physics.Body(_body[vic], k));
            int at = vic * Bodies + k;
            (_endA[at], _endB[at], _bodyCenter[at], _bodyRadius[at], _bodyHalf[at]) = (s.EndA, s.EndB, s.Position, s.Radius, s.HalfLength);
            sum += s.Position;
        }
        var center = sum / Bodies;
        float r = 0;
        for (int k = 0; k < Bodies; k++)
        {
            int at = vic * Bodies + k;
            r = MathF.Max(r, Vector3.Distance(_bodyCenter[at], center) + _bodyHalf[at] + _bodyRadius[at]);
        }
        (_center[vic], _radius[vic]) = (center, r);
    }

    /// <summary>
    /// Развёртка клинка (<c>SweepBlade</c> SWORD, 3 шага): положения клинка от прошлого тика к нынешнему; в первом положении с касанием —
    /// ближайшая капсула (тела по порядку сегментов, равные — первое) и точка на её поверхности.
    /// </summary>
    private bool Sweep(in Blade blade, int vic, out int hit, out Vector3 point)
    {
        hit = -1;
        point = default;
        float best = float.MaxValue;
        for (int k = 0; k <= DamageRules.SweepSteps; k++)
        {
            float t = k / (float)DamageRules.SweepSteps;
            var a = Vector3.Lerp(blade.PrevBase, blade.Base, t);
            var b = Vector3.Lerp(blade.PrevTip, blade.Tip, t);
            for (int body = 0; body < Bodies; body++)
            {
                int at = vic * Bodies + body;
                float d2 = Segments.SegSeg(a, b, _endA[at], _endB[at], out var c1, out var c2);
                float r = _bodyRadius[at] + DamageRules.SweepPad;
                if (d2 >= r * r || d2 >= best) continue;
                best = d2;
                hit = body;
                point = d2 > 1e-8f ? c2 + Vector3.Normalize(c1 - c2) * _bodyRadius[at] : c2;
            }
            if (hit >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Проба удара командой (<see cref="StrikeProbe"/>): касание без клинка — точка в центре капсулы части, направление и ось — от корня
    /// атакующего к жертве по земле (совпали — курс атакующего). Негодная проба — ничего.
    /// </summary>
    private void Probe(in SystemContext ctx, in StrikeProbe p, ulong tick)
    {
        if ((uint)p.Attacker >= Slots || (uint)p.Victim >= Slots || p.Attacker == p.Victim || !_present[p.Attacker] || !_present[p.Victim]
            || (uint)p.Part >= BodySegs.Count || BodySegs.Body(p.Part) < 0 || !float.IsFinite(p.Damage) || p.Damage < 0
            || (byte)p.Kind > (byte)HitKind.Blunt || !_placeable[p.Victim])
            return;
        var point = physics.Get(physics.Body(_body[p.Victim], BodySegs.Body(p.Part))).Position;
        var dir = _root[p.Victim] - _root[p.Attacker];
        dir.Y = 0;
        dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir)
            : new Vector3(MathF.Sin(_facing[p.Attacker]), 0, MathF.Cos(_facing[p.Attacker]));
        ctx.Commands.Emit(new StrikeContact
        {
            Id = new ContactId(tick, _contacts++), Attacker = _entity[p.Attacker], Victim = _entity[p.Victim], Part = p.Part, Point = point,
            Dir = dir, Blade = dir, Kind = p.Kind, Damage = p.Damage, AttackSerial = -1, Probe = true,
        });
    }
}
