using System.Numerics;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Пары сочленений: внахлёст расходятся не быстрее скорости выталкивания, импульс сохраняется; свои тела не сталкиваются; пары
/// без тела на физике не проверяются; отсев — по свежим границам.
/// </summary>
public class ContactsPairTests
{
    public static TheoryData<string, Vector3, Vector3, float> Overlaps => new()
    {
        // ось второй капсулы, её центр (первая — вдоль X в начале координат), полудлина второй
        { "накрест", Vector3.UnitZ, new Vector3(0.05f, 0.1f, 0), 0.25f },
        { "параллельно", Vector3.UnitX, new Vector3(0.1f, 0.08f, 0.02f), 0.25f },
        { "торцом в бок", Vector3.UnitY, new Vector3(0.1f, 0.3f, 0), 0.25f },
        { "шар в капсулу", Vector3.UnitY, new Vector3(-0.1f, 0.1f, 0.03f), 0f },
    };

    [Theory]
    [MemberData(nameof(Overlaps))]
    public void SimulatedPair_Overlapping_SeparatesNoFasterThanPopSpeed_ConservesMomentum(string name, Vector3 axis, Vector3 center, float half)
    {
        var w = World();
        var a = Simulated(w, 5, 0.09f, 0.25f, Vector3.Zero, AxisAlong(Vector3.UnitX), ProfileVacuum);
        var b = Simulated(w, 2, 0.07f, half, center, AxisAlong(axis), ProfileVacuum);
        float overlap = Overlap(w.Get(a), w.Get(b));
        Assert.True(overlap > 0.05f, $"{name}: внахлёст всего {overlap}");
        float limit = PhysicsSettings.Default.PopSpeed * Dt;
        int ticks = 0;
        while (overlap > 1e-4f)
        {
            w.Step(Dt);
            float now = Overlap(w.Get(a), w.Get(b));
            // Относительный выход — не быстрее скорости выталкивания (с запасом на поворот и податливость).
            Assert.True(overlap - now <= limit * 1.05f, $"{name}, тик {ticks}: вышли на {overlap - now:E3} м при пределе {limit:E3}");
            Assert.True(overlap - now >= limit * 0.3f || now < limit, $"{name}, тик {ticks}: застряли, {overlap - now:E3} м за тик");
            overlap = now;
            var p = Momentum(w.Get(a)) + Momentum(w.Get(b));
            Assert.True(p.Length() <= 1e-4f, $"{name}, тик {ticks}: импульс пары {p}");
            Assert.True(++ticks < 200);
        }
        // Разошлись — и не разлетаются: нормальная скорость в контакте погашена; остаток — вращение от выталкивания с плечом
        // (замер: относительная скорость центров до 0,9 % скорости выталкивания).
        var gap = Vector3.Distance(w.Get(a).Position, w.Get(b).Position);
        for (int t = 0; t < 60; t++) w.Step(Dt);
        var va = w.Get(a).Velocity;
        var vb = w.Get(b).Velocity;
        float drift = Vector3.Distance(w.Get(a).Position, w.Get(b).Position) - gap;
        Assert.True((va - vb).Length() <= 0.02f * PhysicsSettings.Default.PopSpeed,
            $"{name}: после расхождения относительная скорость {(va - vb).Length()} м/с");
        Assert.True(drift <= 0.02f * PhysicsSettings.Default.PopSpeed * 0.5f, $"{name}: за 0,5 с разошлись ещё на {drift} м");
        Assert.InRange(Overlap(w.Get(a), w.Get(b)), 0, 1e-4f);
    }

    [Fact]
    public void SameArticulation_BodiesDoNotCollide()
    {
        var w = World();
        var art = w.CreateArticulation(2);
        Setup(w, w.Body(art, 0), 5, 0.09f, 0.25f, Vector3.Zero, AxisAlong(Vector3.UnitX), ProfileVacuum);
        Setup(w, w.Body(art, 1), 2, 0.07f, 0.25f, new Vector3(0.05f, 0.1f, 0), AxisAlong(Vector3.UnitZ), ProfileVacuum);
        var before = (w.Get(w.Body(art, 0)).Position, w.Get(w.Body(art, 1)).Position);
        for (int t = 0; t < 10; t++) w.Step(Dt);
        Assert.Equal(before, (w.Get(w.Body(art, 0)).Position, w.Get(w.Body(art, 1)).Position));
        Assert.Equal(0, w.ContactsNarrowChecks);
    }

