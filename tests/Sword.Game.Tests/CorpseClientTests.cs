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
using Xunit.Abstractions;

namespace Sword.Game.Tests;

/// <summary>
/// Точка 7и этапа 7 — труп на экране без окна и GPU (свои тесты свойств); с 7и, часть 2 — на настоящих трупах (7з: труп падает на физике).
/// Скин трупа — сустав = привязка × показанное тело: в начале тика смерти — ровно поза смерти (≤ 0,1 мм), дальше — за падающими телами при
/// любой доле тика, между кадрами без скачков (поза начала тика → поза тика; конец тика — начало следующего); «сдвинуть тела» движущегося
/// трупа — скин, меч и капсулы на вектор сдвига; меч — на теле клинка, выпавший лежит отдельно от кисти; рамка по телам; капсулы трупа — на
/// физике, касание земли; суставы тела (оверлей <c>joints</c>) — 13, запястье выключено, ошибка точки мала; контакты последнего подшага
/// (оверлей <c>contacts</c>, режим <c>physics.trace</c>) — земля под лежащим трупом; хеш с трассой и без — один на прогоне с трупами
/// (≥ 300 тиков после смерти); строки трупов кадра (F3 «Рэгдолл»); 0 Б на кадр с 32 трупами; оверлеи клиента — в строгом разборе видов
/// манифестов. Контент — синтетический (<see cref="TestAssets"/>).
/// </summary>
public sealed class CorpseClientTests(ITestOutputHelper output)
{
    private static readonly Func<float, float, float> Field = ArenaTerrain.Height;

