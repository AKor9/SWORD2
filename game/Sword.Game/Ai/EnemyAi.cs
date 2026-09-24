using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Animation;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Arena;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Ai;

/// <summary>
/// Константы ИИ врага — <c>Gameplay/Ai/EnemyAi.cs</c> SWORD 2eab13b (<c>Think</c>, <c>Built</c>): движение (этап 4б) и удар по
/// таймеру (6в); блок у врагов в SWORD выключен — его нет.
/// </summary>
public static class EnemyAiRules
{
    /// <summary>Дистанция удара, м (<c>Reach</c>): ближе к игроку — враг бьёт, когда таймер вышел.</summary>
    public const float Reach = 2.3f;

    /// <summary>Таймер удара при постройке — 1 + ГСЧ·2 с (<c>Built</c>), после удара — 1,1 + ГСЧ·1,4 с.</summary>
    public const float FirstTimer = 1f, FirstTimerSpread = 2f, NextTimer = 1.1f, NextTimerSpread = 1.4f;

    /// <summary>Рубящий удар (случайный из связки) с вероятностью 0,75, иначе укол (случайный).</summary>
    public const float SlashChance = 0.75f;

    /// <summary>Поток ГСЧ удара ИИ: выбор удара и таймер (номер выборки — 0, 1, 2) и таймер при постройке.</summary>
    public static readonly uint AttackStream = SimRandom.Stream("ai.attack");

    /// <summary>Дистанция, на которой враг встаёт перед игроком, м (<c>KeepDistance</c>).</summary>
    public const float KeepDistance = 1.8f;

    /// <summary>Личное пространство: ближе этого враги расходятся, м (<c>Personal</c>).</summary>
    public const float Personal = 1.4f;

    /// <summary>Скорость подхода — (dist − <see cref="KeepDistance"/>)·2,5 в пределах −2…3,2 м/с.</summary>
    public const float ApproachGain = 2.5f, ApproachMin = -2.0f, ApproachMax = 3.2f;

    /// <summary>Покачивание вбок: sin(time·0,7 + seed·10)·0,7 м/с.</summary>
    public const float SwayFrequency = 0.7f, SwaySeedScale = 10f, SwaySpeed = 0.7f;

    /// <summary>Расхождение: (Personal − l)·2 м/с от соседа ближе <see cref="Personal"/>.</summary>
    public const float SeparationGain = 2f;

    /// <summary>Нижний предел расстояния, м: до игрока — <c>MathF.Max(|to|, 1e-3f)</c>, до соседа — <c>l &gt; 1e-3f</c>.</summary>
    public const float MinDistance = 1e-3f;
}

/// <summary>
/// Составляющие намерения врага на этом тике — агентский вид ИИ (почему враг идёт туда, куда идёт): расстояние до
/// игрока, скорость подхода, покачивания и толчок расхождения. Пишет <see cref="EnemyAiSystem"/> (и команда арены — тому,
/// кого строит); читают агент (<c>entity.get</c>, <c>watch.add</c>, условия) и манифест фичи. Хешируется (участник
/// <c>ecs</c>): это производное состояние, детерминированное, как и само намерение.
/// Желаемая скорость — <c>dir·ApproachSpeed + side·SwaySpeed + Separation</c> (dir — на игрока, side = (dir.Z, 0, −dir.X)),
/// сумма с точностью до округления float: в <see cref="FighterCommand.DesiredVelocity"/> она сложена в порядке SWORD.
/// </summary>
public struct EnemyAiTerms
{
    /// <summary>Расстояние до игрока по земле, м (<c>dist</c> SWORD, не меньше 1e-3).</summary>
    public float Distance;

    /// <summary>Скорость к игроку, м/с: clamp((dist − 1,8)·2,5, −2, 3,2); больше 0 — подходит, меньше — отступает.</summary>
    public float ApproachSpeed;

    /// <summary>Скорость вбок, м/с: sin(time·0,7 + seed·10)·0,7 (по side = (dir.Z, 0, −dir.X) — вправо от взгляда на игрока).</summary>
    public float SwaySpeed;

