using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Позиционные ограничения XPBD (Müller и др., 2020; идеи — <c>Xpbd</c> SWORD 2eab13b) над <c>ref BodyState</c>: одно решение
/// на подшаг (λ не накапливается между вызовами), податливость α в физических единицах (м/Н или рад/(Н·м)) — жёсткость не
/// зависит от числа подшагов: знаменатель <c>w + α/h²</c>.
/// <para>
/// Две формы каждой функции: тело — тело (<c>a</c> получает <c>+λ·n</c>, <c>b</c> — <c>−λ·n</c>) и тело — мир (перегрузка без
/// второго тела: неподвижная сторона бесконечной массы — мир или кинематическое тело). Кинематическое тело передаётся только
/// миром: у него конечная масса, и функция его сдвинет. <c>a</c> и <c>b</c> — разные тела. Точки — в мире, нормаль и
/// направления — единичные, <c>h</c> — конечный и больше 0 (не проверяется: горячий путь). Памяти не выделяют.
/// </para>
/// </summary>
public static class Xpbd
{
    /// <summary>Порог касательного сдвига трения, м: меньше — направление не определено, трение не решается.</summary>
    public const float FrictionEpsilon = 1e-7f;

    /// <summary>Порог длины для <see cref="Attach(ref BodyState, Vector3, ref BodyState, Vector3, float, float)"/>, м: точки ближе — уже сведены.</summary>
    public const float AttachEpsilon = 1e-6f;

    /// <summary>Порог угла для <see cref="Angular(ref BodyState, ref BodyState, Vector3, float, float)"/>, рад: меньше — поворачивать нечего.</summary>
    public const float AngularEpsilon = 1e-6f;

    /// <summary>
    /// Двустороннее ограничение вдоль <paramref name="n"/>: нарушение <paramref name="c"/> (м) сводится к нулю с
    /// податливостью <paramref name="compliance"/>: <c>λ = −c / (w_a + w_b + α/h²)</c>, где <c>w</c> — обратная масса тела в
    /// точке вдоль n (<see cref="BodyState.InvMassAt"/>); <c>a</c> получает коррекцию <c>+λn</c> в точке <paramref name="pa"/>,
    /// <c>b</c> — <c>−λn</c> в <paramref name="pb"/> (<see cref="Dynamics.Correct"/>). Поправка делится по обратным массам.
    /// Возвращает λ, кг·м (0 — обе стороны неподвижны).
    /// </summary>
    public static float Along(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, Vector3 n, float c, float compliance, float h)
    {
        Vector3 ra = pa - a.Position, rb = pb - b.Position;
        float w = a.InvMassAt(ra, n) + b.InvMassAt(rb, n);
        if (!(w > 0)) return 0;
        float lambda = -c / (w + compliance / (h * h));
        var p = n * lambda;
        Dynamics.Correct(ref a, p, ra);
        Dynamics.Correct(ref b, -p, rb);
        return lambda;
    }

    /// <summary><see cref="Along(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float)"/> с миром: двигается только <paramref name="a"/>.</summary>
    public static float Along(ref BodyState a, Vector3 pa, Vector3 n, float c, float compliance, float h)
    {
        var ra = pa - a.Position;
        float w = a.InvMassAt(ra, n);
        if (!(w > 0)) return 0;
        float lambda = -c / (w + compliance / (h * h));
        Dynamics.Correct(ref a, n * lambda, ra);
        return lambda;
    }

    /// <summary>
    /// Свести точку <paramref name="pa"/> тела <paramref name="a"/> с точкой <paramref name="pb"/> тела <paramref name="b"/>
    /// (точка сустава): <see cref="Along(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float)"/>
    /// вдоль <c>pa − pb</c> с нарушением — расстоянием. Ближе <see cref="AttachEpsilon"/> — ничего. Возвращает λ (≤ 0).
    /// </summary>
    public static float Attach(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, float compliance, float h)
    {
        var d = pa - pb;
        float len = d.Length();
        if (len < AttachEpsilon) return 0;
        return Along(ref a, pa, ref b, pb, d / len, len, compliance, h);
    }

