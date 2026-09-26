using System.Globalization;
using System.Numerics;

namespace Sword.Engine.Render;

/// <summary>
/// S-кривая Filmic в log10: носок и плечо — логистические участки, между ними прямая с наклоном <see cref="Slope"/>, склейка
/// smoothstep; вход 0.18 даёт 0.18. Умолчания — UE 5.8 (<see cref="Default"/>); рендер рисует ими, другие кривые — только
/// CPU-эталону (<see cref="FilmicTonemap.Display"/>). Точки стыка выводятся здесь, шейдер тонмапа получает их корневыми константами.
/// </summary>
public readonly record struct FilmicCurve(float Slope, float Toe, float Shoulder, float BlackClip, float WhiteClip)
{
    /// <summary>Умолчания UE 5.8: наклон 0.88, носок 0.55, плечо 0.26, обрезка чёрного 0, белого 0.04.</summary>
    public static FilmicCurve Default => new(0.88f, 0.55f, 0.26f, 0f, 0.04f);

    /// <summary>Точки стыка и масштабы участков — ровно те числа, что уходят в шейдер.</summary>
    /// <exception cref="ArgumentException">
    /// Вырожденная кривая (иначе в точках стыка — NaN или бесконечность): число не конечно, наклон ≤ 0, масштаб носка
    /// 1 + BlackClip − Toe или плеча 1 + WhiteClip − Shoulder ≤ 0, носок не может провести 0.18 в 0.18.
    /// </exception>
    internal Constants Derive()
    {
        double slope = Slope, toe = Toe, shoulder = Shoulder, black = BlackClip, white = WhiteClip;
        double toeScale = 1 + black - toe, shoulderScale = 1 + white - shoulder;
        if (!double.IsFinite(slope + toe + shoulder + black + white) || !(slope > 0) || !(toeScale > 0) || !(shoulderScale > 0))
            throw new ArgumentException($"кривая Filmic вырождена ({this}): нужны конечные числа, Slope > 0, 1 + BlackClip − Toe > 0, " +
                "1 + WhiteClip − Shoulder > 0");
        const double match = 0.18;
        double toeMatch;
        if (toe > 0.8)
            toeMatch = (1 - toe - match) / slope + Math.Log10(match);   // 0.18 — на прямом участке
        else
        {
            // 0.18 — на носке: стык подобран так, чтобы носок провёл 0.18 в 0.18 (логистика носка проходит только значения
            // −BlackClip…2·масштаб − BlackClip, поэтому |bt| < 1).
            double bt = (match + black) / toeScale - 1;
            if (!(Math.Abs(bt) < 1))
                throw new ArgumentException($"кривая Filmic вырождена ({this}): носок не проводит 0.18 в 0.18 — нужно " +
                    "0 < (0.18 + BlackClip) / (1 + BlackClip − Toe) < 2");
            toeMatch = Math.Log10(match) - 0.5 * Math.Log((1 + bt) / (1 - bt)) * (toeScale / slope);
        }
        double straightMatch = (1 - toe) / slope - toeMatch;
        double shoulderMatch = shoulder / slope - straightMatch;
        double span = shoulderMatch - toeMatch;
        if (Math.Abs(span) < 1e-6) span = span < 0 ? -1e-6 : 1e-6;
        return new Constants((float)slope, (float)toeMatch, (float)straightMatch, (float)shoulderMatch, (float)toeScale,
            (float)shoulderScale, (float)black, (float)white, (float)(1 / span));
    }

    /// <summary>
    /// Константы кривой. <see cref="InvSpan"/> — 1 / (плечо − носок): при плече левее носка (умолчания UE) доля склейки
    /// считается от плеча — знак её и переворачивает.
    /// </summary>
    internal readonly record struct Constants(float Slope, float ToeMatch, float StraightMatch, float ShoulderMatch, float ToeScale,
        float ShoulderScale, float BlackClip, float WhiteClip, float InvSpan);
}