    /// <summary>Сумма толчков расхождения с врагами ближе 1,4 м, м/с (по земле).</summary>
    public Vector3 Separation;

    /// <summary>Сколько врагов ближе 1,4 м (толкнули).</summary>
    public int Neighbours;
}

/// <summary>
/// Кто на арене для ИИ: корни и зёрна мест 0…32 (0 — игрок) и какие места заняты. Корни — до движения тика (в SWORD все
/// <c>Think</c> идут до <c>KinematicBegin</c>). Массивы заводятся один раз — тик памяти не выделяет.
/// </summary>
public sealed class EnemyCrowd
{
    public readonly Vector3[] Roots = new Vector3[ArenaRules.MaxFighters];
    public readonly float[] Seeds = new float[ArenaRules.MaxFighters];
    public readonly bool[] Present = new bool[ArenaRules.MaxFighters];

    /// <summary>Игрок мёртв (6г, <c>p.Dead</c> SWORD): враги подходят ×0,2 и не бьют.</summary>
    public bool PlayerDead;

    /// <summary>Множитель желаемой скорости врага при мёртвом игроке (<c>desired *= 0.2f</c> SWORD).</summary>
    public const float DeadPlayerCalm = 0.2f;

    public void Clear()
    {
        Array.Clear(Present);
        PlayerDead = false;
    }

    public void Put(int slot, Vector3 root, float seed)
    {
        Roots[slot] = root;
        Seeds[slot] = seed;
        Present[slot] = true;
    }

    /// <summary>
    /// Толпа мира до движения тика: все живые бойцы с местом (<see cref="FighterSlot"/>) — корень и зерно (мёртвые враги в
    /// расхождении не участвуют — <c>o.Dead</c> SWORD, 6г); мёртвый игрок — тоже цель (враги идут к нему ×0,2), отмечен
    /// <see cref="PlayerDead"/>. Запросы — <c>With&lt;Fighter&gt;().Without&lt;Dead&gt;().Read&lt;FighterSlot&gt;().Read&lt;Transform&gt;().Read&lt;FighterSeed&gt;()</c>
    /// и тот же у игрока <c>With&lt;Dead&gt;()</c>.
    /// </summary>
    public void Gather(in SystemContext ctx, Query fighters, Query deadPlayer)
    {
        Clear();
        Put(ctx, fighters);
        foreach (var chunk in ctx.Iterate(deadPlayer)) PlayerDead |= chunk.Count > 0;
        if (PlayerDead) Put(ctx, deadPlayer);
    }

    private void Put(in SystemContext ctx, Query fighters)
    {
        foreach (var chunk in ctx.Iterate(fighters))
        {
            var slots = chunk.Read<FighterSlot>();
            var t = chunk.Read<Transform>()[..slots.Length];
            var seeds = chunk.Read<FighterSeed>()[..slots.Length];
            for (int i = 0; i < slots.Length; i++)
                if ((uint)slots[i].Index < ArenaRules.MaxFighters) Put(slots[i].Index, t[i].Position, seeds[i].Value);
        }
    }

    /// <summary>
    /// Намерение врага места <paramref name="slot"/> по этой толпе (<see cref="EnemyAi.Think"/>; игрок мёртв — желаемая скорость
    /// ×0,2, как в SWORD); игрока (место 0) нет — false, намерение не трогается.
    /// </summary>
    public bool Think(int slot, float time, ref FighterCommand intent, ref GazeTarget gaze, ref EnemyAiTerms terms)
    {
        if (!Present[0]) return false;
        intent = EnemyAi.Think(slot, Roots[slot], Seeds[slot], Roots[0], time, Roots, Present, out terms);
        if (PlayerDead) intent.DesiredVelocity *= DeadPlayerCalm;
        gaze = new GazeTarget { Point = Roots[0], Has = true };
        return true;
    }
}

