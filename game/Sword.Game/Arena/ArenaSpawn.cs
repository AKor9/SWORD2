using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Ai;
using Sword.Game.Animation;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Movement;
using Sword.Game.Ragdoll;

namespace Sword.Game.Arena;

/// <summary>
/// Секция конфига <c>arena</c>. Значение в коде — пустая арена (0 врагов): так собирается мир без <c>sword.json</c> (тесты,
/// реплеи). Ключ exe <c>--units N</c> — то же, что <c>--set arena.enemies=N</c>.
/// </summary>
public sealed record ArenaSettings
{
    /// <summary>Врагов при сборке мира: 0…<see cref="ArenaState.MaxEnemies"/>.</summary>
    public int Enemies { get; init; }
}

/// <summary>
/// Постройка бойца на арене: место, корень, курс, зерно, меч в руке и таймер удара врага (у игрока — 0). Числа — счётчиковым
/// ГСЧ (потоки <see cref="ArenaRules.SpawnStream"/> и <see cref="EnemyAiRules.AttackStream"/>).
/// </summary>
public readonly record struct FighterSpawn(int Slot, Vector3 Position, float Facing, float Seed, int Weapon, float AttackTimer = 0)
{
    public bool IsPlayer => Slot == 0;
}

/// <summary>Правила арены: число бойцов, кольцо спауна.</summary>
public static class ArenaRules
{
    /// <summary>Бойцов на арене не больше: игрок и 32 врага.</summary>
    public const int MaxFighters = 33;

    /// <summary>Кольцо спауна вокруг игрока, м (<c>SpawnMin</c>, <c>SpawnMax</c>).</summary>
    public const float SpawnMin = 5f, SpawnMax = 9f;

    /// <summary>Спаун не дальше радиуса арены минус 3 м (<c>ArenaLayout.ArenaRadius − 3</c> в <c>Spawn</c>).</summary>
    public const float SpawnEdgeMargin = 3f;

    /// <summary>Поток ГСЧ спауна.</summary>
    public static readonly uint SpawnStream = SimRandom.Stream("arena.spawn");

    /// <summary>Число врагов в пределах 0…32.</summary>
    public static int ClampEnemies(long n) => (int)Math.Clamp(n, 0, ArenaState.MaxEnemies);

    /// <summary>
    /// Номер выборки ГСЧ: сущности у бойца ещё нет, поэтому числа — по месту и по «партии» постройки внутри тика
    /// (0 — сборка мира, дальше — номер команды арены тика, начиная с 1): одно место в разных партиях — разные числа.
    /// </summary>
    private static uint Sample(uint batch, int slot, uint k) => unchecked((batch * MaxFighters + (uint)slot) * 3 + k);

    /// <summary>
    /// Номер выборки меча врага — со старшего бита (<see cref="WeaponSampleBase"/>), не пересекается с номерами
    /// <see cref="Sample"/>: расстановка (корни, курсы, зёрна) от выборки меча не зависит.
    /// </summary>
    private static uint WeaponSample(uint batch, int slot) => unchecked(WeaponSampleBase + batch * MaxFighters + (uint)slot);

    /// <summary>Начало номеров выборки меча врага.</summary>
    public const uint WeaponSampleBase = 1u << 31;

    /// <summary>
    /// Партия постройки при возрождении врага (Aftermath): номер, которого нет у команд арены, — возрождение не делит чисел с
    /// постройками того же тика.
    /// </summary>
    public const uint RespawnBatch = 1_000_000;

