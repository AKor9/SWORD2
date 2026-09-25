using System.Globalization;
using System.Numerics;
using Sword.Engine.Ecs;
using Sword.Engine.Physics;
using Sword.Engine.Runtime;
using Sword.Game.Arena;
using Sword.Game.Bodies;
using Sword.Game.Contracts;

namespace Sword.Game.Ragdoll;

/// <summary>
/// Инварианты трупа на физике: <c>physics.modes</c> (мёртвый ⇔ труп: тела на физике, профиль трупа, суставы,
/// запястье выключено; живой ⇔ кинематика), <c>physics.simulatedSpeed</c> (скорости тел трупа — не выше потолков профиля), <c>physics.joints</c>
/// (ошибка точки и выход за предел суставов после шага), <c>physics.ground</c> (глубина тел трупа в земле), <c>physics.inArena</c> (тела
/// трупа — внутри стены и вне колонн), <c>physics.sleep</c> (спящий стоит: скорости 0, поза не меняется) и <c>physics.sleepContacts</c>
/// (в спящий никто не вошёл глубже допуска — пробуждение не пропущено), <c>physics.selfContacts</c> (части трупа не входят друг в друга
/// глубже допуска) — после стадии Physics; диагностические (по запросу — <c>invariant.enable</c>, манифест фичи; читают
/// трассу физики — при включённом режиме <c>physics.trace</c>) — <c>physics.energy</c> (рост энергии трупа за подшаг) после Physics и <c>physics.pushes</c>
/// (свойства толчков: доли бюджетов пары и прохода, выталкивание, Δv удара) в конце End. Допуски — по замерам (docs/history/ragdoll.md;
/// текущие — README фичи, «Инварианты»), ~2× к худшему.
/// </summary>
public static class CorpseInvariants
{
    /// <summary>
    /// <c>physics.simulatedSpeed</c>: доля потолка профиля, до которой скорость тела на физике ещё не ошибка. Потолок режет скорость в
    /// обновлении скоростей подшага, а скоростной проход контактов и расслабление суставов после него — проекции в метрике масс: энергии не
    /// добавляют, но могут передать скорость лёгкому телу от тяжёлого. Угловая — поперёк оси капсулы (вращение вокруг своей оси капсулу не
    /// сдвигает, а у клинка — r 3 см — от любого удара с плечом доходит до ×1,9 потолка). Замер — docs/history/ragdoll.md, допуск ~2× к худшему.
    /// </summary>
    public const float SpeedSlack = 1.5f;

    /// <summary>
    /// <c>physics.joints</c>: ошибка точки сустава после шага, м, и выход за предел, рад. Одна итерация решателя: при падении толпы трупов
    /// друг на друга — до 28 мм и 0,66 рад (замер — docs/history/ragdoll.md; в цепочке до 6,4 мм и 0,33 рад, сведение: куча — 37 мм и 0,37 рад).
    /// </summary>
    public const float PointTolerance = 0.06f, ExcessTolerance = 1.2f;

    /// <summary>
    /// <c>physics.ground</c>: глубина торца капсулы трупа в земле после шага, м, без проверки выхода. Мир после пар и трение,
    /// не топящее застарелую глубину, — тело, зажатое кучей или живым, в землю не уходит: клинок под кучей тонул до 7,8 см, стопа в толпе
    /// трупов — до 2,6 см (исправлено). Остаётся медленный выход клинка, зажатого между землёй и телом другого трупа (оба касания — застарелые:
    /// земля выводит клинок, он входит в тело трупа, пара возвращает его — 0,4…1 мм за тик): до 2,7 см (толпа сценария «Бой»). Глубже допуска —
    /// только выходя: не дольше, чем выход от глубины начала со скоростью ¼ <c>PopSpeed</c>, плюс <see cref="GroundExitGrace"/>, и никогда не
    /// глубже <see cref="FreshGroundTolerance"/> (поза смерти: живой у кромки ступени держит стопу в подступенке — до 15 см; команда сцены).
    /// </summary>
    public const float GroundTolerance = 0.03f;

    /// <summary><c>physics.ground</c>: глубже этого — сразу, м (провал сквозь землю).</summary>
    public const float FreshGroundTolerance = 0.3f;

    /// <summary>
    /// <c>physics.ground</c>: запас времени на выход из земли, с, сверх <c>(глубина начала − допуск) / (¼ PopSpeed)</c>. Выход не монотонен:
    /// труп, опрокидываясь через кромку ступени, в первые тики вдавливает стопу в подступенок ещё на 8…10 мм, и
    /// правило «глубина уменьшилась за тик не меньше чем на ¼ <c>PopSpeed·dt</c>» ловило это у 28 мест трупа из 205 у
    /// ступеней пирамиды пака. Замер проверки: без запаса — 2 места из 205 (выход дольше на 2…6 тиков), с 0,125 с — 0; толпа
    /// сценария «Бой» (4 прогона по 3600 тиков) — 0, а в master (клинок под кучей тонет) — 294…658 нарушений на прогон у 3…9 тел.
    /// </summary>
    public const float GroundExitGrace = 0.25f;

    /// <summary>
    /// <c>physics.inArena</c>: выход торца капсулы за стену или в колонну, м, без проверки выхода. Больше — только если выходит: за тик
    /// глубина уменьшилась не меньше чем на четверть <c>PopSpeed·dt</c> (в позе смерти меч или рука живого бывают в колонне — она выталкивает
    /// их не быстрее скорости выталкивания).
    /// </summary>
    public const float ArenaTolerance = 0.01f;

