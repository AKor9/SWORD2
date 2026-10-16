using System.Numerics;
using static Sword.Engine.Physics.Tests.KinematicScene;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Проход клинка и удар по части: открытие контактом или попаданием с бюджетом по потенциалу, закрытие после пустых тиков, рост
/// бюджета; удар — потолок Δv, списание и отдача; новое сочленение в слоте ничего не наследует; 0 Б.
/// </summary>
public class KinematicPassTests
{
    /// <summary>Клинок (кинематический шар, толчок прохода) и тяжёлая цель; клинок в контакте с целью, если <paramref name="touching"/>.</summary>
    private static (PhysicsWorld W, ArticulationHandle Blade, ArticulationHandle Target) Scene(bool touching)
    {
        var w = World();
        var blade = Pusher(w, 0.1f, touching ? Vector3.Zero : new Vector3(5, 0, 0));
        Source(w, blade, KinematicSource.Pass);
        var target = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        return (w, blade, target);
    }

    /// <summary>Поставить клинок вплотную за целью (внахлёст 5 см) или далеко.</summary>
    private static void Put(PhysicsWorld w, ArticulationHandle blade, ArticulationHandle target, bool touching)
    {
        var at = touching ? w.Get(w.Body(target, 0)).Position - new Vector3(0, 0, 0.15f) : new Vector3(5, 0, 0);
        w.Place(w.Body(blade, 0), at, Quaternion.Identity);
    }

    [Fact]
    public void Pass_OpensOnContact_ClosesAfterEmptyTicks_ReopensWithNewBudget()
    {
        var (w, blade, target) = Scene(touching: true);
        float share = PhysicsSettings.Default.PassShare, fdt = PhysicsSettings.Default.PushForce * Dt;
        w.PreparePass(blade, 1, 2, 5);
        var p = w.Pass(blade, target);
        Assert.Equal((1, 0, false, share * 2 * 5, 0f, 0), (p.Strike, p.Number, p.Inside, p.Potential, p.Remaining, p.EmptyTicks));

        // контакт открывает проход с бюджетом потенциала; импульс за проход не больше бюджета (здесь бюджет меньше F·dt)
        w.Step(Dt);
        p = w.Pass(blade, target);
        Assert.True(p.Inside);
        Assert.Equal((1, 5f), (p.Number, p.Potential));
        Assert.True(p.Remaining is >= 0 and < 1e-3f, $"остаток {p.Remaining}");
        Near(5, Momentum(w, target).Z, 1e-3f, "импульс за проход = бюджет прохода");
        w.EndKinematicTick();
        Assert.Equal((true, 0), (w.Pass(blade, target).Inside, w.Pass(blade, target).EmptyTicks));

        // PassExitTicks = 2 тика без контакта и попадания — проход закрыт, остатка и потенциала нет
        Put(w, blade, target, touching: false);
        w.PreparePass(blade, 1, 2, 5);
        w.Step(Dt);
        w.EndKinematicTick();
        Assert.Equal((true, 1), (w.Pass(blade, target).Inside, w.Pass(blade, target).EmptyTicks));
        w.PreparePass(blade, 1, 2, 5);
        w.Step(Dt);
        w.EndKinematicTick();
        p = w.Pass(blade, target);
        Assert.Equal((false, 0, 0f, 0f), (p.Inside, p.EmptyTicks, p.Remaining, p.Potential));

        // снова контакт — новый проход с бюджетом нового потенциала
        w.PreparePass(blade, 1, 2, 8);
        Put(w, blade, target, touching: true);
        var before = Momentum(w, target).Z;
        w.Step(Dt);
        p = w.Pass(blade, target);
        Assert.Equal((2, true, 8f), (p.Number, p.Inside, p.Potential));
        // бюджет прохода 8 больше F·dt — упирается бюджет пары
        Near(fdt, Momentum(w, target).Z - before, 1e-3f, "импульс тика второго прохода");
        Near(8 - fdt, p.Remaining, 1e-3f, "остаток второго прохода");
    }

    [Fact]
    public void Pass_NewStrike_ResetsToOutside()
    {
        var (w, blade, target) = Scene(touching: true);
        w.PreparePass(blade, 7, 2, 16);
        w.Step(Dt);
        w.EndKinematicTick();
        var p = w.Pass(blade, target);
        Assert.True(p.Inside && p.Remaining > 0);

        // новый удар — проход «снаружи»: не открыт, остатка и пустых тиков нет, номер прохода прежний
        w.PreparePass(blade, 8, 1, 4);
        p = w.Pass(blade, target);
        Assert.Equal((8, 1, false, 2f, 0f, 0), (p.Strike, p.Number, p.Inside, p.Potential, p.Remaining, p.EmptyTicks));
        w.Step(Dt);
        Assert.Equal((2, true), (w.Pass(blade, target).Number, w.Pass(blade, target).Inside));
    }