/// <summary>
/// Тонмап Filmic по описанию цепочки UE 5.8 (artifacts/research/fluidninja/fire-smoke-research.md §3.10а), CPU-эталон шейдера
/// <c>common/post.hlsl</c>: те же шаги в том же порядке и те же числа во float (матрицы и константы уходят в шейдер из
/// <see cref="Contribute"/>). Вход — линейный цвет сцены (Rec.709, D65) × экспозиция, выход — sRGB-кодированный цвет 0…1.
///
/// Шаги: вход зажат в 0…65504 (предел half; NaN — 0) → AP1 (с переходом D65 → D60 по Брэдфорду) → расширение гаммы
/// (яркие насыщенные — к гамме между P3 и AP1, ExpandGamut 1) → синяя коррекция 0.6 → кривая (в AP0: «glow» и «red
/// modifier» ACES; в AP1: обрезка ≥ 0, десатурация к яркости AP1 0.96, S-кривая по каналам, десатурация 0.93, обрезка ≥ 0)
/// с долей ToneCurveAmount 1 → обратная синяя коррекция → sRGB (обрезка ≥ 0) → кодирование sRGB. Баланс белого 6500 K и
/// цветокоррекция с умолчаниями — тождество, их шагов нет.
/// </summary>
public static class FilmicTonemap
{
    /// <summary>Доля синей коррекции, расширение гаммы и доля кривой (умолчания UE 5.8).</summary>
    public const float BlueCorrection = 0.6f, ExpandGamut = 1f, ToneCurveAmount = 1f;

    /// <summary>Модуль «glow» ACES: усиление и середина по yc.</summary>
    public const float GlowGain = 0.05f, GlowMid = 0.08f;

    /// <summary>«Red modifier» ACES: масштаб, опора, ширина тона вокруг красного (0°), градусы.</summary>
    public const float RedScale = 0.82f, RedPivot = 0.03f, RedWidth = 135f;

    /// <summary>Вес хромы в yc ACES (1.75).</summary>
    public const float YcRadiusWeight = 1.75f;

    /// <summary>Десатурации к яркости AP1 до кривой и после неё (доля исходного цвета).</summary>
    public const float PreDesaturate = 0.96f, PostDesaturate = 0.93f;

    /// <summary>Предел входа: наибольшее конечное half (цель сцены — RGBA16F).</summary>
    public const float InputMax = 65504f;

    /// <summary>Нижний предел канала под log10 кривой: у нуля логарифма нет, а носок там и так ноль.</summary>
    private const float LogFloor = 1e-10f;

    // Числа ACES: пределы насыщенности у нуля (каналы, делитель), сигмоида доли «glow» по насыщенности (середина, ширина), тон —
    // градусы в радиане и √3. Литералы те же, что стояли в шейдере: так его код не меняется.
    private const float AcesTiny = 1e-10f, AcesSaturationFloor = 1e-2f, GlowSaturationMid = 0.4f, GlowSaturationWidth = 0.2f;
    private const float DegreesPerRadian = 57.29577951f, Sqrt3 = 1.7320508f;

    // Кодирование sRGB: предел и наклон линейного участка, множитель, сдвиг и показатель степенного.
    private const float SrgbLinearLimit = 0.0031308f, SrgbLinearSlope = 12.92f, SrgbScale = 1.055f, SrgbOffset = 0.055f, SrgbGamma = 2.4f;

    // Матрицы (строки; цвет — столбец справа): считаются из цветностей основных цветов, а не переписываются таблицами.
    private static readonly Matrix Rec709ToAp1, Ap1ToAp0, Ap0ToAp1, Expand, BluePre, Output;
    private static readonly Vector3 Ap1Luma;

