using System.Numerics;
using Sword.Engine.Client;
using Sword.Engine.Ecs;
using Sword.Engine.Runtime;
using Sword.Game.Client;
using Sword.Game.Contracts;
using Sword.Game.Fire;

namespace Sword.Game.Tests;

/// <summary>
/// Скелет модуля огня без окна и GPU (контент синтетический, <see cref="TestAssets"/>): огонь только у игрока (решение 5), головка по тикам —
/// кончик клинка тика (<see cref="Blade.Tip"/>) после своего тика, в том числе при нескольких тиках за кадр; у мёртвого игрока оружие выпало —
/// огонь «гаснет» (решение 7), головка — на теле клинка трупа; боец построен в тике (<see cref="WeaponTip.Built"/>: сброс R живого) — огонь
/// заново; T с горящего оружия — огня нет, T обратно — огонь заново; оружия из <c>fire.weapons</c> нет в паке — огня нет; модуль выключен —
/// он не настраивается вовсе, секция <c>fire</c> пропущена; наблюдатель огня — 0 Б на тик.
/// </summary>
public sealed class FireModuleTests
{
    /// <summary>Горит всё оружие пака теста: и у врагов горящее оружие — огня у них всё равно нет.</summary>
    private const string AllBurn = "fire.weapons=[\"sword_1h\",\"katana\"]";

    private sealed class Scene
    {
        public readonly Simulation Sim;
        public readonly ClientModules Modules;
        public readonly SimulationClock Clock;
        public readonly FireModule Fire = new();
        public readonly List<SceneCommand> Pending = [];
        public readonly List<ArenaCommand> PendingArena = [];
        public readonly ConfigSet Config;

        /// <summary>Кончик клинка игрока после каждого тика — снят в мире своим наблюдателем, после наблюдателя огня.</summary>
        public readonly List<(ulong Tick, Vector3 Tip)> Tips = [];

        /// <summary>Центр шишки (кончик на 2 см ближе к основанию) по тикам.</summary>
        public readonly Dictionary<ulong, Vector3> Centers = [];

        public Scene(int ticksPerFrame, params string[] sets)
        {
            Config = ConfigSet.Empty(["arena.enemies=3", "combat.enemiesAttack=false", .. sets]);
            Sim = TestAssets.Simulation(Config);
            Modules = ClientModules.Compose(Config, Sim, [Fire], new ClientServices().Add<IWeaponTips>(link => new ArenaWeaponTips(link)));
            Clock = new SimulationClock(Sim, (link, _) =>
            {
                foreach (var c in Pending) link.Enqueue(c);
                foreach (var c in PendingArena) link.Enqueue(c);
                Pending.Clear();
                PendingArena.Clear();
            }, ticksPerFrame, afterTick: tick =>
            {
                Modules.AfterTick(tick);
                var blade = Sim.World.Get<Blade>(Player);
                Tips.Add((tick, blade.Tip));
                Centers[tick] = FireArea.KnobCenter(blade.Base, blade.Tip, 0.02f);
            });
        }

        public Entity Player
        {
            get
            {
                foreach (var chunk in Sim.World.Iterate(Sim.World.Query().With<PlayerTag>().Read<FighterSlot>()))
                    if (chunk.Count > 0) return chunk.Entities[0];
                throw new InvalidOperationException("нет игрока");
            }
        }

        public void Frames(int n, float alpha = 1)
        {
            for (int i = 0; i < n; i++)
            {
                Modules.BeginFrame(Sim.Tick);
                int ticks = Clock.RunFrame(0);
                Modules.Extract(new FrameView(alpha, ticks, 0, default));
            }
        }

