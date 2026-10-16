using System.Numerics;
using Sword.Engine.Geometry;
using Xunit.Abstractions;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Самоколлизия (<see cref="IPhysicsWorld.SetSelfContacts"/>): таблица пар без выделений, новое сочленение в слоте — без пар;
/// пара тел одного сочленения расходится как пара разных. Труп (<see cref="JointsRig.Fighter"/>) в любой позе, через кромку и
/// в куче не складывается сам в себя; без самоколлизии на боку — рука в торсе.
/// </summary>
public class SelfContactsTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 120;

    /// <summary>Родитель тела бойца теста по номеру (дерево <see cref="JointsRig.FighterJoints"/>).</summary>
    private static readonly int[] Parent = [-1, 0, 1, 1, 3, 1, 5, 0, 7, 0, 9, 6, 8, 10];

    /// <summary>Пары бойца теста — правило таблицы трупа игры (<c>CorpseJoints.SelfPairs</c>): все пары, кроме связанных суставом и торса с бёдрами.</summary>
    internal static BodyPair[] FighterPairs()
    {
        var list = new List<BodyPair>();
        for (int a = 0; a < 14; a++)
            for (int b = a + 1; b < 14; b++)
                if (Parent[b] != a && Parent[a] != b && !(a == 1 && b is 7 or 9)) list.Add(new BodyPair(a, b));
        return [.. list];
    }

    [Fact]
    public void SetSelfContacts_Validates_Replaces_ReservedNoAllocation_NewSlotEmpty()
    {
        var w = new PhysicsWorld();
        var art = w.CreateArticulation(4);
        Assert.Equal(0, w.SelfContacts(art).Length);
        BodyPair[] good = [new(0, 2), new(3, 1), new(0, 3)];
        w.SetSelfContacts(art, good);
        Assert.Equal(good, w.SelfContacts(art).ToArray());
        foreach (BodyPair[] bad in (BodyPair[][])[[new(0, 4)], [new(-1, 2)], [new(2, 2)], [new(0, 1), new(1, 0)], [new(1, 2), new(1, 2)]])
        {
            Assert.Throws<ArgumentException>(() => w.SetSelfContacts(art, bad));
            Assert.Equal(good, w.SelfContacts(art).ToArray());   // негодное — прежние не меняются
        }
        BodyPair[] all = [new(0, 1), new(0, 2), new(0, 3), new(1, 2), new(1, 3), new(2, 3)];
        long before = GC.GetAllocatedBytesForCurrentThread();
        w.SetSelfContacts(art, all);   // все пары сочленения — место с создания
        w.SetSelfContacts(art, good);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(good, w.SelfContacts(art).ToArray());
        w.DestroyArticulation(art);
        var again = w.CreateArticulation(4);
        Assert.Equal(art.Index, again.Index);
        Assert.Equal(0, w.SelfContacts(again).Length);   // слот пар не наследует
        Assert.Throws<InvalidOperationException>(() => w.SelfContacts(art));
    }

    /// <summary>Два шара одного сочленения (без суставов, без гравитации) внахлёст на 10 см.</summary>
    private static (PhysicsWorld World, ArticulationHandle Art, BodyHandle A, BodyHandle B) Overlapping(PhysicsSettings settings)
    {
        var w = new PhysicsWorld(settings: settings);
        w.ReplaceDynamics(0, TestBodies.Vacuum);
        var art = w.CreateArticulation(2);
        var a = JointsRig.Capsule(w, art, 0, 4, 0.15f, 0, Vector3.Zero, Quaternion.Identity, BodyMode.Simulated);
        var b = JointsRig.Capsule(w, art, 1, 2, 0.15f, 0, new Vector3(0.2f, 0, 0), Quaternion.Identity, BodyMode.Simulated);
        return (w, art, a, b);
    }

    private static float Gap(PhysicsWorld w, BodyHandle a, BodyHandle b) => Vector3.Distance(w.Get(a).Position, w.Get(b).Position) - 0.3f;

    [Fact]
    public void SelfPair_SeparatesNoFasterThanPopSpeed_MomentumKept_ScaleZeroOff_KinematicSkipped()
    {
        var settings = PhysicsSettings.Default with { PopSpeed = 1 };
        var (w, art, a, b) = Overlapping(settings);
        // без пар — свои тела не сталкиваются
        for (int t = 0; t < 12; t++) w.Step(Dt);
        Assert.Equal(-0.1f, Gap(w, a, b), 4);

        w.SetSelfContacts(art, [new BodyPair(0, 1)]);
        float prev = Gap(w, a, b);
        for (int t = 0; t < 60; t++)
        {
            w.Step(Dt);
            float gap = Gap(w, a, b);
            Assert.True(gap - prev <= settings.PopSpeed * Dt * 1.001f, $"расходятся быстрее PopSpeed: {(gap - prev) / Dt} м/с");
            prev = gap;
            var p = JointsRig.Momentum(w, art);
            Assert.True(p.Length() < 1e-4f, $"импульс пары {p}");
        }
        Assert.True(prev > -1e-3f, $"не разошлись: {prev} м");
        Assert.True(w.SelfTouches <= 1);

        // толщина 0 — пар нет; толщина 1,5 — шары толще: расходятся до 0,45 м
        var (w0, art0, a0, b0) = Overlapping(settings with { SelfContactScale = 0 });
        w0.SetSelfContacts(art0, [new BodyPair(0, 1)]);
        for (int t = 0; t < 60; t++) w0.Step(Dt);
        Assert.Equal(-0.1f, Gap(w0, a0, b0), 4);
        var (w15, art15, a15, b15) = Overlapping(settings with { SelfContactScale = 1.5f });
        w15.SetSelfContacts(art15, [new BodyPair(0, 1)]);
        for (int t = 0; t < 120; t++) w15.Step(Dt);
        Assert.True(Gap(w15, a15, b15) > 0.15f - 1e-3f, $"толщина 1,5: расстояние {Gap(w15, a15, b15) + 0.3f} м");

        // кинематическое тело сочленения в самоколлизии не участвует (толчок живого — у пар разных сочленений)
        var (wk, artk, ak, bk) = Overlapping(settings);
        wk.SetMode(ak, BodyMode.Kinematic);
        wk.SetSelfContacts(artk, [new BodyPair(0, 1)]);
        for (int t = 0; t < 30; t++) wk.Step(Dt);
        Assert.Equal(-0.1f, Gap(wk, ak, bk), 4);
    }

    [Fact]
    public void SelfContactsChange_WakesSleeper()
    {
        var w = new PhysicsWorld(settings: PhysicsSettings.Default with { SleepTime = 0.25f });
        w.SetGround(new StepGround(new Vector3(50, -1, 50), new Vector3(51, 0.2f, 51)));
        var art = w.CreateArticulation(2);
        JointsRig.Capsule(w, art, 0, 4, 0.1f, 0.2f, new Vector3(0, 0.1f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), BodyMode.Simulated);
        JointsRig.Capsule(w, art, 1, 4, 0.1f, 0.2f, new Vector3(0, 0.1f, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), BodyMode.Simulated);
        for (int t = 0; t < 240 && !w.IsAsleep(art); t++) w.Step(Dt);
        Assert.True(w.IsAsleep(art));
        w.SetSelfContacts(art, []);   // та же (пустая) таблица — не будит
        Assert.True(w.IsAsleep(art));
        w.SetSelfContacts(art, [new BodyPair(0, 1)]);
        Assert.False(w.IsAsleep(art));
    }

    /// <summary>Позы трупа: лёжа — ось стойки горизонтально, затем — на спину, лицом вниз, на правый и левый бок; поперёк кромки ступени.</summary>
    public static TheoryData<string> Poses => ["спина", "лицом вниз", "правый бок", "левый бок", "ступень"];

    private static Quaternion PoseTurn(string pose) => pose switch
    {
        // стойка вдоль +Y, лицом к +Z, правая сторона — +X
        "спина" => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),      // голова к −Z, лицо вверх
        "лицом вниз" => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2),  // голова к +Z, лицо вниз
        "левый бок" => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),   // голова к −X, левый бок вниз
        _ => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2),            // голова к +X, правый бок вниз (рука под торсом)
    };

    /// <summary>Труп бойца теста в позе <paramref name="pose"/> над полом (над кромкой ступени — «ступень»), суставы по этой позе, запястье выключено.</summary>
    private static ArticulationHandle Corpse(PhysicsWorld w, string pose, Vector3 at, bool self)
    {
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
        // клинок — вниз вдоль правой ноги, а не вперёд на метр (лицом вниз он ушёл бы в землю): хват — точка сустава запястья
        var blade = w.Body(art, 11);
        float half = w.Get(blade).HalfLength;
        JointsRig.PlaceAbout(w, blade, new Vector3(0, -half + 0.15f, 0), new Vector3(0.24f, 0.9f, 0.06f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI));
        var turn = PoseTurn(pose);
        float lowest = float.PositiveInfinity;
        for (int i = 0; i < 14; i++)
        {
            var s = w.Get(w.Body(art, i));
            foreach (var end in (ReadOnlySpan<Vector3>)[s.EndA, s.EndB]) lowest = MathF.Min(lowest, Vector3.Transform(end, turn).Y - s.Radius);
        }
        var shift = at + new Vector3(0, 0.03f - lowest, 0);
        for (int i = 0; i < 14; i++)
        {
            var b = w.Body(art, i);
            var s = w.Get(b);
            w.Place(b, shift + Vector3.Transform(s.Position, turn), Quaternion.Normalize(turn * s.Rotation));
        }
        w.SetJoints(art, JointsRig.FighterJoints(w, art));
        w.SetJointEnabled(art, 10, false);
        if (self) w.SetSelfContacts(art, FighterPairs());
        return art;
    }

    /// <summary>Самая глубокая пара таблицы трупа (капсулы как есть), м, и её имя.</summary>
    private static (float Depth, string Pair) Deepest(PhysicsWorld w, ArticulationHandle art)
    {
        float deepest = 0;
        string pair = "—";
        foreach (var p in FighterPairs())
        {
            var a = w.Get(w.Body(art, p.A));
            var b = w.Get(w.Body(art, p.B));
            float d = a.Radius + b.Radius - MathF.Sqrt(Segments.SegSeg(a.EndA, a.EndB, b.EndA, b.EndB, out _, out _));
            if (d > deepest) (deepest, pair) = (d, $"{p.A}–{p.B}");
        }
        return (deepest, pair);
    }

    private static readonly StepGround Ground = new(new Vector3(0, -1, -3), new Vector3(3, 0.2f, 3));

    [Theory]
    [MemberData(nameof(Poses))]
    public void Corpse_LyingAnyWay_NoPartInsideItsBody_EnergyAtRestDoesNotGrow_ZeroBytes(string pose)
    {
        (float Fall, float Still, string Pair, double Rise, double Net, float Kinetic, long Bytes) Run(bool self)
        {
            var w = new PhysicsWorld(settings: PhysicsSettings.Default with { JointLimitCompliance = 2e-6f, PopSpeed = 1 });
            w.SetGround(Ground);
            var art = Corpse(w, pose, pose == "ступень" ? new Vector3(-0.9f, 0.2f, 0) : new Vector3(-2, 0, 0), self);
            float fall = 0;
            for (int t = 0; t < 360; t++)
            {
                w.Step(Dt);
                if (t >= 60) fall = MathF.Max(fall, Deepest(w, art).Depth);   // первые 0,5 с — выход из позы, если она внахлёст
            }
            double e0 = JointsRig.Energy(w, art, 9.81f), rise = 0;
            float rest = 0;
            string pair = "—";
            long bytes = 0;
            for (int t = 0; t < 240; t++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                w.Step(Dt);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                rise = Math.Max(rise, JointsRig.Energy(w, art, 9.81f) - e0);
                var (d, p) = Deepest(w, art);
                if (d > rest) (rest, pair) = (d, p);
            }
            float kinetic = 0;
            for (int i = 0; i < 14; i++) kinetic += w.Get(w.Body(art, i)).KineticEnergy;
            return (fall, rest, pair, rise, JointsRig.Energy(w, art, 9.81f) - e0, kinetic, bytes);
        }

        var with = Run(self: true);
        var without = Run(self: false);
        output.WriteLine(FormattableString.Invariant($"{pose}: с самоколлизией — при падении ≤ {with.Fall * 1000:F1} мм, в покое ≤ {with.Still * 1000:F1} мм ({with.Pair}), подъём энергии {with.Rise:G3} Дж (итог {with.Net:G3}), кинетическая в конце {with.Kinetic:G3} Дж, {with.Bytes} Б; без — в покое {without.Still * 1000:F1} мм ({without.Pair}), подъём энергии {without.Rise:G3} Дж"));
        Assert.True(with.Fall <= FallDepth, $"при падении часть в своём теле на {with.Fall} м");
        Assert.True(with.Still <= RestDepth, $"в покое часть в своём теле на {with.Still} м ({with.Pair})");
        Assert.True(with.Rise <= RestEnergyRise && with.Net <= RestEnergyNet, $"энергия в покое выросла на {with.Rise} Дж (итог {with.Net})");
        Assert.True(with.Kinetic <= RestKinetic, $"не лёг: кинетическая {with.Kinetic} Дж");
        Assert.Equal(0, with.Bytes);
        if (pose == "правый бок") Assert.True(without.Still >= 0.05f, $"без самоколлизии рука не в торсе ({without.Still} м) — тест не проверяет то, что должен");
    }

    [Fact]
    public void Pile8_NoPartInsideItsBody()
    {
        var w = new PhysicsWorld(settings: PhysicsSettings.Default with { JointLimitCompliance = 2e-6f, PopSpeed = 1 });
        w.SetGround(Ground);
        var rng = new Random(8);
        var arts = new ArticulationHandle[8];
        string[] poses = ["спина", "лицом вниз", "правый бок", "левый бок"];
        for (int k = 0; k < 8; k++)
            arts[k] = Corpse(w, poses[k % 4], new Vector3(-1.2f + TestBodies.RandomVector(rng, 0.3f).X, k * 0.35f, TestBodies.RandomVector(rng, 0.3f).Z), self: true);
        float late = 0;
        string pair = "—";
        for (int t = 0; t < 600; t++)
        {
            w.Step(Dt);
            if (t < 360) continue;
            foreach (var a in arts)
            {
                var (d, p) = Deepest(w, a);
                if (d > late) (late, pair) = (d, p);
            }
        }
        output.WriteLine(FormattableString.Invariant($"куча 8 трупов, 3…5 с: самая глубокая своя пара {late * 1000:F1} мм ({pair})"));
        Assert.True(late <= PileDepth, $"в куче часть в своём теле на {late} м ({pair})");
    }

    /// <summary>
    /// Куча трёх трупов с самоколлизией и сном: два мира шаг за шагом бит в бит; пары самоколлизии решаются, пока трупы не спят,
    /// шаг спящей кучи не доходит ни до одной пары.
    /// </summary>
    [Fact]
    public void Pile3_Deterministic_AsleepSkipsSelfPairs()
    {
        var settings = PhysicsSettings.Default with { JointLimitCompliance = 2e-6f, PopSpeed = 1, SleepTime = 0.5f };
        (PhysicsWorld World, ArticulationHandle[] Arts) Build()
        {
            var w = new PhysicsWorld(settings: settings);
            w.SetGround(Ground);
            string[] poses = ["правый бок", "спина", "лицом вниз"];
            var arts = new ArticulationHandle[3];
            for (int k = 0; k < 3; k++) arts[k] = Corpse(w, poses[k], new Vector3(-1.5f + 0.2f * k, k * 0.35f, 0.1f * k), self: true);
            return (w, arts);
        }

        var (w1, arts1) = Build();
        var (w2, arts2) = Build();
        int touches = 0;
        bool AllAsleep(PhysicsWorld w, ArticulationHandle[] arts) => arts.All(w.IsAsleep);
        for (int t = 0; t < 1200 && !AllAsleep(w1, arts1); t++)
        {
            w1.Step(Dt);
            w2.Step(Dt);
            touches += w1.SelfTouches;
            for (int k = 0; k < 3; k++)
                for (int i = 0; i < 14; i++)
                {
                    var a = w1.Get(w1.Body(arts1[k], i));
                    var b = w2.Get(w2.Body(arts2[k], i));
                    Assert.True(a.Position == b.Position && a.Rotation == b.Rotation && a.Velocity == b.Velocity && a.AngularVelocity == b.AngularVelocity,
                        $"шаг {t}: тело {i} трупа {k} разошлось");
                }
        }
        output.WriteLine($"куча 3 трупов: касаний самоколлизии до сна {touches}, уснули все — {AllAsleep(w1, arts1)}");
        Assert.True(touches > 0, "самоколлизия не касалась — тест не проверяет то, что должен");
        Assert.True(AllAsleep(w1, arts1), "куча не уснула за 10 с");
        for (int t = 0; t < 60; t++)
        {
            w1.Step(Dt);
            Assert.Equal((0, 0), (w1.SelfNarrowChecks, w1.SelfTouches));
        }
    }

    // Допуски — по замерам (вывод тестов; docs/history/engine-physics.md), ≈ 2…5× к худшему.

    /// <summary>Самая глубокая своя пара при падении (после 0,5 с; замер ≤ 1,0 мм) и в покое (замер ≤ 0,2 мм), м.</summary>
    private const float FallDepth = 0.003f, RestDepth = 0.001f;

    /// <summary>
    /// Подъём полной энергии трупа в покое (3…5 с), Дж (замер 0…0,002; через кромку — 0,26: сомкнутые ноги медленно ползут против
    /// оси колен, в игре это останавливает сон), итог окна (≤ −0,04 — убыль), кинетическая в конце (≤ 0,003).
    /// </summary>
    private const double RestEnergyRise = 0.5, RestEnergyNet = 0.02;

    private const float RestKinetic = 0.02f;

    /// <summary>Самая глубокая своя пара в куче 8 трупов (3…5 с), м (замер 1,2 мм).</summary>
    private const float PileDepth = 0.005f;
}