    static FilmicTonemap()
    {
        var d65 = (0.3127, 0.3290);
        var d60 = (0.32168, 0.33767);
        var rec709 = Mat3.RgbToXyz((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), d65);
        var ap0 = Mat3.RgbToXyz((0.7347, 0.2653), (0.0, 1.0), (0.0001, -0.0770), d60);
        var ap1 = Mat3.RgbToXyz((0.713, 0.293), (0.165, 0.830), (0.128, 0.044), d60);
        // Гамма «между P3 и AP1» расширения UE — белая точка D65 и без перехода к D60, как в UE.
        var wide = Mat3.RgbToXyz((0.6965, 0.3065), (0.245, 0.718), (0.1302, 0.0456), d65);
        var toAp1 = Mat3.Mul(Mat3.Inverse(ap1), Mat3.Mul(Mat3.Bradford(d65, d60), rec709));
        var fromAp1 = Mat3.Inverse(toAp1);
        var ap1ToAp0 = Mat3.Mul(Mat3.Inverse(ap0), ap1);
        var ap0ToAp1 = Mat3.Inverse(ap1ToAp0);
        // Синяя коррекция UE — эмпирические таблицы в AP0 (прямая и её приближённая обратная — числами UE).
        double[] blue = [0.9404372683, -0.0183068787, 0.0778696104, 0.0083786969, 0.8286599939, 0.1629613092,
            0.0005471261, -0.0008833746, 1.0003362486];
        double[] blueInv = [1.06318, 0.0233956, -0.0865726, -0.0106337, 1.20632, -0.19569, -0.000590887, 0.00105248, 0.999538];
        var blueAp1 = Mat3.Mul(ap0ToAp1, Mat3.Mul(blue, ap1ToAp0));
        var blueInvAp1 = Mat3.Mul(ap0ToAp1, Mat3.Mul(blueInv, ap1ToAp0));

        Rec709ToAp1 = Matrix.From(toAp1);
        Ap1ToAp0 = Matrix.From(ap1ToAp0);
        Ap0ToAp1 = Matrix.From(ap0ToAp1);
        Expand = Matrix.From(Mat3.Mul(Mat3.Mul(Mat3.Inverse(ap1), wide), fromAp1));
        BluePre = Matrix.From(Mat3.Lerp(Mat3.Identity, blueAp1, BlueCorrection));
        Output = Matrix.From(Mat3.Mul(fromAp1, Mat3.Lerp(Mat3.Identity, blueInvAp1, BlueCorrection)));
        Ap1Luma = new Vector3((float)ap1[3], (float)ap1[4], (float)ap1[5]);
    }

    /// <summary>Числа тонмапа для шейдеров: константы шагов и матрицы (<c>float3x3</c>, строки).</summary>
    public static void Contribute(ShaderContract c)
    {
        c.Section("тонмап Filmic (FilmicTonemap)");
        c.Add("FILMIC_EXPAND_GAMUT", ExpandGamut);
        c.Add("FILMIC_TONE_CURVE_AMOUNT", ToneCurveAmount);
        c.Add("FILMIC_GLOW_GAIN", GlowGain);
        c.Add("FILMIC_GLOW_MID", GlowMid);
        c.Add("FILMIC_RED_SCALE", RedScale);
        c.Add("FILMIC_RED_PIVOT", RedPivot);
        c.Add("FILMIC_RED_WIDTH", RedWidth, "градусы");
        c.Add("FILMIC_YC_RADIUS", YcRadiusWeight);
        c.Add("FILMIC_PRE_DESATURATE", PreDesaturate);
        c.Add("FILMIC_POST_DESATURATE", PostDesaturate);
        c.Add("FILMIC_INPUT_MAX", InputMax);
        c.Add("FILMIC_LOG_FLOOR", LogFloor);
        c.Add("FILMIC_ACES_TINY", AcesTiny, "насыщенность ACES: предел каналов у нуля");
        c.Add("FILMIC_ACES_SATURATION_FLOOR", AcesSaturationFloor, "насыщенность ACES: предел делителя");
        c.Add("FILMIC_GLOW_SATURATION_MID", GlowSaturationMid);
        c.Add("FILMIC_GLOW_SATURATION_WIDTH", GlowSaturationWidth);
        c.Add("FILMIC_DEGREES_PER_RADIAN", DegreesPerRadian);
        c.Add("FILMIC_SQRT3", Sqrt3);
        c.Add("FILMIC_SRGB_LINEAR_LIMIT", SrgbLinearLimit);
        c.Add("FILMIC_SRGB_LINEAR_SLOPE", SrgbLinearSlope);
        c.Add("FILMIC_SRGB_SCALE", SrgbScale);
        c.Add("FILMIC_SRGB_OFFSET", SrgbOffset);
        c.Add("FILMIC_SRGB_GAMMA", SrgbGamma);
        c.AddRaw("FILMIC_REC709_TO_AP1", Rec709ToAp1.Hlsl(), "Rec.709 D65 -> AP1 D60");
        c.AddRaw("FILMIC_EXPAND", Expand.Hlsl(), "AP1 -> sRGB -> как «широкая» гамма -> AP1");
        c.AddRaw("FILMIC_BLUE_PRE", BluePre.Hlsl(), "синяя коррекция с долей");
        c.AddRaw("FILMIC_AP1_TO_AP0", Ap1ToAp0.Hlsl());
        c.AddRaw("FILMIC_AP0_TO_AP1", Ap0ToAp1.Hlsl());
        c.AddRaw("FILMIC_OUTPUT", Output.Hlsl(), "обратная синяя коррекция с долей, затем AP1 -> Rec.709");
        c.AddRaw("FILMIC_AP1_LUMA", $"float3({F(Ap1Luma.X)}, {F(Ap1Luma.Y)}, {F(Ap1Luma.Z)})", "яркость AP1 (строка Y)");
    }

