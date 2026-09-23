using System.Numerics;
using Sword.Engine.Ecs;

namespace Sword.Game.Contracts;

/// <summary>
/// Анимация бойцов для наблюдателей симуляции (клиент — отрисовка, модули-представления, агент): вид только для чтения
/// (<c>WorldReader.View&lt;IAnimationView&gt;()</c>) на пул поз и библиотеку клипов подсистемы Animation. Не стратегия:
/// ядро его не спрашивает, а только отдаёт наружу то, что посчитал тик. Меняют позы только системы тика.
/// </summary>
public interface IAnimationView : IReadView
{
    /// <summary>Суставов скелета бойца.</summary>
    int JointCount { get; }

    /// <summary>Имя сустава <paramref name="joint"/>.</summary>
    string JointName(int joint);

    /// <summary>Номер сустава по имени; нет — −1.</summary>
    int FindJoint(string name);

    /// <summary>
    /// Итоговая поза последнего тика — модельные матрицы суставов в пространстве персонажа (вектор слева; мир бойца —
    /// поворот на курс и перенос в корень). Позы нет (не заведена или освобождена) — пусто.
    /// </summary>
    ReadOnlySpan<Matrix4x4> Model(PoseRef pose);

    /// <summary>Поза привязки — модельные матрицы, в которых меш рисуется без анимации (новая поза — такая).</summary>
    ReadOnlySpan<Matrix4x4> ReferenceModel();

    /// <summary>Клипов в библиотеке; номер клипа — в состояниях слоёв (например, у локомоции).</summary>
    int ClipCount { get; }

    /// <summary>ID клипа «источник/имя».</summary>
    string ClipName(int clip);

    /// <summary>Длительность клипа, с.</summary>
    float ClipDuration(int clip);

    bool ClipLoops(int clip);

    /// <summary>Скорость корневого движения клипа, м/с.</summary>
    float ClipRootSpeed(int clip);

    /// <summary>Клип по ID или имени (если оно одно); нет — −1.</summary>
    int FindClip(string name);

    /// <summary>Живых поз в пуле.</summary>
    int PoseCount { get; }

    /// <summary>Заданий поз, выполненных пулом в последнем тике, и пропущенных (к освобождённым позам).</summary>
    int LastTickJobs { get; }
    int LastTickStale { get; }
}

/// <summary>
/// Тела для наблюдателей симуляции (агент — 5б, клиент — капсулы на экране, 5в): вид только для чтения
/// (<c>WorldReader.View&lt;IBodyView&gt;()</c>) на тела подсистемы Physics по хэндлу сочленения из <see cref="BodyRef"/>.
/// Тело <c>i</c> сочленения бойца — сегмент <see cref="BodySegs.Segment"/>(i). Меняют тела только системы тика.
/// </summary>
public interface IBodyView : IReadView
{
    /// <summary>Тел в сочленении; сочленения нет (пустой или освобождённый хэндл) — 0.</summary>
    int BodyCount(BodyRef body);

    /// <summary>Тело <paramref name="index"/> сочленения (0 … <see cref="BodyCount"/> − 1) — копия состояния.</summary>
    BodySnapshot Body(BodyRef body, int index);

    /// <summary>Живых сочленений и тел.</summary>
    int Articulations { get; }
    int Bodies { get; }
}

/// <summary>
/// Капсула тела после тика: постановка (центр, поворот; ось капсулы — локальная Y), прошлая постановка (для скорости и
/// интерполяции), скорость, м/с, угловая скорость, рад/с (мир), радиус и полудлина оси, м.
/// </summary>
public readonly record struct BodySnapshot(Vector3 Position, Quaternion Rotation, Vector3 PrevPosition, Quaternion PrevRotation,
    Vector3 Velocity, Vector3 AngularVelocity, float Radius, float HalfLength)
{
    /// <summary>Ось капсулы в мире.</summary>
    public Vector3 Axis => Vector3.Transform(Vector3.UnitY, Rotation);

    /// <summary>Концы оси капсулы: нижний (A) и верхний (B).</summary>
    public Vector3 EndA => Position - Axis * HalfLength;
    public Vector3 EndB => Position + Axis * HalfLength;
}