    /// <summary>
    /// Враг на месте <paramref name="slot"/>: угол a = ГСЧ·τ, радиус r = 5 + ГСЧ·4 вокруг <paramref name="center"/> (не дальше
    /// радиуса арены − 3 м), курс — на центр; зерно — третьим числом; меч — k-е из оружия врагов <paramref name="weapons"/>
    /// своей выборкой.
    /// </summary>
    public static FighterSpawn Enemy(SimRandom rng, ulong tick, uint batch, int slot, Vector3 center, ReadOnlySpan<int> weapons)
    {
        if (slot is < 1 or > ArenaState.MaxEnemies) throw new ArgumentOutOfRangeException(nameof(slot), slot, "место врага — 1…32");
        if (weapons.IsEmpty) throw new ArgumentException("оружия врагов нет", nameof(weapons));
        float a = rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 0)) * MathF.Tau;
        float r = SpawnMin + rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 1)) * (SpawnMax - SpawnMin);
        var pos = new Vector3(center.X + MathF.Cos(a) * r, 0, center.Z + MathF.Sin(a) * r);
        float lim = ArenaLayout.ArenaRadius - SpawnEdgeMargin;
        if (new Vector2(pos.X, pos.Z).Length() > lim) pos = Vector3.Normalize(pos) * lim;
        float facing = MathF.Atan2(center.X - pos.X, center.Z - pos.Z);
        // Таймер удара (1 + ГСЧ·2) — свой поток: расстановка и мечи не сдвигаются.
        float timer = EnemyAiRules.FirstTimer
                      + rng.Float(EnemyAiRules.AttackStream, tick, Entity.Null, Sample(batch, slot, 0)) * EnemyAiRules.FirstTimerSpread;
        return new FighterSpawn(slot, pos, facing, rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, slot, 2)),
            weapons[rng.Index(SpawnStream, tick, Entity.Null, weapons.Length, WeaponSample(batch, slot))], timer);
    }

    /// <summary>Игрок: в центре, курсом 0; зерно — как у врага; меч — <paramref name="weapon"/>.</summary>
    public static FighterSpawn Player(SimRandom rng, ulong tick, uint batch, int weapon) =>
        new(0, Vector3.Zero, 0, rng.Float(SpawnStream, tick, Entity.Null, Sample(batch, 0, 2)), weapon);

    /// <summary>
    /// Партия постройки командой сцены (<see cref="SceneAction.Place"/>): номер, которого нет ни у команд арены (с 1), ни у
    /// возрождения (<see cref="RespawnBatch"/>) — таймер удара поставленного врага не делит чисел ни с одной постройкой того же тика.
    /// </summary>
    public const uint SceneBatch = 2_000_000;

    /// <summary>
    /// Боец места <paramref name="slot"/>, поставленный командой сцены: корень <paramref name="position"/> (y = 0), курс
    /// <paramref name="facing"/>, зерно и меч — прежние бойца; таймер удара врага — как при постройке (партия
    /// <see cref="SceneBatch"/>), у игрока — 0.
    /// </summary>
    public static FighterSpawn Placed(SimRandom rng, ulong tick, int slot, Vector2 position, float facing, float seed, int weapon) =>
        Placed(rng, tick, slot, position, facing, seed, weapon, SceneBatch);

    /// <summary>
    /// Партия постройки заново при нечисловом теле (<c>core.body-rebuild</c>): номер, которого нет ни у команд арены, ни у возрождения, ни
    /// у команд сцены — таймер удара пересобранного врага не делит чисел с ними.
    /// </summary>
    public const uint RebuildBatch = 3_000_000;

    /// <summary>Боец места, поставленный в точку (как <see cref="Placed(SimRandom, ulong, int, Vector2, float, float, int)"/>) — таймер удара партией <paramref name="batch"/>.</summary>
    public static FighterSpawn Placed(SimRandom rng, ulong tick, int slot, Vector2 position, float facing, float seed, int weapon, uint batch)
    {
        if (slot is < 0 or > ArenaState.MaxEnemies) throw new ArgumentOutOfRangeException(nameof(slot), slot, "место — 0…32");
        float timer = slot == 0 ? 0
            : EnemyAiRules.FirstTimer + rng.Float(EnemyAiRules.AttackStream, tick, Entity.Null, Sample(batch, slot, 0)) * EnemyAiRules.FirstTimerSpread;
        return new FighterSpawn(slot, new Vector3(position.X, 0, position.Y), facing, seed, weapon, timer);
    }
}

