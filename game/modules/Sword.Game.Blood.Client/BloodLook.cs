using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Game.Blood;

/// <summary>
/// Числа функции свежести крови (ручки F3 «Кровь»): палитра L2 — густая (226, 24, 28) sRGB, тонкий край (126, 7, 0), засохшая на коже —
/// (0.06, 0.008, 0.006) линейно (WR §4.7); блеск уходит за <see cref="GlossStart"/>…<see cref="GlossEnd"/> с, темнеет и буреет за
/// <see cref="DarkStart"/>…<see cref="DarkEnd"/> с (решение 6); толстая сохнет в <see cref="ThickSlow"/> раз дольше (SWORD 1: DryThick).
/// Раскладка — корневые константы <c>blood_look.hlsli</c> (<see cref="Slots"/> подряд, по порядку полей).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BloodLookParams
{
    /// <summary>Толщина, мм, с которой кровь «густая» (цвет и замедление высыхания — полностью).</summary>
    public float ThickMm;
    /// <summary>Толщина, мм, при которой покрытие 1 − 1/e.</summary>
    public float CoverMm;
    public float ThickSlow;
    public float GlossStart, GlossEnd, DarkStart, DarkEnd;
    public float WetRoughness, DryRoughness;
    public Vector3 Thick, Thin, Dry;

    /// <summary>Скаляров в раскладке.</summary>
    public const int Slots = 18;

    public static BloodLookParams Default => new()
    {
        ThickMm = 0.3f, CoverMm = 0.02f, ThickSlow = 2f,
        GlossStart = 10, GlossEnd = 60, DarkStart = 60, DarkEnd = 180,
        WetRoughness = 0.12f, DryRoughness = 0.6f,
        Thick = BloodLook.Linear(226, 24, 28), Thin = BloodLook.Linear(126, 7, 0), Dry = new Vector3(0.06f, 0.008f, 0.006f),
    };
}

/// <summary>Вид крови: линейное альбедо, покрытие (доля поверхности под кровью), шероховатость GGX, влажность (1 — свежая) и засохлость.</summary>
public readonly record struct BloodLookResult(Vector3 Albedo, float Coverage, float Roughness, float Wet, float Dry);

/// <summary>
/// Функция свежести — одна для кожи и пола: возраст, с, и толщина, мм → вид. Тот же расчёт в тех же выражениях и порядке — HLSL
/// <c>BloodLookEval</c> (<c>Shaders/blood/blood_look.hlsli</c>).
/// </summary>
public static class BloodLook
{
    public const string ShaderInclude = "blood/blood_look.hlsli";

    public static BloodLookResult Evaluate(float age, float thicknessMm, in BloodLookParams p)
    {
        float thick = Saturate(thicknessMm / p.ThickMm);
        float slow = 1 + (p.ThickSlow - 1) * thick;
        float a = age / slow;
        float wet = 1 - Smooth(p.GlossStart, p.GlossEnd, a);
        float dry = Smooth(p.DarkStart, p.DarkEnd, a);
        var fresh = p.Thin + (p.Thick - p.Thin) * thick;
        var albedo = fresh + (p.Dry - fresh) * dry;
        float roughness = p.WetRoughness + (p.DryRoughness - p.WetRoughness) * (1 - wet);
        float coverage = 1 - MathF.Exp(-thicknessMm / p.CoverMm);
        return new BloodLookResult(albedo, coverage, roughness, wet, dry);
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
        into[1] = p.CoverMm;
        into[2] = p.ThickSlow;
        into[3] = p.GlossStart;
        into[4] = p.GlossEnd;
        into[5] = p.DarkStart;
        into[6] = p.DarkEnd;
        into[7] = p.WetRoughness;
        into[8] = p.DryRoughness;
        into[9] = p.Thick.X;
        into[10] = p.Thick.Y;
        into[11] = p.Thick.Z;
        into[12] = p.Thin.X;
        into[13] = p.Thin.Y;
        into[14] = p.Thin.Z;
        into[15] = p.Dry.X;
        into[16] = p.Dry.Y;
        into[17] = p.Dry.Z;
    }
}