    /// <summary>Свести точку <paramref name="pa"/> тела с неподвижной точкой мира <paramref name="anchor"/>.</summary>
    public static float Attach(ref BodyState a, Vector3 pa, Vector3 anchor, float compliance, float h)
    {
        var d = pa - anchor;
        float len = d.Length();
        if (len < AttachEpsilon) return 0;
        return Along(ref a, pa, d / len, len, compliance, h);
    }

    /// <summary>
    /// Повернуть <paramref name="a"/> на вектор поворота <paramref name="theta"/> (мир, рад) относительно
    /// <paramref name="b"/> (предел сустава): <c>λ = |θ| / (n·I_a⁻¹n + n·I_b⁻¹n + α/h²)</c>, <c>a</c> поворачивается на
    /// <c>I_a⁻¹ λn</c>, <c>b</c> — на <c>−I_b⁻¹ λn</c> (<see cref="Dynamics.Rotate"/>, с обрезкой угла). Угол меньше
    /// <see cref="AngularEpsilon"/> — ничего. Возвращает λ, кг·м²·рад.
    /// </summary>
    public static float Angular(ref BodyState a, ref BodyState b, Vector3 theta, float compliance, float h)
    {
        float angle = theta.Length();
        if (angle < AngularEpsilon) return 0;
        var n = theta / angle;
        float w = Vector3.Dot(n, a.InvInertiaWorld(n)) + Vector3.Dot(n, b.InvInertiaWorld(n));
        if (!(w > 0)) return 0;
        float lambda = angle / (w + compliance / (h * h));
        var p = n * lambda;
        var da = a.InvInertiaWorld(p);
        var db = b.InvInertiaWorld(p);
        Dynamics.Rotate(ref a, da);
        Dynamics.Rotate(ref b, -db);
        return lambda;
    }

    /// <summary>Повернуть тело <paramref name="a"/> на <paramref name="theta"/> относительно мира.</summary>
    public static float Angular(ref BodyState a, Vector3 theta, float compliance, float h)
    {
        float angle = theta.Length();
        if (angle < AngularEpsilon) return 0;
        var n = theta / angle;
        float w = Vector3.Dot(n, a.InvInertiaWorld(n));
        if (!(w > 0)) return 0;
        float lambda = angle / (w + compliance / (h * h));
        Dynamics.Rotate(ref a, a.InvInertiaWorld(n * lambda));
        return lambda;
    }

    /// <summary>
    /// Односторонний контакт: точку <paramref name="pa"/> тела <paramref name="a"/> выталкивает из точки <paramref name="pb"/>
    /// тела <paramref name="b"/> вдоль <paramref name="n"/> — нормали от b к a. <paramref name="c"/> — решаемое нарушение:
    /// отрицательное — проникновение (не обязательно вся глубина: вызывающий может ограничить темп выхода), <c>c ≥ 0</c> или
    /// NaN — контакта нет. <c>λ = min(−c / (w + α/h²), maxLambda)</c> — контакт только отталкивает, λ ≥ 0.
    /// <paramref name="maxLambda"/> — предел этого вызова, кг·м (сила F за подшаг — <c>F·h²</c>, импульс J — <c>J·h</c>);
    /// без предела — <see cref="float.PositiveInfinity"/>; ноль, отрицательный или NaN — коррекции нет. Возвращает
    /// применённую λ ≥ 0, кг·м; импульс — <c>λ/h</c>.
    /// </summary>
    public static float Contact(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, Vector3 n, float c, float compliance, float h,
        float maxLambda = float.PositiveInfinity)
    {
        if (!(c < 0) || !(maxLambda > 0)) return 0;
        Vector3 ra = pa - a.Position, rb = pb - b.Position;
        float w = a.InvMassAt(ra, n) + b.InvMassAt(rb, n);
        if (!(w > 0)) return 0;
        float lambda = MathF.Min(-c / (w + compliance / (h * h)), maxLambda);
        if (!(lambda > 0)) return 0;
        var p = n * lambda;
        Dynamics.Correct(ref a, p, ra);
        Dynamics.Correct(ref b, -p, rb);
        return lambda;
    }