/// <summary>
/// Боец — один набор компонентов у игрока и у врага; у игрока ещё управление боем, у врага — составляющие ИИ, толчки и таймер
/// удара. Три пути: сборка мира (поза сразу); новый боец командами тика (позу заводит <see cref="Animation.FighterSetupSystem"/>
/// в Gameplay — команда может не сыграть); постройка заново на той же сущности (<see cref="Rebuild"/>).
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
        if (s.IsPlayer)
        {
            world.AddImmediate(e, new PlayerTag());
            world.AddImmediate(e, PlayerControl.Idle);
            world.AddImmediate(e, AttackCombo.Start);
            world.AddImmediate(e, Flinch.None);
        }
        else
        {
            world.AddImmediate(e, new EnemyTag());
            world.AddImmediate(e, new EnemyAiTerms());
            world.AddImmediate(e, RootContacts.None);
            world.AddImmediate(e, new EnemyAttack { Timer = s.AttackTimer });
            world.AddImmediate(e, StrikePairs.None);
        }
        world.AddImmediate(e, Health.Fresh);
        world.AddImmediate(e, HitImpulse.None);   // импульс удара в тело и отдача корню — последние
        world.AddImmediate(e, Recoil.None);
        world.AddImmediate(e, AnimatorAction.None);
        world.AddImmediate(e, new ActionPoses());
        world.AddImmediate(e, new WeaponSlot { Index = s.Weapon });
        world.AddImmediate(e, Blade.None);
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
        world.AddImmediate(e, new BodyRef());   // тело заводит хук добавления (FighterBodies)
        return e;
    }

    /// <summary>
    /// Боец командами тика: появится на барьере стадии; поза — пустой хэндл до <see cref="Animation.FighterSetupSystem"/>.
    /// <paramref name="intent"/> и <paramref name="gaze"/> — намерение и точка взгляда до Intent этого тика.
    /// </summary>
    public static void Create(CommandBuffer c, in FighterSpawn s, in FighterCommand intent, in GazeTarget gaze, in EnemyAiTerms terms = default)
    {
        var d = c.Create();
        c.Add(d, new Fighter());
        if (s.IsPlayer)
        {
            c.Add(d, new PlayerTag());
            c.Add(d, PlayerControl.Idle);
            c.Add(d, AttackCombo.Start);
            c.Add(d, Flinch.None);
        }
        else
        {
            c.Add(d, new EnemyTag());
            c.Add(d, terms);
            c.Add(d, RootContacts.None);
            c.Add(d, new EnemyAttack { Timer = s.AttackTimer });
            c.Add(d, StrikePairs.None);
        }
        c.Add(d, Health.Fresh);
        c.Add(d, HitImpulse.None);   // импульс удара в тело и отдача корню — последние
        c.Add(d, Recoil.None);
        c.Add(d, AnimatorAction.None);
        c.Add(d, new ActionPoses());
        c.Add(d, new WeaponSlot { Index = s.Weapon });
        c.Add(d, Blade.None);
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
        c.Add(d, new BodyRef());   // тело заводит хук добавления на барьере стадии (FighterBodies)
    }

    /// <summary>
    /// Постройка заново на той же сущности (новое поколение): корень без интерполяции, скорость, намерение, действие, меч и клинок —
    /// заново; место, тег, поза и тело — прежние. Одна функция на все постройки места заново (сброс, смена числа врагов,
    /// возрождение). Метка <see cref="Fighter"/> снимается и ставится на одном барьере — модули заводят своё состояние бойца с нуля.
    /// </summary>
    public static void Rebuild(CommandBuffer c, Entity e, in FighterSpawn s, in FighterCommand intent, in GazeTarget gaze,
        in AnimatorAction action, in EnemyAiTerms terms = default, bool dead = false)
    {
        c.Remove<Fighter>(e);
        c.Add(e, new Fighter());
        // Здоровье — полное, мёртвый оживает (возрождение врага, сброс арены), вздрагивание игрока и пары удара врага — с нуля
        if (dead) c.Remove<Dead>(e);
        c.Remove<Severance>(e);   // отделённые части уходят с телом: постройка их убирает (BodySetupSystem)
        c.Set(e, Health.Fresh);
        c.Set(e, HitImpulse.None);   // импульс удара и отдача — с нуля
        c.Set(e, Recoil.None);
        c.Set(e, new FighterSeed { Value = s.Seed });
        var root = new Transform { Position = s.Position, Facing = s.Facing };
        c.Set(e, root);
        c.Set(e, new PrevTransform { Position = root.Position, Facing = root.Facing });
        c.Set(e, new RootVelocity());
        c.Set(e, intent);
        c.Set(e, gaze);
        c.Set(e, new AnimatorMotion());
        c.Set(e, action.Rebuilt());   // без действия и снимка; номер удара и счёт снимков — прежние
        c.Set(e, new WeaponSlot { Index = s.Weapon });
        c.Set(e, Blade.None);
        if (s.IsPlayer)
        {
            c.Set(e, PlayerControl.Idle);   // буферы, перезарядка и рывок — с нуля; связка — прежняя
            c.Set(e, Flinch.None);
        }
        else
        {
            c.Set(e, terms);
            c.Set(e, new EnemyAttack { Timer = s.AttackTimer });
            c.Set(e, StrikePairs.None);
        }
    }

    /// <summary>Объявления системы, которая строит бойцов командами (<see cref="Create(CommandBuffer, in FighterSpawn, in FighterCommand, in GazeTarget, in EnemyAiTerms)"/>, <see cref="Rebuild"/>).</summary>
    public static SystemSetup DeclaresFighters(this SystemSetup s) => s.Creates().Destroys()
        .Adds<Fighter>().Adds<PlayerTag>().Adds<EnemyTag>().Adds<FighterSlot>().Adds<FighterSeed>().Adds<Transform>()
        .Adds<PrevTransform>().Adds<RootVelocity>().Adds<FighterCommand>().Adds<GazeTarget>().Adds<AnimatorMotion>()
        .Adds<PoseRef>().Adds<BodyRef>().Adds<EnemyAiTerms>().Adds<RootContacts>().Adds<AnimatorAction>().Adds<ActionPoses>()
        .Adds<WeaponSlot>().Adds<Blade>().Adds<PlayerControl>().Adds<AttackCombo>().Adds<EnemyAttack>()
        .Adds<Health>().Adds<StrikePairs>().Adds<Flinch>().Adds<HitImpulse>().Adds<Recoil>()
        .DeclaresRebuild();

    /// <summary>Объявления системы, которая только строит бойцов заново (<see cref="Rebuild"/>: возрождение врага).</summary>
    public static SystemSetup DeclaresRebuild(this SystemSetup s) => s
        .Adds<Fighter>().Removes<Fighter>().Removes<Dead>().Removes<Severance>().Sets<FighterSeed>().Sets<Transform>().Sets<PrevTransform>().Sets<RootVelocity>()
        .Sets<FighterCommand>().Sets<GazeTarget>().Sets<AnimatorMotion>().Sets<EnemyAiTerms>().Sets<AnimatorAction>()
        .Sets<WeaponSlot>().Sets<Blade>().Sets<PlayerControl>().Sets<EnemyAttack>().Sets<Health>().Sets<StrikePairs>().Sets<Flinch>()
        .Sets<HitImpulse>().Sets<Recoil>();
}