    /// <summary>
    /// Цвет на экране (sRGB-кодированный, 0…1) для линейного цвета сцены <paramref name="scene"/> при экспозиции
    /// <paramref name="exposure"/> и кривой <paramref name="curve"/> (null — <see cref="FilmicCurve.Default"/>).
    /// </summary>
    public static Vector3 Display(Vector3 scene, float exposure, FilmicCurve? curve = null)
    {
        var k = (curve ?? FilmicCurve.Default).Derive();
        var c = scene * exposure;
        c = new Vector3(ClampInput(c.X), ClampInput(c.Y), ClampInput(c.Z));
        var ap1 = Rec709ToAp1.Apply(c);
        ap1 = ExpandToWide(ap1);
        ap1 = BluePre.Apply(ap1);
        ap1 = Vector3.Lerp(ap1, ToneAp1(ap1, k), ToneCurveAmount);
        var rgb = Vector3.Max(Output.Apply(ap1), Vector3.Zero);
        return new Vector3(EncodeSrgb(rgb.X), EncodeSrgb(rgb.Y), EncodeSrgb(rgb.Z));
    }

    /// <summary>Байты заднего буфера (R8G8B8A8_UNorm: округление к ближайшему) для цвета сцены — эталон GPU-тестов.</summary>
    public static (byte R, byte G, byte B) DisplayBytes(Vector3 scene, float exposure, FilmicCurve? curve = null)
    {
        var d = Display(scene, exposure, curve);
        return (ToByte(d.X), ToByte(d.Y), ToByte(d.Z));
    }

    /// <summary>
    /// Линейный цвет сцены, который цепочка выведет на экран цветом <paramref name="display"/> (sRGB-кодированный, 0…1) — обратная
    /// задача методом Ньютона по логарифму цвета (шаг не больше e раз). Канал 0 на экране допустим: сцена в нём — малое положительное
    /// число (логарифм идёт вниз, пока экран не сойдётся). Для переноса цветов, подобранных под экран (очистка, фон и туман ночи), в
    /// линейную сцену.
    /// </summary>
    public static Vector3 SceneFor(Vector3 display, float exposure, FilmicCurve? curve = null)
    {
        var u = new double[3];
        for (int i = 0; i < 3; i++) u[i] = Math.Log(Math.Max(DecodeSrgb(display[i]), 1e-6) / exposure);
        var f = new double[3];
        var j = new double[9];
        const double du = 1e-3;
        for (int iteration = 0; iteration < 60; iteration++)
        {
            var y = Display(FromLog(u), exposure, curve);
            double error = 0;
            for (int i = 0; i < 3; i++)
            {
                f[i] = y[i] - display[i];
                error = Math.Max(error, Math.Abs(f[i]));
            }
            if (error < 2e-6) break;
            for (int col = 0; col < 3; col++)
            {
                var shifted = (double[])u.Clone();
                shifted[col] += du;
                var ys = Display(FromLog(shifted), exposure, curve);
                for (int row = 0; row < 3; row++) j[row * 3 + col] = (ys[row] - y[row]) / du;
            }
            double[] step;
            try
            {
                step = Mat3.Apply(Mat3.Inverse(j), f);
            }
            catch (ArgumentException)
            {
                step = [f[0] / Math.Max(j[0], 1e-6), f[1] / Math.Max(j[4], 1e-6), f[2] / Math.Max(j[8], 1e-6)];   // вырожденная — по диагонали
            }
            for (int i = 0; i < 3; i++) u[i] -= Math.Clamp(step[i], -1, 1);
        }
        return FromLog(u);
    }

    private static Vector3 FromLog(double[] u) => new((float)Math.Exp(u[0]), (float)Math.Exp(u[1]), (float)Math.Exp(u[2]));

