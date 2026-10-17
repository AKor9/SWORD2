using System.Numerics;
using Sword.Engine.Content;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Animation;
using Sword.Game.Bootstrap;
using Sword.Game.Contracts;

namespace Sword.Game.Tests.Cutting;

/// <summary>
/// Сцена расчленения на паке игры: враг перед игроком, пробы рубят плечо, голень, затем голову трупа. Реплей сцены пишется в
/// <c>artifacts/dismember/scene.swreplay</c> (вне git) — его показывает <c>Sword.exe --replay</c>. Инварианты чисты.
/// </summary>
[Trait("Category", "Content")]
public sealed class SeverSceneTests
{
    private static readonly Lazy<(CharacterAssets Assets, string Content)> Pak = new(() =>
    {
        using var pak = PakSource.Open(TestContent.GamePak());
        return (CharacterAssets.Load(pak), pak.ContentHash);
    });

    [Fact]
    public void Scene_SeversOnPak_AndRecordsReplay()
    {
        var (assets, content) = Pak.Value;
        var sim = SwordSimulation.Create(ConfigSet.Empty(["arena.enemies=4", "combat.enemiesAttack=false"]), assets);
        var recorder = sim.Record(new ReplayInfo("test", "tests", content));
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(0, 0), Facing = 0 });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 1, Position = new Vector2(0, 1.6f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 2, Position = new Vector2(2.2f, 1.6f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 3, Position = new Vector2(-2.5f, 2.5f), Facing = MathF.PI });
        sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = 4, Position = new Vector2(3f, -2f), Facing = 0 });
        var modes = new List<SeverMode>();
        for (int t = 0; t < 900; t++)
        {
            if (t == 120) Probe(sim, 1, BodySegs.UArmL, 60);
            if (t == 240) Probe(sim, 1, BodySegs.FArmR, 60);
            if (t == 360) Probe(sim, 1, BodySegs.ThighR, 70);
            if (t == 540) Probe(sim, 1, BodySegs.Head, 60);
            if (t == 150) Probe(sim, 2, BodySegs.FArmL, 40, HitKind.Blunt);   // перелом
            if (t == 300) Probe(sim, 2, BodySegs.Head, 80, HitKind.Blunt);    // голова в клочья
            if (t == 420) sim.Enqueue(new BlastCommand { Position = new Vector3(-2.3f, 0.8f, 2.4f), Energy = 40000, Radius = 5 });
            if (t == 200) Probe(sim, 3, BodySegs.ShinL, 80, HitKind.Blunt);   // отрыв голени у живого (без кусков)
            if (t == 60) Probe(sim, 4, BodySegs.Torso, 120);   // торс пополам: верх с головой и руками — кусок
            sim.Export.BeginFrame();
            sim.Step();
            foreach (var r in sim.Export.Frame<Event<LimbDetached>>()) modes.Add(r.Record.Value.Mode);
        }
        Assert.Contains(SeverMode.Cut, modes);
        Assert.Contains(SeverMode.Fracture, modes);
        Assert.Contains(SeverMode.Shatter, modes);
        Assert.Contains(SeverMode.Tear, modes);
        Assert.True(sim.Invariants.Total == 0, string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick}: {v.Details}")));
        string dir = Path.Combine(Root(), "artifacts", "dismember");
        Directory.CreateDirectory(dir);
        recorder.ToReplay().Save(Path.Combine(dir, "scene.swreplay"));
    }

    private sealed class Take(Action<IPhysicsWorld> take) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
    }

    /// <summary>
    /// Выпавшее оружие (смерть и отрубленная рука с мечом, все мечи пака по очереди у игрока-жертвы нет — у врагов) лежит на земле:
    /// ни одна вершина его меша не уходит под землю глубже 1,5 см — навершие, гарда и головка держатся щупами земли.
    /// </summary>
    [Fact]
    public void DroppedWeapons_StayAboveGround()
    {
        var (assets, _) = Pak.Value;
        var weapons = assets.Weapons;
        var meshes = new System.Numerics.Vector3[weapons.Count][];
        using (var pak = PakSource.Open(TestContent.GamePak()))
            for (int w = 0; w < weapons.Count; w++)
                using (var s = pak.OpenRead(weapons.Weapons[w].Mesh))
                    meshes[w] = Sword.Game.Data.WeaponMesh.Read(s, weapons.Weapons[w].Mesh).Points;
        IPhysicsWorld? physics = null;
        var sim = Simulation.Build(ConfigSet.Empty(["arena.enemies=6", "combat.enemiesAttack=false"]), GameStages.All,
            b => GameCore.Configure(b, assets), [.. SwordSimulation.Modules(), new Take(p => physics = p)], SwordSimulation.ClientModuleIds);
        for (int slot = 1; slot <= 6; slot++)
            sim.Enqueue(new SceneCommand { Action = SceneAction.Place, Slot = slot, Position = new Vector2(slot * 2.5f - 9, 4), Facing = 0 });
        for (int t = 0; t < 5; t++) sim.Step();
        for (int slot = 1; slot <= 6; slot++)
            if (slot % 2 == 0) sim.Enqueue(new SceneCommand { Action = SceneAction.Kill, Slot = slot });
            else Probe(sim, slot, BodySegs.FArmR, 60);
        for (int t = 0; t < 480; t++) sim.Step();
        float worst = float.MaxValue;
        string where = "";
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<EnemyTag>().Read<BodyRef>().Read<WeaponSlot>()))
            for (int i = 0; i < chunk.Count; i++)
            {
                int w = chunk.Read<WeaponSlot>()[i].Index;
                var body = Bodies.FighterBodies.Handle(chunk.Read<BodyRef>()[i]);
                var s = physics!.Get(physics.Body(body, BodySegs.Weapon));
                Assert.Equal(Sword.Engine.Physics.BodyMode.Simulated, s.Mode);
                var system = Matrix4x4.CreateFromQuaternion(s.Rotation);
                system.Translation = s.Position;
                var toWorld = Client.ArenaView.BladeInHandInverse(weapons.Bodies[w]) * system;
                foreach (var p in meshes[w])
                {
                    var q = Vector3.Transform(p, toWorld);
                    float above = q.Y - Arena.ArenaTerrain.Height(q.X, q.Z);
                    if (above < worst) (worst, where) = (above, weapons.Weapons[w].Name);
                }
            }
        Assert.True(worst > -0.015f, $"оружие {where} ушло под землю на {-worst:F3} м");
    }

    private static void Probe(Simulation sim, int victim, int part, float damage, HitKind kind = HitKind.Slash) =>
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = victim, Part = part, Damage = damage, Kind = kind });

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SWORD.slnx"))) return dir.FullName;
        throw new InvalidOperationException("нет корня репозитория");
    }
}
