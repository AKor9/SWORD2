using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid2D;

/// <summary>16 весов ядра давления (тапы 0…15).</summary>
[InlineArray(16)]
internal struct KernelWeights
{
    private float _element;
}

/// <summary>
/// Числа одного шага — общие для GPU (элемент <c>StructuredBuffer&lt;FluidConstants&gt;</c>, <c>fluid2d.hlsl</c>) и CPU-эталона: обе стороны
/// считают по одним и тем же готовым числам. Раскладка плотная (структурированный буфер), сверка — <c>_Static_assert</c> с
/// <c>FLUID2D_CONSTANTS_SIZE</c>. Всё, что не зависит от текселя (поворот ввода, сдвиги шумов, веса ядра), считается здесь, в double
/// там, где растёт со временем.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Fluid2DConstants
{
    public uint N, BrushN;
    public float InvN, SimSpeed;
    public Vector2 Wod, VelocityBase;
    public float VelocityFeedback, VelocityClamp, CollisionFloor, Weaken;
    public float BrushVelocity, VeloStrength, Eraser, DensityFloor;
    public uint AmpEnabled;
    public float AmpNoise, AmpSize;
    public uint DensityEnabled;
    public Vector2 AmpOffset, DensityOffset;
    public float DensityAmount, DensityScale, DirNoiseAmp, DirNoiseMask;
    public Vector2 DirNoiseOffset;
    public float DirNoiseScale, DirNoiseTime;
    public float EdgeInvWidth, EdgePow, DensityAccumulation, WetInvWidth;
    public float WetFeed, DivergenceMult, Puncture, PressureFeedback;
    public float DivergenceFeedback;
    public uint PressureMask;
    public float PressureInvWidth, PressurePow;
    public uint Taps;
    public float TapCenter, Contrast;
    public uint ExportFlags;
    public float ExportInvWidth;
    public uint AmpSrv, AmpLayer, AmpWidth, AmpHeight;
    public uint DensitySrv, DensityLayer, DensityWidth, DensityHeight;
    public uint StepLo, StepHi, CrudePass, Pad;
    public KernelWeights WeightP, WeightD;

    // Painter (fluid.painter): бининг шага в буфере кисти, текстуры, числа формул BR §4.2, общие для всех точек.
    public uint PainterCells, PainterCellGroups, PainterCellBase, PainterEntryBase;
    public uint PainterPointBase, PainterFlags, PainterNoiseSrv, PainterNoiseLayer;
    public uint PainterNoiseWidth, PainterNoiseHeight, BitmapSrv, BitmapLayer;
    public uint BitmapWidth, BitmapHeight;
    public float BrushInvN, PainterBase;
    public float PainterQ, PainterSharpen, PainterEdgeInv, PainterVeloClamp;
    public float PainterVeloMult, VeloNoiseScale, VeloNoiseAmp, VeloNoiseBias;
    public float BrushDensityScale;
    public Vector2 VeloNoiseOffset, BrushDensityOffset;
    public float BrushDensityFloor, BrushDensityPow;
    public Vector2 BitmapOffset;
    public float BitmapScale, DensityTxtMult;

    public const uint ExportInvert = 1, ExportInvertedAlpha = 2;

    public const uint PainterPainterMode = 1, PainterAdditive = 2, PainterEdgeStep = 4, PainterNoise = 8, PainterBitmap = 16,
        PainterBitmapDensityOnly = 32;

    /// <summary>
    /// Числа Painter шага: раскладка шага кисти в буфере (смещения в словах от <paramref name="wordBase"/>), шум кисти и битмап (сдвиги по
    /// модулю 1 в double, как у шумов сима), b = (1 − IF)³, заострение скорости при IF &gt; 0.5, маска края.
    /// </summary>
    public void SetPainter(Fluid2DParams p, in Fluid2DStepInput input, Fluid2DBrush brush, uint wordBase, in Fluid2DNoise noise, in Fluid2DNoise bitmap) =>
        SetPainter(p, input, brush.CellsPerAxis, brush.CellGroups, brush.Entries.Length, wordBase, noise, bitmap);

    public void SetPainter(Fluid2DParams p, in Fluid2DStepInput input, int cellsPerAxis, int cellGroups, int entries, uint wordBase,
        in Fluid2DNoise noise, in Fluid2DNoise bitmap)
    {
        PainterCells = (uint)cellsPerAxis;
        PainterCellGroups = (uint)Math.Max(cellGroups, 1);
        PainterCellBase = wordBase;
        PainterEntryBase = wordBase + (uint)(cellsPerAxis * cellsPerAxis);
        PainterPointBase = PainterEntryBase + (uint)entries;
        bool additive = p.SimplePainterMode || p.ForceAdditiveStrokeBlending;
        bool hasBitmap = bitmap.Width > 0 && !(bitmap.Width == 1 && bitmap.Height == 1);
        PainterFlags = (p.SimplePainterMode ? PainterPainterMode : 0) | (additive ? PainterAdditive : 0)
            | (p.PainterEdgeMaskWidth <= 0.05f ? PainterEdgeStep : 0) | (p.BrushNoiseUsed ? PainterNoise : 0)
            | (hasBitmap ? PainterBitmap : 0) | (p.UseInputTextureOnlyAsDensity ? PainterBitmapDensityOnly : 0);
        PainterNoiseSrv = (uint)noise.Srv;
        PainterNoiseLayer = (uint)noise.Layer;
        PainterNoiseWidth = (uint)noise.Width;
        PainterNoiseHeight = (uint)noise.Height;
        BitmapSrv = (uint)bitmap.Srv;
        BitmapLayer = (uint)bitmap.Layer;
        BitmapWidth = (uint)bitmap.Width;
        BitmapHeight = (uint)bitmap.Height;
        BrushInvN = 1f / p.BrushResolution;
        float keep = MathF.Max(1f - p.PersistencyOfFieldAndPointData, 0f);
        PainterBase = keep * keep * keep;
        PainterQ = MathF.Max(p.PersistencyOfFieldAndPointData - 0.5f, 0f);
        PainterSharpen = 1f - MathF.Min(2f * PainterQ, 0.9f);
        PainterEdgeInv = 1f / MathF.Max(p.PainterEdgeMaskWidth, 0.001f);
        PainterVeloClamp = p.BrushVelocityClamp;
        PainterVeloMult = p.BrushVelocityMultXY;
        VeloNoiseAmp = 0.1f * p.BrushVelocityNoise;
        VeloNoiseBias = 0.05f * p.BrushVelocityNoise;
        BrushDensityFloor = 1f - p.BrushDensityNoise;
        BrushDensityPow = p.BrushDensityNoisePow;
        BitmapScale = p.DensityTxtScale + 1f;
        DensityTxtMult = p.DensityTxtMult;

        // Координаты шумов кисти: (uv + WO)·Scale + t·Freq (скорость) и (uv + WO)·Scale + floor(t·Freq)·0.1 (плотность: узор прыгает Freq раз
        // в секунду); без мировых координат — 0.5·uv·Scale. Постоянная часть — по модулю 1 (текстуры заворачиваются).
        double t = input.Step * p.StepSeconds;
        double wx = input.WorldOffset.X, wy = input.WorldOffset.Y;
        bool world = p.BrushNoiseInWorldSpace;
        double tv = t * p.BrushVelocityNoiseFreq, td = Math.Floor(t * p.BrushDensityNoiseFreq + 1e-9) * 0.1;
        VeloNoiseScale = world ? p.BrushVelocityNoiseScale : 0.5f * p.BrushVelocityNoiseScale;
        BrushDensityScale = world ? p.BrushDensityNoiseScale : 0.5f * p.BrushDensityNoiseScale;
        VeloNoiseOffset = world
            ? new Vector2(Frac(wx * p.BrushVelocityNoiseScale + tv), Frac(wy * p.BrushVelocityNoiseScale + tv))
            : new Vector2(Frac(tv), Frac(tv));
        BrushDensityOffset = world
            ? new Vector2(Frac(wx * p.BrushDensityNoiseScale + td), Frac(wy * p.BrushDensityNoiseScale + td))
            : new Vector2(Frac(td), Frac(td));
        BitmapOffset = new Vector2(Frac(wx * BitmapScale + p.DensityTxtOffsetX * t + p.DensityTxtRandomOffset),
            Frac(wy * BitmapScale + p.DensityTxtOffsetY * t + p.DensityTxtRandomOffset));
    }

    /// <summary>Шаг: всё, кроме номера прохода давления, — из параметров и входа.</summary>
    public static Fluid2DConstants Build(Fluid2DParams p, in Fluid2DStepInput input, bool brush, in Fluid2DNoise amp, in Fluid2DNoise density)
    {
        double t = input.Step * p.StepSeconds;
        var c = new Fluid2DConstants
        {
            N = (uint)p.Resolution,
            BrushN = brush ? (uint)p.BrushResolution : 0,
            InvN = 1f / p.Resolution,
            SimSpeed = p.SimSpeed,
            Wod = input.WorldOffsetDelta,
            VelocityBase = VelocityBaseOf(p, input),
            VelocityFeedback = p.VelocityFeedback,
            VelocityClamp = p.VelocityClamp,
            CollisionFloor = 1f - MathF.Sqrt(MathF.Max(p.SimEdgeBounciness, 0)),
            Weaken = p.WeakenCollisionMask,
            BrushVelocity = 20f * p.VeloFromBrushMotion,
            VeloStrength = p.VeloStrength,
            Eraser = p.EraserStrength,
            DensityFloor = p.DensityFloor,
            AmpEnabled = p.VeloAmplitudeNoiseEnabled ? 1u : 0,
            AmpNoise = p.VeloAmpNoise,
            AmpSize = p.VeloAmpNoiseSize,
            DensityEnabled = p.DensityNoiseEnabled ? 1u : 0,
            DensityAmount = p.DensityNoiseAmount,
            DensityScale = p.DensityNoiseTile + 1,
            DirNoiseAmp = 0.005f * p.VeloDirNoise,
            DirNoiseMask = 1f - p.MaskDirNoiseWithSimVelocity,
            DirNoiseScale = 128f * 0.5f / p.VeloDirNoiseSize,
            DirNoiseTime = (float)(t * p.VeloDirNoiseSpeed),
            EdgeInvWidth = 1f / MathF.Max(p.EdgeMaskWidth, 0.001f),
            EdgePow = MathF.Max(p.EdgeMaskPow, 0.01f),
            DensityAccumulation = p.DensityAccumulation,
            WetInvWidth = 1f / MathF.Max(p.WetMapEdgeMaskWidth, 0.001f),
            WetFeed = 1f - p.WetmapFeedback,
            DivergenceMult = p.CrudePressureSolver ? MathF.Min(p.DivergenceMult, 0.5f) : p.DivergenceMult,
            Puncture = 0.01f * p.BrushPuncture,
            PressureFeedback = p.PressureFeedback,
            DivergenceFeedback = p.DivergenceFeedback,
            PressureMask = p.PressureEdgeMasking ? 1u : 0,
            PressureInvWidth = 1f / MathF.Max(p.PressureEdgeMaskWidth, 0.001f),
            PressurePow = MathF.Max(p.PressureEdgeMaskPow, 0.01f),
            Contrast = p.FluidDensityContrast,
            ExportFlags = (p.InvertFluidDensity ? ExportInvert : 0) | (p.CopyInvertedDensityToAlpha ? ExportInvertedAlpha : 0),
            ExportInvWidth = 1f / MathF.Max(p.EdgeMaskWidth, 0.001f),
            AmpSrv = (uint)amp.Srv, AmpLayer = (uint)amp.Layer, AmpWidth = (uint)amp.Width, AmpHeight = (uint)amp.Height,
            DensitySrv = (uint)density.Srv, DensityLayer = (uint)density.Layer, DensityWidth = (uint)density.Width,
            DensityHeight = (uint)density.Height,
            StepLo = (uint)input.Step,
            StepHi = (uint)(input.Step >> 32),
            CrudePass = (uint)(input.Step & 1),
        };

        // Шумы-текстуры заворачиваются с периодом 1 по UV: постоянная часть координаты берётся по модулю 1 в double —
        // точность не падает с ростом WorldOffset и времени.
        float rno = p.RandomizeNoiseOffsets;
        double wx = input.WorldOffset.X, wy = input.WorldOffset.Y;
        c.AmpOffset = new Vector2(
            Frac((wx + rno) * p.VeloAmpNoiseSize + t * (p.VeloAmpNoiseSpeed * 0.5 + 0.1 * rno)),
            Frac((wy + rno) * p.VeloAmpNoiseSize + t * (p.VeloAmpNoiseSpeed * 0.2 + 0.1 * rno)));
        c.DensityOffset = new Vector2(
            Frac(wx * c.DensityScale + p.DensityNoiseSpeed * t * 0.5 + rno),
            Frac(wy * c.DensityScale + p.DensityNoiseSpeed * t + rno));
        c.DirNoiseOffset = new Vector2((float)(wx * c.DirNoiseScale), (float)(wy * c.DirNoiseScale));

        var (taps, rowP, rowD) = Kernel(p.KernelMult, p.KernelIndexOffset);
        c.Taps = (uint)taps;
        c.TapCenter = (taps - 1) * 0.5f;
        for (int j = 0; j < 16; j++)
        {
            c.WeightP[j] = j <= taps ? PressureKernels.P(rowP + j) : 0;
            c.WeightD[j] = j <= taps ? PressureKernels.D(rowD + j) : 0;
        }
        return c;
    }

    /// <summary>
    /// Ядро давления как у L2: номер ядра k = ⌈(15 − clamp(15·KernelMult, 5, 15))/2⌉, тапов 15 − 2k, ряд весов P — (k + KIO)·15 + k,
    /// ряд D — k·15 + k (шаг рядов 15, а читается 16 весов; вне таблицы — 0).
    /// </summary>
    internal static (int Taps, int RowP, int RowD) Kernel(float kernelMult, float kernelIndexOffset)
    {
        int k = (int)MathF.Ceiling((15f - Math.Clamp(15f * kernelMult, 5f, 15f)) * 0.5f);
        int kio = (int)kernelIndexOffset;
        return (15 - 2 * k, (k + kio) * 15 + k, k * 15 + k);
    }

    /// <summary>
    /// Постоянный ввод скорости в осях площадки: в мировых осях — поворот, обратный повороту площадки; y меняет знак (v растёт вниз);
    /// минус инерция от движения площадки.
    /// </summary>
    private static Vector2 VelocityBaseOf(Fluid2DParams p, in Fluid2DStepInput input)
    {
        var d = p.DirectVelocity;
        if (p.DirectVelocityInWorldSpace) d = Vector3.Transform(d, Quaternion.Inverse(input.Rotation));
        return new Vector2(d.X, -d.Y) - p.VeloFromSimAreaMotion * input.VelocityOffsetDelta;
    }

    private static float Frac(double x) => (float)(x - Math.Floor(x));
}

