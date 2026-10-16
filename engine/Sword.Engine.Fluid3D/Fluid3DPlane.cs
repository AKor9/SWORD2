using System.Numerics;
using System.Runtime.InteropServices;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Источник газа из карты на плоскости (README, «Источник-карта»): квадратная карта <see cref="Side"/>² клеток по <see cref="Texel"/> м с центром
/// <see cref="Center"/>; столбцы карты идут вдоль <see cref="AxisX"/>, ряды — вдоль <see cref="AxisY"/> (единичные, взаимно перпендикулярные,
/// мир). Ячейка газа на расстоянии d от плоскости получает выборку карты в своей проекции (билинейно, вне карты — 0) с весом слоя
/// saturate((h/dx + e/2 − |d|/dx)/e), e = max(1, <see cref="Edge"/>/dx): плотность и температура растут на значения клетки × вес, скорость
/// подтягивается к скорости клетки на долю клетки × вес. Клетки — <see cref="Fluid3DMapTexel"/>, значения за шаг.
/// </summary>
/// <param name="Center">Центр карты, мир, м.</param>
/// <param name="AxisX">Направление столбцов карты (i растёт), единичное.</param>
/// <param name="AxisY">Направление рядов карты (j растёт), единичное, ⟂ <paramref name="AxisX"/>.</param>
/// <param name="Side">Клеток по стороне карты.</param>
/// <param name="Texel">Сторона клетки, м.</param>
/// <param name="HalfThickness">Полутолщина слоя у плоскости, м.</param>
/// <param name="Edge">Ширина сглаженного края слоя, м; не больше ячейки — одна ячейка.</param>
public readonly record struct Fluid3DPlane(Vector3 Center, Vector3 AxisX, Vector3 AxisY, int Side, float Texel, float HalfThickness, float Edge = 0)
{
    /// <summary>Клеток в карте.</summary>
    public int Texels => Side * Side;

    /// <summary>Нормаль плоскости: AxisX × AxisY.</summary>
    public Vector3 Normal => Vector3.Cross(AxisX, AxisY);

    /// <summary>Центр клетки (i, j), мир, м.</summary>
    public Vector3 TexelCentre(int i, int j) =>
        Center + AxisX * ((i + 0.5f - Side * 0.5f) * Texel) + AxisY * ((j + 0.5f - Side * 0.5f) * Texel);

    public void Validate()
    {
        if (Side is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(Side), Side, "клеток по стороне карты 1…256");
        if (!(Texel > 0) || !float.IsFinite(Texel)) throw new ArgumentOutOfRangeException(nameof(Texel), Texel, "клетка > 0");
        if (!(HalfThickness >= 0) || !float.IsFinite(HalfThickness) || !(Edge >= 0) || !float.IsFinite(Edge))
            throw new ArgumentOutOfRangeException(nameof(HalfThickness), "толщина и край ≥ 0");
        if (!float.IsFinite(Center.X + Center.Y + Center.Z) || MathF.Abs(AxisX.Length() - 1) > 1e-3f || MathF.Abs(AxisY.Length() - 1) > 1e-3f ||
            MathF.Abs(Vector3.Dot(AxisX, AxisY)) > 1e-3f)
            throw new ArgumentException("плоскость: центр конечен, оси единичные и перпендикулярные");
    }
}

/// <summary>
/// Клетка карты источника (<see cref="Fluid3DPlane"/>; <c>uint4</c> half-битов): плотность и температура за шаг, доля скорости за шаг (0…1),
/// скорость газа (мир, м/с).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Fluid3DMapTexel
{
    public uint Rates, Blend, VelocityXY, VelocityZ;

    public static Fluid3DMapTexel Pack(float density, float temperature, float blend, Vector3 velocity) => new()
    {
        Rates = Bits(density) | ((uint)Bits(temperature) << 16),
        Blend = Bits(blend),
        VelocityXY = Bits(velocity.X) | ((uint)Bits(velocity.Y) << 16),
        VelocityZ = Bits(velocity.Z),
    };

    public readonly float Density => Value(Rates);
    public readonly float Temperature => Value(Rates >> 16);
    public readonly float BlendValue => Value(Blend);
    public readonly Vector3 Velocity => new(Value(VelocityXY), Value(VelocityXY >> 16), Value(VelocityZ));

    private static ushort Bits(float x) => BitConverter.HalfToUInt16Bits((Half)x);

    private static float Value(uint bits) => (float)BitConverter.UInt16BitsToHalf((ushort)(bits & 0xFFFF));
}
