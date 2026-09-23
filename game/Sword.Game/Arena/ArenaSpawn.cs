using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Contracts;

namespace Sword.Game.Arena;

/// <summary>
/// Секция конфига <c>arena</c>. Значение в коде — пустая арена (0 врагов): так собирается мир без <c>sword.json</c> —
/// тесты, сценарии фич и реплеи, записанные до этапа 4 (в их итоговом конфиге секции нет). Игра берёт число из
/// <c>sword.json</c> — 10, как <c>Units</c> SWORD; ключ exe <c>--units N</c> — то же, что <c>--set arena.enemies=N</c>.
/// </summary>
public sealed record ArenaSettings
{
    /// <summary>Врагов при сборке мира: 0…<see cref="ArenaState.MaxEnemies"/>.</summary>
    public int Enemies { get; init; }
}

/// <summary>
/// Постройка бойца на арене — <c>DuelArena.Reset/Spawn</c> и <c>Fighter.Build</c> SWORD 2eab13b: место, корень, курс,
/// зерно. Числа — счётчиковым ГСЧ SWORD2 (поток <see cref="SpawnStream"/>), а не <c>Random(7)</c> SWORD.
/// </summary>
public readonly record struct FighterSpawn(int Slot, Vector3 Position, float Facing, float Seed)
{
    public bool IsPlayer => Slot == 0;
}

/// <summary>Правила арены SWORD 2eab13b (<c>DuelArena</c>): число бойцов, кольцо спауна.</summary>
public static class ArenaRules
{
    /// <summary>Бойцов на арене не больше (<c>DuelArena.MaxFighters</c>): игрок и 32 врага.</summary>
    public const int MaxFighters = 33;

    /// <summary>Кольцо спауна вокруг игрока, м (<c>SpawnMin</c>, <c>SpawnMax</c>).</summary>
    public const float SpawnMin = 5f, SpawnMax = 9f;

    /// <summary>Спаун не дальше радиуса арены минус 3 м (<c>ArenaLayout.ArenaRadius − 3</c> в <c>Spawn</c>).</summary>
    public const float SpawnEdgeMargin = 3f;

    /// <summary>Поток ГСЧ спауна.</summary>
    public static readonly uint SpawnStream = SimRandom.Stream("arena.spawn");

    /// <summary>Число врагов в пределах 0…32 (<c>Math.Clamp(n, 0, MaxFighters − 1)</c> SWORD).</summary>
    public static int ClampEnemies(long n) => (int)Math.Clamp(n, 0, ArenaState.MaxEnemies);

    /// <summary>
    /// Номер выборки ГСЧ: сущности у бойца ещё нет, поэтому числа — по месту и по «партии» постройки внутри тика
    /// (0 — сборка мира, дальше — номер команды арены тика, начиная с 1): одно место в разных партиях — разные числа.
    /// </summary>
    private static uint Sample(uint batch, int slot, uint k) => unchecked((batch * MaxFighters + (uint)slot) * 3 + k);

    /// <summary>
    /// Враг на месте <paramref name="slot"/> (<c>DuelArena.Spawn</c>): угол a = ГСЧ·τ, радиус r = 5 + ГСЧ·4 вокруг
    /// <paramref name="center"/> (корня игрока — в SWORD таз его тела, тел до этапа 5 нет); дальше радиуса арены − 3 м —
    /// на этот радиус; курс — на центр (<c>atan2(p.X − pos.X, p.Z − pos.Z)</c>); зерно — третьим числом (в SWORD — в
    /// <c>Fighter.Build</c> тем же генератором после угла и радиуса).
    /// </summary>
    public static FighterSpawn Enemy(SimRandom rng, ulong tick, uint batch, int slot, Vector3 center)
    {
        if (slot is < 1 or > ArenaState.MaxEnemies) throw new ArgumentOutOfRangeException(nameof(slot), slot, "место врага — 1…32");
        float a = rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 0)) * MathF.Tau;
        float r = SpawnMin + rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 1)) * (SpawnMax - SpawnMin);
        var pos = new Vector3(center.X + MathF.Cos(a) * r, 0, center.Z + MathF.Sin(a) * r);
        float lim = ArenaLayout.ArenaRadius - SpawnEdgeMargin;
        if (new Vector2(pos.X, pos.Z).Length() > lim) pos = Vector3.Normalize(pos) * lim;
        float facing = MathF.Atan2(center.X - pos.X, center.Z - pos.Z);
        return new FighterSpawn(slot, pos, facing, rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 2)));
    }

    /// <summary>Игрок (<c>Build(Player, Vector3.Zero, 0)</c> в <c>DuelArena.Reset</c>): в центре, курсом 0; зерно — как у врага.</summary>
    public static FighterSpawn Player(SimRandom rng, ulong tick, uint batch) =>
        new(0, Vector3.Zero, 0, rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, 0, 2)));
}

