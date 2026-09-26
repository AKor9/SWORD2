using System.Numerics;
using System.Runtime.InteropServices;
using Sword.Engine.Render;

namespace Sword.Engine.Fluid2D;

/// <summary>
/// Вход одного шага сима (FSR §3.4, §3.6): номер шага (время шумов t = Step·<see cref="Fluid2DParams.StepSeconds"/> — по номеру,
/// не по часам кадра), сдвиг площадки и её поворот. Площадку двигает вызывающий: <see cref="WorldOffsetDelta"/> — перенос за шаг
/// в UV (буферы читаются в uv + WOD), <see cref="WorldOffset"/> — накопленный, привязывает шумы к миру.
/// </summary>
/// <param name="PlaneRotation">Поворот площадки из её осей (x — u, y — вверх по площадке, z — нормаль) в мир; default — без поворота.</param>
public readonly record struct Fluid2DStepInput(ulong Step, Vector2 WorldOffset, Vector2 WorldOffsetDelta, Quaternion PlaneRotation = default)
{
    /// <summary>Сдвиг площадки для инерции скоростью (<c>WorldOffsetDeltaForVelocityOffset</c>, при <see cref="Fluid2DParams.VeloFromSimAreaMotion"/>).</summary>
    public Vector2 VelocityOffsetDelta { get; init; }

    internal Quaternion Rotation => PlaneRotation == default ? Quaternion.Identity : PlaneRotation;
}

/// <summary>
/// Тексель грида кисти (слои PainterGrid L2, BR §6): 0, 1 — скорость кисти, 2 — плотность, 3 — поле захвата (не читается),
/// 4, 5 — скорость битмапа, 6 — плотность битмапа, 7 — не используется. Восемь half — 16 Б; грид — (N / PaintBufferDownScaleFactor)².
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Fluid2DBrushTexel
{
    public Half VelocityX, VelocityY, Density, Capture, BitmapVelocityX, BitmapVelocityY, BitmapDensity, Unused;

    public Fluid2DBrushTexel(float vx, float vy, float density, float bitmapVx = 0, float bitmapVy = 0, float bitmapDensity = 0)
    {
        VelocityX = (Half)vx;
        VelocityY = (Half)vy;
        Density = (Half)density;
        BitmapVelocityX = (Half)bitmapVx;
        BitmapVelocityY = (Half)bitmapVy;
        BitmapDensity = (Half)bitmapDensity;
    }
}

/// <summary>
/// Шумовая текстура сима на GPU: слой <see cref="Layer"/> текстуры-массива (<see cref="GpuTexture"/> пака или <see cref="GpuImage"/>),
/// канал r, мип 0. Выборка — билинейная с заворотом, вручную (как у CPU-эталона); sRGB декодирует чтение текстуры.
/// </summary>
public readonly record struct Fluid2DNoise(int Srv, int Layer, int Width, int Height)
{
    /// <summary>Слой <paramref name="layer"/> текстуры пака; нет слоя — ошибка.</summary>
    public static Fluid2DNoise FromLayer(GpuTexture texture, string layer)
    {
        int index = texture.FindLayer(layer);
        if (index < 0) throw new ArgumentException($"{texture.Path}: нет слоя {layer} (есть: {string.Join(", ", texture.Layers)})", nameof(layer));
        return new Fluid2DNoise(texture.Srv, index, texture.Width, texture.Height);
    }

    public static Fluid2DNoise FromImage(GpuImage image) => new(image.Srv, 0, image.Width, image.Height);
}

/// <summary>Шумовая текстура для CPU-эталона: канал r в линейных значениях (после sRGB-декода), ряд за рядом.</summary>
public sealed class Fluid2DNoiseImage
{
    public Fluid2DNoiseImage(float[] values, int width, int height)
    {
        if (width <= 0 || height <= 0 || values.Length != width * height)
            throw new ArgumentException($"шум {width}×{height}: значений {values.Length}");
        Values = values;
        Width = width;
        Height = height;
    }

    public float[] Values { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Канал r картинки RGBA8; <paramref name="srgb"/> — декод sRGB (как выборка из sRGB-текстуры).</summary>
    public static Fluid2DNoiseImage FromRgba8(ReadOnlySpan<byte> rgba, int width, int height, bool srgb)
    {
        var values = new float[width * height];
        for (int i = 0; i < values.Length; i++)
            values[i] = srgb ? SrgbToLinear(rgba[i * 4]) : rgba[i * 4] / 255f;
        return new Fluid2DNoiseImage(values, width, height);
    }

    /// <summary>sRGB → линейное (IEC 61966-2-1).</summary>
    public static float SrgbToLinear(byte value)
    {
        double c = value / 255.0;
        return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
    }
}

/// <summary>
/// Текстура кисти для CPU-эталона: RGBA в линейных значениях, ряд за рядом (шум кисти <c>T_NoiseTemplate2</c> — R плотность, RG скорость;
/// битмап — RGB). Выборка — билинейная с заворотом, мип 0, как у Painter на GPU.
/// </summary>
public sealed class Fluid2DImage
{
    public Fluid2DImage(float[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length != 4 * width * height)
            throw new ArgumentException($"картинка {width}×{height}: значений {rgba.Length}, нужно {4 * width * height}");
        Rgba = rgba;
        Width = width;
        Height = height;
    }

    public float[] Rgba { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Картинка RGBA8; <paramref name="srgb"/> — декод sRGB каналов RGB (как выборка из sRGB-текстуры).</summary>
    public static Fluid2DImage FromRgba8(ReadOnlySpan<byte> rgba, int width, int height, bool srgb)
    {
        var values = new float[4 * width * height];
        for (int i = 0; i < values.Length; i++)
            values[i] = srgb && i % 4 != 3 ? Fluid2DNoiseImage.SrgbToLinear(rgba[i]) : rgba[i] / 255f;
        return new Fluid2DImage(values, width, height);
    }
}
