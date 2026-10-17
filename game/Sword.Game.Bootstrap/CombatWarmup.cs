using System.Numerics;
using Sword.Engine.Diagnostics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Contracts;

namespace Sword.Game.Bootstrap;

/// <summary>
/// Прогрев боевых путей: первый удар каждого вида (разрез, пошатывание, перелом, падение и вставание, взрыв) иначе компилируется JIT
/// прямо в тике игры — рывок 5–13 мс. Отдельная симуляция той же композиции (конфиг игры + арена из трёх врагов, без инвариантов)
/// проигрывает короткую сцену пробами и ударами игрока; игровая симуляция и её хеши её не видят. Только в фоне при запуске игры.
/// </summary>
public static class CombatWarmup
{
    /// <summary>Тиков сцены: падение и вставание укладываются (не позже 5 с лёжа).</summary>
    public const int Ticks = 760;

    /// <summary>Прогреть в фоне; итог — шагов сделано (ошибка прогрева игру не останавливает — пишется в лог).</summary>
    public static Task<int> Start(ConfigSet config, CharacterAssets assets) => Task.Run(() =>
    {
        try
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int ticks = Run(config, assets);
            Log.Info("warmup", $"прогрев боя: {ticks} тиков, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds:0.00} с");
            return ticks;
        }
        catch (Exception e)
        {
            Log.Warning("warmup", $"прогрев боя: {e.GetBaseException().Message}");
            return 0;
        }
    });

    /// <summary>Сцена прогрева на копии конфига <paramref name="config"/>; вернёт число тиков.</summary>
    public static int Run(ConfigSet config, CharacterAssets assets)
    {
        var sets = new[] { "arena.enemies=3", "combat.enemiesAttack=false", "combat.playerInvulnerable=true", "sim.invariants=false" };
        var sim = SwordSimulation.Create(ConfigSet.FromReplay(config.Effective, sets), assets);
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.4f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 2, Position = new Vector2(1.8f, 1.2f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 3, Position = new Vector2(-1.8f, 1.2f), Facing = MathF.PI });
        for (int t = 0; t < Ticks; t++)
        {
            Script(sim, t);
            // удары игрока по врагу перед ним: касания клинка, отдача
            sim.Enqueue(new PlayerCommand { Attack = t % 60 < 30, HasGaze = true, Gaze = new Vector3(0, 0, 1.4f) });
            sim.Export.BeginFrame();
            sim.Step();
        }
        return Ticks;
    }

    private static void Script(Simulation sim, int t)
    {
        switch (t)
        {
            case 10: Probe(sim, 1, BodySegs.UArmL, 60, HitKind.Slash); break;      // разрез у живого
            case 20: Probe(sim, 1, BodySegs.Torso, 20, HitKind.Thrust); break;     // укол, вздрагивание
            case 30: Probe(sim, 2, BodySegs.Torso, 30, HitKind.Blunt); break;      // пошатывание
            case 40: Probe(sim, 2, BodySegs.FArmR, 40, HitKind.Blunt); break;      // перелом руки с мечом
            case 50 or 51: Probe(sim, 3, BodySegs.Pelvis, 40, HitKind.Blunt); break;   // падение, затем вставание
            case 60: Probe(sim, 2, BodySegs.ShinL, 40, HitKind.Blunt); break;      // перелом ноги — лежит
            case 120: Probe(sim, 2, BodySegs.FArmR, 60, HitKind.Blunt); break;     // удар по лежащему
            case 700: Probe(sim, 1, BodySegs.Head, 60, HitKind.Slash); break;      // смерть
            case 720: Probe(sim, 1, BodySegs.Head, 80, HitKind.Blunt); break;      // голова трупа в клочья
            case 740: sim.Enqueue(new BlastCommand { Position = new Vector3(1.8f, 0.8f, 1.2f), Energy = 20000, Radius = 3 }); break;
        }
    }

    private static void Probe(Simulation sim, int victim, int part, float damage, HitKind kind) =>
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = victim, Part = part, Damage = damage, Kind = kind });
}