    /// <summary>
    /// <c>physics.energy</c>: рост энергии за подшаг трупа без толчков живых и без контакта с другим трупом, Дж (труп на трупе — энергия
    /// передаётся, рост одного — не накачка). Всплески при ударе о землю малы: унаследованный разрыв сустава
    /// сводится не быстрее <see cref="PhysicsSettings.JointPopSpeed"/>: замер (пак) — s8 ≤ 5,7 Дж (было до 17,3), s4 — 4,4, толпа 32 трупов — 6,2
    /// (было 25 Дж допуска при замерах до 10,6); проверка: 205 мест трупа у ступеней — ≤ 8,2 Дж (p95 5,8), 40 вариантов толпы 32 трупов
    /// (смерть на тиках 600…1185) — ≤ 8,9. Ловит «взрыв» и сустав, рывком стягивающий оторванное тело.
    /// </summary>
    public const float EnergyTolerance = 12.5f;

    /// <summary>
    /// <c>physics.energy</c>: рост за подшаг суммы энергий всех трупов, касавшихся другого трупа, Дж (передача внутри суммы сокращается). Замер
    /// исполнителя — ≤ 3,3 Дж на сценарии <c>crowd32</c>; проверка на 40 вариантах толпы (смерть на тиках 600…1185) — до 14,6 Дж (с
    /// допуском 12,5 — ложное нарушение у одного варианта из 40), наибольший рост одного трупа в контакте — до 11,3. Сумма — по всей арене, не по
    /// группе касавшихся: рост одной группы может скрыть убыль другой (по группам — до 14,2 Дж). Допуск ~1,7× к худшему.
    /// </summary>
    public const float PairedEnergyTolerance = 25f;

    /// <summary><c>physics.pushes</c>: запас над долей 1 бюджетов и над пределами скорости выталкивания и Δv (округление float).</summary>
    public const float PushTolerance = 1e-3f;

    /// <summary>
    /// <c>physics.sleepContacts</c>: глубина, на которую тело живого или неспящее тело на физике входит в капсулу спящего тела после шага,
    /// м. Проверка до подшагов будит спящее, в которое вошли глубже <see cref="PhysicsWorld.SleepSlop"/> (живое шаг не двигает — глубже допуска
    /// его быть не может), неспящее тело шаг выталкивает из спящего, как из земли; запас 5 мм — выталкивание застарелой глубины не быстрее
    /// <see cref="PhysicsSettings.PopSpeed"/>.
    /// </summary>
    public const float SleepContactTolerance = PhysicsWorld.SleepSlop + 0.005f;

    /// <summary>
    /// <c>physics.selfContacts</c>: глубина пары самоколлизии трупа после шага, м (капсулы с толщиной самоколлизии), без проверки выхода.
    /// Лежащий труп — доли миллиметра; держится глубже только лёгкая часть, зажатая тяжёлой: один проход решателя делит поправку по массам, а
    /// земля (мир после пар) возвращает вдавленную часть обратно — клинок под своим торсом (1,4 кг против 20) стоит на 3…5 см, как клинок под
    /// чужим трупом у земли (<see cref="GroundTolerance"/>); рука под ногой живого — выходя. Замер (пак, толпа «Боя» 20 с, 2e-6): клинок
    /// в торсе — до 5,4 см, прочие пары через 1 с после смерти — до 3,4 см (рука в торсе под ногами живых — 7,1 см, выходя), лежащий на ровной
    /// земле (синтетический риг) — 3,0 см (docs/history/ragdoll.md).
    /// </summary>
    public const float SelfTolerance = 0.06f;