    [Fact]
    public void Pass_HitWithoutContact_OpensAndKeepsIt_SpendHitTakesFromRemainder()
    {
        var (w, blade, target) = Scene(touching: false);
        w.PreparePass(blade, 1, 2, 3);   // потенциал 3
        w.Step(Dt);
        Assert.False(w.Pass(blade, target).Inside);   // контакта нет — проход закрыт

        w.PassHit(blade, target);
        var p = w.Pass(blade, target);
        Assert.Equal((true, 1, 3f), (p.Inside, p.Number, p.Remaining));
        Assert.Equal(1f, w.SpendHit(blade, target, 1));
        Assert.Equal(2f, w.SpendHit(blade, target, 5));   // выдаётся не больше остатка
        Assert.Equal(0f, w.SpendHit(blade, target, 1));
        Assert.Equal(0f, w.SpendHit(blade, target, -1));
        Assert.Equal(0f, w.SpendHit(blade, target, float.NaN));
        Assert.Equal(0f, w.Pass(blade, target).Remaining);

        w.EndKinematicTick();   // попадание в тике — проход держится
        Assert.Equal((true, 0), (w.Pass(blade, target).Inside, w.Pass(blade, target).EmptyTicks));
        w.EndKinematicTick();
        Assert.True(w.Pass(blade, target).Inside);
        w.EndKinematicTick();
        Assert.False(w.Pass(blade, target).Inside);
        Assert.Equal(0f, w.SpendHit(blade, target, 1));   // закрыт — 0
    }

    [Fact]
    public void Pass_BudgetRisesToLargerPotential_WhileOpen_ClosedPotentialIsPerTick()
    {
        var w = World();
        var blade = Pusher(w, 0.1f, Vector3.Zero);
        var t1 = Target(w, 10, 0.1f, Vacuum, new Vector3(5, 0, 0));
        var t2 = Target(w, 10, 0.1f, Vacuum, new Vector3(-5, 0, 0));
        w.PreparePass(blade, 1, 1, 4);   // 2 Н·с
        w.PassHit(blade, t1);
        Assert.Equal(1.5f, w.SpendHit(blade, t1, 1.5f));
        Assert.Equal((2f, 0.5f), (w.Pass(blade, t1).Potential, w.Pass(blade, t1).Remaining));

        // быстрая часть взмаха: бюджет открытого прохода — наибольший потенциал, потраченное (1,5) сохраняется
        w.PreparePass(blade, 1, 1, 10);   // 5 Н·с
        Assert.Equal((5f, 3.5f), (w.Pass(blade, t1).Potential, w.Pass(blade, t1).Remaining));
        w.PreparePass(blade, 1, 1, 2);   // замедлился — бюджет не падает
        Assert.Equal((5f, 3.5f), (w.Pass(blade, t1).Potential, w.Pass(blade, t1).Remaining));
        // у закрытого прохода потенциал — этого тика, конец тика его снимает
        Assert.Equal((false, 1f), (w.Pass(blade, t2).Inside, w.Pass(blade, t2).Potential));
        w.EndKinematicTick();
        Assert.Equal(0f, w.Pass(blade, t2).Potential);
        Assert.Equal((true, 5f), (w.Pass(blade, t1).Inside, w.Pass(blade, t1).Potential));
        // скорость выше потолка кинематической точки не прибавляет потенциала
        w.PreparePass(blade, 1, 1, 100);
        Assert.Equal(PhysicsSettings.Default.PassShare * PhysicsSettings.Default.KinematicPointSpeedMax, w.Pass(blade, t2).Potential);
    }

