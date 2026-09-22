using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Ui;

/// <summary>
/// Режим квада. ЕДИНСТВЕННОЕ объявление номеров: из этого перечисления собираются дефайны <c>UI_BOX</c>, <c>UI_TRI</c>,
/// <c>UI_SPRITE</c> контракта шейдеров (UI-проход рендера), в <c>ui.hlsl</c> их объявить уже нельзя.
///
/// Разбор в <c>PsUi</c> знает не все члены — и это не рассинхрон. <see cref="Text"/> уходит в отдельный конвейер
/// <c>PsVectorText</c>, который считает TrueType-контур аналитически; до общего разбора фигур такие квады не доходят
/// вовсе. <see cref="Arc"/> и <see cref="Soft"/> — номера SWORD без рисующих их вызовов: номер занят, чтобы новый режим
/// не получил его молча.
/// </summary>
public enum UiMode : uint
{
    /// <summary>Прямоугольник, скруглённый прямоугольник, круг, кольцо, линия.</summary>
    Box = 0,

    /// <summary>Дуга кольца.</summary>
    Arc = 1,

    /// <summary>Треугольник.</summary>
    Triangle = 2,

    /// <summary>TrueType-контур вычисляется аналитически в pixel shader.</summary>
    Text = 3,

    /// <summary>Мягкое поле: то же поле расстояний, но вместо резкого края — спад по кривой.</summary>
    Soft = 4,

    /// <summary>
    /// Плитка массива текстур — ЕДИНСТВЕННЫЙ режим, читающий ресурс. Индекс дескриптора едет в самой записи, а не
    /// корневой константой: куча bindless, и любой квад может показать на свой ресурс, не разрывая проход на два вызова.
    /// Едет он ЗНАЧЕНИЕМ, а не набором бит, — как номер glyph'а: у asfloat малого целого нулевой порядок, то есть
    /// денормаль, а flush-to-zero у DXC разрешён по умолчанию и обратил бы все индексы в ноль.
    /// </summary>
    Sprite = 5,
}

/// <summary>
/// Одна запись на квад: всё, что нужно видеокарте, чтобы развернуть шесть вершин.
///
/// <b>Раскладка совпадает с <c>UiQuad</c> в <c>ui.hlsl</c> байт в байт</b> — шейдер сверяет свой размер с
/// <c>UI_QUAD_SIZE</c> контракта (<c>sizeof(UiQuad)</c> в C#). <c>StructuredBuffer</c> DXC упаковывает плотно, как C# с
/// <c>Pack = 4</c>, так что порядок полей свободен; <see cref="Shape"/> первым — порядок SWORD (правило «вектор не
/// пересекает границу шестнадцати байт» — у cbuffer, не у этого буфера). Переставляя поля, менять обе стороны: размер
/// сверяет <c>_Static_assert</c>, смещения — нет.
///
/// Поля переиспользуются между режимами; таблица — в <c>ui.hlsl</c> рядом с объявлением.
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