    /// <summary>Односторонний контакт тела <paramref name="a"/> с миром (нормаль — от мира к телу).</summary>
    public static float Contact(ref BodyState a, Vector3 pa, Vector3 n, float c, float compliance, float h,
        float maxLambda = float.PositiveInfinity)
    {
        if (!(c < 0) || !(maxLambda > 0)) return 0;
        var ra = pa - a.Position;
        float w = a.InvMassAt(ra, n);
        if (!(w > 0)) return 0;
        float lambda = MathF.Min(-c / (w + compliance / (h * h)), maxLambda);
        if (!(lambda > 0)) return 0;
        Dynamics.Correct(ref a, n * lambda, ra);
        return lambda;
    }

    /// <summary>
    /// Трение покоя контакта (Müller и др., 2020, §3.5) — «всё или ничего» по Кулону. <paramref name="pa"/>,
    /// <paramref name="pb"/> — точки контакта на телах сейчас (после нормальной коррекции), <paramref name="n"/> — нормаль от
    /// b к a, <paramref name="lambdaN"/> — нормальная λ этого контакта в этом подшаге (<see cref="Contact(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float, float)"/>).
    /// <para>
    /// Касательное нарушение — сдвиг материальной точки за подшаг (<see cref="BodyState.PrevPoint"/>: вращение на месте — тоже
    /// скольжение) у a минус у b, без нормальной части. Меньше <see cref="FrictionEpsilon"/> — ничего. Направление
    /// <c>t = C_t/|C_t|</c>, кандидат <c>λ_t = |C_t| / (w_t + α/h²)</c>. <c>λ_t ≤ μs·λ_n</c> — к a прикладывается
    /// <c>−λ_t·t</c>, к b <c>+λ_t·t</c>, касательный сдвиг отменяется; больше — трение покоя не держит, коррекции нет
    /// (скольжение гасится на уровне скоростей: трение о землю профиля). <c>λ_n ≤ 0</c>, NaN или <c>μs ≤ 0</c> — ничего.
    /// Возвращает применённую λ_t ≥ 0, кг·м.
    /// </para>
    /// </summary>
    public static float Friction(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, Vector3 n, float lambdaN, float muStatic,
        float compliance, float h)
    {
        if (!(lambdaN > 0) || !(muStatic > 0)) return 0;
        var d = pa - a.PrevPoint(pa) - (pb - b.PrevPoint(pb));
        var ct = d - n * Vector3.Dot(d, n);
        float slip = ct.Length();
        if (!(slip >= FrictionEpsilon)) return 0;
        var t = ct / slip;
        Vector3 ra = pa - a.Position, rb = pb - b.Position;
        float w = a.InvMassAt(ra, t) + b.InvMassAt(rb, t);
        if (!(w > 0)) return 0;
        float lambda = slip / (w + compliance / (h * h));
        if (!(lambda <= muStatic * lambdaN)) return 0;
        var p = t * lambda;
        Dynamics.Correct(ref a, -p, ra);
        Dynamics.Correct(ref b, p, rb);
        return lambda;
    }

    /// <summary>Трение покоя тела <paramref name="a"/> о неподвижный мир (точка мира не сдвигается).</summary>
    public static float Friction(ref BodyState a, Vector3 pa, Vector3 n, float lambdaN, float muStatic, float compliance, float h)
    {
        if (!(lambdaN > 0) || !(muStatic > 0)) return 0;
        var d = pa - a.PrevPoint(pa);
        var ct = d - n * Vector3.Dot(d, n);
        float slip = ct.Length();
        if (!(slip >= FrictionEpsilon)) return 0;
        var t = ct / slip;
        var ra = pa - a.Position;
        float w = a.InvMassAt(ra, t);
        if (!(w > 0)) return 0;
        float lambda = slip / (w + compliance / (h * h));
        if (!(lambda <= muStatic * lambdaN)) return 0;
        Dynamics.Correct(ref a, -t * lambda, ra);
        return lambda;
    }
}
