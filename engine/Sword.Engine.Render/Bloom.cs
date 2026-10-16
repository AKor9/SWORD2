using System.Globalization;
using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// Настройки bloom: интенсивность (UE BloomIntensity; 0 — как выключен) и порог по яркости входа тонмапа
/// (UE BloomThreshold; −1 и меньше — без порога).
/// </summary>
public readonly record struct BloomSettings(bool Enabled, float Intensity, float Threshold)
{
    /// <summary>Как в уровне LIVE-2 с факелом (эталон вида): интенсивность 10, порог 1.</summary>
    public static BloomSettings Default => new(true, 10f, 1f);

    /// <summary>Выключен (интенсивность и порог — по умолчанию).</summary>
    public static BloomSettings Off => Default with { Enabled = false };

    /// <summary>Проходы bloom в кадре будут: включён и интенсивность больше нуля.</summary>
    public bool Active => Enabled && Intensity > 0;
}

/// <summary>Bloom на снимке: настройки и яркость добавки bloom ко входу тонмапа по цели ½ экрана.</summary>
public sealed record BloomCapture(BloomSettings Settings, int Width, int Height, float MaxLuminance, float MeanLuminance, int NonFinite)
{
    /// <summary>Добавка в ложных цветах той же шкалы, что карта HDR (<see cref="HdrCapture.Path"/>), ½ экрана; null — не заказана.</summary>
    public string? Path { get; init; }

    /// <summary>Сырая добавка рядом с картой (<c>.bin</c>: RGBA half на пиксель ½ экрана, строки сверху вниз, без экспозиции).</summary>
    public string? RawPath { get; init; }
}

/// <summary>
/// Bloom UE 5.8 «сумма гауссиан» — числа и CPU-эталон <c>common/post.hlsl</c>. Порог на ½ экрана → уменьшения вдвое до 1/64 →
/// шесть гауссиан от широкой к узкой: по x, затем по y с усилением ступени и прибавкой суммы более широких. Итог — ½ экрана.
/// </summary>
public static class BloomKernel
{
    /// <summary>Ступеней (гауссиан) и уровней цепочки: ½ … 1/64.</summary>
    public const int Stages = 6;

    /// <summary>Множитель размеров ступеней (BloomSizeScale UE).</summary>
    public const float SizeScale = 4f;

    /// <summary>Множитель интенсивности метода SOG (BloomGaussianIntensity UE).</summary>
    public const float GaussianIntensity = 1f;

    /// <summary>Спад веса: exp(−Falloff·(x/R)²), то есть σ = R/√(2·16.7) ≈ R/5.78 (постоянная UE).</summary>
    public const float Falloff = 16.7f;

    /// <summary>
    /// Предел радиуса ядра, тексели уровня: 127 — предел UE с динамическим циклом; при умолчании UE (31) широкие ступени
    /// перестают расти с экраном от ~1550 px.
    /// </summary>
    public const int MaxRadius = 127;

    /// <summary>Нижний предел радиуса (у UE — DELTA).</summary>
    public const float MinRadius = 1e-5f;

    /// <summary>Яркость Rec.709 (строка Y матрицы sRGB → XYZ) — у порога и в сводке снимка.</summary>
    public static readonly Vector3 Luma = new(0.2126390059f, 0.7151686788f, 0.0721923154f);

    // Поля, а не «=> [...]»: выражение-коллекция float в свойстве выделяет массив на каждый вызов, а зовут его каждый кадр.
    private static readonly float[] SizeTable = [0.3f, 1f, 2f, 10f, 30f, 64f];
    private static readonly float[] TintTable = [0.3465f, 0.138f, 0.1176f, 0.066f, 0.066f, 0.061f];

    /// <summary>Размер ступени, % ширины экрана до <see cref="SizeScale"/> (Bloom1Size … Bloom6Size UE): от узкой к широкой.</summary>
    public static ReadOnlySpan<float> Sizes => SizeTable;

