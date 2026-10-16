using System.Numerics;

namespace Sword.Engine.Fluid3D;

/// <summary>
/// Источник газа шага: шар у <see cref="Center"/> или капсула — отрезок <see cref="From"/> → <see cref="Center"/> радиуса <see cref="Radius"/>.
/// Вес расстояния d (в ячейках): saturate((r + h/2 − d)/h), h = max(1, <see cref="Edge"/>/dx) — ширина сглаженного края (на радиусе — ½).
/// У шара w = вес расстояния до центра; у капсулы w = max(w(конец), w(отрезок) − w(начало)) — шар в конце и часть капсулы, заметённая от
/// начала (шар в начале впрыснут прошлым шагом): цепочка шагов по прямой складывается в одну капсулу без «бусин» на стыках. Внутри плотность
/// и температура растут на <see cref="Density"/>·dt·w и <see cref="Temperature"/>·dt·w, скорость подтягивается к <see cref="Velocity"/> на
/// min(1, <see cref="VelocityRate"/>·dt)·w. Источник по траектории (отрезок от прошлой позиции, скорость, мерцание) — <see cref="Fluid3DEmitters"/>.
/// </summary>
/// <param name="Center">Центр шара или конец отрезка (позиция шага), мир, м.</param>
/// <param name="Radius">Радиус, м.</param>
/// <param name="Density">Плотность в секунду.</param>
/// <param name="Temperature">Температура в секунду.</param>
/// <param name="Velocity">Скорость газа источника, м/с.</param>
/// <param name="VelocityRate">Как быстро скорость внутри подтягивается к <see cref="Velocity"/>, 1/с (0 — не трогает).</param>
public readonly record struct Fluid3DSource(Vector3 Center, float Radius, float Density, float Temperature, Vector3 Velocity = default,
    float VelocityRate = 0)
{
    /// <summary>Начало отрезка — позиция прошлого шага (шар там уже впрыснут), мир, м; null — шар.</summary>
    public Vector3? From { get; init; }

    /// <summary>Ширина сглаженного края, м; не больше ячейки — одна ячейка.</summary>
    public float Edge { get; init; }
}
