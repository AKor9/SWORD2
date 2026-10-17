using System.Numerics;
using Sword.Engine.Animation;
using Sword.Engine.Diagnostics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Bodies;
using Sword.Game.Combat;
using Sword.Game.Contracts;
using Sword.Game.Ragdoll;

namespace Sword.Game.Tests;

/// <summary>
/// Смерть и рэгдолл: тело клинка (14-е, сегмент 11) — кинематическая капсула на мече в руке; массы и инерции тел; команды сцен
/// в реплее с теми же хешами; причина смерти; скин мёртвого = сустав × привязка; инвариант <c>ragdoll.skin</c>. Контент —
/// синтетический (<see cref="TestAssets"/>: sword_1h и katana разной длины).
/// </summary>
public sealed class RagdollSceneTests
{
    /// <summary>Модуль теста: берёт физику и привязки скина симуляции после сборки мира и (по желанию) ставит свою систему в стадию.</summary>
    private sealed class Probe(Action<IPhysicsWorld, SkinBindings> take, string? stage = null, ISystem? system = null) : IModule
    {
        public string Id => "probe";

        public void Configure(SimulationBuilder b)
        {
            b.OnWorldBuilt(w => take(w.Resource<IPhysicsWorld>(), w.Resource<SkinBindings>()));
            if (system is not null) b.System(stage!, system);
        }
    }

    private sealed record Game(Simulation Sim, IPhysicsWorld Physics, SkinBindings Skins);