    [Fact]
    public void ApplyHit_CapsPointDeltaV_ByImpactSpeed()
    {
        var s = PhysicsSettings.Default;
        var w = World();
        var piece = w.CreateArticulation(1);
        var b = w.Body(piece, 0);
        w.SetShape(b, 0.4f, 0.03f, 0.1f);
        w.Place(b, new Vector3(0, 1, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f));
        w.SetMode(b, BodyMode.Simulated);
        var point = w.Get(b).ToWorld(new Vector3(0.03f, 0.08f, 0));   // у торца: удар крутит
        var dir = Vector3.Normalize(new Vector3(1, 0.2f, 0.4f));
        var before = w.Get(b);

        var applied = w.ApplyHit(b, point, dir * 12, 10, ArticulationHandle.None);
        var after = w.Get(b);
        float dv = Vector3.Dot(after.PointVelocity(point) - before.PointVelocity(point), dir);
        Near(s.HitDvShare * 10, dv, 1e-4f, "Δv точки вдоль удара");
        Assert.True(applied.Length() < 12);
        Near(1, Vector3.Dot(Vector3.Normalize(applied), dir), 1e-6f, "направление удара");

        // без скорости удара — без потолка
        var c = w.Body(w.CreateArticulation(1), 0);
        w.SetShape(c, 0.4f, 0.03f, 0.1f);
        w.SetMode(c, BodyMode.Simulated);
        Assert.True(Vector3.Distance(dir * 12, w.ApplyHit(c, Vector3.Zero, dir * 12, 0, ArticulationHandle.None)) < 1e-5f);
        Near(12 / 0.4f, w.Get(c).Velocity.Length(), 1e-5f, "импульс в центр");
        Assert.Equal(Vector3.Zero, w.ApplyHit(c, Vector3.Zero, Vector3.Zero, 10, ArticulationHandle.None));
    }

    [Fact]
    public void ApplyHit_OnSimulatedPart_SpendsPassAndPushesPusherBack()
    {
        var (w, blade, target) = Scene(touching: false);
        var part = w.Body(target, 0);
        var center = w.Get(part).Position;
        w.PreparePass(blade, 1, 2, 3);   // 3 Н·с
        // проход закрыт — удар по части на физике ничего не даёт
        Assert.Equal(Vector3.Zero, w.ApplyHit(part, center, new Vector3(0, 0, 5), 0, blade));
        Assert.Equal(Vector3.Zero, Momentum(w, target));

        w.PassHit(blade, target);
        var applied = w.ApplyHit(part, center, new Vector3(0, 0, 5), 0, blade);
        Assert.Equal(new Vector3(0, 0, 3), applied);   // урезан остатком прохода
        Assert.Equal(0f, w.Pass(blade, target).Remaining);
        Near(3, Momentum(w, target).Z, 1e-5f, "импульс части");
        Assert.Equal(new Vector3(0, 0, -3), w.Reaction(blade));

        // без толкающего (или мёртвого) — без прохода и отдачи
        Near(4, w.ApplyHit(part, center, new Vector3(0, 0, 4), 0, ArticulationHandle.None).Z, 1e-6f, "удар без толкающего");
        Assert.Equal(new Vector3(0, 0, -3), w.TakeReaction(blade));
    }

    [Fact]
    public void ApplyHit_OnKinematicPart_NoPassNoReaction()
    {
        var w = World();
        var blade = Pusher(w, 0.1f, new Vector3(5, 0, 0));
        var living = Pusher(w, 0.1f, Vector3.Zero);   // ещё кинематическая часть — тик смерти
        var part = w.Body(living, 0);
        w.PreparePass(blade, 1, 2, 3);
        var applied = w.ApplyHit(part, Vector3.Zero, new Vector3(0, 0, 5), 0, blade);
        Assert.Equal(new Vector3(0, 0, 5), applied);
        Assert.Equal(new Vector3(0, 0, 5), w.Get(part).Velocity);   // масса 1 кг
        Assert.Equal(Vector3.Zero, w.Reaction(blade));
        Assert.False(w.Pass(blade, living).Inside);
        Assert.Equal(3f, w.Pass(blade, living).Potential);
    }

    [Fact]
    public void NewArticulationInSlot_DoesNotInheritPassOrReaction()
    {
        var (w, blade, target) = Scene(touching: true);
        w.PreparePass(blade, 1, 2, 16);
        w.Step(Dt);
        Assert.True(w.Pass(blade, target).Inside);
        Assert.NotEqual(Vector3.Zero, w.Reaction(blade));

        // цель ушла, в её слоте — новое сочленение
        w.DestroyArticulation(target);
        var fresh = Target(w, 100, 0.1f, Vacuum, new Vector3(0, 0, 0.15f));
        Assert.Equal(target.Index, fresh.Index);
        Assert.Equal(default, w.Pass(blade, fresh));
        Assert.Equal(default, w.Pass(fresh, blade));

        // толкающий ушёл: новое в его слоте — без отдачи и без проходов в обе стороны
        w.PreparePass(blade, 2, 2, 16);
        w.PassHit(blade, fresh);
        w.DestroyArticulation(blade);
        var reborn = Pusher(w, 0.1f, Vector3.Zero);
        Assert.Equal(blade.Index, reborn.Index);
        Assert.Equal(Vector3.Zero, w.Reaction(reborn));
        Assert.Equal(default, w.Pass(reborn, fresh));
        Assert.Equal(default, w.Pass(fresh, reborn));
    }