/// <summary>
/// Ядра давления L2 (DXBC <c>SM000_0003</c>, FSR §3.4): шесть ядер 15/13/11/9/7/5 тапов подряд с шагом 15 (90 весов), гаусс для P и
/// «кастомное» ядро для дивергенции. Сумма весов ядра 15: P — 0.9998, D — 1.7506.
/// </summary>
internal static class PressureKernels
{
    private static readonly float[] Gauss =
    [
        0.00117f, 0.0045f, 0.01394f, 0.035f, 0.07186f, 0.12f, 0.1631f, 0.1807f, 0.1631f, 0.12f, 0.07186f, 0.035f, 0.01394f, 0.0045f, 0.00117f, 0,
        0.00222f, 0.00876f, 0.027f, 0.0648f, 0.121f, 0.176f, 0.199f, 0.176f, 0.121f, 0.0648f, 0.027f, 0.00876f, 0.00222f, 0, 0, 0,
        0.00276f, 0.01377f, 0.0478f, 0.1169f, 0.19945f, 0.23832f, 0.19945f, 0.1169f, 0.0478f, 0.01377f, 0.00276f, 0, 0, 0, 0, 0,
        0.0034f, 0.0202f, 0.09134f, 0.22958f, 0.31094f, 0.22958f, 0.09134f, 0.0202f, 0.0034f, 0, 0, 0, 0, 0, 0, 0,
        0.0106f, 0.075f, 0.2387f, 0.351f, 0.2387f, 0.075f, 0.0106f, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0.06136f, 0.24477f, 0.38774f, 0.24477f, 0.06136f, 0, 0, 0, 0, 0,
    ];

