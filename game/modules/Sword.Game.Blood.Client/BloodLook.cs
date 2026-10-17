using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Blood;

/// <summary>
/// Числа функции свежести крови (ручки F3 «Кровь»). Кровь — поглощающий слой поверх подложки (Бер — Ламберт, BVR §5.3): пропускание
/// туда и обратно e^(−2σh) по каналам — сине-зелёный гаснет за 30–50 мкм, красный проходит доли миллиметра; рассеянное самим слоем —
/// альбедо бесконечно толстой крови <see cref="Deep"/> (58, 4, 6) sRGB × (1 − пропускание). Тонкий слой — подложка видна, темнее и
/// красная; толстый — глубокий тёмно-красный; засохшая буреет к <see cref="Dry"/> (WR §4.7) и мутнеет (<see cref="DryAbsorb"/>). Блеск
/// уходит за <see cref="GlossStart"/>…<see cref="GlossEnd"/> с, темнеет за <see cref="DarkStart"/>…<see cref="DarkEnd"/> с (решение 6);
/// толстая сохнет в <see cref="ThickSlow"/> раз дольше. Раскладка — корневые константы <c>blood_look.hlsli</c> (<see cref="Slots"/> подряд,
/// по порядку полей).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BloodLookParams
{
    /// <summary>Толщина, мм, с которой кровь «густая» (замедление высыхания — полностью).</summary>
    public float ThickMm;
    /// <summary>Мягкий край: покрытие (блеск плёнки) — smoothstep(0, EdgeMm, h).</summary>
    public float EdgeMm;
    public float ThickSlow;
    public float GlossStart, GlossEnd, DarkStart, DarkEnd;
    public float WetRoughness, DryRoughness;
    /// <summary>Альбедо толстого слоя (свежая), поглощение σ по каналам, 1/мм, альбедо засохшей.</summary>
    public Vector3 Deep, Absorb, Dry;
    /// <summary>Засохшая поглощает сильнее во всех каналах, 1/мм (тонкая корка — бурая, а не красная).</summary>
    public float DryAbsorb;

    /// <summary>Скаляров в раскладке.</summary>
    public const int Slots = 19;

    public static BloodLookParams Default => new()
    {
        ThickMm = 0.3f, EdgeMm = 0.02f, ThickSlow = 2f,
        GlossStart = 10, GlossEnd = 60, DarkStart = 60, DarkEnd = 180,
        WetRoughness = 0.08f, DryRoughness = 0.6f,
        Deep = BloodLook.Linear(58, 4, 6), Absorb = new Vector3(1.5f, 40, 55), Dry = new Vector3(0.055f, 0.010f, 0.008f),
        DryAbsorb = 10,
    };
}

/// <summary>
/// Вид крови: альбедо слоя (рассеянное толстой кровью), пропускание до подложки и обратно по каналам, покрытие (доля поверхности под
/// плёнкой — блеск), шероховатость GGX, влажность (1 — свежая) и засохлость. Слой поверх подложки s: s·<see cref="Mul"/> + <see cref="Add"/>.
/// </summary>
public readonly record struct BloodLookResult(Vector3 Albedo, Vector3 Transmission, float Coverage, float Roughness, float Wet, float Dry)
{
    /// <summary>Множитель подложки: 1 − покрытие·(1 − пропускание).</summary>
    public Vector3 Mul => Vector3.One - Coverage * (Vector3.One - Transmission);

    /// <summary>Своё альбедо слоя: покрытие · альбедо · (1 − пропускание).</summary>
    public Vector3 Add => Coverage * Albedo * (Vector3.One - Transmission);
}

/// <summary>
/// Функция свежести — одна для кожи и пола: возраст, с, и толщина, мм → вид. Тот же расчёт в тех же выражениях и порядке — HLSL
/// <c>BloodLookEval</c> (<c>Shaders/blood/blood_look.hlsli</c>).
/// </summary>
public static class BloodLook
{
    public const string ShaderInclude = "blood/blood_look.hlsli";

    public static BloodLookResult Evaluate(float age, float thicknessMm, in BloodLookParams p)
    {
        float h = MathF.Max(thicknessMm, 0);
        float thick = Saturate(h / p.ThickMm);
        float slow = 1 + (p.ThickSlow - 1) * thick;
        float a = age / slow;
        float wet = 1 - Smooth(p.GlossStart, p.GlossEnd, a);
        float dry = Smooth(p.DarkStart, p.DarkEnd, a);
        var albedo = p.Deep + (p.Dry - p.Deep) * dry;
        var sigma = p.Absorb + new Vector3(p.DryAbsorb * dry);
        var transmission = new Vector3(MathF.Exp(-2 * sigma.X * h), MathF.Exp(-2 * sigma.Y * h), MathF.Exp(-2 * sigma.Z * h));
        float roughness = p.WetRoughness + (p.DryRoughness - p.WetRoughness) * (1 - wet);
        float coverage = Smooth(0, p.EdgeMm, h);
        return new BloodLookResult(albedo, transmission, coverage, roughness, wet, dry);
    }

    /// <summary>
    /// Оттенок мокрой подложки: пол под кровью и в ореоле — множитель lerp(1, darken·WetTint, wet) по каналам. Сыворотка впитывается
    /// тёплой: синий и зелёный гаснут сильнее красного — подложка темнее и насыщеннее, но ни в одном канале не светлее (каналы ≤ 1).
    /// </summary>
    public static readonly Vector3 WetTint = new(1f, 0.88f, 0.8f);

    /// <summary>Множитель мокрой подложки (HLSL <c>BloodWetMul</c>): wet 0 — 1, wet 1 — darken·<see cref="WetTint"/>; darken зажат в [0, 1].</summary>
    public static Vector3 WetMul(float wet, float darken)
    {
        float w = Saturate(wet);
        var full = Saturate(darken) * WetTint;
        return Vector3.One + (full - Vector3.One) * w;
    }

    /// <summary>Гладкая ступенька (как <c>smoothstep</c> HLSL).</summary>
    public static float Smooth(float a, float b, float x)
    {
        float t = b > a ? Saturate((x - a) / (b - a)) : x >= a ? 1 : 0;   // a = b с панели ручек: ступенька, а не 0/0
        return t * t * (3 - 2 * t);
    }

    public static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    /// <summary>Цвет sRGB 0…255 → линейный.</summary>
    public static Vector3 Linear(int r, int g, int b) => new(Channel(r), Channel(g), Channel(b));

    private static float Channel(int c)
    {
        double s = c / 255.0;
        return (float)(s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4));
    }

    /// <summary>Корневые константы в порядке полей (<see cref="BloodLookParams.Slots"/>).</summary>
    public static void Write(in BloodLookParams p, Span<float> into)
    {
        into[0] = p.ThickMm;
        into[1] = p.EdgeMm;
        into[2] = p.ThickSlow;
        into[3] = p.GlossStart;
        into[4] = p.GlossEnd;
        into[5] = p.DarkStart;
        into[6] = p.DarkEnd;
        into[7] = p.WetRoughness;
        into[8] = p.DryRoughness;
        into[9] = p.Deep.X;
        into[10] = p.Deep.Y;
        into[11] = p.Deep.Z;
        into[12] = p.Absorb.X;
        into[13] = p.Absorb.Y;
        into[14] = p.Absorb.Z;
        into[15] = p.Dry.X;
        into[16] = p.Dry.Y;
        into[17] = p.Dry.Z;
        into[18] = p.DryAbsorb;
    }
}