/// <summary>
/// ИИ врага — <c>EnemyAi.Think</c> SWORD 2eab13b, движение (удар по таймеру — <see cref="EnemyAiSystem.Attack"/>, 6в; блока у
/// врагов нет, <c>p.Dead</c> — с 6г): идёт на игрока до 1,8 м, покачиваясь вбок, и расходится с врагами ближе 1,4 м; курс-цель
/// и точка взгляда — на игрока. Порядок вычислений float — как в SWORD (сверка трассой на 4д): расхождение складывается по
/// местам 1…32.
/// </summary>
public static class EnemyAi
{
    /// <summary>
    /// Один враг. <paramref name="roots"/> и <paramref name="present"/> — по местам (0 — игрок, в расхождении не участвует);
    /// сам враг (<paramref name="slot"/>) пропускается. <paramref name="time"/> — время арены (<c>DuelArena.Time</c>:
    /// <see cref="SimTime.Time"/>).
    /// </summary>
    public static FighterCommand Think(int slot, Vector3 self, float seed, Vector3 player, float time,
        ReadOnlySpan<Vector3> roots, ReadOnlySpan<bool> present, out EnemyAiTerms terms)
    {
        var to = player - self;
        to.Y = 0;
        float dist = MathF.Max(to.Length(), EnemyAiRules.MinDistance);
        var dir = to / dist;
        var side = new Vector3(dir.Z, 0, -dir.X);
        float approach = Math.Clamp((dist - EnemyAiRules.KeepDistance) * EnemyAiRules.ApproachGain, EnemyAiRules.ApproachMin, EnemyAiRules.ApproachMax);
        float sway = MathF.Sin(time * EnemyAiRules.SwayFrequency + seed * EnemyAiRules.SwaySeedScale);
        // Порядок SWORD: dir·clamp + (side·sin)·0,7, затем толчки соседей по местам.
        var desired = dir * approach + side * sway * EnemyAiRules.SwaySpeed;
        terms = new EnemyAiTerms { Distance = dist, ApproachSpeed = approach, SwaySpeed = sway * EnemyAiRules.SwaySpeed };

        int n = Math.Min(roots.Length, present.Length);
        for (int j = 1; j < n; j++)
        {
            if (j == slot || !present[j]) continue;
            var d = self - roots[j];
            d.Y = 0;
            float l = d.Length();
            if (l < EnemyAiRules.Personal && l > EnemyAiRules.MinDistance)
            {
                var push = d / l * (EnemyAiRules.Personal - l) * EnemyAiRules.SeparationGain;
                desired += push;
                terms.Separation += push;
                terms.Neighbours++;
            }
        }
        return new FighterCommand { DesiredVelocity = desired, FacingGoal = MathF.Atan2(dir.X, dir.Z) };
    }
}

/// <summary>
/// Таймер удара врага (<c>EnemyAi._attackTimer</c> SWORD 2eab13b, 6в): сколько ещё ждать до следующего удара, с (не ниже 0). Задаёт
/// постройка места (<see cref="FighterSpawn.AttackTimer"/>: 1 + ГСЧ·2), после удара ИИ — 1,1 + ГСЧ·1,4; идёт каждый тик и при
/// мирных врагах (<c>combat.enemiesAttack = false</c>). Хешируется, виден агенту.
/// </summary>
public struct EnemyAttack
{
    public float Timer;
}