        /// <summary>Кадр без тиков (пауза).</summary>
        public void Paused(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Modules.BeginFrame(Sim.Tick);
                Modules.Extract(new FrameView(1, 0, 0, default));
            }
        }
    }

    [Fact]
    public void Head_IsBladeTipOfEachTick_OnlyPlayerBurns()
    {
        var scene = new Scene(ticksPerFrame: 3, AllBurn);
        var fires = scene.Fire.Fires!;
        Assert.Equal(["katana", "sword_1h"], fires.Burning.Order());
        for (int frame = 0; frame < 40; frame++)
        {
            scene.Frames(1);
            // Враги тоже с горящим оружием, но огонь — только у игрока.
            var fire = Assert.Single(fires.Fires.ToArray());
            Assert.Equal(0, fire.Slot);
            Assert.False(fire.Dying);
            Assert.Equal(3, fires.LastFrameTicks);
            // Головки кадра по тикам — кончик клинка тика, каждого из трёх, а не последнего.
            for (int k = 0; k < 3; k++)
            {
                var (tick, head) = fire.Point(k);
                var (expectedTick, tip) = scene.Tips[^(k + 1)];
                Assert.Equal(expectedTick, tick);
                Assert.Equal(tip, head);
            }
            Assert.Equal(scene.Sim.Tick - 1, fire.LastTick);
            Assert.Equal(scene.Tips[^1].Tip, fire.Head);
        }
        // Клинок за 120 тиков сдвинулся (бег, поворот, атака не нужны: поза стойки качается) или стоит — головка всё равно его кончик.
        Assert.Equal(120, fires.Fires[0].Ticks);
        Assert.Equal(0, fires.OverLimit);
    }

    [Fact]
    public void DeadPlayer_FireIsDying_HeadOnDroppedWeapon()
    {
        var scene = new Scene(ticksPerFrame: 1, "fire.weapons=[\"sword_1h\"]", "combat.playerWeapon=sword_1h");
        scene.Frames(10);
        Assert.False(Assert.Single(scene.Fire.Fires!.Fires.ToArray()).Dying);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        scene.Frames(60);
        var fire = Assert.Single(scene.Fire.Fires!.Fires.ToArray());
        Assert.True(fire.Dying);
        // Головка — кончик выпавшего оружия: по телу клинка трупа (там же рисуется меч трупа).
        var sim = scene.Sim;
        var player = scene.Player;
        Assert.True(sim.World.Has<Dead>(player));
        var bodies = sim.World.View<IBodyView>();
        var weapon = sim.World.View<ICombatView>().Weapons[sim.World.Get<WeaponSlot>(player).Index];
        var body = bodies.Body(sim.World.Get<BodyRef>(player), BodySegs.Body(BodySegs.Weapon));
        var inWorld = ArenaView.BladeInHandInverse(weapon.Body) * ArenaView.BodySystem(body.Position, body.Rotation);
        Assert.True(Vector3.Distance(Vector3.Transform(weapon.BladeTip, inWorld), fire.Head) < 1e-5f);
        Assert.Equal(sim.Tick - 1, fire.LastTick);

        // Постройка места заново (как сброс R) — на той же сущности, ключ тот же: огонь в руке — новый, не «гаснущий» с прежними головками.
        ulong key = fire.Key, rebuilt = sim.Tick;
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(3, 0) });
        scene.Frames(1);
        var reborn = Assert.Single(scene.Fire.Fires!.Fires.ToArray());
        Assert.Equal(key, reborn.Key);
        Assert.False(reborn.Dying);
        Assert.Equal(rebuilt, reborn.FirstTick);
        Assert.Equal(1, reborn.PointCount);
        Assert.Equal(sim.World.Get<Blade>(player).Tip, reborn.Head);
    }

    [Fact]
    public void ResetOfLivePlayer_Built_FireStartsAnew()
    {
        // Сброс R живого игрока: та же сущность и тот же ключ, оружие в руке и живо — без признака постройки огонь продолжался бы с
        // головками старого места. Постройка в тике — огонь заново: первый тик — тик сброса, одна головка — кончик клинка нового места.
        var scene = new Scene(ticksPerFrame: 2, "fire.weapons=[\"sword_1h\"]", "combat.playerWeapon=sword_1h");
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Place, Slot = 0, Position = new Vector2(4, 3) });
        scene.Frames(20);
        var fire = Assert.Single(scene.Fire.Fires!.Fires.ToArray());
        ulong key = fire.Key, first = fire.FirstTick;
        Assert.False(fire.Dying);
        Assert.True(fire.PointCount > 1);
        Assert.True(new Vector2(fire.Head.X, fire.Head.Z).Length() > 3);   // игрок поставлен в (4, 3) — далеко от центра

        ulong reset = scene.Sim.Tick;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.Reset });
        scene.Frames(1);   // два тика: сброс и следующий
        var reborn = Assert.Single(scene.Fire.Fires!.Fires.ToArray());
        Assert.Equal(key, reborn.Key);
        Assert.False(reborn.Dying);
        Assert.NotEqual(first, reborn.FirstTick);
        Assert.Equal(reset, reborn.FirstTick);
        Assert.Equal(2, reborn.PointCount);
        Assert.Equal(scene.Tips[^2], reborn.Point(1));
        Assert.Equal(scene.Tips[^1].Tip, reborn.Head);
        Assert.True(new Vector2(reborn.Head.X, reborn.Head.Z).Length() < 2);   // игрок заново в центре

        // Без постройки огонь не заводится заново: следующий кадр — тот же огонь, головок прибавилось.
        scene.Frames(1);
        var same = Assert.Single(scene.Fire.Fires!.Fires.ToArray());
        Assert.Equal(reset, same.FirstTick);
        Assert.Equal(4, same.PointCount);
    }

    [Fact]
    public void WeaponSwitch_FireGoes_BackToBurning_FireStartsAnew()
    {
        // T с горящего оружия (sword_1h) на другое (katana) — огня нет; T обратно — огонь заново: ключ тот же, первый тик — тик смены.
        var scene = new Scene(ticksPerFrame: 1, "fire.weapons=[\"sword_1h\"]", "combat.playerWeapon=sword_1h");
        scene.Frames(10);
        ulong key = Assert.Single(scene.Fire.Fires!.Fires.ToArray()).Key;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(5);
        Assert.Empty(scene.Fire.Fires.Fires.ToArray());

        ulong back = scene.Sim.Tick;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(3);
        var again = Assert.Single(scene.Fire.Fires.Fires.ToArray());
        Assert.Equal((key, back, 3L, 3, false), (again.Key, again.FirstTick, again.Ticks, again.PointCount, again.Dying));
        Assert.Equal(scene.Tips[^1].Tip, again.Head);
    }

    [Fact]
    public void WeaponTips_PlayerOnly_SkipsOtherFightersAndCorpses_AllReadsEveryone()
    {
        // Игрок и три врага; враг места 1 убит — его оружие выпало. Всё — четыре оружия (одно выпавшее); только игрок — одно, кончик его клинка.
        var scene = new Scene(ticksPerFrame: 1, AllBurn);
        scene.Frames(5);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 1 });
        scene.Frames(30);
        var tips = new ArenaWeaponTips(scene.Clock.Link);
        var buffer = new WeaponTip[8];
        var all = buffer.AsSpan(0, tips.Read(buffer, playerOnly: false)).ToArray();
        Assert.Equal(4, all.Length);
        Assert.Equal([0, 1, 2, 3], all.Select(t => t.Slot).Order());
        Assert.True(Assert.Single(all, t => t.Slot == 1).Dropped);
        var player = Assert.Single(buffer.AsSpan(0, tips.Read(buffer, playerOnly: true)).ToArray());
        Assert.Equal((0, true, false), (player.Slot, player.IsPlayer, player.Dropped));
        Assert.Equal(scene.Tips[^1].Tip, player.Tip);
        // Буфер мал — пишет, сколько влезло, и возвращает, сколько всего.
        Assert.Equal(4, tips.Read(buffer.AsSpan(0, 2), playerOnly: false));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) tips.Read(buffer, playerOnly: true);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void MissingWeapon_NoFire_DisabledModule_NotConfigured()
    {
        // В синтетическом паке факела нет (значение в коде — torch): огня нет, имя — в missing.
        var plain = new Scene(ticksPerFrame: 2);
        plain.Frames(5);
        Assert.Equal(["torch"], plain.Fire.Fires!.Missing);
        Assert.Empty(plain.Fire.Fires.Fires.ToArray());
        Assert.Equal(10, plain.Fire.Fires.ObservedTicks);

        var off = new Scene(ticksPerFrame: 2, "modules.fire.enabled=false", AllBurn);
        off.Frames(5);
        Assert.Null(off.Fire.Fires);
        Assert.Null(off.Fire.Overlay);
        Assert.Empty(off.Modules.Active);
        Assert.Equal(["fire"], off.Modules.Inactive);
        Assert.Empty(off.Modules.Overlays);
        Assert.Empty(off.Modules.TuningGroups);
        Assert.Empty(off.Modules.Sections());
        off.Config.SkipClientSections();
        off.Config.Complete();   // секция fire выключенного модуля — не «неизвестная»
    }

    private const string Sword = "fire.weapons=[\"sword_1h\"]";
    private const string InHand = "combat.playerWeapon=sword_1h";

    [Fact]
    public void Steps_EverySecondTick_CenterOfStepTick_PauseNoSteps()
    {
        // 120 Гц: шаг сима — на чётных тиках (60 Гц по TickId), центр площадки шага — центр шишки тика шага.
        var scene = new Scene(ticksPerFrame: 3, Sword, InHand);
        var slots = scene.Fire.Slots!;
        Assert.Equal(120, slots.TickRate);
        scene.Frames(1);   // тики 0, 1, 2 — шаги на 0 и 2
        var s = slots[0];
        Assert.Equal((FireState.Burning, 2L, 2UL, 1UL), (s.State, s.Steps, s.StepTick, s.Step));
        Assert.Equal(FireStepFlags.Burning, s.StepFlags);
        for (int frame = 0; frame < 40; frame++)
        {
            scene.Frames(1);
            ulong last = scene.Sim.Tick - 1, stepTick = last & ~1UL;
            Assert.Equal((stepTick, stepTick / 2, (long)stepTick / 2 + 1), (s.StepTick, s.Step, s.Steps));
            Assert.Equal(scene.Centers[stepTick], s.StepCenter);
            Assert.Equal(scene.Centers[last], s.Center);
        }
        Assert.Equal(s.Steps, slots.StepRequests);
        // Пауза: тиков нет — шагов нет, площадка стоит.
        long steps = s.Steps;
        var center = s.StepCenter;
        scene.Paused(10);
        Assert.Equal((steps, center), (s.Steps, s.StepCenter));
        Assert.Same(s, Assert.Single(slots.Slots.ToArray(), x => x.State != FireState.Free));
    }

    [Theory]
    [InlineData(120, new ulong[] { 0, 2, 4, 6, 8, 10 })]
    [InlineData(100, new ulong[] { 0, 2, 4, 5, 7, 9, 10 })]
    [InlineData(60, new ulong[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void StepClock_ByTickId(int tickRate, ulong[] steps)
    {
        var at = Enumerable.Range(0, 11).Select(t => (ulong)t).Where(t => FireArea.IsStep(t, tickRate)).ToArray();
        Assert.Equal(steps, at);
        // Шагов за секунду тиков — 60 (при T ≥ 60).
        int perSecond = Enumerable.Range(0, tickRate).Count(t => FireArea.IsStep((ulong)t, tickRate));
        Assert.Equal(Math.Min(tickRate, FireArea.StepRate), perSecond);
    }

    [Fact]
    public void Area_FollowShare_And_OffsetDelta()
    {
        var e = new Vector3(3, 3, 3);
        // Неподвижный факел: Δ = 0 → k = 1, WOD = 0.
        Assert.Equal(1f, FireArea.Follow(Vector3.Zero, e, 368, 0.35f));
        Assert.Equal(Vector2.Zero, FireArea.OffsetDelta(1f, Quaternion.Identity, Vector3.Zero, e));
        // k = clamp(0.03/|Δтекс|, 0.35, 1): тексель 300/368 см; 0.06 текселя → 0.5; бег 4.2 м/с — 7 см за шаг (8.6 текселя) → 0.35.
        float texel = 3f / 368;
        Assert.Equal(0.5f, FireArea.Follow(new Vector3(0.06f * texel, 0, 0), e, 368, 0.35f), 1e-4f);
        var run = new Vector3(4.2f / 60, 0, 0);
        float k = FireArea.Follow(run, e, 368, 0.35f);
        Assert.Equal(0.35f, k);
        Assert.Equal(1f, FireArea.Follow(run, e, 368, 1f));
        // WOD = k·R⁻¹·Δ/E: x вида — вправо (u), y — вверх (v вниз).
        var wod = FireArea.OffsetDelta(k, Quaternion.Identity, run, e);
        Assert.Equal(0.35f * 0.07f / 3, wod.X, 1e-7f);
        Assert.Equal(0f, wod.Y);
        Assert.Equal(-0.35f * 0.07f / 3, FireArea.OffsetDelta(k, Quaternion.Identity, new Vector3(0, 0.07f, 0), e).Y, 1e-7f);
        // Поворот вида на 90° вокруг вертикали: ось x вида — мировая −z; движение вдоль взгляда (мировая x) буферы не сдвигает.
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var turned = FireArea.OffsetDelta(k, yaw, new Vector3(0, 0, -0.07f), e);
        Assert.Equal(0.35f * 0.07f / 3, turned.X, 1e-6f);
        Assert.True(FireArea.OffsetDelta(k, yaw, new Vector3(0.07f, 0, 0), e).Length() < 1e-7f);
        // Точка кисти шишки: s = 0.07·2.5·2 = 0.35 (BR §2.2: 0.33–0.35), σ = 9.496·s = 3.3 см; центр шишки — на 2 см ближе кончика.
        Assert.Equal(0.35f, FireArea.BrushScale(new FireSettings()), 1e-6f);
        Assert.Equal(new Vector3(0, 0, 0.98f), FireArea.KnobCenter(Vector3.Zero, new Vector3(0, 0, 1), 0.02f));
    }

    [Fact]
    public void Plane_UpOnScreenIsWorldUp_FacesEye_CameraPlaneStraightBelow()
    {
        // Камера 54° вниз, как RTS: у огня сбоку и внизу кадра мировая вертикаль на экране отклоняется от «вверх по экрану» — пламя
        // на площадке камеры поднимается не туда, куда дым. Площадка огня: её ось y на экране — та же, что мировая вертикаль.
        var eye = new Vector3(0, 2.4f, 1.75f);
        var f = Vector3.Normalize(-eye);
        var x = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var y = Vector3.Cross(x, f);
        var camera = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, -f.X, -f.Y, -f.Z, 0, 0, 0, 0, 1));
        var viewProj = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY) * Matrix4x4.CreatePerspectiveFieldOfView(1.05f, 16f / 9, 0.1f, 100);
        float worstCamera = 0;
        foreach (var c in new[] { new Vector3(0.9f, 0, 0.6f), new Vector3(-0.8f, 0.3f, 0.5f), new Vector3(0.4f, 0.8f, -0.6f), Vector3.Zero })
        {
            var plane = FireArea.Plane(camera, eye, c);
            Assert.Equal(1f, Vector3.Dot(Vector3.Transform(Vector3.UnitZ, plane), Vector3.Normalize(eye - c)), 1e-5f);
            float angle = ScreenAngle(viewProj, c, Vector3.Transform(Vector3.UnitY, plane), Vector3.UnitY);
            Assert.True(angle < 0.1f, $"{c}: «вверх» площадки на экране в {angle:0.00}° от мировой вертикали");
            worstCamera = MathF.Max(worstCamera, ScreenAngle(viewProj, c, Vector3.Transform(Vector3.UnitY, camera), Vector3.UnitY));
        }
        Assert.True(worstCamera > 10, $"площадка камеры отклоняется не больше {worstCamera:0.0}° — пример не показывает расхождения");
        // Луч по вертикали — площадка камеры (без скачка: переход по длине проекции вертикали); камеры нет — без поворота.
        Assert.Equal(camera, FireArea.Plane(camera, new Vector3(0, 3, 0), Vector3.Zero));
        Assert.Equal(Quaternion.Identity, FireArea.Plane(default, eye, Vector3.Zero));
    }

    private static float ScreenAngle(in Matrix4x4 viewProj, Vector3 at, Vector3 a, Vector3 b)
    {
        static Vector2 Screen(in Matrix4x4 m, Vector3 p)
        {
            var c = Vector4.Transform(new Vector4(p, 1), m);
            return new Vector2(c.X, c.Y) / c.W;
        }
        var o = Screen(viewProj, at);
        var da = Vector2.Normalize(Screen(viewProj, at + 0.01f * a) - o);
        var db = Vector2.Normalize(Screen(viewProj, at + 0.01f * b) - o);
        return MathF.Acos(Math.Clamp(Vector2.Dot(da, db), -1f, 1f)) * 180 / MathF.PI;
    }

    [Fact]
    public void Display_InterpolatedCenter_OffsetFromStepCenter()
    {
        // Кадр между шагами: показанный центр — центр шишки между двумя последними тиками по доле тика, смещение — от центра площадки шага.
        var scene = new Scene(ticksPerFrame: 1, Sword, InHand);
        scene.Frames(30);
        scene.Frames(1, alpha: 0.25f);   // тик 30 — шаг
        var s = scene.Fire.Slots![0];
        ulong last = scene.Sim.Tick - 1;
        Assert.Equal(last, s.StepTick);
        var (center, uv) = scene.Fire.Display(0);
        Assert.Equal(Vector3.Lerp(scene.Centers[last - 1], scene.Centers[last], 0.25f), center);
        Assert.Equal(FireArea.ToUv(Quaternion.Identity, center - s.StepCenter, new Vector3(3, 3, 3)), uv);
        scene.Frames(1, alpha: 0.5f);    // тик 31 — шага нет: площадка на тике 30
        last = scene.Sim.Tick - 1;
        Assert.Equal(last - 1, s.StepTick);
        (center, uv) = scene.Fire.Display(0);
        Assert.Equal(Vector3.Lerp(scene.Centers[last - 1], scene.Centers[last], 0.5f), center);
        Assert.Equal(FireArea.ToUv(Quaternion.Identity, center - scene.Centers[last - 1], new Vector3(3, 3, 3)), uv);
    }

    [Fact]
    public void Dropped_FireDies_ReleasedAfterFade_NotReignited()
    {
        // Смерть игрока: оружие выпало — огонь гаснет (кисть выключена), сим шагает 1.5 с (90 шагов), затем экземпляр свободен; выпавшее
        // оружие, чей огонь погас, снова не загорается. Сброс R — огонь заново, со сбросом поля.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var slots = scene.Fire.Slots!;
        scene.Frames(10);
        var s = slots[0];
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        scene.Frames(1);
        Assert.Equal(FireState.Dying, s.State);
        Assert.Equal(FireStepFlags.None, s.StepFlags);   // кисть выключена
        Assert.True(s.HasWeapon);
        Assert.Equal(90, slots.FadeSteps);
        int frames = 0;
        while (s.State == FireState.Dying && frames++ < 200) scene.Frames(1);
        Assert.Equal(FireState.Free, s.State);
        Assert.Equal(90, s.FadeSteps);
        Assert.Equal((1L, 0L), (slots.ReleasedByTime, slots.ReleasedByDensity));
        scene.Frames(30);
        Assert.All(slots.Slots.ToArray(), x => Assert.Equal(FireState.Free, x.State));
        Assert.True(Assert.Single(scene.Fire.Fires!.Fires.ToArray()).Dying);   // оружие лежит, огня нет

        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.Reset });
        scene.Frames(1);
        var reborn = Assert.Single(slots.Slots.ToArray(), x => x.State == FireState.Burning);
        Assert.Equal(FireStepFlags.Burning | FireStepFlags.Reset, reborn.StepFlags);
    }

    [Fact]
    public void WeaponSwitch_OldFireDies_NewFire_Limit()
    {
        // T с горящего и обратно: прежний огонь гаснет там, где пропало оружие, новый — во втором экземпляре со сбросом (горящий и
        // гаснущий). При fire.maxFires=1 новый огонь занимает гаснущий экземпляр.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var slots = scene.Fire.Slots!;
        scene.Frames(10);
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(3);
        Assert.Equal((FireState.Dying, false), (slots[0].State, slots[0].HasWeapon));
        var frozen = slots[0].Center;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(3);
        Assert.Equal((FireState.Dying, frozen), (slots[0].State, slots[0].StepCenter));
        Assert.Equal(FireState.Burning, slots[1].State);
        Assert.Equal(0, slots.Stolen);

        var one = new Scene(ticksPerFrame: 2, Sword, InHand, "fire.maxFires=1");
        var only = one.Fire.Slots!;
        one.Frames(10);
        one.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        one.Frames(3);
        one.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        one.Frames(1);
        Assert.Equal((FireState.Burning, 1L, 0L), (only[0].State, only.Stolen, only.OverLimit));
        Assert.Equal(FireStepFlags.Burning | FireStepFlags.Reset, only[0].StepFlags);
    }

    [Fact]
    public void FireObserver_SteadyTick_DoesNotAllocate()
    {
        var scene = new Scene(ticksPerFrame: 1, AllBurn);
        scene.Frames(30);
        var fires = scene.Fire.Fires!;
        ulong tick = scene.Sim.Tick - 1;
        for (int i = 0; i < 100; i++) fires.Observe(tick);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) fires.Observe(tick);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Single(fires.Fires.ToArray());

        // Экземпляры пламени и газа и заявки шага (тик шага) — тоже 0 Б: заявки кадра — в памяти, заведённой прогревом.
        var slots = scene.Fire.Slots!;
        var gas = scene.Fire.GasSlots!;
        ulong step = tick & ~1UL;
        for (int i = 0; i < 100; i++)
        {
            scene.Modules.BeginFrame(scene.Sim.Tick);
            slots.Observe(step);
            gas.Observe(step);
        }
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            scene.Modules.BeginFrame(scene.Sim.Tick);
            slots.Observe(step);
            gas.Observe(step);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Light_FollowsKnobFrameByFrame_NoFireNoLight()
    {
        // Источник — в показанном центре шишки каждого кадра (при любой доле тика) + смещение LIVE-2 в осях SWORD2 (10 см вверх и 10 см
        // вбок по +X), полная интенсивность; оружие игрока не горит — источника нет.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var light = scene.Fire.Light!;
        Assert.Equal(new Vector3(0.1f, 0.1f, 0), FireLightParams.Torch.Offset);
        float[] alphas = [1, 0.25f, 0.5f, 0.75f, 0.1f, 1];
        for (int frame = 0; frame < 30; frame++)
        {
            scene.Frames(1, alphas[frame % alphas.Length]);
            var l = light.Light(0) ?? throw new Xunit.Sdk.XunitException($"кадр {frame}: источника нет");
            Assert.Equal(scene.Fire.Display(0).Center + FireLightParams.Torch.Offset, l.Position);
            Assert.Equal((100f, 1f), (l.Intensity, light.Fade(0)));
            Assert.Null(light.Light(1));
        }
        // Кадр паузы — источник на месте.
        var still = light.Light(0)!.Value.Position;
        scene.Paused(3);
        Assert.Equal(still, light.Light(0)!.Value.Position);

        var cold = new Scene(ticksPerFrame: 2, "fire.weapons=[\"katana\"]", InHand);
        cold.Frames(20);
        Assert.NotNull(cold.Fire.Light);
        Assert.Null(cold.Fire.Light!.Light(0));
        Assert.Equal(0f, cold.Fire.Light.Fade(0));
    }

    [Fact]
    public void Light_DroppedTorch_FadesWithFire_NewFireAtFullAtOnce()
    {
        // Смерть: факел выпал — свет гаснет вместе с огнём: 0.9405^шагов с начала гашения по показанному моменту кадра, монотонно, к
        // освобождению экземпляра (90 шагов) — меньше 0.005 (без скачка); свободен — источника нет. Сброс R — новый огонь сразу полный.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var light = scene.Fire.Light!;
        var slot = scene.Fire.Slots![0];
        scene.Frames(10);
        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        scene.Frames(1, alpha: 0.5f);
        Assert.Equal(FireState.Dying, slot.State);
        float previous = light.Fade(0);
        Assert.True(previous is > 0.9f and <= 1f, $"первый кадр гашения: {previous}");
        float last = previous;
        int frames = 0;
        while (slot.State == FireState.Dying && frames++ < 200)
        {
            scene.Frames(1, alpha: 0.5f);
            if (slot.State != FireState.Dying) break;
            float fade = light.Fade(0);
            // Показанный момент — между двумя последними тиками: (последний тик + 0.5 − 1 − тик начала) тиков = ×60/120 шагов.
            double steps = (scene.Sim.Tick - 1 + 0.5 - 1 - slot.FadeTick) * 0.5;
            Assert.Equal(MathF.Pow(0.9405f, (float)steps), fade, 1e-6f);
            Assert.True(fade < previous, $"кадр {frames}: {fade} не меньше {previous}");
            Assert.Equal(100f * fade, light.Light(0)!.Value.Intensity, 1e-4f);
            Assert.Equal(scene.Fire.Display(0).Center + FireLightParams.Torch.Offset, light.Light(0)!.Value.Position);
            previous = last = fade;
        }
        Assert.Equal(FireState.Free, slot.State);
        Assert.True(last < 0.005f, $"перед освобождением свет {last}");
        Assert.Null(light.Light(0));

        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.Reset });
        scene.Frames(1);
        int burning = Array.FindIndex(scene.Fire.Slots.Slots.ToArray(), x => x.State == FireState.Burning);
        Assert.Equal(1f, light.Fade(burning));
        Assert.Equal(100f, light.Light(burning)!.Value.Intensity);
    }
    [Fact]
    public void GasWindow_AboveKnob_CentreFollowsKnob_LeadClamped_RotationCentred()
    {
        var settings = new FireSettings();
        float dt = 1f / 60, tau = settings.GasFollowSeconds;
        var knob = new Vector3(1, 1.3f, -2);
        // Стоя — окно над шишкой на GasUp: шишка на 1.92 − 1 = 0.92 м над низом окна 3.84 м.
        Assert.Equal(knob + new Vector3(0, 1, 0), FireGasSlots.WindowTarget(knob, new Vector3(knob.X, 0, knob.Z), settings));
        Vector3 Steady(Func<int, Vector3> path, int steps)
        {
            var f = new Vector3(path(0).X, 0, path(0).Z);
            for (int i = 1; i <= steps; i++) f = FireGasSlots.Follow(f, path(i), dt, settings);
            return f;
        }
        // Шаг 0.5 м/с по x — центр позади на v·(τ − dt) = 0.49 м; бег 5 м/с — 4.9 м зажато до 1.1; высота центра не важна.
        var walk = Steady(i => new Vector3(0.5f * i * dt, 1.3f + 0.1f * i, 0), 900);
        Assert.Equal(900 * 0.5f * dt - 0.5f * (tau - dt), walk.X, 2e-3f);
        var run = Steady(i => new Vector3(5 * i * dt, 1.3f, 0), 300);
        Assert.Equal(300 * 5 * dt - 1.1f, run.X, 1e-3f);
        // Вращение на месте (факел по кругу 1.15 м, 1 об/с, как в игре) — центр окна у оси, а не на шишке: шишка на пределе 1.1 м тянет его
        // на 0.28 м от оси — кольцо дыма (1.15 + 0.28) влезает в окно ±1.44 м.
        float omega = 2 * MathF.PI;
        var spin = Steady(i => 1.15f * new Vector3(MathF.Cos(omega * i * dt), 0, MathF.Sin(omega * i * dt)), 600);
        Assert.InRange(new Vector2(spin.X, spin.Z).Length(), 0.2f, 0.3f);
        // Скорость (итогу): экспонента по шагам, только горизонталь; скачок быстрее 40 м/с — прежняя.
        tau = FireGasSlots.VelocitySmoothing;
        var v = FireGasSlots.SmoothVelocity(Vector3.Zero, Vector3.Zero, new Vector3(0.1f, 0.5f, 0), dt, tau);
        Assert.Equal(6f * dt / tau, v.X, 1e-4f);
        Assert.Equal(0f, v.Y);
        Assert.Equal(v, FireGasSlots.SmoothVelocity(v, Vector3.Zero, new Vector3(1f, 0, 0), dt, tau));
    }

    [Fact]
    public void GasDrift_KnobAndCentroidPaths_OverWindow()
    {
        // Шишка по кругу 0.5 м за 60 шагов, центроид стоит — путь шишки π м, центроида 0; центроид за шишкой — доля 1. Шаги без чтения
        // (каждый второй) не рвут путь шишки.
        var drift = new FireGasDrift();
        for (ulong s = 100; s <= 160; s++)
        {
            float a = 2 * MathF.PI * (s - 100) / 60f;
            var knob = new Vector3(0.5f * MathF.Cos(a), 1, 0.5f * MathF.Sin(a));
            drift.Knob(s, knob);
            if (s % 2 == 0) drift.Centroid(s, new Vector3(0.01f, 1.5f, 0));
        }
        var d = Assert.IsType<FireGasDriftStats>(drift.Compute(60));
        Assert.Equal((100UL, 160UL, 31), (d.FromStep, d.ToStep, d.Samples));
        Assert.Equal(MathF.PI, d.KnobPath, 0.01f);
        Assert.Equal(0f, d.CentroidPath);
        Assert.Equal(0f, d.Ratio);
        drift.Reset();
        for (ulong s = 0; s <= 60; s++)
        {
            var knob = new Vector3(s * 0.05f, 1, 0);
            drift.Knob(s, knob);
            drift.Centroid(s, knob - new Vector3(0.3f, 0, 0));
        }
        d = Assert.IsType<FireGasDriftStats>(drift.Compute(60));
        Assert.Equal(1f, d.Ratio, 1e-4f);
        Assert.Equal(3f, d.KnobShift, 1e-4f);
        Assert.Null(new FireGasDrift().Compute(60));
    }

    [Fact]
    public void Gas_FollowsFire_DeathFades_WindowStays_ReleasedByTime_ResetNewInstance()
    {
        // Горит — экземпляр газа 0 с источником у шишки шага и окном над ней; смерть (факел выпал) — источник выключен, окно стоит, газ шагает
        // до предела времени (без GPU Σρ не читается), затем свободен. Сброс R — новый огонь: экземпляр со сбросом. Шаги газа — те же тики, что
        // у сима пламени.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var fire = scene.Fire.Slots!;
        var gas = scene.Fire.GasSlots!;
        scene.Frames(1);
        var g = gas[0];
        Assert.Equal((FireGasState.Emitting, FireGasFlags.Emit | FireGasFlags.Reset), (g.State, g.StepFlags));
        scene.Frames(20);
        Assert.Equal((FireGasFlags.Emit, fire[0].Step, fire[0].StepTick, fire[0].StepCenter), (g.StepFlags, g.Step, g.StepTick, g.StepKnob));
        Assert.Equal(FireGasSlots.WindowTarget(g.Knob, g.Follow, gas.Settings), g.Target);
        Assert.Equal(fire[0].Steps, g.Steps);

        scene.Pending.Add(new SceneCommand { Action = SceneAction.Kill, Slot = 0 });
        scene.Frames(1);
        Assert.Equal((FireGasState.Fading, FireGasFlags.None), (g.State, g.StepFlags));
        var target = g.Target;
        Assert.Equal(480, gas.FadeSteps);
        int frames = 0;
        while (g.State == FireGasState.Fading && frames++ < 600)
        {
            scene.Frames(1);
            if (g.State == FireGasState.Fading) Assert.Equal(target, g.Target);
        }
        Assert.Equal(FireGasState.Free, g.State);
        Assert.Equal((1L, 0L), (gas.ReleasedByTime, gas.ReleasedByDensity));
        Assert.Equal(FireState.Free, fire[0].State);   // пламя освобождено раньше (1.5 с)

        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.Reset });
        scene.Frames(1);
        var reborn = Assert.Single(gas.Slots.ToArray(), x => x.State == FireGasState.Emitting);
        Assert.Equal(FireGasFlags.Emit | FireGasFlags.Reset, reborn.StepFlags);
        Assert.Equal(0, gas.OverLimit);
    }

    [Fact]
    public void Gas_WeaponSwitch_OldSmokeFades_NewFireSecondInstance_SwitchOffFreesAll()
    {
        // T с горящего и обратно: прежний дым гаснет на месте (окно стоит), новый огонь — второй экземпляр со сбросом. Ручка «3D-дым» выкл. —
        // все свободны, заявок нет; вкл. — горящему огню новый экземпляр.
        var scene = new Scene(ticksPerFrame: 2, Sword, InHand);
        var gas = scene.Fire.GasSlots!;
        scene.Frames(10);
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(3);
        Assert.Equal(FireGasState.Fading, gas[0].State);
        var target = gas[0].Target;
        scene.PendingArena.Add(new ArenaCommand { Action = ArenaAction.NextWeapon });
        scene.Frames(3);
        Assert.Equal((FireGasState.Fading, target), (gas[0].State, gas[0].Target));
        Assert.Equal((FireGasState.Emitting, FireGasFlags.Emit), (gas[1].State, gas[1].StepFlags));
        Assert.Equal(0, gas.Stolen);

        long requests = gas.StepRequests;
        gas.Enabled = false;
        Assert.All(gas.Slots.ToArray(), s => Assert.Equal(FireGasState.Free, s.State));
        scene.Frames(5);
        Assert.Equal(requests, gas.StepRequests);
        Assert.Equal(2, gas.ReleasedBySwitch);
        gas.Enabled = true;
        scene.Frames(1);
        var s = Assert.Single(gas.Slots.ToArray(), x => x.State == FireGasState.Emitting);
        Assert.Equal(FireGasFlags.Emit | FireGasFlags.Reset, s.StepFlags);
    }
}