    private static readonly float[] Custom =
    [
        0.0006f, 0.003f, 0.01f, 0.028f, 0.064f, 0.1348f, 0.30636f, 0.65711f, 0.30636f, 0.1348f, 0.064f, 0.028f, 0.01f, 0.003f, 0.0006f, 0,
        0.00166f, 0.00726f, 0.02369f, 0.06055f, 0.1353f, 0.3028f, 0.64626f, 0.3028f, 0.1353f, 0.06055f, 0.02369f, 0.00726f, 0.00166f, 0, 0, 0,
        0.00426f, 0.017f, 0.052f, 0.13326f, 0.324532f, 0.63592f, 0.324532f, 0.13326f, 0.052f, 0.017f, 0.00426f, 0, 0, 0, 0, 0,
        0.00514f, 0.022f, 0.09f, 0.2956f, 0.60248f, 0.2956f, 0.09f, 0.022f, 0.00514f, 0, 0, 0, 0, 0, 0, 0,
        0.0053f, 0.0528f, 0.2503f, 0.57293f, 0.2503f, 0.0528f, 0.0053f, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0.04567f, 0.18454f, 0.51596f, 0.18454f, 0.04567f, 0, 0, 0, 0, 0,
    ];

    public static float P(int index) => (uint)index < (uint)Gauss.Length ? Gauss[index] : 0;
    public static float D(int index) => (uint)index < (uint)Custom.Length ? Custom[index] : 0;
}