    [Fact]
    public void PairsWithoutSimulatedBody_AreNotChecked()
    {
        var w = World();
        // Два кинематических сочленения внахлёст — ни пары сочленений, ни пар тел.
        var k1 = w.CreateArticulation(3);
        var k2 = w.CreateArticulation(3);
        for (int i = 0; i < 3; i++)
        {
            w.SetShape(w.Body(k1, i), 1, 0.1f, 0.2f);
            w.SetShape(w.Body(k2, i), 1, 0.1f, 0.2f);
            w.Place(w.Body(k1, i), new Vector3(0, i * 0.1f, 0), Quaternion.Identity);
            w.Place(w.Body(k2, i), new Vector3(0.05f, i * 0.1f, 0), Quaternion.Identity);
        }
        w.Step(Dt);
        Assert.Equal((0, 0), (w.ContactsPairChecks, w.ContactsNarrowChecks));

        // Сочленение с одним телом на физике из трёх: до SegSeg доходят только пары с ним — 3 × 3 (с тремя телами каждого
        // кинематического сочленения) за подшаг; кинематические сочленения между собой — нет.
        var mixed = w.CreateArticulation(3);
        for (int i = 0; i < 3; i++)
        {
            w.SetShape(w.Body(mixed, i), 1, 0.1f, 0.2f);
            w.Place(w.Body(mixed, i), new Vector3(0.02f, i * 0.1f, 0.03f), Quaternion.Identity);
        }
        w.SetMode(w.Body(mixed, 1), BodyMode.Simulated);
        w.SetDynamics(w.Body(mixed, 1), ProfileVacuum);
        w.Step(Dt);
        int substeps = w.Settings.Substeps;
        Assert.Equal(2 * substeps, w.ContactsPairChecks);          // (k1, mixed), (k2, mixed); (k1, k2) — нет
        Assert.Equal(2 * 3 * substeps, w.ContactsNarrowChecks);    // тело на физике × 3 тела каждого кинематического
        // Кинематические тела шаг не двигает.
        Assert.Equal(new Vector3(0, 0.1f, 0), w.Get(w.Body(k1, 1)).Position);
    }

    [Fact]
    public void PairFilter_UsesFreshBounds_NotLastStep()
    {
        var w = World();
        var a = Simulated(w, 5, 0.09f, 0.25f, Vector3.Zero, AxisAlong(Vector3.UnitX), ProfileVacuum);
        var b = Simulated(w, 2, 0.07f, 0.25f, new Vector3(50, 0, 0), AxisAlong(Vector3.UnitZ), ProfileVacuum);
        w.Step(Dt);
        var stale = w.Bounds(w.ArticulationOf(b));
        Assert.Equal(new Vector3(50, 0, 0), stale.Center);
        // Сдвинули внахлёст между шагами: границы прошлого шага пару отсеяли бы — свежие ловят её в первом же подшаге.
        w.Place(b, new Vector3(0.05f, 0.1f, 0), AxisAlong(Vector3.UnitZ));
        float before = Overlap(w.Get(a), w.Get(b));
        w.Step(Dt);
        Assert.True(w.ContactsNarrowChecks > 0);
        Assert.True(Overlap(w.Get(a), w.Get(b)) < before - 0.5f * PhysicsSettings.Default.PopSpeed * Dt, "пара не решалась в этом шаге");
    }

    [Fact]
    public void Bounds_EmptyBeforeStep_ContainAllCapsulesAfter_EmptyForNewArticulation()
    {
        var w = World();
        var art = w.CreateArticulation(3);
        var rng = new Random(7);
        for (int i = 0; i < 3; i++)
        {
            w.SetShape(w.Body(art, i), 1 + i, 0.05f + 0.02f * i, 0.1f + 0.1f * i);
            w.Place(w.Body(art, i), TestBodies.RandomVector(rng, 1) + new Vector3(3, 1, 0), TestBodies.RandomRotation(rng));
        }
        Assert.Equal(default, w.Bounds(art));
        w.Step(Dt);
        var bounds = w.Bounds(art);
        for (int i = 0; i < 3; i++)
        {
            var s = w.Get(w.Body(art, i));
            foreach (var end in new[] { s.EndA, s.EndB })
                Assert.True(Vector3.Distance(end, bounds.Center) + s.Radius <= bounds.Radius + 1e-5f, $"тело {i} вне границ");
        }
        var fresh = w.CreateArticulation(1);
        Assert.Equal(default, w.Bounds(fresh));
        w.DestroyArticulation(art);
        Assert.Throws<InvalidOperationException>(() => w.Bounds(art));
        var reused = w.CreateArticulation(3);   // тот же слот — границы не наследуются
        Assert.Equal(art.Index, reused.Index);
        Assert.Equal(default, w.Bounds(reused));
    }
}
