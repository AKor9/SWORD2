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
/// Инварианты трупа на физике (7з; план этапа §3.3): <c>physics.modes</c> (мёртвый ⇔ труп: тела на физике, профиль трупа, суставы,
/// запястье выключено; живой ⇔ кинематика), <c>physics.simulatedSpeed</c> (скорости тел трупа — не выше потолков профиля), <c>physics.joints</c>
/// (ошибка точки и выход за предел суставов после шага), <c>physics.ground</c> (глубина тел трупа в земле), <c>physics.inArena</c> (тела
/// трупа — внутри стены и вне колонн) — после стадии Physics; диагностические (по запросу — <c>invariant.enable</c>, манифест фичи; читают
/// трассу физики — при включённом режиме <c>physics.trace</c>) — <c>physics.energy</c> (рост энергии трупа за подшаг) после Physics и <c>physics.pushes</c>
/// (свойства толчков: доли бюджетов пары и прохода, выталкивание, Δv удара) в конце End. Допуски — по замерам (README фичи «Рэгдолл», «7з»),
/// ~2× к худшему.
/// </summary>
public static class CorpseInvariants
{
    /// <summary>
    /// <c>physics.simulatedSpeed</c>: доля потолка профиля, до которой скорость тела на физике ещё не ошибка. Потолок режет скорость в
    /// обновлении скоростей подшага, а скоростной проход контактов и расслабление суставов после него — проекции в метрике масс: энергии не
    /// добавляют, но могут передать скорость лёгкому телу от тяжёлого. Угловая — поперёк оси капсулы (вращение вокруг своей оси капсулу не
    /// сдвигает, а у клинка — r 3 см — от любого удара с плечом доходит до ×1,9 потолка). Замер — README «7з», допуск ~2× к худшему.
    /// </summary>
    public const float SpeedSlack = 1.5f;

    /// <summary>
    /// <c>physics.joints</c>: ошибка точки сустава после шага, м, и выход за предел, рад. Одна итерация решателя: при падении толпы трупов
    /// друг на друга — до 28 мм и 0,66 рад (замер — README «7з»; 7в: в цепочке до 6,4 мм и 0,33 рад, сведение: куча — 37 мм и 0,37 рад).
    /// </summary>
    public const float PointTolerance = 0.06f, ExcessTolerance = 1.2f;

    /// <summary>
    /// <c>physics.ground</c>: глубина торца капсулы трупа в земле после шага, м. Застарелая глубина уходит не быстрее скорости выталкивания, а
    /// поза смерти бывает в земле: живой, стоящий у кромки ступени, держит стопу в подступенке — до 15 см (замер s8), клинок под кучей трупов
    /// вдавлен до 11 см. Допуск — ~2× к худшему: ловит провал сквозь землю, а не выход застарелой глубины.
    /// </summary>
    public const float GroundTolerance = 0.3f;

    /// <summary>
    /// <c>physics.inArena</c>: выход торца капсулы за стену или в колонну, м, без проверки выхода. Больше — только если выходит: за тик
    /// глубина уменьшилась не меньше чем на четверть <c>PopSpeed·dt</c> (в позе смерти меч или рука живого бывают в колонне — она выталкивает
    /// их не быстрее скорости выталкивания).
    /// </summary>
    public const float ArenaTolerance = 0.01f;

    /// <summary>
    /// <c>physics.energy</c>: рост энергии трупа (сочленения без толчков живых) за подшаг, Дж. Одна итерация решателя при переходе в труп, ударах
    /// о землю и друг о друга даёт всплески (замер — README «7з», пак): s8 — до 10,6 Дж, s4 — 5,1, толпа 32 трупов — 10,1 (синтетический риг тестов:
    /// s4 — 20,1, толпа — 12,8); допуск ~2× к худшему на паке — ловит «взрыв» и сустав, рывком стягивающий оторванное тело, а не мелкую накачку
    /// в куче (её ловят проверки покоя сценариев).
    /// </summary>
    public const float EnergyTolerance = 25f;

