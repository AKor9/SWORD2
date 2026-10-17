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
/// Инварианты трупа на физике <c>physics.*</c> — после стадии Physics. Допуски — по замерам, ~2× к худшему (текущие — README
/// фичи, «Инварианты»).
/// </summary>
public static class CorpseInvariants
{
    /// <summary>
    /// <c>physics.simulatedSpeed</c>: доля потолка профиля, до которой скорость тела ещё не ошибка: проходы контактов и суставов
    /// после потолка передают скорость лёгкому телу от тяжёлого (клинок — до ×1,9 потолка).
    /// </summary>
    public const float SpeedSlack = 1.5f;

    private static int Index(IPhysicsWorld physics, BodyHandle b)
    {
        var a = physics.ArticulationOf(b);
        if (a.IsNone) return -1;
        for (int k = 0, n = physics.BodyCount(a); k < n; k++)
            if (physics.Body(a, k) == b) return k;
        return -1;
    }

    /// <summary>
    /// <c>physics.joints</c>: ошибка точки сустава после шага, м, и выход за предел, рад. Одна итерация решателя: толпа трупов
    /// друг на друге — до 28 мм и 0,66 рад.
    /// </summary>
    public const float PointTolerance = 0.06f, ExcessTolerance = 1.2f;

    /// <summary>
    /// <c>physics.ground</c>: глубина торца капсулы трупа в земле после шага, м. Клинок, зажатый между землёй и чужим трупом,
    /// выходит медленно (до 2,7 см). Глубже допуска — только выходя (со скоростью ¼ <c>PopSpeed</c> плюс
    /// <see cref="GroundExitGrace"/>) и никогда не глубже <see cref="FreshGroundTolerance"/>.
    /// </summary>
    public const float GroundTolerance = 0.03f;

    /// <summary><c>physics.ground</c>: глубже этого — сразу, м (провал сквозь землю).</summary>
    public const float FreshGroundTolerance = 0.3f;

    /// <summary>
    /// <c>physics.ground</c>: запас времени на выход из земли, с, сверх <c>(глубина начала − допуск) / (¼ PopSpeed)</c>: выход не
    /// монотонен — труп, опрокидываясь через кромку ступени, в первые тики вдавливает стопу ещё на 8…10 мм.
    /// </summary>
    public const float GroundExitGrace = 0.25f;

    /// <summary>
    /// <c>physics.inArena</c>: выход торца капсулы за стену или в колонну, м. Больше — только выходя: за тик глубина уменьшилась не
    /// меньше чем на ¼ <c>PopSpeed·dt</c> (колонна выталкивает не быстрее скорости выталкивания).
    /// </summary>
    public const float ArenaTolerance = 0.01f;

    /// <summary>
    /// <c>physics.sleepContacts</c>: глубина входа живого или неспящего тела в капсулу спящего после шага, м: глубже
    /// <see cref="PhysicsWorld.SleepSlop"/> спящее будят; запас 5 мм — выталкивание не быстрее <see cref="PhysicsSettings.PopSpeed"/>.
    /// </summary>
    public const float SleepContactTolerance = PhysicsWorld.SleepSlop + 0.005f;

    /// <summary>
    /// <c>physics.selfContacts</c>: глубина пары самоколлизии трупа после шага, м. Лёгкая часть, зажатая тяжёлой (клинок под своим
    /// торсом), держится до 3…5 см: решатель делит поправку по массам, а земля возвращает вдавленную часть.
    /// </summary>
    public const float SelfTolerance = 0.06f;

