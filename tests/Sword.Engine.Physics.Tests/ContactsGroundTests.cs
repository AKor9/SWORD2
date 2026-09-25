using System.Numerics;
using static Sword.Engine.Physics.Tests.ContactsKit;

namespace Sword.Engine.Physics.Tests;

/// <summary>
/// Контакты с землёй через тестовые <see cref="IGround"/>: покой на ровной земле, выход утопленного тела не быстрее
/// скорости выталкивания без скрытой скорости, трение покоя на склоне до μ, торцы короткой капсулы не удваивают контакт, грани
/// ступени-ящика по отдельности.
/// </summary>
public class ContactsGroundTests
{
    private const float Radius = 0.08f, Half = 0.2f, Mass = 4;

    public static TheoryData<string> Poses => new() { "лёжа", "стоя", "наклон 30°", "шар" };

    private static (float Half, Quaternion Rotation) Pose(string pose) => pose switch
    {
        "лёжа" => (Half, AxisAlong(Vector3.UnitX)),
        "стоя" => (Half, Quaternion.Identity),
        "наклон 30°" => (Half, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)),
        _ => (0f, Quaternion.Identity),
    };

    [Theory]
    [MemberData(nameof(Poses))]
    public void FallingCapsule_SettlesOnFlatGround_DepthWithinTolerance(string pose)
    {
        var ground = new FlatGround();
        var w = World(ground: ground);
        var (half, rot) = Pose(pose);
        var b = Simulated(w, Mass, Radius, half, new Vector3(0, 0.6f, 0), rot);

        float maxDepth = 0, lastSpeed = 0, lastSpin = 0;
        for (int t = 0; t < 360; t++)
        {
            w.Step(Dt);
            var s = w.Get(b);
            Assert.True(s.IsFinite);
            maxDepth = MathF.Max(maxDepth, Depth(ground, s));
            if (t >= 300)
            {
                lastSpeed = MathF.Max(lastSpeed, s.Velocity.Length());
                lastSpin = MathF.Max(lastSpin, s.AngularVelocity.Length());
                Assert.True(s.Grounded, $"{pose}: тик {t} — не касается земли");
            }
        }
        // Удар о землю на ~3 м/с (6 мм за подшаг) решается в том же подшаге; остаток — от торцов по очереди (замер: до 2,2e-4 м
        // у наклонной капсулы, у остальных — порядка g·h²).
        Assert.True(maxDepth <= 5e-4f, $"{pose}: глубина после шага до {maxDepth:E2} м");
        Assert.True(lastSpeed <= 1e-3f && lastSpin <= 1e-2f, $"{pose}: в покое скорость {lastSpeed:E2} м/с, угловая {lastSpin:E2} рад/с");
        var end = w.Get(b);
        Assert.InRange(MathF.Min(end.EndA.Y, end.EndB.Y) - end.Radius, -1e-4f, 1e-3f);   // лежит на земле, а не висит
    }

    [Theory]
    [InlineData(3f, 4)]
    [InlineData(3f, 8)]
    [InlineData(1f, 4)]
    public void SunkBody_ExitsNoFasterThanPopSpeed_NothingHidden(float popSpeed, int substeps)
    {
        var ground = new FlatGround();
        var w = World(PhysicsSettings.Default with { PopSpeed = popSpeed, Substeps = substeps }, ground);
        // Лежачая капсула целиком на 0,3 м в земле (прошлая поза — та же: ушла в землю в позе анимации).
        var b = Simulated(w, Mass, Radius, Half, new Vector3(0, Radius - 0.3f, 0), AxisAlong(Vector3.UnitX));

        float limit = popSpeed * Dt;
        float y = w.Get(b).Position.Y, rest = Radius, speed = 0;
        float endA = -w.Get(b).EndA.Y, endB = -w.Get(b).EndB.Y;
        int ticks = 0;
        while (Depth(ground, w.Get(b)) > 1e-4f)
        {
            w.Step(Dt);
            var s = w.Get(b);
            float rise = s.Position.Y - y;
            y = s.Position.Y;
            speed = MathF.Max(speed, s.Velocity.Y);
            // Выход — не быстрее скорости выталкивания ни центром, ни торцом; скоростью сверх неё он не становится.
            Assert.True(rise <= limit * 1.001f, $"тик {ticks}: подъём {rise:E3} м при пределе {limit:E3}");
            Assert.True(endA + s.EndA.Y <= limit * 1.001f && endB + s.EndB.Y <= limit * 1.001f,
                $"тик {ticks}: торцы вышли на {endA + s.EndA.Y:E3} и {endB + s.EndB.Y:E3} м при пределе {limit:E3}");
            endA = -s.EndA.Y;
            endB = -s.EndB.Y;
            Assert.True(++ticks < 1000);
        }
        // Не застревает: торцы выходят по очереди, наклон замедляет выход (замер: 16 тиков против 12 у скорости выталкивания).
        Assert.InRange(ticks, (int)(0.3f / limit), (int)(1.5f * 0.3f / limit));
        // Скоростной проход гасит нормальную скорость торцов; остаток — от поворота торцами по очереди (замер: до 0,21 · PopSpeed).
        Assert.True(speed <= 0.4f * popSpeed, $"вертикальная скорость при выходе до {speed} м/с");

        // Вышедшее тело не подпрыгивает: скрытой энергии нет (замер: подскок до 0,5 мм при выходе с 0,3 м).
        float top = 0;
        for (int t = 0; t < 120; t++)
        {
            w.Step(Dt);
            top = MathF.Max(top, w.Get(b).Position.Y - rest);
        }
        Assert.True(top <= 1e-3f, $"после выхода подскок {top:E2} м");
    }

    [Fact]
    public void Step_DoesNotRewritePreviousPose_PushBecomesPositionOnly()
    {
        // Один подшаг на тик: прошлая поза после шага — поза до шага; выталкивание не спрятано в прошлую позу.
        var ground = new FlatGround();
        var w = World(PhysicsSettings.Default with { Substeps = 1 }, ground);
        var b = Simulated(w, Mass, Radius, Half, new Vector3(0, Radius - 0.1f, 0), AxisAlong(Vector3.UnitX));
        for (int t = 0; t < 10; t++)
        {
            var before = w.Get(b);
            w.Step(Dt);
            var after = w.Get(b);
            Assert.Equal((before.Position, before.Rotation), (after.PrevPosition, after.PrevRotation));
            Assert.Equal((before.Position, before.Rotation), (after.StartPosition, after.StartRotation));
        }
    }

    public static TheoryData<float, float, int> Slopes => new()
    {
        // μ, tg угла / μ, подшагов
        { 0.3f, 0.5f, 4 }, { 0.3f, 0.95f, 4 }, { 0.3f, 1.05f, 4 }, { 0.3f, 1.5f, 4 },
        { 0.7f, 0.5f, 4 }, { 0.7f, 0.95f, 4 }, { 0.7f, 1.05f, 4 }, { 0.7f, 1.5f, 4 },
        { 0.7f, 0.95f, 8 }, { 0.7f, 1.05f, 8 },
        { 0f, 0.3f, 4 },
    };

    [Theory]
    [MemberData(nameof(Slopes))]
    public void Slope_HoldsUpToMu_SlidesBeyondWithCoulombFriction(float mu, float ratio, int substeps)
    {
        float tan = mu > 0 ? mu * ratio : ratio;
        var slope = new SlopeGround(MathF.Atan(tan));
        var w = World(PhysicsSettings.Default with { StaticFriction = mu, Substeps = substeps }, slope);
        // Капсула вдоль склона (катиться не может), на склоне без зазора; трение о землю профиля выключено — держит только μ.
        var b = Simulated(w, Mass, Radius, Half, slope.Normal * Radius, AxisAlong(slope.Downhill), ProfileGravityOnly);
        var start = w.Get(b).Position;
        const int ticks = 120;
        for (int t = 0; t < ticks; t++) w.Step(Dt);
        var s = w.Get(b);
        float along = Vector3.Dot(s.Position - start, slope.Downhill);
        float depth = Depth(slope, s);
        Assert.True(depth <= 1e-4f, $"глубина {depth:E2} м");
        if (mu > 0 && ratio < 1)
        {
            Assert.True(MathF.Abs(along) <= 1e-3f, $"tg/μ {ratio}, μ {mu}: сползла на {along:E2} м за 1 с");
            return;
        }
        // Сверх μ скользит с трением скольжения: ускорение g·(sin θ − μ cos θ). Замер: μ скольжения — 0,93…0,96 μ (нормаль
        // одного прохода по двум торцам чуть меньше веса).
        float time = ticks * Dt, angle = MathF.Atan(tan);
        float a = 2 * along / (time * time);
        float muSliding = (MathF.Sin(angle) - a / GravityOnly.Gravity) / MathF.Cos(angle);
        Assert.True(muSliding >= 0.85f * mu - 0.01f && muSliding <= mu + 0.01f,
            $"tg/μ {ratio}, μ {mu}: μ скольжения {muSliding:F4} (сползла на {along:F4} м за {time} с)");
    }

    [Theory]
    [InlineData(ProfileGravityOnly)]
    [InlineData(0)]
    public void SlidingBody_StopsOnSlopeBelowMu_DoesNotCreep(int profile)
    {
        // Тело, пущенное вниз по склону 0,5 μ на 1 м/с, тормозится трением скольжения и встаёт — а не ползёт с установившейся
        // скоростью (трение покоя «всё или ничего» держит только почти неподвижную точку).
        const float mu = 0.7f;
        var slope = new SlopeGround(MathF.Atan(0.5f * mu));
        var w = World(ground: slope);
        var b = Simulated(w, Mass, Radius, Half, slope.Normal * Radius, AxisAlong(slope.Downhill), profile);
        var s = w.Get(b);
        s.Velocity = slope.Downhill;
        w.Set(b, s);
        var start = s.Position;
        for (int t = 0; t < 120; t++) w.Step(Dt);
        s = w.Get(b);
        Assert.True(s.Velocity.Length() <= 1e-3f, $"профиль {profile}: скорость через 1 с {s.Velocity.Length()} м/с");
        float angle = MathF.Atan(0.5f * mu);
        float coulomb = 1 / (2 * 9.81f * (mu * MathF.Cos(angle) - MathF.Sin(angle)));   // тормозной путь по Кулону
        float along = Vector3.Dot(s.Position - start, slope.Downhill);
        Assert.True(along <= 1.3f * coulomb, $"профиль {profile}: тормозной путь {along:F3} м, по Кулону {coulomb:F3}");
        for (int t = 0; t < 120; t++) w.Step(Dt);
        Assert.True(Vector3.Dot(w.Get(b).Position - s.Position, slope.Downhill) <= 1e-3f, "встав, ползёт");
    }

    [Fact]
    public void ShortCapsule_EndsDoNotDoubleNormalOrFriction()
    {
        // Шар и капсула с полудлиной 2 мм — почти одно тело; если бы второй торец решал ту же глубину заново, капсула выходила бы
        // из земли вдвое быстрее и отскакивала от удара.
        const float r = 0.1f, m = 3;
        var ground = new FlatGround();
        var w = World(ground: ground);
        var sphere = Simulated(w, m, r, 0, new Vector3(0, r - 0.2f, 0));
        var shortOne = Simulated(w, m, r, 0.002f, new Vector3(5, r - 0.2f, 0), AxisAlong(Vector3.UnitX));
        float limit = PhysicsSettings.Default.PopSpeed * Dt;
        for (int t = 0; t < 90; t++)
        {
            float y0 = w.Get(shortOne).Position.Y;
            w.Step(Dt);
            var s = w.Get(shortOne);
            Assert.True(s.Position.Y - y0 <= limit * 1.001f, $"тик {t}: короткая капсула вышла на {s.Position.Y - y0:E3} м");
            float dy = MathF.Abs(w.Get(sphere).Position.Y - s.Position.Y);
            Assert.True(dy <= 5e-4f, $"тик {t}: шар и короткая капсула разошлись на {dy:E2} м (выход за тик — {limit:E2})");
        }

        // Падение с 0,5 м: оба не отскакивают и встают на одну высоту.
        var w2 = World(ground: ground);
        var s2 = Simulated(w2, m, r, 0, new Vector3(0, r + 0.5f, 0));
        var c2 = Simulated(w2, m, r, 0.002f, new Vector3(5, r + 0.5f, 0), AxisAlong(Vector3.UnitX));
        float bounceS = 0, bounceC = 0;
        for (int t = 0; t < 120; t++)
        {
            w2.Step(Dt);
            if (t > 50)
            {
                bounceS = MathF.Max(bounceS, w2.Get(s2).Velocity.Y);
                bounceC = MathF.Max(bounceC, w2.Get(c2).Velocity.Y);
            }
        }
        Assert.True(bounceS <= 1e-3f && bounceC <= 1e-3f, $"отскок: шар {bounceS}, капсула {bounceC} м/с");
        Assert.True(MathF.Abs(w2.Get(s2).Position.Y - w2.Get(c2).Position.Y) <= 1e-4f);

        // Склон с малым μ: шар катится с проскальзыванием (μ < 2/7 tg) — трение скольжения на пределе; у короткой капсулы оно
        // не удваивается — она скатывается как шар.
        var slope = new SlopeGround(MathF.Atan(0.9f));
        var w3 = World(PhysicsSettings.Default with { StaticFriction = 0.1f }, slope);
        var s3 = Simulated(w3, m, r, 0, slope.Normal * r, null, ProfileGravityOnly);
        var c3 = Simulated(w3, m, r, 0.002f, slope.Normal * r + new Vector3(0, 0, 3), AxisAlong(Vector3.UnitZ), ProfileGravityOnly);
        for (int t = 0; t < 60; t++) w3.Step(Dt);
        float ds = Vector3.Dot(w3.Get(s3).Position, slope.Downhill), dc = Vector3.Dot(w3.Get(c3).Position - new Vector3(0, 0, 3), slope.Downhill);
        float free = 0.5f * 9.81f * MathF.Sin(MathF.Atan(0.9f)) * 0.25f;   // без трения за 0,5 с
        Assert.True(ds > 0.5f * free && ds < 0.95f * free, $"шар скатился на {ds} м, без трения — {free}");
        Assert.True(MathF.Abs(ds - dc) <= 0.02f * ds, $"шар {ds:F4} м, короткая капсула {dc:F4} м");
    }

    [Fact]
    public void StepBox_InnerCorner_EachFaceExitsAtPopSpeed_TopHolds_RiserStops()
    {
        // Ступень: ящик x ∈ [0, 1], y ∈ [−1, 0,12], z ∈ [−1, 1] на полу y = 0; подступенок — плоскость x = 0.
        var step = new StepGround(new Vector3(0, -1, -1), new Vector3(1, 0.12f, 1));
        const float r = 0.1f;
        float limit = PhysicsSettings.Default.PopSpeed * Dt;

        // Во внутреннем углу шар утоплен и в пол, и в подступенок — у каждой грани своя старая глубина, каждая уходит не быстрее
        // скорости выталкивания.
        var w = World(ground: step);
        var ball = Simulated(w, 2, r, 0, new Vector3(-(r - 0.06f), r - 0.05f, 0));
        float floor0 = step.Depth(w.Get(ball).Position, r, 0, out _), riser0 = step.Depth(w.Get(ball).Position, r, 1, out var nRiser);
        Assert.True(floor0 > 0.04f && riser0 > 0.04f && nRiser.X < -0.99f, $"пол {floor0}, подступенок {riser0}, нормаль {nRiser}");
        for (int t = 0; t < 60; t++)
        {
            w.Step(Dt);
            var p = w.Get(ball).Position;
            float floor = step.Depth(p, r, 0, out _), riser = step.Depth(p, r, 1, out _);
            Assert.True(floor0 - floor <= limit * 1.001f && riser0 - riser <= limit * 1.001f,
                $"тик {t}: пол {floor0 - floor:E3}, подступенок {riser0 - riser:E3} м за тик при пределе {limit:E3}");
            floor0 = MathF.Max(floor, 0);
            riser0 = MathF.Max(riser, 0);
        }
        Assert.True(floor0 <= 1e-4f && riser0 <= 1e-4f, $"осталось: пол {floor0}, подступенок {riser0}");

        // Лёжа на верху ступени вдоль кромки, ось в 1 см от неё (торцы на 7 см свисают за кромку) — стоит на верху: над верхом
        // ящика ближайшая точка — верх, нормаль вверх.
        var w2 = World(ground: step);
        var onTop = Simulated(w2, 4, 0.08f, 0.2f, new Vector3(0.01f, 0.12f + 0.08f + 0.05f, 0), AxisAlong(Vector3.UnitZ));
        for (int t = 0; t < 240; t++) w2.Step(Dt);
        var top = w2.Get(onTop);
        Assert.InRange(top.Position.Y, 0.12f + 0.08f - 1e-4f, 0.12f + 0.08f + 1e-3f);
        Assert.InRange(top.Position.X, 0.01f - 1e-3f, 0.01f + 1e-3f);   // не скатилась с кромки
        Assert.True(top.Velocity.Length() <= 1e-3f, $"на верху ступени скорость {top.Velocity}");

        // Шар катится по полу в подступенок (трение о землю профиля выключено) — встаёт у него, не взбирается и не проходит.
        var w3 = World(ground: step);
        var roll = Simulated(w3, 2, r, 0, new Vector3(-1, r, 0), null, ProfileGravityOnly);
        var s = w3.Get(roll);
        s.Velocity = new Vector3(3, 0, 0);
        w3.Set(roll, s);
        for (int t = 0; t < 120; t++)
        {
            w3.Step(Dt);
            var p = w3.Get(roll).Position;
            Assert.True(p.X <= -r + 1e-4f, $"тик {t}: шар в подступенке, x = {p.X}");
            Assert.True(p.Y <= r + 1e-3f, $"тик {t}: шар взбирается, y = {p.Y}");
        }
        Assert.True(MathF.Abs(w3.Get(roll).Velocity.X) <= 1e-3f, $"у подступенка скорость {w3.Get(roll).Velocity}");
    }
}