    /// <summary>Оттенок ступени (Bloom1Tint … Bloom6Tint UE, серые): от узкой к широкой.</summary>
    public static ReadOnlySpan<float> Tints => TintTable;

    /// <summary>Сторона уровня цепочки <paramref name="level"/> (0 — ½) для стороны экрана <paramref name="screen"/>: вдвое с округлением вверх.</summary>
    public static int LevelSize(int screen, int level)
    {
        int s = Math.Max(screen, 1);
        for (int i = 0; i <= level; i++) s = (s + 1) / 2;
        return s;
    }

    /// <summary>
    /// Радиус ядра ступени, тексели её уровня: ширина × размер × <see cref="SizeScale"/> × 0.01 × 0.5, в пределах
    /// <see cref="MinRadius"/>…<see cref="MaxRadius"/>.
    /// </summary>
    public static float Radius(int levelWidth, int stage) =>
        Math.Clamp(levelWidth * Sizes[stage] * SizeScale * 0.01f * 0.5f, MinRadius, MaxRadius);

    /// <summary>Целый радиус: выборки ядра — тексели −N…N.</summary>
    public static int IntegerRadius(float radius) => Math.Min((int)MathF.Ceiling(radius), MaxRadius);

    /// <summary>Усиление ступени: оттенок × интенсивность × <see cref="GaussianIntensity"/> / <see cref="Stages"/> (как в UE).</summary>
    public static float Gain(int stage, float intensity) => Tints[stage] * intensity * GaussianIntensity / Stages;

    /// <summary>Сумма усилений ступеней: во сколько раз энергия bloom больше энергии прошедшего порог (≈1.325 при интенсивности 10).</summary>
    public static float TotalGain(float intensity)
    {
        float sum = 0;
        for (int s = 0; s < Stages; s++) sum += Gain(s, intensity);
        return sum;
    }

    /// <summary>Вес текселя на расстоянии <paramref name="x"/> (до нормировки).</summary>
    public static float Weight(float x, float radius)
    {
        float t = x / radius;
        return MathF.Exp(-Falloff * t * t);
    }

    /// <summary>Доля цвета в bloom: saturate((яркость входа тонмапа − порог) · 0.5); порог −1 и меньше — всё.</summary>
    public static float Amount(float luminance, float threshold) =>
        threshold > -1 ? Math.Clamp((luminance - threshold) * 0.5f, 0f, 1f) : 1f;

    /// <summary>
    /// Выборки ядра, как у шейдера: пары текселей одной билинейной выборкой, последний тексель — один; веса нормированы.
    /// Вернёт число выборок (N + 1).
    /// </summary>
    public static int Taps(float radius, Span<float> offsets, Span<float> weights)
    {
        int n = IntegerRadius(Math.Clamp(radius, MinRadius, MaxRadius));
        int count = 0;
        float total = 0;
        for (int i = -n; i <= n; i += 2)
        {
            float w0 = Weight(i, radius);
            float w1 = i < n ? Weight(i + 1, radius) : 0f;
            float w = w0 + w1;
            offsets[count] = w > 0 ? i + w1 / w : i;
            weights[count] = w;
            total += w;
            count++;
        }
        for (int k = 0; k < count; k++) weights[k] /= total;
        return count;
    }

    /// <summary>Числа bloom для шейдеров.</summary>
    internal static void Contribute(ShaderContract c)
    {
        c.Section("bloom (BloomKernel)");
        c.Add("BLOOM_FALLOFF", Falloff, "вес exp(-BLOOM_FALLOFF*(x/R)^2)");
        c.Add("BLOOM_NONE", uint.MaxValue, "нет текстуры (прибавки, bloom у тонмапа)");
        c.AddRaw("BLOOM_LUMA", string.Create(CultureInfo.InvariantCulture, $"float3({Luma.X:R}, {Luma.Y:R}, {Luma.Z:R})"), "яркость Rec.709");
    }
}
