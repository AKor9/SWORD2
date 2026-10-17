using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ui;

/// <summary>
/// Режим квада — единственное объявление номеров: из него собираются дефайны <c>UI_*</c> контракта шейдеров. Все режимы
/// рисует один конвейер (<c>PsUi</c>); пропуски в номерах — от снятых режимов, номера не переиспользуются.
/// </summary>
public enum UiMode : uint
{
    /// <summary>Прямоугольник, скруглённый прямоугольник, круг, кольцо, линия.</summary>
    Box = 0,

    /// <summary>Треугольник.</summary>
    Triangle = 2,

    /// <summary>TrueType-контур вычисляется аналитически в pixel shader.</summary>
    Text = 3,

    /// <summary>
    /// Плитка массива текстур — единственный режим, читающий ресурс; индекс дескриптора едет в записи значением, а не битами:
    /// asfloat малого целого — денормаль, и flush-to-zero DXC обратил бы его в ноль.
    /// </summary>
    Sprite = 5,
}

/// <summary>
/// Запись на квад; раскладка совпадает с <c>UiQuad</c> в <c>ui.hlsl</c> байт в байт. Размер сверяет <c>_Static_assert</c>,
/// смещения — нет: переставляя поля, менять обе стороны. Таблица полей по режимам — в <c>ui.hlsl</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct UiQuad
{
    public Vector4 Shape;
    public Vector2 Centre;
    public Vector2 HalfSize;
    public Vector2 Axis;
    public Vector2 Uv0;
    public Vector2 Uv1;
    public uint ColourA;
    public uint ColourB;
    public UiMode Mode;

    /// <summary>Шаг записи, байт.</summary>
    public const int Stride = 68;
}