/// <summary>
/// Intent, после команды игрока: намерение каждого врага (<c>EnemyAi.Think</c> SWORD по местам 1…32 после
/// <c>PlayerBrain.Think</c>) — желаемая скорость и курс-цель (<see cref="FighterCommand"/>), точка взгляда на игрока
/// (<see cref="GazeTarget"/>) и составляющие (<see cref="EnemyAiTerms"/>). Корни — до движения тика: сначала толпа
/// собирается целиком, потом думает каждый. Команды арены — в стадии Begin (6б): Intent уже видит арену после них.
/// С 6в — удар (<see cref="Attack"/>): таймер минус тик; удар командой врагу (<see cref="EnemyCommand.Attack"/>, поверх ИИ на
/// тик) или по таймеру ИИ, если враги нападают (<see cref="CombatRules.EnemiesAttack"/>).
/// </summary>
public sealed class EnemyAiSystem(StrikeTable strikes) : IContinuousSystem
{
    private Query _fighters = null!, _deadPlayer = null!, _enemies = null!;
    private readonly EnemyCrowd _crowd = new();

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.EnemyAi).After(GameLabels.PlayerCommand).ReadsJournal<Event<EnemyCommand>>().Reads<FighterSlot>()
            .Reads<Transform>().Reads<FighterSeed>().Writes<FighterCommand>().Writes<GazeTarget>().Writes<EnemyAiTerms>()
            .Writes<AnimatorAction>().Writes<EnemyAttack>();
        var fighters = s.Query().With<Fighter>().Read<FighterSlot>().Read<Transform>().Read<FighterSeed>();
        _fighters = fighters.Without<Dead>();
        _deadPlayer = fighters.With<PlayerTag>().With<Dead>();
        // мёртвый враг (6г) не думает — он замер
        _enemies = s.Query().With<Fighter>().With<EnemyTag>().Without<Dead>().Read<FighterSlot>().Write<FighterCommand>().Write<GazeTarget>()
            .Write<EnemyAiTerms>().Write<AnimatorAction>().Write<EnemyAttack>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var time = world.Resource<SimTime>();
        var rng = world.Resource<SimRandom>();
        var rules = world.Resource<CombatRules>();
        var clips = world.Resource<PosePool>().Clips;
        var commands = ctx.Journal<Event<EnemyCommand>>();
        var log = ctx.Causality;
        _crowd.Gather(ctx, _fighters, _deadPlayer);
        bool strike = rules.EnemiesAttack && !_crowd.PlayerDead;   // мёртвого игрока не бьют (!p.Dead SWORD)
        foreach (var chunk in ctx.Iterate(_enemies))
        {
            var intent = chunk.Write<FighterCommand>();
            var entities = chunk.Entities;
            var slots = chunk.Read<FighterSlot>()[..intent.Length];
            var gaze = chunk.Write<GazeTarget>()[..intent.Length];
            var terms = chunk.Write<EnemyAiTerms>()[..intent.Length];
            var action = chunk.Write<AnimatorAction>()[..intent.Length];
            var attack = chunk.Write<EnemyAttack>()[..intent.Length];
            for (int i = 0; i < intent.Length; i++)
            {
                int slot = slots[i].Index;
                if ((uint)slot >= ArenaRules.MaxFighters || !_crowd.Think(slot, time.Time, ref intent[i], ref gaze[i], ref terms[i])) continue;
                int serial = action[i].AttackSerial, commanded = Commanded(commands, slot);
                Attack(ref action[i], ref attack[i], terms[i].Distance, commanded, strike, entities[i], time, rng, clips);
                if (log is not null && action[i].AttackSerial != serial) Started(ctx, log, entities[i], serial, action[i].AttackSerial, commands, slot, commanded);
            }
        }
    }

    /// <summary>Удар последней команды тика этому месту (<see cref="EnemyCommand.Attack"/>); команды нет — 0.</summary>
    private static int Commanded(ReadOnlySpan<Event<EnemyCommand>> commands, int slot)
    {
        for (int c = commands.Length - 1; c >= 0; c--)
            if (commands[c].Value.Slot == slot) return commands[c].Value.Attack;
        return 0;
    }

    /// <summary>
    /// Диагностический режим (6д): удар начался — родитель начала удара — команда врагу этого тика или решение ИИ «удар по таймеру»
    /// (корень: команды нет).
    /// </summary>
    private static void Started(in SystemContext ctx, CausalityLog log, Entity e, int before, int serial, ReadOnlySpan<Event<EnemyCommand>> commands,
        int slot, int commanded)
    {
        var parent = CauseId.None;
        if (commanded != 0)
            for (int c = commands.Length - 1; c >= 0; c--)
                if (commands[c].Value.Slot == slot)
                {
                    parent = commands[c].Cause;
                    break;
                }
        if (parent.Value == 0) parent = ctx.Commands.Note(CombatCauses.AiStrike, e, CauseId.None);
        CombatCauses.Started(ctx, log, e, before, serial, parent);
    }

    /// <summary>
    /// Удар врага (<c>EnemyAi.Think</c> SWORD, конец): таймер минус тик; удар команды — если враг может действовать (<c>CanAct</c>
    /// не-<c>Agile</c>), таймер не трогается; иначе ИИ: игрок ближе <see cref="EnemyAiRules.Reach"/>, таймер вышел, может
    /// действовать — рубящий (0,75; случайный из связки) или укол (случайный), таймер 1,1 + ГСЧ·1,4. <paramref name="enemiesAttack"/> —
    /// враги нападают и игрок жив (6г, <c>!p.Dead</c> SWORD); мёртвый враг сюда не приходит (<c>f.CanAttack</c> SWORD: жив; рука и меч
    /// целы — этап 11). Удар командой при мёртвом игроке начинается (вид), но трупы — не цели.
    /// </summary>
    public void Attack(ref AnimatorAction action, ref EnemyAttack state, float distance, int commanded, bool enemiesAttack, Entity e,
        in SimTime time, SimRandom rng, ClipLibrary clips)
    {
        var attacks = strikes.Attacks;
        state.Timer = MathF.Max(0, state.Timer - time.Dt);
        if (commanded != 0)
        {
            int k = commanded - 1;
            if ((uint)k < (uint)attacks.Length && ActionLayer.CanAttack(action, false, attacks)) ActionLayer.StartAttack(ref action, k, attacks[k], clips);
            return;
        }
        if (!enemiesAttack || distance >= EnemyAiRules.Reach || state.Timer > 0 || !ActionLayer.CanAct(action, false, attacks)) return;
        int slashes = strikes.Slashes.Length, stabs = attacks.Length - slashes;
        bool slash = stabs == 0 || rng.Float(EnemyAiRules.AttackStream, time.Tick, e, 0) < EnemyAiRules.SlashChance;
        int pick = slash ? rng.Index(EnemyAiRules.AttackStream, time.Tick, e, slashes, 1)
            : slashes + rng.Index(EnemyAiRules.AttackStream, time.Tick, e, stabs, 1);
        ActionLayer.StartAttack(ref action, pick, attacks[pick], clips);
        state.Timer = EnemyAiRules.NextTimer + rng.Float(EnemyAiRules.AttackStream, time.Tick, e, 2) * EnemyAiRules.NextTimerSpread;
    }
}

