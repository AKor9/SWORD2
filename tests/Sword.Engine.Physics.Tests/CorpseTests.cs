using System.Numerics;
using Xunit.Abstractions;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Труп целиком (сведение физики 7в–7д): сочленение бойца из 14 капсул с суставами падает на тестовую землю со ступенью —
/// успокаивается, энергия в покое не растёт, суставы держат, в землю не уходит, 0 Б на тик; куча из 32 трупов — без разлёта,
/// энергия кучи убывает, местный рост энергии (риск 7г «зажимы в куче») — замер в выводе и в README физики («Сведение физики»).
/// Массы и радиусы — как у игры (<c>FighterShape.Masses</c>, <c>FighterShape.Radii</c> — данные теста, игра не подключена; они же
/// у заготовки <see cref="JointsRig.Fighter"/>), суставы — таблица-заготовка <see cref="JointsRig.FighterJoints"/> (отправные
/// пределы SWORD), профиль динамики — по умолчанию (<see cref="BodyDynamics"/>), шаг — настоящий <see cref="PhysicsWorld.Step"/>.
/// </summary>
public class CorpseTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 120;

    /// <summary>Земля: пол y = 0 и ступень 20 см — ящик x ∈ [0; 3], z ∈ [−3; 3] (грань 1: верх, подступенок, кромка).</summary>
    private static readonly StepGround Ground = new(new Vector3(0, -1, -3), new Vector3(3, 0.2f, 3));

    private static PhysicsWorld World()
    {
        var w = new PhysicsWorld();
        w.SetGround(Ground);
        return w;
    }

    /// <summary>
    /// Труп: боец в позе стойки (<see cref="JointsRig.Fighter"/>), повёрнутый вокруг начала координат на <paramref name="turn"/> и
    /// сдвинутый на <paramref name="origin"/>, без скоростей, на физике; суставы — привязка по этой позе.
    /// </summary>
    private static ArticulationHandle Corpse(PhysicsWorld w, Vector3 origin, Quaternion turn)
    {
        var art = JointsRig.Fighter(w, Vector3.Zero, BodyMode.Simulated);
        for (int i = 0; i < 14; i++)
        {
            var b = w.Body(art, i);
            var s = w.Get(b);
            w.Place(b, origin + Vector3.Transform(s.Position, turn), Quaternion.Normalize(turn * s.Rotation));
        }
        w.SetJoints(art, JointsRig.FighterJoints(w, art));
        return art;
    }

    /// <summary>Лёжа: ось стойки — вдоль +X (ноги у начала координат), затем курс <paramref name="yaw"/> вокруг вертикали.</summary>
    private static Quaternion Lying(float yaw) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);

    /// <summary>Энергия тел на физике мира, Дж: кинетическая + m·g·y (гравитация профиля тела).</summary>
    private static double Energy(PhysicsWorld w)
    {
        double e = 0;
        for (int i = 0; i < w.BodyCapacity; i++)
        {
            if (w.BodyAt(i) is not { IsNone: false } b) continue;
            var s = w.Get(b);
            if (s.Mode == BodyMode.Simulated) e += s.KineticEnergy + (double)s.Mass * w.GetDynamics(s.Profile).Gravity * s.Position.Y;
        }
        return e;
    }

    /// <summary>Наибольшие скорость, угловая скорость и глубина торца в земле по телам на физике.</summary>
    private static (float Speed, float Angular, float Depth) Motion(PhysicsWorld w)
    {
        float v = 0, a = 0, d = 0;
        for (int i = 0; i < w.BodyCapacity; i++)
        {
            if (w.BodyAt(i) is not { IsNone: false } b) continue;
            var s = w.Get(b);
            if (s.Mode != BodyMode.Simulated) continue;
            v = MathF.Max(v, s.Velocity.Length());
            a = MathF.Max(a, s.AngularVelocity.Length());
            d = MathF.Max(d, ContactsKit.Depth(Ground, s));
        }
        return (v, a, d);
    }

    /// <summary>Шаг с замером выделенной им памяти, Б.</summary>
    private static long StepAllocated(PhysicsWorld w)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        w.Step(Dt);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Theory]
    [InlineData(-0.9f, 0.7f, 0f)]      // таз над кромкой ступени: ноги — на пол, торс — на ступень
    [InlineData(-1.2f, 0.9f, 0.6f)]    // наискось через кромку
    [InlineData(0.8f, 0.9f, 1.57f)]    // поперёк, на ступени
    [InlineData(-2.5f, 0.6f, 3.14f)]   // на полу головой к подступенку
    public void OneCorpse_FallsOnStep_Settles_JointsHold_EnergyAtRestDoesNotGrow_ZeroBytes(float x, float y, float yaw)
    {
        var w = World();
        var trace = w.EnableTrace();
        var art = Corpse(w, new Vector3(x, y, 0), Lying(yaw));
        float fallPoint = 0, fallExcess = 0;
        for (int t = 0; t < SettleTicks; t++)
        {
            w.Step(Dt);
            var (point, excess) = JointsRig.Worst(w, art);
            (fallPoint, fallExcess) = (MathF.Max(fallPoint, point), MathF.Max(fallExcess, excess));
        }

        // Покой: энергия (кинетическая + m·g·y) не поднимается над началом покоя, рост за подшаг (трасса — инвариант
        // physics.energy, 7з) мал, суставы держат, в землю не уходит, шаг не выделяет памяти.
        trace.ClearWindow();
        double e0 = Energy(w), eMax = e0, rise = 0, prev = e0;
        float speed = 0, angular = 0, depth = 0, restPoint = 0, restExcess = 0;
        long allocated = 0;
        for (int t = 0; t < RestTicks; t++)
        {
            allocated += StepAllocated(w);
            double e = Energy(w);
            (eMax, rise, prev) = (Math.Max(eMax, e), rise + Math.Max(0, e - prev), e);
            var (v, a, d) = Motion(w);
            (speed, angular, depth) = (MathF.Max(speed, v), MathF.Max(angular, a), MathF.Max(depth, d));
            var (point, excess) = JointsRig.Worst(w, art);
            (restPoint, restExcess) = (MathF.Max(restPoint, point), MathF.Max(restExcess, excess));
        }
        var window = trace.Window;
        output.WriteLine($"({x}; {y}; курс {yaw}): падение — точка {fallPoint * 1000:F1} мм, выход {fallExcess:F3} рад; покой " +
                         $"{RestTicks / 120} с — скорость ≤ {speed:G3} м/с, {angular:G3} рад/с; энергия над началом покоя ≤ {eMax - e0:G3} Дж, " +
                         $"итог {prev - e0:G3} Дж, сумма приростов за тик {rise:G3} Дж; рост за подшаг (трасса) {window.EnergyGrowth:G3} Дж; " +
                         $"точка {restPoint * 1000:F2} мм, выход {restExcess:F4} рад; глубина {depth * 1000:F3} мм; {allocated} Б");
        Assert.True(speed <= RestSpeed && angular <= RestAngular, $"не успокоился: {speed} м/с, {angular} рад/с");
        Assert.True(eMax - e0 <= RestEnergyRise, $"энергия в покое выросла на {eMax - e0} Дж");
        Assert.True(window.EnergyGrowth <= RestSubstepGrowth, $"рост энергии за подшаг {window.EnergyGrowth} Дж");
        Assert.True(fallPoint <= FallPoint && fallExcess <= FallExcess, $"суставы при падении: точка {fallPoint}, выход {fallExcess}");
        Assert.True(restPoint <= RestPoint && restExcess <= RestExcess, $"суставы в покое: точка {restPoint}, выход {restExcess}");
        Assert.True(depth <= RestDepth, $"в земле на {depth} м");
        Assert.Equal(0, window.NonFinite);
        Assert.True(allocated == 0, $"{RestTicks} тиков трупа с суставами, землёй и трассой выделили {allocated} Б");
    }

    [Fact]
    public void Pile32_NoScatter_JointsHold_PileEnergyDecreases()
    {
        // 32 трупа восемью слоями по четыре над кромкой ступени, курс случайный: падают друг на друга и ложатся кучей. Свои тела
        // сочленения не сталкиваются — тело, зажатое между двумя телами одного трупа, выталкивается из одного в другое (риск 7г
        // «зажимы в куче»): местный рост энергии — замер в выводе (README «Сведение физики»), здесь держится, что куча не
        // разлетается и её энергия убывает.
        var w = World();
        var trace = w.EnableTrace();
        var rng = new Random(32);
        var arts = new ArticulationHandle[32];
        for (int k = 0; k < 32; k++)
        {
            int layer = k / 4, column = k % 4;
            var at = new Vector3(-1.4f + (column % 2) * 0.9f + TestBodies.RandomVector(rng, 0.2f).X, 0.6f + layer * 0.45f,
                -0.6f + (column / 2) * 1.2f + TestBodies.RandomVector(rng, 0.2f).Z);
            arts[k] = Corpse(w, at, Lying(rng.NextSingle() * MathF.Tau));
        }
        var center = new Vector2(-0.5f, 0);
        float Spread()
        {
            float r = 0;
            foreach (var a in arts)
            {
                var s = w.Get(w.Body(a, 0));   // таз
                r = MathF.Max(r, Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), center));
            }
            return r;
        }
        float spread0 = Spread();
        (float Point, float Excess) Joints((float Point, float Excess) worst)
        {
            foreach (var a in arts)
            {
                var (point, excess) = JointsRig.Worst(w, a);
                worst = (MathF.Max(worst.Point, point), MathF.Max(worst.Excess, excess));
            }
            return worst;
        }
        (float Point, float Excess) fall = default, rest = default;
        for (int t = 0; t < PileSettleTicks; t++)
        {
            w.Step(Dt);
            if (t % 4 == 0) fall = Joints(fall);
        }

        trace.ClearWindow();
        double e0 = Energy(w), rise = 0, prev = e0;
        float speed = 0, depth = 0;
        for (int t = 0; t < PileRestTicks; t++)
        {
            w.Step(Dt);
            double e = Energy(w);
            (rise, prev) = (rise + Math.Max(0, e - prev), e);
            var (v, _, d) = Motion(w);
            (speed, depth) = (MathF.Max(speed, v), MathF.Max(depth, d));
            if (t % 4 == 0) rest = Joints(rest);
        }
        float spread = Spread();
        var window = trace.Window;
        output.WriteLine($"32 трупа, {(PileSettleTicks + PileRestTicks) / 120} с: разброс тазов {spread0:F2} → {spread:F2} м; суставы при " +
                         $"падении — точка {fall.Point * 1000:F1} мм, выход {fall.Excess:F3} рад; последние {PileRestTicks / 120} с — энергия " +
                         $"{prev - e0:F1} Дж, сумма приростов за тик {rise:G3} Дж, рост за подшаг (трасса) {window.EnergyGrowth:G3} Дж " +
                         $"({window.EnergyArticulation}), всех {window.EnergyGrowthTotal:G3} Дж, скорость ≤ {speed:G3} м/с, суставы — точка " +
                         $"{rest.Point * 1000:F1} мм, выход {rest.Excess:F3} рад; глубина {depth * 1000:F2} мм");
        Assert.True(spread <= spread0 + PileScatter, $"куча разлетелась: разброс тазов {spread0} → {spread} м");
        Assert.True(prev < e0, $"энергия кучи выросла за {PileRestTicks / 120} с: {prev - e0} Дж");
        Assert.True(speed <= PileSpeed, $"тела кучи быстрее {speed} м/с");
        Assert.True(fall.Point <= PileFallPoint && fall.Excess <= PileFallExcess, $"суставы при падении кучи: точка {fall.Point}, выход {fall.Excess}");
        Assert.True(rest.Point <= PileRestPoint && rest.Excess <= PileRestExcess, $"суставы в куче: точка {rest.Point}, выход {rest.Excess}");
        Assert.True(depth <= PileDepth, $"в земле на {depth} м");
        Assert.Equal(0, window.NonFinite);
    }

    // Допуски — по своим замерам (Debug, ≈ 2× к худшему; замеры — в выводе тестов и README физики «Сведение физики»).

    /// <summary>Одиночный труп: падение до замера покоя, тиков (5 с); покой — тиков замера (3 с).</summary>
    private const int SettleTicks = 600, RestTicks = 360;

    /// <summary>
    /// Покой трупа: скорость тел, м/с (замер ≤ 0,0078), угловая, рад/с (замер ≤ 0,30: стопы и клинок чуть покачиваются, сдвига
    /// нет).
    /// </summary>
    private const float RestSpeed = 0.02f, RestAngular = 0.6f;

    /// <summary>
    /// Покой: подъём энергии над началом покоя, Дж (замер ≤ 2,6e-3 — досыпание наискось через кромку, итог — убыль; у остальных
    /// ≤ 8e-5), рост за подшаг по трассе, Дж (замер ≤ 1,4e-4).
    /// </summary>
    private const float RestEnergyRise = 1e-2f, RestSubstepGrowth = 5e-4f;

    /// <summary>Суставы при падении (удар о землю и кромку): ошибка точки, м (замер ≤ 0,023), выход, рад (замер ≤ 0,136).</summary>
    private const float FallPoint = 0.05f, FallExcess = 0.3f;

    /// <summary>Суставы в покое: ошибка точки, м (замер ≤ 5,2e-4), выход, рад (замер ≤ 4,3e-3).</summary>
    private const float RestPoint = 1.5e-3f, RestExcess = 0.01f;

    /// <summary>Глубина торца в земле в покое, м (замер ≤ 7,9e-5).</summary>
    private const float RestDepth = 2e-4f;

    /// <summary>Куча: падение до замера и замер, тиков (по 2 с; 32 × 14 тел в Debug — ≈ 14 мс на тик).</summary>
    private const int PileSettleTicks = 240, PileRestTicks = 240;

    /// <summary>
    /// Куча: разброс тазов сверх начального, м (замер +0,69: трупы сползают со ступени); скорость тел в последние 2 с, м/с (замер
    /// ≤ 2,29 — куча ещё оседает).
    /// </summary>
    private const float PileScatter = 1.5f, PileSpeed = 5f;

    /// <summary>
    /// Куча, суставы: при падении — ошибка точки, м (замер ≤ 0,037), выход, рад (замер ≤ 0,37: удары трупов друг о друга, одна
    /// итерация); в последние 2 с — точка (замер ≤ 0,003), выход (замер ≤ 0,060); глубина в земле, м (замер ≤ 0,008 — тело под
    /// весом кучи).
    /// </summary>
    private const float PileFallPoint = 0.08f, PileFallExcess = 0.75f, PileRestPoint = 0.01f, PileRestExcess = 0.15f, PileDepth = 0.02f;
}