    /// <summary>Зажим входа 0…<see cref="InputMax"/>; NaN — 0 (как <c>clamp</c> на GPU: min и max берут не-NaN операнд).</summary>
    private static float ClampInput(float v) => v >= 0 ? MathF.Min(v, InputMax) : 0;

    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.ToEven);

    /// <summary>Расширение гаммы UE: яркие насыщенные цвета сдвигаются к более широкой гамме (доля — по хроме и яркости AP1).</summary>
    private static Vector3 ExpandToWide(Vector3 ap1)
    {
        float luma = Vector3.Dot(ap1, Ap1Luma);
        var chroma = ap1 / MathF.Max(luma, LogFloor) - Vector3.One;
        float distance = Vector3.Dot(chroma, chroma);
        float amount = (1 - MathF.Pow(2, -4 * distance)) * (1 - MathF.Pow(2, -4 * ExpandGamut * luma * luma));
        return Vector3.Lerp(ap1, Expand.Apply(ap1), amount);
    }

    /// <summary>Кривая Filmic в AP1: «glow» и «red modifier» в AP0, десатурации и S-кривая по каналам в AP1.</summary>
    private static Vector3 ToneAp1(Vector3 ap1, in FilmicCurve.Constants k)
    {
        var ap0 = Ap1ToAp0.Apply(ap1);
        float saturation = Saturation(ap0);
        float glow = Glow(Yc(ap0), GlowGain * SigmoidShaper((saturation - GlowSaturationMid) / GlowSaturationWidth));
        ap0 *= 1 + glow;
        float hue = CenteredHue(ap0);
        float weight = Smoothstep(1 - MathF.Abs(2 * hue / RedWidth));
        weight *= weight;
        ap0.X += weight * saturation * (RedPivot - ap0.X) * (1 - RedScale);

        var w = Vector3.Max(Ap0ToAp1.Apply(ap0), Vector3.Zero);
        w = Vector3.Lerp(new Vector3(Vector3.Dot(w, Ap1Luma)), w, PreDesaturate);
        var tone = new Vector3(Curve(w.X, k), Curve(w.Y, k), Curve(w.Z, k));
        tone = Vector3.Lerp(new Vector3(Vector3.Dot(tone, Ap1Luma)), tone, PostDesaturate);
        return Vector3.Max(tone, Vector3.Zero);
    }

    /// <summary>S-кривая одного канала в log10: носок, прямая, плечо, склейка smoothstep.</summary>
    private static float Curve(float x, in FilmicCurve.Constants k)
    {
        float l = MathF.Log10(MathF.Max(x, LogFloor));
        float straight = k.Slope * (l + k.StraightMatch);
        float toe = l < k.ToeMatch
            ? -k.BlackClip + 2 * k.ToeScale / (1 + MathF.Exp(-2 * k.Slope / k.ToeScale * (l - k.ToeMatch)))
            : straight;
        float shoulder = l > k.ShoulderMatch
            ? 1 + k.WhiteClip - 2 * k.ShoulderScale / (1 + MathF.Exp(2 * k.Slope / k.ShoulderScale * (l - k.ShoulderMatch)))
            : straight;
        float t = Math.Clamp((l - k.ToeMatch) * k.InvSpan, 0, 1);
        if (k.InvSpan < 0) t = 1 - t;
        t = t * t * (3 - 2 * t);
        return toe + (shoulder - toe) * t;
    }

    /// <summary>Насыщенность ACES: (max − min) / max с пределами у нуля.</summary>
    private static float Saturation(Vector3 c)
    {
        float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), min = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        return (MathF.Max(max, AcesTiny) - MathF.Max(min, AcesTiny)) / MathF.Max(max, AcesSaturationFloor);
    }

    /// <summary>yc ACES: яркость с добавкой хромы (конус вокруг серой оси).</summary>
    private static float Yc(Vector3 c)
    {
        float chroma = MathF.Sqrt(MathF.Max(c.Z * (c.Z - c.Y) + c.Y * (c.Y - c.X) + c.X * (c.X - c.Z), 0));
        return (c.X + c.Y + c.Z + YcRadiusWeight * chroma) / 3;
    }

    /// <summary>Сигмоида ACES на −2…2 → 0…1.</summary>
    private static float SigmoidShaper(float x)
    {
        float t = MathF.Max(1 - MathF.Abs(0.5f * x), 0);
        return 0.5f * (1 + MathF.Sign(x) * (1 - t * t));
    }

    /// <summary>Прибавка «glow»: полная в тенях, ноль от 2·середины, между — спадает.</summary>
    private static float Glow(float yc, float gain) =>
        yc <= 2f / 3f * GlowMid ? gain : yc >= 2 * GlowMid ? 0 : gain * (GlowMid / yc - 0.5f);

    /// <summary>
    /// Тон в градусах, отсчитанный от красного (−180…180); у серого — 0. Перевода «> 180 → −360» ACES нет: atan2 уже в [−π, π], а
    /// округление float даёт за 180 не больше ULP — там вес красного (от |тон|) и так 0 (он нулевой с |тон| ≥ 67.5°).
    /// </summary>
    private static float CenteredHue(Vector3 c)
    {
        if (c.X == c.Y && c.Y == c.Z) return 0;
        return DegreesPerRadian * MathF.Atan2(Sqrt3 * (c.Y - c.Z), 2 * c.X - c.Y - c.Z);
    }

    private static float Smoothstep(float x)
    {
        float t = Math.Clamp(x, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Кодирование sRGB (кусочное: линейный участок у нуля, степень 1/2.4), результат зажат в 0…1.</summary>
    public static float EncodeSrgb(float v)
    {
        float e = v <= SrgbLinearLimit ? SrgbLinearSlope * v : SrgbScale * MathF.Pow(v, 1 / SrgbGamma) - SrgbOffset;
        return Math.Clamp(e, 0, 1);
    }

    private static double DecodeSrgb(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static string F(float v)
    {
        string text = v.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    /// <summary>Матрица 3×3 во float — те же числа у CPU-эталона и в шейдере.</summary>
    private readonly struct Matrix(float[] m)
    {
        private readonly float[] _m = m;

        public static Matrix From(double[] m) => new([.. m.Select(v => (float)v)]);

        public Vector3 Apply(Vector3 v) => new(
            _m[0] * v.X + _m[1] * v.Y + _m[2] * v.Z,
            _m[3] * v.X + _m[4] * v.Y + _m[5] * v.Z,
            _m[6] * v.X + _m[7] * v.Y + _m[8] * v.Z);

        public string Hlsl() => "float3x3(" + string.Join(", ", _m.Select(F)) + ")";
    }

    /// <summary>Матрицы 3×3 в double (строки подряд) — для вывода матриц из цветностей.</summary>
    private static class Mat3
    {
        public static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        public static double[] Apply(double[] m, double[] v) =>
            [m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2]];

        public static double[] Lerp(double[] a, double[] b, double t)
        {
            var r = new double[9];
            for (int i = 0; i < 9; i++) r[i] = a[i] + (b[i] - a[i]) * t;
            return r;
        }

        public static double[] Inverse(double[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double co0 = e * i - f * h, co1 = f * g - d * i, co2 = d * h - e * g;
            double det = a * co0 + b * co1 + c * co2;
            if (Math.Abs(det) < 1e-300) throw new ArgumentException("вырожденная матрица 3×3");
            double s = 1 / det;
            return
            [
                co0 * s, (c * h - b * i) * s, (b * f - c * e) * s,
                co1 * s, (a * i - c * g) * s, (c * d - a * f) * s,
                co2 * s, (b * g - a * h) * s, (a * e - b * d) * s,
            ];
        }

        private static double[] Xyz((double X, double Y) xy) => [xy.X / xy.Y, 1, (1 - xy.X - xy.Y) / xy.Y];

        /// <summary>RGB → XYZ по цветностям основных цветов и белой точки (белый RGB 1,1,1 → белая точка, Y = 1).</summary>
        public static double[] RgbToXyz((double, double) r, (double, double) g, (double, double) b, (double, double) white)
        {
            double[] pr = Xyz(r), pg = Xyz(g), pb = Xyz(b);
            double[] p = [pr[0], pg[0], pb[0], pr[1], pg[1], pb[1], pr[2], pg[2], pb[2]];
            var s = Apply(Inverse(p), Xyz(white));
            return [p[0] * s[0], p[1] * s[1], p[2] * s[2], p[3] * s[0], p[4] * s[1], p[5] * s[2], p[6] * s[0], p[7] * s[1], p[8] * s[2]];
        }

        /// <summary>Хроматическая адаптация по Брэдфорду: XYZ при белой точке <paramref name="from"/> → при <paramref name="to"/>.</summary>
        public static double[] Bradford((double, double) from, (double, double) to)
        {
            double[] cone = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];
            var a = Apply(cone, Xyz(from));
            var b = Apply(cone, Xyz(to));
            double[] scale = [b[0] / a[0], 0, 0, 0, b[1] / a[1], 0, 0, 0, b[2] / a[2]];
            return Mul(Inverse(cone), Mul(scale, cone));
        }
    }
}