/// <summary>
/// Gameplay, до курса и разгона: команды врагу тика (<see cref="EnemyCommand"/>) — намерение врага места вместо ИИ. Стоит
/// после барьера Intent, чтобы лечь поверх и ИИ, и команды арены (в её тике ИИ по новой арене ставится на барьере).
/// Составляющие ИИ (<see cref="EnemyAiTerms"/>) и точка взгляда остаются от ИИ — что он хотел бы. Несколько команд
/// одному месту — последняя; нечисловая скорость — стоять, нечисловой курс — курс-цель ИИ.
/// </summary>
public sealed class EnemyCommandSystem : IContinuousSystem
{
    private Query _enemies = null!;

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.EnemyCommand).Before(GameLabels.RootSteer).ReadsJournal<Event<EnemyCommand>>().Reads<FighterSlot>()
            .Writes<FighterCommand>();
        _enemies = s.Query().With<Fighter>().With<EnemyTag>().Without<Dead>().Read<FighterSlot>().Write<FighterCommand>();
    }

    public void Run(in SystemContext ctx)
    {
        var commands = ctx.Journal<Event<EnemyCommand>>();
        if (commands.IsEmpty) return;
        foreach (var chunk in ctx.Iterate(_enemies))
        {
            var intent = chunk.Write<FighterCommand>();
            var slots = chunk.Read<FighterSlot>()[..intent.Length];
            for (int i = 0; i < intent.Length; i++)
                for (int c = commands.Length - 1; c >= 0; c--)
                {
                    if (commands[c].Value.Slot != slots[i].Index) continue;
                    Apply(commands[c].Value, ref intent[i]);
                    break;
                }
        }
    }

    public static void Apply(in EnemyCommand c, ref FighterCommand intent)
    {
        intent.DesiredVelocity = float.IsFinite(c.Velocity.X) && float.IsFinite(c.Velocity.Y) ? new Vector3(c.Velocity.X, 0, c.Velocity.Y) : Vector3.Zero;
        if (float.IsFinite(c.FacingGoal)) intent.FacingGoal = c.FacingGoal;
    }
}
