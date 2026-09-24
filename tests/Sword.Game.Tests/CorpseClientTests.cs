using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Engine.Introspection;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Client;
using Sword.Game.Combat;
using Sword.Game.Contracts;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 7и этапа 7 — труп на экране без окна и GPU (свои тесты свойств): скин трупа — сустав = привязка × показанное тело (до сдвига —
/// ровно поза смерти, ≤ 0,1 мм; после «сдвинуть тела» — за телами), меч на теле клинка, рамка по телам, капсулы трупа; показ тела трупа
/// (на физике — интерполяция позы начала тика, застывшее — поза тика); суставы тела (оверлей <c>joints</c>) по виду отладки физики;
/// контакты последнего подшага из трассы (диагностический режим <c>physics.trace</c>, вид <see cref="IPhysicsDebugView"/>); 0 Б на кадр
/// с 32 трупами; оверлеи клиента — в строгом разборе видов манифестов. Контент — синтетический (<see cref="TestAssets"/>).
/// </summary>
public sealed class CorpseClientTests
{
    private static readonly Func<float, float, float> Field = ArenaTerrain.Height;

    /// <summary>Модуль теста: берёт физику симуляции после сборки мира.</summary>
    private sealed class Probe(Action<IPhysicsWorld> take) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
    }

    /// <summary>Симуляция арены с командами сцен из очереди теста (каждый кадр — один тик) и физикой симуляции.</summary>
    private sealed class Scene
    {
        public readonly Simulation Sim;
        public readonly IPhysicsWorld Physics;
        public readonly SimulationClock Clock;
        public readonly List<SceneCommand> Pending = [];

        public Scene(int enemies)
        {
            IPhysicsWorld? physics = null;
            Sim = Simulation.Build(ConfigSet.Empty($"arena.enemies={enemies}"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
                [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p)]);
            Physics = physics!;
            Clock = new SimulationClock(Sim, (link, _) =>
            {
                foreach (var c in Pending) link.Enqueue(c);
                Pending.Clear();
            }, ticksPerFrame: 1);
        }

        public void Frames(int n, ArenaView view, float alpha = 1, bool joints = false, bool contacts = false)
        {
            for (int i = 0; i < n; i++)
            {
                Clock.RunFrame(0);
                view.Extract(Clock.Link, alpha, joints: joints, contacts: contacts);
            }
        }

        public Entity Fighter(int slot)
        {
            foreach (var chunk in Sim.World.Iterate(Sim.World.Query().With<Fighter>().Read<FighterSlot>()))
                for (int i = 0; i < chunk.Count; i++)
                    if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
            throw new InvalidOperationException($"нет бойца на месте {slot}");
        }
    }

    private static SceneCommand Kill(int slot) => new() { Action = SceneAction.Kill, Slot = slot };

    private static SceneCommand Shift(int slot, Vector3 offset) => new() { Action = SceneAction.ShiftBodies, Slot = slot, Offset = offset };

    private static FighterView Shown(ArenaView view, Entity e) => view.Fighters.Single(f => f.Entity == e);

    /// <summary>Сустав позы смерти в мире: модельная матрица пула × мир бойца (как снимок привязки, 7ж).</summary>
    private static Matrix4x4[] DeathPose(Simulation sim, Entity e)
    {
        var model = sim.World.View<IAnimationView>().Model(sim.World.Get<PoseRef>(e));
        var root = sim.World.Get<Transform>(e);
        var world = BodyPlacement.World(root.Position, root.Facing);
        var pose = new Matrix4x4[model.Length];
        for (int j = 0; j < pose.Length; j++) pose[j] = model[j] * world;
        return pose;
    }

    /// <summary>Наибольшее расхождение двух матриц: перенос (м) и оси (длины разности базисных векторов).</summary>
    private static float Distance(in Matrix4x4 a, in Matrix4x4 b)
    {
        float d = Vector3.Distance(a.Translation, b.Translation);
        foreach (var axis in (Vector3[])[Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ])
            d = MathF.Max(d, Vector3.Distance(Vector3.TransformNormal(axis, a), Vector3.TransformNormal(axis, b)));
        return d;
    }

    /// <summary>
    /// Скин трупа (7и) — сустав = привязка × тело его сегмента: пока тела стоят, это поза смерти (≤ 0,1 мм и по осям) при любой доле тика
    /// (застывшие кинематические тела трупа — поза тика: интерполяция к прошлой постановке живого не качает труп); меч — на теле клинка:
    /// до сдвига — там же, где в кисти позы смерти; «сдвинуть тела» (команда сцены) — суставы, меч и капсулы трупа ровно на вектор сдвига,
    /// рамка трупа — по его телам. Живой — как раньше (поза пула, не труп).
    /// </summary>
    [Fact]
    public void CorpseSkin_IsDeathPose_ThenFollowsShiftedBodies_SwordOnBladeBody()
    {
        var scene = new Scene(enemies: 2);
        var view = new ArenaView(Field);
        scene.Frames(20, view);
        var enemy = scene.Fighter(1);
        scene.Pending.Add(Kill(1));
        scene.Frames(3, view);
        var death = DeathPose(scene.Sim, enemy);
        int hand = scene.Sim.World.View<ICombatView>().HandJoint;
        foreach (float alpha in (float[])[0.1f, 0.5f, 1f])
        {
            view.Extract(scene.Clock.Link, alpha);
            var f = Shown(view, enemy);
            Assert.True(f.Dead && f.Corpse, $"доля {alpha}: мёртвый с привязкой — труп по телам");
            var pose = view.Pose(f);
            Assert.Equal(death.Length, pose.Length);
            float worst = 0;
            for (int j = 0; j < pose.Length; j++) worst = MathF.Max(worst, Distance(pose[j], death[j]));
            Assert.True(worst <= 1e-4f, $"доля {alpha}: скин трупа ушёл от позы смерти на {worst}");
            Assert.True(Distance(f.WeaponWorld, death[hand]) <= 1e-4f, $"доля {alpha}: меч на теле клинка не там, где был в кисти");
            Assert.False(Shown(view, scene.Fighter(2)).Corpse);   // живой — поза пула
            Assert.False(view.Player!.Value.Corpse);
        }
        Assert.Equal(1, view.CorpseCount);

        var before = view.Pose(Shown(view, enemy)).ToArray();
        var swordBefore = Shown(view, enemy).WeaponWorld;
        var capsulesBefore = view.Body(Shown(view, enemy)).ToArray();
        var offset = new Vector3(0.3f, 0, 0.4f);
        scene.Pending.Add(Shift(1, offset));
        scene.Frames(1, view, alpha: 0.4f);
        var shifted = Shown(view, enemy);
        var after = view.Pose(shifted);
        float moved = 0;
        for (int j = 0; j < after.Length; j++)
            moved = MathF.Max(moved, Distance(after[j], before[j] * Matrix4x4.CreateTranslation(offset)));
        Assert.True(moved <= 1e-4f, $"скин трупа не ушёл за телами на сдвиг: расхождение {moved}");
        Assert.True(Distance(shifted.WeaponWorld, swordBefore * Matrix4x4.CreateTranslation(offset)) <= 1e-4f, "меч не ушёл с телом клинка");
        var capsules = view.Body(shifted);
        Assert.Equal(BodySegs.Bodies, capsules.Length);
        for (int k = 0; k < capsules.Length; k++)
        {
            Assert.True(Vector3.Distance(capsules[k].Center, capsulesBefore[k].Center + offset) <= 1e-4f, $"капсула {k} трупа не на сдвиге");
            Assert.False(capsules[k].Simulated);   // до 7з тела трупа кинематические
            // рамка трупа — по его телам
            Assert.True(Inside(capsules[k].EndA, shifted) && Inside(capsules[k].EndB, shifted), $"капсула {k} вне рамки трупа");
        }
        Assert.True(scene.Sim.Invariants.Total == 0, string.Join("; ", scene.Sim.Invariants.Recent().Take(3).Select(v => v.ToString())));
    }

    private static bool Inside(Vector3 p, in FighterView f) =>
        p.X >= f.BoundsMin.X - 1e-4f && p.Y >= f.BoundsMin.Y - 1e-4f && p.Z >= f.BoundsMin.Z - 1e-4f
        && p.X <= f.BoundsMax.X + 1e-4f && p.Y <= f.BoundsMax.Y + 1e-4f && p.Z <= f.BoundsMax.Z + 1e-4f;

    /// <summary>
    /// Показ тела трупа (7и): тело на физике — между позой начала тика и позой тика по доле (его прошлую постановку перезаписывает каждый
    /// подшаг — от неё нельзя); кинематическое застывшее — поза тика при любой доле; при доле 1 — поза тика всегда.
    /// </summary>
    [Fact]
    public void Shown_SimulatedBody_FromTickStart_KinematicAtTick()
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.8f);
        var body = new BodySnapshot(new Vector3(1, 2, 3), turn, PrevPosition: new Vector3(9, 9, 9), PrevRotation: Quaternion.Identity, Vector3.Zero,
            Vector3.Zero, 0.1f, 0.2f, StartPosition: new Vector3(1, 1, 3), StartRotation: Quaternion.Identity, 2, Simulated: true, 0, false);
        var (center, rotation) = ArenaView.Shown(body, 0.25f);
        Assert.True(Vector3.Distance(new Vector3(1, 1.25f, 3), center) < 1e-6f);
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.Slerp(Quaternion.Identity, turn, 0.25f), rotation)) > 1 - 1e-6f);
        Assert.Equal((body.Position, body.Rotation), ArenaView.Shown(body, 1));
        var frozen = body with { Simulated = false };
        Assert.Equal((body.Position, body.Rotation), ArenaView.Shown(frozen, 0.25f));
        Assert.Equal((body.Position, body.Rotation), ArenaView.Shown(frozen, 0));
    }

    /// <summary>
    /// Меч на теле клинка (7и): кисть = (тело клинка в кисти)⁻¹ × тело — обратное к постановке тела клинка по кисти (7ж,
    /// <see cref="BladePlacement.BodyOf"/>) для каждого меча и любого поворота кисти (≤ 1e-5).
    /// </summary>
    [Fact]
    public void BladeInHandInverse_UndoesBladePlacement()
    {
        var weapons = TestAssets.Assets.Weapons;
        var rng = new Random(7);
        for (int w = 0; w < weapons.Count; w++)
        {
            var inverse = ArenaView.BladeInHandInverse(weapons.Bodies[w]);
            for (int i = 0; i < 50; i++)
            {
                var q = Quaternion.Normalize(new Quaternion(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
                var hand = Matrix4x4.CreateFromQuaternion(q);
                hand.Translation = new Vector3(rng.NextSingle() * 10 - 5, rng.NextSingle() * 2, rng.NextSingle() * 10 - 5);
                var (position, rotation) = BladePlacement.BodyOf(hand, weapons.Weapons[w], weapons.Bodies[w]);
                Assert.True(Distance(inverse * ArenaView.BodySystem(position, rotation), hand) <= 1e-5f, $"меч {w}, поворот {i}");
            }
        }
    }

    /// <summary>
    /// Суставы тела (7и, оверлей <c>joints</c>): у живого суставов нет; у трупа с суставами (здесь их ставит тест — таблица суставов игры
    /// появится с 7з) — точка сустава на ребёнке и точка на родителе в мире по показанным телам: после привязки по позе совпадают, ошибка
    /// точки и выход — 0; тело ребёнка сдвинуто отдельно — точки разошлись на сдвиг, вид называет ошибку точки; выключенный сустав — так и
    /// показан. Без флага суставов — не снимаются.
    /// </summary>
    [Fact]
    public void Joints_OnCorpseBodies_AnchorsAndErrorFromView()
    {
        var scene = new Scene(enemies: 1);
        var view = new ArenaView(Field);
        scene.Frames(10, view, joints: true);
        Assert.Equal(0, view.JointTotal);   // у живых суставов нет
        var enemy = scene.Fighter(1);
        scene.Pending.Add(Kill(1));
        scene.Frames(2, view);
        var a = FighterBodies.Handle(scene.Sim.World.Get<BodyRef>(enemy));
        var physics = scene.Physics;
        float thighHalf = physics.Get(physics.Body(a, BodySegs.ThighL)).HalfLength, shinHalf = physics.Get(physics.Body(a, BodySegs.ShinL)).HalfLength;
        physics.SetJoints(a,
        [
            JointDesc.SwingTwist(BodySegs.Pelvis, BodySegs.ThighL, new Vector3(0, thighHalf, 0), -1, 1, 0.5f, 0.3f),
            JointDesc.Hinge(BodySegs.ThighL, BodySegs.ShinL, new Vector3(0, shinHalf, 0), -3, 3, 0.1f, 1),
        ]);
        physics.SetJointEnabled(a, 1, false);
        view.Extract(scene.Clock.Link, 0.5f, allBodies: false, joints: true);
        var f = Shown(view, enemy);
        var joints = view.Joints(f);
        Assert.Equal(2, joints.Length);
        Assert.Equal(2, view.JointTotal);
        Assert.Equal((BodySegs.Pelvis, BodySegs.ThighL, false, true), (joints[0].Parent, joints[0].Child, joints[0].Hinge, joints[0].Enabled));
        Assert.Equal((true, false), (joints[1].Hinge, joints[1].Enabled));
        var capsules = view.Body(f);   // суставы снимаются вместе с капсулами, на которых стоят
        Assert.Equal(BodySegs.Bodies, capsules.Length);
        var thigh = capsules[BodySegs.Body(BodySegs.ThighL)];
        foreach (var j in joints)
        {
            Assert.True(Vector3.Distance(j.Point, j.ParentPoint) < 1e-4f && j.PointError < 1e-4f && j.Excess < 1e-4f, $"{j}: после привязки точка разошлась");
        }
        Assert.True(Vector3.Distance(joints[0].Point, thigh.EndB) < 1e-4f, "точка бедра — верхний конец его капсулы");

        // Тело голени — отдельно на 5 см: точка сустава колена на ребёнке ушла, на родителе — нет.
        var shin = physics.Body(a, BodySegs.ShinL);
        var s = physics.Get(shin);
        physics.Place(shin, s.Position + new Vector3(0.05f, 0, 0), s.Rotation);
        view.Extract(scene.Clock.Link, 1, allBodies: false, joints: true);
        var knee = view.Joints(Shown(view, enemy))[1];
        Assert.Equal(0.05f, Vector3.Distance(knee.Point, knee.ParentPoint), 4);
        Assert.Equal(0.05f, knee.PointError, 4);
        view.Extract(scene.Clock.Link, 1, allBodies: false);
        Assert.Equal((0, 0), (view.JointTotal, Shown(view, enemy).BodyJointCount));
    }

    /// <summary>
    /// Трасса физики — диагностический режим <c>physics.trace</c> (7и): включают его владелец и агент между тиками, мир и хеш от него не
    /// зависят (хеши 60 тиков с режимом и без — те же); вид отладки физики — выключено, пусто, после включения — трасса идёт (шагов столько,
    /// сколько тиков со Step), контакты последнего подшага — в сочленение и номер тела, мир — пустое сочленение; выключили — снова пусто.
    /// Шага физики в игре до 7з нет — контакты даёт шаг мира в тесте (тело трупа на физике над землёй арены).
    /// </summary>
    [Fact]
    public void TraceMode_ContactsThroughView_DoesNotChangeHash()
    {
        var plain = new Scene(enemies: 1);
        var traced = new Scene(enemies: 1);
        Assert.True(traced.Sim.Diagnostics.Has(IPhysicsDebugView.TraceMode));
        var info = Enumerable.Range(0, traced.Sim.Diagnostics.Count).Select(i => traced.Sim.Diagnostics[i]).Single(d => d.Id == IPhysicsDebugView.TraceMode);
        Assert.Equal(("core", false), (info.Owner, info.Enabled));
        var debug = traced.Sim.World.View<IPhysicsDebugView>();
        Assert.False(debug.TraceOn);
        Assert.True(debug.Contacts.IsEmpty);
        Assert.Equal(default, debug.TraceSummary(PhysicsTracePeriod.Run));
        traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, true);
        Assert.True(debug.TraceOn && traced.Sim.Diagnostics.IsEnabled(IPhysicsDebugView.TraceMode));
        var view = new ArenaView(Field);
        for (int i = 0; i < 60; i++)
        {
            plain.Clock.RunFrame(0);
            traced.Clock.RunFrame(0);
            Assert.Equal(plain.Sim.HashState().Total, traced.Sim.HashState().Total);
        }
        Assert.Throws<ArgumentException>(() => traced.Sim.Diagnostics.Set("physics.nope", true));

        // Тело трупа на физике над землёй в центре арены и шаг мира — контакт с землёй в трассе.
        var enemy = traced.Fighter(1);
        traced.Pending.Add(Kill(1));
        traced.Clock.RunFrame(0);
        var physics = traced.Physics;
        physics.SetGround(ArenaGround.Instance);
        var a = FighterBodies.Handle(traced.Sim.World.Get<BodyRef>(enemy));
        var foot = physics.Body(a, BodySegs.FootL);
        var state = physics.Get(foot);
        physics.Place(foot, new Vector3(0.5f, state.Radius * 0.5f, 0.5f), Quaternion.Identity);
        physics.SetMode(foot, BodyMode.Simulated);
        physics.Step(traced.Sim.Dt);
        view.Extract(traced.Clock.Link, 1, contacts: true);
        Assert.True(view.TraceOn);
        var contacts = view.Contacts;
        Assert.NotEmpty(contacts.ToArray());
        var ground = contacts.ToArray().First(c => c.Source == PhysicsContactSource.Ground);
        Assert.Equal((a.Index, a.Generation, BodySegs.Body(BodySegs.FootL)), (ground.Body.Index, ground.Body.Generation, ground.BodyIndex));
        Assert.Equal((0, -1), (ground.Other.Generation, ground.OtherIndex));   // мир
        Assert.True(ground.Normal.Y > 0.99f && ground.Depth > 0, $"{ground}");
        var run = debug.TraceSummary(PhysicsTracePeriod.Run);
        Assert.Equal(1, run.Steps);
        Assert.True(debug.TraceSource(PhysicsTracePeriod.Run, PhysicsContactSource.Ground).Contacts > 0);
        view.Extract(traced.Clock.Link, 1);   // контакты не заказаны — не снимаются
        Assert.Equal(0, view.ContactCount);

        traced.Sim.Diagnostics.ResetWindow(IPhysicsDebugView.TraceMode);
        Assert.Equal(0, debug.TraceSummary(PhysicsTracePeriod.Window).Steps);
        Assert.Equal(1, debug.TraceSummary(PhysicsTracePeriod.Run).Steps);
        traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, false);
        view.Extract(traced.Clock.Link, 1, contacts: true);
        Assert.False(view.TraceOn);
        Assert.Equal(0, view.ContactCount);
    }

    /// <summary>
    /// 0 Б на кадр (7и): мир представления 32 трупов и игрока — скин по телам, мечи на телах клинков, капсулы, суставы и контакты по флагам
    /// — без выделений в установившемся кадре.
    /// </summary>
    [Fact]
    public void Extract_32Corpses_AllOverlays_AllocatesNothing()
    {
        var scene = new Scene(enemies: 32);
        var view = new ArenaView(Field);
        scene.Frames(5, view);
        for (int slot = 1; slot <= 32; slot++) scene.Pending.Add(Kill(slot));
        scene.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, true);
        scene.Frames(5, view, alpha: 0.5f, joints: true, contacts: true);
        long bytes = 0;
        for (int i = 0; i < 30; i++)
        {
            scene.Clock.RunFrame(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            view.Extract(scene.Clock.Link, 0.5f, allBodies: true, blades: true, joints: true, contacts: true);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(32, view.CorpseCount);
        Assert.Equal(33 * BodySegs.Bodies, view.BodyCount);
        Assert.Equal(0, bytes);
    }

    /// <summary>
    /// Оверлеи клиента (7и: + <c>contacts</c>, <c>joints</c>) — имена годятся для строгого разбора видов манифеста (строчные латинские,
    /// цифры, «-»), вид манифеста с ними разбирается, а каждый оверлей в видах и бюджетах манифестов фич игры клиент знает (иначе
    /// <c>Sword.exe --overlay</c> отказал бы на снимке вида).
    /// </summary>
    [Fact]
    public void Overlays_KnownToClient_ParseInManifestViews()
    {
        Assert.Equal(["capsules", "blades", "contacts", "joints"], ArenaClient.KnownOverlays);
        Assert.All(ArenaClient.KnownOverlays, o => Assert.Matches("^[a-z0-9-]+$", o));
        foreach (var m in FeatureManifestsTests.All())
        {
            foreach (var v in m.Views) Assert.All(v.Overlays, o => Assert.Contains(o, ArenaClient.KnownOverlays));
            foreach (var b in m.Budgets)
                if (b.Frame is { } frame) Assert.All(frame.Overlays, o => Assert.Contains(o, ArenaClient.KnownOverlays));
        }
        string dir = Path.Combine(Path.GetTempPath(), $"sword-overlays-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "# x");
            string manifest = Path.Combine(dir, "feature.json");
            File.WriteAllText(manifest, """
                {
                  "format": "sword-feature", "version": 1, "id": "corpse", "title": "труп", "doc": "README.md", "sourceRoots": ["."],
                  "scenarios": [{ "id": "s", "ticks": 10 }],
                  "views": [{ "id": "v", "scenario": "s", "tick": 5, "camera": "0.6,0.95,24", "overlays": ["capsules", "contacts", "joints"] }],
                  "checklist": ["посмотреть"]
                }
                """);
            var parsed = FeatureManifest.Load(manifest);
            Assert.Equal(["capsules", "contacts", "joints"], parsed.Views[0].Overlays);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
