using System.Numerics;

namespace Sword.Engine.Physics;

/// <summary>
/// Позиционные ограничения XPBD (Müller и др., 2020) над <c>ref BodyState</c>: λ не накапливается между вызовами,
/// податливость α в физических единицах — знаменатель <c>w + α/h²</c>, жёсткость не зависит от числа подшагов. Перегрузка
/// без второго тела — неподвижная сторона (мир или кинематическое тело). Аргументы не проверяют; памяти не выделяют.
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
    /// Двустороннее ограничение вдоль <paramref name="n"/>: нарушение <paramref name="c"/>, м, сводится к нулю,
    /// <c>λ = −c / (w_a + w_b + α/h²)</c>; a получает <c>+λn</c>, b — <c>−λn</c>. Возвращает λ, кг·м.
    /// </summary>
    public static float Along(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, Vector3 n, float c, float compliance, float h)
    {
        Vector3 rna = Vector3.Cross(pa - a.Position, n), rnb = Vector3.Cross(pb - b.Position, n);
        Vector3 ia = a.InvInertiaWorld(rna), ib = b.InvInertiaWorld(rnb);
        float w = a.InvMass + Vector3.Dot(rna, ia) + b.InvMass + Vector3.Dot(rnb, ib);
        if (!(w > 0)) return 0;
        float lambda = -c / (w + compliance / (h * h));
        Shift(ref a, n * lambda, ia * lambda);
        Shift(ref b, -n * lambda, ib * -lambda);
        return lambda;
    }

    /// <summary><see cref="Along(ref BodyState, Vector3, ref BodyState, Vector3, Vector3, float, float, float)"/> с миром: двигается только <paramref name="a"/>.</summary>
    public static float Along(ref BodyState a, Vector3 pa, Vector3 n, float c, float compliance, float h)
    {
        var rna = Vector3.Cross(pa - a.Position, n);
        var ia = a.InvInertiaWorld(rna);
        float w = a.InvMass + Vector3.Dot(rna, ia);
        if (!(w > 0)) return 0;
        float lambda = -c / (w + compliance / (h * h));
        Shift(ref a, n * lambda, ia * lambda);
        return lambda;
    }

    /// <summary>Свести точки сустава вдоль <c>pa − pb</c>; ближе <see cref="AttachEpsilon"/> — ничего. Возвращает λ (≤ 0).</summary>
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
    /// Повернуть <paramref name="a"/> на <paramref name="theta"/> (мир, рад) относительно <paramref name="b"/>:
    /// <c>λ = |θ| / (n·I_a⁻¹n + n·I_b⁻¹n + α/h²)</c>. Угол меньше <see cref="AngularEpsilon"/> — ничего. Возвращает λ.
    /// </summary>
    public static float Angular(ref BodyState a, ref BodyState b, Vector3 theta, float compliance, float h)
    {
        float angle = theta.Length();
        if (angle < AngularEpsilon) return 0;
        var n = theta / angle;
        Vector3 ia = a.InvInertiaWorld(n), ib = b.InvInertiaWorld(n);
        float w = Vector3.Dot(n, ia) + Vector3.Dot(n, ib);
        if (!(w > 0)) return 0;
        float lambda = angle / (w + compliance / (h * h));
        Dynamics.Rotate(ref a, ia * lambda);
        Dynamics.Rotate(ref b, ib * -lambda);
        return lambda;
    }

    /// <summary>Повернуть тело <paramref name="a"/> на <paramref name="theta"/> относительно мира.</summary>
    public static float Angular(ref BodyState a, Vector3 theta, float compliance, float h)
    {
        float angle = theta.Length();
        if (angle < AngularEpsilon) return 0;
        var n = theta / angle;
        var ia = a.InvInertiaWorld(n);
        float w = Vector3.Dot(n, ia);
        if (!(w > 0)) return 0;
        float lambda = angle / (w + compliance / (h * h));
        Dynamics.Rotate(ref a, ia * lambda);
        return lambda;
    }

    /// <summary>
    /// Односторонний контакт вдоль <paramref name="n"/> (от b к a): <paramref name="c"/> &lt; 0 — решаемое проникновение,
    /// <c>c ≥ 0</c> или NaN — контакта нет. <c>λ = min(−c / (w + α/h²), maxLambda)</c> ≥ 0; <paramref name="maxLambda"/>, кг·м —
    /// предел вызова (сила F — <c>F·h²</c>, импульс J — <c>J·h</c>). Возвращает λ; импульс — <c>λ/h</c>.
    /// </summary>
    public static float Contact(ref BodyState a, Vector3 pa, ref BodyState b, Vector3 pb, Vector3 n, float c, float compliance, float h,
        float maxLambda = float.PositiveInfinity)
    {
        if (!(c < 0) || !(maxLambda > 0)) return 0;
        Vector3 rna = Vector3.Cross(pa - a.Position, n), rnb = Vector3.Cross(pb - b.Position, n);
        Vector3 ia = a.InvInertiaWorld(rna), ib = b.InvInertiaWorld(rnb);
        float w = a.InvMass + Vector3.Dot(rna, ia) + b.InvMass + Vector3.Dot(rnb, ib);
        if (!(w > 0)) return 0;
        float lambda = MathF.Min(-c / (w + compliance / (h * h)), maxLambda);
        if (!(lambda > 0)) return 0;
        Shift(ref a, n * lambda, ia * lambda);
        Shift(ref b, -n * lambda, ib * -lambda);
        return lambda;
    }

    /// <summary>Односторонний контакт тела <paramref name="a"/> с миром (нормаль — от мира к телу).</summary>
    public static float Contact(ref BodyState a, Vector3 pa, Vector3 n, float c, float compliance, float h,
        float maxLambda = float.PositiveInfinity)
    {
        if (!(c < 0) || !(maxLambda > 0)) return 0;
        var rna = Vector3.Cross(pa - a.Position, n);
        var ia = a.InvInertiaWorld(rna);
        float w = a.InvMass + Vector3.Dot(rna, ia);
        if (!(w > 0)) return 0;
        float lambda = MathF.Min(-c / (w + compliance / (h * h)), maxLambda);
        if (!(lambda > 0)) return 0;
        Shift(ref a, n * lambda, ia * lambda);
        return lambda;
    }

    /// <summary>
    /// Трение покоя контакта (Müller и др., 2020, §3.5) — «всё или ничего» по Кулону: касательный сдвиг материальных точек за
    /// подшаг (<see cref="BodyState.PrevPoint"/>) отменяется, если <c>λ_t ≤ μs·λ_n</c>, иначе коррекции нет (скольжение гасится
    /// скоростями). Возвращает λ_t ≥ 0, кг·м.
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
        Vector3 rta = Vector3.Cross(pa - a.Position, t), rtb = Vector3.Cross(pb - b.Position, t);
        Vector3 ia = a.InvInertiaWorld(rta), ib = b.InvInertiaWorld(rtb);
        float w = a.InvMass + Vector3.Dot(rta, ia) + b.InvMass + Vector3.Dot(rtb, ib);
        if (!(w > 0)) return 0;
        float lambda = slip / (w + compliance / (h * h));
        if (!(lambda <= muStatic * lambdaN)) return 0;
        Shift(ref a, -t * lambda, ia * -lambda);
        Shift(ref b, t * lambda, ib * lambda);
        return lambda;
    }

    /// <summary>Трение покоя тела <paramref name="a"/> о неподвижный мир (точка мира не сдвигается).</summary>
    public static float Friction(ref BodyState a, Vector3 pa, Vector3 n, float lambdaN, float muStatic, float compliance, float h) =>
        Friction(ref a, pa, n, lambdaN, muStatic, compliance, h, out _);

    /// <summary>
    /// То же; <paramref name="turn"/> — поворот тела поправкой, рад (не держит — 0): по нему мир считает, куда поворот увёл
    /// торцы по нормали.
    /// </summary>
    public static float Friction(ref BodyState a, Vector3 pa, Vector3 n, float lambdaN, float muStatic, float compliance, float h, out Vector3 turn)
    {
        turn = default;
        if (!(lambdaN > 0) || !(muStatic > 0)) return 0;
        var d = pa - a.PrevPoint(pa);
        var ct = d - n * Vector3.Dot(d, n);
        float slip = ct.Length();
        if (!(slip >= FrictionEpsilon)) return 0;
        var t = ct / slip;
        var rta = Vector3.Cross(pa - a.Position, t);
        var ia = a.InvInertiaWorld(rta);
        float w = a.InvMass + Vector3.Dot(rta, ia);
        if (!(w > 0)) return 0;
        float lambda = slip / (w + compliance / (h * h));
        if (!(lambda <= muStatic * lambdaN)) return 0;
        turn = ia * -lambda;
        Shift(ref a, -t * lambda, turn);
        return lambda;
    }

    /// <summary>Коррекция импульсом, когда <c>I⁻¹(r × p)</c> уже известен: центр — на <c>p / m</c>, поворот — на <paramref name="dTheta"/>.</summary>
    private static void Shift(ref BodyState b, Vector3 impulse, Vector3 dTheta)
    {
        b.Position += impulse * b.InvMass;
        Dynamics.Rotate(ref b, dTheta);
    }
}