    /// <summary><c>physics.pushes</c>: запас над долей 1 бюджетов и над пределами скорости выталкивания и Δv (округление float).</summary>
    public const float PushTolerance = 1e-3f;

    public static IEnumerable<IInvariant> For(IPhysicsWorld physics, int corpseProfile) =>
    [
        new Modes(physics, corpseProfile), new SimulatedSpeed(physics), new Joints(physics), new Ground(physics, ArenaGround.Instance),
        new InArena(physics, ArenaGround.WallRadius, ArenaGround.Pillars.ToArray()), new Energy(physics), new Pushes(physics),
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
    /// (README физики, «7в»): допуски — по замерам на трупах с землёй. Ловит разорванный сустав (тело унесло), привязку не по позе смерти.
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
    /// <see cref="GroundTolerance"/> в земле арены (<see cref="IGround.Touches"/>: наибольшая глубина по граням). Ловит тело, провалившееся
    /// сквозь землю или ступень (контакты не держат). Нечисловое — дело <c>physics.finite</c>.
    /// </summary>
    private sealed class Ground(IPhysicsWorld physics, IGround ground) : IInvariant
    {
        private readonly GroundTouch[] _touches = new GroundTouch[IGround.MaxTouches];

        public string Id => "physics.ground";
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

        /// <summary>Наибольшая глубина сферы в земле, м (не касается — 0).</summary>
        public static float Depth(IGround ground, Vector3 center, float radius, Span<GroundTouch> touches)
        {
            float depth = 0;
            int n = ground.Touches(center, radius, touches);
            for (int t = 0; t < n; t++) depth = MathF.Max(depth, touches[t].Depth);
            return depth;
        }

        private string? Problem(int slot)
        {
            var b = physics.BodyAt(slot);
            if (b.IsNone) return null;
            var s = physics.Get(b);
            if (s.Mode != BodyMode.Simulated || !s.IsFinite) return null;
            float depth = MathF.Max(Depth(ground, s.EndA, s.Radius, _touches), Depth(ground, s.EndB, s.Radius, _touches));
            if (depth <= GroundTolerance) return null;
            return Text($"тело {b} (сочленение {physics.ArticulationOf(b)}) в земле на {depth:G4} м (допуск {GroundTolerance}); центр {s.Position}");
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
    /// внутри шага: наибольший рост за подшаг у сочленения, которого в подшаге не толкали живые (<see cref="PhysicsTrace.Stats.EnergyGrowth"/>),
    /// — не больше <see cref="EnergyTolerance"/>. По сочленению, а не суммой (<see cref="PhysicsTrace.Stats.EnergyGrowthTotal"/> — в сообщении):
    /// сумма растёт с числом трупов, одновременно выходящих из внахлёста (32 трупа синтетической толпы — 296 Дж в тик смерти всех при 12,8 Дж
    /// у одного), а передача энергии трупу при ударе другого трупа в замеры по сочленению уже вошла (толпа — ≤ 12,8 Дж). Меряет
    /// трасса физики — проверяется, пока включён диагностический режим <c>physics.trace</c> (7и; трасса у мира одна — инвариант её не
    /// включает и не выключает; манифест фичи включает режим ключом сценария <c>diagnostics</c>, агент — <c>physics.bodies {trace:"on"}</c>);
    /// режим выключен — проверять нечего. Ловит «взрыв» решателя и сустав, рывком стягивающий оторванное тело.
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
            if (!(last.EnergyGrowth > EnergyTolerance)) return;
            violations.Report(Text($"рост энергии трупа {last.EnergyArticulation} за подшаг {last.EnergyGrowth:G4} Дж (допуск {EnergyTolerance}); всех трупов без толчков вместе — {last.EnergyGrowthTotal:G4} Дж"));
        }
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