    /// <summary><c>physics.selfContacts</c>: запас времени на выход пары из внахлёста, с, сверх <c>(глубина начала − допуск) / (¼ PopSpeed)</c>.</summary>
    public const float SelfExitGrace = 0.25f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics, int corpseProfile) =>
    [
        new Modes(physics, corpseProfile), new SimulatedSpeed(physics), new Joints(physics), new Ground(physics, ArenaGround.Instance),
        new InArena(physics, ArenaGround.WallRadius, ArenaGround.Pillars.ToArray()), new Energy(physics), new Pushes(physics),
        new Sleep(physics), new SleepContacts(physics), new SelfContacts(physics),
    ];

    private static string Text(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static int StageIndex(string stage)
    {
        var stages = GameStages.All;
        for (int i = 0; i < stages.Count; i++)
            if (stages[i] == stage) return i;
        return -1;   // между тиками
    }

    private static readonly int GameplayIndex = StageIndex(GameStages.Gameplay), ResolveIndex = StageIndex(GameStages.Resolve),
        ReactIndex = StageIndex(GameStages.React), AftermathIndex = StageIndex(GameStages.Aftermath), IntentIndex = StageIndex(GameStages.Intent);

    /// <summary>
    /// <c>physics.modes</c> (после Physics) — режимы тел согласованы со смертью: у мёртвого бойца все 14 тел на физике с профилем трупа, 13
    /// суставов, сустав запястья выключен, прочие включены; у живого — все тела кинематические с профилем по умолчанию, суставы (если есть)
    /// включены. Ловит смерть мимо перехода в труп, возрождение без возврата кинематики, выпавший меч у живого. Проверка «сейчас» посреди тика
    /// знает, где переход ещё впереди: смерть этого тика до Settle (Intent — до Gameplay) и постройка заново до Gameplay следующего тика.
    /// </summary>
    private sealed class Modes(IPhysicsWorld physics, int corpseProfile) : IInvariant
    {
        private ReadQuery? _fighters;

        public string Id => "physics.modes";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (все тела бойцов)
        public InvariantScope Scope => InvariantScope.Entity;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            var world = ctx.World;
            int stage = StageIndex(ctx.Stage);
            float now = world.Resource<SimTime>().Time;
            // Живые — только между Gameplay (постройка) и React: после Aftermath возрождённый ждёт постройки тела следующего тика.
            bool alive = stage >= GameplayIndex && stage <= ReactIndex;
            // Смерть этого тика — в труп в Gameplay (команда, Intent) или в Settle (удары, кровопотеря): до того её не проверять.
            bool fresh = stage == IntentIndex || stage >= ResolveIndex && stage <= AftermathIndex;
            _fighters ??= world.Query().With<Fighter>().Read<BodyRef>();
            foreach (var chunk in world.Iterate(_fighters))
            {
                var entities = chunk.Entities;
                var bodies = chunk.Read<BodyRef>();
                for (int i = 0; i < entities.Length; i++)
                {
                    var body = FighterBodies.Handle(bodies[i]);
                    if (!BodyPlacement.Placeable(physics, body)) continue;   // негодное тело — physics.bodyRefs
                    bool dead = world.TryGet<Dead>(entities[i], out var d);
                    string? problem = dead ? (fresh && d.Time == now ? null : Corpse(body)) : alive ? Alive(body) : null;
                    if (problem is null) continue;
                    violations.Report(entities[i], problem);
                    if (violations.Full) return;
                }
            }
        }

        private string? Corpse(ArticulationHandle body)
        {
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                var s = physics.Get(physics.Body(body, k));
                if (s.Mode != BodyMode.Simulated || s.Profile != corpseProfile)
                    return Text($"мёртвый, а тело {BodySegs.Name(k)} — {s.Mode}, профиль {s.Profile} (у трупа — Simulated, профиль {corpseProfile})");
            }
            int n = physics.JointCount(body);
            if (n != CorpseJoints.Count) return Text($"мёртвый, а суставов {n} (у трупа — {CorpseJoints.Count})");
            for (int j = 0; j < n; j++)
                if (physics.IsJointEnabled(body, j) != (j != CorpseJoints.Wrist))
                    return Text($"мёртвый, а сустав {j} {(j == CorpseJoints.Wrist ? "запястья включён — меч не выпал" : "выключен")}");
            // самоколлизия — таблица игры
            if (!physics.SelfContacts(body).SequenceEqual(CorpseJoints.SelfPairs))
                return Text($"мёртвый, а пар самоколлизии {physics.SelfContacts(body).Length} не по таблице трупа ({CorpseJoints.SelfPairs.Length})");
            return null;
        }

        private string? Alive(ArticulationHandle body)
        {
            for (int k = 0; k < BodySegs.Bodies; k++)
            {
                var s = physics.Get(physics.Body(body, k));
                if (s.Mode != BodyMode.Kinematic || s.Profile != 0)
                    return Text($"живой, а тело {BodySegs.Name(k)} — {s.Mode}, профиль {s.Profile} (у живого — Kinematic, профиль 0)");
            }
            for (int j = 0, n = physics.JointCount(body); j < n; j++)
                if (!physics.IsJointEnabled(body, j)) return Text($"живой, а сустав {j} выключен");
            return null;
        }
    }

    /// <summary>Угловая скорость тела поперёк оси капсулы, рад/с (вращение вокруг своей оси капсулу не сдвигает).</summary>
    public static float Across(in BodyState s)
    {
        var axis = s.Axis;
        return (s.AngularVelocity - axis * Vector3.Dot(s.AngularVelocity, axis)).Length();
    }

    /// <summary>
    /// <c>physics.simulatedSpeed</c> (после Physics) — скорость и угловая скорость поперёк оси каждого тела на физике не выше потолков его
    /// профиля (<see cref="BodyDynamics.MaxSpeed"/>, <see cref="BodyDynamics.MaxAngularSpeed"/>) с запасом <see cref="SpeedSlack"/>. Ловит
    /// «взрыв» решателя, который потолок не удержал, и тело с чужим профилем. Нечисловое — дело <c>physics.finite</c>.
    /// </summary>
    private sealed class SimulatedSpeed(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.simulatedSpeed";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            for (int i = 0, n = physics.BodyCapacity; i < n; i++)
            {
                if (Problem(i) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(int slot)
        {
            var b = physics.BodyAt(slot);
            if (b.IsNone) return null;
            var s = physics.Get(b);
            if (s.Mode != BodyMode.Simulated) return null;
            var d = physics.GetDynamics(s.Profile);
            float v = s.Velocity.Length(), w = Across(s);
            if (v <= d.MaxSpeed * SpeedSlack && w <= d.MaxAngularSpeed * SpeedSlack || !float.IsFinite(v) || !float.IsFinite(w)) return null;
            return Text($"тело {b} (сочленение {physics.ArticulationOf(b)}): скорость {v:G4} м/с (потолок {d.MaxSpeed}), угловая поперёк оси {w:G4} рад/с (потолок {d.MaxAngularSpeed}); запас ×{SpeedSlack}");
        }
    }

    /// <summary>
    /// <c>physics.joints</c> (после Physics) — у сочленений с телами на физике каждый включённый сустав держит точку (ошибка ≤
    /// <see cref="PointTolerance"/>) и пределы (выход ≤ <see cref="ExcessTolerance"/>) после шага. Один проход решателя держит их не точно
    /// (docs/history/engine-physics.md): допуски — по замерам на трупах с землёй. Ловит разорванный сустав (тело унесло), привязку не по позе смерти.
    /// </summary>
    private sealed class Joints(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.joints";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            for (int i = 0, n = physics.ArticulationCapacity; i < n; i++)
            {
                if (Problem(i) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(int slot)
        {
            var a = physics.ArticulationAt(slot);
            if (a.IsNone || physics.JointCount(a) == 0 || !AnySimulated(physics, a)) return null;
            for (int j = 0, n = physics.JointCount(a); j < n; j++)
            {
                if (!physics.IsJointEnabled(a, j)) continue;
                var m = physics.MeasureJoint(a, j);
                if (m.PointError <= PointTolerance && m.Excess <= ExcessTolerance || !float.IsFinite(m.PointError) || !float.IsFinite(m.Excess)) continue;
                var desc = physics.Joint(a, j);
                return Text($"сочленение {a}, сустав {j} ({BodySegs.Name(desc.Parent)} → {BodySegs.Name(desc.Child)}, {desc.Kind}): ошибка точки {m.PointError:G4} м (допуск {PointTolerance}), выход за предел {m.Excess:G4} рад (допуск {ExcessTolerance})");
            }
            return null;
        }
    }

    private static bool AnySimulated(IPhysicsWorld physics, ArticulationHandle a)
    {
        for (int k = 0, n = physics.BodyCount(a); k < n; k++)
            if (physics.Get(physics.Body(a, k)).Mode == BodyMode.Simulated) return true;
        return false;
    }

    /// <summary>
    /// <c>physics.ground</c> (после Physics) — торцы капсул тел на физике (сферы концов оси, как их видит физика) не глубже
    /// <see cref="GroundTolerance"/> в земле арены (<see cref="IGround.Touches"/>: наибольшая глубина по граням); глубже — только выходя:
    /// эпизод «глубже допуска» (с проверки, где тело глубже допуска, а прошлая проверка того же тела — в допуске или её нет) длится не дольше
    /// <c>(глубина начала − допуск) / (¼ PopSpeed) + </c><see cref="GroundExitGrace"/>; глубже <see cref="FreshGroundTolerance"/> — никогда.
    /// Память — по слотам тел (поколение, тик, начало эпизода и его глубина). Ловит тело, провалившееся сквозь землю или ступень, и тело, которое
    /// держит в земле (куча или живой вдавливает). Нечисловое — дело <c>physics.finite</c>.
    /// </summary>
    private sealed class Ground(IPhysicsWorld physics, IGround ground) : IInvariant
    {
        private readonly GroundTouch[] _touches = new GroundTouch[IGround.MaxTouches];

        /// <summary>По слотам тел: поколение, тик проверки, тик начала эпизода «глубже допуска» и глубина начала (0 — тело в допуске).</summary>
        private (int Generation, ulong Tick, ulong Start, float StartDepth)[] _last = [];

        public string Id => "physics.ground";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (_last.Length < physics.BodyCapacity) Array.Resize(ref _last, physics.BodyCapacity);
            float exit = 0.25f * physics.Settings.PopSpeed;
            for (int i = 0, n = physics.BodyCapacity; i < n; i++)
            {
                if (Problem(i, ctx.Tick, ctx.Dt, exit) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        /// <summary>Наибольшая глубина сферы в земле, м (не касается — 0).</summary>
        public static float Depth(IGround ground, Vector3 center, float radius, Span<GroundTouch> touches)
        {
            float depth = 0;
            int n = ground.Touches(center, radius, touches);
            for (int t = 0; t < n; t++) depth = MathF.Max(depth, touches[t].Depth);
            return depth;
        }

        private string? Problem(int slot, ulong tick, float dt, float exit)
        {
            var b = physics.BodyAt(slot);
            if (b.IsNone) return null;
            var s = physics.Get(b);
            if (s.Mode != BodyMode.Simulated || !s.IsFinite) return null;
            float depth = MathF.Max(Depth(ground, s.EndA, s.Radius, _touches), Depth(ground, s.EndB, s.Radius, _touches));
            var last = _last[slot];
            if (depth <= GroundTolerance)
            {
                _last[slot] = (b.Generation, tick, 0, 0);
                return null;
            }
            // эпизод «глубже допуска» начинается здесь, если прошлая проверка того же тела — в допуске, её нет или она не раньше (перемотка)
            if (last.Generation != b.Generation || last.Tick >= tick || !(last.StartDepth > 0)) last = (b.Generation, tick, tick, depth);
            _last[slot] = (b.Generation, tick, last.Start, last.StartDepth);
            float elapsed = (tick - last.Start) * dt;
            float allowed = exit > 0 ? (last.StartDepth - GroundTolerance) / exit + GroundExitGrace : float.PositiveInfinity;
            if (depth <= FreshGroundTolerance && elapsed <= allowed) return null;
            return Text($"тело {b} (сочленение {physics.ArticulationOf(b)}) в земле на {depth:G4} м {elapsed:G3} с (с {last.StartDepth:G4} м; допуск {GroundTolerance}, выход — не дольше {allowed:G3} с, глубже {FreshGroundTolerance} — сразу); центр {s.Position}");
        }
    }

    /// <summary>
    /// <c>physics.selfContacts</c> (после Physics) — части неспящего трупа не входят друг в друга: у каждой пары самоколлизии, где оба
    /// тела на физике, глубина капсул (радиусы × <see cref="PhysicsSettings.SelfContactScale"/>) не больше <see cref="SelfTolerance"/>; глубже —
    /// только выходя (как <c>physics.ground</c>): эпизод «глубже допуска» длится не дольше <c>(глубина начала − допуск) / (¼ PopSpeed) + </c>
    /// <see cref="SelfExitGrace"/> (поза смерти бывает внахлёст — в позах живого клинок у головы, рука у торса до 15 см, 0,1…2 % поз; пара
    /// расходится не быстрее скорости выталкивания). Порога «глубже — сразу», как у земли, нет: сумма радиусов пары трупа — не больше 0,28 м.
    /// Самоколлизия выключена (толщина 0) — не проверяется. Память — по слотам сочленений × пары. Ловит руку или ногу, прошедшую сквозь своё
    /// тело (пара пропущена, таблица пуста, самоколлизия не решается).
    /// </summary>
    private sealed class SelfContacts(IPhysicsWorld physics) : IInvariant
    {
        /// <summary>По слотам сочленений × номер пары: поколение, тик проверки, тик начала эпизода и глубина начала (0 — в допуске).</summary>
        private (int Generation, ulong Tick, ulong Start, float StartDepth)[] _last = [];

        private static int Stride => CorpseJoints.SelfPairs.Length;

        public string Id => "physics.selfContacts";
        public InvariantCost Cost => InvariantCost.Heavy;   // в живой игре — раз в 4 тика (все пары всех трупов)
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            float scale = physics.Settings.SelfContactScale;
            if (!(scale > 0)) return;
            int need = physics.ArticulationCapacity * Stride;
            if (_last.Length < need) Array.Resize(ref _last, need);
            float exit = 0.25f * physics.Settings.PopSpeed;
            for (int i = 0, n = physics.ArticulationCapacity; i < n; i++)
            {
                if (Problem(i, ctx.Tick, ctx.Dt, exit, scale) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(int slot, ulong tick, float dt, float exit, float scale)
        {
            var a = physics.ArticulationAt(slot);
            if (a.IsNone || physics.IsAsleep(a)) return null;
            var pairs = physics.SelfContacts(a);
            for (int k = 0; k < pairs.Length && k < Stride; k++)
                if (Pair(a, slot * Stride + k, pairs[k], tick, dt, exit, scale) is { } problem) return problem;
            return null;
        }

        private string? Pair(ArticulationHandle a, int at, BodyPair p, ulong tick, float dt, float exit, float scale)
        {
            var sa = physics.Get(physics.Body(a, p.A));
            var sb = physics.Get(physics.Body(a, p.B));
            if (sa.Mode != BodyMode.Simulated || sb.Mode != BodyMode.Simulated || !sa.IsFinite || !sb.IsFinite) return null;
            float depth = SelfOverlap.Depth(sa, sb, scale);
            var last = _last[at];
            if (depth <= SelfTolerance)
            {
                _last[at] = (a.Generation, tick, 0, 0);
                return null;
            }
            // эпизод «глубже допуска» начинается здесь, если прошлая проверка той же пары — в допуске, её нет или она не раньше (перемотка)
            if (last.Generation != a.Generation || last.Tick >= tick || !(last.StartDepth > 0)) last = (a.Generation, tick, tick, depth);
            _last[at] = (a.Generation, tick, last.Start, last.StartDepth);
            float elapsed = (tick - last.Start) * dt;
            float allowed = exit > 0 ? (last.StartDepth - SelfTolerance) / exit + SelfExitGrace : float.PositiveInfinity;
            if (elapsed <= allowed) return null;
            return Text($"сочленение {a}: {BodySegs.Name(p.A)} в {BodySegs.Name(p.B)} на {depth:G4} м {elapsed:G3} с (с {last.StartDepth:G4} м; допуск {SelfTolerance}, выход — не дольше {allowed:G3} с)");
        }
    }

    /// <summary>
    /// <c>physics.inArena</c> (после Physics) — тела на физике внутри стены арены (торцы капсулы — не дальше радиуса стены с радиусом тела) и
    /// вне колонн (отрезок оси — не ближе радиуса колонны с радиусом тела): выход больше <see cref="ArenaTolerance"/> — только у тела, которое
    /// выходит (с прошлой проверки глубина уменьшилась не меньше чем на четверть <c>PopSpeed·dt</c> за тик; первая проверка тела на физике —
    /// без памяти). Память — по слотам тел (поколение, тик, выход). Ловит тело, пролетевшее сквозь стену или колонну или застрявшее в ней.
    /// </summary>
    private sealed class InArena(IPhysicsWorld physics, float wall, Pillar[] pillars) : IInvariant
    {
        private (int Generation, ulong Tick, float Excess)[] _last = [];

        public string Id => "physics.inArena";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (_last.Length < physics.BodyCapacity) Array.Resize(ref _last, physics.BodyCapacity);
            float exit = 0.25f * physics.Settings.PopSpeed * ctx.Dt;
            for (int i = 0, n = physics.BodyCapacity; i < n; i++)
            {
                if (Problem(i, ctx.Tick, exit) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(int slot, ulong tick, float exit)
        {
            var b = physics.BodyAt(slot);
            if (b.IsNone) return null;
            var s = physics.Get(b);
            if (s.Mode != BodyMode.Simulated || !s.IsFinite) return null;
            Vector3 a = s.EndA, e = s.EndB;
            float excess = MathF.Max(Flat(a).Length(), Flat(e).Length()) + s.Radius - wall;
            int pillar = -1;
            for (int i = 0; i < pillars.Length; i++)
            {
                var p = pillars[i];
                float inside = p.Radius + s.Radius - Distance(new Vector2(p.X, p.Z), Flat(a), Flat(e));
                if (inside > excess) (excess, pillar) = (inside, i);
            }
            var last = _last[slot];
            _last[slot] = (b.Generation, tick, excess);
            if (excess <= ArenaTolerance) return null;
            // выходит: с прошлой проверки (того же тела) глубина уменьшилась не меньше чем на четверть выталкивания за прошедшие тики
            if (last.Generation != b.Generation || last.Tick >= tick) return null;
            if (excess <= last.Excess - exit * (tick - last.Tick)) return null;
            string where = pillar < 0 ? "за стеной" : Text($"в колонне ({pillars[pillar].X}, {pillars[pillar].Z})");
            return Text($"тело {b} (сочленение {physics.ArticulationOf(b)}) {where} на {excess:G4} м и не выходит (было {last.Excess:G4} м); центр {s.Position}");
        }

        private static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

        /// <summary>Расстояние от точки до отрезка в плоскости XZ.</summary>
        private static float Distance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float l2 = ab.LengthSquared();
            float t = l2 > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0, 1) : 0;
            return Vector2.Distance(p, a + ab * t);
        }
    }

    /// <summary>
    /// <c>physics.energy</c> (после Physics; диагностический — по запросу) — энергия трупа (кинетическая + m·g·y) не растёт скачком за подшаг
    /// внутри шага (честно, без передачи между трупами): (1) наибольший рост за подшаг у сочленения, которого в подшаге не толкали живые и
    /// которое не касалось другого тела на физике (<see cref="PhysicsTrace.Stats.EnergyGrowth"/>), и (2) наибольший рост суммы всех касавшихся друг
    /// друга трупов за подшаг (<see cref="PhysicsTrace.Stats.EnergyGrowthPairedTotal"/>: удар трупа о труп передаёт энергию — рост одного не
    /// накачка, в сумме передача сокращается) — не больше <see cref="EnergyTolerance"/> и <see cref="PairedEnergyTolerance"/> соответственно.
    /// Рост по фазам подшага — в сообщении (где энергия
    /// появилась: суставы, пары, мир, скоростной проход). Меряет трасса физики — проверяется, пока включён диагностический режим
    /// <c>physics.trace</c> (трасса у мира одна — инвариант её не включает и не выключает; манифест фичи включает режим ключом сценария
    /// <c>diagnostics</c>, агент — <c>physics.bodies {trace:"on"}</c>); режим выключен — проверять нечего. Ловит «взрыв» решателя и сустав,
    /// рывком стягивающий оторванное тело.
    /// </summary>
    private sealed class Energy(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.energy";
        public InvariantCost Cost => InvariantCost.OnDemand;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (physics.Trace is not { } trace) return;   // режим physics.trace выключен
            var last = trace.Last;
            if (last.EnergyGrowth > EnergyTolerance)
                violations.Report(Text($"рост энергии трупа {last.EnergyArticulation} за подшаг {last.EnergyGrowth:G4} Дж (допуск {EnergyTolerance}); {Phases(last)}"));
            if (last.EnergyGrowthPairedTotal > PairedEnergyTolerance)
                violations.Report(Text($"рост энергии трупов в контакте друг с другом (сумма) за подшаг {last.EnergyGrowthPairedTotal:G4} Дж (допуск {PairedEnergyTolerance}; наибольший у одного — {last.EnergyGrowthPaired:G4} Дж, {last.EnergyPairedArticulation}); {Phases(last)}"));
        }

        private static string Phases(PhysicsTrace.Stats s)
        {
            var sb = new System.Text.StringBuilder("по фазам:");
            for (int p = 0; p < PhysicsTrace.PhaseCount; p++)
                sb.Append(CultureInfo.InvariantCulture, $" {PhysicsTrace.PhaseName((PhysicsPhase)p)} {s.PhaseGrowth((PhysicsPhase)p):G3};");
            return sb.ToString();
        }
    }

    /// <summary>
    /// <c>physics.sleep</c> (после Physics, тяжёлый) — спящий труп стоит: у каждого тела спящего сочленения скорость и угловая скорость —
    /// ровно 0, прошлая поза и поза начала тика — ровно поза (клиент не качает его интерполяцией), тело — на физике; спал на прошлой проверке и
    /// спит сейчас непрерывно (счётчик покоя не убывал) — поза бит в бит та же. Память — по слотам тел (поколение, тик, счётчик покоя, поза).
    /// Ловит шаг, который двигает спящее, и сон без обнуления скоростей.
    /// </summary>
    private sealed class Sleep(IPhysicsWorld physics) : IInvariant
    {
        /// <summary>По слотам тел: поколение, тик проверки, счётчик покоя сочленения, поза (у неспящего — поколение 0).</summary>
        private (int Generation, ulong Tick, float RestTime, Vector3 Position, Quaternion Rotation)[] _last = [];

        public string Id => "physics.sleep";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (_last.Length < physics.BodyCapacity) Array.Resize(ref _last, physics.BodyCapacity);
            for (int i = 0, n = physics.ArticulationCapacity; i < n; i++)
            {
                if (Problem(i, ctx.Tick) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        private string? Problem(int slot, ulong tick)
        {
            var a = physics.ArticulationAt(slot);
            if (a.IsNone) return null;
            bool asleep = physics.IsAsleep(a);
            float rest = physics.RestTime(a);
            string? problem = null;
            for (int k = 0, n = physics.BodyCount(a); k < n; k++)
            {
                var b = physics.Body(a, k);
                var s = physics.Get(b);
                var last = _last[b.Index];
                _last[b.Index] = asleep ? (b.Generation, tick, rest, s.Position, s.Rotation) : default;
                if (!asleep || problem is not null) continue;
                if (s.Mode != BodyMode.Simulated) problem = Text($"спящий {a}: тело {BodySegs.Name(k)} — {s.Mode}");
                else if (s.Velocity != Vector3.Zero || s.AngularVelocity != Vector3.Zero)
                    problem = Text($"спящий {a}: тело {BodySegs.Name(k)} со скоростью {s.Velocity} м/с, {s.AngularVelocity} рад/с (у спящего — 0)");
                else if (s.PrevPosition != s.Position || s.StartPosition != s.Position || s.PrevRotation != s.Rotation || s.StartRotation != s.Rotation)
                    problem = Text($"спящий {a}: тело {BodySegs.Name(k)} — прошлая поза или поза начала тика не поза (сдвиг {Vector3.Distance(s.StartPosition, s.Position):G3} м)");
                else if (last.Generation == b.Generation && last.Tick < tick && rest >= last.RestTime && (last.Position != s.Position || last.Rotation != s.Rotation))
                    problem = Text($"спящий {a} сдвинулся во сне: тело {BodySegs.Name(k)} на {Vector3.Distance(last.Position, s.Position):G3} м с тика {last.Tick}");
            }
            return problem;
        }
    }

    /// <summary>
    /// <c>physics.sleepContacts</c> (после Physics, тяжёлый) — пробуждение не пропущено: тело живого (кинематическое) не входит в капсулу
    /// тела спящего сочленения глубже <see cref="SleepContactTolerance"/> (проверка до подшагов будит спящее, в которое вошли глубже
    /// <see cref="PhysicsWorld.SleepSlop"/>, а живое шаг не двигает — глубже быть не может); неспящее тело на физике — не две проверки подряд при
    /// непрерывном сне (шаг выталкивает его из спящего, как из земли; зажатое между спящим и землёй или двумя спящими может остаться глубже до
    /// конца шага — тогда его разбудит проверка до подшагов следующего). Отсев — по сферам сочленений из нынешних поз тел (границы физики — конца
    /// шага, а тело могли поставить и после). Память — по слотам сочленений (поколение, тик проверки, счётчик покоя). Ловит «не разбудили»:
    /// живой идёт сквозь спящий труп, труп лёг в спящий.
    /// </summary>
    private sealed class SleepContacts(IPhysicsWorld physics) : IInvariant
    {
        /// <summary>По слотам сочленений: спящее, в которое вошло неспящее тело на физике глубже допуска, — поколение, тик проверки, счётчик покоя.</summary>
        private (int Generation, ulong Tick, float RestTime)[] _pierced = [];
        private ulong _lastCheck = ulong.MaxValue;

        public string Id => "physics.sleepContacts";
        public InvariantCost Cost => InvariantCost.Heavy;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.Physics;

        /// <summary>Сферы сочленений по нынешним позам тел (центр — среднее центров, радиус — до дальней капсулы): границы физики — конца шага.</summary>
        private (Vector3 Center, float Radius)[] _spheres = [];

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            ulong previous = _lastCheck;
            _lastCheck = ctx.Tick;
            if (physics.AsleepCount == 0) return;
            if (_pierced.Length < physics.ArticulationCapacity) Array.Resize(ref _pierced, physics.ArticulationCapacity);
            if (_spheres.Length < physics.ArticulationCapacity) Array.Resize(ref _spheres, physics.ArticulationCapacity);
            for (int i = 0, n = physics.ArticulationCapacity; i < n; i++) _spheres[i] = Sphere(physics.ArticulationAt(i));
            for (int i = 0, n = physics.ArticulationCapacity; i < n; i++)
            {
                var s = physics.ArticulationAt(i);
                if (s.IsNone || !physics.IsAsleep(s)) continue;
                if (Problem(s, ctx.Tick, previous) is not { } problem) continue;
                violations.Report(problem);
                if (violations.Full) return;
            }
        }

        /// <summary>Спящее <paramref name="s"/> против всех неспящих (тело цикла по спящим — свой метод).</summary>
        private (Vector3 Center, float Radius) Sphere(ArticulationHandle a)
        {
            if (a.IsNone) return default;
            int n = physics.BodyCount(a);
            var sum = Vector3.Zero;
            for (int k = 0; k < n; k++) sum += physics.Get(physics.Body(a, k)).Position;
            var center = sum / n;
            float r = 0;
            for (int k = 0; k < n; k++)
            {
                var s = physics.Get(physics.Body(a, k));
                r = MathF.Max(r, Vector3.Distance(s.Position, center) + s.HalfLength + s.Radius);
            }
            return (center, r);
        }

        private string? Problem(ArticulationHandle s, ulong tick, ulong previous)
        {
            var bs = _spheres[s.Index];
            float rest = physics.RestTime(s);
            var last = _pierced[s.Index];
            string? found = null;
            bool pierced = false;
            for (int j = 0, n = physics.ArticulationCapacity; j < n && found is null; j++)
            {
                var w = physics.ArticulationAt(j);
                if (w.IsNone || w == s || physics.IsAsleep(w)) continue;
                var bw = _spheres[j];
                if (Vector3.Distance(bs.Center, bw.Center) >= bs.Radius + bw.Radius) continue;
                if (Deepest(s, w) is not { Depth: > SleepContactTolerance } d) continue;
                string what = Text($"в спящий {s} вошло тело {(d.Kinematic ? "живого" : "неспящего трупа")} {w} ({BodySegs.Name(d.Other)} в {BodySegs.Name(d.Body)}) на {d.Depth:G3} м (допуск {SleepContactTolerance})");
                if (d.Kinematic) found = what + ": пробуждение пропущено";
                else
                {
                    pierced = true;
                    // вторая проверка подряд при непрерывном сне — проверка до подшагов его не разбудила
                    if (last.Generation == s.Generation && last.Tick == previous && previous < tick && rest >= last.RestTime)
                        found = what + Text($" и на прошлой проверке (тик {previous}): пробуждение пропущено");
                }
            }
            _pierced[s.Index] = pierced ? (s.Generation, tick, rest) : default;
            return found;
        }

        private (float Depth, int Body, int Other, bool Kinematic) Deepest(ArticulationHandle s, ArticulationHandle w)
        {
            (float Depth, int Body, int Other, bool Kinematic) worst = default;
            for (int x = 0, ns = physics.BodyCount(s); x < ns; x++)
            {
                var a = physics.Get(physics.Body(s, x));
                for (int y = 0, nw = physics.BodyCount(w); y < nw; y++)
                {
                    var b = physics.Get(physics.Body(w, y));
                    float depth = a.Radius + b.Radius - Distance(in a, in b);
                    if (depth > worst.Depth) worst = (depth, x, y, b.Mode != BodyMode.Simulated);
                }
            }
            return worst;
        }

        /// <summary>Расстояние между осями капсул, м (первым — более короткий отрезок, как у физики).</summary>
        private static float Distance(in BodyState a, in BodyState b) => b.HalfLength < a.HalfLength
            ? MathF.Sqrt(Sword.Engine.Geometry.Segments.SegSeg(b.EndA, b.EndB, a.EndA, a.EndB, out _, out _))
            : MathF.Sqrt(Sword.Engine.Geometry.Segments.SegSeg(a.EndA, a.EndB, b.EndA, b.EndB, out _, out _));
    }

    /// <summary>
    /// <c>physics.pushes</c> (конец End; диагностический — по запросу) — свойства толчков живых и ударов за последний тик (трасса физики
    /// <see cref="PhysicsTrace.Last"/>): доля бюджета пары за подшаг и доля бюджета прохода — не больше 1; выталкивание точки сверх
    /// толкающей — не больше <see cref="PhysicsSettings.PopSpeed"/>; Δv точки удара — не больше доли <see cref="PhysicsSettings.HitDvShare"/>
    /// скорости удара. Проверяется, пока включён диагностический режим <c>physics.trace</c> (как <c>physics.energy</c>).
    /// </summary>
    private sealed class Pushes(IPhysicsWorld physics) : IInvariant
    {
        public string Id => "physics.pushes";
        public InvariantCost Cost => InvariantCost.OnDemand;
        public InvariantScope Scope => InvariantScope.Subsystem;
        public string After => GameStages.End;

        public void Evaluate(in InvariantContext ctx, ref ViolationSink violations)
        {
            if (physics.Trace is not { } trace) return;   // режим physics.trace выключен
            var last = trace.Last;
            var settings = physics.Settings;
            if (last.PairShare > 1 + PushTolerance)
                violations.Report(Text($"толчок пары за подшаг — {last.PairShare:G4} бюджета (не больше 1)"));
            if (last.PassShare > 1 + PushTolerance)
                violations.Report(Text($"импульс прохода — {last.PassShare:G4} бюджета прохода (не больше 1)"));
            if (last.PushPopSpeed > settings.PopSpeed + PushTolerance)
                violations.Report(Text($"выталкивание толчком {last.PushPopSpeed:G4} м/с сверх толкающей точки (не больше {settings.PopSpeed})"));
            if (last.HitDvShare > settings.HitDvShare + PushTolerance)
                violations.Report(Text($"Δv удара — {last.HitDvShare:G4} скорости удара (не больше {settings.HitDvShare})"));
        }
    }
}