    private static Game Create(string sets, string? stage = null, ISystem? system = null)
    {
        IPhysicsWorld? physics = null;
        SkinBindings? skins = null;
        // Без расчленения: тела бойца — 14 капсул трупа (отделение частей проверяет SeverTests).
        var sim = Simulation.Build(ConfigSet.Empty([.. sets.Split(';'), "modules.cutting.enabled=false"]), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe((p, s) => (physics, skins) = (p, s), stage, system)]);
        return new Game(sim, physics!, skins!);
    }

    private static void Step(Simulation sim, params SceneCommand[] commands)
    {
        foreach (var c in commands) sim.Enqueue(c);
        sim.Step();
    }

    private static Entity Fighter(Simulation sim, int slot)
    {
        foreach (var chunk in sim.World.Iterate(sim.World.Query().With<Fighter>().Read<FighterSlot>()))
            for (int i = 0; i < chunk.Count; i++)
                if (chunk.Read<FighterSlot>()[i].Index == slot) return chunk.Entities[i];
        throw new InvalidOperationException($"нет бойца на месте {slot}");
    }

    private static ArticulationHandle Body(Simulation sim, Entity e) => FighterBodies.Handle(sim.World.Get<BodyRef>(e));

    private static void Clean(Simulation sim) => Assert.True(sim.Invariants.Total == 0,
        string.Join("; ", sim.Invariants.Recent().Take(5).Select(v => $"{v.Id} тик {v.Tick} {v.Stage}: {v.Details}")));

    private static SceneCommand Kill(int slot) => new() { Action = SceneAction.Kill, Slot = slot };

    private static SceneCommand Place(int slot, float x, float z, float facing) =>
        new() { Action = SceneAction.Place, Slot = slot, Position = new Vector2(x, z), Facing = facing };

    private static SceneCommand Shift(int slot, Vector3 offset) => new() { Action = SceneAction.ShiftBodies, Slot = slot, Offset = offset };

    private static SceneCommand Sword(int slot, int weapon) => new() { Action = SceneAction.Weapon, Slot = slot, Weapon = weapon };

    private static SceneCommand Intent(float vx, float vz, float facing) =>
        new() { Action = SceneAction.PlayerIntent, Velocity = new Vector2(vx, vz), Facing = facing };

    // ── тело клинка ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Тело клинка по кончику меча (<see cref="BladeBody.Of"/>) в системе кисти: хват — начало кисти, ось — к кончику, центр — середина,
    /// полудлина — половина длины; опорная X — единичная и поперёк оси; система тела (<see cref="AnimMath.FromBasis"/>) переводит хват
    /// тела (конец A) в начало кисти, конец B — в кончик. Кончик в хвате — ось Y, длины нет.
    /// </summary>
    [Fact]
    public void BladeBody_OfTip_GripAtHandOrigin_AxisToTip()
    {
        Vector3[] tips = [TestAssets.SwordTip, TestAssets.KatanaTip, new(0.8f, 0, 0), new(0, 0, -1.2f), new(0.4f, 0.4f, 0.4f), new(0.01f, -0.9f, 0.3f)];
        foreach (var tip in tips)
        {
            var body = BladeBody.Of(tip);
            Assert.Equal(tip.Length() * 0.5f, body.HalfLength, 6);
            Assert.True(Vector3.Distance(tip * 0.5f, body.Center) < 1e-6f);
            Assert.True(Vector3.Distance(Vector3.Normalize(tip), body.Axis) < 1e-6f);
            Assert.Equal(1f, body.XHint.Length(), 5);
            Assert.True(MathF.Abs(Vector3.Dot(body.XHint, body.Axis)) < 1e-6f, $"{tip}: опорная ось не поперёк клинка");
            Assert.Equal(new Vector3(0, -body.HalfLength, 0), body.GripLocal);
            var q = AnimMath.FromBasis(body.Axis, body.XHint);
            Assert.True((body.Center + Vector3.Transform(body.GripLocal, q)).Length() < 1e-5f, $"{tip}: хват тела не в начале кисти");
            Assert.True(Vector3.Distance(tip, body.Center + Vector3.Transform(new Vector3(0, body.HalfLength, 0), q)) < 1e-5f);
            Assert.True(Vector3.Distance(body.XHint, Vector3.Transform(Vector3.UnitX, q)) < 1e-5f);
        }
        Assert.Equal(BladeBody.Grip, Vector3.Zero);
        var none = BladeBody.Of(Vector3.Zero);
        Assert.Equal((Vector3.UnitY, 0f), (none.Axis, none.HalfLength));
        // Таблица оружия: тело каждого меча посчитано заранее — то же, что по кончику.
        var weapons = TestAssets.Assets.Weapons;
        Assert.All(Enumerable.Range(0, weapons.Count), w => Assert.Equal(weapons.Weapons[w].Body, weapons.Bodies[w]));
        Assert.NotEqual(weapons.Bodies[0].HalfLength, weapons.Bodies[1].HalfLength);   // katana и sword_1h — разной длины
    }

    /// <summary>
    /// Капсула клинка каждый тик на отрезке меча в руке: A — хват (начало кисти), B — кончик (<see cref="Blade.Tip"/>); радиус
    /// 0,03, масса 1,4; скорость — разность постановок. Смена меча меняет полудлину в тот же тик и ставит тело без скорости.
    /// </summary>
    [Fact]
    public void BladeBody_LiesOnTheSwordInHand_EveryTick_ChangesFormWithWeapon()
    {
        var (sim, physics, _) = Create("arena.enemies=2");
        var weapons = TestAssets.Assets.Weapons;
        var poses = sim.World.View<IAnimationView>();
        int hand = TestAssets.Assets.Hand;
        int checkedTicks = 0;

        void CheckAll(string when)
        {
            foreach (int slot in new[] { 0, 1, 2 })
            {
                var e = Fighter(sim, slot);
                var s = physics.Get(physics.Body(Body(sim, e), BodySegs.Body(BodySegs.Weapon)));
                var blade = sim.World.Get<Blade>(e);
                int weapon = sim.World.Get<WeaponSlot>(e).Index;
                var root = sim.World.Get<Transform>(e);
                var handWorld = poses.Model(sim.World.Get<PoseRef>(e))[hand] * BodyPlacement.World(root.Position, root.Facing);
                Assert.Equal(weapon, blade.Weapon);
                Assert.Equal((BladeBody.Radius, BladeBody.Mass, weapons.Bodies[weapon].HalfLength, BodyMode.Kinematic),
                    (s.Radius, s.Mass, s.HalfLength, s.Mode));
                Assert.True(Vector3.Distance(s.EndA, handWorld.Translation) < 1e-4f, $"{when}, место {slot}: хват не в кисти ({Vector3.Distance(s.EndA, handWorld.Translation)} м)");
                Assert.True(Vector3.Distance(s.EndB, blade.Tip) < 1e-4f, $"{when}, место {slot}: конец B не на кончике");
                // основание клинка (22 см от кисти) — на оси капсулы
                float along = Vector3.Dot(blade.Base - s.EndA, s.Axis);
                Assert.True(Vector3.Distance(s.EndA + s.Axis * along, blade.Base) < 1e-4f && along > 0 && along < 2 * s.HalfLength, $"{when}: основание не на оси");
            }
            checkedTicks++;
        }

        CheckAll("сборка мира");
        for (int t = 0; t < 160; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0.3f, 1), Attack = t % 40 == 0 });
            Step(sim);
            CheckAll($"тик {sim.Tick}");
            var s = physics.Get(physics.Body(Body(sim, Fighter(sim, 0)), BodySegs.Weapon));
            Assert.Equal((s.Position - s.PrevPosition) / sim.Dt, s.Velocity);   // кинематическое: скорость — разность за тик
        }

        // T: следующий меч игрока — другая форма в тот же тик, постановка без скорости.
        var player = Fighter(sim, 0);
        int before = sim.World.Get<WeaponSlot>(player).Index;
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.NextWeapon });
        Step(sim);
        int after = sim.World.Get<WeaponSlot>(player).Index;
        Assert.NotEqual(before, after);
        var changed = physics.Get(physics.Body(Body(sim, player), BodySegs.Weapon));
        Assert.Equal(weapons.Bodies[after].HalfLength, changed.HalfLength);
        Assert.Equal((Vector3.Zero, Vector3.Zero, changed.Position), (changed.Velocity, changed.AngularVelocity, changed.PrevPosition));
        CheckAll("смена меча T");
        Step(sim);
        var next = physics.Get(physics.Body(Body(sim, player), BodySegs.Weapon));
        Assert.Equal(changed.Position, next.PrevPosition);   // дальше — снова кинематическая цель от прошлой постановки

        // Меч места врагу командой сцены — тот же путь.
        var enemy = Fighter(sim, 1);
        int other = 1 - sim.World.Get<WeaponSlot>(enemy).Index;
        Step(sim, Sword(1, other));
        Assert.Equal(other, sim.World.Get<WeaponSlot>(enemy).Index);
        Assert.Equal(weapons.Bodies[other].HalfLength, physics.Get(physics.Body(Body(sim, enemy), BodySegs.Weapon)).HalfLength);
        CheckAll("меч места");
        Assert.True(checkedTicks > 160);
        Clean(sim);
    }

    /// <summary>
    /// Массы и инерции всех 14 тел бойца заданы при постройке (таблица сегментов, клинок — 1,4 кг при любом мече; всего 74,8 кг): обратная
    /// масса — 1/m, обратная инерция — капсулы этой формы; так же после сброса R, постройки командой сцены и возрождения.
    /// </summary>
    [Fact]
    public void Masses_AndInertia_OnEveryBody_ThroughEveryRebuild()
    {
        var (sim, physics, _) = Create("arena.enemies=3");
        Assert.Equal([12f, 20f, 5f, 2.5f, 2f, 2.5f, 2f, 8f, 4.5f, 8f, 4.5f, 1.4f, 1.2f, 1.2f], FighterShape.Masses.ToArray());
        Assert.Equal(74.8f, FighterShape.Masses.ToArray().Sum(), 3);

        void Check(string when)
        {
            foreach (int slot in new[] { 0, 1, 2, 3 })
            {
                var body = Body(sim, Fighter(sim, slot));
                for (int k = 0; k < BodySegs.Bodies; k++)
                {
                    var s = physics.Get(physics.Body(body, k));
                    int seg = BodySegs.Segment(k);
                    Assert.True(s.Mass == FighterShape.Masses[seg] && s.InvMass == 1 / s.Mass, $"{when}, место {slot}, {BodySegs.Name(seg)}: масса {s.Mass}");
                    Assert.Equal(Dynamics.CapsuleInvInertia(s.Mass, s.Radius, s.HalfLength), s.InvInertia);
                    Assert.True(s.InvInertia.X > 0 && s.InvInertia.Y > 0 && s.InvInertia.Z > 0 && float.IsFinite(s.InvInertia.X + s.InvInertia.Y + s.InvInertia.Z));
                }
            }
        }

        Check("сборка мира");
        for (int t = 0; t < 20; t++) Step(sim);
        Check("тик 20");
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim);
        Check("сброс R");
        Step(sim, Place(2, 3, 4, 1), Sword(3, 0), Sword(1, 1));
        Step(sim);
        Check("постройка командой и смена мечей");
        Step(sim, Kill(1));
        for (int t = 0; t < (int)(8.2f / sim.Dt); t++) Step(sim);   // возрождение через 8 с
        Assert.False(sim.World.Has<Dead>(Fighter(sim, 1)));
        Check("возрождение");
        Clean(sim);
    }

    // ── команды сцен ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Смерть без удара (<see cref="SceneAction.Kill"/>): метка <see cref="Dead"/> с временем тика и причиной «команда», здоровье не
    /// тронуто; мёртвый — труп на физике с тика смерти: все тела на физике, сустав запястья выключен (меч выпал), тела падают. Уже
    /// мёртвому — ничего (труп не переходит второй раз); убитый враг засчитан; причины других смертей — проба и кровопотеря.
    /// </summary>
    [Fact]
    public void Kill_ByCommand_DeadWithCommandCause_BecomesCorpse()
    {
        var (sim, physics, _) = Create("arena.enemies=2");
        for (int t = 0; t < 10; t++) Step(sim);
        var enemy = Fighter(sim, 1);
        var hp = sim.World.Get<Health>(enemy).Hp;
        Step(sim, Kill(1));
        var dead = sim.World.Get<Dead>(enemy);
        Assert.Equal((DeathCause.Command, sim.World.Resource<SimTime>().Time), (dead.Cause, dead.Time));
        Assert.Equal(hp, sim.World.Get<Health>(enemy).Hp);
        Assert.Equal(1, sim.World.Resource<ArenaStats>().Kills);
        var body = Body(sim, enemy);
        var start = Enumerable.Range(0, BodySegs.Bodies).Select(k => physics.Get(physics.Body(body, k))).ToArray();
        var blade = sim.World.Get<Blade>(enemy);
        // тик смерти командой: переход в Gameplay по телам прошлого тика, шаг этого тика — первый шаг трупа (начало шага — поза смерти)
        Assert.True(Vector3.Distance(start[BodySegs.Weapon].StartPosition + Vector3.Transform(new Vector3(0, start[BodySegs.Weapon].HalfLength, 0),
            start[BodySegs.Weapon].StartRotation), blade.Tip) < 1e-4f);   // меч был в руке
        Assert.All(start, s => Assert.Equal(BodyMode.Simulated, s.Mode));
        Assert.False(physics.IsJointEnabled(body, Ragdoll.CorpseJoints.Wrist));
        var bindings = Enumerable.Range(0, Ragdoll.CorpseJoints.Count).Select(j => physics.Binding(body, j)).ToArray();
        Step(sim, Kill(1));   // уже мёртвому — ничего: и труп тот же (суставы не перепривязаны)
        Assert.Equal(dead, sim.World.Get<Dead>(enemy));
        Assert.Equal(bindings, Enumerable.Range(0, Ragdoll.CorpseJoints.Count).Select(j => physics.Binding(body, j)));
        for (int t = 0; t < 30; t++) Step(sim);
        Assert.True(physics.Get(physics.Body(body, BodySegs.Pelvis)).Position.Y < start[BodySegs.Pelvis].Position.Y - 0.1f, "труп не падает");

        // Проба — причина «проба»; кровопотеря после пробы — «кровопотеря»; игрок убит командой — тоже «команда».
        sim.Enqueue(new StrikeProbe { Attacker = 0, Victim = 2, Part = BodySegs.Head, Damage = 1000 });
        Step(sim);
        Assert.Equal(DeathCause.Probe, sim.World.Get<Dead>(Fighter(sim, 2)).Cause);
        Step(sim, Kill(0));
        Assert.Equal(DeathCause.Command, sim.World.Get<Dead>(Fighter(sim, 0)).Cause);
        Clean(sim);

        var (bleeding, _, _) = Create("arena.enemies=1");
        bleeding.Enqueue(new StrikeProbe { Attacker = 0, Victim = 1, Part = BodySegs.ThighL, Damage = 196 });   // 2 очка, кровотечение 3,92/с
        Step(bleeding);
        Assert.False(bleeding.World.Has<Dead>(Fighter(bleeding, 1)));
        for (int t = 0; t < 120 && !bleeding.World.Has<Dead>(Fighter(bleeding, 1)); t++) Step(bleeding);
        Assert.Equal(DeathCause.Bleed, bleeding.World.Get<Dead>(Fighter(bleeding, 1)).Cause);
        Clean(bleeding);
    }

    /// <summary>
    /// Расстановка (<see cref="SceneAction.Place"/>): боец места построен заново в точке курсом — корень, прошлый корень, тело и клинок
    /// без скачка (тела — по позе постройки в новом месте), зерно и меч прежние; мёртвый оживает (привязка скина снята). Нечисловая
    /// точка и место без бойца — ничего. Команды одного места в тике — по порядку.
    /// </summary>
    [Fact]
    public void Place_ByCommand_RebuildsAtPoint_RevivesDead_KeepsSeedAndWeapon()
    {
        var (sim, physics, skins) = Create("arena.enemies=2");
        for (int t = 0; t < 5; t++) Step(sim);
        var enemy = Fighter(sim, 1);
        var seed = sim.World.Get<FighterSeed>(enemy);
        int weapon = sim.World.Get<WeaponSlot>(enemy).Index;
        Step(sim, Kill(1));
        Assert.True(skins.TryGet(Body(sim, enemy), out _, out _));
        Step(sim, Place(1, -12.5f, 0.5f, MathF.PI / 2));
        Assert.Equal(enemy, Fighter(sim, 1));   // та же сущность
        Assert.False(sim.World.Has<Dead>(enemy));
        Assert.False(skins.TryGet(Body(sim, enemy), out _, out _));
        // корень постройки — прошлый корень тика постройки (в тике поставленный уже прошёл свой шаг движения)
        var built = sim.World.Get<PrevTransform>(enemy);
        Assert.Equal((new Vector3(-12.5f, 0, 0.5f), MathF.PI / 2), (built.Position, built.Facing));
        var root = sim.World.Get<Transform>(enemy);
        Assert.True(Vector3.Distance(root.Position, built.Position) < 0.05f, $"корень {root.Position}");
        Assert.Equal((seed, weapon), (sim.World.Get<FighterSeed>(enemy), sim.World.Get<WeaponSlot>(enemy).Index));
        var pelvis = physics.Get(physics.Body(Body(sim, enemy), BodySegs.Pelvis));
        Assert.InRange(new Vector2(pelvis.Position.X + 12.5f, pelvis.Position.Z - 0.5f).Length(), 0, 0.3f);
        Assert.True(pelvis.Velocity.Length() < 5, $"тело прилетело: {pelvis.Velocity.Length()} м/с");   // постройка — без скачка

        // Нечисловая точка, место без бойца, пустое действие — ничего; «убить, затем поставить» — жив и стоит; «поставить, затем убить» — мёртв.
        Step(sim, Place(1, float.NaN, 0, 0), Place(7, 1, 1, 0), new SceneCommand { Action = SceneAction.None, Slot = 1 });
        var prev = sim.World.Get<PrevTransform>(enemy);
        Assert.Equal((root.Position, root.Facing), (prev.Position, prev.Facing));   // не построен заново: прошлый корень — корень прошлого тика
        Step(sim, Kill(2), Place(2, 4, 4, 0));
        Assert.False(sim.World.Has<Dead>(Fighter(sim, 2)));
        Assert.Equal(new Vector3(4, 0, 4), sim.World.Get<PrevTransform>(Fighter(sim, 2)).Position);
        Step(sim, Place(2, 5, 5, 0), Kill(2));
        Assert.True(sim.World.Has<Dead>(Fighter(sim, 2)));
        // стоит мёртвым в точке — труп с тика смерти: корень — за тазом тела постройки
        Assert.True(Vector3.Distance(new Vector3(5, 0, 5), sim.World.Get<Transform>(Fighter(sim, 2)).Position) < 0.2f);
        Clean(sim);
    }

    /// <summary>
    /// Сдвиг тел трупа (<see cref="SceneAction.ShiftBodies"/>): все 14 тел мёртвого — ровно на вектор, без скорости, поворот прежний (в Intent,
    /// до шага: поза начала шага тика — сдвинутая, скорость после шага — только от шага); поза не двигается, корень — за тазом. Живому
    /// и убитому этим же тиком — ничего; нечисловой сдвиг — ничего.
    /// </summary>
    [Fact]
    public void ShiftBodies_MovesOnlyCorpseBodies_WithoutVelocity()
    {
        var (sim, physics, _) = Create("arena.enemies=2");
        for (int t = 0; t < 10; t++) Step(sim);
        var offset = new Vector3(0.3f, 0.1f, -0.2f);
        var enemy = Fighter(sim, 1);
        var body = Body(sim, enemy);
        var alive = Body(sim, Fighter(sim, 2));
        Step(sim, Kill(1), Shift(1, offset));   // убит этим же тиком — не сдвигается
        var before = Enumerable.Range(0, BodySegs.Bodies).Select(k => physics.Get(physics.Body(body, k))).ToArray();
        var root = sim.World.Get<Transform>(enemy);
        var aliveBefore = physics.Get(physics.Body(alive, BodySegs.Pelvis));
        Step(sim, Shift(1, offset), Shift(2, new Vector3(5, 0, 0)), Shift(1, new Vector3(float.NaN, 0, 0)));
        for (int k = 0; k < BodySegs.Bodies; k++)
        {
            var s = physics.Get(physics.Body(body, k));
            Assert.Equal((before[k].Position + offset, before[k].Rotation), (s.StartPosition, s.StartRotation));   // начало шага — сдвинутое
            Assert.True(Vector3.Distance(s.Position, s.StartPosition) < 0.01f && s.Velocity.Length() < 0.5f, $"тело {k}: сдвиг со скоростью {s.Velocity}");
        }
        var pelvis = physics.Get(physics.Body(body, BodySegs.Pelvis)).Position;
        Assert.Equal((pelvis.X, pelvis.Z, root.Facing), (sim.World.Get<Transform>(enemy).Position.X, sim.World.Get<Transform>(enemy).Position.Z,
            sim.World.Get<Transform>(enemy).Facing));
        // живого тело ставит поза: его сдвиг не прошёл — таз там, где поставила постановка тика (рядом с прежним)
        Assert.True(Vector3.Distance(aliveBefore.Position, physics.Get(physics.Body(alive, BodySegs.Pelvis)).Position) < 0.2f);
        Clean(sim);   // physics.kinematicPose мёртвого не сверяет: его тела уже не по позе
    }

    /// <summary>
    /// Намерение игрока командой сцены (<see cref="SceneAction.PlayerIntent"/>, сцена s3): поверх команды игрока — желаемая скорость и
    /// курс-цель этого тика; удержание — повторять; корень идёт с заданной скоростью. Нечисловая скорость — стоять; мёртвому — ничего.
    /// </summary>
    [Fact]
    public void PlayerIntent_ByCommand_OverridesPlayerCommand()
    {
        var (sim, _, _) = Create("arena.enemies=0");
        var player = Fighter(sim, 0);
        for (int t = 0; t < 240; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(1, 0) });   // вбок — намерение сцены перебивает
            Step(sim, Intent(0, 4.2f, 0));
            Assert.Equal((new Vector3(0, 0, 4.2f), 0f), (sim.World.Get<FighterCommand>(player).DesiredVelocity, sim.World.Get<FighterCommand>(player).FacingGoal));
        }
        var root = sim.World.Get<Transform>(player).Position;
        Assert.True(root.Z > 5 && MathF.Abs(root.X) < 0.05f, $"корень {root}");
        Assert.Equal(4.2f, sim.World.Get<RootVelocity>(player).Value.Z, 2);
        Step(sim, Intent(float.NaN, 0, float.NaN));
        Assert.Equal(Vector3.Zero, sim.World.Get<FighterCommand>(player).DesiredVelocity);
        Step(sim, Kill(0));
        var dead = sim.World.Get<FighterCommand>(player);
        Step(sim, Intent(0, 3, 1));
        Assert.Equal(dead, sim.World.Get<FighterCommand>(player));
        Clean(sim);
    }

    [Fact]
    public void SceneCommands_RecordedInReplay_RepeatWithSameHashes()
    {
        var a = TestAssets.Simulation(ConfigSet.Empty("arena.enemies=3"));
        var recorder = a.Record(new ReplayInfo("test", "tests", "none"));

        static IEnumerable<SceneCommand> At(int t) => t switch
        {
            1 => [Place(1, 0, 1.4f, MathF.PI), Place(2, 0.36f, 1.5f, MathF.PI), Place(3, -0.36f, 1.5f, MathF.PI)],
            60 => [Kill(2), Kill(3)],
            61 => [Shift(3, new Vector3(0.1f, 0, 0.2f))],
            90 => [Sword(1, 0), Sword(0, 0)],
            120 => [Kill(1)],
            >= 130 and < 250 => [Intent(0, 4.2f, 0)],
            260 => [Place(2, 3, 3, 0)],
            _ => [],
        };

        for (int t = 0; t < 300; t++)
        {
            foreach (var c in At(t)) a.Enqueue(c);
            a.Step();
        }
        Clean(a);
        var replay = Replay.Parse(recorder.ToReplay().ToJson());
        Assert.Contains(replay.CommandTypes, c => c.Name.Contains("SceneCommand", StringComparison.Ordinal));
        var b = TestAssets.Simulation(replay.CreateConfig());
        var player = b.Play(replay);
        while (b.Tick < replay.Ticks) b.Step();
        Assert.Null(player.Divergence);
        Assert.Equal(301, player.HashesChecked);
        Assert.Equal(a.HashState().Total, b.HashState().Total);
        Assert.NotEqual(0ul, b.HashState().Parts["skin"]);
    }

    // ── привязка скина ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Привязка скина мёртвого (<see cref="ISkinView"/>) снята в тик смерти: сустав × привязка = сустав позы смерти в мире бойца;
    /// тела падают или сдвинуты — скин идёт за ними, привязка не меняется; у живого привязки нет, ожил — сброшена. Хеш
    /// <c>skin</c> её видит.
    /// </summary>
    [Fact]
    public void SkinBinding_JointTimesBinding_IsDeathPose_AndFollowsBodies()
    {
        var (sim, physics, bindings) = Create("arena.enemies=2");
        for (int t = 0; t < 30; t++)
        {
            sim.Enqueue(new PlayerCommand { Move = new Vector2(0, 1) });
            Step(sim);
        }
        var skins = sim.World.View<ISkinView>();
        var bodies = sim.World.View<IBodyView>();
        var poses = sim.World.View<IAnimationView>();
        Assert.Equal(poses.JointCount, skins.JointCount);
        var enemy = Fighter(sim, 1);
        var player = Fighter(sim, 0);
        Assert.False(skins.TryGet(sim.World.Get<BodyRef>(enemy), out _, out _));
        ulong emptySkin = sim.HashState().Parts["skin"];

        // тела и корень врага — до тика смерти командой (их и снимает Gameplay)
        var enemyRoot = sim.World.Get<Transform>(enemy);
        var enemyBodies = Enumerable.Range(0, BodySegs.Bodies).Select(k => bodies.Body(sim.World.Get<BodyRef>(enemy), k).World).ToArray();

        float Error(Entity e, Vector3 shift, Transform? deathRoot = null, Matrix4x4[]? deathBodies = null)
        {
            var bodyRef = sim.World.Get<BodyRef>(e);
            Assert.True(skins.TryGet(bodyRef, out var binding, out float time));
            Assert.Equal(sim.World.Get<Dead>(e).Time, time);
            Assert.Equal(poses.JointCount, binding.Length);
            var model = poses.Model(sim.World.Get<PoseRef>(e));
            var root = deathRoot ?? sim.World.Get<Transform>(e);
            var fighter = BodyPlacement.World(root.Position, root.Facing);
            float worst = 0;
            for (int j = 0; j < binding.Length; j++)
            {
                int body = BodySegs.Body(bodies.JointSegment(j));
                var skin = binding[j] * (deathBodies is null ? bodies.Body(bodyRef, body).World : deathBodies[body]);
                var pose = model[j] * fighter;
                worst = MathF.Max(worst, Vector3.Distance(skin.Translation, pose.Translation + shift));
                // поворот сустава — тот же (строки системы сустава)
                worst = MathF.Max(worst, Vector3.Distance(new Vector3(skin.M11, skin.M12, skin.M13), new Vector3(pose.M11, pose.M12, pose.M13)));
                worst = MathF.Max(worst, Vector3.Distance(new Vector3(skin.M21, skin.M22, skin.M23), new Vector3(pose.M21, pose.M22, pose.M23)));
            }
            return worst;
        }

        // смерть командой (снимок в Gameplay) и пробой (снимок в Settle) в одном тике
        sim.Enqueue(new StrikeProbe { Attacker = 1, Victim = 0, Part = BodySegs.Head, Damage = 1000 });
        Step(sim, Kill(1));
        Assert.True(Error(enemy, Vector3.Zero, enemyRoot, enemyBodies) < 1e-4f, $"враг: {Error(enemy, Vector3.Zero, enemyRoot, enemyBodies)}");
        Assert.True(Error(player, Vector3.Zero) < 1e-4f, $"игрок: {Error(player, Vector3.Zero)}");   // смерть в Settle — после шага, тела те же
        Assert.NotEqual(emptySkin, sim.HashState().Parts["skin"]);
        var binding0 = skins.TryGet(sim.World.Get<BodyRef>(enemy), out var b0, out _) ? b0.ToArray() : [];
        for (int t = 0; t < 240; t++) Step(sim);
        Assert.Equal(binding0, skins.TryGet(sim.World.Get<BodyRef>(enemy), out var b1, out _) ? b1.ToArray() : []);   // привязка не меняется
        Assert.True(Error(enemy, Vector3.Zero, enemyRoot) > 0.05f, "скин не пошёл за падающими телами");   // труп лёг — скин с ним
        // лежащий труп сдвинут: скин — ровно на сдвиг (шаг сдвинутого, с 5 см над землёй — доли мм)
        var lying = Enumerable.Range(0, BodySegs.Bodies).Select(k => bodies.Body(sim.World.Get<BodyRef>(enemy), k).World).ToArray();
        var shift = new Vector3(0.4f, 0.05f, -0.3f);
        Step(sim, Shift(1, shift));
        float moved = 0;
        for (int j = 0; j < binding0.Length; j++)
        {
            int body = BodySegs.Body(bodies.JointSegment(j));
            var before = (binding0[j] * lying[body]).Translation;
            var after = (binding0[j] * bodies.Body(sim.World.Get<BodyRef>(enemy), body).World).Translation;
            moved = MathF.Max(moved, Vector3.Distance(after - before, shift));
        }
        Assert.True(moved < 0.01f, $"скин не пошёл за телами: {moved}");
        int captured = Enumerable.Range(0, bindings.Capacity).Count(i => bindings.At(i).Generation != 0);
        Assert.Equal((2, poses.JointCount), (captured, bindings.JointCount));
        Clean(sim);

        // Ожил — привязка сброшена (команда сцены), игрок — сброс R.
        Step(sim, Place(1, 2, 2, 0));
        Assert.False(skins.TryGet(sim.World.Get<BodyRef>(enemy), out _, out _));
        sim.Enqueue(new ArenaCommand { Action = ArenaAction.Reset });
        Step(sim);
        Assert.False(skins.TryGet(sim.World.Get<BodyRef>(player), out _, out _));
        Assert.Equal(emptySkin, sim.HashState().Parts["skin"]);
        Clean(sim);
    }

    /// <summary>End, после сброса привязок: сбрасывает привязку мёртвого врага места 1 (неисправность).</summary>
    private sealed class DropSkin : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("drop-skin")).After(GameLabels.SkinRelease);

        public void Run(in SystemContext ctx)
        {
            foreach (var chunk in ctx.World.Iterate(ctx.World.Query().With<Dead>().Read<FighterSlot>()))
                for (int i = 0; i < chunk.Count; i++)
                    if (chunk.Read<FighterSlot>()[i].Index == 1) ctx.World.Resource<SkinBindings>().Release(chunk.Entities[i]);
        }
    }

    /// <summary>End: снимает «привязку» живому игроку (неисправность — привязка у живого).</summary>
    private sealed class SkinOnLiving(IPhysicsWorld physics) : IContinuousSystem
    {
        public void Setup(SystemSetup s) => s.Label(new SystemLabel("skin-living")).After(GameLabels.SkinRelease);

        public void Run(in SystemContext ctx)
        {
            var w = ctx.World;
            if (w.Resource<SimTime>().Tick != 3) return;
            var player = GameCore.Player(w.Reader);
            var root = w.Get<Transform>(player);
            w.Resource<SkinBindings>().Capture(physics, player, FighterBodies.Handle(w.Get<BodyRef>(player)),
                w.Resource<PosePool>().Model(new PoseHandle(w.Get<PoseRef>(player).Index, w.Get<PoseRef>(player).Generation)),
                BodyPlacement.World(root.Position, root.Facing), 0);
        }
    }

    /// <summary><c>ragdoll.skin</c> ловит мёртвого без привязки и привязку у живого — на своём тике, в конце End; честная игра — чисто.</summary>
    [Fact]
    public void SkinInvariant_CatchesMissingAndForeignBinding()
    {
        var (missing, _, _) = Create("arena.enemies=2", GameStages.End, new DropSkin());
        for (int t = 0; t < 5; t++) Step(missing);
        Assert.Equal(0, missing.Invariants.Total);
        Step(missing, Kill(2));
        Assert.Equal(0, missing.Invariants.Total);   // место 2 — привязка есть
        ulong killTick = missing.Tick;
        Step(missing, Kill(1));
        var v = missing.Invariants.Recent().First(x => x.Id == "ragdoll.skin");
        Assert.Equal((killTick, GameStages.End), (v.Tick, v.Stage));   // тик смерти, после End
        Assert.Contains("без привязки скина", v.Details);
        Assert.Contains("Command", v.Details);

        IPhysicsWorld? physics = null;
        var foreign = Simulation.Build(ConfigSet.Empty("arena.enemies=1"), GameStages.All, b => GameCore.Configure(b, TestAssets.Assets),
            [.. Bootstrap.SwordSimulation.Modules(), new Probe((p, _) => physics = p, GameStages.End, new LazySkinOnLiving(() => physics!))]);
        for (int t = 0; t < 5; t++) Step(foreign);
        var living = foreign.Invariants.Recent().First(x => x.Id == "ragdoll.skin");
        Assert.Equal(3ul, living.Tick);
        Assert.Contains("у живого", living.Details);
    }

    /// <summary><see cref="SkinOnLiving"/> с физикой, которую модуль получит только при сборке мира.</summary>
    private sealed class LazySkinOnLiving(Func<IPhysicsWorld> physics) : IContinuousSystem
    {
        private SkinOnLiving? _inner;

        public void Setup(SystemSetup s) => s.Label(new SystemLabel("skin-living")).After(GameLabels.SkinRelease);

        public void Run(in SystemContext ctx) => (_inner ??= new SkinOnLiving(physics())).Run(ctx);
    }
}