    [Fact]
    public void Arguments_Validated()
    {
        var (w, blade, target) = Scene(touching: false);
        Assert.Throws<ArgumentException>(() => w.Pass(blade, blade));
        Assert.Throws<ArgumentException>(() => w.PassHit(target, target));
        Assert.Throws<ArgumentException>(() => w.SpendHit(blade, blade, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.PreparePass(blade, 1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.PreparePass(blade, 1, float.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.PreparePass(blade, 1, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.PreparePass(blade, 1, 1, float.PositiveInfinity));
        var part = w.Body(target, 0);
        Assert.Throws<ArgumentException>(() => w.ApplyHit(part, new Vector3(float.NaN, 0, 0), Vector3.UnitZ, 1, blade));
        Assert.Throws<ArgumentException>(() => w.ApplyHit(part, Vector3.Zero, new Vector3(0, float.PositiveInfinity, 0), 1, blade));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.ApplyHit(part, Vector3.Zero, Vector3.UnitZ, float.NaN, blade));

        var stale = target;
        w.DestroyArticulation(target);
        Assert.Throws<InvalidOperationException>(() => w.Pass(blade, stale));
        Assert.Throws<InvalidOperationException>(() => w.Reaction(stale));
        Assert.Throws<InvalidOperationException>(() => w.TakeReaction(stale));
        Assert.Throws<InvalidOperationException>(() => w.PreparePass(stale, 1, 1, 1));
        Assert.Throws<InvalidOperationException>(() => w.ApplyHit(part, Vector3.Zero, Vector3.UnitZ, 1, blade));
    }

    /// <summary>
    /// Два толкающих по 13 тел и две цели по 14 тел внахлёст (шары вдоль X); земля — плоскость на 1 мм выше низа целей (цели
    /// чуть в земле: касание и трение есть, толчкам не мешает).
    /// </summary>
    internal static (ArticulationHandle[] Pushers, ArticulationHandle[] Targets) Crowd(PhysicsWorld w)
    {
        w.SetGround(new FlatGround(-0.099f));
        var pushers = new ArticulationHandle[2];
        var targets = new ArticulationHandle[2];
        for (int k = 0; k < 2; k++)
        {
            var at = new Vector3[13];
            for (int i = 0; i < 13; i++) at[i] = new Vector3(i * 0.3f, 0, k * 3);
            pushers[k] = Pusher(w, 0.1f, at);
            var tat = new Vector3[14];
            for (int i = 0; i < 14; i++) tat[i] = new Vector3(i * 0.3f, 0, k * 3 + 0.15f);
            targets[k] = Target(w, 5, 0.1f, Vacuum, tat);
        }
        Source(w, pushers[1], KinematicSource.Pass);
        return (pushers, targets);
    }

    /// <summary>Тик толпы: цели на места, толкающие в цель тика, удар, шаг, попадание и удар по части, отдача, конец тика.</summary>
    internal static float CrowdTick(PhysicsWorld w, ArticulationHandle[] pushers, ArticulationHandle[] targets, int t)
    {
        float sink = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < 14; i++) w.Place(w.Body(targets[k], i), new Vector3(i * 0.3f, 0, k * 3 + 0.15f), Quaternion.Identity);
            for (int i = 0; i < 13; i++)
                w.SetKinematicTarget(w.Body(pushers[k], i), new Vector3(i * 0.3f, 0.01f * (t % 3), k * 3), Quaternion.Identity, Dt);
        }
        w.PreparePass(pushers[1], t / 50, 2, 8);
        w.Step(Dt);
        w.PassHit(pushers[1], targets[1]);
        sink += w.ApplyHit(w.Body(targets[1], 3), new Vector3(0.9f, 0, 3.1f), new Vector3(0, 0, 2), 6, pushers[1]).Z;
        sink += w.SpendHit(pushers[1], targets[1], 0.1f);
        sink += w.Pass(pushers[1], targets[1]).Remaining + w.Reaction(pushers[0]).Z;
        sink += w.TakeReaction(pushers[0]).Length() + w.TakeReaction(pushers[1]).Length();
        w.EndKinematicTick();
        return sink;
    }
}