    /// <summary>Модуль теста: берёт физику симуляции после сборки мира.</summary>
    private sealed class Probe(Action<IPhysicsWorld> take) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b) => b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>()));
    }

    /// <summary>
    /// Симуляция арены с командами сцен из очереди теста (каждый кадр — один тик) и физикой симуляции; враги не нападают (игрок стоит — за
    /// сотни тиков живые враги его не убьют, трупы — только те, что убил тест).
    /// </summary>
    /// <summary>Сон трупа выключен (7з-3: порог 0): спящий труп шаг не считает — контактов с землёй в трассе у него нет.</summary>
    private const string NoSleep = "ragdoll.sleepSpeed=0";

    private sealed class Scene
    {
        public readonly Simulation Sim;
        public readonly IPhysicsWorld Physics;
        public readonly SimulationClock Clock;
        public readonly List<SceneCommand> Pending = [];

        /// <param name="sets">Ещё ключи конфига (7з-3: <see cref="NoSleep"/> — труп не засыпает, и контакты лежащего видны в трассе).</param>
        public Scene(int enemies, params string[] sets)
        {
            IPhysicsWorld? physics = null;
            Sim = Simulation.Build(ConfigSet.Empty([$"arena.enemies={enemies}", "combat.enemiesAttack=false", .. sets]), GameStages.All,
                b => GameCore.Configure(b, TestAssets.Assets), [.. Bootstrap.SwordSimulation.Modules(), new Probe(p => physics = p)]);
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

        public void Clean() => Assert.True(Sim.Invariants.Total == 0,
            string.Join("; ", Sim.Invariants.Recent().Take(3).Select(v => $"{v.Id} тик {v.Tick}: {v.Details}")));
    }

    private static SceneCommand Kill(int slot) => new() { Action = SceneAction.Kill, Slot = slot };

    private static SceneCommand Shift(int slot, Vector3 offset) => new() { Action = SceneAction.ShiftBodies, Slot = slot, Offset = offset };

    private static FighterView Shown(ArenaView view, Entity e) => view.Fighters.Single(f => f.Entity == e);

    /// <summary>
    /// Сустав позы смерти в мире: модельная матрица пула × мир бойца (как снимок привязки, 7ж). 7з: корень мёртвого идёт за тазом трупа —
    /// корень смерти тест снимает до тика смерти (в тике смерти корень мёртвого не рулится).
    /// </summary>
    private static Matrix4x4[] DeathPose(Simulation sim, Entity e, Transform root)
    {
        var model = sim.World.View<IAnimationView>().Model(sim.World.Get<PoseRef>(e));
        var world = BodyPlacement.World(root.Position, root.Facing);
        var pose = new Matrix4x4[model.Length];
        for (int j = 0; j < pose.Length; j++) pose[j] = model[j] * world;
        return pose;
    }

    /// <summary>Тело трупа по доле тика — независимо от клиента, по виду тел: центр — от позы начала тика линейно, поворот — Slerp.</summary>
    private static Matrix4x4 BodyAt(in BodySnapshot b, float alpha) => ArenaView.BodySystem(Vector3.Lerp(b.StartPosition, b.Position, alpha),
        Quaternion.Normalize(Quaternion.Slerp(b.StartRotation, b.Rotation, alpha)));

    /// <summary>Ожидаемый скин трупа по видам симуляции (привязка × тело сегмента по доле тика) и ожидаемый мир меча (на теле клинка).</summary>
    private static (Matrix4x4[] Joints, Matrix4x4 Sword) Expected(Simulation sim, Entity e, float alpha)
    {
        var body = sim.World.Get<BodyRef>(e);
        var bodies = sim.World.View<IBodyView>();
        Assert.True(sim.World.View<ISkinView>().TryGet(body, out var binding, out _), "у мёртвого нет привязки скина");
        var joints = new Matrix4x4[binding.Length];
        for (int j = 0; j < joints.Length; j++)
            joints[j] = binding[j] * BodyAt(bodies.Body(body, BodySegs.Body(bodies.JointSegment(j))), alpha);
        var weapon = sim.World.View<ICombatView>().Weapons[sim.World.Get<WeaponSlot>(e).Index];
        var sword = ArenaView.BladeInHandInverse(weapon.Body) * BodyAt(bodies.Body(body, BodySegs.Body(BodySegs.Weapon)), alpha);
        return (joints, sword);
    }

    /// <summary>Наибольшее расхождение двух матриц: перенос (м) и оси (длины разности базисных векторов).</summary>
    private static float Distance(in Matrix4x4 a, in Matrix4x4 b)
    {
        float d = Vector3.Distance(a.Translation, b.Translation);
        foreach (var axis in (Vector3[])[Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ])
            d = MathF.Max(d, Vector3.Distance(Vector3.TransformNormal(axis, a), Vector3.TransformNormal(axis, b)));
        return d;
    }

    private static float Worst(ReadOnlySpan<Matrix4x4> a, ReadOnlySpan<Matrix4x4> b)
    {
        Assert.Equal(a.Length, b.Length);
        float worst = 0;
        for (int j = 0; j < a.Length; j++) worst = MathF.Max(worst, Distance(a[j], b[j]));
        return worst;
    }

    /// <summary>Наибольший путь сустава (перенос, м) между двумя позами.</summary>
    private static float Moved(ReadOnlySpan<Matrix4x4> a, ReadOnlySpan<Matrix4x4> b)
    {
        float worst = 0;
        for (int j = 0; j < a.Length; j++) worst = MathF.Max(worst, Vector3.Distance(a[j].Translation, b[j].Translation));
        return worst;
    }

    /// <summary>
    /// Скин падающего трупа (7и; с части 2 — настоящий труп 7з): сустав = привязка × тело его сегмента. В тике смерти командой переход в
    /// труп — до шага, поэтому поза начала тика — поза смерти: при доле 0 скин — ровно поза смерти (≤ 0,1 мм и по осям), меч — там же, где
    /// в кисти позы смерти; при любой доле скин и меч — привязка и тело клинка по виду тел (≤ 0,05 мм); за 60 тиков труп падает. «Сдвинуть
    /// тела» движущегося трупа — при доле 0 тика сдвига суставы, меч и капсулы — ровно на вектор от конца прошлого тика, капсулы — на физике
    /// и в рамке трупа. Живой — как раньше (поза пула, не труп).
    /// </summary>
    [Fact]
    public void CorpseSkin_Falling_DeathPoseAtTickStart_FollowsBodies_ShiftOfMovingCorpse()
    {
        var scene = new Scene(enemies: 2);
        var view = new ArenaView(Field);
        scene.Frames(20, view);
        var enemy = scene.Fighter(1);
        var root = scene.Sim.World.Get<Transform>(enemy);
        scene.Pending.Add(Kill(1));
        scene.Frames(1, view);   // тик смерти: привязка и переход в труп (Gameplay), шаг физики
        var death = DeathPose(scene.Sim, enemy, root);
        int hand = scene.Sim.World.View<ICombatView>().HandJoint;

        view.Extract(scene.Clock.Link, 0);
        var f = Shown(view, enemy);
        Assert.True(f.Dead && f.Corpse, "мёртвый с привязкой — труп по телам");
        float atStart = Worst(view.Pose(f), death);
        Assert.True(atStart <= 1e-4f, $"в начале тика смерти скин ушёл от позы смерти на {atStart}");
        Assert.True(Distance(f.WeaponWorld, death[hand]) <= 1e-4f, "меч на теле клинка не там, где был в кисти");
        foreach (float alpha in (float[])[0.3f, 1f])
        {
            view.Extract(scene.Clock.Link, alpha);
            f = Shown(view, enemy);
            var (joints, sword) = Expected(scene.Sim, enemy, alpha);
            Assert.True(Worst(view.Pose(f), joints) <= 5e-5f, $"доля {alpha}: скин трупа — не привязка × тело");
            Assert.True(Distance(f.WeaponWorld, sword) <= 5e-5f, $"доля {alpha}: меч — не на теле клинка");
            Assert.False(Shown(view, scene.Fighter(2)).Corpse);   // живой — поза пула
            Assert.False(view.Player!.Value.Corpse);
        }

        scene.Frames(60, view);
        f = Shown(view, enemy);
        float fell = Moved(view.Pose(f), death);
        output.WriteLine($"за 60 тиков скин трупа ушёл от позы смерти на {fell:F3} м");
        Assert.True(fell > 0.05f, $"труп не падает: {fell}");
        var (fallen, fallenSword) = Expected(scene.Sim, enemy, 1);
        Assert.True(Worst(view.Pose(f), fallen) <= 5e-5f && Distance(f.WeaponWorld, fallenSword) <= 5e-5f, "лёг — скин и меч не по телам");
        Assert.Equal(1, view.CorpseCount);

        // Сдвиг движущегося трупа: поза начала тика сдвига — конец прошлого тика + сдвиг (Place сдвигает и позу начала тика).
        var before = view.Pose(f).ToArray();
        var swordBefore = f.WeaponWorld;
        var capsulesBefore = view.Body(f).ToArray();
        var offset = new Vector3(0.3f, 0, 0.4f);
        scene.Pending.Add(Shift(1, offset));
        scene.Frames(1, view, alpha: 0);
        var shifted = Shown(view, enemy);
        var move = Matrix4x4.CreateTranslation(offset);
        float moved = 0;
        var after = view.Pose(shifted);
        for (int j = 0; j < after.Length; j++) moved = MathF.Max(moved, Distance(after[j], before[j] * move));
        Assert.True(moved <= 1e-4f, $"скин трупа не ушёл за телами на сдвиг: расхождение {moved}");
        Assert.True(Distance(shifted.WeaponWorld, swordBefore * move) <= 1e-4f, "меч не ушёл с телом клинка");
        var capsules = view.Body(shifted);
        Assert.Equal(BodySegs.Bodies, capsules.Length);
        for (int k = 0; k < capsules.Length; k++)
        {
            Assert.True(Vector3.Distance(capsules[k].Center, capsulesBefore[k].Center + offset) <= 1e-4f, $"капсула {k} трупа не на сдвиге");
            Assert.True(capsules[k].Simulated);   // 7з: тела трупа — на физике
            Assert.True(Inside(capsules[k].EndA, shifted) && Inside(capsules[k].EndB, shifted), $"капсула {k} вне рамки трупа");
        }
        scene.Clean();
    }

    private static bool Inside(Vector3 p, in FighterView f) =>
        p.X >= f.BoundsMin.X - 1e-4f && p.Y >= f.BoundsMin.Y - 1e-4f && p.Z >= f.BoundsMin.Z - 1e-4f
        && p.X <= f.BoundsMax.X + 1e-4f && p.Y <= f.BoundsMax.Y + 1e-4f && p.Z <= f.BoundsMax.Z + 1e-4f;

    /// <summary>
    /// Скин трупа между кадрами без скачков (7и, часть 2): кадры по четверти тика (доли 0,25; 0,5; 0,75; 1) — скин падающего трупа идёт от позы
    /// начала тика к позе тика; начало тика — ровно конец прошлого (≤ 0,01 мм: поза начала тика — поза конца прошлого), шаг кадра — не больше
    /// 0,35 пути тика (линейно по центру, Slerp по повороту). И переход живой → труп в тике смерти командой без скачка: живой при доле 1 до
    /// смерти (поза пула в мире корня тика) — это поза смерти, с неё начинается труп. Без интерполяции (доля 1 в каждом кадре) шаг кадра был
    /// бы путём тика.
    /// </summary>
    [Fact]
    public void CorpseSkin_BetweenFrames_NoJumps_TickStartToTick()
    {
        var scene = new Scene(enemies: 1);
        var view = new ArenaView(Field);
        scene.Frames(20, view);
        var enemy = scene.Fighter(1);
        // Живой при доле 1 — поза пула тика в мире корня тика: так он показан в последнем кадре перед смертью.
        var live = Shown(view, enemy);
        var world = live.World;
        var last = view.Pose(live).ToArray();
        for (int j = 0; j < last.Length; j++) last[j] *= world;
        scene.Pending.Add(Kill(1));
        float path = 0, worstJump = 0, worstRatio = 0;
        for (int tick = 0; tick < 90; tick++)
        {
            scene.Clock.RunFrame(0);
            view.Extract(scene.Clock.Link, 0);
            var start = view.Pose(Shown(view, enemy)).ToArray();
            float jump = Worst(start, last);
            worstJump = MathF.Max(worstJump, jump);
            Assert.True(jump <= (tick == 0 ? 1e-4f : 1e-5f), $"тик {tick} после смерти: начало тика — не конец прошлого, скачок {jump}");
            view.Extract(scene.Clock.Link, 1);
            var end = view.Pose(Shown(view, enemy)).ToArray();
            float full = Moved(start, end);
            path += full;
            var prev = start;
            foreach (float alpha in (float[])[0.25f, 0.5f, 0.75f, 1f])
            {
                view.Extract(scene.Clock.Link, alpha);
                var now = view.Pose(Shown(view, enemy)).ToArray();
                float step = Moved(prev, now);
                if (full > 1e-4f) worstRatio = MathF.Max(worstRatio, step / full);
                Assert.True(step <= 0.35f * full + 1e-5f, $"тик {tick}, доля {alpha}: шаг кадра {step} при пути тика {full}");
                prev = now;
            }
            last = end;
        }
        output.WriteLine($"путь суставов за 90 тиков {path:F3} м; скачок на границе тиков ≤ {worstJump:E1}; шаг кадра ≤ {worstRatio:F3} пути тика");
        Assert.True(path > 0.1f, $"труп не падает: путь {path}");
        scene.Clean();
    }

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
    /// Суставы трупа (7и, оверлей <c>joints</c>; с части 2 — суставы перехода в труп 7з): у живых суставов нет; у трупа — 13, запястье (ребёнок
    /// — тело клинка) выключено, прочие включены; точки на ребёнке и на родителе — в мире по показанным телам: в начале тика смерти (поза
    /// смерти, по ней привязаны) совпадают, у падающего и лежащего трупа — расходятся не больше чем на сантиметр (ошибка точки из вида — та
    /// же); голень сдвинута отдельно на 5 см — точка колена на ребёнке ушла, на родителе — нет, вид называет ошибку точки. Без флага
    /// суставов — не снимаются.
    /// </summary>
    [Fact]
    public void Joints_OfRealCorpse_WristDisabled_AnchorsFromView_ErrorAfterShinShift()
    {
        var scene = new Scene(enemies: 1);
        var view = new ArenaView(Field);
        scene.Frames(10, view, joints: true);
        Assert.Equal(0, view.JointTotal);   // у живых суставов нет
        var enemy = scene.Fighter(1);
        scene.Pending.Add(Kill(1));
        scene.Frames(1, view);
        view.Extract(scene.Clock.Link, 0, allBodies: false, joints: true);
        var f = Shown(view, enemy);
        var joints = view.Joints(f).ToArray();
        Assert.Equal(13, joints.Length);
        Assert.Equal(13, view.JointTotal);
        Assert.Equal(BodySegs.Bodies, view.Body(f).Length);   // суставы снимаются вместе с капсулами, на которых стоят
        var wrist = Assert.Single(joints, j => j.Child == BodySegs.Body(BodySegs.Weapon));
        Assert.Equal((BodySegs.Body(BodySegs.FArmR), false), (wrist.Parent, wrist.Enabled));   // меч выпал
        Assert.All(joints.Where(j => j.Child != BodySegs.Body(BodySegs.Weapon)), j => Assert.True(j.Enabled, $"{j}"));
        Assert.Equal(4, joints.Count(j => j.Hinge));   // колени и локти — шарниры
        foreach (var j in joints)
            Assert.True(Vector3.Distance(j.Point, j.ParentPoint) < 1e-4f, $"{j}: в позе смерти (начало тика смерти) точка разошлась");

        float worst = 0, worstError = 0;
        for (int t = 0; t < 360; t++)
        {
            scene.Frames(1, view, joints: true);
            foreach (var j in view.Joints(Shown(view, enemy)))
            {
                if (!j.Enabled) continue;
                worst = MathF.Max(worst, Vector3.Distance(j.Point, j.ParentPoint));
                worstError = MathF.Max(worstError, j.PointError);
            }
        }
        output.WriteLine($"суставы падающего и лежащего трупа: точки разошлись ≤ {worst * 1000:F2} мм, ошибка точки вида ≤ {worstError * 1000:F2} мм");
        Assert.True(worst <= 0.01f && worstError <= 0.01f, $"точки сустава разошлись на {worst} (вид — {worstError})");

        // Голень — отдельно на 5 см: точка колена на ребёнке ушла, на родителе — нет.
        var a = FighterBodies.Handle(scene.Sim.World.Get<BodyRef>(enemy));
        var physics = scene.Physics;
        var shin = physics.Body(a, BodySegs.ShinL);
        var s = physics.Get(shin);
        view.Extract(scene.Clock.Link, 1, allBodies: false, joints: true);
        var knee0 = Assert.Single(view.Joints(Shown(view, enemy)).ToArray(), j => j.Child == BodySegs.Body(BodySegs.ShinL));
        Assert.Equal((BodySegs.Body(BodySegs.ThighL), true), (knee0.Parent, knee0.Hinge));
        physics.Place(shin, s.Position + new Vector3(0.05f, 0, 0), s.Rotation);
        view.Extract(scene.Clock.Link, 1, allBodies: false, joints: true);
        var knee = Assert.Single(view.Joints(Shown(view, enemy)).ToArray(), j => j.Child == BodySegs.Body(BodySegs.ShinL));
        Assert.True(Vector3.Distance(knee.Point, knee0.Point + new Vector3(0.05f, 0, 0)) < 1e-4f, "точка на голени не ушла с ней");
        Assert.True(Vector3.Distance(knee.ParentPoint, knee0.ParentPoint) < 1e-5f, "точка на бедре ушла");
        Assert.InRange(knee.PointError, 0.04f, 0.06f);
        Assert.Equal(Vector3.Distance(knee.Point, knee.ParentPoint), knee.PointError, 3);
        view.Extract(scene.Clock.Link, 1, allBodies: false);
        Assert.Equal((0, 0), (view.JointTotal, Shown(view, enemy).BodyJointCount));
    }

    /// <summary>
    /// Лежащий труп (7и, часть 2; ≥ 300 тиков после смерти, трасса физики включена): капсулы трупа — все на физике (крест), часть касается
    /// земли; оверлей <c>contacts</c> с данными — контакты земли с телами трупа (нормаль вверх, глубина — миллиметры); оверлей <c>joints</c> —
    /// 13 суставов; выпавший меч лежит отдельно от кисти: мир меча — по телу клинка (≤ 0,05 мм), от сустава кисти скина — дальше 10 см, клинок
    /// у земли; строки трупов кадра (F3 «Рэгдолл») — 1 труп, 14 тел на физике, на земле — сколько капсул касается земли.
    /// </summary>
    [Fact]
    public void LyingCorpse_GroundContacts_Joints_Crosses_SwordApartFromHand()
    {
        var scene = new Scene(enemies: 2, NoSleep);   // 7з-3: лежащий уснул бы — спящего шаг не считает, контактов в трассе нет (сон — тест ниже)
        var view = new ArenaView(Field);
        scene.Frames(10, view);
        var enemy = scene.Fighter(1);
        scene.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, true);
        scene.Pending.Add(Kill(1));
        scene.Frames(360, view, joints: true, contacts: true);
        var f = Shown(view, enemy);
        Assert.True(f.Corpse);
        var body = scene.Sim.World.Get<BodyRef>(enemy);

        var capsules = view.Body(f).ToArray();
        Assert.Equal(BodySegs.Bodies, capsules.Length);
        Assert.All(capsules, c => Assert.True(c.Simulated));
        int grounded = capsules.Count(c => c.Grounded);
        output.WriteLine($"капсул трупа на земле: {grounded} из {capsules.Length}");
        Assert.InRange(grounded, 3, BodySegs.Bodies);

        Assert.True(view.TraceOn);
        var ground = view.Contacts.ToArray().Where(c => c.Source == PhysicsContactSource.Ground && c.Body.Index == body.Index
            && c.Body.Generation == body.Generation).ToArray();
        output.WriteLine($"контактов с землёй у трупа: {ground.Length}; глубина ≤ {ground.Max(c => c.Depth) * 1000:F2} мм");
        Assert.True(ground.Length >= 3, $"контактов трупа с землёй {ground.Length}");
        Assert.All(ground, c =>
        {
            Assert.True(c.Normal.Y > 0.9f && c.Depth >= 0 && c.Depth < 0.01f, $"{c}");
            Assert.InRange(c.BodyIndex, 0, BodySegs.Bodies - 1);
            Assert.True(capsules[c.BodyIndex].Grounded, $"контакт с землёй у тела {c.BodyIndex}, а касания нет");
        });

        var joints = view.Joints(f).ToArray();
        Assert.Equal(13, joints.Length);
        Assert.Equal(13, view.JointTotal);

        int hand = scene.Sim.World.View<ICombatView>().HandJoint;
        var (_, sword) = Expected(scene.Sim, enemy, 1);
        Assert.True(Distance(f.WeaponWorld, sword) <= 5e-5f, "меч трупа — не на теле клинка");
        float apart = Vector3.Distance(f.WeaponWorld.Translation, view.Pose(f)[hand].Translation);
        var blade = capsules[BodySegs.Body(BodySegs.Weapon)];
        float lift = MathF.Max(blade.EndA.Y - Field(blade.EndA.X, blade.EndA.Z), blade.EndB.Y - Field(blade.EndB.X, blade.EndB.Z));
        output.WriteLine($"меч от кисти скина: {apart:F3} м; концы клинка над землёй ≤ {lift:F3} м (радиус {blade.Radius})");
        Assert.True(apart > 0.1f, $"меч не выпал: {apart} м от кисти");
        Assert.True(lift < 0.25f, $"клинок не у земли: {lift} м");

        var stats = view.CorpseStats;
        output.WriteLine($"трупы кадра: {stats}");
        Assert.Equal((1, BodySegs.Bodies, grounded), (stats.Corpses, stats.SimulatedBodies, stats.GroundedBodies));
        float fastest = Enumerable.Range(0, BodySegs.Bodies).Max(k => scene.Sim.World.View<IBodyView>().Body(body, k).Velocity.Length());
        Assert.Equal(fastest, stats.MaxSpeed, 5);
        scene.Clean();
    }

    /// <summary>
    /// 7з-3: лежащий труп засыпает — все капсулы трупа на физике и «спят» (F7: крест синий, <see cref="ArenaClient.AsleepMark"/>), строка
    /// «трупов» F3 — «1, в покое 1, спят 1»; показанное тело спящего при любой доле тика — поза тика (поза начала тика = поза: не качается).
    /// </summary>
    [Fact]
    public void SleepingCorpse_CapsulesAsleep_F3Row_ShownPoseSameAtAnyAlpha()
    {
        var scene = new Scene(enemies: 1);
        var view = new ArenaView(Field);
        scene.Frames(10, view);
        var enemy = scene.Fighter(1);
        scene.Pending.Add(Kill(1));
        scene.Frames(600, view);
        var body = FighterBodies.Handle(scene.Sim.World.Get<BodyRef>(enemy));
        Assert.True(scene.Physics.IsAsleep(body), $"труп не уснул за 5 с (покой {scene.Physics.RestTime(body)} с)");
        var f = Shown(view, enemy);
        var at1 = view.Body(f).ToArray();
        Assert.All(at1, c => Assert.True(c.Simulated && c.Asleep, $"{c}"));
        Assert.Equal((1, 1), (view.CorpseStats.Corpses, view.CorpseStats.Asleep));
        Span<char> d = stackalloc char[Sword.Engine.DevTools.TuningPanel.InfoCapacity];
        Assert.Equal("1, в покое 1, спят 1", d[..RagdollTuning.Corpses(d, view.CorpseStats)].ToString());
        view.Extract(scene.Clock.Link, 0.3f);
        var at03 = view.Body(Shown(view, enemy)).ToArray();
        for (int k = 0; k < at1.Length; k++) Assert.Equal((at1[k].Center, at1[k].Rotation), (at03[k].Center, at03[k].Rotation));
        scene.Clean();
    }

    /// <summary>
    /// Трасса физики — диагностический режим <c>physics.trace</c> (7и): включают его владелец и агент между тиками, мир и хеш от него не
    /// зависят. С части 2 — на прогоне с трупами на физике: три врага убиты на тике 20, 400 тиков после смерти (падение, куча, покой) хеши
    /// с режимом и без — те же каждый тик, и когда режим посреди прогона сбрасывают, выключают и включают снова. Вид отладки физики —
    /// выключено, пусто; включено — трасса идёт (шагов столько, сколько тиков с режимом), контакты последнего подшага — в сочленение и номер
    /// тела, мир — пустое сочленение; выключили — снова пусто.
    /// </summary>
    [Fact]
    public void TraceMode_CorpsesOnPhysics_SameHashes_ContactsThroughView()
    {
        var plain = new Scene(enemies: 3, NoSleep);    // 7з-3: контакты лежащих — только у неспящих
        var traced = new Scene(enemies: 3, NoSleep);
        Assert.True(traced.Sim.Diagnostics.Has(IPhysicsDebugView.TraceMode));
        var info = Enumerable.Range(0, traced.Sim.Diagnostics.Count).Select(i => traced.Sim.Diagnostics[i]).Single(d => d.Id == IPhysicsDebugView.TraceMode);
        Assert.Equal(("core", false), (info.Owner, info.Enabled));
        var debug = traced.Sim.World.View<IPhysicsDebugView>();
        Assert.False(debug.TraceOn);
        Assert.True(debug.Contacts.IsEmpty);
        Assert.Equal(default, debug.TraceSummary(PhysicsTracePeriod.Run));
        traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, true);
        Assert.True(debug.TraceOn && traced.Sim.Diagnostics.IsEnabled(IPhysicsDebugView.TraceMode));
        Assert.Throws<ArgumentException>(() => traced.Sim.Diagnostics.Set("physics.nope", true));
        var view = new ArenaView(Field);

        const int death = 20, ticks = death + 400, off = 250, on = 262;
        for (int t = 0; t < ticks; t++)
        {
            if (t == death)
                foreach (var scene in (Scene[])[plain, traced])
                    scene.Pending.AddRange([Kill(1), Kill(2), Kill(3)]);
            if (t == 200) traced.Sim.Diagnostics.ResetWindow(IPhysicsDebugView.TraceMode);
            if (t == off) traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, false);
            if (t == on) traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, true);
            plain.Clock.RunFrame(0);
            traced.Clock.RunFrame(0);
            Assert.True(plain.Sim.HashState().Total == traced.Sim.HashState().Total, $"тик {t}: хеш с трассой и без разошёлся");
        }
        Assert.All((int[])[1, 2, 3], slot => Assert.True(traced.Sim.World.Has<Dead>(traced.Fighter(slot))));

        // Контакты лежащих трупов с землёй — в трассе и в виде.
        view.Extract(traced.Clock.Link, 1, contacts: true);
        Assert.True(view.TraceOn);
        var contacts = view.Contacts.ToArray();
        Assert.NotEmpty(contacts);
        var a = FighterBodies.Handle(traced.Sim.World.Get<BodyRef>(traced.Fighter(1)));
        var ground = contacts.First(c => c.Source == PhysicsContactSource.Ground && c.Body.Index == a.Index);
        Assert.Equal(a.Generation, ground.Body.Generation);
        Assert.InRange(ground.BodyIndex, 0, BodySegs.Bodies - 1);
        Assert.Equal((0, -1), (ground.Other.Generation, ground.OtherIndex));   // мир
        Assert.True(ground.Normal.Y > 0.9f && ground.Depth >= 0, $"{ground}");   // земля арены — с уклоном
        var run = debug.TraceSummary(PhysicsTracePeriod.Run);
        Assert.Equal(ticks - on, run.Steps);   // включили снова — накопление заново: шагов столько, сколько тиков с режимом
        Assert.True(debug.TraceSource(PhysicsTracePeriod.Run, PhysicsContactSource.Ground).Contacts > 0);
        view.Extract(traced.Clock.Link, 1);   // контакты не заказаны — не снимаются
        Assert.Equal(0, view.ContactCount);

        traced.Sim.Diagnostics.ResetWindow(IPhysicsDebugView.TraceMode);
        Assert.Equal(0, debug.TraceSummary(PhysicsTracePeriod.Window).Steps);
        Assert.Equal(ticks - on, debug.TraceSummary(PhysicsTracePeriod.Run).Steps);
        traced.Sim.Diagnostics.Set(IPhysicsDebugView.TraceMode, false);
        view.Extract(traced.Clock.Link, 1, contacts: true);
        Assert.False(view.TraceOn);
        Assert.Equal(0, view.ContactCount);
        plain.Clean();
        traced.Clean();
    }

    /// <summary>
    /// 0 Б на кадр (7и): мир представления 32 трупов и игрока — скин по телам, мечи на телах клинков, капсулы, суставы и контакты по флагам,
    /// строки трупов кадра — без выделений в установившемся кадре.
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
        Assert.Equal((32, 32 * BodySegs.Bodies), (view.CorpseStats.Corpses, view.CorpseStats.SimulatedBodies));
        Assert.Equal(33 * BodySegs.Bodies, view.BodyCount);
        Assert.Equal(32 * 13, view.JointTotal);
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