/// <summary>
/// Боец — один набор компонентов у игрока и у врага (всё, что нужно движению и анимации): тег стороны, место, зерно,
/// корень и прошлый корень (тот же — боец не «прилетает» в кадр из начала координат; <c>RenderPos = Pos</c> SWORD),
/// скорость 0, намерение — стоять курсом постройки (<c>FacingGoal = facing</c>), точка взгляда, вход аниматора, поза.
/// Три пути: сборка мира (немедленно, поза берётся сразу); новый боец командами тика (поза — пустой хэндл, её заводит
/// <see cref="Animation.FighterSetupSystem"/> в Gameplay: команда может не сыграть, а поза из пула — не вернуться); постройка
/// заново на той же сущности (<see cref="Rebuild"/> — <c>Fighter.Build</c> SWORD над тем же бойцом места).
/// </summary>
public static class Fighters
{
    /// <summary>Намерение новорождённого бойца: стоять, курс-цель — курс постройки.</summary>
    public static FighterCommand Standing(in FighterSpawn s) => new() { FacingGoal = s.Facing };

    /// <summary>Боец при сборке мира (немедленные операции): поза — сразу, до первого тика это поза привязки.</summary>
    public static Entity Create(World world, in FighterSpawn s)
    {
        var e = world.CreateImmediate();
        world.AddImmediate(e, new Fighter());
        if (s.IsPlayer) world.AddImmediate(e, new PlayerTag());
        else world.AddImmediate(e, new EnemyTag());
        world.AddImmediate(e, new FighterSlot { Index = s.Slot });
        world.AddImmediate(e, new FighterSeed { Value = s.Seed });
        var root = new Transform { Position = s.Position, Facing = s.Facing };
        world.AddImmediate(e, root);
        world.AddImmediate(e, new PrevTransform { Position = root.Position, Facing = root.Facing });
        world.AddImmediate(e, new RootVelocity());
        world.AddImmediate(e, Standing(s));
        world.AddImmediate(e, new GazeTarget());
        world.AddImmediate(e, new AnimatorMotion());
        var pose = world.Resource<PosePool>().Alloc();
        world.AddImmediate(e, new PoseRef { Index = pose.Index, Generation = pose.Generation });
        return e;
    }

    /// <summary>
    /// Боец командами тика (черновик): появится на барьере стадии со всеми компонентами; поза — пустой хэндл до
    /// <see cref="Animation.FighterSetupSystem"/>. <paramref name="intent"/> и <paramref name="gaze"/> — намерение и точка
    /// взгляда на этот тик (игроку после сброса — его команда тика и прежняя точка взгляда, как в SWORD).
    /// </summary>
    public static void Create(CommandBuffer c, in FighterSpawn s, in FighterCommand intent, in GazeTarget gaze)
    {
        var d = c.Create();
        c.Add(d, new Fighter());
        if (s.IsPlayer) c.Add(d, new PlayerTag());
        else c.Add(d, new EnemyTag());
        c.Add(d, new FighterSlot { Index = s.Slot });
        c.Add(d, new FighterSeed { Value = s.Seed });
        var root = new Transform { Position = s.Position, Facing = s.Facing };
        c.Add(d, root);
        c.Add(d, new PrevTransform { Position = root.Position, Facing = root.Facing });
        c.Add(d, new RootVelocity());
        c.Add(d, intent);
        c.Add(d, gaze);
        c.Add(d, new AnimatorMotion());
        c.Add(d, new PoseRef());
    }

    /// <summary>
    /// Постройка заново на той же сущности (<c>Fighter.Build</c> SWORD: тот же боец места, новое поколение): корень, прошлый
    /// корень, скорость, намерение, точка взгляда, вход аниматора и зерно — заново; место, тег и поза — прежние. Метка
    /// <see cref="Fighter"/> снимается и ставится на одном барьере — <c>Removed&lt;Fighter&gt;</c> и <c>Added&lt;Fighter&gt;</c>
    /// (протокол <c>Game.Contracts</c>): модули заводят своё состояние бойца с нуля (локомоция — со стойки, как
    /// <c>ResetAnimator</c> SWORD), имя и номер сущности у агента не меняются.
    /// </summary>
    public static void Rebuild(CommandBuffer c, Entity e, in FighterSpawn s, in FighterCommand intent, in GazeTarget gaze)
    {
        c.Remove<Fighter>(e);
        c.Add(e, new Fighter());
        c.Set(e, new FighterSeed { Value = s.Seed });
        var root = new Transform { Position = s.Position, Facing = s.Facing };
        c.Set(e, root);
        c.Set(e, new PrevTransform { Position = root.Position, Facing = root.Facing });
        c.Set(e, new RootVelocity());
        c.Set(e, intent);
        c.Set(e, gaze);
        c.Set(e, new AnimatorMotion());
    }

    /// <summary>Объявления системы, которая строит бойцов командами (<see cref="Create(CommandBuffer, in FighterSpawn, in FighterCommand, in GazeTarget)"/>, <see cref="Rebuild"/>).</summary>
    public static SystemSetup DeclaresFighters(this SystemSetup s) => s.Creates().Destroys()
        .Adds<Fighter>().Adds<PlayerTag>().Adds<EnemyTag>().Adds<FighterSlot>().Adds<FighterSeed>().Adds<Transform>()
        .Adds<PrevTransform>().Adds<RootVelocity>().Adds<FighterCommand>().Adds<GazeTarget>().Adds<AnimatorMotion>()
        .Adds<PoseRef>()
        .Removes<Fighter>().Sets<FighterSeed>().Sets<Transform>().Sets<PrevTransform>().Sets<RootVelocity>()
        .Sets<FighterCommand>().Sets<GazeTarget>().Sets<AnimatorMotion>();
}