    /// <summary><c>physics.selfContacts</c>: запас времени на выход пары из внахлёста, с, сверх <c>(глубина начала − допуск) / (¼ PopSpeed)</c>.</summary>
    public const float SelfExitGrace = 0.25f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics, int corpseProfile) =>
    [
        new Modes(physics, corpseProfile), new SimulatedSpeed(physics), new Joints(physics), new Ground(physics, ArenaGround.Instance),
        new InArena(physics, ArenaGround.WallRadius, ArenaGround.Pillars.ToArray()),
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
    /// <c>physics.modes</c> — мёртвый: 14 тел на физике с профилем трупа, 13 суставов, запястье выключено; живой: всё кинематическое.
    /// Проверка посреди тика учитывает переход, который ещё впереди (смерть до Settle, постройка до Gameplay следующего тика).
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
                    bool severed = world.TryGet<Severance>(entities[i], out var severance);
                    bool down = world.TryGet<Downed>(entities[i], out var downed);
                    string? problem = dead ? (fresh && d.Time == now ? null : severed ? SeveredCorpse(body, severance) : Corpse(body))
                        : down ? (fresh && downed.Time == now ? null : Lying(body))
                        : alive ? Alive(body, severance) : null;
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

        /// <summary>
        /// Труп с отделёнными частями: все тела (и куски) — на физике с профилем трупа; сустав запястья (если есть) выключен, сустав
        /// оторванного сегмента — тоже; прочие включены. Пары самоколлизии — по дереву после отделений, их число не сверяется.
        /// </summary>
        private string? SeveredCorpse(ArticulationHandle body, in Severance severance)
        {
            for (int k = 0, n = physics.BodyCount(body); k < n; k++)
            {
                var s = physics.Get(physics.Body(body, k));
                if (s.Mode != BodyMode.Simulated || s.Profile != corpseProfile)
                    return Text($"мёртвый, а тело {k} — {s.Mode}, профиль {s.Profile} (у трупа — Simulated, профиль {corpseProfile})");
            }
            for (int j = 0, n = physics.JointCount(body); j < n; j++)
            {
                int child = physics.Joint(body, j).Child;
                bool want = child != BodySegs.Weapon && !(child < BodySegs.Bodies && severance.IsTorn(child));
                if (physics.IsJointEnabled(body, j) != want)
                    return Text($"мёртвый с отделёнными частями, а сустав {j} (ребёнок {child}) {(want ? "выключен" : "включён")}");
            }
            return null;
        }

        /// <summary>Сбитый с ног: все тела (и куски) — на физике с профилем трупа.</summary>
        private string? Lying(ArticulationHandle body)
        {
            for (int k = 0, n = physics.BodyCount(body); k < n; k++)
            {
                var s = physics.Get(physics.Body(body, k));
                if (s.Mode != BodyMode.Simulated || s.Profile != corpseProfile)
                    return Text($"сбит с ног, а тело {k} — {s.Mode}, профиль {s.Profile} (лежащий — Simulated, профиль трупа)");
            }
            return null;
        }

        private string? Alive(ArticulationHandle body, in Severance severance)
        {
            for (int k = 0, n = physics.BodyCount(body); k < n; k++)
            {
                var s = physics.Get(physics.Body(body, k));
                // отделённое у живого — на физике с профилем трупа (кусок, ветвь на нём, меч из отрубленной руки)
                bool free = k >= BodySegs.Bodies || severance.IsDetached(k);
                if (free ? s.Mode != BodyMode.Simulated || s.Profile != corpseProfile : s.Mode != BodyMode.Kinematic || s.Profile != 0)
                    return Text($"живой, а тело {k} — {s.Mode}, профиль {s.Profile} (у живого — {(free ? "отделённое Simulated, профиль трупа" : "Kinematic, профиль 0")})");
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
    /// <c>physics.simulatedSpeed</c> — скорость и угловая скорость поперёк оси тела на физике не выше потолков профиля с запасом
    /// <see cref="SpeedSlack"/> (вращение сверх потолка — если и концы тела быстрее потолка скорости): ловит «взрыв» решателя и чужой
    /// профиль.
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
            // шар (клочок) катится: его вращение — качение, не «взрыв» решателя (на 10 м/с при радиусе 5 см — 200 рад/с); короткая
            // капсула (стопа, кусок) кувыркается так же — вращение сверх потолка не ошибка, пока концы тела не быстрее потолка скорости
            float v = s.Velocity.Length(), w = s.HalfLength > 0 ? Across(s) : 0;
            bool spin = w <= d.MaxAngularSpeed * SpeedSlack || w * (s.HalfLength + s.Radius) <= d.MaxSpeed * SpeedSlack;
            if (v <= d.MaxSpeed * SpeedSlack && spin || !float.IsFinite(v) || !float.IsFinite(w)) return null;
            return Text($"тело {b} (сочленение {physics.ArticulationOf(b)}): скорость {v:G4} м/с (потолок {d.MaxSpeed}), угловая поперёк оси {w:G4} рад/с (потолок {d.MaxAngularSpeed}); запас ×{SpeedSlack}; масса {s.Mass:G3} кг, радиус {s.Radius:G3}, полудлина {s.HalfLength:G3} м, номер в сочленении {Index(physics, b)}");
        }
    }

    /// <summary>
    /// <c>physics.joints</c> — включённые суставы трупа держат точку (≤ <see cref="PointTolerance"/>) и пределы
    /// (≤ <see cref="ExcessTolerance"/>) после шага: ловит разорванный сустав.
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
                var d = physics.Joint(a, j);
                // оба тела кинематические (живой с отделённой рукой: плечо к торсу) — сустав решатель не держит, их ставит поза
                if (physics.Get(physics.Body(a, d.Parent)).Mode == BodyMode.Kinematic && physics.Get(physics.Body(a, d.Child)).Mode == BodyMode.Kinematic)
                    continue;
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
    /// <c>physics.ground</c> — торцы капсул тел на физике не глубже <see cref="GroundTolerance"/> в земле; глубже — эпизод выхода не
    /// дольше <c>(глубина начала − допуск) / (¼ PopSpeed) + </c><see cref="GroundExitGrace"/>, глубже <see cref="FreshGroundTolerance"/> —
    /// никогда. Память — по слотам тел.
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
    /// <c>physics.selfContacts</c> — части неспящего трупа не входят друг в друга глубже <see cref="SelfTolerance"/>; глубже — только
    /// выходя, как <c>physics.ground</c>, с запасом <see cref="SelfExitGrace"/> (поза смерти бывает внахлёст). Самоколлизия выключена —
    /// не проверяется. Память — по слотам сочленений × пары.
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
    /// <c>physics.inArena</c> — тела на физике внутри стены и вне колонн; выход больше <see cref="ArenaTolerance"/> — только у тела,
    /// которое выходит. Память — по слотам тел.
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
    /// <c>physics.sleep</c> (тяжёлый) — спящий труп стоит: скорости ровно 0, прошлая поза и поза начала тика — ровно поза; при
    /// непрерывном сне поза бит в бит та же.
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
    /// <c>physics.sleepContacts</c> (тяжёлый) — пробуждение не пропущено: живое тело не входит в спящее глубже
    /// <see cref="SleepContactTolerance"/>; неспящее тело на физике — не две проверки подряд при непрерывном сне. Отсев — по сферам
    /// сочленений из нынешних поз.
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
}
