using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Contracts;

namespace Sword.Game.Ai;

/// <summary>
/// Константы ИИ врага — <c>Gameplay/Ai/EnemyAi.cs</c> SWORD 2eab13b (<c>Think</c>). Дистанция удара <c>Reach = 2.3f</c>,
/// таймер удара и блок — этап 6 (бой): здесь их нет.
/// </summary>
public static class EnemyAiRules
{
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

    public void Clear() => Array.Clear(Present);

    public void Put(int slot, Vector3 root, float seed)
    {
        Roots[slot] = root;
        Seeds[slot] = seed;
        Present[slot] = true;
    }

    /// <summary>
    /// Толпа мира до движения тика: все бойцы с местом (<see cref="FighterSlot"/>) — корень и зерно. Запрос —
    /// <c>With&lt;Fighter&gt;().Read&lt;FighterSlot&gt;().Read&lt;Transform&gt;().Read&lt;FighterSeed&gt;()</c>.
    /// </summary>
    public void Gather(in SystemContext ctx, Query fighters)
    {
        Clear();
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
    /// Намерение врага места <paramref name="slot"/> по этой толпе (<see cref="EnemyAi.Think"/>); игрока (место 0) нет —
    /// false, намерение не трогается.
    /// </summary>
    public bool Think(int slot, float time, ref FighterCommand intent, ref GazeTarget gaze, ref EnemyAiTerms terms)
    {
        if (!Present[0]) return false;
        intent = EnemyAi.Think(slot, Roots[slot], Seeds[slot], Roots[0], time, Roots, Present, out terms);
        gaze = new GazeTarget { Point = Roots[0], Has = true };
        return true;
    }
}

/// <summary>
/// ИИ врага — <c>EnemyAi.Think</c> SWORD 2eab13b без удара, блока и таймера удара (этап 6) и без <c>p.Dead</c> (смерти ещё
/// нет): идёт на игрока до 1,8 м, покачиваясь вбок, и расходится с врагами ближе 1,4 м; курс-цель и точка взгляда — на
/// игрока. Порядок вычислений float — как в SWORD (сверка трассой на 4д): расхождение складывается по местам 1…32.
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
/// Intent, после команды игрока: намерение каждого врага (<c>EnemyAi.Think</c> SWORD по местам 1…32 после
/// <c>PlayerBrain.Think</c>) — желаемая скорость и курс-цель (<see cref="FighterCommand"/>), точка взгляда на игрока
/// (<see cref="GazeTarget"/>) и составляющие (<see cref="EnemyAiTerms"/>). Корни — до движения тика: сначала толпа
/// собирается целиком, потом думает каждый. Тик, в котором команда арены меняет состав или расстановку, думает за всех
/// врагов уже по новой арене <see cref="ArenaCommandSystem"/> (её запись ложится на барьере Intent поверх этой):
/// в SWORD <c>SetEnemyCount</c> и <c>Reset</c> идут до <c>Think</c> того же тика.
/// </summary>
public sealed class EnemyAiSystem : IContinuousSystem
{
    private Query _fighters = null!, _enemies = null!;
    private readonly EnemyCrowd _crowd = new();

    public void Setup(SystemSetup s)
    {
        s.Label(GameLabels.EnemyAi).After(GameLabels.PlayerCommand).Reads<FighterSlot>().Reads<Transform>().Reads<FighterSeed>()
            .Writes<FighterCommand>().Writes<GazeTarget>().Writes<EnemyAiTerms>();
        _fighters = s.Query().With<Fighter>().Read<FighterSlot>().Read<Transform>().Read<FighterSeed>();
        _enemies = s.Query().With<Fighter>().With<EnemyTag>().Read<FighterSlot>().Write<FighterCommand>().Write<GazeTarget>()
            .Write<EnemyAiTerms>();
    }

    public void Run(in SystemContext ctx)
    {
        float time = ctx.World.Resource<SimTime>().Time;
        _crowd.Gather(ctx, _fighters);
        foreach (var chunk in ctx.Iterate(_enemies))
        {
            var intent = chunk.Write<FighterCommand>();
            var slots = chunk.Read<FighterSlot>()[..intent.Length];
            var gaze = chunk.Write<GazeTarget>()[..intent.Length];
            var terms = chunk.Write<EnemyAiTerms>()[..intent.Length];
            for (int i = 0; i < intent.Length; i++)
                if ((uint)slots[i].Index < ArenaRules.MaxFighters) _crowd.Think(slots[i].Index, time, ref intent[i], ref gaze[i], ref terms[i]);
        }
    }
}
